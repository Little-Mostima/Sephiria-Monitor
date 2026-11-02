using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SephiriaMonitor
{
    /// <summary>Runtime view of WeaponDatabase: base weapon -> enhanced weapon entities, with prefab names.</summary>
    internal static class WeaponOps
    {
        internal static string Map(string q, int limit)
        {
            JObject root = new JObject();
            JArray rows = new JArray();
            int bases = 0, pairs = 0;
            try
            {
                List<WeaponEntity> all = WeaponDatabase.GetAll();
                root["weapons"] = all.Count;
                foreach (WeaponEntity w in all)
                {
                    if (w == null)
                    {
                        continue;
                    }
                    string baseName = Safe(() => w.Name);
                    string basePrefab = w.mainWeaponPrefab != null ? w.mainWeaponPrefab.name : "";
                    bool hit = string.IsNullOrEmpty(q) ||
                        (baseName != null && baseName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (basePrefab != null && basePrefab.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        w.id.ToString() == q;
                    List<EnhancementMetadata> list = w.standardEnhancements;
                    bool any = false;
                    if (list != null)
                    {
                        foreach (EnhancementMetadata m in list)
                        {
                            if (m == null || m.enhanced == null)
                            {
                                continue;
                            }
                            any = true;
                            pairs++;
                            if (!hit || rows.Count >= limit)
                            {
                                continue;
                            }
                            JObject row = new JObject();
                            row["baseId"] = w.id;
                            row["baseName"] = baseName;
                            row["basePrefab"] = basePrefab;
                            row["enhancedId"] = m.enhanced.id;
                            row["enhancedName"] = Safe(() => m.enhanced.Name);
                            row["enhancedPrefab"] = m.enhanced.mainWeaponPrefab != null ? m.enhanced.mainWeaponPrefab.name : "";
                            row["enabled"] = m.enabled;
                            row["enhanceFromId"] = m.enhanced.enhanceFromId;
                            rows.Add(row);
                        }
                    }
                    if (any)
                    {
                        bases++;
                    }
                }
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            root["baseWeapons"] = bases;
            root["pairs"] = pairs;
            root["shown"] = rows.Count;
            root["rows"] = rows;
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string Safe(Func<string> f)
        {
            try
            {
                return f();
            }
            catch
            {
                return "";
            }
        }
    }
}
