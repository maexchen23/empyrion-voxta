using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Playfield server: structure functions the mod API doesn't offer (set a signal, switch main power, shield state and
    /// maximum, shield generator positions), reached through the game objects behind the API wrappers.
    ///
    /// The API's wrappers expose the game object through a public "Entity" property. The game's member names are
    /// obfuscated and may change with any update, so everything is found by shape (parameter and field types), each match
    /// must be unique, and every call fails with a readable error instead of guessing. Main thread only.
    ///
    /// Shapes (game build of 2026-11):
    /// - structure game object: has a field whose type has exactly one method void (string, bool, P, M) where M is a base
    ///   type of the structure and the player game objects; that object is the structure's signal logic, the method sets a
    ///   signal by name like the control panel (P = the playfield, a field of type P on the player's game object);
    /// - player game object: exactly one method bool (bool, &lt;signal logic type&gt;, bool): switch the structure's main power
    ///   like the control panel (the game's own scenario action "StructPower" calls it with throttle = false);
    /// - signal logic: a field whose type has a nested enum "EnumShieldState" (structure data); that type has a field of that
    ///   enum (shield state) and exactly one method int (out int, out int, out int) = shield capacity (out: recharge rate,
    ///   two cooldown values); its fields of the API type IDevicePosList are the structure's device lists by type.
    /// </summary>
    public static class GameInternals
    {
        /// <summary>
        /// Game builds these internals were checked against: SHA-256 of the running process's Assembly-CSharp.dll → label.
        /// Any other file switches the internals off (requests get an error, everything else keeps working) until the mod
        /// is checked and updated for the new game version. AllowUnverifiedGameInternals=true in VoxtaBridge.cfg skips this.
        /// </summary>
        static readonly Dictionary<string, string> KnownGameBuilds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["15eb372cb612c2f791f5d7e4b72d6d44e3b8a7b96c39e7013c5816b106c5c1f4"] = "v1.20.1 build 5232, playfield server",
        };

        static bool? verified;
        static string versionInfo;

        /// <summary>Whether the running game is a build the internals were checked against (computed once, about 50 ms).</summary>
        public static bool Verified(bool allowUnverified, out string info)
        {
            if (verified == null)
            {
                try
                {
                    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                    if (asm == null || string.IsNullOrEmpty(asm.Location)) { verified = false; versionInfo = "game assembly not found"; }
                    else
                    {
                        string hash;
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                        using (var fs = System.IO.File.OpenRead(asm.Location))
                            hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                        string build = null;
                        try
                        {
                            // <game>/<Process>_Data/Managed/Assembly-CSharp.dll; BuildNumber.txt is in <game>
                            var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(asm.Location))));
                            var file = root == null ? null : System.IO.Path.Combine(root, "BuildNumber.txt");
                            if (file != null && System.IO.File.Exists(file)) build = string.Join(" ", System.IO.File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0));
                        }
                        catch { }
                        verified = KnownGameBuilds.TryGetValue(hash, out var label);
                        versionInfo = verified == true ? label : $"game build {build ?? "unknown"} (Assembly-CSharp.dll SHA-256 {hash.Substring(0, 16)}…) not checked yet";
                    }
                }
                catch (Exception e) { verified = false; versionInfo = "version check failed: " + e.Message; }
            }
            info = versionInfo;
            return verified == true || allowUnverified;
        }

        /// <summary>The error text for a request that needs the internals, or null when they may be used.</summary>
        public static string Gate(bool allowUnverified)
        {
            if (Verified(allowUnverified, out var info)) return null;
            return $"This feature uses game functions the mod has not been checked against for this game version ({info}). " +
                   "It stays off until the mod is updated; everything else still works.";
        }

        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static object GameObject(object apiWrapper) =>
            apiWrapper?.GetType().GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance)?.GetValue(apiWrapper, null);

        static IEnumerable<FieldInfo> AllFields(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(Inst | BindingFlags.DeclaredOnly)) yield return f;
        }

        static IEnumerable<MethodInfo> AllMethods(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
                foreach (var m in t.GetMethods(Inst | BindingFlags.DeclaredOnly)) yield return m;
        }

        sealed class Shapes
        {
            public FieldInfo LogicField;      // structure game object -> signal logic
            public MethodInfo SetSignal;      // on signal logic
            public Type PaneType;             // playfield type (3rd parameter)
            public FieldInfo DataField;       // signal logic -> structure data
            public FieldInfo ShieldStateField;
            public MethodInfo ShieldCapacity;
            public List<FieldInfo> DeviceLists;
        }

        static readonly Dictionary<Type, Shapes> cache = new Dictionary<Type, Shapes>();
        static readonly Dictionary<Type, MethodInfo> powerCache = new Dictionary<Type, MethodInfo>();

        static Shapes For(object structObj, out string error)
        {
            error = null;
            var t = structObj.GetType();
            lock (cache)
                if (cache.TryGetValue(t, out var known)) return known;

            var sh = new Shapes();
            foreach (var f in AllFields(t))
            {
                var hits = AllMethods(f.FieldType).Where(m =>
                {
                    var ps = m.GetParameters();
                    return m.ReturnType == typeof(void) && ps.Length == 4 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool)
                           && ps[3].ParameterType.IsAssignableFrom(t);
                }).ToList();
                if (hits.Count == 0) continue;
                if (hits.Count > 1 || sh.LogicField != null) { error = "the game's signal method is not unique (game update?)"; return null; }
                sh.LogicField = f;
                sh.SetSignal = hits[0];
                sh.PaneType = hits[0].GetParameters()[2].ParameterType;
            }
            if (sh.LogicField == null) { error = "the game's signal method was not found (game update?)"; return null; }

            foreach (var f in AllFields(sh.LogicField.FieldType))
                if (f.FieldType.GetNestedType("EnumShieldState", Inst) != null) { sh.DataField = f; break; }
            if (sh.DataField != null)
            {
                var dt = sh.DataField.FieldType;
                var shieldEnum = dt.GetNestedType("EnumShieldState", Inst);
                sh.ShieldStateField = AllFields(dt).FirstOrDefault(f => f.FieldType == shieldEnum);
                var cap = AllMethods(dt).Where(m =>
                {
                    var ps = m.GetParameters();
                    return m.ReturnType == typeof(int) && ps.Length == 3 && ps.All(p => p.IsOut && p.ParameterType == typeof(int).MakeByRefType());
                }).ToList();
                if (cap.Count == 1) sh.ShieldCapacity = cap[0];
                sh.DeviceLists = AllFields(dt).Where(f => typeof(IDevicePosList).IsAssignableFrom(f.FieldType)).ToList();
            }
            lock (cache) cache[t] = sh;
            return sh;
        }

        static object Logic(IStructure s, out Shapes sh, out string error)
        {
            sh = null;
            var obj = GameObject(s?.Entity);
            if (obj == null) { error = "the structure's game object is not available"; return null; }
            sh = For(obj, out error);
            if (sh == null) return null;
            var logic = sh.LogicField.GetValue(obj);
            if (logic == null) error = "the structure has no signal logic";
            return logic;
        }

        static object Pane(object playerObj, Type paneType)
        {
            foreach (var f in AllFields(playerObj.GetType()))
                if (f.FieldType == paneType) return f.GetValue(playerObj);
            return null;
        }

        /// <summary>Set a signal (control panel or block signal) by name like the control panel does. Null = done, else the error.</summary>
        public static string SetSignal(IStructure s, IPlayer player, string name, bool on, bool allowUnverified)
        {
            var gate = Gate(allowUnverified);
            if (gate != null) return gate;
            var logic = Logic(s, out var sh, out var error);
            if (logic == null) return error;
            var playerObj = GameObject(player);
            if (playerObj == null || !sh.SetSignal.GetParameters()[3].ParameterType.IsInstanceOfType(playerObj)) return "the player's game object is not available";
            var pane = Pane(playerObj, sh.PaneType);
            if (pane == null) return "the player's playfield object is not available";
            sh.SetSignal.Invoke(logic, new[] { name, (object)on, pane, playerObj });
            return null;
        }

        /// <summary>Switch the structure's main power like the control panel does. Null = done, else the error.</summary>
        public static string SetPower(IStructure s, IPlayer player, bool on, bool allowUnverified)
        {
            var gate = Gate(allowUnverified);
            if (gate != null) return gate;
            var logic = Logic(s, out _, out var error);
            if (logic == null) return error;
            var playerObj = GameObject(player);
            if (playerObj == null) return "the player's game object is not available";
            var pt = playerObj.GetType();
            MethodInfo m;
            lock (powerCache)
                if (!powerCache.TryGetValue(pt, out m))
                {
                    var hits = AllMethods(pt).Where(x =>
                    {
                        var ps = x.GetParameters();
                        return x.ReturnType == typeof(bool) && ps.Length == 3 && ps[0].ParameterType == typeof(bool)
                               && ps[1].ParameterType.IsInstanceOfType(logic) && ps[2].ParameterType == typeof(bool);
                    }).ToList();
                    m = hits.Count == 1 ? hits[0] : null;
                    powerCache[pt] = m;
                }
            if (m == null) return "the game's power switch method was not found (game update?)";
            var ok = (bool)m.Invoke(playerObj, new[] { (object)on, logic, false });
            return ok ? null : "the game refused the power switch";
        }

        public sealed class ShieldInfo
        {
            public string State;          // Off, Active, ActiveRecharging, ActiveHitCooldown, WaitRecharge, CooldownFromRecharge, CooldownFromSwitchOn
            public int? Capacity;         // max shield points (generator + boosters); null if unknown
            public int? RechargeRate;     // points per second (game value)
        }

        public static ShieldInfo Shield(IStructure s, bool allowUnverified, out string error)
        {
            error = Gate(allowUnverified);
            if (error != null) return null;
            var logic = Logic(s, out var sh, out error);
            if (logic == null) return null;
            if (sh.DataField == null) { error = "the structure data was not found (game update?)"; return null; }
            var data = sh.DataField.GetValue(logic);
            if (data == null) { error = "the structure has no data object"; return null; }
            var info = new ShieldInfo { State = sh.ShieldStateField?.GetValue(data)?.ToString() };
            if (sh.ShieldCapacity != null)
            {
                var args = new object[] { 0, 0, 0 };
                var cap = (int)sh.ShieldCapacity.Invoke(data, args);
                if (cap >= 0) { info.Capacity = cap; info.RechargeRate = (int)args[0]; }
            }
            return info;
        }

        /// <summary>The structure's internal device lists (one per device kind; positions only).</summary>
        public static List<IDevicePosList> DeviceLists(IStructure s, bool allowUnverified, out string error)
        {
            error = Gate(allowUnverified);
            if (error != null) return null;
            var logic = Logic(s, out var sh, out error);
            if (logic == null) return null;
            if (sh.DataField == null) { error = "the structure data was not found (game update?)"; return null; }
            var data = sh.DataField.GetValue(logic);
            if (data == null) { error = "the structure has no data object"; return null; }
            return sh.DeviceLists.Select(f => f.GetValue(data) as IDevicePosList).Where(l => l != null && l.Count > 0).ToList();
        }
    }
}
