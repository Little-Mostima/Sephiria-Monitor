using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Read-only dump of every weapon: entity fields, the WeaponSimple component on its prefab,
    /// the three fire-data arrays (basic / dash / special) and the addon instances with their numbers.
    /// Nothing is instantiated: components are read straight off the prefab asset.
    /// </summary>
    internal static class WeaponDump
    {
        internal static string Dump(string q, int limit)
        {
            JObject root = new JObject();
            JArray items = new JArray();
            int total = 0;
            try
            {
                List<WeaponEntity> all = WeaponDatabase.GetAll();
                total = all.Count;
                foreach (WeaponEntity w in all)
                {
                    if (w == null)
                    {
                        continue;
                    }
                    string name = Safe(() => w.Name);
                    string prefab = w.mainWeaponPrefab != null ? w.mainWeaponPrefab.name : "";
                    if (!string.IsNullOrEmpty(q) &&
                        (name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                        prefab.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                        w.id.ToString() != q)
                    {
                        continue;
                    }
                    if (items.Count >= limit)
                    {
                        continue;
                    }
                    JObject row = new JObject();
                    row["id"] = w.id;
                    row["name"] = name;
                    row["prefab"] = prefab;
                    row["isDefault"] = w.isDefaultWeapon;
                    row["enhanceFromId"] = w.enhanceFromId;
                    row["unlockMiniscript"] = w.unlockMiniscript ?? "";
                    JArray enh = new JArray();
                    if (w.standardEnhancements != null)
                    {
                        foreach (EnhancementMetadata m in w.standardEnhancements)
                        {
                            if (m != null && m.enhanced != null)
                            {
                                enh.Add(m.enhanced.id);
                            }
                        }
                    }
                    row["enhancesTo"] = enh;

                    GameObject go = w.mainWeaponPrefab;
                    WeaponSimple simple = go != null ? go.GetComponent<WeaponSimple>() : null;
                    if (simple == null)
                    {
                        row["component"] = "";
                        items.Add(row);
                        continue;
                    }
                    row["component"] = simple.GetType().Name;
                    row["weaponType"] = Str(F(simple, "weaponType"));
                    row["attackWeightPerSwing"] = Num(F(simple, "attackWeightPerSwing"));
                    row["attackSpeedAmplify"] = Num(F(simple, "attackSpeedAmplify"));
                    row["specialAttackIsRelatedToAttackSpeed"] = Bool(F(simple, "specialAttackIsRelatedToAttackSpeed"));
                    row["isRangedWeapon"] = Bool(F(simple, "isRangedWeapon"));
                    row["finalComboIdx"] = Num(F(simple, "finalComboIdx"));
                    row["isDashAttackAvailable"] = Num(F(simple, "isDashAttackAvailable"));
                    row["attackMoveSet"] = Num(F(simple, "attackMoveSet"));

                    row["basic"] = FireArray(F(simple, "basicComboAttacks"));
                    row["dash"] = FireArray(F(simple, "dashAttacks"));
                    row["special"] = FireArray(F(simple, "specialAttacks"));
                    row["addons"] = Addons(F(simple, "addons"));
                    items.Add(row);
                }
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            root["weapons"] = total;
            root["shown"] = items.Count;
            root["items"] = items;
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JArray FireArray(object value)
        {
            JArray arr = new JArray();
            Array a = value as Array;
            if (a == null)
            {
                return arr;
            }
            foreach (object o in a)
            {
                if (o == null)
                {
                    continue;
                }
                JObject f = new JObject();
                f["cls"] = o.GetType().Name;
                f["damageMultiplier"] = Num(F(o, "damageMultiplier"));
                f["relatedStatFormula"] = Str(F(o, "relatedStatFormula"));
                f["element"] = Str(F(o, "damageElementalType"));
                f["addDamagePerUsedMP"] = Num(F(o, "addDamagePerUsedMP"));
                f["staggeringLevel"] = Num(F(o, "staggeringLevel"));
                f["attackDashSpeed"] = Num(F(o, "attackDashSpeed"));
                f["attackDashTime"] = Num(F(o, "attackDashTime"));
                f["swingID"] = Str(F(o, "swingID"));
                f["forceFinalCombo"] = Bool(F(o, "forceFinalCombo"));
                f["useElementalTypeFromRelatedStatFormula"] = Bool(F(o, "useElementalTypeFromRelatedStatFormula"));
                GameObject proj = F(o, "projectilePrefab") as GameObject ?? F(o, "bulletPrefab") as GameObject;
                f["projectile"] = proj != null ? proj.name : "";
                arr.Add(f);
            }
            return arr;
        }

        private static JArray Addons(object value)
        {
            JArray arr = new JArray();
            Array a = value as Array;
            if (a == null)
            {
                return arr;
            }
            foreach (object o in a)
            {
                if (o == null)
                {
                    continue;
                }
                JObject addon = new JObject();
                addon["cls"] = o.GetType().Name;
                JObject fields = new JObject();
                int n = 0;
                foreach (FieldInfo fi in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (n >= 12)
                    {
                        break;
                    }
                    object v = null;
                    try
                    {
                        v = fi.GetValue(o);
                    }
                    catch
                    {
                        continue;
                    }
                    if (v == null)
                    {
                        continue;
                    }
                    Type t = v.GetType();
                    if (t.IsPrimitive || t.IsEnum || v is string)
                    {
                        fields[fi.Name] = v is Enum ? v.ToString() : (v is string ? (string)v : (JToken)Convert.ToDouble(v));
                        n++;
                        continue;
                    }
                    Array inner = v as Array;
                    if (inner != null && inner.Length > 0 && inner.Length <= 8)
                    {
                        JArray vals = new JArray();
                        bool ok = true;
                        foreach (object e in inner)
                        {
                            if (e == null)
                            {
                                ok = false;
                                break;
                            }
                            Type et = e.GetType();
                            if (et.IsPrimitive || et.IsEnum || e is string)
                            {
                                vals.Add(e is Enum ? e.ToString() : (e is string ? (string)e : (JToken)Convert.ToDouble(e)));
                            }
                            else
                            {
                                ok = false;
                                break;
                            }
                        }
                        if (ok)
                        {
                            fields[fi.Name] = vals;
                            n++;
                        }
                    }
                }
                addon["fields"] = fields;
                arr.Add(addon);
            }
            return arr;
        }

        private static object F(object o, string name)
        {
            if (o == null)
            {
                return null;
            }
            try
            {
                Type t = o.GetType();
                while (t != null)
                {
                    FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fi != null)
                    {
                        return fi.GetValue(o);
                    }
                    t = t.BaseType;
                }
            }
            catch
            {
            }
            return null;
        }

        private static string Str(object v)
        {
            return v == null ? "" : v.ToString();
        }

        private static double Num(object v)
        {
            try
            {
                return v == null ? 0 : Convert.ToDouble(v);
            }
            catch
            {
                return 0;
            }
        }

        private static bool Bool(object v)
        {
            try
            {
                return v != null && Convert.ToBoolean(v);
            }
            catch
            {
                return false;
            }
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
