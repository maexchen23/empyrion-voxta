using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Eleon.Modding;
using UnityEngine;

namespace VoxtaBridge
{
    /// <summary>
    /// Proof of concept (2026-11-18): can the server move the ship a player is piloting, like RE2's micro jump drive
    /// does on the client (its dialog calls LocalPlayer.Teleport(pos + forward * distance))?
    /// POST /debug/jump  player=SteamID  distance=5000  method=info|position|player|legacy
    ///   info     — report only (ship, forward, target, obstruction, Pentaxid); call again after a jump to see where things ended up
    ///   position — PF: ship IEntity.Position = target
    ///   player   — PF: pilot IPlayer.Teleport(target) (what the dialog does, but server side)
    ///   legacy / legacy-player / legacy-both — dedi: API1 Request_Entity_Teleport for ship / pilot / both (EWA's same-playfield warp)
    ///   changepf / changepf-player — dedi: Request_Entity_ChangePlayfield (ship) / Request_Player_ChangePlayerfield (pilot) into the same playfield
    ///   console  — dedi: Request_ConsoleCommand, cmd template with {ship} {player} {x} {y} {z} {pf}
    ///   forward  — PF: ship IEntity.MoveForward(speed)   (speed=… m/s, default 50)
    ///   move     — PF: ship IEntity.Move(forward * speed)
    ///   stop     — PF: ship IEntity.MoveStop()
    /// No fuel is taken and no cooldown is kept; space only, refuses when anything is within 2 km of the target.
    /// </summary>
    public partial class BridgeMod
    {
        const float JumpClearance = 2000f;

        static readonly string[] PfJumpMethods = { "info", "position", "player", "forward", "move", "stop" };
        static readonly string[] DediJumpMethods = { "legacy", "legacy-player", "legacy-both", "changepf", "changepf-player", "console" };

        static (int, string) HandleDebugJump(string body)
        {
            var f = Form.Parse(body);
            var method = (f.Get("method") ?? "info").ToLowerInvariant();
            var dedi = DediJumpMethods.Contains(method);
            if (!dedi && !PfJumpMethods.Contains(method))
                return (400, Error("method must be one of " + string.Join(", ", PfJumpMethods.Concat(DediJumpMethods))));
            var distance = f.Get("distance") ?? "5000";

            var (code, json, player) = ForwardToPlayfield("jump", f.Get("player"), ("method", dedi ? "info" : method),
                ("distance", distance), ("speed", f.Get("speed") ?? "50"),
                ("aim", f.Get("aim") ?? "0"), ("standoff", f.Get("standoff") ?? ""), ("buffer", f.Get("buffer") ?? ""), ("maxangle", f.Get("maxangle") ?? "10"), ("aimfrom", f.Get("aimfrom") ?? "ship"), ("aimonly", f.Get("aimonly") ?? ""), ("requiredrive", f.Get("requiredrive") ?? "1"));
            if (!dedi || code != 200) return (code, json);

            // dedi methods: the PF worked out ship and target, the dedi sends API1 requests (as EWA does)
            var ok = Regex.Match(json, "\"canJump\":true").Success;
            var id = Regex.Match(json, "\"shipId\":(\\d+)");
            var t = Regex.Match(json, "\"target\":\\[([-0-9.eE]+),([-0-9.eE]+),([-0-9.eE]+)\\]");
            var r = Regex.Match(json, "\"rotation\":\\[([-0-9.eE]+),([-0-9.eE]+),([-0-9.eE]+)\\]");
            var pfm = Regex.Match(json, "\"playfield\":\"([^\"]*)\"");
            if (!ok || !id.Success || !t.Success || !r.Success || !pfm.Success)
                return (409, new Json().Obj().Prop("ok", false).Prop("error", "PF says no jump (see info)").Key("info").Raw(json).EndObj().ToString());

            float F(Match m, int g) => float.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            var shipId = int.Parse(id.Groups[1].Value);
            var pos = new PVector3(F(t, 1), F(t, 2), F(t, 3));
            var rot = new PVector3(F(r, 1), F(r, 2), F(r, 3));
            var pf = pfm.Groups[1].Value;
            // changepf-player with topf=<playfield> and pos=x,y,z: cross-sector teleport (debug, e.g. to measure planet radii)
            if (!string.IsNullOrEmpty(f.Get("topf"))) pf = f.Get("topf");
            var posArg = (f.Get("pos") ?? "").Split(',');
            if (posArg.Length == 3 && posArg.All(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                pos = new PVector3(float.Parse(posArg[0], CultureInfo.InvariantCulture), float.Parse(posArg[1], CultureInfo.InvariantCulture), float.Parse(posArg[2], CultureInfo.InvariantCulture));
            var steps = new Json().Arr();
            void Send(string what, CmdId cmd, object payload)
            {
                var (sent, evt, data) = Api1(cmd, payload, true);
                Log($"debug jump {method}: {what} {cmd} → {pos.x:F0},{pos.y:F0},{pos.z:F0}: {evt} {Describe(data)}");
                steps.Obj().Prop("what", what).Prop("cmd", cmd.ToString()).Prop("ok", sent).Prop("event", evt.ToString()).Prop("detail", Describe(data)).EndObj();
            }

            switch (method)
            {
                case "legacy": Send("ship", CmdId.Request_Entity_Teleport, new IdPositionRotation(shipId, pos, rot)); break;
                case "legacy-player": Send("player", CmdId.Request_Entity_Teleport, new IdPositionRotation(player.EntityId, pos, rot)); break;
                case "legacy-both":
                    Send("ship", CmdId.Request_Entity_Teleport, new IdPositionRotation(shipId, pos, rot));
                    Send("player", CmdId.Request_Entity_Teleport, new IdPositionRotation(player.EntityId, pos, rot));
                    break;
                case "changepf": Send("ship", CmdId.Request_Entity_ChangePlayfield, new IdPlayfieldPositionRotation(shipId, pf, pos, rot)); break;
                case "changepf-player": Send("player", CmdId.Request_Player_ChangePlayerfield, new IdPlayfieldPositionRotation(player.EntityId, pf, pos, rot)); break;
                case "console":
                    // cmd template with {ship} {player} {x} {y} {z} {pf}, e.g. "teleport {player} {x} {y} {z}"
                    var cmd = (f.Get("cmd") ?? "").Replace("{ship}", shipId.ToString()).Replace("{player}", player.EntityId.ToString())
                        .Replace("{x}", pos.x.ToString("F0", CultureInfo.InvariantCulture)).Replace("{y}", pos.y.ToString("F0", CultureInfo.InvariantCulture))
                        .Replace("{z}", pos.z.ToString("F0", CultureInfo.InvariantCulture)).Replace("{pf}", pf);
                    if (cmd.Length == 0) return (400, Error("console needs cmd=…"));
                    Send(cmd, CmdId.Request_ConsoleCommand, new PString(cmd));
                    break;
            }
            return (200, new Json().Obj().Prop("method", method).Key("steps").Raw(steps.EndArr().ToString()).Key("before").Raw(json).EndObj().ToString());
        }

        sealed class Cand
        {
            public string Kind, Name, Type, RadiusSource, Class;
            public long Id;
            public Vector3 P;
            public float? Radius;
        }

        /// <summary>Planet/moon radius in space from the size class: 3 → 1955 m, 4 → 3916 m measured (HUD), doubles per class.</summary>
        static float PlanetRadius(int sizeClass) => JumpRules.PlanetRadius(sizeClass);

        /// <summary>
        /// Structures: half the diagonal of the block bounding box (2 m blocks, 0.5 m on SV/HV). Asteroids: unknown
        /// (to be measured; flat 2 km for now). null = unknown.
        /// </summary>
        static float? EstimateRadius(IEntity e, out string source) => EstimateRadius(e, null, out source);

        static float? EstimateRadius(IEntity e, string playfield, out string source)
        {
            if (e.Type == EntityType.AstVoxel || e.Type == EntityType.Asteroid || e.Type == EntityType.AstRes || (e.Name ?? "").Contains("Asteroid"))
            {
                source = "asteroid (unmeasured, 2 km)";
                return null;
            }
            var dims = StructureDims(e);
            if (dims.HasValue)
            {
                var (dx, dy, dz) = dims.Value;
                if (playfield != null) RememberSize(playfield, e, dx, dy, dz);
                source = $"bounds {dx:F0}×{dy:F0}×{dz:F0} m ({e.Type})";
                return 0.5f * Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            var mem = RememberedSize(e.Id);
            if (mem != null)
            {
                var age = DateTime.UtcNow - mem.SeenUtc;
                source = $"remembered {mem.Dx:F0}×{mem.Dy:F0}×{mem.Dz:F0} m, seen {(age.TotalHours >= 1 ? $"{age.TotalHours:F0} h" : $"{Math.Max(1, age.TotalMinutes):F0} min")} ago ({e.Type})";
                return mem.Radius;
            }
            source = $"unknown ({e.Type}, no structure loaded, never seen loaded)";
            return null;
        }

        /// <summary>Bounding box in metres while the structure is loaded (2 m blocks, 0.5 m on SV/HV); null for proxies.</summary>
        static (float dx, float dy, float dz)? StructureDims(IEntity e)
        {
            IStructure s = null;
            try { s = e.Structure; } catch { }
            if (s == null) return null;
            try
            {
                var bs = e.Type == EntityType.SV || e.Type == EntityType.HV ? 0.5f : 2f;
                return ((s.MaxPos.x - s.MinPos.x + 1) * bs, (s.MaxPos.y - s.MinPos.y + 1) * bs, (s.MaxPos.z - s.MinPos.z + 1) * bs);
            }
            catch { return null; }
        }

        // ----- size memory (PF) -------------------------------------------------------------------

        static readonly SizeMemory sizes = new SizeMemory();
        static readonly HashSet<int> sizeMisses = new HashSet<int>(); // looked up in the store, not there (this session)
        static DateTime nextSizeScan = DateTime.MinValue;

        static string SizeDb => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "VoxtaBridge.db");

        /// <summary>Memory first, then the shared store (another playfield server may have seen it). PF main thread.</summary>
        static SizeMemory.Entry RememberedSize(int id)
        {
            var e = sizes.Get(id);
            if (e != null || sizeMisses.Contains(id)) return e;
            try
            {
                e = SizeStore.Read(SizeDb, id);
                if (e != null) sizes.Loaded(e); else sizeMisses.Add(id);
            }
            catch (Exception ex) { Log($"size store read failed: {ex.Message}"); }
            return e;
        }

        static void RememberSize(string playfield, IEntity e, float dx, float dy, float dz)
        {
            if (e.Type != EntityType.CV && e.Type != EntityType.SV && e.Type != EntityType.HV && e.Type != EntityType.BA) return;
            sizes.Record(e.Id, e.Name, playfield, dx, dy, dz, DateTime.UtcNow);
            sizeMisses.Remove(e.Id);
        }

        /// <summary>PF main thread, every 30 s: note the size of every loaded structure, save what changed.</summary>
        static void ScanSizes(IEnumerable<IPlayfield> loaded, DateTime now)
        {
            if (now < nextSizeScan) return;
            nextSizeScan = now.AddSeconds(30);
            foreach (var pf in loaded)
            {
                try
                {
                    foreach (var e in pf.Entities.Values)
                    {
                        if (e.Type != EntityType.CV && e.Type != EntityType.SV && e.Type != EntityType.HV && e.Type != EntityType.BA) continue;
                        var dims = StructureDims(e);
                        if (dims.HasValue) RememberSize(pf.Name, e, dims.Value.dx, dims.Value.dy, dims.Value.dz);
                    }
                }
                catch (Exception ex) { Log($"size scan {pf.Name} failed: {ex.Message}"); }
            }
            if (!sizes.Dirty) return;
            var pending = sizes.Pending();
            try
            {
                SizeStore.Write(SizeDb, pending);
                sizes.Saved(pending);
            }
            catch (Exception ex) { Log($"size store write failed ({pending.Count} pending): {ex.Message}"); }
        }

        static readonly string[] JumpDriveBlocks = { "Eden_PrototypeMicroJumpDriveSV", "Eden_PrototypeMicroJumpDriveCV" };

        static readonly BlockFinder driveFinder = new BlockFinder();
        const long DriveScanMaxBlocks = 2_000_000; // bounding-box positions; measured 41,580 in 2 ms (a large carrier), so ~100 ms at the cap, once per ship

        /// <summary>
        /// Is a Prototype Micro Jump Drive (RE2, MaxCount 1) on the ship? It is not in the Container device list,
        /// so: 1) named devices (control panel names, the way RE2's own dialog finds the Pentaxid
        /// tank), 2) a full block scan, capped; remembered per structure (<see cref="BlockFinder"/>). Pilot's ship = always loaded.
        /// </summary>
        static string FindJumpDrive(IStructure s, out string diag)
        {
            diag = null;
            if (s == null) { diag = "structure not loaded"; return null; }
            var ids = new HashSet<int>();
            lock (itemNames)
                foreach (var kv in itemNames)
                    if (JumpDriveBlocks.Contains(kv.Value)) ids.Add(kv.Key);
            if (ids.Count == 0) { diag = "drive blocks not in the item mapping (not an RE2 scenario?)"; return null; }

            string searchDiag = "";
            var pos = driveFinder.Find(s, ids.Contains, () => SearchJumpDrive(s, ids, out searchDiag), out var how);
            diag = how == "searched" ? searchDiag : how;
            return pos.HasValue ? ItemName(BlockFinder.TypeAt(s, pos.Value)) : null;
        }

        static VectorInt3? SearchJumpDrive(IStructure s, HashSet<int> ids, out string diag)
        {
            diag = "";
            int TypeAt(int x, int y, int z) => BlockFinder.TypeAt(s, new VectorInt3(x, y, z));

            // 1) named devices
            var named = 0;
            try
            {
                foreach (var name in s.GetAllCustomDeviceNames() ?? new string[0])
                    foreach (var p in s.GetDevicePositions(name) ?? new List<VectorInt3>())
                    {
                        named++;
                        foreach (var dy in new[] { 0, 128 })
                        {
                            if (!ids.Contains(TypeAt(p.x, p.y + dy, p.z))) continue;
                            diag = $"named device '{name}'" + (dy != 0 ? " (+128 y)" : "");
                            return new VectorInt3(p.x, p.y + dy, p.z);
                        }
                    }
            }
            catch (Exception e) { diag = $"named devices failed: {e.Message}; "; }

            // 2) full scan, capped
            var min = s.MinPos; var max = s.MaxPos;
            long volume = (long)(max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1);
            if (volume > DriveScanMaxBlocks)
            {
                diag += $"{named} named device position(s), no drive; ship too large for a block scan ({volume} positions)";
                return null;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        if (!ids.Contains(TypeAt(x, y + 128, z))) continue; // bounding box has no +128 y offset, blocks do (see Inside())
                        diag += $"{named} named device position(s); block scan found it ({volume} positions, {sw.ElapsedMilliseconds} ms)";
                        Log($"drive check {s.Entity?.Name}: {diag}");
                        return new VectorInt3(x, y + 128, z);
                    }
            diag += $"{named} named device position(s), no drive; block scan {volume} positions in {sw.ElapsedMilliseconds} ms, no drive";
            Log($"drive check {s.Entity?.Name}: {diag}");
            return null;
        }

        static string ExecuteJump(System.Collections.Generic.Dictionary<string, string> req)
        {
            var pfName = req.Get("playfield");
            var pf = playfields.FirstOrDefault(x => x.Name == pfName);
            if (pf == null) return Error($"The playfield {pfName} is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var method = req.Get("method") ?? "info";
            if (!float.TryParse(req.Get("distance"), NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)) distance = 5000;
            distance = Mathf.Sign(distance) * Mathf.Clamp(Mathf.Abs(distance), 100, 50000); // negative = backwards (debug sampling)

            var j = new Json().Obj().Prop("method", method).Prop("playfield", pf.Name)
                .Prop("playfieldType", pf.PlayfieldType).Prop("pvp", pf.IsPvP);

            bool isPilot = false;
            IEntity driving = null;
            IStructure current = null;
            try { isPilot = player.IsPilot; } catch (Exception e) { j.Prop("isPilotError", e.Message); }
            try { driving = player.DrivingEntity; } catch (Exception e) { j.Prop("drivingError", e.Message); }
            try { current = player.CurrentStructure; } catch (Exception e) { j.Prop("currentError", e.Message); }
            var pp = player.Position;
            j.Prop("isPilot", isPilot).Vec("playerPos", pp.x, pp.y, pp.z);
            if (current?.Entity != null) j.Prop("currentStructureId", current.Entity.Id);

            // the ship: what the player drives; fall back to the structure they are in (DrivingEntity may be client-only)
            var ship = driving ?? current?.Entity;
            j.Prop("shipSource", driving != null ? "DrivingEntity" : ship != null ? "CurrentStructure" : "none");
            if (ship == null) return j.Prop("canJump", false).Prop("why", "not in a ship").EndObj().ToString();

            var pos = ship.Position;
            var fwd = ship.Forward;
            var rot = ship.Rotation.eulerAngles;
            j.Prop("shipId", ship.Id).Prop("shipName", ship.Name).Prop("shipType", ship.Type.ToString())
                .Vec("shipPos", pos.x, pos.y, pos.z).Vec("forward", fwd.x, fwd.y, fwd.z).Vec("rotation", rot.x, rot.y, rot.z);
            if (ship.DockedTo > 0) j.Prop("dockedTo", ship.DockedTo); // -1 = not docked
            try
            {
                var tank = ship.Structure?.PentaxidTank;
                if (tank != null) j.Prop("pentaxid", tank.Content).Prop("pentaxidCapacity", tank.Capacity);
            }
            catch (Exception e) { j.Prop("pentaxidError", e.Message); }

            // crew: players near the ship (to see whether they come along)
            j.Key("players").Arr();
            foreach (var p in pf.Players.Values)
                j.Obj().Prop("name", p.Name).Prop("id", p.Id).Prop("distanceToShip", Math.Round(Vector3.Distance(p.Position, pos))).EndObj();
            j.EndArr();

            // aim=1: jump to the object closest to where the ship (or the player, aimfrom=player) points, stopping
            // `standoff` metres short of it. Candidates are listed so we can see what the server thinks is "ahead".
            var pfwd = player.Forward;
            j.Vec("playerForward", pfwd.x, pfwd.y, pfwd.z);
            int aimedId = 0;
            if (req.Get("aim") == "1")
            {
                float P(string k, float d) => float.TryParse(req.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
                var standoff = P("standoff", 2500); // explicit standoff= overrides the radius rule
                var maxAngle = P("maxangle", 10);
                var dir = (req.Get("aimfrom") == "player" ? pfwd : fwd).normalized;
                // candidates: structures/objects (never docked ones, never players), the player's HUD waypoints,
                // and the sector's planets/moons. Each gets a radius estimate so the jump stops radius + buffer short.
                var buffer = P("buffer", 500);
                var cands = pf.Entities.Values
                    .Where(e => e.Id != ship.Id && e.DockedTo <= 0 && e.Type != EntityType.Player)
                    .Select(e => { var r = EstimateRadius(e, pf.Name, out var src); return new Cand { Kind = "entity", Id = e.Id, Name = e.Name, Type = e.Type.ToString(), P = e.Position, Radius = r, RadiusSource = src }; })
                    .ToList();
                string db = null;
                try
                {
                    string save = null;
                    try { save = api.Application.GetPathFor(AppFolder.SaveGame); } catch { }
                    db = Waypoints.FindDb(save);
                    if (db != null)
                    {
                        int fac = 0;
                        try { fac = player.Faction.Id; } catch { }
                        foreach (var m in Waypoints.For(db, player.Id, fac, pf.Name))
                            cands.Add(new Cand { Kind = "waypoint", Id = m.Id, Name = m.Name, Type = $"bookmark type {m.Type}{(m.Temporary ? ", temporary" : "")}", P = new Vector3(m.X, m.Y, m.Z), RadiusSource = "point" , Radius = 0 });
                        foreach (var b in Waypoints.Bodies(db, pf.Name))
                            cands.Add(new Cand { Kind = b.Kind == 4 ? "moon" : "planet", Id = -1, Name = b.Name, Type = $"size class {b.Size}", P = new Vector3(b.X, b.Y, b.Z),
                                Radius = PlanetRadius(b.Size), RadiusSource = "size class (2 readings: 3 → 1955 m, 4 → 3916 m)" });
                    }
                }
                catch (Exception e) { j.Prop("dbError", e.Message); }

                // angle to the object's edge, not its centre: a carrier 140 m away covers ±27° of the view
                // (seen 2026-11-18: a carrier at 6° lost to a wreck at 4° 16 km away). Unknown radius = point.
                var ranked = cands.Select(c =>
                    {
                        var d = Vector3.Distance(pos, c.P);
                        var centre = Vector3.Angle(dir, c.P - pos);
                        var half = c.Radius.HasValue && d > c.Radius.Value ? Mathf.Asin(c.Radius.Value / d) * Mathf.Rad2Deg : c.Radius.HasValue ? 90f : 0f;
                        return (c, d, a: Mathf.Max(0, centre - half), centre);
                    })
                    .Where(x => x.d > 1).OrderBy(x => x.a).ThenBy(x => x.d).ToList();
                Dictionary<long, string> bps = null;
                try { if (db != null) bps = Waypoints.Blueprints(db, ranked.Take(8).Where(x => x.c.Kind == "entity").Select(x => x.c.Id)); } catch { }
                string Bp(Cand c) => c.Kind == "entity" && bps != null && bps.TryGetValue(c.Id, out var b) ? b : null;
                j.Key("aim").Arr();
                foreach (var x in ranked.Take(8))
                {
                    j.Obj().Prop("kind", x.c.Kind).Prop("id", x.c.Id).Prop("name", x.c.Name).Prop("type", x.c.Type)
                        .Prop("angle", Math.Round(x.a, 1)).Prop("centreAngle", Math.Round(x.centre, 1)).Prop("distance", Math.Round(x.d)).Prop("radiusSource", x.c.RadiusSource);
                    if (x.c.Radius.HasValue) j.Prop("radius", Math.Round(x.c.Radius.Value));
                    if (Bp(x.c) != null) j.Prop("blueprint", Bp(x.c));
                    j.EndObj();
                }
                j.EndArr();
                j.Key("waypoints").Arr();
                foreach (var x in ranked.Where(x => x.c.Kind == "waypoint"))
                    j.Obj().Prop("name", x.c.Name).Prop("type", x.c.Type).Prop("angle", Math.Round(x.a, 1)).Prop("distance", Math.Round(x.d)).EndObj();
                j.EndArr();

                var only = req.Get("aimonly"); // "waypoint" = only HUD waypoints count
                var found = ranked.Any(x => x.a <= maxAngle && (only != "waypoint" || x.c.Kind == "waypoint"));
                if (!found)
                    return j.Prop("canJump", false).Prop("why", $"nothing within {maxAngle}° of where the ship points").EndObj().ToString();
                var pick = ranked.First(x => x.a <= maxAngle && (only != "waypoint" || x.c.Kind == "waypoint"));
                // stop short: radius + buffer when known; asteroids and unknown sizes: flat 2 km from the centre
                var stop = !string.IsNullOrEmpty(req.Get("standoff")) ? standoff
                    : pick.c.Radius.HasValue ? pick.c.Radius.Value + buffer : JumpClearance;
                if (pick.d <= stop + 100)
                    return j.Prop("canJump", false).Prop("why", $"{pick.c.Name} is already within {pick.d:F0} m").EndObj().ToString();
                var toward = (pick.c.P - pos).normalized;
                var aimTarget = pick.c.P - toward * stop;
                distance = Vector3.Distance(pos, aimTarget);
                fwd = toward; // so target below lands on the aimed point
                aimedId = pick.c.Kind == "entity" ? (int)pick.c.Id : 0;
                j.Prop("aimPick", pick.c.Name).Prop("aimPickKind", pick.c.Kind).Prop("aimPickId", pick.c.Id)
                    .Prop("stopAt", Math.Round(stop)).Prop("radiusSource", pick.c.RadiusSource);
                if (pick.c.Radius.HasValue) j.Prop("radius", Math.Round(pick.c.Radius.Value));
                if (!pick.c.Radius.HasValue && method != "info")
                {
                    // data over time: every jump to an object of unknown size is a sample; the player reads the HUD distance at arrival
                    var line = $"size sample: {pick.c.Name} ({pick.c.Type}, blueprint {Bp(pick.c) ?? "?"}, id {pick.c.Id}) — arrival {stop:F0} m from its centre; " +
                               $"HUD distance at arrival → radius = {stop:F0} − HUD";
                    Log(line);
                    j.Prop("measure", line);
                }
            }

            var target = pos + fwd.normalized * distance;
            j.Prop("distance", distance).Vec("target", target.x, target.y, target.z);

            string why = null;
            if (pf.PlayfieldType != "Space") why = "not in space";
            else if (ship.Type != EntityType.SV && ship.Type != EntityType.CV) why = $"ship type {ship.Type}";
            else if (ship.DockedTo > 0) why = "ship is docked";
            if (why == null && req.Get("requiredrive") != "0")
            {
                // like RE2's dialog (opened from the drive block), the ship must carry a micro jump drive
                var drive = FindJumpDrive(ship.Structure, out var diag);
                j.Key("jumpDrive").Obj().Prop("found", drive != null).Prop("check", diag);
                if (drive != null) j.Prop("block", drive);
                j.EndObj();
                if (drive == null) why = "the ship has no Prototype Micro Jump Drive";
            }
            if (why == null)
            {
                // others near the target: radius + 500 m when known, else 2 km; the aimed object itself is the destination
                var block = pf.Entities.Values
                    .Where(e => e.Id != ship.Id && e.DockedTo != ship.Id && e.Id != aimedId && e.DockedTo != aimedId && e.Type != EntityType.Player)
                    .Select(e => (e, d: Vector3.Distance(target, e.Position), r: EstimateRadius(e, pf.Name, out _)))
                    .Where(x => x.d < (x.r.HasValue ? x.r.Value + 500 : JumpClearance)).OrderBy(x => x.d).FirstOrDefault();
                if (block.e != null) why = $"{block.e.Name} ({block.e.Type}) is {block.d:F0} m from the target";
            }
            j.Prop("canJump", why == null);
            if (why != null) j.Prop("why", why);
            if (method == "forward" || method == "move" || method == "stop")
            {
                if (!float.TryParse(req.Get("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)) speed = 50;
                if (method == "forward") ship.MoveForward(speed);
                else if (method == "move") ship.Move(fwd.normalized * speed);
                else ship.MoveStop();
                Log($"debug jump {method}: {ship.Name} ({ship.Id}) speed {speed}");
                return j.Prop("done", true).Prop("speed", speed).EndObj().ToString();
            }
            if (why != null || method == "info") return j.EndObj().ToString();

            bool done;
            if (method == "position") { ship.Position = target; done = true; }
            else done = player.Teleport(target);
            var after = ship.Position;
            Log($"debug jump {method}: {ship.Name} ({ship.Id}) {distance:F0} m → {done}; position now {after.x:F0},{after.y:F0},{after.z:F0}");
            return j.Prop("done", done).Vec("shipPosAfter", after.x, after.y, after.z)
                .Prop("movedMeters", Math.Round(Vector3.Distance(pos, after))).EndObj().ToString();
        }
    }
}
