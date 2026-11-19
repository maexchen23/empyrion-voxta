using System;
using System.Globalization;
using System.Text;

namespace VoxtaBridge
{
    /// <summary>
    /// Minimal JSON writer. The mod runs inside the game's Mono runtime, so it avoids extra
    /// dependencies (Newtonsoft etc.) that could clash with other mods' copies.
    /// </summary>
    public sealed class Json
    {
        readonly StringBuilder sb = new StringBuilder();
        // true when the next value in the current container needs a leading comma
        bool needComma;

        public Json Obj() { Sep(); sb.Append('{'); needComma = false; return this; }
        public Json EndObj() { sb.Append('}'); needComma = true; return this; }
        public Json Arr() { Sep(); sb.Append('['); needComma = false; return this; }
        public Json EndArr() { sb.Append(']'); needComma = true; return this; }

        public Json Key(string name)
        {
            Sep();
            Str(name);
            sb.Append(':');
            needComma = false;
            return this;
        }

        public Json Val(string s) { Sep(); if (s == null) sb.Append("null"); else Str(s); needComma = true; return this; }
        public Json Val(bool b) { Sep(); sb.Append(b ? "true" : "false"); needComma = true; return this; }
        public Json Val(long n) { Sep(); sb.Append(n.ToString(CultureInfo.InvariantCulture)); needComma = true; return this; }
        public Json Val(double d)
        {
            Sep();
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else sb.Append(Math.Round(d, 2).ToString("0.##", CultureInfo.InvariantCulture));
            needComma = true;
            return this;
        }

        /// <summary>Inserts an already serialised JSON fragment as a value.</summary>
        public Json Raw(string json) { Sep(); sb.Append(string.IsNullOrEmpty(json) ? "null" : json); needComma = true; return this; }

        public Json Prop(string k, string v) => Key(k).Val(v);
        public Json Prop(string k, bool v) => Key(k).Val(v);
        public Json Prop(string k, long v) => Key(k).Val(v);
        public Json Prop(string k, double v) => Key(k).Val(v);
        public Json Vec(string k, float x, float y, float z) => Key(k).Arr().Val(x).Val(y).Val(z).EndArr();

        public override string ToString() => sb.ToString();

        void Sep()
        {
            if (needComma) sb.Append(',');
        }

        void Str(string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
