using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>A crafting recipe from Templates.ecf.</summary>
    public sealed class Recipe
    {
        public string Name;
        public int OutputCount = 1;
        public double CraftTime;
        /// <summary>Constructor types that can make it (SurvC, SmallC, HoverC, BaseC, LargeC, AdvC, FoodP, Furn, SuitC).</summary>
        public List<string> Targets = new List<string>();
        public List<(string item, int count)> Inputs = new List<(string, int)>();
    }

    /// <summary>
    /// Recipes from the game's Templates.ecf (base game, then the scenario's file replaces entries by name) and the
    /// virtual-crafting planner. Game-free, unit-tested.
    /// </summary>
    public sealed class Recipes
    {
        readonly Dictionary<string, Recipe> byName = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);

        public int Count => byName.Count;
        public Recipe Get(string name) => name != null && byName.TryGetValue(name, out var r) ? r : null;

        static readonly Regex Header = new Regex(@"^\{\s*\+?Template\s+Name:\s*(?<name>[^\s,#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex InputLine = new Regex(@"^(?<item>[A-Za-z0-9_.\-]+)\s*:\s*(?<n>\d+)", RegexOptions.Compiled);

        public void Load(TextReader reader)
        {
            string line;
            Recipe current = null;
            int depth = 0;
            bool inInputs = false;
            while ((line = reader.ReadLine()) != null)
            {
                var hash = line.IndexOf('#');
                var t = (hash >= 0 ? line.Substring(0, hash) : line).Trim();
                if (t.Length == 0) continue;

                if (t[0] == '{')
                {
                    depth++;
                    if (depth == 1)
                    {
                        var m = Header.Match(t);
                        current = m.Success ? new Recipe { Name = m.Groups["name"].Value } : null;
                        if (current != null) byName[current.Name] = current;
                    }
                    else if (depth == 2 && current != null)
                        inInputs = t.IndexOf("Inputs", StringComparison.OrdinalIgnoreCase) >= 0;
                    continue;
                }
                if (t[0] == '}')
                {
                    if (depth == 2) inInputs = false;
                    depth = Math.Max(0, depth - 1);
                    if (depth == 0) current = null;
                    continue;
                }
                if (current == null) continue;

                if (inInputs)
                {
                    var m = InputLine.Match(t);
                    if (m.Success) current.Inputs.Add((m.Groups["item"].Value, int.Parse(m.Groups["n"].Value)));
                    continue;
                }
                if (depth != 1) continue;
                var v = After(t, "OutputCount:");
                if (v != null && int.TryParse(v, out var oc) && oc > 0) current.OutputCount = oc;
                v = After(t, "CraftTime:");
                if (v != null && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ct)) current.CraftTime = ct;
                v = After(t, "Target:", raw: true);
                if (v != null)
                    current.Targets = v.Trim().Trim('"').Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            }
        }

        static string After(string line, string key, bool raw = false)
        {
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return null;
            var v = line.Substring(key.Length).Trim();
            if (raw) return v;
            var comma = v.IndexOf(',');
            return (comma >= 0 ? v.Substring(0, comma) : v).Trim();
        }

        /// <summary>
        /// The scenario's Templates.ecf <b>replaces</b> the base game's (verified in game: Anti-Parasite Pills
        /// have a base-game recipe but none in RE2, and are not craftable in RE2). Base file only without a scenario file.
        /// </summary>
        public static Recipes LoadFiles(string contentDir, string scenarioDir, Action<string> log)
        {
            var r = new Recipes();
            var scenarioFile = string.IsNullOrEmpty(scenarioDir) ? null : Path.Combine(scenarioDir, "Content", "Configuration", "Templates.ecf");
            var baseFile = string.IsNullOrEmpty(contentDir) ? null : Path.Combine(contentDir, "Configuration", "Templates.ecf");
            var path = scenarioFile != null && File.Exists(scenarioFile) ? scenarioFile : baseFile;
            {
                if (path == null || !File.Exists(path)) return r;
                try
                {
                    using (var reader = new StreamReader(path)) r.Load(reader);
                    log?.Invoke($"loaded {path}");
                }
                catch (Exception e) { log?.Invoke($"could not read {path}: {e.Message}"); }
            }
            return r;
        }

        // ----- planning ---------------------------------------------------------------------------

        public sealed class Plan
        {
            public string Item;
            public int Wanted;
            /// <summary>Taken from the VC container (items that were there).</summary>
            public Dictionary<string, int> Consumed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Recipe runs (name → batches).</summary>
            public Dictionary<string, int> Batches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Produced beyond what was used (intermediates and surplus output) → back into the VC container.</summary>
            public Dictionary<string, int> Leftover = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            /// <summary>What is missing (at the highest level that could not be made), empty when craftable.</summary>
            public Dictionary<string, int> Missing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Constructor types each recipe run could use (for the RequireConstructor check).</summary>
            public Dictionary<string, List<string>> TargetsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public double CraftSeconds;
            public bool Ok => Missing.Count == 0;
        }

        /// <summary>
        /// Plans <paramref name="wanted"/> of an item from the stock in the VC container: existing stock first (also
        /// intermediates), recipes only where the whole expansion works out, otherwise the item itself is reported missing
        /// (so "Iron Ingot missing", not "Rock Dust missing", although RE2 has a recipe for iron ore).
        /// The final item itself is always crafted (stock of it in the container is not counted toward the wanted amount).
        /// Recipes without a Target (not craftable in any constructor) are not used.
        /// </summary>
        public Plan MakePlan(string item, int wanted, IDictionary<string, int> stock, int maxDepth, Func<string, bool> allowedTarget = null)
        {
            var plan = new Plan { Item = item, Wanted = wanted };
            var pool = new Dictionary<string, int>(stock, StringComparer.OrdinalIgnoreCase);
            var recipe = Get(item);
            if (recipe == null || !Usable(recipe, allowedTarget))
            {
                plan.Missing[item] = wanted;
                return plan;
            }
            var produced = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var batches = (int)Math.Ceiling(wanted / (double)recipe.OutputCount);
            var ok = Craft(recipe, batches, pool, produced, plan, 1, maxDepth, allowedTarget, commit: true);
            if (!ok) return plan;
            Add(plan.Leftover, item, batches * recipe.OutputCount - wanted);
            foreach (var kv in produced.Where(kv => kv.Value > 0 && !kv.Key.Equals(item, StringComparison.OrdinalIgnoreCase)))
                Add(plan.Leftover, kv.Key, kv.Value);
            foreach (var kv in stock)
            {
                var used = kv.Value - (pool.TryGetValue(kv.Key, out var left) ? left : 0);
                if (used > 0) plan.Consumed[kv.Key] = used;
            }
            return plan;
        }

        static bool Usable(Recipe r, Func<string, bool> allowedTarget) =>
            r.Targets.Count > 0 && (allowedTarget == null || r.Targets.Any(allowedTarget));

        /// <summary>Runs a recipe <paramref name="batches"/> times against the pool (commit=false: dry run on copies).</summary>
        bool Craft(Recipe r, int batches, Dictionary<string, int> pool, Dictionary<string, int> produced, Plan plan,
            int depth, int maxDepth, Func<string, bool> allowedTarget, bool commit)
        {
            var allOk = true;
            foreach (var (input, count) in r.Inputs)
            {
                var need = count * batches;
                if (!Supply(input, need, pool, produced, plan, depth, maxDepth, allowedTarget)) allOk = false;
            }
            if (!allOk) return false;
            Add(plan.Batches, r.Name, batches);
            plan.TargetsOf[r.Name] = r.Targets;
            plan.CraftSeconds += r.CraftTime * batches;
            return true;
        }

        /// <summary>Takes <paramref name="need"/> of an item: produced surplus, then stock, then a recipe if it fully works out.</summary>
        bool Supply(string item, int need, Dictionary<string, int> pool, Dictionary<string, int> produced, Plan plan,
            int depth, int maxDepth, Func<string, bool> allowedTarget)
        {
            need -= Take(produced, item, need);
            need -= Take(pool, item, need);
            if (need <= 0) return true;

            var r = Get(item);
            if (r != null && depth <= maxDepth && Usable(r, allowedTarget))
            {
                var batches = (int)Math.Ceiling(need / (double)r.OutputCount);
                // dry run on copies: only commit when the whole sub-tree can be made
                var tryPool = new Dictionary<string, int>(pool, StringComparer.OrdinalIgnoreCase);
                var tryProduced = new Dictionary<string, int>(produced, StringComparer.OrdinalIgnoreCase);
                var tryPlan = new Plan();
                if (Craft(r, batches, tryPool, tryProduced, tryPlan, depth + 1, maxDepth, allowedTarget, commit: false))
                {
                    Craft(r, batches, pool, produced, plan, depth + 1, maxDepth, allowedTarget, commit: true);
                    Add(produced, item, batches * r.OutputCount - need);
                    return true;
                }
                // The sub-tree failed. If the container contributed to it (e.g. 200 of 250 iron ingots for steel
                // plates), the missing inputs below are the useful answer; if nothing in the container helped at all,
                // report this item itself (iron ingot, not the rock dust of RE2's ore recipe).
                if (UsedStock(pool, tryPool))
                {
                    foreach (var kv in tryPlan.Missing) Add(plan.Missing, kv.Key, kv.Value);
                    return false;
                }
            }
            Add(plan.Missing, item, need);
            return false;
        }

        /// <summary>Everything that goes back into the VC container: the wanted amount plus leftovers.</summary>
        public static Dictionary<string, int> Outputs(Plan plan)
        {
            var d = new Dictionary<string, int>(plan.Leftover, StringComparer.OrdinalIgnoreCase);
            Add(d, plan.Item, plan.Wanted);
            return d;
        }

        /// <summary>Stock for planning: only stacks without state (a loaded or worn weapon is never an ingredient).</summary>
        public static Dictionary<string, int> StockOf(IEnumerable<InvStack> stacks, Func<int, string> nameOf)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in stacks.Where(x => !x.HasState && x.Count > 0))
            {
                var n = nameOf(st.Id);
                if (n != null) Add(d, n, st.Count);
            }
            return d;
        }

        /// <summary>
        /// The VC container's contents after crafting: consumed items taken (stateless stacks only), outputs placed
        /// (merged first, then free slots). Error when the plan no longer fits the contents or the outputs don't fit.
        /// </summary>
        public static (List<InvStack> result, string error) Apply(List<InvStack> stacks, Plan plan, Func<string, int> idOf,
            int slots, Func<int, int> stackSize)
        {
            var plain = stacks.Where(x => !x.HasState).ToList();
            var withState = stacks.Where(x => x.HasState).ToList();
            foreach (var kv in plan.Consumed)
            {
                var id = idOf(kv.Key);
                if (id <= 0) return (null, $"Unknown item '{kv.Key}'.");
                if (Stacks.Total(Stacks.Take(plain, id, kv.Value)) < kv.Value)
                    return (null, "The container's contents changed while planning; try again.");
            }
            var result = plain.Concat(withState).ToList();
            var outputs = new List<InvStack>();
            foreach (var kv in Outputs(plan))
            {
                var id = idOf(kv.Key);
                if (id <= 0) return (null, $"Unknown item '{kv.Key}'.");
                outputs.Add(new InvStack(id, kv.Value));
            }
            var notPlaced = Stacks.Place(result, outputs, slots, stackSize);
            if (notPlaced.Count > 0)
                return (null, "The results don't fit into the container (not enough free slots); make room and try again.");
            return (result, null);
        }

        static bool UsedStock(Dictionary<string, int> before, Dictionary<string, int> after) =>
            before.Any(kv => kv.Value > 0 && after.TryGetValue(kv.Key, out var v) && v < kv.Value);

        static int Take(Dictionary<string, int> d, string key, int want)
        {
            if (want <= 0 || !d.TryGetValue(key, out var have) || have <= 0) return 0;
            var n = Math.Min(have, want);
            d[key] = have - n;
            return n;
        }

        static void Add(Dictionary<string, int> d, string key, int n)
        {
            if (n <= 0) return;
            d.TryGetValue(key, out var v);
            d[key] = v + n;
        }
    }
}
