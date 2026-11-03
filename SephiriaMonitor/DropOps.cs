using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>Drop tables: every DropItemOnDie (prefab or live) and what it can drop.</summary>
    internal static class DropOps
    {
        internal static string Drops(string q, int limit)
        {
            JObject root = new JObject();
            JArray rows = new JArray();
            HashSet<int> weaponIds = new HashSet<int>();
            HashSet<int> seen = new HashSet<int>();
            int total = 0, withWeapon = 0;
            try
            {
                DropItemOnDie[] all = Resources.FindObjectsOfTypeAll<DropItemOnDie>();
                root["components"] = all.Length;
                foreach (DropItemOnDie d in all)
                {
                    if (d == null)
                    {
                        continue;
                    }
                    string owner = d.gameObject != null ? d.gameObject.name : "";
                    if (!string.IsNullOrEmpty(q) && owner.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    total++;
                    JArray drops = new JArray();
                    bool anyWeapon = false;
                    DropItemOnDie.DropItemData[] list = d.droppable;
                    if (list != null)
                    {
                        foreach (DropItemOnDie.DropItemData item in list)
                        {
                            if (item.entity == null)
                            {
                                continue;
                            }
                            int id = item.entity.id;
                            JObject row = new JObject();
                            row["id"] = id;
                            row["name"] = Safe(() => item.entity.Name);
                            row["percent"] = item.dropPercent;
                            bool isWeapon = WeaponDatabase.FindWeaponById(id) != null;
                            row["weapon"] = isWeapon;
                            if (isWeapon)
                            {
                                anyWeapon = true;
                                weaponIds.Add(id);
                            }
                            drops.Add(row);
                        }
                    }
                    if (anyWeapon)
                    {
                        withWeapon++;
                    }
                    if (rows.Count < limit)
                    {
                        JObject r = new JObject();
                        r["owner"] = owner;
                        r["scene"] = d.gameObject != null && d.gameObject.scene.IsValid();
                        r["money"] = d.dropMoney;
                        r["exp"] = d.expDrop;
                        r["drops"] = drops;
                        rows.Add(r);
                    }
                }
                root["tables"] = total;
                root["tablesWithWeapon"] = withWeapon;
                root["weaponIds"] = new JArray(weaponIds);
                root["shown"] = rows.Count;
                root["rows"] = rows;
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
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
