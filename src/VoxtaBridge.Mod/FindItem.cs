using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// find_item: where is an item, and how much? Player inventory + every container in reach (docking group and own
    /// structures in range). The dedi reads the inventory fresh and sends it along, the playfield server does the rest.
    /// Logs a profiling line per search (is the range search a bottleneck?).
    /// </summary>
    public partial class BridgeMod
    {
        /// <summary>HTTP thread: POST /find (item=…, player=…).</summary>
        static (int, string) HandleFind(string body)
        {
            var f = Form.Parse(body);
            var item = f.Get("item");
            if (item == null) return (400, Error("'item' is required."));

            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (409, Error(why));

            string bag = "", toolbar = "";
            if (legacy != null)
            {
                var inv = GetInventory(player.EntityId, out _);
                if (inv != null)
                {
                    bag = Stacks.Encode(ToStacks(inv.bag));
                    toolbar = Stacks.Encode(ToStacks(inv.toolbelt));
                }
            }
            var (code, json, _) = ForwardToPlayfield("find", player.SteamId, ("item", item), ("bag", bag), ("toolbar", toolbar));
            return (code, json);
        }

        /// <summary>Playfield server: search, build the answer, log the timing.</summary>
        static string ExecuteFind(Dictionary<string, string> req)
        {
            var total = Stopwatch.StartNew();
            var pfName = req.Get("playfield");
            var pf = playfields.FirstOrDefault(x => x.Name == pfName);
            if (pf == null) return Error($"The playfield {pfName} is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var requireOwnership = req.Get("ownership") != "0";
            var active = ResolveActive(pf, player, requireOwnership, ParseRange(req.Get("range")));
            var current = active.Structure;

            var timing = new ScopeTiming();
            var inRange = new List<InRange>();
            // on foot: own structures in transfer range of the player
            var all = ActiveScope(pf, active, player, requireOwnership, ParseRange(req.Get("range")), includeRangeContainers: true, inRange, null, timing);
            var distance = inRange.Where(r => r.Structure.Entity != null)
                .GroupBy(r => r.Structure.Entity.Name).ToDictionary(g => g.Key, g => g.Min(r => r.Distance));

            // one read per container
            var scan = Stopwatch.StartNew();
            var contents = all.Select(c => (c, stacks: c.GetStacks())).ToList();
            var bag = Stacks.Decode(req.Get("bag"));
            var toolbar = Stacks.Decode(req.Get("toolbar"));
            var candidates = contents.SelectMany(x => x.stacks.Select(st => st.Id)).Concat(bag.Select(b => b.Id)).Concat(toolbar.Select(t => t.Id));
            var ids = Transfers.MatchItems(req.Get("item"), candidates, ItemName, id => gameText?.DisplayName(ItemName(id)));
            scan.Stop();

            var j = new Json().Obj().Prop("ok", true).Prop("query", req.Get("item"));
            j.Key("items").Arr();
            foreach (var id in ids)
            {
                var name = gameText?.DisplayName(ItemName(id)) ?? ItemName(id) ?? $"item #{id}";
                var where = new List<(string label, int count, bool open, double? dist, List<InvStack> states)>();
                void From(string label, List<InvStack> stacks, bool open, double? dist)
                {
                    var mine = stacks.Where(st => st.Id == id).ToList();
                    var n = Stacks.Total(mine);
                    if (n > 0) where.Add((label, n, open, dist, mine.Where(st => st.HasState).ToList()));
                }
                From("player bag", bag, false, null);
                From("player toolbar", toolbar, false, null);
                foreach (var (c, stacks) in contents)
                    From(c.Label, stacks, c.Locked, c.Vessel != null && distance.TryGetValue(c.Vessel, out var d) ? d : (double?)null);

                j.Obj().Prop("id", id).Prop("name", name).Prop("total", where.Sum(w => w.count));
                if (gameText?.IsToken(ItemName(id)) == true) j.Prop("token", true);
                j.Key("locations").Arr();
                foreach (var w in where.OrderBy(w => w.dist ?? 0))
                {
                    j.Obj().Prop("where", w.label).Prop("count", w.count);
                    if (w.open) j.Prop("open", true);
                    if (w.dist.HasValue) j.Prop("distance", Math.Round(w.dist.Value));
                    if (w.states.Count > 0 && gameText?.IsToken(ItemName(id)) != true)
                    {
                        j.Key("states").Arr();
                        foreach (var st in w.states.Take(10)) j.Obj().Prop("count", st.Count).Prop("ammo", st.Ammo).Prop("decay", st.Decay).EndObj();
                        j.EndArr();
                    }
                    j.EndObj();
                }
                j.EndArr().EndObj();
            }
            j.EndArr();
            total.Stop();

            var line = $"find '{req.Get("item")}': {ids.Count} item(s); structures {timing.GroupStructures} in group + {timing.RangeStructures} in range " +
                       $"({timing.StructuresChecked} checked for distance); containers {timing.GroupContainerCount} group + {timing.RangeContainerCount} range; " +
                       $"group containers {timing.GroupContainers.ElapsedMilliseconds} ms, range lookup {timing.RangeLookup.ElapsedMilliseconds} ms, " +
                       $"range containers {timing.RangeContainers.ElapsedMilliseconds} ms, scan {scan.ElapsedMilliseconds} ms, total {total.ElapsedMilliseconds} ms";
            Log(line);
            j.Key("timing").Obj()
                .Prop("groupStructures", timing.GroupStructures).Prop("rangeStructures", timing.RangeStructures)
                .Prop("groupContainers", timing.GroupContainerCount).Prop("rangeContainers", timing.RangeContainerCount)
                .Prop("groupMs", timing.GroupContainers.ElapsedMilliseconds).Prop("rangeLookupMs", timing.RangeLookup.ElapsedMilliseconds)
                .Prop("rangeContainersMs", timing.RangeContainers.ElapsedMilliseconds).Prop("scanMs", scan.ElapsedMilliseconds)
                .Prop("totalMs", total.ElapsedMilliseconds).EndObj();
            return j.EndObj().ToString();
        }
    }
}
