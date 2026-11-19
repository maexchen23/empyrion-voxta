using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Eleon.Modding;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// where_to_buy (POST /where-to-buy, dedi only): which traders sell an item, in which POIs, and where those POIs spawn
    /// (static, from the scenario files), plus the sellers this player already knows (visited POIs and past trades in
    /// global.db) with the distance from the player's sector. POIs whose faction the player's faction stands below
    /// Neutral with are left out; standings come from the save's game.dat.
    /// </summary>
    public partial class BridgeMod
    {
        static volatile TraderIndex traderIndex;
        static volatile PoiTerminals poiTerminals;
        static volatile GalaxyOdds galaxyOdds;
        static string saveGameDir;

        const int MaxBuyItems = 3, MaxSellers = 8, MaxPoisPerSeller = 5, MaxKinds = 6, MaxKnown = 5, MaxHidden = 8, MaxStars = 3;
        /// <summary>A star is suggested when its class gives at least this chance of a matching place.</summary>
        const double MinStarChance = 0.25;

        /// <summary>Background thread on the dedi: trader config, factions, playfields, prefabs.</summary>
        static void LoadTraderIndex(string content, string scenario)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var idx = new TraderIndex();
                string Cfg(string file)
                {
                    foreach (var dir in new[] { scenario, content })
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        var p = Path.Combine(dir, "Configuration", file);
                        if (File.Exists(p)) return p;
                        p = Path.Combine(dir, "Content", "Configuration", file);
                        if (File.Exists(p)) return p;
                    }
                    return null;
                }
                var traders = Cfg("TraderNPCConfig.ecf");
                if (traders == null) { Log("traders: no TraderNPCConfig.ecf found, where-to-buy is off"); return; }
                using (var r = new StreamReader(traders)) idx.LoadTraderConfig(r);
                var factions = Cfg("Factions.ecf");
                if (factions != null) using (var r = new StreamReader(factions)) idx.LoadFactions(r);
                var rep = Cfg("DefReputation.ecf");
                if (rep != null) using (var r = new StreamReader(rep)) idx.LoadDefReputation(r);
                try
                {
                    var dlg = Cfg("Dialogues.ecf");
                    var dlgText = Cfg("Dialogues.csv");
                    if (dlg != null && dlgText != null)
                    {
                        var terms = new PoiTerminals();
                        using (var r = new StreamReader(dlg)) terms.LoadDialogues(r);
                        using (var r = new StreamReader(dlgText)) terms.LoadTexts(r);
                        poiTerminals = terms;
                        Log($"poi terminals: {terms.InitCount} terminals in {terms.DialogueCount} dialogues");
                        var missions = new TradeMissions();
                        using (var r = new StreamReader(dlg)) missions.LoadDialogues(r);
                        using (var r = new StreamReader(dlgText)) missions.LoadTexts(r);
                        tradeMissions = missions;
                        Log($"trade missions: {missions.Count} trader mission dialogues");
                    }
                }
                catch (Exception e) { Log($"poi terminals: {e.Message}"); }

                var root = string.IsNullOrEmpty(scenario) ? content : scenario;
                var pfDir = Path.Combine(root, "Playfields");
                if (Directory.Exists(pfDir))
                    foreach (var dir in Directory.GetDirectories(pfDir))
                        foreach (var f in Directory.GetFiles(dir, "*.yaml"))
                            try { idx.LoadPlayfield(Path.GetFileName(dir), File.ReadAllText(f)); }
                            catch (Exception e) { Log($"traders: {f}: {e.Message}"); }

                int scanned = 0, failed = 0;
                // scenario prefabs first; base-game prefabs (e.g. BA_CrashedCV_…) only fill gaps, for the POI classification
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prefabDir in new[] { Path.Combine(root, "Prefabs"), string.IsNullOrEmpty(content) ? null : Path.Combine(content, "Prefabs") })
                {
                    if (prefabDir == null || !Directory.Exists(prefabDir)) continue;
                    foreach (var f in Directory.GetFiles(prefabDir, "*.epb"))
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        if (!seen.Add(name)) continue;
                        scanned++;
                        try { idx.AddPrefab(name, File.ReadAllBytes(f)); }
                        catch (Exception) { failed++; }
                    }
                }
                traderIndex = idx;
                try
                {
                    var odds = new GalaxyOdds();
                    var gc = Cfg("GalaxyConfig.ecf");
                    if (gc != null) odds.LoadGalaxyConfig(File.ReadAllText(gc));
                    var presetDir = Path.Combine(root, "RandomPresets");
                    if (Directory.Exists(presetDir))
                        foreach (var f in Directory.GetFiles(presetDir, "SolarSystemConfig*.yaml"))
                            odds.AddPreset(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f));
                    if (odds.ClassCount > 0 && odds.PresetCount > 0) galaxyOdds = odds;
                    Log($"galaxy odds: {odds.ClassCount} star classes, {odds.PresetCount} system presets");
                }
                catch (Exception e) { Log($"galaxy odds: load failed: {e.Message}"); }
                Log($"traders ready in {sw.ElapsedMilliseconds} ms: {idx.TraderCount} traders, {idx.PrefabCount} of {scanned} prefabs with traders" +
                    $"{(failed > 0 ? $" ({failed} unreadable)" : "")}, {idx.GroupCount} POI groups, {idx.FactionCount} factions");
            }
            catch (Exception e) { Log($"traders: load failed: {e.Message}"); }
        }

        /// <summary>The save game folder (global.db, game.dat). GetPathFor(SaveGame), else Saves/Games/&lt;GameName from the server config&gt;.</summary>
        static string SaveGameDir()
        {
            if (saveGameDir != null) return saveGameDir;
            string dir = null;
            try { dir = api.Application.GetPathFor(AppFolder.SaveGame); } catch { }
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "global.db")))
            {
                try
                {
                    var content = api.Application.GetPathFor(AppFolder.Content);
                    var root = Path.GetDirectoryName(content.TrimEnd('/', '\\'));
                    var games = Path.Combine(root, "Saves", "Games");
                    var file = GameText.ConfigFileFromArgs(Environment.GetCommandLineArgs()) ?? "dedicated.yaml";
                    var cfg = Path.IsPathRooted(file) ? file : Path.Combine(root, file);
                    string name = null;
                    if (File.Exists(cfg))
                    {
                        var m = Regex.Match(File.ReadAllText(cfg), @"^\s*SaveGameName:\s*""?(?<n>[^""\r\n#]+?)""?\s*(#.*)?$", RegexOptions.Multiline);
                        if (m.Success) name = m.Groups["n"].Value.Trim();
                    }
                    if (name != null && File.Exists(Path.Combine(games, name, "global.db"))) dir = Path.Combine(games, name);
                    else if (Directory.Exists(games))
                    {
                        var withDb = Directory.GetDirectories(games).Where(d => File.Exists(Path.Combine(d, "global.db"))).ToList();
                        if (withDb.Count == 1) dir = withDb[0];
                    }
                }
                catch (Exception e) { Log($"where-to-buy: no save folder: {e.Message}"); }
            }
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "global.db")))
            {
                saveGameDir = dir;
                Log($"where-to-buy: save folder {dir}");
            }
            return saveGameDir;
        }

        sealed class KnownPoi
        {
            public long Id;
            public string Name, Prefab, Playfield, Via, Trader;
            public int FacGroup;
            public double Sx, Sy, Sz;
        }

        /// <summary>HTTP thread: POST /where-to-buy (item=…, player=SteamID optional).</summary>
        static (int, string) HandleWhereToBuy(string body)
        {
            var f = Form.Parse(body);
            var query = f.Get("item")?.Trim();
            if (string.IsNullOrEmpty(query)) return (400, Error("'item' is required."));
            var idx = traderIndex;
            if (idx == null) return (503, Error("The trader data is not loaded yet (or this server has no TraderNPCConfig.ecf)."));
            var t = gameText;

            var candidates = idx.SoldItems.Select(n => (n, t?.DisplayName(n) ?? n)).ToList();
            var hits = ItemSearch.Search(query, candidates);
            var j = new Json().Obj().Prop("ok", true).Prop("query", query);
            if (hits.Count == 0)
            {
                j.Prop("count", 0);
                var buyers = ItemSearch.Search(query, SearchCandidates()).Take(1).ToList();
                if (buyers.Count > 0)
                {
                    j.Prop("matchedItem", buyers[0].Display);
                    j.Prop("boughtBy", idx.Buyers(buyers[0].Internal).Count);
                }
                return (200, j.EndObj().ToString());
            }
            if (hits.Count > MaxBuyItems)
            {
                j.Prop("count", hits.Count).Prop("namesOnly", true).Key("names").Arr();
                foreach (var h in hits.Take(MaxListedNames)) j.Val(h.Display);
                return (200, j.EndArr().EndObj().ToString());
            }

            // the player (optional): faction, location, known POIs, standings
            DediPlayer player = null;
            lock (sync) player = PickPlayer(f.Get("player"), out _);
            int pFacGroup = -1, pFacId = -1;
            double? px = null, py = null, pz = null;
            var known = new List<KnownPoi>();
            List<KnownBody> knownBodies = null;
            Dictionary<int, Dictionary<int, int>> standings = null;
            string dbNote = null;
            var save = SaveGameDir();
            if (player != null && save != null)
            {
                try
                {
                    ReadPlayerTradeData(Path.Combine(save, "global.db"), player.EntityId, player.Playfield,
                        out pFacGroup, out pFacId, out px, out py, out pz, known);
                }
                catch (Exception e) { dbNote = "global.db: " + e.Message; }
                try
                {
                    var dat = Path.Combine(save, "game.dat");
                    if (File.Exists(dat)) standings = TraderIndex.ReadStandings(File.ReadAllBytes(dat), idx.IsNpcFaction);
                }
                catch (Exception e) { dbNote = (dbNote == null ? "" : dbNote + "; ") + "game.dat: " + e.Message; }
            }
            // test/debug: at=<playfield name> gives a position when nobody is online
            if (!px.HasValue && save != null && f.Get("at") is string at && at.Length > 0)
                try { ReadSector(Path.Combine(save, "global.db"), at, out px, out py, out pz); } catch (Exception e) { dbNote = "at: " + e.Message; }
            // only a player faction (group 0) has its own row in game.dat; others fall back to the starting standings
            var standingKey = pFacGroup == 0 ? pFacId : -1;
            // trader missions: which known factories trade already (unlocks are per factory instance)
            ILookup<long, QuestRow> questsByPoi = null;
            if (player != null && save != null && tradeMissions != null)
                try { questsByPoi = ReadQuests(Path.Combine(save, "global.db"), player.EntityId).ToLookup(q => q.PoiId); }
                catch (Exception e) { dbNote = (dbNote == null ? "" : dbNote + "; ") + "trader missions: " + e.Message; }

            if (player != null)
            {
                j.Key("player").Obj().Prop("name", player.Name);
                if (pFacGroup >= 0) j.Prop("factionGroup", pFacGroup).Prop("factionId", pFacId);
                j.Prop("standings", standings != null && standings.ContainsKey(standingKey) ? "game.dat" : "starting values");
                if (dbNote != null) j.Prop("note", dbNote);
                j.EndObj();
            }

            j.Prop("count", hits.Count).Key("items").Arr();
            foreach (var h in hits)
            {
                var sellers = idx.Sellers(h.Internal);
                var sellerNames = new HashSet<string>(sellers.Select(s => s.Trader), StringComparer.OrdinalIgnoreCase);
                j.Obj().Prop("name", h.Display);

                // known sellers: visited POIs whose prefab holds a seller, and past trades with a seller
                var knownRows = new List<(KnownPoi poi, string trader)>();
                foreach (var k in known)
                {
                    if (k.Trader != null) { if (sellerNames.Contains(k.Trader)) knownRows.Add((k, k.Trader)); continue; }
                    var pre = idx.Prefab(k.Prefab);
                    if (pre == null) continue;
                    foreach (var tr in pre.Traders.Where(sellerNames.Contains)) knownRows.Add((k, tr));
                }
                var knownList = knownRows
                    .GroupBy(r => (r.poi.Id, r.trader.ToLowerInvariant()))
                    .Select(g => g.OrderBy(r => r.poi.Via == "traded" ? 0 : 1).First())
                    .Select(r =>
                    {
                        var fac = idx.Faction(r.poi.FacGroup);
                        var st = idx.Standing(standings, standingKey, fac);
                        double? ly = px.HasValue ? Math.Sqrt(Sq(r.poi.Sx - px.Value) + Sq(r.poi.Sy - py.Value) + Sq(r.poi.Sz - pz.Value)) / 100000.0 : (double?)null;
                        return (r.poi, r.trader, fac, st, ly);
                    })
                    .ToList();
                // a factory whose trades are still locked counts as LockedPenaltyLy further away: an unlocked one a bit further is the better tip
                var knownOk = knownList.Where(k => TraderIndex.NeutralOrBetter(k.st))
                    .OrderBy(k => (k.ly ?? double.MaxValue / 2) + (TradeLock(k.poi.Prefab, k.poi.Id, questsByPoi) != null ? LockedPenaltyLy : 0)).ToList();
                var knownBlocked = knownList.Where(k => !TraderIndex.NeutralOrBetter(k.st)).ToList();
                if (knownOk.Count > 0)
                {
                    j.Key("known").Arr();
                    foreach (var k in knownOk.Take(MaxKnown))
                    {
                        j.Obj().Prop("trader", k.trader).Prop("poi", k.poi.Name).Prop("playfield", k.poi.Playfield).Prop("via", k.poi.Via);
                        if (k.ly.HasValue) j.Prop("ly", Math.Round(k.ly.Value, 1));
                        if (k.fac != null) j.Prop("faction", k.fac.Label);
                        if (k.st.HasValue) j.Prop("tier", TraderIndex.Tier(k.st.Value));
                        if (TradeLock(k.poi.Prefab, k.poi.Id, questsByPoi) is string lockText) j.Prop("tradesLocked", lockText);
                        else if (GatedFamily(k.poi.Prefab) != null) j.Prop("tradesUnlocked", true);
                        j.EndObj();
                    }
                    j.EndArr();
                    if (knownOk.Count > MaxKnown) j.Prop("moreKnown", knownOk.Count - MaxKnown);
                }

                // static: traders → POI prefabs → where their group spawns; hidden when the faction is below Neutral
                var hidden = knownBlocked.Select(k => (k.trader, poi: k.poi.Name, fac: k.fac?.Label, tier: k.st.HasValue ? TraderIndex.Tier(k.st.Value) : null)).ToList();
                var sellerRows = new List<(TraderOffer offer, List<(PrefabTraders pre, List<PoiSpawn> spawns, FactionDef fac, int? st)> pois)>();
                var notPlaced = 0;
                foreach (var s in sellers)
                {
                    var pois = new List<(PrefabTraders, List<PoiSpawn>, FactionDef, int?)>();
                    foreach (var pre in idx.PrefabsWith(s.Trader))
                    {
                        var spawns = idx.Spawns(pre.Group);
                        if (spawns.Count == 0) continue;
                        foreach (var byFac in spawns.GroupBy(sp => sp.Faction ?? "", StringComparer.OrdinalIgnoreCase))
                        {
                            var fac = idx.Faction(byFac.Key);
                            var st = idx.Standing(standings, standingKey, fac);
                            if (!TraderIndex.NeutralOrBetter(st)) { hidden.Add((s.Trader, pre.Display ?? pre.Prefab, fac?.Label ?? byFac.Key, TraderIndex.Tier(st.Value))); continue; }
                            pois.Add((pre, byFac.ToList(), fac, st));
                        }
                    }
                    if (pois.Count == 0 && idx.PrefabsWith(s.Trader).All(p => idx.Spawns(p.Group).Count == 0)) notPlaced++;
                    if (pois.Count > 0) sellerRows.Add((s, pois));
                }
                var knownTraders = new HashSet<string>(knownOk.Select(k => k.trader), StringComparer.OrdinalIgnoreCase);
                j.Key("sellers").Arr();
                foreach (var (offer, pois) in sellerRows.OrderByDescending(r => knownTraders.Contains(r.offer.Trader)).ThenByDescending(r => r.pois.Count).Take(MaxSellers))
                {
                    j.Obj().Prop("trader", offer.Trader).Prop("stock", offer.StockMin == offer.StockMax ? $"{offer.StockMax}" : $"{offer.StockMin}-{offer.StockMax}");
                    if (offer.Buys) j.Prop("alsoBuys", true);
                    j.Key("pois").Arr();
                    // one entry per POI name; different prefabs with the same display name merge their places
                    var merged = pois.GroupBy(p => (p.pre.Display ?? TraderIndex.SplitWords(p.pre.Prefab)) + "|" + (p.fac?.Label ?? ""))
                        .OrderByDescending(g => g.SelectMany(p => p.spawns).Count()).Take(MaxPoisPerSeller);
                    foreach (var g in merged)
                    {
                        var first = g.First();
                        j.Obj().Prop("name", first.pre.Display ?? TraderIndex.SplitWords(first.pre.Prefab));
                        if (first.fac != null) j.Prop("faction", first.fac.Label);
                        if (first.st.HasValue) j.Prop("tier", TraderIndex.Tier(first.st.Value));
                        if (g.Any(p => GatedFamily(p.pre.Prefab) != null)) j.Prop("tradeMission", true);
                        var kinds = g.SelectMany(p => p.spawns).Select(sp => sp.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        j.Key("where").Arr();
                        foreach (var k in kinds.Take(MaxKinds)) j.Val(k);
                        j.EndArr();
                        if (kinds.Count > MaxKinds) j.Prop("moreWhere", kinds.Count - MaxKinds);
                        j.EndObj();
                    }
                    j.EndArr();
                    var poiNames = pois.Select(p => p.pre.Display ?? p.pre.Prefab).Distinct().Count();
                    if (poiNames > MaxPoisPerSeller) j.Prop("morePois", poiNames - MaxPoisPerSeller);
                    j.EndObj();
                }
                j.EndArr();
                if (sellerRows.Count > MaxSellers) j.Prop("moreSellers", sellerRows.Count - MaxSellers);
                if (notPlaced > 0) j.Prop("sellersNotPlaced", notPlaced);
                if (px.HasValue && player != null && save != null)
                {
                    try
                    {
                        knownBodies = knownBodies ?? ReadKnownBodies(Path.Combine(save, "global.db"), player.EntityId, pFacGroup, pFacId);
                        KnownPlanetSuggestions(j, idx, sellers, standings, standingKey, knownBodies,
                            new HashSet<string>(knownOk.Select(k => k.poi.Playfield), StringComparer.OrdinalIgnoreCase),
                            px.Value / 100000.0, py.Value / 100000.0, pz.Value / 100000.0);
                    }
                    catch (Exception e) { Log($"where-to-buy known planets: {e.Message}"); }
                }
                // stars of known systems are covered planet by planet above
                var knownSystems = new HashSet<string>((knownBodies ?? new List<KnownBody>()).Select(b => b.System), StringComparer.OrdinalIgnoreCase);
                if (px.HasValue) StarSuggestions(j, idx, sellers, standings, standingKey, px.Value / 100000.0, py.Value / 100000.0, pz.Value / 100000.0, knownSystems);

                var hiddenDistinct = hidden.GroupBy(x => x.trader + "|" + x.poi).Select(g => g.First()).ToList();
                if (hiddenDistinct.Count > 0)
                {
                    j.Key("hidden").Arr();
                    foreach (var x in hiddenDistinct.Take(MaxHidden))
                    {
                        j.Obj().Prop("trader", x.trader).Prop("poi", x.poi);
                        if (x.fac != null) j.Prop("faction", x.fac);
                        if (x.tier != null) j.Prop("tier", x.tier);
                        j.EndObj();
                    }
                    j.EndArr();
                    if (hiddenDistinct.Count > MaxHidden) j.Prop("moreHidden", hiddenDistinct.Count - MaxHidden);
                }
                j.EndObj();
            }
            j.EndArr();
            Log($"where-to-buy '{query}': {hits.Count} item(s), {known.Count} known POI/trade rows, standings {(standings == null ? "none" : $"{standings.Count} faction(s)")}");
            return (200, j.EndObj().ToString());
        }

        static double Sq(double v) => v * v;

        const int MaxKnownPlanets = 4;

        /// <summary>
        /// "Could have one": planets, moons and sectors of systems the player or their faction discovered (the galaxy map
        /// shows their types) whose planet type can spawn a matching trader POI, with the chance from the type's playfield
        /// configs and the faction territory there; never from what is actually on them. Bodies with
        /// an already known seller are left out; closest first.
        /// </summary>
        static void KnownPlanetSuggestions(Json j, TraderIndex idx, List<TraderOffer> sellers, Dictionary<int, Dictionary<int, int>> standings,
            int standingKey, List<KnownBody> bodies, HashSet<string> sellerPlayfields, double lx, double ly, double lz)
        {
            var odds = galaxyOdds;
            var entries = new List<(PoiSpawn sp, string poi)>();
            var seen = new HashSet<PoiSpawn>();
            foreach (var s in sellers)
                foreach (var pre in idx.PrefabsWith(s.Trader))
                    foreach (var sp in idx.Spawns(pre.Group))
                        if (seen.Add(sp) && TraderIndex.NeutralOrBetter(idx.Standing(standings, standingKey, idx.Faction(sp.Faction))))
                            entries.Add((sp, pre.Display ?? TraderIndex.SplitWords(pre.Prefab)));
            if (entries.Count == 0) return;

            var found = new List<(KnownBody b, double chance, List<string> pois, double dist)>();
            foreach (var b in bodies)
            {
                if (b.PfType == 3 || b.PfType == 5 || sellerPlayfields.Contains(b.Name)) continue;
                var folders = idx.FoldersForPlanetType(b.PlanetType);
                if (folders.Count == 0) continue;
                var at = odds?.TerritoriesAt(b.X, b.Y, b.Z);
                var sum = 0.0;
                var pois = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in folders)
                {
                    var miss = 1.0;
                    foreach (var (sp, poi) in entries)
                    {
                        if (!string.Equals(sp.Playfield, folder, StringComparison.OrdinalIgnoreCase)) continue;
                        var p = sp.Chance * (at == null ? 1 : GalaxyOdds.SpawnAllowed(sp.Faction, sp.Territory, at));
                        if (p <= 0) continue;
                        miss *= 1 - p;
                        pois.Add(poi);
                    }
                    sum += 1 - miss;
                }
                var chance = sum / folders.Count; // the body is one of the type's playfields, equally likely
                if (chance < MinStarChance) continue;
                found.Add((b, chance, pois.ToList(), Math.Sqrt(Sq(b.X - lx) + Sq(b.Y - ly) + Sq(b.Z - lz))));
            }
            if (found.Count == 0) return;
            j.Key("knownPlanets").Arr();
            foreach (var (b, chance, pois, dist) in found.OrderBy(x => Math.Round(x.dist, 1)).ThenByDescending(x => x.chance).Take(MaxKnownPlanets))
            {
                j.Obj().Prop("name", b.Name).Prop("kind", b.Kind).Prop("type", PoiScan.PlanetTypeLabel(b.PlanetType)).Prop("system", b.System)
                    .Prop("ly", Math.Round(dist, 1)).Prop("chance", Math.Round(chance * 100)).Prop("entered", b.Entered).Prop("byPlayer", b.ByPlayer);
                j.Key("pois").Arr();
                foreach (var p in pois.Take(3)) j.Val(p);
                j.EndArr().EndObj();
            }
            j.EndArr();
            if (found.Count > MaxKnownPlanets) j.Prop("moreKnownPlanets", found.Count - MaxKnownPlanets);
        }

        /// <summary>
        /// "Might be there": the closest stars whose class (as the galaxy map shows it) makes a matching POI likely, from
        /// the generation configs only, never from what the save knows about the system (no peeking into unexplored systems).
        /// </summary>
        static void StarSuggestions(Json j, TraderIndex idx, List<TraderOffer> sellers, Dictionary<int, Dictionary<int, int>> standings, int standingKey,
            double lx, double ly, double lz, HashSet<string> skipSystems = null)
        {
            var odds = galaxyOdds;
            if (odds == null) return;
            var stars = GalaxyStars();
            if (stars == null) return;

            // seller spawn entries the player can use (standing), each with its own faction territory rule
            var entries = new List<PoiSpawn>();
            var seen = new HashSet<PoiSpawn>();
            foreach (var s in sellers)
                foreach (var pre in idx.PrefabsWith(s.Trader))
                    foreach (var sp in idx.Spawns(pre.Group))
                        if (seen.Add(sp) && TraderIndex.NeutralOrBetter(idx.Standing(standings, standingKey, idx.Faction(sp.Faction))))
                            entries.Add(sp);
            if (entries.Count == 0) return;

            // chance per (star class, territories there); territory ratio rounded so nearby stars share the result
            var cache = new Dictionary<string, double>();
            double ChanceFor(Star st, List<(GalaxyOdds.Territory t, double ratio)> at)
            {
                var key = st.TypeIndex + "|" + string.Join(",", at.Select(a => a.t.Name + ":" + Math.Round(a.ratio, 1)));
                if (cache.TryGetValue(key, out var c)) return c;
                var cls = odds.Class(st.TypeIndex);
                var preset = GalaxyOdds.PresetName(cls);
                if (cls == null || cls.Suffix == null || !odds.HasPreset(preset)) return cache[key] = 0;
                var expected = odds.Expected(preset);
                var miss = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    if (!expected.ContainsKey(e.Playfield)) continue;
                    var p = e.Chance * GalaxyOdds.SpawnAllowed(e.Faction, e.Territory, at);
                    if (p <= 0) continue;
                    miss[e.Playfield] = (miss.TryGetValue(e.Playfield, out var m) ? m : 1) * (1 - p);
                }
                var lambda = miss.Sum(kv => expected[kv.Key] * (1 - kv.Value));
                return cache[key] = GalaxyOdds.AtLeastOne(lambda);
            }

            var ordered = stars.Where(st => !st.Hidden && !st.Manual && !string.IsNullOrEmpty(st.Name) && (skipSystems == null || !skipSystems.Contains(st.Name)))
                .Select(st => (st, dist: Math.Sqrt(Sq(st.X - lx) + Sq(st.Y - ly) + Sq(st.Z - lz))))
                .OrderBy(x => x.dist);
            var chosen = new List<(Star st, double chance, double dist, string territory)>();
            var haveGood = false;
            var scanned = 0;
            foreach (var (st, dist) in ordered)
            {
                if (++scanned > 20000 || (chosen.Count >= MaxStars && haveGood)) break;
                var at = odds.TerritoriesAt(st.X, st.Y, st.Z);
                var chance = ChanceFor(st, at);
                if (chance < MinStarChance) continue;
                var good = chance >= 0.6;
                if (chosen.Count >= MaxStars && !(good && !haveGood)) continue;
                var terr = string.Join(" + ", at.Select(a => a.t == odds.Neutral ? "neutral space" : a.t.Faction));
                chosen.Add((st, chance, dist, terr));
                haveGood |= good;
            }
            if (chosen.Count == 0) return;
            j.Key("stars").Arr();
            foreach (var (st, chance, dist, terr) in chosen)
                j.Obj().Prop("star", st.Name).Prop("class", odds.Class(st.TypeIndex)?.StarClass).Prop("territory", terr)
                    .Prop("ly", Math.Round(dist, 1)).Prop("chance", Math.Round(chance * 100)).EndObj();
            j.EndArr();
        }

        /// <summary>Read-only global.db: the player's faction, current sector, visited POIs and past trades.</summary>
        static void ReadPlayerTradeData(string db, int playerId, string playfield, out int facGroup, out int facId,
            out double? sx, out double? sy, out double? sz, List<KnownPoi> known)
        {
            facGroup = facId = -1;
            sx = sy = sz = null;
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT facgroup, facid FROM Entities WHERE entityid = @p";
                    cmd.Parameters.AddWithValue("@p", playerId);
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) { facGroup = Convert.ToInt32(r[0]); facId = Convert.ToInt32(r[1]); }
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT sectorx, sectory, sectorz FROM Playfields WHERE name = @pf LIMIT 1";
                    cmd.Parameters.AddWithValue("@pf", playfield ?? "");
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) { sx = Convert.ToDouble(r[0]); sy = Convert.ToDouble(r[1]); sz = Convert.ToDouble(r[2]); }
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT DISTINCT e.entityid, e.name, s.bpname, e.facgroup, p.name, p.sectorx, p.sectory, p.sectorz " +
                        "FROM VisitedStructures v JOIN Entities e ON e.entityid = v.poiid JOIN Structures s ON s.entityid = e.entityid " +
                        "JOIN Playfields p ON p.pfid = e.pfid WHERE v.entityid = @p AND e.isremoved = 0 AND e.facgroup <> 0 AND e.facgroup <> 1";
                    cmd.Parameters.AddWithValue("@p", playerId);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) known.Add(Row(r, "visited", null));
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT DISTINCT e.entityid, e.name, s.bpname, e.facgroup, p.name, p.sectorx, p.sectory, p.sectorz, t.tradername " +
                        "FROM TraderHistory t JOIN Entities e ON e.entityid = t.poiid LEFT JOIN Structures s ON s.entityid = e.entityid " +
                        "JOIN Playfields p ON p.pfid = t.pfid WHERE t.entityid = @p AND e.isremoved = 0";
                    cmd.Parameters.AddWithValue("@p", playerId);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) known.Add(Row(r, "traded", r[8] as string));
                }
            }
        }

        static void ReadSector(string db, string playfield, out double? sx, out double? sy, out double? sz)
        {
            sx = sy = sz = null;
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT sectorx, sectory, sectorz FROM Playfields WHERE name = @pf LIMIT 1";
                    cmd.Parameters.AddWithValue("@pf", playfield);
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) { sx = Convert.ToDouble(r[0]); sy = Convert.ToDouble(r[1]); sz = Convert.ToDouble(r[2]); }
                }
            }
        }

        static KnownPoi Row(SqliteDataReader r, string via, string trader) => new KnownPoi
        {
            Id = Convert.ToInt64(r[0]), Name = r[1] as string, Prefab = r[2] as string, FacGroup = r[3] is DBNull ? -1 : Convert.ToInt32(r[3]),
            Playfield = r[4] as string, Sx = Convert.ToDouble(r[5]), Sy = Convert.ToDouble(r[6]), Sz = Convert.ToDouble(r[7]), Via = via, Trader = trader,
        };
    }
}
