using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Virtual crafting: mods can't drive real constructors, so the character "crafts"
    /// out of one marked container whose name ends with a suffix (default "VC"): inputs are taken only from it, results
    /// and leftover intermediates go back into it. Plan first (confirm=0), craft after the player agreed (confirm=1).
    /// Instant; the game's craft time is reported for flavour.
    /// </summary>
    public partial class BridgeMod
    {
        static volatile Recipes recipes;
        const int MaxCraftDepth = 6;

        /// <summary>HTTP thread: POST /craft (item, amount, vc suffix, confirm=0|1, player).</summary>
        static (int, string) HandleCraft(string body)
        {
            var f = Form.Parse(body);
            var item = f.Get("item");
            if (item == null) return (400, Error("'item' is required."));
            var confirm = f.Get("confirm") == "1";
            if (confirm && !config.TransfersEnabled)
                return (403, Error("Crafting is switched off on this server (TransfersEnabled=false)."));
            var (code, json, player) = ForwardToPlayfield("craft", f.Get("player"),
                ("item", item), ("amount", f.Get("amount") ?? "1"), ("vc", f.Get("vc") ?? "VC"), ("confirm", confirm ? "1" : "0"),
                ("constructor", config.RequireConstructor ? "1" : "0"), ("structure", f.Get("structure") ?? ""),
                ("max", config.MaxItemsPerTransfer.ToString()));
            if (confirm && player != null) Audit(player, "craft: " + item, f.Get("amount") ?? "1", "VC", "VC", json);
            return (code, json);
        }

        /// <summary>Does a container name end with the VC suffix (as its own word, or the whole name)?</summary>
        public static bool IsVcName(string name, string suffix)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(suffix)) return false;
            name = name.Trim();
            suffix = suffix.Trim();
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Length == suffix.Length) return true;
            var before = name[name.Length - suffix.Length - 1];
            return !char.IsLetterOrDigit(before);
        }

        static int ItemIdOf(string name)
        {
            if (name == null) return 0;
            ItemName(0);
            lock (itemNames)
                foreach (var kv in itemNames)
                    if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return 0;
        }

        static string Show(string internalName) => gameText?.DisplayName(internalName) ?? internalName;

        /// <summary>Playfield server.</summary>
        static string ExecuteCraft(Dictionary<string, string> req)
        {
            var r = recipes;
            if (r == null || r.Count == 0) return Error("The game's recipes are not loaded (yet).");

            var pfName = req.Get("playfield");
            var pf = playfields.FirstOrDefault(x => x.Name == pfName);
            if (pf == null) return Error($"The playfield {pfName} is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var requireOwnership = req.Get("ownership") != "0";
            var active = ResolveActive(pf, player, requireOwnership, ParseRange(req.Get("range")));
            var current = active.Structure;

            // the VC container: on the active structure's docking group first, else on own structures within transfer range,
            // closest first; works on foot too, like the game's matter transmitter route
            var suffix = req.Get("vc") ?? "VC";
            var wantedStructure = (req.Get("structure") ?? "").Trim();
            var inRange = new List<InRange>();
            var scope = ActiveScope(pf, active, player, requireOwnership, ParseRange(req.Get("range")), includeRangeContainers: true, inRange);
            var allVcs = scope.Where(c => IsVcName(c.Name, suffix)).Cast<GameContainer>().ToList();
            if (wantedStructure.Length > 0)
                allVcs = allVcs.Where(c => string.Equals(c.Vessel, wantedStructure, StringComparison.OrdinalIgnoreCase)
                                           || c.Vessel.StartsWith(wantedStructure, StringComparison.OrdinalIgnoreCase)).ToList();
            double DistanceOf(GameContainer c) =>
                c.IsCurrent || (current != null && (CarrierOf(pf, current) ?? current).Id == (CarrierOf(pf, c.Structure) ?? c.Structure).Id) ? -1
                : inRange.Where(r => r.Structure.Id == c.Structure.Id || r.Structure.Id == (CarrierOf(pf, c.Structure) ?? c.Structure).Id)
                    .Select(r => (double)r.Distance).DefaultIfEmpty(double.MaxValue).Min();
            var vcs = allVcs.GroupBy(DistanceOf).OrderBy(g => g.Key).FirstOrDefault()?.ToList() ?? new List<GameContainer>();
            if (vcs.Count == 0)
                return Error(wantedStructure.Length > 0
                    ? $"No container ending with '{suffix}' on '{wantedStructure}' within transfer range."
                    : $"No crafting container: name a container on one of the player's ships or bases (aboard or within transfer range) so it ends with '{suffix}' (e.g. 'Production {suffix}') and put the materials into it.");
            if (vcs.Count > 1)
                return Error($"Several containers end with '{suffix}' ({string.Join(", ", vcs.Select(c => c.Label))}); keep exactly one per structure, or name the structure.");
            var vc = vcs[0];
            // constructors: the docking group of the structure that holds the VC
            var vcRoot = CarrierOf(pf, vc.Structure) ?? vc.Structure;
            var group = new[] { vcRoot }.Concat(DockedOf(vcRoot)).Where(x => !requireOwnership || Owns(player, x))
                .Select(x => new GroupMember { Structure = x }).ToList();

            // what to make
            var craftable = new List<int>();
            ItemName(0);
            lock (itemNames)
                foreach (var kv in itemNames)
                {
                    var rec = r.Get(kv.Value);
                    if (rec != null && rec.Targets.Count > 0) craftable.Add(kv.Key);
                }
            var itemId = Transfers.ResolveItem(req.Get("item"), craftable, ItemName, id => gameText?.DisplayName(ItemName(id)), out var itemError);
            if (itemId == 0)
            {
                if (itemError == null || !itemError.StartsWith("No '")) return Error(itemError);
                var close = CraftSuggestions(req.Get("item"));
                return Error(close.Count > 0
                    ? $"There is no recipe for '{req.Get("item")}'. Did you mean: {string.Join(", ", close)}? Use the exact name."
                    : $"There is no recipe for '{req.Get("item")}', and no similar craftable item. empyrion_search_items finds the game's names.");
            }
            var itemName = ItemName(itemId);

            if (!int.TryParse(req.Get("amount"), out var amount) || amount <= 0) return Error($"Bad amount '{req.Get("amount")}'.");
            int.TryParse(req.Get("max"), out var max);
            if (max > 0 && amount > max) return Error($"At most {max} per crafting order (server setting).");

            // constructors on the ship
            Func<string, bool> allowed = null;
            HashSet<string> have = null;
            if (req.Get("constructor") == "1")
            {
                have = ConstructorTargets(group.Select(g => g.Structure));
                allowed = t => have.Contains(t);
            }

            var confirm = req.Get("confirm") == "1";
            if (confirm && vc.Locked) return Error(Transfers.OpenMessage(new[] { vc.Label }, "crafting container").Replace("Not moved", "Not crafted"));

            var original = vc.GetStacks();
            var plan = r.MakePlan(itemName, amount, Recipes.StockOf(original, ItemName), MaxCraftDepth, allowed);

            var j = new Json().Obj();
            List<InvStack> result = null;
            string applyError = null;
            if (plan.Ok)
            {
                (result, applyError) = Recipes.Apply(original, plan, ItemIdOf, vc.MaxSlots, StackSizeOf);
            }
            var ok = plan.Ok && applyError == null;
            var crafted = false;
            string error = null;
            if (!plan.Ok)
            {
                error = $"Not enough materials in '{vc.Label}' for {amount} {Show(itemName)}.";
                if (have != null && r.Get(itemName) is Recipe top && !top.Targets.Any(have.Contains))
                    error = $"No constructor on this ship can make {Show(itemName)} (needs one of: {string.Join(", ", top.Targets.Select(ConstructorLabel))}).";
            }
            else if (applyError != null) error = applyError;
            else if (confirm)
            {
                vc.SetStacks(result);
                var after = vc.GetStacks();
                var expected = result.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
                var observed = after.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
                var same = expected.Count == observed.Count && expected.All(kv => observed.TryGetValue(kv.Key, out var n) && n == kv.Value);
                if (same) crafted = true;
                else
                {
                    vc.SetStacks(original);
                    ok = false;
                    error = $"The game did not keep the change in '{vc.Label}'; its contents were restored, nothing was crafted.";
                    api.LogError($"[{ModName}] craft write mismatch in {vc.Label}: expected {Stacks.Encode(result)}, got {Stacks.Encode(after)}");
                }
            }

            j.Prop("ok", ok);
            if (error != null) j.Prop("error", error);
            j.Prop("item", Show(itemName)).Prop("amount", amount).Prop("container", vc.Label)
                .Prop("crafted", crafted).Prop("planOnly", !confirm)
                .Prop("craftSeconds", Math.Round(plan.CraftSeconds));
            if (vc.Locked && !confirm) j.Prop("open", true);
            if (r.Get(itemName) is Recipe target) RecipeJson(j, target);
            void List(string key, Dictionary<string, int> d)
            {
                j.Key(key).Arr();
                foreach (var kv in d.OrderByDescending(kv => kv.Value))
                    j.Obj().Prop("name", Show(kv.Key)).Prop("count", kv.Value).EndObj();
                j.EndArr();
            }
            List("uses", plan.Consumed);
            List("produces", plan.Ok ? Recipes.Outputs(plan) : new Dictionary<string, int>());
            List("missing", plan.Missing);
            List("steps", plan.Batches.ToDictionary(kv => kv.Key, kv => kv.Value * (r.Get(kv.Key)?.OutputCount ?? 1)));
            Log($"craft {(confirm ? "" : "plan ")}{amount} {itemName} in {vc.Label}: " +
                (crafted ? "done" : ok ? "ok" : "failed: " + error));
            return j.EndObj().ToString();
        }

        // ----- constructors (RequireConstructor) -------------------------------------------------

        /// <summary>
        /// Constructor block → recipe Target keys. Best guess from the block names (RE2); the game's own mapping is
        /// internal. Verify when RequireConstructor is used.
        /// </summary>
        static readonly Dictionary<string, string[]> ConstructorBlocks = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConstructorSurvival"] = new[] { "SurvC" },
            ["ConstructorSurvivalV2"] = new[] { "SurvC" },
            ["ConstructorSV"] = new[] { "SmallC" },
            ["ConstructorSV_Old"] = new[] { "SmallC" },
            ["ConstructorSmallV2"] = new[] { "SmallC" },
            ["ConstructorHV"] = new[] { "HoverC" },
            ["ConstructorT0"] = new[] { "BaseC" },
            ["ConstructorT1"] = new[] { "LargeC" },
            ["ConstructorT1V2"] = new[] { "LargeC" },
            ["ConstructorMS"] = new[] { "LargeC" },
            ["ConstructorT2"] = new[] { "AdvC" },
            ["ConstructorRecursive"] = new[] { "AdvC" },
            ["FoodProcessorV2"] = new[] { "FoodP" },
            ["FoodProcessorMS"] = new[] { "FoodP" },
            ["Furnace"] = new[] { "Furn" },
        };

        static string ConstructorLabel(string target)
        {
            switch (target)
            {
                case "SurvC": return "portable constructor";
                case "SmallC": return "small/SV constructor";
                case "HoverC": return "HV constructor";
                case "BaseC": return "small constructor";
                case "LargeC": return "large constructor";
                case "AdvC": return "advanced constructor";
                case "FoodP": return "food processor";
                case "Furn": return "furnace";
                case "SuitC": return "suit constructor";
                default: return target;
            }
        }

        static readonly Dictionary<int, (DateTime at, HashSet<string> targets)> constructorCache = new Dictionary<int, (DateTime, HashSet<string>)>();

        /// <summary>Target keys of all constructor blocks in the structures (block scan, cached 2 minutes per structure).</summary>
        static HashSet<string> ConstructorTargets(IEnumerable<IStructure> structures)
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in structures)
            {
                if (s == null) continue;
                if (!constructorCache.TryGetValue(s.Id, out var c) || DateTime.UtcNow - c.at > TimeSpan.FromMinutes(2))
                {
                    var sw = Stopwatch.StartNew();
                    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var types = new Dictionary<int, string[]>();
                    long blocks = 0;
                    try
                    {
                        var min = s.MinPos;
                        var max = s.MaxPos;
                        for (int x = min.x; x <= max.x; x++)
                        for (int y = min.y; y <= max.y; y++)
                        for (int z = min.z; z <= max.z; z++)
                        {
                            var b = s.GetBlock(x, y + 128, z);
                            if (b == null) continue;
                            b.Get(out var type, out _, out _, out _);
                            if (type <= 0) continue;
                            blocks++;
                            if (!types.TryGetValue(type, out var t))
                            {
                                var n = ItemName(type);
                                types[type] = t = n != null && ConstructorBlocks.TryGetValue(n, out var keys) ? keys : null;
                            }
                            if (t != null) foreach (var k in t) found.Add(k);
                        }
                    }
                    catch (Exception e) { Log($"constructor scan of {s.Entity?.Name} failed: {e.Message}"); }
                    Log($"constructor scan {s.Entity?.Name}: {blocks} blocks in {sw.ElapsedMilliseconds} ms → {string.Join(",", found)}");
                    constructorCache[s.Id] = c = (DateTime.UtcNow, found);
                }
                all.UnionWith(c.targets);
            }
            return all;
        }
    }
}
