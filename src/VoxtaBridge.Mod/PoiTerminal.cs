using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// POST /poi-terminal (dedi): the info terminal texts of a POI (abandoned bunkers, reactors, launch controllers), by
    /// POI name. Like in the game, only where the player could read them: a POI on the current playfield within
    /// <see cref="NearMeters"/> (loaded), or one the player has visited. Pages in tree order, the option labels with them.
    /// </summary>
    public partial class BridgeMod
    {
        static (int, string) HandlePoiTerminal(string body)
        {
            var f = Form.Parse(body);
            var idx = traderIndex;
            var terms = poiTerminals;
            if (idx == null || terms == null) return (503, Error("The POI terminal texts are not loaded (yet)."));
            var wanted = (f.Get("poi") ?? "").Trim();
            if (wanted.Length == 0) return (400, Error("'poi' is required (a POI name, e.g. 'Abandoned Bunker')."));

            var (code, json, player) = ForwardToPlayfield("pois-pos", f.Get("player"), ("near", NearMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (code != 200 || player == null) return (code, json);
            string Field(string k) { var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"" + k + "\":\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : null; }
            if (Field("ok") != "true") return (200, json);
            var near = new HashSet<long>((Field("near") ?? "").Split(',').Where(s => s.Length > 0).Select(long.Parse));
            var save = SaveGameDir();
            if (save == null) return (500, Error("The save game folder was not found."));

            // candidates by name: near on this playfield, or visited anywhere; the nearest/visited one with a terminal wins
            var hits = new List<(long id, string name, string bp, string pf, bool visited)>();
            using (var c = new SqliteConnection($"Data Source={Path.Combine(save, "global.db")};Read Only=True;"))
            {
                c.Open();
                using (var q = c.CreateCommand())
                {
                    q.CommandText =
                        "SELECT e.entityid, e.name, s.bpname, pf.name, EXISTS (SELECT 1 FROM VisitedStructures v WHERE v.poiid = e.entityid AND v.entityid = @p) " +
                        "FROM Entities e JOIN Structures s ON s.entityid = e.entityid JOIN Playfields pf ON pf.pfid = e.pfid " +
                        "WHERE e.ispoi = 1 AND e.isremoved = 0 AND e.name LIKE @n";
                    q.Parameters.AddWithValue("@p", player.EntityId);
                    q.Parameters.AddWithValue("@n", "%" + wanted.Replace("%", "") + "%");
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                            hits.Add((Convert.ToInt64(r[0]), r[1] as string, r[2] as string, r[3] as string, Convert.ToInt32(r[4]) > 0));
                }
            }
            var readable = hits.Where(h => h.visited || near.Contains(h.id)).ToList();
            if (readable.Count == 0)
                return (200, Error(hits.Count == 0
                    ? $"No POI called '{wanted}' is known."
                    : $"The player has to be at '{hits[0].name}' (within {NearMeters / 1000:0} km) or have visited it to read its terminal."));
            var withTerminal = readable.Select(h => (h, init: idx.Facts(h.bp)?.PoiDialogue)).Where(x => x.init != null && terms.Pages(x.init, 1).Count > 0)
                .OrderByDescending(x => near.Contains(x.h.id)).ThenBy(x => string.Equals(x.h.name, wanted, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            if (withTerminal.Count == 0) return (200, Error($"'{readable[0].name}' has no info terminal with text."));
            var (hit, initName) = withTerminal[0];

            var pages = terms.Pages(initName);
            var j = new Json().Obj().Prop("ok", true).Prop("poi", hit.name).Prop("playfield", hit.pf).Prop("terminal", terms.TerminalName(initName))
                .Prop("near", near.Contains(hit.id)).Prop("visited", hit.visited).Key("pages").Arr();
            foreach (var p in pages)
            {
                j.Obj().Prop("text", p.Text).Key("options").Arr();
                foreach (var o in p.Options) j.Val(o);
                j.EndArr().EndObj();
            }
            Log($"poi terminal: {player.Name} read {hit.name} ({initName}, {pages.Count} pages)");
            return (200, j.EndArr().EndObj().ToString());
        }
    }
}
