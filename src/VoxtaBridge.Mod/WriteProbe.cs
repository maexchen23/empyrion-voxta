using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Write probe (DevWriteTools=true in VoxtaBridge.cfg; development only, not part of the normal game). Playfield server, every snapshot: if
    /// VoxtaBridge.probe.cmd.txt exists next to the DLL, it is read, deleted and each line run once against the
    /// structure the (first) player in this playfield is in. Results go to the probe log. Commands:
    ///   signal &lt;name&gt; on|off       set any signal by name through the game's own signal logic (reflection, see below)
    ///   switch &lt;name&gt; on|off       IBlock.SetSwitchState on the block that sends the block signal &lt;name&gt;
    ///   panelswitch &lt;name&gt; on|off  IBlock.SetSwitchState on the block position of control panel signal &lt;name&gt;
    ///   light &lt;n&gt; on|off          IBlock.Set(active) on the n-th light (GetDevices(Light))
    ///   device &lt;name&gt; on|off       IBlock.Set(active) on every block of the named device / device group
    /// "signal": the mod API can only read signals. The game's control panel sets one with a method on the structure's
    /// signal logic object: void (string name, bool on, &lt;playfield&gt;, &lt;player entity&gt;). The API wrappers expose the
    /// game objects through a public "Entity" property; the method and fields are found by their types, because the
    /// game's names are obfuscated and may change with updates.
    /// </summary>
    public partial class BridgeMod
    {
        static string ProbeCmdFile => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "VoxtaBridge.probe.cmd.txt");

        static void ProbeCommands(IPlayfield pf)
        {
            if (config?.DevWriteTools != true || mode == ApplicationMode.DedicatedServer) return;
            var file = ProbeCmdFile;
            if (!File.Exists(file)) return;
            var player = pf.Players.Values.FirstOrDefault(p => p.CurrentStructure != null);
            if (player == null) return; // leave the file for the playfield the player is in
            string[] lines;
            try { lines = File.ReadAllLines(file); File.Delete(file); }
            catch (Exception e) { ProbeLine($"cmd: read failed: {e.Message}"); return; }
            var s = player.CurrentStructure;
            foreach (var raw in lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")))
            {
                string result;
                try { result = RunProbeCommand(raw, pf, player, s); }
                catch (Exception e) { result = $"{e.GetType().Name}: {e.Message}"; }
                bool? st = null;
                ProbeLine($"cmd [{raw}] on #{s.Id} '{s.Entity?.Name}': {result}");
                var name = ArgName(raw);
                if (name != null) { try { st = s.GetSignalState(name); } catch { } ProbeLine($"cmd   signal '{name}' state now {st?.ToString() ?? "?"}"); }
            }
        }

        static string ArgName(string raw)
        {
            var parts = raw.Split(' ');
            if (parts.Length < 3 || (parts[0] != "signal" && parts[0] != "switch" && parts[0] != "panelswitch")) return null;
            return string.Join(" ", parts.Skip(1).Take(parts.Length - 2));
        }

        static string RunProbeCommand(string raw, IPlayfield pf, IPlayer player, IStructure s)
        {
            if (raw.Trim() == "ground") return ProbeGround(pf, s);
            var parts = raw.Split(' ');
            if (parts.Length < 3) return "usage: <cmd> <name> on|off";
            if (parts[0] == "lightcolor")
            {
                // lightcolor <name> r,g,b (0..1 each): ILight.SetColor on every block of the named device
                var rgb = parts[parts.Length - 1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var lname = string.Join(" ", parts.Skip(1).Take(parts.Length - 2));
                var lpos = s.GetDevicePositions(lname);
                if (lpos == null || lpos.Count == 0) return "no such device name";
                return string.Join("; ", lpos.Select(p =>
                {
                    var l = s.GetDevice<ILight>(p);
                    if (l == null) return $"({p.x},{p.y},{p.z}) not a light";
                    var before = l.GetColor();
                    l.SetColor(new UnityEngine.Color(rgb[0], rgb[1], rgb[2]));
                    return $"({p.x},{p.y},{p.z}) colour {before} -> {l.GetColor()}";
                }));
            }
            var on = parts[parts.Length - 1] == "on";
            var name = string.Join(" ", parts.Skip(1).Take(parts.Length - 2));
            switch (parts[0])
            {
                case "signal": return SetSignalViaGame(s, player, name, on);
                case "switch":
                case "panelswitch":
                {
                    var list = parts[0] == "switch" ? s.GetBlockSignals(null) : s.GetControlPanelSignals();
                    var hit = list.Where(x => x.Name == name).ToList();
                    if (hit.Count == 0) return "no such signal";
                    var outp = new List<string>();
                    foreach (var x in hit)
                    {
                        if (!x.BlockPos.HasValue) { outp.Add("no block position"); continue; }
                        var b = s.GetBlock(x.BlockPos.Value);
                        var before = b?.GetSwitchState(x.Index);
                        var ret = b?.SetSwitchState(on, x.Index);
                        outp.Add($"block ({x.BlockPos.Value.x},{x.BlockPos.Value.y},{x.BlockPos.Value.z}) idx {x.Index}: switch {Fmt(before)} -> SetSwitchState returned {Fmt(ret)}, now {Fmt(b?.GetSwitchState(x.Index))}");
                    }
                    return string.Join("; ", outp);
                }
                case "light":
                {
                    var lights = s.GetDevices(DeviceTypeName.Light);
                    if (!int.TryParse(name, out var n) || n < 0 || n >= lights.Count) return $"light index 0..{lights.Count - 1}";
                    return SetActive(s, lights.GetAt(n), on);
                }
                case "device":
                {
                    var pos = s.GetDevicePositions(name);
                    if (pos == null || pos.Count == 0) return "no such device name";
                    return string.Join("; ", pos.Select(p => SetActive(s, p, on)));
                }
            }
            return "unknown command";
        }

        /// <summary>
        /// ground (read-only): is the ship landed or hovering? Playfield type, every landing gear block (active flag,
        /// switch state), and the lowest corner of the ship's block box against the terrain height below it.
        /// </summary>
        static string ProbeGround(IPlayfield pf, IStructure s)
        {
            var outp = new List<string> { $"playfield {pf.Name} type {pf.PlayfieldType} planet {pf.PlanetType}" };
            var lists = GameInternals.DeviceLists(s, true, out var err);
            if (lists == null) outp.Add("device lists: " + err);
            else
            {
                var gears = new List<string>();
                foreach (var l in lists)
                    for (var i = 0; i < l.Count; i++)
                    {
                        var p = l.GetAt(i);
                        var b = s.GetBlock(p);
                        if (b == null) continue;
                        b.Get(out var type, out _, out _, out var active);
                        var name = ItemName(type) ?? type.ToString();
                        if (name.IndexOf("landinggear", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        bool? sw = null;
                        try { sw = b.GetSwitchState(0); } catch { }
                        gears.Add($"{name}@({p.x},{p.y},{p.z}) active {active} switch {Fmt(sw)}");
                    }
                outp.Add($"{gears.Count} landing gear: " + string.Join("; ", gears.Take(12)));
            }
            try
            {
                var mn = s.MinPos; var mx = s.MaxPos;
                var lowest = float.MaxValue; UnityEngine.Vector3 low = default;
                foreach (var x in new[] { mn.x, mx.x })
                    foreach (var y in new[] { mn.y, mx.y })
                        foreach (var z in new[] { mn.z, mx.z })
                        {
                            var g = s.StructToGlobalPos(new VectorInt3(x, y, z));
                            if (g.y < lowest) { lowest = g.y; low = g; }
                        }
                var pos = s.Entity.Position;
                var tCentre = pf.GetTerrainHeightAt(pos.x, pos.z);
                var tLow = pf.GetTerrainHeightAt(low.x, low.z);
                outp.Add($"entity y {pos.y:F1}, lowest corner y {lowest:F1}, terrain below centre {tCentre:F1}, below lowest corner {tLow:F1}, clearance {lowest - tLow:F1} m");
            }
            catch (Exception e) { outp.Add("height: " + e.Message); }
            return string.Join(" | ", outp);
        }

        static string Fmt(bool? b) => b?.ToString() ?? "null";

        static string SetActive(IStructure s, VectorInt3 p, bool on)
        {
            var b = s.GetBlock(p);
            if (b == null) return $"({p.x},{p.y},{p.z}) no block";
            b.Get(out var type, out _, out _, out var before);
            // all four arguments explicit: 'active' defaults to false
            b.Set(null, null, null, on);
            b.Get(out _, out _, out _, out var after);
            return $"({p.x},{p.y},{p.z}) type {type}: active {before} -> {after}";
        }

        /// <summary>Set a signal by name the way the control panel does (reflection into the game; see class summary).</summary>
        static string SetSignalViaGame(IStructure s, IPlayer player, string name, bool on)
        {
            object GameEntity(object wrapper) =>
                wrapper?.GetType().GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance)?.GetValue(wrapper, null);

            var structEntity = GameEntity(s.Entity);   // DomainTable-like: the structure
            var playerEntity = GameEntity(player);     // the player entity
            if (structEntity == null || playerEntity == null) return $"no game objects (structure {structEntity?.GetType().Name ?? "null"}, player {playerEntity?.GetType().Name ?? "null"})";

            // the structure's signal logic object: a field on the structure type whose type has a method
            // void M(string, bool, X, Y) where the player entity is assignable to Y and the playfield field type is X
            var trail = new List<string>();
            foreach (var t in Hierarchy(structEntity.GetType()))
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var logic = f.GetValue(structEntity);
                    if (logic == null) continue;
                    foreach (var m in logic.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        var ps = m.GetParameters();
                        if (m.ReturnType != typeof(void) || ps.Length != 4 || ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(bool)) continue;
                        if (!ps[3].ParameterType.IsInstanceOfType(playerEntity) || !ps[3].ParameterType.IsInstanceOfType(structEntity)) continue;
                        // the playfield: a field of the player entity whose type is ps[2]
                        object pane = null;
                        foreach (var pt in Hierarchy(playerEntity.GetType()))
                            foreach (var pfField in pt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                if (pane == null && pfField.FieldType == ps[2].ParameterType) pane = pfField.GetValue(playerEntity);
                        trail.Add($"{t.Name}.{f.Name}:{logic.GetType().Name}.{m.Name}(string,bool,{ps[2].ParameterType.Name},{ps[3].ParameterType.Name}) pane {(pane == null ? "null" : pane.GetType().Name)}");
                        // test build: only invoke the method known from the current game build; report other matches
                        if (pane == null || m.Name != "RebuildSelection") continue;
                        m.Invoke(logic, new[] { name, (object)on, pane, playerEntity });
                        return "invoked " + string.Join(" | ", trail);
                    }
                }
            return "no matching method; candidates: " + (trail.Count == 0 ? "none" : string.Join(" | ", trail));
        }

        static IEnumerable<Type> Hierarchy(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType) yield return t;
        }
    }
}
