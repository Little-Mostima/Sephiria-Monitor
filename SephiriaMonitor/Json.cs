using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SephiriaMonitor
{
    /// <summary>Tiny dependency-free JSON writer for dictionaries/lists/primitives/unity objects.</summary>
    public static class Json
    {
        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null)
            {
                sb.Append("null");
                return;
            }
            if (v is string s)
            {
                WriteString(sb, s);
                return;
            }
            if (v is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }
            if (v is float f)
            {
                sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is double d)
            {
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is Enum)
            {
                WriteString(sb, v.ToString());
                return;
            }
            if (v is sbyte || v is byte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is UnityEngine.Object uo)
            {
                WriteString(sb, uo == null ? "<destroyed>" : uo.name);
                return;
            }
            if (v is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, e.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            if (v is IEnumerable seq)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in seq)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteValue(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }
            if (v is DateTime dt)
            {
                WriteString(sb, dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                return;
            }
            WriteString(sb, v.ToString());
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        public static JsonObject Obj()
        {
            return new JsonObject();
        }
    }

    /// <summary>Dictionary whose Add returns itself, so JSON object literals can be chained.</summary>
    public class JsonObject : Dictionary<string, object>
    {
        public new JsonObject Add(string key, object value)
        {
            this[key] = value;
            return this;
        }
    }
}
