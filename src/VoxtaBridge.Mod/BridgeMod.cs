using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Phase-0 spike. One DLL, role chosen by process:
    ///  - DedicatedServer: HTTP listener (/ping, /status), player list (ModApi v2 + legacy API1), receives playfield snapshots.
    ///  - PlayfieldServer: every few seconds builds a snapshot of players + their current structure and sends it to the dedi.
    ///  - Client / SinglePlayer: not supported yet, does nothing.
    /// State is static because the game may instantiate the class once for IMod and once for ModInterface.
    /// </summary>
    public partial class BridgeMod : IMod, ModInterface
    {
        public const string ModName = "VoxtaBridge";
        const string PacketPrefix = "VB1:";
        static DateTime nextMappingSend = DateTime.MinValue;

        static IModApi api;
        static ModGameAPI legacy;
        static ApplicationMode mode;
        static BridgeConfig config;
        static HttpServer http;

        // dedi state (written on the game thread or network callback, read by HTTP threads)
        static readonly object sync = new object();
        static string playersJson = "[]";
        static readonly Queue<(int player, int item, int count)> pendingGives = new Queue<(int, int, int)>();
        static readonly Dictionary<int, string> legacyPlayerInfo = new Dictionary<int, string>();
        static readonly Dictionary<string, (DateTime at, string json)> pfSnapshots = new Dictionary<string, (DateTime, string)>();
        static readonly Dictionary<string, int> pfPacketCount = new Dictionary<string, int>();
        static DateTime startedAt = DateTime.UtcNow;
        static DateTime nextDediRefresh = DateTime.MinValue;
        static DateTime nextLegacyRefresh = DateTime.MinValue;
        static ushort legacySeq = 1000;
        static int legacyEvents;

        // playfield-server state (game thread only)
        static readonly List<IPlayfield> playfields = new List<IPlayfield>();
        static DateTime nextSnapshot = DateTime.MinValue;

        static void Log(string msg) => api?.Log($"[{ModName}] {msg}");

        // ----- IMod (ModApi v2) ------------------------------------------------

        public void Init(IModApi modApi)
        {
            api = modApi;
            mode = modApi.Application.Mode;
            Log($"init, mode={mode}, version={Assembly.GetExecutingAssembly().GetName().Version}");
            // the game calls ModInterface.Game_Start before IMod.Init, so its log line can't be written there
            if (legacy != null) Log("legacy API1 attached (Game_Start ran before Init)");

            try
            {
                switch (mode)
                {
                    case ApplicationMode.DedicatedServer: StartGameTextLoad(); InitDedi(); break;
                    case ApplicationMode.PlayfieldServer: StartGameTextLoad(); InitPf(); break;
                    default: Log("client/single player not supported yet, staying idle"); break;
                }
            }
            catch (Exception e)
            {
                api.LogError($"[{ModName}] init failed: {e}");
            }
        }

        public void Shutdown()
        {
            Log("shutdown");
            try { http?.Stop(); } catch { }
            http = null;
            if (api != null) api.Application.Update -= OnUpdate;
        }

        // ----- ModInterface (legacy API1, dedi only) ---------------------------

        public void Game_Start(ModGameAPI dediAPI)
        {
            legacy = dediAPI;
            Log("legacy API1 attached");
        }

        // debug: playfield load requested over HTTP, executed on the game thread
        static string pendingLoad;

        public void Game_Update()
        {
            string load;
            lock (sync) { load = pendingLoad; pendingLoad = null; }
            if (load != null && legacy != null)
            {
                var ok = legacy.Game_Request(CmdId.Request_Load_Playfield, ++legacySeq, new PlayfieldLoad(120f, load, 0));
                Log($"API1 Request_Load_Playfield '{load}' (keep 120 s): {ok}");
            }

            if (legacy == null || api == null || DateTime.UtcNow < nextLegacyRefresh) return;
            // every second: its position is the fresh one for players on foot (see LivePosition.cs)
            nextLegacyRefresh = DateTime.UtcNow.AddSeconds(1);
            try
            {
                // GetPlayerEntityIds also lists known offline players; asking for them only yields a
                // PlayerIdNotFound error event every 5 s.
                foreach (var id in api.Application.GetPlayerEntityIds())
                    if (api.Application.GetPlayerDataFor(id)?.IsOnline == true)
                        legacy.Game_Request(CmdId.Request_Player_Info, ++legacySeq, new Id(id));
            }
            catch (Exception e) { Log($"legacy refresh failed: {e.Message}"); }
        }

        public void Game_Exit() => Log("legacy API1 exit");

        public void Game_Event(CmdId eventId, ushort seqNr, object data)
        {
            try
            {
                if (CompleteApi1(eventId, seqNr, data)) return; // answer to a waiting inventory call
                switch (eventId)
                {
                    case CmdId.Event_Player_Connected:
                    case CmdId.Event_Player_ChangedPlayfield:
                        if (data is Id cid) legacy?.Game_Request(CmdId.Request_Player_Info, ++legacySeq, new Id(cid.id));
                        else if (data is IdPlayfield ip) legacy?.Game_Request(CmdId.Request_Player_Info, ++legacySeq, new Id(ip.id));
                        Log($"API1 {eventId}");
                        break;

                    case CmdId.Event_Player_Info when data is PlayerInfo pi:
                        var j = new Json().Obj()
                            .Prop("entityId", pi.entityId).Prop("name", pi.playerName).Prop("steamId", pi.steamId)
                            .Prop("playfield", pi.playfield).Vec("pos", pi.pos.x, pi.pos.y, pi.pos.z)
                            .Prop("health", pi.health).Prop("healthMax", pi.healthMax)
                            .Prop("oxygen", pi.oxygen).Prop("oxygenMax", pi.oxygenMax)
                            .Prop("food", pi.food).Prop("foodMax", pi.foodMax)
                            .Prop("stamina", pi.stamina).Prop("staminaMax", pi.staminaMax)
                            .Prop("radiation", pi.radiation).Prop("radiationMax", pi.radiationMax)
                            .Prop("bodyTemp", pi.bodyTemp).Prop("bodyTempMax", pi.bodyTempMax)
                            .Prop("credits", pi.credits).Prop("exp", pi.exp).Prop("upgradePoints", pi.upgrade)
                            .Prop("kills", pi.kills).Prop("died", pi.died)
                            .Prop("bagStacks", pi.bag?.Length ?? 0).Prop("toolbarStacks", pi.toolbar?.Length ?? 0);
                        Items(j, "toolbar", pi.toolbar);
                        Items(j, "bag", pi.bag);
                        if (!string.IsNullOrEmpty(pi.bpInFactory))
                            j.Key("factory").Obj().Prop("blueprint", pi.bpInFactory).Prop("remainingS", pi.bpRemainingTime).EndObj();
                        var json = j.EndObj().ToString();
                        lock (sync)
                        {
                            legacyPlayerInfo[pi.entityId] = json; legacyEvents++;
                            legacyPos[pi.entityId] = (pi.playfield, DateTime.UtcNow, pi.pos.x, pi.pos.y, pi.pos.z);
                        }
                        RecordTokens(pi);
                        ProbeKills(pi);
                        break;

                    case CmdId.Event_ChatMessage when data is ChatInfo ci:
                        ChatFromApi1(ci);
                        break;

                    case CmdId.Event_PdaStateChange when data is PdaStateInfo psi:
                        RecordPda(psi);
                        break;

                    case CmdId.Event_Error:
                        Log($"API1 error event (seq {seqNr}): {(data is ErrorInfo ei ? ei.errorType.ToString() : data?.ToString())}");
                        break;
                }
            }
            catch (Exception e) { Log($"API1 event {eventId} failed: {e.Message}"); }
        }

        // ----- dedicated server ---------------------------------------------------

        static void InitDedi()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            config = BridgeConfig.LoadOrCreate(dir, Log);
            ProbeHook();
            ChatHook();

            var ok = api.Network.RegisterReceiverForPlayfieldPackets(OnPlayfieldPacket);
            Log($"registered playfield packet receiver: {ok}");

            api.Application.Update += OnUpdate;

            try
            {
                http = new HttpServer(config.BindAddress, config.Port, Handle, Log);
                http.Start();
            }
            catch (Exception e)
            {
                api.LogError($"[{ModName}] listener failed on {config.BindAddress}:{config.Port}: {e}");
            }
        }

        static void OnPlayfieldPacket(string sender, string playfieldName, byte[] data)
        {
            try
            {
                var s = Encoding.UTF8.GetString(data);
                if (s.StartsWith(MappingPrefix)) { ReceiveMapping(s.Substring(MappingPrefix.Length)); return; }
                if (s.StartsWith(ReplyPrefix)) { ReceiveReply(s.Substring(ReplyPrefix.Length)); return; }
                if (s.StartsWith(BoxesPrefix)) { ReceiveBoxes(s.Substring(BoxesPrefix.Length)); return; }
                if (!s.StartsWith(PacketPrefix)) return; // packet of another mod
                lock (sync)
                {
                    pfSnapshots[playfieldName ?? "?"] = (DateTime.UtcNow, s.Substring(PacketPrefix.Length));
                    pfPacketCount.TryGetValue(playfieldName ?? "?", out var c);
                    pfPacketCount[playfieldName ?? "?"] = c + 1;
                }
            }
            catch (Exception e) { Log($"bad playfield packet from {sender}/{playfieldName}: {e.Message}"); }
        }

        static void RefreshDediPlayers()
        {
            var j = new Json().Arr();
            var cache = new Dictionary<int, DediPlayer>();
            foreach (var id in api.Application.GetPlayerEntityIds())
            {
                var pd = api.Application.GetPlayerDataFor(id);
                if (pd == null) continue;
                var d = pd.Value;
                j.Obj().Prop("entityId", d.EntityId).Prop("name", d.PlayerName).Prop("steamId", d.SteamId)
                    .Prop("playfield", d.PlayfieldName).Prop("online", d.IsOnline).EndObj();
                cache[d.EntityId] = new DediPlayer
                {
                    EntityId = d.EntityId, Name = d.PlayerName, SteamId = d.SteamId,
                    Playfield = d.PlayfieldName, Online = d.IsOnline,
                };
            }
            j.EndArr();
            Dictionary<int, DediPlayer> before;
            lock (sync) { before = dediPlayers; playersJson = j.ToString(); dediPlayers = cache; }
            // arrival in another playfield: Voxta's star markers for that system go away
            foreach (var p in cache.Values)
                if (p.Online && before.TryGetValue(p.EntityId, out var old) && old.Playfield != p.Playfield)
                    OnPlayfieldChanged(p.EntityId, p.Name, p.Playfield);
            // logoff: queued marker deletions are safe now
            foreach (var old in before.Values)
                if (old.Online && (!cache.TryGetValue(old.EntityId, out var now) || !now.Online))
                    OnPlayerOffline(old.EntityId, old.Name);
        }

        static void FlushGives()
        {
            while (true)
            {
                (int player, int item, int count) g;
                lock (sync)
                {
                    if (pendingGives.Count == 0 || legacy == null) return;
                    g = pendingGives.Dequeue();
                }
                legacy.Game_Request(CmdId.Request_Player_AddItem, ++legacySeq, new IdItemStack(g.player, new ItemStack(g.item, g.count)));
                Log($"debug give: player {g.player} item {g.item} x{g.count}");
            }
        }

        static (int, string) Handle(string method, string path, string auth, string body)
        {
            if (method == "POST")
            {
                if (!Authorized(auth)) return (401, "{\"error\":\"missing or wrong bearer token\"}");
                if (path == "/transfer") return HandleTransfer(body);
                if (path == "/containers") return HandleContainers(body);
                if (path == "/find") return HandleFind(body);
                if (path == "/craft") return HandleCraft(body);
                if (path == "/items") return HandleItems(body);
                if (path == "/where-to-buy") return HandleWhereToBuy(body);
                if (path == "/pois") return HandlePois(body);
                if (path == "/structure") return HandleStructureInfo(body);
                if (path == "/active") return HandleSetActive(body);
                if (path == "/system") return HandleSystem(body);
                if (path == "/poi-trade") return HandlePoiTrade(body);
                if (path == "/poi-terminal") return HandlePoiTerminal(body);
                if (path == "/quests") return HandleQuests(body);
                if (path == "/quest-pursue") return HandlePursueQuest(body);
                if (path == "/chat-say") return HandleChatSay(body);
                if (path == "/marks") return HandleMarks(body);
                if (path == "/microjump") return HandleMicroJump(body);
                if (path == "/debug/jump") return HandleDebugJump(body);
                if (path == "/debug/starmarker") return HandleStarMarker(body);
                return (404, "{\"error\":\"unknown path\"}");
            }
            if (method != "GET") return (400, "{\"error\":\"only GET and POST\"}");

            if (path.StartsWith("/debug/give/"))
            {
                // /debug/give/<playerEntityId>/<internalItemName>/<count>: test helper (API1 Request_Player_AddItem).
                if (!Authorized(auth)) return (401, "{\"error\":\"missing or wrong bearer token\"}");
                var parts = path.Substring("/debug/give/".Length).Split('/');
                if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[2], out var count) || count <= 0)
                    return (400, "{\"error\":\"use /debug/give/<playerEntityId>/<itemName>/<count>\"}");
                var itemName = Uri.UnescapeDataString(parts[1]);
                int itemId = 0;
                lock (itemNames)
                    foreach (var kv in itemNames)
                        if (string.Equals(kv.Value, itemName, StringComparison.OrdinalIgnoreCase)) { itemId = kv.Key; break; }
                if (itemId == 0) return (404, new Json().Obj().Prop("error", $"unknown item '{itemName}'").EndObj().ToString());
                lock (sync) pendingGives.Enqueue((pid, itemId, count));
                return (200, new Json().Obj().Prop("queued", itemName).Prop("id", itemId).Prop("count", count).Prop("player", pid).EndObj().ToString());
            }

            if (path.StartsWith("/debug/pois"))
            {
                if (!Authorized(auth)) return (401, "{\"error\":\"missing or wrong bearer token\"}");
                try { return (200, DebugPois(path)); }
                catch (Exception e) { return (500, new Json().Obj().Prop("error", e.ToString()).EndObj().ToString()); }
            }
            if (path.StartsWith("/debug/stars"))
            {
                if (!Authorized(auth)) return (401, "{\"error\":\"missing or wrong bearer token\"}");
                try { return (200, DebugStars(path)); }
                catch (Exception e) { return (500, new Json().Obj().Prop("error", e.ToString()).EndObj().ToString()); }
            }
            if (path == "/debug/galaxy")
            {
                if (!Authorized(auth)) return (401, "{\"error\":\"missing or wrong bearer token\"}");
                try { return (200, GalaxyProbe()); }
                catch (Exception e) { return (500, new Json().Obj().Prop("error", e.ToString()).EndObj().ToString()); }
            }

            if (path.StartsWith("/debug/loadpf/"))
            {
                if (!Authorized(auth)) return (401, "{\"error\":\"missing or wrong bearer token\"}");
                var name = Uri.UnescapeDataString(path.Substring("/debug/loadpf/".Length));
                lock (sync) pendingLoad = name;
                return (200, new Json().Obj().Prop("queued", name).EndObj().ToString());
            }

            if (path == "/ping")
            {
                return (200, new Json().Obj().Prop("ok", true).Prop("mod", ModName)
                    .Prop("version", Assembly.GetExecutingAssembly().GetName().Version.ToString())
                    .Prop("mode", mode.ToString()).Prop("uptimeS", (long)(DateTime.UtcNow - startedAt).TotalSeconds)
                    .EndObj().ToString());
            }

            if (!Authorized(auth)) return (401, "{\"error\":\"missing or wrong bearer token\"}");

            if (path == "/status")
            {
                var j = new Json().Obj();
                List<DediPlayer> online;
                lock (sync) online = dediPlayers.Values.Where(x => x.Online).ToList();
                if (tradeMissions != null && online.Count > 0) WriteQuestStatus(j, online); // global.db reads: outside the lock
                if (online.Count > 0) WriteLive(j, online);
                WriteChat(j, online);
                lock (sync)
                {
                    j.Key("players").Raw(playersJson);
                    j.Key("api1").Obj().Prop("attached", legacy != null).Prop("playerInfoEvents", legacyEvents)
                        .Key("players").Arr();
                    foreach (var v in legacyPlayerInfo.Values) j.Raw(v);
                    j.EndArr().EndObj();

                    j.Key("playfields").Arr();
                    foreach (var kv in pfSnapshots)
                    {
                        j.Obj().Prop("name", kv.Key).Prop("ageS", (long)(DateTime.UtcNow - kv.Value.at).TotalSeconds)
                            .Prop("packets", pfPacketCount.TryGetValue(kv.Key, out var c) ? c : 0)
                            .Key("snapshot").Raw(kv.Value.json).EndObj();
                    }
                    j.EndArr();
                }
                return (200, j.EndObj().ToString());
            }

            return (404, "{\"error\":\"unknown path\"}");
        }

        static bool Authorized(string header)
        {
            // Logs why a request was rejected (never the token itself), so a bad paste is easy to spot.
            if (header == null) { Log("401: no Authorization header"); return false; }
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) { Log("401: Authorization is not a Bearer token"); return false; }
            var given = header.Substring(7).Trim();
            var want = config?.Token ?? "";
            if (given.Length != want.Length) { Log($"401: token length {given.Length}, expected {want.Length}"); return false; }
            int diff = 0;
            for (int i = 0; i < want.Length; i++) diff |= given[i] ^ want[i];
            if (diff != 0) Log("401: token has the right length but is wrong");
            return diff == 0;
        }

        // ----- playfield server ------------------------------------------------------

        static void InitPf()
        {
            api.Application.OnPlayfieldLoaded += pf =>
            {
                playfields.Add(pf);
                Log($"playfield loaded: {pf.Name} ({pf.PlayfieldType}, system {pf.SolarSystemName})");
            };
            api.Application.OnPlayfieldUnloading += pf =>
            {
                playfields.Remove(pf);
                Log($"playfield unloading: {pf.Name}");
            };
            // Same VoxtaBridge.cfg as the dedi (read only here): ownership + range for the snapshot.
            config = BridgeConfig.LoadOrCreate(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), Log, create: false);
            var ok = api.Network.RegisterReceiverForDediPackets(OnDediPacket);
            Log($"registered dedi packet receiver: {ok}");
            api.Application.Update += OnUpdate;
            ProbeHook();
        }

        static void OnUpdate()
        {
            var now = DateTime.UtcNow;
            try
            {
                if (mode == ApplicationMode.DedicatedServer)
                {
                    FlushDediOutbox(); // every tick: transfer requests are waiting on HTTP threads
                    FlushGives();
                    FlushSays();
                    FlushApi1Outbox();
                    FlushBookmarkPushes();
                    QuestRecorderTick();
                    ProbeStatsTick();
                    if (now < nextDediRefresh) return;
                    nextDediRefresh = now.AddMilliseconds(config?.SnapshotIntervalMs ?? 2000);
                    RefreshDediPlayers();
                }
                else if (mode == ApplicationMode.PlayfieldServer)
                {
                    ProcessPfInbox(); // every tick
                    SampleHits(now);
                    ProcessMarkerRemovals();
                    if (now < nextSnapshot) return;
                    nextSnapshot = now.AddMilliseconds(2000);
                    var loaded = playfields.ToList();
                    // Resend the mapping now and then, so a restarted dedi gets it again.
                    if (loaded.Count > 0 && now >= nextMappingSend)
                    {
                        nextMappingSend = now.AddMinutes(10);
                        SendMappingToDedi(loaded[0].Name);
                    }
                    foreach (var pf in loaded)
                    {
                        SendSnapshot(pf);
                        try { SendBoxes(pf); } catch (Exception e) { Log($"boxes for {pf.Name} failed: {e.Message}"); }
                        try { ProbeNpcs(pf); } catch (Exception e) { Log($"probe npcs: {e.Message}"); }
                    }
                    ForgetAbsentPlayers(loaded);
                    ScanSizes(loaded, now);
                }
            }
            catch (Exception e)
            {
                Log($"update failed: {e.GetType().Name}: {e.Message}");
            }
        }

        static void SendSnapshot(IPlayfield pf)
        {
            var j = new Json().Obj()
                .Prop("playfield", pf.Name).Prop("type", pf.PlayfieldType).Prop("planetType", pf.PlanetType)
                .Prop("system", pf.SolarSystemName).Prop("pvp", pf.IsPvP);

            var entities = pf.Entities;
            j.Prop("entityCount", entities?.Count ?? 0);
            if (entities != null)
            {
                j.Key("entityTypes").Obj();
                foreach (var g in entities.Values.GroupBy(e => e.Type.ToString()).OrderBy(g => g.Key))
                    j.Prop(g.Key, g.Count());
                j.EndObj();
            }

            j.Key("players").Arr();
            foreach (var p in pf.Players.Values)
            {
                j.Obj().Prop("id", p.Id).Prop("name", p.Name).Prop("steamId", p.SteamId)
                    .Vec("pos", p.Position.x, p.Position.y, p.Position.z)
                    .Prop("health", p.Health).Prop("oxygen", p.Oxygen).Prop("food", p.Food)
                    .Prop("isPilot", p.IsPilot).Prop("bagStacks", p.Bag?.Count ?? 0);

                var active = ResolveActive(pf, p, config?.RequireOwnership ?? true, config?.TransferRangeMeters ?? 0);
                var s = active.Structure;
                if (active.PlayerOn) j.Prop("aboard", active.How == "pinned" ? "aboard" : active.How);
                j.Key("active");
                WriteActive(j, active);
                if (!active.PlayerOn && ForeignStructureFor(p) is { } foreign)
                {
                    // inside a POI or someone else's structure: name, type and owner only (no tanks, no containers)
                    var fe = foreign.Entity;
                    j.Key("visiting").Obj().Prop("name", fe?.Name).Prop("type", fe?.Type.ToString());
                    try
                    {
                        // NPC owners: the group byte is the Factions.ecf id; the enum names the built-in ones (Polaris, Zirax…)
                        var g = fe.Faction.Group;
                        var owner = g == FactionGroup.Player ? "another player" : g == FactionGroup.Faction ? "a player faction" : g.ToString();
                        if (owner.Length > 0 && char.IsDigit(owner[0])) owner = null;
                        if (owner != null) j.Prop("owner", owner);
                    }
                    catch { }
                    j.EndObj();
                }
                j.Key("structure");
                if (s == null) j.Raw("null");
                else
                {
                    j.Obj();
                    StructureCore(j, s);
                    Containers(j, pf, active, p);
                    j.EndObj();
                }
                j.EndObj();
            }
            j.EndArr().EndObj();

            var data = Encoding.UTF8.GetBytes(PacketPrefix + j);
            if (!api.Network.SendToDedicatedServer(ModName, data, pf.Name))
                Log($"SendToDedicatedServer returned false for {pf.Name}");
        }

        /// <summary>Name, type, size, power, shield, mass, pilot, position and tanks of a structure (inside an open object).</summary>
        static void StructureCore(Json j, IStructure s)
        {
            var e = s.Entity;
            j.Prop("id", s.Id).Prop("name", e?.Name).Prop("type", e?.Type.ToString())
                .Prop("sizeClass", s.SizeClass).Prop("blocks", s.BlockCount).Prop("devices", s.DeviceCount)
                .Prop("powered", s.IsPowered).Prop("shieldActive", s.IsShieldActive).Prop("shield", s.ShieldLevel)
                .Prop("mass", s.TotalMass).Prop("pilot", s.Pilot?.Name);
            if (e != null) j.Vec("worldPos", e.Position.x, e.Position.y, e.Position.z);
            Tank(j, "fuel", s.FuelTank);
            Tank(j, "oxygen", s.OxygenTank);
            Tank(j, "pentaxid", s.PentaxidTank);
        }

        // Item id -> name. GetBlockAndItemMapping only works in playfield servers (empty on the dedi), so
        // playfield servers also send their mapping to the dedi (MappingPrefix packets).
        static readonly Dictionary<int, string> itemNames = new Dictionary<int, string>();
        static bool ownMappingLoaded;
        const string MappingPrefix = "VBM:";

        static string ItemName(int id)
        {
            lock (itemNames)
            {
                if (!ownMappingLoaded)
                {
                    ownMappingLoaded = true;
                    try
                    {
                        var map = api.Application.GetBlockAndItemMapping();
                        if (map != null)
                            foreach (var kv in map) itemNames[kv.Value] = kv.Key;
                        Log($"item mapping loaded: {itemNames.Count} entries");
                    }
                    catch (Exception ex) { Log($"GetBlockAndItemMapping failed: {ex.Message}"); }
                }
                return itemNames.TryGetValue(id, out var n) ? n : null;
            }
        }

        /// <summary>Playfield server: send the id -> name mapping to the dedi in packets of about 32 KB.</summary>
        static void SendMappingToDedi(string playfieldName)
        {
            ItemName(0); // make sure our own mapping is loaded
            List<KeyValuePair<int, string>> all;
            lock (itemNames) all = itemNames.ToList();
            if (all.Count == 0) { Log("no item mapping to send"); return; }

            var sb = new StringBuilder(MappingPrefix);
            int packets = 0;
            foreach (var kv in all)
            {
                sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                if (sb.Length > 32000)
                {
                    api.Network.SendToDedicatedServer(ModName, Encoding.UTF8.GetBytes(sb.ToString()), playfieldName);
                    packets++;
                    sb.Clear().Append(MappingPrefix);
                }
            }
            if (sb.Length > MappingPrefix.Length)
            {
                api.Network.SendToDedicatedServer(ModName, Encoding.UTF8.GetBytes(sb.ToString()), playfieldName);
                packets++;
            }
            Log($"sent item mapping to dedi: {all.Count} entries in {packets} packets");
        }

        /// <summary>Dedi: merge a mapping packet from a playfield server.</summary>
        static void ReceiveMapping(string payload)
        {
            int added = 0;
            lock (itemNames)
            {
                foreach (var line in payload.Split('\n'))
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || !int.TryParse(line.Substring(0, tab), out var id)) continue;
                    if (!itemNames.ContainsKey(id)) added++;
                    itemNames[id] = line.Substring(tab + 1);
                }
            }
            if (added > 0) Log($"item mapping from playfield server: +{added} entries");
        }

        static void Items(Json j, string key, ItemStack[] stacks)
        {
            j.Key(key).Arr();
            if (stacks != null)
                foreach (var st in stacks)
                {
                    j.Obj();
                    ItemProps(j, st, withDescription: true);
                    j.Prop("slot", st.slotIdx).EndObj();
                }
            j.EndArr();
        }

        // ----- display names / descriptions from the game's own files --------------------------

        static volatile GameText gameText;
        static string contentDir, scenarioDir;

        /// <summary>Reads Localization.csv + Items/Blocks/TokenConfig.ecf (base game, then active scenario) off the main thread.</summary>
        static void StartGameTextLoad()
        {
            string content = null, scenario = null;
            try { content = api.Application.GetPathFor(AppFolder.Content); } catch (Exception e) { Log($"no content path: {e.Message}"); }
            try { scenario = api.Application.GetPathFor(AppFolder.ActiveScenario); } catch (Exception e) { Log($"no scenario path: {e.Message}"); }
            if (string.IsNullOrEmpty(scenario) && !string.IsNullOrEmpty(content))
                scenario = ScenarioFromServerConfig(content);
            contentDir = content;
            scenarioDir = scenario;
            Log($"game text: content={content ?? "-"}, scenario={scenario ?? "-"}");

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var t = GameText.Load(content, scenario, msg => Log("game text: " + msg));
                    gameText = t;
                    var r = Recipes.LoadFiles(content, scenario, msg => Log("recipes: " + msg));
                    recipes = r;
                    Log($"recipes ready: {r.Count}");
                    Log($"game text ready in {sw.ElapsedMilliseconds} ms: {t.LocalizationCount} texts, {t.EntryCount} items/blocks, {t.TokenCount} tokens");
                    if (mode == ApplicationMode.DedicatedServer) LoadTraderIndex(content, scenario);
                }
                catch (Exception e) { Log($"game text load failed: {e.Message}"); }
            });
        }

        /// <summary>
        /// GetPathFor(ActiveScenario) is empty on dedi and playfield servers (seen on build 5232), so read
        /// GameConfig.CustomScenario from the server config named on the command line (configFile=…).
        /// </summary>
        static string ScenarioFromServerConfig(string contentDir)
        {
            try
            {
                var root = Path.GetDirectoryName(contentDir.TrimEnd('/', '\\'));
                var file = GameText.ConfigFileFromArgs(Environment.GetCommandLineArgs()) ?? "dedicated.yaml";
                var path = Path.IsPathRooted(file) ? file : Path.Combine(root, file);
                if (!File.Exists(path)) { Log($"server config not found: {path}"); return null; }

                var name = GameText.ScenarioFromDedicatedConfig(File.ReadAllText(path));
                if (name == null) { Log($"no CustomScenario in {path} (vanilla)"); return null; }

                var dir = Path.Combine(contentDir, "Scenarios", name);
                if (!Directory.Exists(dir)) { Log($"scenario folder not found: {dir}"); return null; }
                return dir;
            }
            catch (Exception e)
            {
                Log($"could not read the scenario from the server config: {e.Message}");
                return null;
            }
        }

        /// <summary>id, internal name, display name, count; for tokens the token's own name (token id = stack 'ammo').</summary>
        static void ItemProps(Json j, ItemStack st, bool withDescription)
        {
            var name = ItemName(st.id);
            j.Prop("id", st.id).Prop("name", name).Prop("count", st.count);
            var t = gameText;
            if (t == null || name == null) return;

            if (t.IsToken(name))
            {
                j.Prop("tokenId", st.ammo);
                var token = t.GetToken(st.ammo);
                if (token != null)
                {
                    j.Prop("display", token.Name);
                    if (withDescription && token.Description != null) j.Prop("desc", token.Description);
                    return;
                }
            }

            var display = t.DisplayName(name);
            if (display != null) j.Prop("display", display);
            if (withDescription)
            {
                var desc = t.Description(name);
                if (desc != null) j.Prop("desc", desc);
            }
        }

        static readonly DeviceTypeName[] ContainerTypes =
            { DeviceTypeName.Container, DeviceTypeName.AmmoCntr, DeviceTypeName.Fridge, DeviceTypeName.HarvestCntr };

        /// <summary>Snapshot: containers of the docking group, plus the structures in transfer range (listed, not expanded).</summary>
        static void Containers(Json j, IPlayfield pf, ActiveInfo active, IPlayer player)
        {
            var inRange = new List<InRange>();
            var group = new List<GroupMember>();
            var all = ActiveScope(pf, active, player, config?.RequireOwnership ?? true, config?.TransferRangeMeters ?? 0,
                includeRangeContainers: false, inRange, group);
            j.Key("group").Arr();
            foreach (var m in group)
                j.Obj().Prop("name", m.Name).Prop("type", m.Structure.Entity?.Type.ToString()).Prop("role", m.Role)
                    .Prop("aboard", m.Aboard).Prop("containers", m.Containers).EndObj();
            j.EndArr();
            j.Key("containers").Arr();
            foreach (var tc in all) WriteContainer(j, (GameContainer)tc);
            j.EndArr();
            j.Key("inRange").Arr();
            foreach (var r in inRange.OrderBy(x => x.Distance))
                j.Obj().Prop("name", r.Structure.Entity?.Name).Prop("type", r.Structure.Entity?.Type.ToString())
                    .Prop("distance", Math.Round(r.Distance)).Prop("containers", r.Containers).EndObj();
            j.EndArr();
        }

        static void Tank(Json j, string name, IStructureTank t)
        {
            j.Key(name);
            if (t == null) { j.Raw("null"); return; }
            j.Obj().Prop("content", t.Content).Prop("capacity", t.Capacity).EndObj();
        }
    }
}
