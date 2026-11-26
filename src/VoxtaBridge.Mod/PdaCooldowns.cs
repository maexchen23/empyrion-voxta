using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// Cooldowns (POST /cooldowns, dedi): when the player may do something again, in game time. Refinery contracts come
    /// from the save (the stored value is the next allowed time). Repeatable PDA missions with a waiting time (e.g. RE2's
    /// UCH research for unlock points) keep their state in the player file, so the bridge records the game time of each
    /// completion from API1's PDA events (VoxtaBridge.pdatimes.txt next to the mod); completions before that are unknown.
    /// </summary>
    public partial class BridgeMod
    {
        static volatile PdaRepeats pdaRepeats;
        /// <summary>(player entity id, chapter id) → game time of the last completion, in seconds.</summary>
        static readonly Dictionary<(int player, string chapter), long> pdaCompleted = new Dictionary<(int, string), long>();
        static bool pdaCompletedLoaded;

        static string PdaTimesFile => Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "VoxtaBridge.pdatimes.txt");

        /// <summary>Dedi, background loader: the scenario's repeatable PDA missions (Extras/PDA/PDA.yaml + PDA.csv) and their dialogues.</summary>
        static void LoadPdaRepeats(string content, string scenario, string dialogues)
        {
            try
            {
                string Pda(string file)
                {
                    foreach (var dir in new[] { scenario, content })
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        var p = Path.Combine(dir, "Extras", "PDA", file);
                        if (File.Exists(p)) return p;
                    }
                    return null;
                }
                var yaml = Pda("PDA.yaml");
                if (yaml == null) { Log("pda repeats: no PDA.yaml"); return; }
                var r = new PdaRepeats();
                using (var t = new StreamReader(yaml)) r.LoadPda(t);
                if (Pda("PDA.csv") is string csv) using (var t = new StreamReader(csv)) r.LoadTitles(t);
                if (dialogues != null) using (var t = new StreamReader(dialogues)) r.LinkDialogues(t);
                pdaRepeats = r;
                Log($"pda repeats: {r.Count} missions with a waiting time: " +
                    string.Join(", ", r.All.Select(x => $"{x.Title ?? x.Chapter} {x.DelaySeconds / 60} min{(x.OncePerPoiVar != null ? ", once per POI" : "")}")));
            }
            catch (Exception e) { Log($"pda repeats: {e.Message}"); }
        }

        static void LoadPdaCompleted()
        {
            if (pdaCompletedLoaded) return;
            pdaCompletedLoaded = true;
            try
            {
                if (!File.Exists(PdaTimesFile)) return;
                foreach (var line in File.ReadAllLines(PdaTimesFile))
                {
                    var p = line.Split('\t');
                    if (p.Length == 3 && int.TryParse(p[0], out var player) && long.TryParse(p[2], out var at)) pdaCompleted[(player, p[1])] = at;
                }
            }
            catch (Exception e) { Log($"pda repeats: reading {PdaTimesFile}: {e.Message}"); }
        }

        /// <summary>Dedi: a PDA chapter was completed; remember when, if it is a repeatable mission with a waiting time.</summary>
        static void RecordPdaCompletion(int playerId, string chapter)
        {
            if (pdaRepeats?.Get(chapter) == null || !(GameTimeSeconds() is long now)) return;
            lock (pdaCompleted)
            {
                LoadPdaCompleted();
                pdaCompleted[(playerId, chapter)] = now;
                try
                {
                    File.WriteAllLines(PdaTimesFile, pdaCompleted.Select(kv =>
                        $"{kv.Key.player.ToString(CultureInfo.InvariantCulture)}\t{kv.Key.chapter}\t{kv.Value.ToString(CultureInfo.InvariantCulture)}"));
                }
                catch (Exception e) { Log($"pda repeats: writing {PdaTimesFile}: {e.Message}"); }
            }
            Log($"pda repeats: player {playerId} completed {chapter} at game time {now}");
        }

        /// <summary>
        /// HTTP thread: POST /cooldowns (player). {gameTimeS, refineries: [{key, value = next allowed time, where, ly}],
        /// missions: [{chapter, title, delayS, completedAt?, readyAt?, onceAt: [{where, ly}]}]}.
        /// </summary>
        static (int, string) HandleCooldowns(string body)
        {
            var f = Form.Parse(body);
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (200, Error(why));
            var save = SaveGameDir();
            var j = new Json().Obj().Prop("ok", true);
            if (GameTimeSeconds() is long now) j.Prop("gameTimeS", now);
            var db = save == null ? null : Path.Combine(save, "global.db");

            if (miningContracts?.CooldownVar is string cv && db != null)
                try { j.Key("refineries").Raw(ReadContractOffers(db, player.EntityId, cv, player.Playfield, withDiscovered: true)); }
                catch (Exception e) { j.Prop("refineriesNote", e.Message); }

            j.Key("missions").Arr();
            if (pdaRepeats is PdaRepeats reps)
            {
                lock (pdaCompleted) LoadPdaCompleted();
                foreach (var r in reps.All)
                {
                    long? done = null;
                    lock (pdaCompleted) if (pdaCompleted.TryGetValue((player.EntityId, r.Chapter), out var at)) done = at;
                    string once = null;
                    if (r.OncePerPoiVar != null && db != null)
                        try { once = ReadPoiVarRows(db, player.EntityId, r.OncePerPoiVar, player.Playfield); } catch { }
                    var usedAny = once != null && once.Contains("\"value\":1");
                    if (done == null && !usedAny) continue; // nothing known about this mission for the player
                    j.Obj().Prop("chapter", r.Chapter).Prop("title", r.Title ?? r.Chapter).Prop("delayS", r.DelaySeconds);
                    if (done is long d) j.Prop("completedAt", d).Prop("readyAt", d + r.DelaySeconds);
                    if (once != null) j.Key("perPoi").Raw(once);
                    j.EndObj();
                }
            }
            j.EndArr();
            return (200, j.EndObj().ToString());
        }
    }
}
