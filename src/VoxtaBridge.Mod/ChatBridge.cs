using System;
using System.Collections.Generic;
using System.Linq;
using Eleon;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// In-game chat ↔ Voxta (dedi). Incoming: only private messages a player sends to themselves ("whisper to self",
    /// tested 2026-11-20: reaches the server, nobody else sees it) are kept for a few minutes with a sequence number;
    /// /status carries them per player (chat.&lt;steamId&gt;) and the Voxta module forwards new ones to the character.
    /// Outgoing: POST /chat-say whispers a text to the player under a sender name (the character's replies, or details
    /// the character puts into the chat). IModApi ChatMessageSent delivers the messages; API1 Event_ChatMessage is
    /// hooked as a fallback (it didn't fire in the test), the same message from both is kept once.
    /// </summary>
    public partial class BridgeMod
    {
        sealed class ChatLine
        {
            public long Seq;
            public int Player;
            public string Channel, Text, Source;
            public int Recipient;
            public DateTime At;
        }

        static readonly List<ChatLine> chatLines = new List<ChatLine>();
        static long chatSeq;
        static readonly TimeSpan ChatKeep = TimeSpan.FromMinutes(5);

        static void ChatHook()
        {
            try
            {
                api.Application.ChatMessageSent += m =>
                {
                    try
                    {
                        if (m.SenderType != Eleon.SenderType.Player) return;
                        AddChat(m.SenderEntityId, m.Channel.ToString(), m.RecipientEntityId, m.Text, "modapi");
                    }
                    catch (Exception e) { Log($"chat (modapi): {e.Message}"); }
                };
                Log("chat: ChatMessageSent hooked");
            }
            catch (Exception e) { Log($"chat: hook failed: {e.Message}"); }
        }

        /// <summary>API1 Event_ChatMessage; type: the legacy channel number.</summary>
        static void ChatFromApi1(ChatInfo ci)
        {
            var channel = ci.type == 3 ? "Global" : ci.type == 5 ? "Faction" : ci.type == 8 ? "SinglePlayer" : ci.type == 9 ? "Alliance" : $"type{ci.type}";
            AddChat(ci.playerId, channel, ci.recipientEntityId, ci.msg, "api1");
        }

        static void AddChat(int player, string channel, int recipient, string text, string source)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // only whispers to oneself: the private channel to the character (nobody else sees them)
            if (recipient != player) return;
            // never our own whispers coming back (would loop: reply -> chat -> character -> reply)
            lock (recentSays)
            {
                recentSays.RemoveAll(r => DateTime.UtcNow - r.at > TimeSpan.FromSeconds(30));
                if (recentSays.Any(r => r.player == player && r.text == text.Trim())) return;
            }
            lock (chatLines)
            {
                var now = DateTime.UtcNow;
                chatLines.RemoveAll(c => now - c.At > ChatKeep);
                // the same message from the other source (within 3 s): keep one, note both sources
                var dup = chatLines.FirstOrDefault(c => c.Player == player && c.Text == text && now - c.At < TimeSpan.FromSeconds(3) && c.Source != source);
                if (dup != null) { dup.Source += "+" + source; if (dup.Channel.StartsWith("type")) dup.Channel = channel; return; }
                chatLines.Add(new ChatLine { Seq = ++chatSeq, Player = player, Channel = channel, Recipient = recipient, Text = text.Trim(), Source = source, At = now });
            }
            Log($"chat: player {player} on {channel}{(recipient > 0 ? $" to {recipient}" : "")} via {source} ({text.Length} chars)");
            if (config?.QuestLog == true || config?.CombatProbe == true) ProbeLine($"chat via {source}: player {player} channel {channel} recipient {recipient}: {text}");
        }

        /// <summary>/status: chat.&lt;steamId&gt; = [{seq, channel, text, ageS}] of the online players' own messages.</summary>
        static void WriteChat(Json j, IEnumerable<DediPlayer> online)
        {
            List<ChatLine> lines;
            lock (chatLines) lines = chatLines.ToList();
            j.Key("chatSeq").Val(chatSeq);
            j.Key("chat").Obj();
            var now = DateTime.UtcNow;
            foreach (var p in online)
            {
                if (p.SteamId == null) continue;
                var mine = lines.Where(c => c.Player == p.EntityId).ToList();
                if (mine.Count == 0) continue;
                j.Key(p.SteamId).Arr();
                foreach (var c in mine)
                {
                    j.Obj().Prop("seq", c.Seq).Prop("channel", c.Channel).Prop("text", c.Text).Prop("ageS", Math.Round((now - c.At).TotalSeconds));
                    if (c.Recipient > 0) j.Prop("recipient", c.Recipient);
                    j.EndObj();
                }
                j.EndArr();
            }
            j.EndObj();
        }

        // ----- outgoing ---------------------------------------------------------------------------

        static readonly Queue<(int player, string from, string text)> pendingSays = new Queue<(int, string, string)>();
        static readonly List<(int player, string text, DateTime at)> recentSays = new List<(int, string, DateTime)>();
        const int MaxChatChars = 350;

        /// <summary>HTTP thread: POST /chat-say (player=SteamID, text, from=sender name): a whisper to the player.</summary>
        static (int, string) HandleChatSay(string body)
        {
            var f = Form.Parse(body);
            var text = (f.Get("text") ?? "").Trim();
            if (text.Length == 0) return (400, Error("'text' is required."));
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (200, Error(why));
            var from = (f.Get("from") ?? "").Trim();
            if (from.Length == 0) from = "AI";
            var parts = ChatText.Split(text, MaxChatChars);
            lock (pendingSays) foreach (var part in parts) pendingSays.Enqueue((player.EntityId, from, part));
            return (200, new Json().Obj().Prop("ok", true).Prop("messages", parts.Count).EndObj().ToString());
        }

        /// <summary>Dedi main thread, every tick: send queued whispers.</summary>
        static void FlushSays()
        {
            while (true)
            {
                (int player, string from, string text) s;
                lock (pendingSays)
                {
                    if (pendingSays.Count == 0) return;
                    s = pendingSays.Dequeue();
                }
                lock (recentSays) recentSays.Add((s.player, s.text.Trim(), DateTime.UtcNow));
                try
                {
                    api.Application.SendChatMessage(new MessageData
                    {
                        SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = s.player,
                        SenderNameOverride = s.from, Text = s.text,
                    });
                }
                catch (Exception e) { Log($"chat say failed: {e.Message}"); }
            }
        }
    }
}
