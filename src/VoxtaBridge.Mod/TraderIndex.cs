using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>One item line of a trader in TraderNPCConfig.ecf: "Name, sell price, stock, buy price, buy demand".</summary>
    public sealed class TraderOffer
    {
        public string Trader, Item, SellPrice, BuyPrice;
        public int StockMin, StockMax;
        public bool Sells => StockMax > 0 && !IsZero(SellPrice);
        public bool Buys => !IsZero(BuyPrice);
        static bool IsZero(string s) => string.IsNullOrWhiteSpace(s) || s.Trim() == "0";
    }

    /// <summary>A POI group the scenario spawns on a playfield type, with its faction (playfield yaml).</summary>
    public sealed class PoiSpawn
    {
        public string Group, Faction, Playfield, Kind;
        /// <summary>Rough chance that a generated playfield of this template has the group: CountMinMax starting at 0 counts half, times Probability.</summary>
        public double Chance = 1;
        /// <summary>FactionTerritory of the entry: null = none given; otherwise the listed names ("All", "True", factions, Eden_*Territory).</summary>
        public List<string> Territory;
    }

    /// <summary>A prefab that contains traders: its POI group (header property 7) and display name (property 16).</summary>
    public sealed class PrefabTraders
    {
        public string Prefab, Group, Display;
        public List<string> Traders = new List<string>();
    }

    public sealed class FactionDef
    {
        public int Id;
        public string Name, Abbrev;
        public bool Reputation;
        public string Label => string.IsNullOrEmpty(Abbrev) ? Name : Abbrev;
    }

    /// <summary>
    /// Who sells what and where (game-free, unit-tested): items → traders (TraderNPCConfig.ecf) → POI prefabs that contain
    /// them (Prefabs/*.epb) → the playfield types that spawn those POI groups (Playfields/*/*.yaml), plus factions,
    /// starting standings (DefReputation.ecf) and the live standings from the save's game.dat.
    /// </summary>
    public sealed class TraderIndex
    {
        readonly Dictionary<string, List<TraderOffer>> offersByItem = new Dictionary<string, List<TraderOffer>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<TraderOffer>> offersByTrader = new Dictionary<string, List<TraderOffer>>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> traderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PrefabTraders> prefabs = new Dictionary<string, PrefabTraders>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<PrefabTraders>> prefabsByTrader = new Dictionary<string, List<PrefabTraders>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HashSet<string>> foldersByPlanetType = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> planetTypeByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The PlanetType a playfield folder declares ("EdenSpace_POI_ExploreT1_1" → "Exploration Site Class 1"), or null.</summary>
        public string PlanetTypeOf(string folder) => folder != null && planetTypeByFolder.TryGetValue(folder, out var t) ? t : null;
        readonly Dictionary<string, List<PoiSpawn>> spawnsByGroup = new Dictionary<string, List<PoiSpawn>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, FactionDef> factionsByName = new Dictionary<string, FactionDef>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, FactionDef> factionsById = new Dictionary<int, FactionDef>();
        readonly Dictionary<string, int> startStanding = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int TraderCount => traderNames.Count;
        public int PrefabCount => prefabs.Count;
        public int GroupCount => spawnsByGroup.Count;
        public int FactionCount => factionsById.Count;
        public IEnumerable<string> SoldItems => offersByItem.Where(kv => kv.Value.Any(o => o.Sells)).Select(kv => kv.Key);
        public bool IsTrader(string name) => name != null && traderNames.Contains(name);

        // ----- TraderNPCConfig.ecf -------------------------------------------------------------

        static readonly Regex TraderHeader = new Regex(@"^\{\s*\+?\s*Trader\s+Name:\s*(?<name>.+?)\s*$", RegexOptions.IgnoreCase);
        static readonly Regex ItemLine = new Regex(@"^Item\d+\s*:\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase);

        public void LoadTraderConfig(TextReader reader)
        {
            string line, trader = null;
            while ((line = reader.ReadLine()) != null)
            {
                var t = StripComment(line).Trim();
                if (t.Length == 0) continue;
                var h = TraderHeader.Match(t);
                if (h.Success)
                {
                    trader = h.Groups["name"].Value.Trim().Trim('"');
                    traderNames.Add(trader);
                    continue;
                }
                if (t.StartsWith("}")) { trader = null; continue; }
                if (trader == null) continue;
                var m = ItemLine.Match(t);
                if (!m.Success) continue;
                var parts = m.Groups["v"].Value.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length < 3 || parts[0].Length == 0) continue;
                var o = new TraderOffer { Trader = trader, Item = parts[0], SellPrice = parts[1], BuyPrice = parts.Length > 3 ? parts[3] : "0" };
                ParseRange(parts[2], out o.StockMin, out o.StockMax);
                if (!offersByItem.TryGetValue(o.Item, out var list)) offersByItem[o.Item] = list = new List<TraderOffer>();
                list.Add(o);
                if (!offersByTrader.TryGetValue(trader, out var byTrader)) offersByTrader[trader] = byTrader = new List<TraderOffer>();
                byTrader.Add(o);
            }
        }

        static void ParseRange(string s, out int min, out int max)
        {
            min = max = 0;
            var p = s.Split('-');
            int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out min);
            if (p.Length > 1) int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out max); else max = min;
        }

        /// <summary>Traders that sell this item (internal name).</summary>
        public List<TraderOffer> Sellers(string item) =>
            item != null && offersByItem.TryGetValue(item, out var l) ? l.Where(o => o.Sells).ToList() : new List<TraderOffer>();

        /// <summary>Everything one trader has in its list (sells and/or buys).</summary>
        public List<TraderOffer> Offers(string trader) =>
            trader != null && offersByTrader.TryGetValue(trader, out var l) ? l : new List<TraderOffer>();

        public List<TraderOffer> Buyers(string item) =>
            item != null && offersByItem.TryGetValue(item, out var l) ? l.Where(o => o.Buys).ToList() : new List<TraderOffer>();

        // ----- prefabs -----------------------------------------------------------------------

        /// <summary>Reads one .epb: header properties, then the zipped block/entity data for trader spawners. Needs the trader config first.</summary>
        readonly Dictionary<string, PoiFacts> prefabFacts = new Dictionary<string, PoiFacts>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Header and block-data facts of every prefab read (for the POI classification); null if unknown.</summary>
        public PoiFacts Facts(string prefab) => prefab != null && prefabFacts.TryGetValue(prefab, out var f) ? f : null;
        public int FactsCount => prefabFacts.Count;

        static readonly Regex TurretName = new Regex(@"(?:Turret|SentryGun)[A-Za-z0-9]*", RegexOptions.Compiled);
        static readonly Regex PoiDialogueName = new Regex(@"POI_[A-Za-z0-9_]+?_Init", RegexOptions.Compiled);
        Regex watchRegex;

        /// <summary>
        /// Dialogue names to look for in the prefabs (set before adding prefabs); each prefab that uses one lists it in
        /// <see cref="PoiFacts.WatchedDialogues"/>. Used for the dialogues that lead to a mining contract offer.
        /// </summary>
        public IEnumerable<string> WatchDialogues
        {
            set
            {
                var names = value?.Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderByDescending(n => n.Length).ToList();
                watchRegex = names == null || names.Count == 0 ? null
                    : new Regex(@"(?<![A-Za-z0-9_])(?:" + string.Join("|", names.Select(Regex.Escape)) + @")(?![A-Za-z0-9_])", RegexOptions.Compiled);
            }
        }

        /// <summary>Prefabs whose text uses one of the watched dialogues.</summary>
        public IEnumerable<string> PrefabsWithWatchedDialogues =>
            prefabFacts.Values.Where(f => f.WatchedDialogues.Count > 0).Select(f => f.Prefab);

        static readonly Regex TradeDialogueName = new Regex(@"TD_[A-Za-z0-9]+_Start", RegexOptions.Compiled);

        /// <summary>Facts from a prefab's header alone (cheap; no traders or dialogues): group, name, tag, size in blocks, turret types.</summary>
        public static PoiFacts HeaderFacts(string prefabName, byte[] data, int headerLen = -1)
        {
            if (headerLen < 0) { var z = IndexOf(data, ZipMagic, 0); headerLen = z < 0 ? data.Length : z; }
            var facts = new PoiFacts
            {
                Prefab = prefabName, Group = HeaderString(data, headerLen, 7), Display = HeaderString(data, headerLen, 16), Tag = HeaderString(data, headerLen, 21),
            };
            if (headerLen >= 21)
            {
                facts.SizeX = BitConverter.ToInt32(data, 9);
                facts.SizeY = BitConverter.ToInt32(data, 13);
                facts.SizeZ = BitConverter.ToInt32(data, 17);
            }
            var headerText = Encoding.GetEncoding(28591).GetString(data, 0, headerLen);
            facts.TurretTypes = TurretName.Matches(headerText).Cast<Match>().Select(m => m.Value).Distinct().Count();
            facts.Turrets = DeviceGroupCount(data, headerLen, "cpgTurret");
            facts.Sentries = Math.Max(0, DeviceGroupCount(data, headerLen, "cpgSentry"));
            var dialogue = PoiDialogueName.Match(headerText);
            if (dialogue.Success) facts.PoiDialogue = dialogue.Value;
            facts.HasGin = headerText.IndexOf("GIN_Console", StringComparison.Ordinal) >= 0;
            return facts;
        }

        public PrefabTraders AddPrefab(string prefabName, byte[] data)
        {
            var zipAt = IndexOf(data, ZipMagic, 0);
            var headerLen = zipAt < 0 ? data.Length : zipAt;
            var group = HeaderString(data, headerLen, 7);
            var display = HeaderString(data, headerLen, 16);
            var facts = HeaderFacts(prefabName, data, headerLen);
            void ScanText(string text)
            {
                if (facts.PoiDialogue == null) { var m = PoiDialogueName.Match(text); if (m.Success) facts.PoiDialogue = m.Value; }
                if (!facts.HasGin && text.IndexOf("GIN_Console", StringComparison.Ordinal) >= 0) facts.HasGin = true;
                if (watchRegex != null)
                    foreach (Match m in watchRegex.Matches(text))
                        if (!facts.WatchedDialogues.Contains(m.Value)) facts.WatchedDialogues.Add(m.Value);
                if (text.IndexOf("TD_", StringComparison.Ordinal) >= 0)
                    foreach (Match m in TradeDialogueName.Matches(text))
                        if (!facts.TradeDialogues.Contains(m.Value)) facts.TradeDialogues.Add(m.Value);
            }
            ScanText(Encoding.GetEncoding(28591).GetString(data, 0, headerLen));
            prefabFacts[prefabName] = facts;
            if (zipAt < 0) return null;

            var traders = new List<string>();
            try
            {
                using (var zip = new ZipArchive(new MemoryStream(data, zipAt, data.Length - zipAt, false), ZipArchiveMode.Read))
                    foreach (var e in zip.Entries)
                    {
                        byte[] blob;
                        using (var s = e.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            blob = ms.ToArray();
                        }
                        foreach (var type in EntityTypes(blob))
                            if (traderNames.Contains(type) && !traders.Contains(type, StringComparer.OrdinalIgnoreCase)) traders.Add(type);
                        ScanText(Encoding.GetEncoding(28591).GetString(blob));
                    }
            }
            catch (InvalidDataException) { return null; }

            facts.Traders = traders.Count;
            if (traders.Count == 0) return null;
            var p = new PrefabTraders { Prefab = prefabName, Group = group, Display = display, Traders = traders };
            prefabs[prefabName] = p;
            foreach (var t in traders)
            {
                if (!prefabsByTrader.TryGetValue(t, out var l)) prefabsByTrader[t] = l = new List<PrefabTraders>();
                l.Add(p);
            }
            return p;
        }

        static readonly byte[] ZipMagic = { 0x50, 0x4b, 0x03, 0x04 };
        static readonly byte[] TypeKey = { 0x01, 0x04, (byte)'T', (byte)'y', (byte)'p', (byte)'e' };

        /// <summary>
        /// Control panel device group in the prefab header: [len]["cpgTurret"] 01 01 ff [uint16 count] then count × 4-byte
        /// block positions (seen 2026-11-18). Sum over all groups of that name; -1 if there is none.
        /// </summary>
        public static int DeviceGroupCount(byte[] d, int headerLen, string group)
        {
            var name = Encoding.ASCII.GetBytes(group);
            int total = -1, at = 0;
            while ((at = IndexOf(d, name, at)) >= 0 && at < headerLen)
            {
                var start = at;
                at += name.Length;
                if (start == 0 || d[start - 1] != name.Length) continue;
                if (at + 5 > headerLen || d[at] != 1 || d[at + 1] != 1 || d[at + 2] != 0xff) continue;
                total = Math.Max(0, total) + BitConverter.ToUInt16(d, at + 3);
            }
            return total;
        }

        /// <summary>Header property: int32 key, 4 zero bytes, length byte, ASCII text (seen: 7 = POI group, 16 = display name).</summary>
        public static string HeaderString(byte[] d, int headerLen, int key)
        {
            for (var i = 0; i + 9 < headerLen; i++)
            {
                if (d[i] != key || d[i + 1] != 0 || d[i + 2] != 0 || d[i + 3] != 0) continue;
                if (d[i + 4] != 0 || d[i + 5] != 0 || d[i + 6] != 0 || d[i + 7] != 0) continue;
                int len = d[i + 8];
                if (len < 2 || len > 127 || i + 9 + len > headerLen) continue;
                var ok = true;
                for (var k = 0; k < len && ok; k++) ok = d[i + 9 + k] >= 0x20 && d[i + 9 + k] < 0x7f;
                if (ok) return Encoding.ASCII.GetString(d, i + 9, len);
            }
            return null;
        }

        /// <summary>Entity spawner "Type" values in the prefab's block data (trader names among them).</summary>
        public static IEnumerable<string> EntityTypes(byte[] blob)
        {
            var i = 0;
            while ((i = IndexOf(blob, TypeKey, i)) >= 0)
            {
                var p = i + TypeKey.Length;
                i = p;
                if (p >= blob.Length) yield break;
                int len = blob[p];
                if (len == 0 || len > 127 || p + 1 + len > blob.Length) continue;
                yield return Encoding.UTF8.GetString(blob, p + 1, len);
            }
        }

        static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            for (var i = Math.Max(0, from); i <= hay.Length - needle.Length; i++)
            {
                if (hay[i] != needle[0]) continue;
                var k = 1;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>A prefab with traders by its display name ("Solaris-AMP Factory"): exact first, then containing.</summary>
        public PrefabTraders PrefabsByDisplay(string display)
        {
            var withTraders = prefabs.Values.Where(p => p.Traders.Count > 0 && !string.IsNullOrEmpty(p.Display)).ToList();
            return withTraders.FirstOrDefault(p => string.Equals(p.Display, display, StringComparison.OrdinalIgnoreCase))
                ?? withTraders.FirstOrDefault(p => p.Display.IndexOf(display, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public PrefabTraders Prefab(string name) => name != null && prefabs.TryGetValue(name, out var p) ? p : null;

        public List<PrefabTraders> PrefabsWith(string trader) =>
            trader != null && prefabsByTrader.TryGetValue(trader, out var l) ? l : new List<PrefabTraders>();

        // ----- playfields --------------------------------------------------------------------

        /// <summary>
        /// POI groups from one playfield yaml (list items with GroupName, Faction, CountMinMax; [0, 0] or Probability 0 is
        /// skipped). Several files of the same folder can be fed one after the other.
        /// </summary>
        public void LoadPlayfield(string folder, string yaml)
        {
            string pfType = null, planetType = null;
            var items = new List<Dictionary<string, string>>();
            Dictionary<string, string> cur = null;
            var curIndent = -1;
            foreach (var raw in yaml.Split('\n'))
            {
                var line = StripComment(raw).TrimEnd('\r', ' ', '\t');
                if (line.Trim().Length == 0) continue;
                var indent = line.Length - line.TrimStart(' ').Length;
                var t = line.Trim();
                if (indent == 0)
                {
                    cur = null;
                    curIndent = -1;
                    var kv0 = KeyValue(t);
                    if (kv0.key == "PlayfieldType") pfType = kv0.value;
                    else if (kv0.key == "PlanetType") planetType = kv0.value;
                    continue;
                }
                if (t.StartsWith("- "))
                {
                    if (cur == null || indent <= curIndent)
                    {
                        cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        items.Add(cur);
                        curIndent = indent;
                    }
                    else continue; // nested list inside the current item
                    t = t.Substring(2).Trim();
                }
                else if (cur != null && indent <= curIndent) { cur = null; curIndent = -1; }
                if (cur == null) continue;
                var kv = KeyValue(t);
                if (kv.key != null && !cur.ContainsKey(kv.key)) cur[kv.key] = kv.value;
            }

            if (!string.IsNullOrEmpty(planetType))
            {
                planetTypeByFolder[folder] = planetType;
                if (!foldersByPlanetType.TryGetValue(planetType, out var fs)) foldersByPlanetType[planetType] = fs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                fs.Add(folder);
            }
            var kind = KindLabel(pfType, planetType, folder);
            // own chance per entry, then SpawnPOINear: an entry only spawns next to an anchor group, so it can't be more
            // likely than its best anchor in the same file (2026-11-19: R2FactoryPolaris3 [1,1] near R2PolarisAuxT2 [0,1])
            double OwnChance(Dictionary<string, string> it)
            {
                if (it.TryGetValue("CountMinMax", out var c) && Regex.IsMatch(c, @"^\[\s*0\s*,\s*0\s*\]$")) return 0;
                if (it.TryGetValue("Count", out var c1) && c1.Trim() == "0") return 0;
                var ch = 1.0;
                if (it.TryGetValue("CountMinMax", out var cmm) && Regex.IsMatch(cmm, @"^\[\s*0\s*,")) ch = 0.5;
                if (it.TryGetValue("Probability", out var pr) && double.TryParse(pr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv) && pv < 1) ch *= Math.Max(0, pv);
                return ch;
            }
            // anchor chances per (group, territory rule): an entry restricted to a territory may use anchors with the same
            // rule or none; an unrestricted entry only unrestricted anchors (2026-11-19: R2PolarisAuxT2 is [0,1] in neutral
            // space but [1,1] inside Polaris territory on the same moon)
            string TerritoryKey(Dictionary<string, string> it) =>
                it.TryGetValue("FactionTerritory", out var t) && !string.IsNullOrWhiteSpace(t)
                    ? string.Join(",", t.Trim().TrimStart('[').TrimEnd(']').Split(',').Select(x => x.Trim().Trim('"', '\'')).Where(x => x.Length > 0))
                    : "";
            var groupChance = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
                if (it.TryGetValue("GroupName", out var gn) && !string.IsNullOrEmpty(gn))
                {
                    var key = gn + "|" + TerritoryKey(it);
                    groupChance[key] = Math.Max(groupChance.TryGetValue(key, out var old) ? old : 0, OwnChance(it));
                }
            foreach (var it in items)
            {
                if (!it.TryGetValue("GroupName", out var g) || string.IsNullOrEmpty(g)) continue;
                var chance = OwnChance(it);
                if (chance <= 0) continue;
                it.TryGetValue("Faction", out var fac);
                if (it.TryGetValue("SpawnPOINear", out var near) && !string.IsNullOrWhiteSpace(near))
                {
                    var tk = TerritoryKey(it);
                    var anchors = near.Trim().TrimStart('[').TrimEnd(']').Split(',').Select(x => x.Trim().Trim('"', '\'')).Where(x => x.Length > 0)
                        .SelectMany(x => new[] { x + "|", tk.Length > 0 ? x + "|" + tk : null }).Where(k => k != null && groupChance.ContainsKey(k))
                        .Select(k => groupChance[k]).ToList();
                    var anyAnchor = near.Trim().TrimStart('[').TrimEnd(']').Split(',').Select(x => x.Trim().Trim('"', '\''))
                        .Any(x => groupChance.Keys.Any(k => k.StartsWith(x + "|", StringComparison.OrdinalIgnoreCase)));
                    if (anchors.Count > 0) chance *= Math.Min(1, anchors.Max());
                    else if (anyAnchor) continue; // the anchor exists only under another territory rule: can't spawn here
                    if (chance <= 0) continue;
                }
                if (!spawnsByGroup.TryGetValue(g, out var list)) spawnsByGroup[g] = list = new List<PoiSpawn>();
                List<string> territory = null;
                if (it.TryGetValue("FactionTerritory", out var ft) && !string.IsNullOrWhiteSpace(ft))
                    territory = ft.Trim().TrimStart('[').TrimEnd(']').Split(',').Select(x => x.Trim().Trim('"', '\'')).Where(x => x.Length > 0).ToList();
                var tkey = territory == null ? "" : string.Join(",", territory);
                if (!list.Any(x => x.Playfield == folder && string.Equals(x.Faction ?? "", fac ?? "", StringComparison.OrdinalIgnoreCase) &&
                                   (x.Territory == null ? "" : string.Join(",", x.Territory)) == tkey))
                    list.Add(new PoiSpawn { Group = g, Faction = fac, Playfield = folder, Kind = kind, Chance = chance, Territory = territory });
            }
        }

        static (string key, string value) KeyValue(string t)
        {
            var i = t.IndexOf(':');
            if (i <= 0) return (null, null);
            return (t.Substring(0, i).Trim(), t.Substring(i + 1).Trim().Trim('"', '\''));
        }

        /// <summary>"Alien planet", "Barren Moon", "Poor Quality Asteroid Field (space)"; the folder name when there is no type.</summary>
        public static string KindLabel(string pfType, string planetType, string folder)
        {
            var name = SplitWords(string.IsNullOrEmpty(planetType) ? folder : planetType);
            switch ((pfType ?? "").Trim().ToLowerInvariant())
            {
                case "planet": return name.IndexOf("planet", StringComparison.OrdinalIgnoreCase) >= 0 ? name : name + " planet";
                case "moon": return name.IndexOf("moon", StringComparison.OrdinalIgnoreCase) >= 0 ? name : name + " moon";
                case "space": return name + " (space)";
                default: return name;
            }
        }

        public static string SplitWords(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1])))) sb.Append(' ');
                sb.Append(c);
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        /// <summary>
        /// Scenario playfield folders whose yaml has this PlanetType (the save's Playfields.planettype): "Stormy Desert" →
        /// EdenDesertStorm; "Barren" → 8 folders. Empty for stars and gas giants.
        /// </summary>
        public IReadOnlyCollection<string> FoldersForPlanetType(string planetType) =>
            planetType != null && foldersByPlanetType.TryGetValue(planetType.Trim(), out var f) ? (IReadOnlyCollection<string>)f : new string[0];

        public List<PoiSpawn> Spawns(string group) =>
            group != null && spawnsByGroup.TryGetValue(group, out var l) ? l : new List<PoiSpawn>();

        // ----- factions & standings ----------------------------------------------------------

        static readonly Regex FactionHeader = new Regex(@"^\{\s*\+?\s*Faction\s+Id:\s*(?<id>\d+)\s*,\s*Name:\s*""?(?<name>[^""]+?)""?\s*$", RegexOptions.IgnoreCase);

        public void LoadFactions(TextReader reader)
        {
            string line;
            FactionDef cur = null;
            while ((line = reader.ReadLine()) != null)
            {
                var t = StripComment(line).Trim();
                if (t.Length == 0) continue;
                var h = FactionHeader.Match(t);
                if (h.Success)
                {
                    cur = new FactionDef { Id = int.Parse(h.Groups["id"].Value, CultureInfo.InvariantCulture), Name = h.Groups["name"].Value.Trim() };
                    factionsById[cur.Id] = cur;
                    factionsByName[cur.Name] = cur;
                    continue;
                }
                if (t.StartsWith("}")) { cur = null; continue; }
                if (cur == null) continue;
                var kv = KeyValue(t);
                if (kv.key == "Abbrev") cur.Abbrev = kv.value;
                else if (kv.key == "Reputation") cur.Reputation = kv.value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        public FactionDef Faction(int id) => factionsById.TryGetValue(id, out var f) ? f : null;
        public FactionDef Faction(string name) => name != null && factionsByName.TryGetValue(name.Trim(), out var f) ? f : null;

        /// <summary>Starting standings of one origin block in DefReputation.ecf, e.g. "Human:1".</summary>
        public void LoadDefReputation(TextReader reader, string origin = "Human:1")
        {
            string line;
            var inBlock = false;
            while ((line = reader.ReadLine()) != null)
            {
                var t = StripComment(line).Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("{"))
                {
                    var m = Regex.Match(t, @"Reputation\s+Name:\s*""?(?<n>[^""]+?)""?\s*$", RegexOptions.IgnoreCase);
                    inBlock = m.Success && m.Groups["n"].Value.Trim().Equals(origin, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (t.StartsWith("}")) { inBlock = false; continue; }
                if (!inBlock) continue;
                var kv = KeyValue(t);
                if (kv.key != null && int.TryParse(kv.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) startStanding[kv.key] = v;
            }
        }

        public int? StartStanding(string factionName) =>
            factionName != null && startStanding.TryGetValue(factionName, out var v) ? v : (int?)null;

        /// <summary>
        /// The standings table in the save's game.dat (verified 2026-11-18 on dev and prod, game v1.20): int32 count, then
        /// count × [uint16 standing][int32 player faction id][byte 0][byte NPC faction id]. Found by scanning for the
        /// longest run that fits; only standings that moved away from the start seem to be stored.
        /// Result: player faction id → NPC faction id → standing.
        /// </summary>
        public static Dictionary<int, Dictionary<int, int>> ReadStandings(byte[] d, Func<int, bool> isNpcFaction)
        {
            int bestAt = -1, bestN = 0;
            for (var p = 0; p + 4 <= d.Length; p++)
            {
                var n = BitConverter.ToInt32(d, p);
                if (n < 1 || n > 4096 || n <= bestN || p + 4 + 8L * n > d.Length) continue;
                var ok = true;
                for (var k = 0; k < n && ok; k++)
                {
                    var e = p + 4 + 8 * k;
                    var standing = BitConverter.ToUInt16(d, e);
                    var pf = BitConverter.ToInt32(d, e + 2);
                    ok = standing <= 40000 && pf > 0 && pf < 100_000_000 && d[e + 6] == 0 && isNpcFaction(d[e + 7]);
                }
                if (ok) { bestAt = p; bestN = n; }
            }
            var map = new Dictionary<int, Dictionary<int, int>>();
            for (var k = 0; k < bestN; k++)
            {
                var e = bestAt + 4 + 8 * k;
                var pf = BitConverter.ToInt32(d, e + 2);
                if (!map.TryGetValue(pf, out var row)) map[pf] = row = new Dictionary<int, int>();
                row[d[e + 7]] = BitConverter.ToUInt16(d, e);
            }
            return map;
        }

        public bool IsNpcFaction(int id) => factionsById.ContainsKey(id);

        /// <summary>
        /// The player faction's standing with an NPC faction: game.dat, else the starting standing; null when the faction
        /// has no reputation (Civilian, Alien, …) or nothing is known.
        /// </summary>
        public int? Standing(Dictionary<int, Dictionary<int, int>> standings, int playerFactionId, FactionDef npc)
        {
            if (npc == null || !npc.Reputation) return null;
            if (standings != null && standings.TryGetValue(playerFactionId, out var row) && row.TryGetValue(npc.Id, out var v)) return v;
            return StartStanding(npc.Name);
        }

        /// <summary>Tier names as the Factions screen shows them; limits from the game code (≤ 6000, 12000, 18000, 24000).</summary>
        public static string Tier(int standing) =>
            standing <= 6000 ? "Hated" : standing <= 12000 ? "Unfriendly" : standing <= 18000 ? "Neutral" : standing <= 24000 ? "Friendly" : "Honored";

        /// <summary>
        /// A TraderNPCConfig price ("100-150" credits or "mf=1.0-1.25" × market price) as a credit range and a tier against
        /// the item's market price: below (&lt; 0.9), about (0.9-1.15), above (1.15-1.5), far above (&gt; 1.5); by the
        /// midpoint. Tier null without a market price; credits null for a factor without one.
        /// </summary>
        public static (double lo, double hi)? Credits(string price, double? market)
        {
            if (string.IsNullOrWhiteSpace(price)) return null;
            var p = price.Trim();
            var factor = p.StartsWith("mf=", StringComparison.OrdinalIgnoreCase);
            if (factor) p = p.Substring(3);
            var parts = p.Split('-');
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)) return null;
            var hi = lo;
            if (parts.Length > 1 && !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hi)) hi = lo;
            if (factor) { if (market == null) return null; lo *= market.Value; hi *= market.Value; }
            return (lo, hi);
        }

        public static string PriceTier(string price, double? market)
        {
            if (market == null || market <= 0) return null;
            var c = Credits(price, market);
            if (c == null) return null;
            var f = (c.Value.lo + c.Value.hi) / 2 / market.Value;
            return f < 0.9 ? "below market" : f <= 1.15 ? "about market" : f <= 1.5 ? "above market" : "far above market";
        }

        /// <summary>Market price below which an item counts as everyday (ingots, steel plates, computers, ammo).</summary>
        public const double EverydayPrice = 1000;
        static readonly HashSet<string> EverydayCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Food", "Ingredients", "Farming", "Deco Blocks", "BuildingBlocks" };

        /// <summary>
        /// Everyday goods left out of trade summaries unless asked for (food no, reactor cores yes):
        /// food, ingredients, farming, deco and building blocks, and anything with a market price below
        /// <see cref="EverydayPrice"/>. Tokens (quest items) and items without a market price always count as notable.
        /// </summary>
        public static bool IsEveryday(string item, string category, double? market)
        {
            if (item != null && item.StartsWith("Token:", StringComparison.OrdinalIgnoreCase)) return false;
            if (category != null && EverydayCategories.Contains(category.Trim())) return true;
            return market.HasValue && market.Value < EverydayPrice;
        }

        public static bool NeutralOrBetter(int? standing) => standing == null || standing.Value > 12000;

        static string StripComment(string line)
        {
            var inQuote = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') inQuote = !inQuote;
                else if (line[i] == '#' && !inQuote) return line.Substring(0, i);
            }
            return line;
        }
    }
}
