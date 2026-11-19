using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VoxtaBridge
{
    /// <summary>
    /// Experiment (2026-11-18): is the generated galaxy (GalaxyGenerator.StarInfo list, the stars the galaxy map shows)
    /// reachable on this process? The game assembly is obfuscated, so fields are found by type, not by name:
    /// static List&lt;StarInfo&gt; fields, and List&lt;StarInfo&gt; fields of singletons (static fields of their own type).
    /// GET /debug/galaxy.
    /// </summary>
    public partial class BridgeMod
    {
        static string GalaxyProbe()
        {
            var j = new Json().Obj();
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asm == null) return j.Prop("error", "no Assembly-CSharp").EndObj().ToString();
            var gen = asm.GetType("GalaxyGenerator");
            var starType = gen?.GetNestedType("StarInfo", BindingFlags.Public | BindingFlags.NonPublic);
            if (starType == null) return j.Prop("error", "no GalaxyGenerator.StarInfo").EndObj().ToString();
            var listType = typeof(List<>).MakeGenericType(starType);
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            var found = new List<(string where, IList list)>();
            var siteArrays = new List<(string where, Array arr)>();
            foreach (var t in types)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(all); } catch { continue; }
                foreach (var f in fields.Where(f => f.IsStatic))
                {
                    object v;
                    try { v = f.GetValue(null); } catch { continue; }
                    if (v == null) continue;
                    if (f.FieldType == listType) { found.Add(($"{t.FullName}::{f.Name} (static)", (IList)v)); continue; }
                    if (f.FieldType != t) continue;
                    // singleton: look at its instance fields
                    foreach (var inner in t.GetFields(all).Where(x => !x.IsStatic))
                    {
                        object iv;
                        try { iv = inner.GetValue(v); } catch { continue; }
                        if (iv == null) continue;
                        if (inner.FieldType == listType) found.Add(($"{t.FullName}::{f.Name}.{inner.Name}", (IList)iv));
                        else if (inner.FieldType.IsArray && iv is Array arr && arr.Length > 0 && arr.Length < 500)
                            siteArrays.Add(($"{t.FullName}::{f.Name}.{inner.Name} ({inner.FieldType.GetElementType()?.Name}[{arr.Length}])", arr));
                    }
                }
            }

            var fPos = starType.GetField("pos"); var fType = starType.GetField("typeIndex"); var fId = starType.GetField("id");
            var fName = starType.GetField("manualName"); var fHide = starType.GetField("HideFromMap");
            j.Prop("mode", mode.ToString()).Key("lists").Arr();
            foreach (var (where, list) in found)
            {
                j.Obj().Prop("field", where).Prop("count", list.Count);
                var hist = new Dictionary<int, int>();
                foreach (var s in list) { var ti = (int)fType.GetValue(s); hist[ti] = hist.TryGetValue(ti, out var n) ? n + 1 : 1; }
                j.Key("typeIndex").Obj();
                foreach (var kv in hist.OrderBy(k => k.Key)) j.Prop(kv.Key.ToString(), kv.Value);
                j.EndObj().Key("sample").Arr();
                foreach (var s in list.Cast<object>().Take(4).Concat(list.Cast<object>().Where(s => !string.IsNullOrEmpty(fName.GetValue(s) as string)).Take(8)))
                    j.Obj().Prop("pos", fPos.GetValue(s)?.ToString()).Prop("type", (int)fType.GetValue(s)).Prop("id", (int)fId.GetValue(s))
                        .Prop("name", fName.GetValue(s) as string).Prop("hidden", (bool)fHide.GetValue(s)).EndObj();
                j.EndArr().EndObj();
            }
            j.EndArr().Key("arrays").Arr();
            foreach (var (where, arr) in siteArrays.Take(20))
            {
                j.Obj().Prop("field", where).Key("first").Arr();
                foreach (var el in arr.Cast<object>().Take(3))
                {
                    if (el == null) continue;
                    var strings = el.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(x => x.FieldType == typeof(string)).Select(x => x.GetValue(el) as string).Where(s => s != null);
                    j.Val(string.Join(" | ", strings));
                }
                j.EndArr().EndObj();
            }
            return j.EndArr().EndObj().ToString();
        }
    }
}
