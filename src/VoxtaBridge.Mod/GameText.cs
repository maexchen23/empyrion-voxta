using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>
    /// Human-readable item/block/token names and descriptions, read from the game's own files so it works
    /// for any scenario (vanilla, RE2, …): <c>Extras/Localization.csv</c> (English column) and
    /// <c>Content/Configuration/{ItemsConfig,BlocksConfig,TokenConfig}.ecf</c>. Base game files are loaded
    /// first, the active scenario's files override them.
    /// No game dependencies, so it can be unit-tested.
    /// </summary>
    public sealed class GameText
    {
        // Only a guard against runaway texts: real ones go up to ~1.8k chars (RE2 token guides) and the
        // character needs all of it (a cut-off guide looked like the whole text, 2026-11-17).
        public const int MaxDescription = 2000;

        readonly Dictionary<string, string> localization = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, Token> tokens = new Dictionary<int, Token>();

        /// <summary>Set while the scenario's files load; entries seen then are <see cref="Entry.InScenario"/>.</summary>
        bool scenarioPass;
        /// <summary>Tank accept lists of the tank blocks (BlocksConfig FuelAccept / O2Accept / WarpAccept), per kind
        /// "fuel" / "oxygen" / "pentaxid"; scenario lists only once the scenario defines any.</summary>
        readonly Dictionary<string, List<(List<string> items, bool scenario)>> tankAccepts =
            new Dictionary<string, List<(List<string>, bool)>>(StringComparer.OrdinalIgnoreCase);
        bool anyScenarioEntries;

        sealed class Entry
        {
            /// <summary>Defined by the scenario's own files (base-game entries a scenario dropped stay loaded but are not "in" it).</summary>
            public bool InScenario;
            public string Info;
            public string Ref;
            public string Class;
            public string StackSize;
            public string LootList;
            public string Volume;
            public string Kind;        // Item | Block
            public string WeaponItem;  // block: the weapon item it fires with
            public string ShowUser;
            public string TemplateRoot;
            public string MarketPrice;
            public string Category;
            /// <summary>Energy an item puts into a structure's fuel tank (FuelValue) / oxygen tank (O2Value).</summary>
            public string FuelValue;
            public string O2Value;
            /// <summary>AmmoType of the firing modes (weapon items; inside <c>{ Child … }</c>), null if none.</summary>
            public List<string> AmmoTypes;
            /// <summary>Magazine size per ammo type (AmmoCapacity of the same firing mode).</summary>
            public Dictionary<string, int> MagazineByAmmo;
            /// <summary>Ammo types whose firing mode has UseSingleMagazine: one item refills the whole magazine (energy weapons).</summary>
            public HashSet<string> SingleItemMagazine;
        }

        public sealed class Token
        {
            public string Name;
            public string Description;
        }

        public int LocalizationCount => localization.Count;
        public int EntryCount => entries.Count;
        public int TokenCount => tokens.Count;

        /// <summary>Loads base game files, then each scenario folder on top. Missing files are skipped.</summary>
        /// <param name="contentDir">The game's <c>Content</c> folder.</param>
        /// <param name="scenarioDir">The active scenario folder, or null for vanilla without a scenario.</param>
        public static GameText Load(string contentDir, string scenarioDir, Action<string> log)
        {
            var t = new GameText();
            if (!string.IsNullOrEmpty(contentDir))
                t.LoadFolder(Path.Combine(contentDir, "Extras"), Path.Combine(contentDir, "Configuration"), log);
            if (!string.IsNullOrEmpty(scenarioDir) && Directory.Exists(scenarioDir))
            {
                t.scenarioPass = true;
                t.LoadFolder(Path.Combine(scenarioDir, "Extras"), Path.Combine(scenarioDir, "Content", "Configuration"), log);
            }
            return t;
        }

        void LoadFolder(string extrasDir, string configDir, Action<string> log)
        {
            Try(Path.Combine(extrasDir, "Localization.csv"), r => LoadLocalizationCsv(r), log);
            foreach (var f in new[] { "ItemsConfig.ecf", "BlocksConfig.ecf", "TokenConfig.ecf" })
                Try(Path.Combine(configDir, f), r => LoadEcf(r), log);
        }

        static void Try(string path, Action<TextReader> load, Action<string> log)
        {
            try
            {
                if (!File.Exists(path)) return;
                using (var r = new StreamReader(path, Encoding.UTF8, true)) load(r);
                log?.Invoke($"loaded {path}");
            }
            catch (Exception e)
            {
                log?.Invoke($"could not read {path}: {e.Message}");
            }
        }

        static readonly Regex CustomScenario = new Regex(
            @"^\s*""?CustomScenario""?\s*:\s*""?(?<name>[^""\r\n#]*?)""?\s*,?\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Scenario name from the dedicated server config (<c>GameConfig: CustomScenario</c>, YAML or the
        /// JSON-style YAML AMP writes), or null for none.
        /// </summary>
        public static string ScenarioFromDedicatedConfig(string configText)
        {
            if (string.IsNullOrEmpty(configText)) return null;
            var m = CustomScenario.Match(configText);
            if (!m.Success) return null;
            var name = m.Groups["name"].Value.Trim().Trim('\'').Trim();
            return name.Length > 0 ? name : null;
        }

        /// <summary>
        /// The server config file from the process command line: playfield servers get <c>configFile=x.yaml</c>,
        /// the dedicated server gets <c>-dedicated x.yaml</c>. Null if neither is there.
        /// </summary>
        public static string ConfigFileFromArgs(IEnumerable<string> args)
        {
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i].Trim('"');
                if (t.StartsWith("configFile=", StringComparison.OrdinalIgnoreCase))
                    return t.Substring("configFile=".Length).Trim('"');
                if (t.Equals("-dedicated", StringComparison.OrdinalIgnoreCase) && i + 1 < list.Count
                    && !list[i + 1].StartsWith("-"))
                    return list[i + 1].Trim('"');
            }
            return null;
        }

        // ----- lookups ---------------------------------------------------------------------------

        /// <summary>MarketPrice (credits per unit, ItemsConfig/BlocksConfig, inherited), or null.</summary>
        public double? MarketPrice(string internalName) =>
            double.TryParse(Resolve(internalName, e => e.MarketPrice, 0, inherit: true), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : (double?)null;

        /// <summary>FuelValue (fuel tank units per item, inherited), or 0.</summary>
        public int FuelValue(string internalName) =>
            int.TryParse(Resolve(internalName, e => e.FuelValue, 0, inherit: true), out var v) && v > 0 ? v : 0;

        /// <summary>O2Value (oxygen tank units per item, inherited), or 0.</summary>
        public int O2Value(string internalName) =>
            int.TryParse(Resolve(internalName, e => e.O2Value, 0, inherit: true), out var v) && v > 0 ? v : 0;

        /// <summary>Category (ItemsConfig/BlocksConfig: Food, Components, Devices, …, inherited), or null.</summary>
        public string Category(string internalName) => Resolve(internalName, e => e.Category, 0, inherit: true);

        /// <summary>English display name for an internal item/block name, or null.</summary>
        public string DisplayName(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return null;
            if (localization.TryGetValue(internalName, out var s) && s.Length > 0) return s;
            // Variants without their own row use their parent's name (e.g. "PistolT2, Ref: Pistol" has one,
            // but not every variant does).
            var parent = Resolve(internalName, e => e.Ref, 0);
            return parent != null && localization.TryGetValue(parent, out var p) && p.Length > 0 ? p : null;
        }

        /// <summary>English description (via the item's <c>Info:</c> key), or null.</summary>
        public string Description(string internalName)
        {
            var info = Resolve(internalName, e => e.Info, 0, inherit: true);
            if (info == null || !localization.TryGetValue(info, out var s) || s.Length == 0) return null;
            return Shorten(s, MaxDescription);
        }

        /// <summary>True for token items (their real identity is the token id stored in the stack).</summary>
        public bool IsToken(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return false;
            var cls = Resolve(internalName, e => e.Class, 0, inherit: true);
            return string.Equals(cls, "Token", StringComparison.OrdinalIgnoreCase)
                || string.Equals(internalName, "Token", StringComparison.OrdinalIgnoreCase);
        }

        public Token GetToken(int id) => tokens.TryGetValue(id, out var t) ? t : null;

        /// <summary>Max stack size from the item config (inherited via Ref), else 999.</summary>
        public int StackSize(string internalName)
        {
            var v = Resolve(internalName, e => e.StackSize, 0, inherit: true);
            return int.TryParse(v, out var n) && n > 0 ? n : Stacks.DefaultStackSize;
        }

        /// <summary>Volume of one item in litres from the item/block config (inherited via Ref), or 0 if unknown.</summary>
        public double Volume(string internalName)
        {
            var v = Resolve(internalName, e => e.Volume, 0, inherit: true);
            return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 0;
        }

        /// <summary>
        /// Slot count of a container block. The game maps the block's LootList to a slot count internally; this is the
        /// same table EmpyrionScripting uses (48/66/300 → 128, 67 → 64), default 64.
        /// </summary>
        public int ContainerSlots(string blockName)
        {
            var v = Resolve(blockName, e => e.LootList, 0, inherit: true);
            if (!int.TryParse(v, out var loot)) return 64;
            switch (loot) { case 48: case 66: case 300: return 128; default: return 64; }
        }

        // ----- weapons and ammo --------------------------------------------------------------------

        /// <summary>
        /// Ammo a hand weapon or a weapon block uses (internal names; blocks via their <c>WeaponItem</c>; inherited via
        /// <c>Ref:</c>). Empty for everything else.
        /// </summary>
        public IReadOnlyList<string> AmmoOf(string internalName)
        {
            var list = AmmoList(internalName, 0);
            if (list != null) return list;
            var weapon = Resolve(internalName, e => e.WeaponItem, 0, inherit: true);
            return (weapon != null ? AmmoList(weapon, 0) : null) ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>Magazine size of a weapon for one of its ammo types (the firing mode's AmmoCapacity), or 0.</summary>
        public int MagazineOf(string weapon, string ammo) => Magazine(weapon, ammo).shots;

        /// <summary>
        /// Magazine of a weapon for one ammo type: shots it holds, and whether one ammo item refills it completely
        /// (UseSingleMagazine, e.g. energy weapons: Laser Minigun 250 shots from 1 Pulse Laser Charge).
        /// </summary>
        public (int shots, bool singleItem) Magazine(string weapon, string ammo)
        {
            (int, bool) Find(string name, int depth)
            {
                if (name == null || depth > 8 || !entries.TryGetValue(name, out var e)) return (0, false);
                if (e.AmmoTypes != null && e.AmmoTypes.Count > 0)
                    return (e.MagazineByAmmo != null && e.MagazineByAmmo.TryGetValue(ammo, out var n) ? n : 0,
                            e.SingleItemMagazine != null && e.SingleItemMagazine.Contains(ammo));
                return Find(e.Ref, depth + 1);
            }
            var direct = Find(weapon, 0);
            if (direct.Item1 > 0) return direct;
            var item = Resolve(weapon, e => e.WeaponItem, 0, inherit: true);
            return item != null ? Find(item, 0) : (0, false);
        }

        List<string> AmmoList(string name, int depth)
        {
            if (name == null || depth > 8 || !entries.TryGetValue(name, out var e)) return null;
            if (e.AmmoTypes != null && e.AmmoTypes.Count > 0) return e.AmmoTypes;
            return e.Ref != null ? AmmoList(e.Ref, depth + 1) : null;
        }

        /// <summary>
        /// Exists for the player in the active game: defined by the scenario's own files (when a scenario is active),
        /// not hidden (<c>ShowUser: No</c>), and has a display name.
        /// </summary>
        public bool IsPlayerFacing(string internalName)
        {
            if (internalName == null || !entries.TryGetValue(internalName, out var e)) return false;
            if (anyScenarioEntries && !e.InScenario) return false;
            if (string.Equals(Resolve(internalName, x => x.ShowUser, 0, inherit: true), "No", StringComparison.OrdinalIgnoreCase)) return false;
            return localization.ContainsKey(internalName) || (e.Ref != null && localization.ContainsKey(e.Ref));
        }

        /// <summary>All item/block names from the config files.</summary>
        public IEnumerable<string> AllNames => entries.Keys;

        /// <summary>Recipe name of a block (its <c>TemplateRoot</c>, inherited), else the name itself.</summary>
        public string RecipeName(string internalName) =>
            Resolve(internalName, e => e.TemplateRoot, 0, inherit: true) ?? internalName;

        Dictionary<string, List<string>> usersByAmmo;

        /// <summary>
        /// Player-facing weapons (hand weapons and weapon blocks) that fire this ammo, as internal names. Turret-internal
        /// weapon items are represented by their block; blocks with <c>ShowUser: No</c> and things without a display
        /// name (enemy/POI/drone weapons mostly) are left out.
        /// </summary>
        public IReadOnlyList<string> UsersOf(string ammoName)
        {
            if (ammoName == null) return Array.Empty<string>();
            lock (entries)
            if (usersByAmmo == null)
            {
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var internalWeapons = new HashSet<string>(entries.Values.Where(e => e.WeaponItem != null).Select(e => e.WeaponItem), StringComparer.OrdinalIgnoreCase);
                foreach (var kv in entries)
                {
                    var name = kv.Key;
                    if (internalWeapons.Contains(name)) continue;
                    if (anyScenarioEntries && !kv.Value.InScenario) continue; // e.g. TurretSVSmall: base game only, not in RE2
                    if (string.Equals(Resolve(name, e => e.ShowUser, 0, inherit: true), "No", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!localization.ContainsKey(name) && !(kv.Value.Ref != null && localization.ContainsKey(kv.Value.Ref))) continue;
                    foreach (var ammo in AmmoOf(name))
                    {
                        if (!map.TryGetValue(ammo, out var l)) map[ammo] = l = new List<string>();
                        l.Add(name);
                    }
                }
                usersByAmmo = map;
            }
            return usersByAmmo.TryGetValue(ammoName, out var users) ? users : (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>Follows <c>Ref:</c> parents (depth-limited) until <paramref name="field"/> is set.</summary>
        string Resolve(string name, Func<Entry, string> field, int depth, bool inherit = false)
        {
            if (name == null || depth > 8 || !entries.TryGetValue(name, out var e)) return null;
            if (!inherit) return e.Ref; // used for the parent name lookup
            var v = field(e);
            if (!string.IsNullOrEmpty(v)) return v;
            return e.Ref != null ? Resolve(e.Ref, field, depth + 1, true) : null;
        }

        // ----- Localization.csv ------------------------------------------------------------------

        /// <summary>Key in column 0, English in column 1. Quoted fields may contain commas, "" and line breaks.</summary>
        public void LoadLocalizationCsv(TextReader reader)
        {
            foreach (var row in ReadCsv(reader))
            {
                if (row.Count < 2) continue;
                var key = row[0].Trim();
                if (key.Length == 0 || key == "KEY") continue;
                var text = Clean(row[1]);
                if (text.Length > 0) localization[key] = text;
            }
        }

        public static IEnumerable<List<string>> ReadCsv(TextReader reader)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            int c;
            while ((c = reader.Read()) != -1)
            {
                var ch = (char)c;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                        else quoted = false;
                    }
                    else field.Append(ch);
                    continue;
                }
                switch (ch)
                {
                    case '"' when field.Length == 0: quoted = true; break;
                    case ',': row.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n':
                        row.Add(field.ToString()); field.Clear();
                        yield return row;
                        row = new List<string>();
                        break;
                    default: field.Append(ch); break;
                }
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                yield return row;
            }
        }

        // ----- ECF -------------------------------------------------------------------------------

        static readonly Regex Header = new Regex(
            // the Id is optional: newer configs (RE2: ~1500 blocks, e.g. the Prototype Micro Jump Drives) write "{ Block Name: X"
            @"^\{\s*\+?(?<kind>Item|Block|Token)\s+(?:Id:\s*(?<id>\d+)\s*,\s*)?Name:\s*(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static readonly Regex RefPart = new Regex(@"^(?<name>.*?)\s*,\s*Ref:\s*(?<ref>[^,\s]+)", RegexOptions.Compiled);

        /// <summary>
        /// Reads top-level <c>{ Item|Block|Token Id: n, Name: x[, Ref: y]</c> entries and their
        /// <c>Info:</c>, <c>Class:</c> and (tokens) <c>Description:</c> lines. Nested <c>{ Child …</c> blocks are skipped.
        /// </summary>
        public void LoadEcf(TextReader reader)
        {
            string line;
            int depth = 0;
            Entry current = null;
            Token currentToken = null;
            var ammoReset = false;
            string childAmmo = null;
            var childCapacity = 0;
            var childSingle = false;

            while ((line = reader.ReadLine()) != null)
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] == '#') continue;

                if (t[0] == '{')
                {
                    depth++;
                    if (depth != 1) continue;
                    ammoReset = false;
                    current = null;
                    currentToken = null;
                    var m = Header.Match(t);
                    if (!m.Success) continue;

                    var rest = m.Groups["rest"].Value;
                    var hash = rest.IndexOf('#'); // "{ Block Id: 1147, Name: TurretBaseMinigun   #TurretPlayer BA Minigun"
                    if (hash >= 0) rest = rest.Substring(0, hash);
                    rest = rest.Trim();
                    if (m.Groups["kind"].Value.Equals("Token", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!m.Groups["id"].Success) continue; // tokens are looked up by id
                        currentToken = new Token { Name = Clean(rest) };
                        tokens[int.Parse(m.Groups["id"].Value)] = currentToken;
                        continue;
                    }

                    string name = rest, parent = null;
                    var r = RefPart.Match(rest);
                    if (r.Success) { name = r.Groups["name"].Value.Trim(); parent = r.Groups["ref"].Value.Trim(); }
                    else
                    {
                        var comma = rest.IndexOf(',');
                        if (comma >= 0) name = rest.Substring(0, comma).Trim();
                    }
                    if (name.Length == 0) continue;

                    // A later file (the scenario) replaces the entry, but keeps what it does not set.
                    if (!entries.TryGetValue(name, out current)) entries[name] = current = new Entry();
                    if (parent != null) current.Ref = parent;
                    current.Kind = m.Groups["kind"].Value;
                    if (scenarioPass) { current.InScenario = true; anyScenarioEntries = true; }
                    continue;
                }

                if (t[0] == '}')
                {
                    if (depth >= 2) { childAmmo = null; childCapacity = 0; childSingle = false; } // end of a firing mode block
                    depth = Math.Max(0, depth - 1);
                    if (depth == 0) { current = null; currentToken = null; }
                    continue;
                }

                // firing modes live in nested { Child n } blocks
                if (current != null && depth >= 2)
                {
                    var ammo = Value(t, "AmmoType:");
                    if (ammo != null)
                    {
                        // a later definition (the scenario) replaces the ammo list as a whole
                        if (!ammoReset || current.AmmoTypes == null)
                        {
                            current.AmmoTypes = new List<string>();
                            current.MagazineByAmmo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            current.SingleItemMagazine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            ammoReset = true;
                        }
                        if (!current.AmmoTypes.Contains(ammo, StringComparer.OrdinalIgnoreCase)) current.AmmoTypes.Add(ammo);
                        childAmmo = ammo;
                    }
                    var cap = Value(t, "AmmoCapacity:");
                    if (cap != null && int.TryParse(cap, out var capN)) childCapacity = capN;
                    if (string.Equals(Value(t, "UseSingleMagazine:"), "true", StringComparison.OrdinalIgnoreCase)) childSingle = true;
                    if (childAmmo != null && childSingle && current.SingleItemMagazine != null) current.SingleItemMagazine.Add(childAmmo);
                    // AmmoType and AmmoCapacity of one firing mode may come in any order: pair them when both are known
                    if (childAmmo != null && childCapacity > 0 && current.MagazineByAmmo != null && !current.MagazineByAmmo.ContainsKey(childAmmo))
                        current.MagazineByAmmo[childAmmo] = childCapacity;
                    continue;
                }
                if (depth != 1) continue;

                if (currentToken != null && t.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                {
                    currentToken.Description = Shorten(Clean(t.Substring("Description:".Length)), MaxDescription);
                }
                else if (current != null)
                {
                    var info = Value(t, "Info:");
                    if (info != null) current.Info = info;
                    var cls = Value(t, "Class:");
                    if (cls != null) current.Class = cls;
                    var stack = Value(t, "StackSize:");
                    if (stack != null) current.StackSize = stack;
                    var vol = Value(t, "Volume:");
                    if (vol != null) current.Volume = vol;
                    var weapon = Value(t, "WeaponItem:");
                    if (weapon != null) current.WeaponItem = weapon;
                    var root = Value(t, "TemplateRoot:");
                    if (root != null) current.TemplateRoot = root;
                    var show = Value(t, "ShowUser:");
                    if (show != null) current.ShowUser = show;
                    var category = Value(t, "Category:");
                    if (category != null) current.Category = category;
                    foreach (var (key, kind) in new[] { ("FuelAccept:", "fuel"), ("O2Accept:", "oxygen"), ("WarpAccept:", "pentaxid") })
                    {
                        if (!t.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                        var list = AcceptList(t.Substring(key.Length));
                        if (list.Count == 0) continue;
                        if (!tankAccepts.TryGetValue(kind, out var lists)) tankAccepts[kind] = lists = new List<(List<string>, bool)>();
                        lists.Add((list, scenarioPass));
                    }
                    var fuel = Value(t, "FuelValue:");
                    if (fuel != null) current.FuelValue = fuel;
                    var o2 = Value(t, "O2Value:");
                    if (o2 != null) current.O2Value = o2;
                    var market = Value(t, "MarketPrice:");
                    if (market != null) current.MarketPrice = market;
                    var loot = Value(t, "LootList:");
                    if (loot != null) current.LootList = loot.Split(' ', '#')[0];
                }
            }
        }

        /// <summary><c>"A, B,C"</c> or <c>A</c> (up to the next comma) → item names.</summary>
        static List<string> AcceptList(string v)
        {
            v = v.Trim();
            if (v.StartsWith("\""))
            {
                var end = v.IndexOf('"', 1);
                v = end > 0 ? v.Substring(1, end - 1) : v.Substring(1);
            }
            else
            {
                var comma = v.IndexOf(',');
                if (comma >= 0) v = v.Substring(0, comma);
            }
            return v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        IEnumerable<List<string>> AcceptLists(string kind)
        {
            if (!tankAccepts.TryGetValue(kind, out var lists)) return Enumerable.Empty<List<string>>();
            var scen = lists.Where(x => x.scenario).Select(x => x.items).ToList();
            return scen.Count > 0 ? scen : lists.Select(x => x.items).ToList();
        }

        /// <summary>Items any tank of this kind ("fuel", "oxygen", "pentaxid") accepts.</summary>
        public IReadOnlyCollection<string> TankAccepts(string kind) =>
            new HashSet<string>(AcceptLists(kind).SelectMany(x => x), StringComparer.OrdinalIgnoreCase);

        /// <summary>The item the game fills when content is withdrawn: the first item of the most common accept list.</summary>
        public string TankDrainItem(string kind) =>
            AcceptLists(kind).GroupBy(x => x[0], StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        /// <summary>Tank units one item adds: FuelValue / O2Value; pentaxid tanks count crystals (1 each).</summary>
        public int TankValue(string kind, string internalName)
        {
            switch (kind)
            {
                case "fuel": return FuelValue(internalName);
                case "oxygen": return O2Value(internalName);
                case "pentaxid": return TankAccepts("pentaxid").Contains(internalName) ? 1 : 0;
            }
            return 0;
        }

        /// <summary>Value of <c>Key: value, other: x</c> when the line starts with the key.</summary>
        static string Value(string line, string key)
        {
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return null;
            var v = line.Substring(key.Length);
            var comma = v.IndexOf(',');
            if (comma >= 0) v = v.Substring(0, comma);
            v = v.Trim().Trim('"');
            return v.Length > 0 ? v : null;
        }

        // ----- text cleanup ----------------------------------------------------------------------

        static readonly Regex Markup = new Regex(@"\[(?:/?[a-z]{1,4}|-|[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Spaces = new Regex(@"\s{2,}", RegexOptions.Compiled);
        static readonly Regex EmptyBullets = new Regex(@"(?:\s-){2,}\s", RegexOptions.Compiled);

        /// <summary>Removes colour/format tags (<c>[c][37D2FF]…[-][/c]</c>, <c>[b]</c>, …) and literal <c>\n</c>.</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            s = Markup.Replace(s, "");
            s = Spaces.Replace(s, " ");
            s = EmptyBullets.Replace(s, " "); // "\n-\n" spacer lines become "- -"
            return s.Trim();
        }

        static string Shorten(string s, int max)
        {
            if (s.Length <= max) return s;
            var cut = s.LastIndexOf(". ", max, StringComparison.Ordinal);
            return cut > max / 2 ? s.Substring(0, cut + 1) : s.Substring(0, max).TrimEnd() + "…";
        }
    }
}
