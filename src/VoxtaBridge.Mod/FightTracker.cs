using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Scripted fights (RE2 combat sites, the bandit beacon ambush, smuggler jobs). The game tells the dedicated server when
    /// a PDA chapter starts or ends (API1), but not its task progress; the playfield server sees every block a player
    /// destroys (mod API <c>GameEvent BlockDestroyed</c>: player, block type, position, no structure). So:
    /// - playfield server: counts the destroyed blocks per player and block type, sent with every snapshot
    ///   (<c>players[].destroyed</c>, cumulative per process);
    /// - dedicated server: which fight chapters each player has running (<c>fights.&lt;steamId&gt;</c> in /status, with the
    ///   waves from <see cref="PdaFights"/>); the module counts the destroyed cores since the fight started.
    /// </summary>
    public partial class BridgeMod
    {
        static volatile PdaFights pdaFights;

        // ----- playfield server -------------------------------------------------------------------------------------

        /// <summary>Player entity id → block name → destroyed by that player (this process).</summary>
        static readonly Dictionary<int, Dictionary<string, int>> destroyedBlocks = new Dictionary<int, Dictionary<string, int>>();
        static bool fightHooked;
        /// <summary>Raw BlockDestroyed arguments logged so far in this process (the first few, to check the parsing).</summary>
        static int rawLogged;
        static readonly Regex LastInt = new Regex(@"(-?\d+)\D*$", RegexOptions.Compiled);
        static readonly Regex LeadingInt = new Regex(@"^\s*(\d+)\b", RegexOptions.Compiled);

        static void FightHook()
        {
            if (fightHooked) return;
            fightHooked = true;
            api.GameEvent += (type, a1, a2, a3, a4, a5) =>
            {
                if (type != GameEventType.BlockDestroyed) return;
                try
                {
                    if (rawLogged < 5)
                    {
                        rawLogged++;
                        Log($"fights: BlockDestroyed raw: {a1?.GetType().Name}:{a1} | {a2?.GetType().Name}:{a2} | {a3?.GetType().Name}:{a3} | {a4} | {a5}");
                    }
                    // seen on dev: a1 = "'<player name>', <entity id>", a3 = "<block type id>/R:…/D:…", a5 = block position
                    var playerId = a1 is IPlayer p ? p.Id : LastInt.Match(a1?.ToString() ?? "") is Match pm && pm.Success ? int.Parse(pm.Groups[1].Value) : 0;
                    var blockId = LeadingInt.Match(a3?.ToString() ?? "") is Match bm && bm.Success ? int.Parse(bm.Groups[1].Value) : -1;
                    if (playerId <= 0 || blockId < 0) return;
                    var name = ItemName(blockId) ?? blockId.ToString();
                    int total;
                    lock (destroyedBlocks)
                    {
                        if (!destroyedBlocks.TryGetValue(playerId, out var byName)) destroyedBlocks[playerId] = byName = new Dictionary<string, int>(StringComparer.Ordinal);
                        byName.TryGetValue(name, out var n);
                        byName[name] = total = n + 1;
                    }
                    if (name.StartsWith("Core", StringComparison.Ordinal)) Log($"fights: player {playerId} destroyed {name} (#{total} in this process)");
                }
                catch (Exception e) { Log($"fights: BlockDestroyed: {e.Message}"); }
            };
        }

        /// <summary>Snapshot: <c>"destroyed": {"CoreNPC": 3, …}</c> for the player, when they destroyed anything in this process.</summary>
        static void WriteDestroyed(Json j, IPlayer p)
        {
            KeyValuePair<string, int>[] counts;
            lock (destroyedBlocks)
            {
                if (!destroyedBlocks.TryGetValue(p.Id, out var byName) || byName.Count == 0) return;
                counts = byName.ToArray();
            }
            j.Key("destroyed").Obj();
            foreach (var kv in counts.OrderBy(kv => kv.Key)) j.Prop(kv.Key, kv.Value);
            j.EndObj();
        }

        // ----- dedicated server ---------------------------------------------------------------------------------------

        /// <summary>Player entity id → fight chapter id → (state, since UTC).</summary>
        static readonly Dictionary<int, Dictionary<string, (string state, DateTime at)>> playerFights = new Dictionary<int, Dictionary<string, (string, DateTime)>>();

        /// <summary>Dedi, from API1 Event_PdaStateChange: a fight chapter started, ended or was switched off.</summary>
        static void RecordFight(PdaStateInfo psi)
        {
            if (pdaFights?.ById(psi.Name) == null) return;
            var state = psi.StateChange == PdaStateChange.ChapterActivated ? "active"
                : psi.StateChange == PdaStateChange.ChapterCompleted ? "completed" : "stopped";
            lock (playerFights)
            {
                if (!playerFights.TryGetValue(psi.PlayerId, out var mine)) playerFights[psi.PlayerId] = mine = new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);
                mine[psi.Name] = (state, DateTime.UtcNow);
                // ended fights stay listed for 10 minutes so the module sees how they ended
                foreach (var old in mine.Where(kv => kv.Value.state != "active" && DateTime.UtcNow - kv.Value.at > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
                    mine.Remove(old);
            }
            var title = pdaFights?.ById(psi.Name)?.Title;
            Log($"fights: player {psi.PlayerId} {psi.Name} ({title}) {state}");
            if (config?.QuestLog == true)
            {
                DediPlayer p;
                lock (sync) dediPlayers.TryGetValue(psi.PlayerId, out p);
                QuestLogLine(p != null ? RecWhere(p) : $"player {psi.PlayerId}", $"fight '{title}' ({psi.Name}) {state}");
            }
        }

        /// <summary>/status <c>fights.&lt;steamId&gt;</c>: [{id, title, state, sinceS, startMessage?, after?, waves: [{targets, types, ships, what, allies?, delayS, message?}]}].</summary>
        static string ReadFights(int playerId)
        {
            var fights = pdaFights;
            if (fights == null) return null;
            KeyValuePair<string, (string state, DateTime at)>[] mine;
            lock (playerFights)
            {
                if (!playerFights.TryGetValue(playerId, out var d) || d.Count == 0) return null;
                mine = d.ToArray();
            }
            var j = new Json().Arr();
            foreach (var kv in mine.OrderBy(kv => kv.Value.at))
            {
                var f = fights.ById(kv.Key);
                if (f == null) continue;
                j.Obj().Prop("id", f.Id).Prop("title", f.Title).Prop("state", kv.Value.state)
                    .Prop("sinceS", (long)(DateTime.UtcNow - kv.Value.at).TotalSeconds);
                if (f.StartMessage != null) j.Prop("startMessage", f.StartMessage);
                if (f.After != null) j.Prop("after", f.After);
                j.Key("waves").Arr();
                foreach (var w in f.Waves)
                {
                    j.Obj().Prop("targets", w.Targets).Prop("types", string.Join(",", w.Types)).Prop("ships", w.Ships)
                        .Prop("what", string.Join(", ", w.What)).Prop("delayS", w.DelayS);
                    if (w.Allies > 0) j.Prop("allies", $"{w.Allies} {string.Join(", ", w.AlliesWhat)}");
                    if (w.Message != null) j.Prop("message", w.Message);
                    j.EndObj();
                }
                j.EndArr().EndObj();
            }
            return j.EndArr().ToString();
        }
    }
}
