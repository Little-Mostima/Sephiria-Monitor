using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

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

        /// <summary>Equip a vanilla weapon by id (server side). Used by the damage-multiplier probe.</summary>
        internal static string Equip(int weaponId)
        {
            JObject root = new JObject();
            try
            {
                PlayerAvatar p = PlayerOps.Player();
                if (p == null)
                {
                    root["error"] = "no player";
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }
                if (weaponId <= 0)
                {
                    root["error"] = "weapon id required";
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }
                WeaponControllerSimple controller = p.GetComponent<WeaponControllerSimple>();
                if (controller == null)
                {
                    root["error"] = "no WeaponControllerSimple";
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }
                WeaponEntity entity = WeaponDatabase.FindWeaponById(weaponId);
                if (entity == null)
                {
                    root["error"] = "unknown weapon " + weaponId;
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }
                controller.EquipWeapon(false, weaponId, false);
                root["equipped"] = weaponId;
                root["name"] = Safe(() => entity.Name);
                root["current"] = controller.currentWeapon != null ? controller.currentWeapon.name : null;
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Fire the equipped weapon once (server side) so damage events can be compared.</summary>
        internal static string Attack(float dx, float dy, int dash, bool release)
        {
            JObject root = new JObject();
            try
            {
                PlayerAvatar p = PlayerOps.Player();
                if (p == null)
                {
                    root["error"] = "no player";
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }
                WeaponControllerSimple controller = p.GetComponent<WeaponControllerSimple>();
                if (controller == null || controller.currentWeapon == null)
                {
                    root["error"] = "no weapon equipped";
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }
                Vector2 dir = new Vector2(dx, dy);
                if (dir.sqrMagnitude < 0.0001f)
                {
                    dir = Vector2.right;
                }
                dir.Normalize();
                controller.AttackButtonDown(dir, dash);
                if (release)
                {
                    controller.AttackButtonUp();
                }
                root["ok"] = true;
                root["weapon"] = controller.currentWeapon.name;
                root["dir"] = new JArray(dir.x, dir.y);
                root["dash"] = dash;
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Release the attack button (the swing itself runs in the weapon's Update).</summary>
        internal static string AttackUp()
        {
            JObject root = new JObject();
            try
            {
                PlayerAvatar p = PlayerOps.Player();
                WeaponControllerSimple controller = p == null ? null : p.GetComponent<WeaponControllerSimple>();
                if (controller == null)
                {
                    root["error"] = "no WeaponControllerSimple";
                    return root.ToString(Newtonsoft.Json.Formatting.None);
                }
                controller.AttackButtonUp();
                root["ok"] = true;
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
