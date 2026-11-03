using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Read-only dump of every enemy the spawn tables can produce: the AvatarSpawnEntity rows
    /// (faction / score / wave cap / targets) and, per unique prefab, the UnitAvatar serialized
    /// numbers plus the AI / addon components that sit next to it. Nothing is instantiated.
    /// </summary>
    internal static class UnitOps
    {
        internal static string Dump(string q, int limit)
        {
            JObject root = new JObject();
            JArray entities = new JArray();
            JObject units = new JObject();
            try
            {
                AvatarSpawnEntity[] spawns = Resources.LoadAll<AvatarSpawnEntity>("AvatarSpawn");
                root["spawnEntities"] = spawns != null ? spawns.Length : 0;
                if (spawns == null)
                {
                    spawns = new AvatarSpawnEntity[0];
                }
                foreach (AvatarSpawnEntity s in spawns)
                {
                    if (s == null || entities.Count >= limit)
                    {
                        continue;
                    }
                    try
                    {
                    string label = Safe(() => s.displayName != null ? s.displayName.ToString() : "");
                    string id = s.id ?? "";
                    bool hit = string.IsNullOrEmpty(q) ||
                        id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    JArray targets = new JArray();
                    if (s.spawnTargets != null)
                    {
                        foreach (GameObject go in s.spawnTargets)
                        {
                            if (go == null)
                            {
                                continue;
                            }
                            targets.Add(go.name);
                            if (!units.ContainsKey(go.name))
                            {
                                try
                                {
                                    units[go.name] = Unit(go);
                                }
                                catch (Exception ex)
                                {
                                    JObject bad = new JObject();
                                    bad["error"] = ex.GetType().Name + ": " + ex.Message;
                                    units[go.name] = bad;
                                }
                            }
                        }
                    }
                    if (!hit)
                    {
                        continue;
                    }
                    JObject row = new JObject();
                    row["id"] = id;
                    row["name"] = label;
                    row["faction"] = s.faction.ToString();
                    row["score"] = s.score;
                    row["maxPerWave"] = s.maxQuantityInWave;
                    row["superArmorPercent"] = s.superArmorPercent;
                    row["targets"] = targets;
                    entities.Add(row);
                    }
                    catch (Exception ex)
                    {
                        JObject bad = new JObject();
                        bad["id"] = s != null ? (s.id ?? "") : "";
                        bad["error"] = ex.GetType().Name + ": " + ex.Message;
                        entities.Add(bad);
                    }
                }
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            root["shownEntities"] = entities.Count;
            root["units"] = units.Count;
            root["entities"] = entities;
            root["unitTable"] = units;
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JObject Unit(GameObject go)
        {
            JObject u = new JObject();
            try
            {
                UnitAvatar av = go.GetComponent<UnitAvatar>();
                if (av == null)
                {
                    u["component"] = "";
                    return u;
                }
                u["component"] = av.GetType().Name;
                u["maxHp"] = Num(F(av, "maxHp"));
                u["maxShield"] = Num(F(av, "maxShield"));
                u["attack"] = Num(F(av, "attack"));
                u["maxMp"] = Num(F(av, "maxMp"));
                u["protectionPoint"] = Num(F(av, "protectionPoint"));
                u["staggerResist"] = Num(F(av, "staggerResist"));
                u["moveSpeed"] = Num(F(av, "moveSpeed"));
                u["startingMoney"] = Num(F(av, "randomlyStartingMoney"));
                u["superArmor"] = Num(F(av, "InitializedMaxSuperArmor"));
                u["faction"] = Str(F(av, "faction"));
                u["unitID"] = Str(F(av, "unitID"));
                u["key"] = Str(F(av, "key"));
                u["nameKey"] = Str(F(av, "defaultNameKey"));

                JArray comps = new JArray();
                foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null)
                    {
                        continue;
                    }
                    string n = mb.GetType().Name;
                    if (n.StartsWith("UnitAI_") || n.StartsWith("UnitAddon_") || n.StartsWith("Boss"))
                    {
                        comps.Add(n);
                    }
                }
                u["behaviour"] = comps;
            }
            catch (Exception ex)
            {
                u["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return u;
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
