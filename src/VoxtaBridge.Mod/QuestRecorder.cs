using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Eleon.Modding;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// Quest recorder (QuestLog=true in VoxtaBridge.cfg; dedi only): every 10 s it diffs, for the online players, their
    /// DialogueVars rows, PDA chapters and the global dialogue step counters in global.db, and on every API1 player
    /// info their tokens. Each change is one line in VoxtaBridge.questlog.txt with the player, playfield, position and
    /// the structure they are in or at. Read-only; meant to learn how quest types other than trader missions (story,
    /// mining contracts, …) are stored before building on them. The file contains player names: it stays on the server.
    /// </summary>
    public partial class BridgeMod
    {
        static DateTime nextQuestScan = DateTime.MinValue;
        static volatile bool questScanRunning;
        static readonly Dictionary<int, Dictionary<string, string>> recVars = new Dictionary<int, Dictionary<string, string>>();
        static readonly Dictionary<int, HashSet<string>> recChapters = new Dictionary<int, HashSet<string>>();
        static Dictionary<string, long> recVisited;
        static readonly Dictionary<int, Dictionary<int, int>> recTokens = new Dictionary<int, Dictionary<int, int>>();
        static readonly object questLogLock = new object();

        static string QuestLogFile => Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "VoxtaBridge.questlog.txt");

        static void QuestLogLine(string who, string what)
        {
            try
            {
                lock (questLogLock)
                    File.AppendAllText(QuestLogFile, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z | {who} | {what}\n", new UTF8Encoding(false));
            }
            catch (Exception e) { Log($"quest log: {e.Message}"); }
        }

        /// <summary>"Player1 @ Viunean (14072,126,989) in 'Solaris-AMP Factory'" from the freshest data the dedi has.</summary>
        static string RecWhere(DediPlayer p)
        {
            (string pf, DateTime at, double x, double y, double z) pos;
            (string pf, DateTime at, List<LiveBox> boxes) bx;
            bool hasPos, hasBoxes;
            lock (sync) { hasPos = legacyPos.TryGetValue(p.EntityId, out pos); hasBoxes = liveBoxes.TryGetValue(p.EntityId, out bx); }
            var s = $"{p.Name} @ {p.Playfield}";
            if (!hasPos) return s;
            s += $" ({pos.x:0},{pos.y:0},{pos.z:0})";
            if (hasBoxes && bx.pf == pos.pf)
            {
                var (visiting, standing, _) = LiveBox.Locate(bx.boxes, pos.x, pos.y, pos.z, AboardMarginBlocks);
                if (visiting != null) s += $" in '{visiting.Name}' ({visiting.Type}, {visiting.Owner ?? "?"})";
                else if (standing != null) s += $" at own '{standing.Name}'";
            }
            return s;
        }

        /// <summary>Dedi main thread, every tick: start a scan every 10 s on a worker thread (global.db reads).</summary>
        static void QuestRecorderTick()
        {
            if (config?.QuestLog != true || questScanRunning || DateTime.UtcNow < nextQuestScan) return;
            nextQuestScan = DateTime.UtcNow.AddSeconds(10);
            List<DediPlayer> online;
            lock (sync) online = dediPlayers.Values.Where(x => x.Online).ToList();
            if (online.Count == 0) return;
            questScanRunning = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { QuestScan(online); }
                catch (Exception e) { Log($"quest log scan: {e.Message}"); }
                finally { questScanRunning = false; }
            });
        }

        static void QuestScan(List<DediPlayer> online)
        {
            var save = SaveGameDir();
            if (save == null) return;
            using (var c = new SqliteConnection($"Data Source={Path.Combine(save, "global.db")};Read Only=True;"))
            {
                c.Open();
                foreach (var p in online)
                {
                    var vars = new Dictionary<string, string>();
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText = "SELECT type, name, value, valuestr, state FROM DialogueVars WHERE entityid = @p";
                        q.Parameters.AddWithValue("@p", p.EntityId);
                        using (var r = q.ExecuteReader())
                            while (r.Read())
                                vars[$"{r[0]}:{r[1]}@{r[4]}"] = r[3] is string str && str.Length > 0 ? $"{r[2]} '{str}'" : Convert.ToString(r[2]);
                    }
                    var chapters = new Dictionary<string, string>();
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText = "SELECT chapterid, title FROM PlayerStatisticsPDAChapters WHERE entityid = @p";
                        q.Parameters.AddWithValue("@p", p.EntityId);
                        using (var r = q.ExecuteReader())
                            while (r.Read()) chapters[r[0] as string ?? ""] = PoiTerminals.CleanMarkup(System.Text.RegularExpressions.Regex.Replace(r[1] as string ?? "", @"\[[^\]]*\]", ""));
                    }
                    var who = RecWhere(p);
                    if (recVars.TryGetValue(p.EntityId, out var before))
                    {
                        foreach (var kv in vars)
                            if (!before.TryGetValue(kv.Key, out var old)) QuestLogLine(who, $"var new {kv.Key} = {kv.Value}");
                            else if (old != kv.Value) QuestLogLine(who, $"var {kv.Key}: {old} -> {kv.Value}");
                        foreach (var kv in before.Where(kv => !vars.ContainsKey(kv.Key))) QuestLogLine(who, $"var gone {kv.Key} (was {kv.Value})");
                    }
                    else QuestLogLine(who, $"start: {vars.Count} dialogue vars, {chapters.Count} PDA chapters");
                    recVars[p.EntityId] = vars;
                    if (recChapters.TryGetValue(p.EntityId, out var seen))
                        foreach (var kv in chapters.Where(kv => !seen.Contains(kv.Key))) QuestLogLine(who, $"pda chapter {kv.Key} '{kv.Value}'");
                    recChapters[p.EntityId] = new HashSet<string>(chapters.Keys);
                }

                // dialogue steps: global counters, attributed to everyone online
                var visited = new Dictionary<string, long>();
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT state, count FROM DialogueVisitedStates";
                    using (var r = q.ExecuteReader())
                        while (r.Read()) visited[r[0] as string ?? ""] = Convert.ToInt64(r[1]);
                }
                if (recVisited != null)
                {
                    var changed = visited.Where(kv => !recVisited.TryGetValue(kv.Key, out var n) || n != kv.Value).Select(kv => kv.Key).ToList();
                    if (changed.Count > 0)
                        QuestLogLine(string.Join(", ", online.Select(RecWhere)), "dialogue steps: " + string.Join(", ", changed.Take(40)) + (changed.Count > 40 ? $" (+{changed.Count - 40})" : ""));
                }
                recVisited = visited;
            }
        }

        /// <summary>Player entity id → the PDA chapter last activated (the one shown in the HUD?), from Event_PdaStateChange.</summary>
        static readonly Dictionary<int, string> pdaActive = new Dictionary<int, string>();

        /// <summary>Dedi: API1 Event_PdaStateChange (chapter activated / deactivated / completed). Always kept, logged when QuestLog is on.</summary>
        static void RecordPda(PdaStateInfo psi)
        {
            lock (pdaActive)
            {
                if (psi.StateChange == PdaStateChange.ChapterActivated) pdaActive[psi.PlayerId] = psi.Name;
                if (psi.StateChange == PdaStateChange.ChapterCompleted) RecordPdaCompletion(psi.PlayerId, psi.Name);
                else if (pdaActive.TryGetValue(psi.PlayerId, out var cur) && cur == psi.Name) pdaActive.Remove(psi.PlayerId);
            }
            Log($"API1 PDA {psi.StateChange} '{psi.Name}' (player {psi.PlayerId})");
            if (config?.QuestLog != true) return;
            DediPlayer p;
            lock (sync) dediPlayers.TryGetValue(psi.PlayerId, out p);
            QuestLogLine(p != null ? RecWhere(p) : $"player {psi.PlayerId}", $"pda {psi.StateChange} '{psi.Name}'");
        }

        /// <summary>Dedi, on every API1 player info: token changes (contracts and work orders are tokens).</summary>
        static void RecordTokens(PlayerInfo pi)
        {
            if (config?.QuestLog != true) return;
            var t = gameText;
            var now = new Dictionary<int, int>();
            foreach (var st in (pi.bag ?? new ItemStack[0]).Concat(pi.toolbar ?? new ItemStack[0]))
            {
                var name = ItemName(st.id);
                if (name == null || t?.IsToken(name) != true) continue;
                now.TryGetValue(st.ammo, out var n);
                now[st.ammo] = n + st.count;
            }
            Dictionary<int, int> before;
            lock (recTokens) { recTokens.TryGetValue(pi.entityId, out before); recTokens[pi.entityId] = now; }
            if (before == null) return;
            DediPlayer p;
            lock (sync) dediPlayers.TryGetValue(pi.entityId, out p);
            var who = p != null ? RecWhere(p) : $"{pi.playerName} @ {pi.playfield}";
            foreach (var id in now.Keys.Union(before.Keys))
            {
                now.TryGetValue(id, out var a);
                before.TryGetValue(id, out var b);
                if (a != b) QuestLogLine(who, $"token {id} '{t?.GetToken(id)?.Name}': {b} -> {a}");
            }
        }
    }
}
