using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Eleon.Modding;
using UnityEngine;

namespace VoxtaBridge
{
    /// <summary>
    /// empyrion_microjump: the character jumps the player's ship like RE2's Prototype Micro Jump Drive, either a
    /// distance straight ahead or to what the ship's nose points at (object, HUD waypoint, planet, moon).
    /// Proven 2026-11-18 on the dev server: an API1 Request_Entity_Teleport on the PILOT moves the ship along (moving the
    /// ship entity itself is undone by the pilot's client).
    ///
    ///   POST /microjump  player=SteamID  distance=metres (empty = aim)  confirm=0|1  expect=target name from the plan
    ///   confirm=0: plan only (target, length, cost, checks). confirm=1: re-plans with the aim of this moment, refuses
    ///   if the target changed (expect), takes Refined Pentaxid from the "... VJ" container (suffix from the request, set in Voxta), teleports, refunds if the teleport fails.
    ///
    /// Differences to the drive's dialogue (accepted): Pentaxid only from the VJ container (not the inventory or tank: the tank cannot be lowered via ModApi);
    /// own cooldown (the dialogue's per-player timer isn't readable from the save); stop distance by target size.
    /// </summary>
    public partial class BridgeMod
    {
        static readonly Dictionary<string, DateTime> lastJump = new Dictionary<string, DateTime>(); // SteamID → UTC
        const float AimMaxAngle = 10f;

        // ----- dedi (HTTP thread) ------------------------------------------------------------------

        static (int, string) HandleMicroJump(string body)
        {
            if (!config.MicroJumpEnabled) return (403, Error("Micro jumps are switched off on this server (MicroJumpEnabled=false)."));
            var f = Form.Parse(body);
            var confirm = f.Get("confirm") == "1";
            var vjSuffix = string.IsNullOrWhiteSpace(f.Get("vj")) ? JumpRules.DefaultContainerSuffix : f.Get("vj").Trim();

            DediPlayer who;
            string why0;
            lock (sync) who = PickPlayer(f.Get("player"), out why0);
            if (who == null) return (409, Error(why0));
            DateTime? last;
            lock (lastJump) last = lastJump.TryGetValue(who.SteamId, out var l) ? l : (DateTime?)null;
            var cooldown = JumpRules.CooldownLeft(last, DateTime.UtcNow, config.MicroJumpCooldownSeconds);
            if (confirm && cooldown > 0)
                return (200, new Json().Obj().Prop("ok", false).Prop("cooldownS", cooldown)
                    .Prop("error", $"The micro jump drive is still cooling down: {cooldown} s left. Nothing happened.").EndObj().ToString());

            var fields = new List<(string, string)>
            {
                ("distance", f.Get("distance") ?? ""), ("confirm", confirm ? "1" : "0"), ("expect", f.Get("expect") ?? ""),
                ("min", config.MicroJumpMinMeters.ToString(CultureInfo.InvariantCulture)),
                ("max", config.MicroJumpMaxMeters.ToString(CultureInfo.InvariantCulture)),
                ("requiredrive", config.MicroJumpRequireDrive ? "1" : "0"),
                ("vj", vjSuffix),
                ("hitlock", config.MicroJumpHitLockSeconds.ToString(CultureInfo.InvariantCulture)),
                ("bodyclear", config.MicroJumpBodyClearanceMeters.ToString(CultureInfo.InvariantCulture)),
                ("kmper", config.MicroJumpKmPerPentaxid.ToString(CultureInfo.InvariantCulture)),
            };
            var (code, json, player) = ForwardToPlayfield("microjump", f.Get("player"), fields.ToArray());
            if (code != 200) return (code, json);

            bool Has(string key, string value) => Regex.IsMatch(json, $"\"{key}\":{value}");
            if (!confirm || !Has("paid", "true"))
                return (200, cooldown > 0 ? json.TrimEnd('}') + $",\"cooldownS\":{cooldown}}}" : json);

            // paid: teleport the pilot (the ship follows), refund on failure
            float F(string key, int i) => float.Parse(Regex.Match(json, $"\"{key}\":\\[([-0-9.eE]+),([-0-9.eE]+),([-0-9.eE]+)\\]").Groups[i].Value, CultureInfo.InvariantCulture);
            var pos = new PVector3(F("land", 1), F("land", 2), F("land", 3));
            var rot = new PVector3(F("rotation", 1), F("rotation", 2), F("rotation", 3));
            var (sent, evt, data) = Api1(CmdId.Request_Entity_Teleport, new IdPositionRotation(player.EntityId, pos, rot), true);
            var shipId = Regex.Match(json, "\"shipId\":(\\d+)").Groups[1].Value;
            var cost = Regex.Match(json, "\"cost\":(\\d+)").Groups[1].Value;
            if (!sent)
            {
                var (rc, rjson, _) = ForwardToPlayfield("microjump-refund", f.Get("player"), ("ship", shipId), ("amount", cost), ("vj", vjSuffix));
                Log($"micro jump teleport failed ({evt} {Describe(data)}), refund: {rjson}");
                return (200, new Json().Obj().Prop("ok", false)
                    .Prop("error", $"The jump failed ({Describe(data)}); the {cost} Pentaxid went back into the '{vjSuffix}' container. The ship did not move.")
                    .EndObj().ToString());
            }
            lock (lastJump) lastJump[who.SteamId] = DateTime.UtcNow;
            AuditJump(player, json);
            return (200, json.TrimEnd('}') + ",\"jumped\":true}");
        }

        static void AuditJump(DediPlayer player, string json)
        {
            try
            {
                var line = string.Join("\t", DateTime.UtcNow.ToString("o"), player.SteamId, player.Name, player.Playfield, "microjump", json.Replace('\t', ' ').Replace('\n', ' '));
                var path = Path.Combine(Path.GetDirectoryName(config.Path) ?? ".", "VoxtaBridge.audit.log");
                lock (auditLock) File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
            }
            catch (Exception e) { Log($"audit log write failed: {e.Message}"); }
        }

        // ----- playfield server ----------------------------------------------------------------------

        static string ExecuteMicroJump(Dictionary<string, string> req)
        {
            string Fail(string msg) => new Json().Obj().Prop("ok", false).Prop("error", msg + " Nothing happened.").EndObj().ToString();
            float P(string k, float d) => float.TryParse(req.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

            var pf = playfields.FirstOrDefault(x => x.Name == req.Get("playfield"));
            if (pf == null) return Fail($"The playfield {req.Get("playfield")} is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Fail("The player is not on this playfield right now.");

            // --- where and in what
            if (pf.PlayfieldType != "Space") return Fail("Micro jumps only work in space.");
            if (JumpRules.IsForbiddenPlayfield(pf.Name)) return Fail("Micro jumps don't work here (the M31 core).");
            bool isPilot = false; IEntity ship = null;
            try { isPilot = player.IsPilot; ship = player.DrivingEntity; } catch { }
            if (!isPilot || ship == null) return Fail("The player must be flying the ship (pilot seat).");
            if (ship.Type != EntityType.SV && ship.Type != EntityType.CV) return Fail("Only small and capital vessels can micro jump.");
            if (ship.DockedTo > 0) return Fail("The ship is docked; undock first.");
            var hitLock = JumpRules.CooldownLeft(LastHit(ship.Id), DateTime.UtcNow, (int)P("hitlock", 30));
            if (hitLock > 0)
                return new Json().Obj().Prop("ok", false).Prop("hitLockS", hitLock)
                    .Prop("error", $"The ship is under attack (hit recently): no micro jump for another {hitLock} s. Nothing happened.")
                    .EndObj().ToString();

            // --- RE2 settings, best effort (dialogue vars in global.db; missing = defaults)
            string db = null;
            try { db = Waypoints.FindDb(api.Application.GetPathFor(AppFolder.SaveGame)); } catch { }
            int admin = 0, rangeBonus = 0;
            if (db != null)
            {
                try { admin = Waypoints.DialogueInt(db, "Eden_Admin_MicroJump", 0) ?? 0; } catch { }
                try { rangeBonus = Waypoints.DialogueInt(db, "Eden_MicroJumpBonus", player.Id) ?? 0; } catch { }
            }
            if (admin == 2) return Fail("The admins switched micro jumps off on this server.");
            if (admin == 1 && pf.IsPvP) return Fail("Micro jumps are switched off in PvP sectors on this server.");
            var minLen = P("min", 1000);
            var maxLen = P("max", 25000) + rangeBonus;

            // --- drive
            var structure = ship.Structure;
            if (req.Get("requiredrive") != "0" && FindJumpDrive(structure, out _) == null)
                return Fail("The ship has no Prototype Micro Jump Drive.");

            // --- target
            var pos = ship.Position;
            var fwd = ship.Forward.normalized;
            var j = new Json().Obj();
            string targetName, targetKind, targetClass = null; long targetId = 0; float? targetRadius = null; string radiusSource = null;
            float length; Vector3 dir;
            if (float.TryParse(req.Get("distance"), NumberStyles.Float, CultureInfo.InvariantCulture, out var asked) && asked > 0)
            {
                targetName = "straight ahead"; targetKind = "distance";
                length = asked; dir = fwd;
            }
            else
            {
                var ranked = RankTargets(pf, player, ship, pos, fwd, db);
                if (ranked.Count == 0 || ranked[0].angle > AimMaxAngle)
                {
                    var near = ranked.Take(3).Select(x => $"{x.c.Name} ({x.centre:F0}° off, {JumpRules.Km(x.d)})");
                    return Fail("The ship isn't pointing at anything to jump to (nothing within 10° of the nose)" +
                                (ranked.Count > 0 ? "; closest: " + string.Join(", ", near) + "." : ".") +
                                " Aim the ship at the target or give a distance.");
                }
                var pick = ranked[0];
                targetName = pick.c.Name; targetKind = pick.c.Kind; targetId = pick.c.Id; targetClass = pick.c.Class;
                targetRadius = pick.c.Radius; radiusSource = pick.c.RadiusSource;
                var bodyClearance = P("bodyclear", JumpRules.DefaultBodyClearance);
                var isBody = pick.c.Kind == "planet" || pick.c.Kind == "moon";
                var stop = isBody && pick.c.Radius.HasValue ? JumpRules.BodyStopDistance(pick.c.Radius.Value, bodyClearance) : JumpRules.StopDistance(pick.c.Radius);
                if (pick.d <= stop + 100) return Fail($"{pick.c.Name} is already within {JumpRules.Km(pick.d)}.");
                dir = (pick.c.P - pos).normalized;
                length = pick.d - stop;
                j.Prop("targetDistance", Math.Round(pick.d)).Prop("stopAt", Math.Round(stop));
                if (targetRadius.HasValue) j.Prop("targetRadius", Math.Round(targetRadius.Value));
                if (radiusSource != null) j.Prop("radiusSource", radiusSource);
            }

            var expect = req.Get("expect");
            if (req.Get("confirm") == "1" && !string.IsNullOrEmpty(expect) && !string.Equals(expect.Trim(), targetName, StringComparison.OrdinalIgnoreCase))
                return Fail($"The ship now points at '{targetName}', not '{expect}' as planned. Plan again.");

            var rangeError = JumpRules.CheckRange(length, minLen, maxLen);
            if (rangeError != null) return Fail(rangeError + (targetKind != "distance" ? $" ({targetName})" : ""));

            var land = pos + dir * length;
            // --- landing point clear? (others by radius + 500 m, unknown 2 km; never players, the ship, the target)
            var aimedId = targetKind == "entity" ? (int)targetId : 0;
            var block = pf.Entities.Values
                .Where(e => e.Id != ship.Id && e.DockedTo != ship.Id && e.Id != aimedId && e.DockedTo != aimedId && e.Type != EntityType.Player)
                .Select(e => (e, d: Vector3.Distance(land, e.Position), r: EstimateRadius(e, pf.Name, out _)))
                .Where(x => x.d < (x.r.HasValue ? x.r.Value + JumpRules.Buffer : JumpRules.UnknownClearance))
                .OrderBy(x => x.d).FirstOrDefault();
            if (block.e != null) return Fail($"{block.e.Name} is {JumpRules.Km(block.d)} from the landing point; it isn't clear.");
            foreach (var b in BodiesNear(db, pf.Name))
                if (Vector3.Distance(land, b.p) < JumpRules.BodyStopDistance(b.r, P("bodyclear", JumpRules.DefaultBodyClearance)) - 50) // aimed at a planet lands exactly there
                    return Fail($"The landing point would be too close to {b.name}.");

            // --- cost
            // Paid with refined Pentaxid items from the one container named "... VJ" on the ship (docking group).
            // Not from the Pentaxid tank: IStructureTank.AddContent ignores negative amounts (live test 2026-11-18).
            var kmPer = (double)P("kmper", (float)JumpRules.DefaultKmPerPentaxid);
            var cost = JumpRules.Cost(length, kmPer);
            var suffix = string.IsNullOrWhiteSpace(req.Get("vj")) ? JumpRules.DefaultContainerSuffix : req.Get("vj").Trim();
            var vjs = CollectScope(pf, structure, player, req.Get("ownership") != "0", 0, includeRangeContainers: false, null)
                .Where(c => IsVcName(c.Name, suffix)).ToList();
            if (vjs.Count == 0)
                return Fail($"No jump container: name a container on this ship so it ends with '{suffix}' (e.g. 'Pentaxid {suffix}') and put Refined Pentaxid into it.");
            if (vjs.Count > 1)
                return Fail($"Several containers end with '{suffix}' ({string.Join(", ", vjs.Select(c => c.Label))}); keep exactly one.");
            var vj = vjs[0];
            var pentaxidId = ItemIdOf(JumpRules.PentaxidItem);
            if (pentaxidId == 0) return Fail($"The item {JumpRules.PentaxidItem} is unknown to this game.");
            var have = vj.Count(pentaxidId);
            if (have < cost)
                return Fail($"The jump needs {cost} Refined Pentaxid; '{vj.Label}' has {have}.");

            var rot = ship.Rotation.eulerAngles;
            j.Prop("ok", true).Prop("ship", ship.Name).Prop("shipId", ship.Id).Prop("shipType", ship.Type.ToString())
                .Prop("target", targetName).Prop("targetKind", targetKind).Prop("targetClass", targetClass).Prop("jump", Math.Round(length)).Prop("cost", cost).Prop("kmPerPentaxid", kmPer)
                .Prop("pentaxid", have).Prop("container", vj.Label).Prop("maxRange", Math.Round(maxLen))
                .Vec("land", land.x, land.y, land.z).Vec("rotation", rot.x, rot.y, rot.z);

            if (req.Get("confirm") != "1") return j.Prop("planned", true).EndObj().ToString();

            // --- pay (verify by re-reading; refund handled by the dedi if the teleport fails)
            if (vj.Locked) return Fail($"'{vj.Label}' is open by someone; close it first.");
            try { vj.Remove(pentaxidId, cost); }
            catch (Exception e) { return Fail($"Could not take the Pentaxid from '{vj.Label}' ({e.Message})."); }
            var after = vj.Count(pentaxidId);
            if (after != have - cost)
            {
                if (after < have) try { vj.Add(pentaxidId, have - after); } catch { }
                return Fail($"Could not take the Pentaxid from '{vj.Label}' (had {have}, now {vj.Count(pentaxidId)}).");
            }
            Log($"micro jump: {player.Name} in {ship.Name} → {targetName}, {length:F0} m, {cost} Pentaxid from {vj.Label} ({have} → {after})");
            return j.Prop("paid", true).Prop("pentaxidAfter", after).EndObj().ToString();
        }

        static string ExecuteMicroJumpRefund(Dictionary<string, string> req)
        {
            var pf = playfields.FirstOrDefault(x => x.Name == req.Get("playfield"));
            int.TryParse(req.Get("ship"), out var shipId);
            int.TryParse(req.Get("amount"), out var amount);
            IEntity ship = null;
            if (pf != null) pf.Entities.TryGetValue(shipId, out ship);
            try
            {
                int.TryParse(req.Get("player"), out var playerId);
                var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
                var suffix = string.IsNullOrWhiteSpace(req.Get("vj")) ? JumpRules.DefaultContainerSuffix : req.Get("vj").Trim();
                var vj = CollectScope(pf, ship.Structure, player, false, 0, includeRangeContainers: false, null)
                    .First(c => IsVcName(c.Name, suffix));
                vj.Add(ItemIdOf(JumpRules.PentaxidItem), amount);
                Log($"micro jump refund: {amount} Pentaxid to {vj.Label}");
                return new Json().Obj().Prop("ok", true).EndObj().ToString();
            }
            catch (Exception e)
            {
                Log($"micro jump refund FAILED ({amount} Pentaxid to ship {shipId}): {e.Message}");
                return Error($"refund failed: {e.Message}");
            }
        }

        // ----- targets (shared with /debug/jump's aim) -----------------------------------------------

        /// <summary>
        /// Everything the ship could aim at, best first: objects (not docked ones, not players), the player's HUD
        /// waypoints and the sector's planets/moons (gas giants are not targets). Ranked by the angle to the object's
        /// edge (asin(radius/distance)), nearest first on ties: a carrier 140 m away covers ±27°.
        /// </summary>
        static List<(Cand c, float d, float angle, float centre)> RankTargets(IPlayfield pf, IPlayer player, IEntity ship, Vector3 pos, Vector3 dir, string db)
        {
            var cands = pf.Entities.Values
                .Where(e => e.Id != ship.Id && e.DockedTo <= 0 && e.Type != EntityType.Player)
                .Select(e => { var r = EstimateRadius(e, pf.Name, out var src); return new Cand { Kind = "entity", Id = e.Id, Name = e.Name, Type = e.Type.ToString(), P = e.Position, Radius = r, RadiusSource = src }; })
                .ToList();
            // POI classes: decoration (billboards, deco modules, markers) is never a jump target
            var dropped = 0;
            if (db != null && cands.Count > 0)
            {
                try
                {
                    var bps = Waypoints.Blueprints(db, cands.Select(c => c.Id));
                    var byId = pf.Entities.Values.ToDictionary(e => (long)e.Id);
                    foreach (var c in cands)
                    {
                        bps.TryGetValue(c.Id, out var bp);
                        int faction = -1;
                        try { if (byId.TryGetValue(c.Id, out var e)) faction = (int)e.Faction.Group; } catch { }
                        c.Class = PoiClassifier.Label(ClassifyPoi(bp, c.Name, faction).kind);
                    }
                    dropped = cands.RemoveAll(c => c.Class == PoiClassifier.Label(PoiKind.Decorative));
                }
                catch (Exception e) { Log($"poi classes: {e.Message}"); }
            }
            if (dropped > 0) Log($"aim: left out {dropped} decorative POI(s)");
            if (db != null)
            {
                try
                {
                    int fac = 0;
                    try { fac = player.Faction.Id; } catch { }
                    foreach (var m in Waypoints.For(db, player.Id, fac, pf.Name))
                        cands.Add(new Cand { Kind = "waypoint", Id = m.Id, Name = m.Name, Type = "HUD marker", P = new Vector3(m.X, m.Y, m.Z), Radius = 0, RadiusSource = "point" });
                }
                catch (Exception e) { Log($"waypoints: {e.Message}"); }
                foreach (var b in BodiesNear(db, pf.Name))
                    cands.Add(new Cand { Kind = b.kind, Id = -1, Name = b.name, Type = b.kind, P = b.p, Radius = b.r, RadiusSource = "size class" });
            }
            return cands.Select(c =>
                {
                    var d = Vector3.Distance(pos, c.P);
                    var centre = Vector3.Angle(dir, c.P - pos);
                    var half = c.Radius.HasValue && c.Radius.Value > 0 ? (d > c.Radius.Value ? Mathf.Asin(c.Radius.Value / d) * Mathf.Rad2Deg : 90f) : 0f;
                    return (c, d, angle: Mathf.Max(0, centre - half), centre);
                })
                .Where(x => x.d > 1).OrderBy(x => x.angle).ThenBy(x => x.d).ToList();
        }

        static readonly Dictionary<string, PoiFacts> headerFactsCache = new Dictionary<string, PoiFacts>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// POI class on a playfield server: prefab header facts (read once per prefab from the scenario, else the base
        /// game's Prefabs folder; no traders, which only the dedi's full index knows) plus the live faction; without a
        /// prefab file (AsteroidVoxel…, player-made) by blueprint/entity name.
        /// </summary>
        static (PoiKind kind, string why) ClassifyPoi(string blueprint, string name, int faction)
        {
            PoiFacts facts = null;
            if (!string.IsNullOrEmpty(blueprint))
                lock (headerFactsCache)
                    if (!headerFactsCache.TryGetValue(blueprint, out facts))
                    {
                        foreach (var dir in new[] { scenarioDir, contentDir })
                        {
                            if (string.IsNullOrEmpty(dir)) continue;
                            var file = System.IO.Path.Combine(dir, "Prefabs", blueprint + ".epb");
                            if (!System.IO.File.Exists(file)) continue;
                            try { facts = TraderIndex.HeaderFacts(blueprint, System.IO.File.ReadAllBytes(file)); } catch { }
                            break;
                        }
                        headerFactsCache[blueprint] = facts;
                    }
            if (facts == null) return PoiClassifier.ClassifyByName(blueprint ?? name, name, faction);
            return PoiClassifier.Classify(facts.WithFaction(faction));
        }

        static List<(string name, string kind, Vector3 p, float r)> BodiesNear(string db, string playfield)
        {
            var list = new List<(string, string, Vector3, float)>();
            if (db == null) return list;
            try
            {
                foreach (var b in Waypoints.Bodies(db, playfield))
                    list.Add((b.Name, b.Kind == 4 ? "moon" : "planet", new Vector3(b.X, b.Y, b.Z), JumpRules.PlanetRadius(b.Size)));
            }
            catch (Exception e) { Log($"bodies: {e.Message}"); }
            return list;
        }
    }
}
