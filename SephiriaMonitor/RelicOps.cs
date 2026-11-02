using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Read-only dump of every charm / stone tablet the game knows about, including the effect text
    /// the game itself would render, so the docs can quote the game instead of re-implementing it.
    /// </summary>
    internal static class RelicOps
    {
        internal static string Relics(string kind, int level, string q, int limit)
        {
            JObject root = new JObject();
            JArray rows = new JArray();
            int charms = 0, tablets = 0, inspected = 0, noPrefab = 0, errors = 0;
            try
            {
                PlayerAvatar avatar = FindAvatar();
                GridInventory inv = avatar != null ? avatar.Inventory : null;
                int w = inv != null ? (int)inv.Width : 6;
                int storage = inv != null ? (int)inv.CurrentInventoryStorage : 24;
                int h = inv != null ? (int)inv.Height : 4;
                JObject invObj = new JObject();
                invObj["width"] = w;
                invObj["height"] = h;
                invObj["storage"] = storage;
                invObj["fromPlayer"] = inv != null;
                root["inventory"] = invObj;

                int[] ids = ItemDatabase.GetAllItemID();
                root["items"] = ids == null ? 0 : ids.Length;
                if (ids != null)
                {
                    foreach (int id in ids)
                    {
                        if (rows.Count >= limit)
                        {
                            break;
                        }
                        ItemEntity e;
                        try { e = ItemDatabase.FindItemById(id); }
                        catch { e = null; }
                        if (e == null)
                        {
                            continue;
                        }
                        GameObject prefab = e.resourcePrefab;
                        if (prefab == null)
                        {
                            noPrefab++;
                            continue;
                        }
                        Charm_Basic charm = prefab.GetComponent<Charm_Basic>();
                        StoneTablet tablet = prefab.GetComponent<StoneTablet>();
                        if (charm == null && tablet == null)
                        {
                            continue;
                        }
                        if (kind == "charm" && charm == null)
                        {
                            continue;
                        }
                        if (kind == "tablet" && tablet == null)
                        {
                            continue;
                        }
                        string name = Safe(() => e.aName.ToString());
                        if (!string.IsNullOrEmpty(q))
                        {
                            bool hit = (name != null && name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                                || prefab.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                || id.ToString(CultureInfo.InvariantCulture) == q;
                            if (!hit)
                            {
                                continue;
                            }
                        }
                        inspected++;
                        JObject row = new JObject();
                        row["id"] = id;
                        row["name"] = name;
                        row["rarity"] = e.rarity.ToString();
                        row["cost"] = e.cost;
                        row["itemType"] = e.type.ToString();
                        row["categories"] = new JArray(e.categories == null ? new List<string>() : e.categories);
                        row["prefab"] = prefab.name;
                        if (charm != null)
                        {
                            charms++;
                            try { CharmRow(row, charm, level, avatar); }
                            catch (Exception ex) { errors++; row["charmError"] = ex.GetType().Name + ": " + ex.Message; }
                        }
                        if (tablet != null)
                        {
                            tablets++;
                            try { TabletRow(row, tablet, w, h, storage); }
                            catch (Exception ex) { errors++; row["tabletError"] = ex.GetType().Name + ": " + ex.Message; }
                        }
                        rows.Add(row);
                    }
                }
            }
            catch (Exception ex)
            {
                errors++;
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            root["charms"] = charms;
            root["tablets"] = tablets;
            root["inspected"] = inspected;
            root["noPrefab"] = noPrefab;
            root["errors"] = errors;
            root["shown"] = rows.Count;
            root["rows"] = rows;
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void CharmRow(JObject row, Charm_Basic charm, int level, PlayerAvatar avatar)
        {
            row["className"] = charm.GetType().Name;
            row["category"] = Str(FieldMember(charm, "category"));
            row["comboCount"] = Num(FieldMember(charm, "comboCount"));
            row["maxLevel"] = charm.maxLevel;
            row["isUniqueEffect"] = charm.isUniqueEffect;
            int count = 0;
            try { count = charm.GetEffectStringCount(); }
            catch (Exception ex) { row["countError"] = ex.GetType().Name; }
            row["effectStringCount"] = count;
            int maxLevel = charm.maxLevel > 0 ? charm.maxLevel : 1;
            int top = level > 0 ? Math.Min(level, maxLevel) : maxLevel;
            JArray levels = new JArray();
            for (int lv = 1; lv <= top; lv++)
            {
                JObject lvObj = new JObject();
                lvObj["level"] = lv;
                JArray texts = new JArray();
                for (int i = 0; i < count; i++)
                {
                    string text;
                    try { text = charm.GetEffectString(i, lv, 0, false); }
                    catch (Exception ex) { text = "<error " + ex.GetType().Name + ">"; }
                    texts.Add(text ?? "");
                }
                lvObj["text"] = texts;
                string pretty;
                try { pretty = charm.BuildEffectString(avatar, "• ", "\n", lv, 0, false, false); }
                catch (Exception ex) { pretty = "<error " + ex.GetType().Name + ": " + ex.Message + ">"; }
                lvObj["pretty"] = pretty;
                levels.Add(lvObj);
            }
            row["levels"] = levels;
            row["values"] = Numbers(charm);
        }

        private static JObject Numbers(object o)
        {
            JObject result = new JObject();
            try
            {
                FieldInfo[] fields = o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                int n = 0;
                foreach (FieldInfo f in fields)
                {
                    if (n >= 14)
                    {
                        break;
                    }
                    Type t = f.FieldType;
                    object v = f.GetValue(o);
                    if (v == null)
                    {
                        continue;
                    }
                    if (t == typeof(int) || t == typeof(float))
                    {
                        result[f.Name] = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                        n++;
                    }
                    else if (t == typeof(int[]))
                    {
                        int[] arr = (int[])v;
                        if (arr.Length > 0 && arr.Length <= 12)
                        {
                            result[f.Name] = new JArray(arr);
                            n++;
                        }
                    }
                    else if (t == typeof(float[]))
                    {
                        float[] arr = (float[])v;
                        if (arr.Length > 0 && arr.Length <= 12)
                        {
                            result[f.Name] = new JArray(arr);
                            n++;
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static void TabletRow(JObject row, StoneTablet tablet, int w, int h, int storage)
        {
            row["className"] = tablet.GetType().Name;
            row["isRotatable"] = tablet.isRotatable;
            row["includeConditionCriteriaToMinMaxGrid"] = tablet.includeConditionCriteriaToMinMaxGrid;
            row["rotation"] = tablet.rotation;
            row["xIdx"] = (int)tablet.xIdx;
            row["yIdx"] = (int)tablet.yIdx;
            row["isCustomTablet"] = tablet.isCustomTablet;

            string query = tablet.query == null ? "" : tablet.query;
            string conditionQuery = tablet.conditionQuery == null ? "" : tablet.conditionQuery;
            row["query"] = query;
            row["conditionQuery"] = conditionQuery;

            row["effects"] = Parse(query, w, h, storage, tablet);
            row["conditions"] = Parse(conditionQuery, w, h, storage, tablet);
        }

        private static JArray Parse(string query, int w, int h, int storage, StoneTablet tablet)
        {
            JArray list = new JArray();
            if (string.IsNullOrEmpty(query))
            {
                return list;
            }
            try
            {
                List<StoneTablet.HumanReadableAddition> human;
                StoneTablet.ParseQuery(query, w, h, storage, new ItemPosition(0, 0), tablet.rotation, out human);
                if (human != null)
                {
                    foreach (StoneTablet.HumanReadableAddition a in human)
                    {
                        JObject o = new JObject();
                        o["area"] = a.areaName;
                        o["criteria"] = a.criteria;
                        list.Add(o);
                    }
                }
            }
            catch (Exception ex)
            {
                JObject o = new JObject();
                o["error"] = ex.GetType().Name + ": " + ex.Message;
                list.Add(o);
            }
            return list;
        }

        private static PlayerAvatar FindAvatar()
        {
            try
            {
                List<PlayerSpawner> list = PlayerSpawner.MultiplayerList;
                if (list == null || list.Count == 0 || list[0] == null)
                {
                    return null;
                }
                return list[0].PlayerAvatar;
            }
            catch
            {
                return null;
            }
        }

        private static GridInventory PlayerInventory()
        {
            try
            {
                List<PlayerSpawner> list = PlayerSpawner.MultiplayerList;
                if (list == null || list.Count == 0 || list[0] == null)
                {
                    return null;
                }
                PlayerAvatar a = list[0].PlayerAvatar;
                return a != null ? a.Inventory : null;
            }
            catch
            {
                return null;
            }
        }

        private static object FieldMember(object o, string name)
        {
            try
            {
                Type t = o.GetType();
                FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null)
                {
                    return fi.GetValue(o);
                }
                PropertyInfo pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi != null)
                {
                    return pi.GetValue(o, null);
                }
            }
            catch
            {
            }
            return null;
        }

        private static string Str(object v)
        {
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static int Num(object v)
        {
            if (v == null)
            {
                return 0;
            }
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); }
            catch { return ""; }
        }
    }
}
