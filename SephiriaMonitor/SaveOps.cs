using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace SephiriaMonitor
{
    /// <summary>Reads/writes individual save keys (meta save vs. run temp save).</summary>
    internal static class SaveOps
    {
        internal static object Current(string scope)
        {
            if (scope == "run")
            {
                return SaveManager.CurrentRun;
            }
            return SaveManager.Current;
        }

        internal static string Key(string key, string scope, string set, string type)
        {
            JObject root = new JObject();
            key = (key ?? string.Empty).Trim();
            scope = string.IsNullOrEmpty(scope) ? "meta" : scope;
            root["key"] = key;
            root["scope"] = scope;
            if (key.Length == 0)
            {
                root["error"] = "key is required";
                return root.ToString(Newtonsoft.Json.Formatting.None);
            }
            object save = Current(scope);
            if (save == null)
            {
                root["error"] = "no " + scope + " save is loaded";
                return root.ToString(Newtonsoft.Json.Formatting.None);
            }
            Type t = save.GetType();
            if (!string.IsNullOrEmpty(set))
            {
                try
                {
                    string kind = string.IsNullOrEmpty(type) ? "string" : type.ToLowerInvariant();
                    switch (kind)
                    {
                        case "int":
                            t.GetMethod("SetInt").Invoke(save, new object[] { key, int.Parse(set, CultureInfo.InvariantCulture) });
                            break;
                        case "float":
                            t.GetMethod("SetFloat").Invoke(save, new object[] { key, float.Parse(set, CultureInfo.InvariantCulture) });
                            break;
                        case "long":
                            t.GetMethod("SetLong").Invoke(save, new object[] { key, long.Parse(set, CultureInfo.InvariantCulture) });
                            break;
                        case "bool":
                            t.GetMethod("SetBool").Invoke(save, new object[] { key, set == "1" || set.Equals("true", StringComparison.OrdinalIgnoreCase) });
                            break;
                        default:
                            t.GetMethod("SetString").Invoke(save, new object[] { key, set });
                            break;
                    }
                    MethodInfo sv = t.GetMethod("Save", Type.EmptyTypes);
                    if (sv != null)
                    {
                        sv.Invoke(save, null);
                    }
                    root["written"] = true;
                }
                catch (Exception ex)
                {
                    Exception inner = ex.InnerException ?? ex;
                    root["error"] = inner.GetType().Name + ": " + inner.Message;
                }
            }
            try
            {
                MethodInfo has = t.GetMethod("HasKey");
                root["exists"] = has != null && (bool)has.Invoke(save, new object[] { key });
                object raw = RawValue(save, key);
                if (raw != null)
                {
                    root["value"] = raw.ToString();
                    root["type"] = raw.GetType().Name;
                }
            }
            catch (Exception ex)
            {
                root["readError"] = ex.GetType().Name + ": " + ex.Message;
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static object RawValue(object save, string key)
        {
            PropertyInfo baked = save.GetType().GetProperty("BakedData");
            IEnumerable pairs = baked != null ? baked.GetValue(save) as IEnumerable : null;
            if (pairs == null)
            {
                return null;
            }
            foreach (object pair in pairs)
            {
                if (pair == null)
                {
                    continue;
                }
                Type pt = pair.GetType();
                PropertyInfo k = pt.GetProperty("Key");
                PropertyInfo v = pt.GetProperty("Value");
                if (k == null || v == null)
                {
                    continue;
                }
                if (string.Equals(Convert.ToString(k.GetValue(pair)), key, StringComparison.Ordinal))
                {
                    return v.GetValue(pair);
                }
            }
            return null;
        }

        internal static string Dump(string prefix, string scope, int limit)
        {
            JObject root = new JObject();
            scope = string.IsNullOrEmpty(scope) ? "meta" : scope;
            object save = Current(scope);
            root["scope"] = scope;
            if (save == null)
            {
                root["error"] = "no " + scope + " save is loaded";
                return root.ToString(Newtonsoft.Json.Formatting.None);
            }
            JArray rows = new JArray();
            PropertyInfo baked = save.GetType().GetProperty("BakedData");
            IEnumerable pairs = baked != null ? baked.GetValue(save) as IEnumerable : null;
            int total = 0;
            if (pairs != null)
            {
                foreach (object pair in pairs)
                {
                    if (pair == null)
                    {
                        continue;
                    }
                    Type pt = pair.GetType();
                    PropertyInfo k = pt.GetProperty("Key");
                    PropertyInfo v = pt.GetProperty("Value");
                    if (k == null || v == null)
                    {
                        continue;
                    }
                    string key = Convert.ToString(k.GetValue(pair));
                    if (!string.IsNullOrEmpty(prefix) && key.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    total++;
                    if (rows.Count < limit)
                    {
                        object val = v.GetValue(pair);
                        rows.Add(new JObject
                        {
                            ["key"] = key,
                            ["value"] = val != null ? val.ToString() : null,
                            ["type"] = val != null ? val.GetType().Name : null
                        });
                    }
                }
            }
            root["matched"] = total;
            root["shown"] = rows.Count;
            root["rows"] = rows;
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
