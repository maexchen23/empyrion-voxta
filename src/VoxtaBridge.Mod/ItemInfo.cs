using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// Item search (POST /items): game names, whether and where an item can be crafted, and the weapon ↔ ammo links
    /// (ammo: which weapons fire it; weapon: which ammo it takes). Runs on the dedi from the game's config files, no
    /// player or playfield needed. Also the "did you mean" suggestions for crafting.
    /// </summary>
    public partial class BridgeMod
    {
        /// <summary>More matches than this: names only, ask for a narrower search.</summary>
        const int MaxDetailedItems = 5;
        const int MaxListedNames = 40;
        const int MaxUsers = 15;

        /// <summary>All known items/blocks the player can meet: (internal, display).</summary>
        static List<(string internalName, string display)> SearchCandidates(Func<string, bool> filter = null)
        {
            var t = gameText;
            // the game's id mapping reaches the dedi only once a playfield server runs; the config files are always there
            ItemName(0);
            List<string> names;
            lock (itemNames) names = itemNames.Values.ToList();
            if (t != null) names.AddRange(t.AllNames);
            names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return names
                .Where(n => t == null || t.IsPlayerFacing(n))
                .Where(n => filter == null || filter(n))
                .Select(n => (n, t?.DisplayName(n) ?? n))
                .ToList();
        }

        static Recipe CraftRecipe(string internalName)
        {
            var r = recipes;
            if (r == null) return null;
            var rec = r.Get(internalName) ?? r.Get(gameText?.RecipeName(internalName));
            return rec != null && rec.Targets.Count > 0 ? rec : null;
        }

        /// <summary>{ makes: n, seconds: s, inputs: [{name, count}] } for one craft.</summary>
        static void RecipeJson(Json j, Recipe rec)
        {
            j.Key("recipe").Obj().Prop("makes", rec.OutputCount).Prop("seconds", Math.Round(rec.CraftTime, 1));
            j.Key("inputs").Arr();
            foreach (var (item, count) in rec.Inputs)
                j.Obj().Prop("name", gameText?.DisplayName(item) ?? item).Prop("count", count).EndObj();
            j.EndArr().EndObj();
        }

        /// <summary>"Did you mean …" for a crafting request: close names among craftable items.</summary>
        static List<string> CraftSuggestions(string query) =>
            ItemSearch.Search(query, SearchCandidates(n => CraftRecipe(n) != null)).Take(5).Select(h => h.Display).ToList();

        /// <summary>HTTP thread: POST /items (query=…).</summary>
        static (int, string) HandleItems(string body)
        {
            var q = Form.Parse(body).Get("query");
            if (string.IsNullOrWhiteSpace(q)) return (400, Error("'query' is required."));
            var t = gameText;
            if (t == null) return (503, Error("The game's item data is not loaded yet."));

            var hits = ItemSearch.Search(q.Trim(), SearchCandidates());
            var j = new Json().Obj().Prop("ok", true).Prop("query", q.Trim()).Prop("count", hits.Count);
            if (hits.Count > MaxDetailedItems)
            {
                j.Prop("namesOnly", true);
                j.Key("names").Arr();
                foreach (var h in hits.Take(MaxListedNames)) j.Val(h.Display);
                j.EndArr();
                return (200, j.EndObj().ToString());
            }

            j.Key("items").Arr();
            foreach (var h in hits)
            {
                j.Obj().Prop("name", h.Display);
                var rec = CraftRecipe(h.Internal);
                j.Prop("craftable", rec != null);
                if (rec != null)
                {
                    j.Key("constructors").Arr();
                    foreach (var c in rec.Targets.Select(ConstructorLabel).Distinct()) j.Val(c);
                    j.EndArr();
                    RecipeJson(j, rec);
                }
                var ammo = t.AmmoOf(h.Internal);
                if (ammo.Count > 0)
                {
                    j.Key("ammo").Arr();
                    // magazine size with the ammo, so "give me 10 mags" works; nothing else from the firing modes
                    foreach (var a in ammo)
                    {
                        var (mag, single) = t.Magazine(h.Internal, a);
                        var info = mag <= 0 ? "" : single ? $" (1 per magazine, {mag} shots)" : $" (magazine {mag})";
                        j.Val((t.DisplayName(a) ?? a) + info);
                    }
                    j.EndArr();
                }
                var users = t.UsersOf(h.Internal).Select(u => t.DisplayName(u) ?? u).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (users.Count > 0)
                {
                    j.Key("usedBy").Arr();
                    foreach (var u in users.Take(MaxUsers)) j.Val(u);
                    j.EndArr();
                    if (users.Count > MaxUsers) j.Prop("moreUsers", users.Count - MaxUsers);
                }
                j.EndObj();
            }
            j.EndArr();
            return (200, j.EndObj().ToString());
        }
    }
}
