using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eleon.Modding;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// scan_pois (POST /pois, dedi): the POIs of the player's playfield that the player could know about (discovered by
    /// the player or their faction, or loaded within <see cref="NearMeters"/>), classified (prefab index incl. traders),
    /// grouped into complexes, with faction standing and distance; resources are only counted, decoration is left out.
    /// The playfield server supplies the player's position and the loaded POIs nearby. Each answer is a snapshot of now.
    /// </summary>
    public partial class BridgeMod
    {
        const double NearMeters = 2000;
        const int MaxScanGroups = 10;

        /// <summary>HTTP thread: POST /pois (player=SteamID, scope=nearby|all).</summary>
        static (int, string) HandlePois(string body)
        {
            var f = Form.Parse(body);
            var idx = traderIndex;
            if (idx == null) return (503, Error("The POI data is not loaded yet."));
            var (code, json, player) = ForwardToPlayfield("pois-pos", f.Get("player"), ("near", NearMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (code != 200 || player == null) return (code, json);
            string Field(string k) { var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"" + k + "\":\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : null; }
            if (Field("ok") != "true") return (200, json);
            double px = Num(Field("x")), py = Num(Field("y")), pz = Num(Field("z"));
            var near = new HashSet<long>((Field("near") ?? "").Split(',').Where(s => s.Length > 0).Select(long.Parse));
            var own = (Field("mine") ?? "").Split('|').Where(s => s.Length > 0).Select(s => s.Split(new[] { '~' }, 5)).Where(p => p.Length == 5)
                .Select(p => (type: p[1], dist: Num(p[2]), docked: (int)Num(p[3]), name: p[4])).OrderBy(x => x.dist).ToList();
            var save = SaveGameDir();
            if (save == null) return (500, Error("The save game folder was not found."));
            var db = Path.Combine(save, "global.db");
            var scope = (f.Get("scope") ?? "nearby").Trim().ToLowerInvariant();

            int facGroup = -1, facId = -1;
            Dictionary<int, Dictionary<int, int>> standings = null;
            try { ReadPlayerTradeData(db, player.EntityId, player.Playfield, out facGroup, out facId, out _, out _, out _, new List<KnownPoi>()); } catch { }
            try { var dat = Path.Combine(save, "game.dat"); if (File.Exists(dat)) standings = TraderIndex.ReadStandings(File.ReadAllBytes(dat), idx.IsNpcFaction); } catch { }
            var standingKey = facGroup == 0 ? facId : -1;

            var pois = new List<ScanPoi>();
            int resources = 0, decoration = 0, unseen = 0;
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                using (var q = c.CreateCommand())
                {
                    q.CommandText =
                        "SELECT e.entityid, e.name, s.bpname, e.facgroup, e.posx, e.posy, e.posz, " +
                        "EXISTS (SELECT 1 FROM DiscoveredPOIs d WHERE d.poiid = e.entityid AND (d.entityid = @p OR (d.facgroup = @fg AND d.facid = @fi))) " +
                        "FROM Entities e JOIN Structures s ON s.entityid = e.entityid JOIN Playfields pf ON pf.pfid = e.pfid " +
                        "WHERE pf.name = @pf AND e.ispoi = 1 AND e.isremoved = 0";
                    q.Parameters.AddWithValue("@p", player.EntityId);
                    q.Parameters.AddWithValue("@fg", facGroup);
                    q.Parameters.AddWithValue("@fi", facId);
                    q.Parameters.AddWithValue("@pf", player.Playfield);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                        {
                            var p = new ScanPoi
                            {
                                Id = Convert.ToInt64(r[0]), Name = r[1] as string, FactionId = r[3] is DBNull ? -1 : Convert.ToInt32(r[3]),
                                X = Convert.ToDouble(r[4]), Y = Convert.ToDouble(r[5]), Z = Convert.ToDouble(r[6]), Discovered = Convert.ToInt32(r[7]) > 0,
                            };
                            p.Near = near.Contains(p.Id);
                            p.Distance = Math.Sqrt(Sq(p.X - px) + Sq(p.Y - py) + Sq(p.Z - pz));
                            if (!p.Discovered && !p.Near) { unseen++; continue; }
                            var bp = r[2] as string;
                            var (kind, why) = ClassifyWithIndex(bp, p.Name, p.FactionId);
                            if (kind == PoiKind.Decorative) { decoration++; continue; }
                            if (kind == PoiKind.Resource) { resources++; continue; }
                            var facts = idx.Facts(bp);
                            p.Kind = kind; p.Why = why;
                            p.Turrets = facts?.Turrets ?? -1; p.Sentries = facts?.Sentries ?? 0; p.Traders = facts?.Traders ?? 0;
                            p.TerminalDialogue = facts?.PoiDialogue;
                            var fac = idx.Faction(p.FactionId);
                            p.Faction = fac?.Label;
                            var st = idx.Standing(standings, standingKey, fac);
                            if (st.HasValue) p.Tier = TraderIndex.Tier(st.Value);
                            pois.Add(p);
                        }
                }
            }
            if (scope != "all") pois = pois.Where(p => p.Distance <= 10000 || p.Near).ToList();
            var groups = PoiScan.Group(pois);

            var j = new Json().Obj().Prop("ok", true).Prop("playfield", player.Playfield).Prop("scope", scope)
                .Vec("playerPos", (float)px, (float)py, (float)pz).Prop("asOf", DateTime.UtcNow.ToString("HH:mm:ss") + " UTC");
            j.Key("groups").Arr();
            var shown = PoiScan.Pick(groups, MaxScanGroups);
            foreach (var g in shown)
            {
                var lead = g.Lead;
                j.Obj().Prop("name", lead.Name).Prop("class", PoiClassifier.Label(g.Kind)).Prop("distance", Math.Round(g.Distance));
                if (lead.Faction != null) j.Prop("faction", lead.Faction);
                if (lead.Tier != null) j.Prop("standing", lead.Tier);
                if (g.IsComplex) j.Prop("buildings", g.Members.Count).Prop("members", PoiScan.MemberSummary(g));
                if (g.Turrets > 0) j.Prop("turrets", g.Turrets);
                var traders = g.Members.Sum(m => m.Traders);
                if (traders > 0) j.Prop("traders", traders);
                // every trading place by name, also inside a complex: they matter for item search
                var trading = g.Members.Where(m => m.Kind == PoiKind.Trading).OrderBy(m => m.Distance).ToList();
                if (g.IsComplex && trading.Count > 0)
                {
                    j.Key("tradingPlaces").Arr();
                    foreach (var t in trading) j.Obj().Prop("name", t.Name).Prop("traders", t.Traders).Prop("distance", Math.Round(t.Distance)).EndObj();
                    j.EndArr();
                }
                // any POI with a readable info terminal (bunkers, reactors, launch controllers), not only abandoned ones
                var term = g.Members.FirstOrDefault(m => m.TerminalDialogue != null && (poiTerminals?.Pages(m.TerminalDialogue, 1).Count ?? 0) > 0);
                if (term != null) j.Prop("terminal", poiTerminals.TerminalName(term.TerminalDialogue) ?? "info terminal").Prop("terminalPoi", term.Name);
                if (!g.Members.Any(m => m.Discovered)) j.Prop("onlyInRange", true);
                j.EndObj();
            }
            j.EndArr();
            var left = groups.Except(shown).ToList();
            if (left.Count > 0)
            {
                j.Prop("moreGroups", left.Count);
                var civ = left.Count(g => g.Kind == PoiKind.Civilian);
                if (civ > 0) j.Prop("moreCivilian", civ);
            }
            if (own.Count > 0)
            {
                j.Key("yourStructures").Arr();
                foreach (var o in own) j.Obj().Prop("name", o.name).Prop("type", o.type).Prop("distance", o.dist).Prop("docked", o.docked).EndObj();
                j.EndArr();
            }
            j.Prop("resources", resources).Prop("decorationLeftOut", decoration).Prop("unknownToPlayer", unseen);
            Log($"pois: {player.Name} on {player.Playfield} ({scope}): {pois.Count} POIs in {groups.Count} groups, {resources} resources, {decoration} decoration, {unseen} not known");
            return (200, j.EndObj().ToString());
        }

        static double Num(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

        /// <summary>Playfield server: the player's position and the POI structures loaded within the given range.</summary>
        static string ExecutePoisPos(Dictionary<string, string> req)
        {
            var pf = playfields.FirstOrDefault(x => x.Name == req.Get("playfield"));
            if (pf == null) return Error("The playfield is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var range = (float)Num(req.Get("near"));
            // the player's / faction's own root structures in range (docked vessels count with their carrier; not the one
            // the player is in): "id~type~distance~docked~name" joined by '|' (the ship parked outside)
            var current = FindStructureFor(pf, player, true, out _); // the one the player is on (not the active one)
            var all = pf.Entities.Values.Where(e => e != null).ToList();
            var mine = new List<string>();
            foreach (var e in all)
            {
                if (e.Type != EntityType.BA && e.Type != EntityType.CV && e.Type != EntityType.SV && e.Type != EntityType.HV) continue;
                if (e.DockedTo > 0 || (current != null && e.Id == current.Id)) continue;
                IStructure s = null;
                try { s = e.Structure; } catch { }
                if (s == null || !Owns(player, s)) continue;
                var d = UnityEngine.Vector3.Distance(e.Position, player.Position);
                if (d > range) continue;
                var docked = all.Count(x => x.DockedTo == e.Id);
                var name = (e.Name ?? "").Replace("|", "/").Replace("~", "-");
                mine.Add($"{e.Id}~{e.Type}~{Math.Round(d)}~{docked}~{name}");
            }
            var near = pf.Entities.Values
                .Where(e => e.Type == EntityType.BA || e.Type == EntityType.CV || e.Type == EntityType.SV || e.Type == EntityType.HV)
                .Where(e => UnityEngine.Vector3.Distance(e.Position, player.Position) <= range)
                .Select(e => e.Id.ToString()).ToList();
            return new Json().Obj().Prop("ok", "true").Prop("x", player.Position.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Prop("y", player.Position.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Prop("z", player.Position.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Prop("near", string.Join(",", near)).Prop("mine", string.Join("|", mine)).EndObj().ToString();
        }
    }
}
