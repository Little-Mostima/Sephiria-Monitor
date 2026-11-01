using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>Reflection-based read-only views over live game objects.</summary>
    public static class StateApi
    {
        public static List<UnityEngine.Object> Instances(Type type)
        {
            List<UnityEngine.Object> list = new List<UnityEngine.Object>();
            if (type == typeof(UnitAvatar) || type == typeof(PlayerAvatar))
            {
                // 单位走注册表：出生/销毁时登记，这里只做一次列表拷贝，不再全场景扫描。
                List<UnitAvatar> units = UnitRegistry.Snapshot();
                for (int i = 0; i < units.Count; i++)
                {
                    if (type == typeof(UnitAvatar) || units[i] is PlayerAvatar)
                    {
                        list.Add(units[i]);
                    }
                }
                return list;
            }
            try
            {
                // 其它类型（楼梯/门/宝箱…）数量很少，但扫描很贵，给 0.5s 的短缓存。
                List<UnityEngine.Object> cached;
                if (cache.TryGetValue(type, out cached) && Time.realtimeSinceStartup - cachedAt < 0.5f)
                {
                    return cached;
                }
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(type);
                for (int i = 0; i < all.Length; i++)
                {
                    UnityEngine.Object o = all[i];
                    if (o == null)
                    {
                        continue;
                    }
                    Component c = o as Component;
                    if (c != null)
                    {
                        if (!c.gameObject.scene.IsValid())
                        {
                            continue;
                        }
                    }
                    list.Add(o);
                }
                cache[type] = list;
                cachedAt = Time.realtimeSinceStartup;
            }
            catch
            {
            }
            return list;
        }

        private static readonly Dictionary<Type, List<UnityEngine.Object>> cache =
            new Dictionary<Type, List<UnityEngine.Object>>();
        private static float cachedAt;

        public static void InvalidateCache()
        {
            cache.Clear();
        }

        public static Type FindType(string shortName)
        {
            Assembly asm = typeof(GameLogWriter).Assembly;
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t != null && (t.Name == shortName || t.FullName == shortName))
                {
                    return t;
                }
            }
            return null;
        }

        public static Dictionary<string, object> Describe(UnityEngine.Object o, int depth)
        {
            Dictionary<string, object> d = Json.Obj();
            if (o == null)
            {
                d["null"] = true;
                return d;
            }
            d["name"] = o.name;
            d["type"] = o.GetType().Name;
            Component c = o as Component;
            if (c != null)
            {
                d["pos"] = Vec(c.transform.position);
                d["active"] = c.gameObject.activeInHierarchy;
                if (c.transform.parent != null)
                {
                    d["parent"] = c.transform.parent.name;
                }
            }
            if (depth > 0)
            {
                d["fields"] = DumpFields(o, depth, 60);
            }
            return d;
        }

        public static List<object> Vec(Vector3 v)
        {
            List<object> l = new List<object>(3);
            l.Add(Math.Round(v.x, 2));
            l.Add(Math.Round(v.y, 2));
            l.Add(Math.Round(v.z, 2));
            return l;
        }

        public static Dictionary<string, object> DumpFields(object target, int depth, int maxFields)
        {
            Dictionary<string, object> outFields = Json.Obj();
            if (target == null)
            {
                return outFields;
            }
            Type t = target.GetType();
            int count = 0;
            while (t != null && t != typeof(object) && count < maxFields)
            {
                FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length && count < maxFields; i++)
                {
                    FieldInfo f = fields[i];
                    if (f.Name.IndexOf('<') >= 0 && f.Name.IndexOf("k__BackingField") < 0)
                    {
                        continue;
                    }
                    // Delegate / pointer plumbing (Mirror sync hooks, Unity callbacks) is noise for a reader.
                    Type ft = f.FieldType;
                    if (typeof(Delegate).IsAssignableFrom(ft) || ft == typeof(IntPtr) || ft == typeof(UIntPtr) || ft.IsPointer)
                    {
                        continue;
                    }
                    if (f.Name.IndexOf("_Mirror_SyncVarHookDelegate", StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }
                    object value;
                    try
                    {
                        value = f.GetValue(target);
                    }
                    catch
                    {
                        continue;
                    }
                    outFields[NiceName(f.Name)] = Simplify(value, depth, ref count);
                    count++;
                }
                t = t.BaseType;
            }
            return outFields;
        }

        private static string NiceName(string raw)
        {
            int lt = raw.IndexOf("k__BackingField", StringComparison.Ordinal);
            if (raw.StartsWith("<", StringComparison.Ordinal) && lt > 0)
            {
                return raw.Substring(1, lt - 1);
            }
            return raw;
        }

        private static object Simplify(object value, int depth, ref int budget)
        {
            if (value == null)
            {
                return null;
            }
            Type vt = value.GetType();
            if (value is string || value is bool || vt.IsPrimitive || vt.IsEnum)
            {
                return value;
            }
            if (value is Vector3 v3)
            {
                return Vec(v3);
            }
            if (value is Vector2 v2)
            {
                List<object> l2 = new List<object>(2) { Math.Round(v2.x, 2), Math.Round(v2.y, 2) };
                return l2;
            }
            if (value is UnityEngine.Object uo)
            {
                return uo == null ? "<destroyed>" : (object)("<" + uo.GetType().Name + ">" + uo.name);
            }
            if (value is IDictionary dict)
            {
                if (depth < 0) { return "<max depth>"; }
                Dictionary<string, object> sub = Json.Obj();
                int n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (n++ >= 40)
                    {
                        sub["..."] = "truncated";
                        break;
                    }
                    sub[Convert.ToString(e.Key)] = Simplify(e.Value, depth - 1, ref budget);
                }
                return sub;
            }
            if (value is IEnumerable seq)
            {
                if (depth < 0) { return "<max depth>"; }
                List<object> items = new List<object>();
                int n = 0;
                foreach (object item in seq)
                {
                    if (n++ >= 40)
                    {
                        items.Add("...truncated");
                        break;
                    }
                    items.Add(Simplify(item, depth - 1, ref budget));
                }
                return items;
            }
            if (depth > 0)
            {
                return DumpFields(value, depth - 1, 25);
            }
            return "<" + vt.Name + ">";
        }

        public static Dictionary<string, object> WorldSummary()
        {
            Dictionary<string, object> d = Json.Obj();
            d["scene"] = SceneTracker.Current;
            d["prevScene"] = SceneTracker.Previous;
            d["unity"] = Application.unityVersion;
            d["t"] = Time.realtimeSinceStartup;
            List<UnityEngine.Object> units = Instances(typeof(UnitAvatar));
            d["unitCount"] = units.Count;
            List<object> brief = new List<object>();
            int shown = 0;
            int hostileSeen = 0;
            string playerFaction = "";
            for (int i = 0; i < units.Count; i++)
            {
                PlayerAvatar pa = units[i] as PlayerAvatar;
                if (pa != null)
                {
                    playerFaction = FactionOf(pa);
                    break;
                }
            }
            for (int i = 0; i < units.Count && shown < 40; i++)
            {
                UnitAvatar ua = units[i] as UnitAvatar;
                if (ua == null)
                {
                    continue;
                }
                bool hostile = IsHostileToPlayer(ua, playerFaction);
                if (hostile)
                {
                    hostileSeen++;
                }
                Dictionary<string, object> u = Json.Obj();
                u["name"] = ua.name;
                u["cls"] = ua.GetType().Name;
                u["pos"] = Vec(ua.transform.position);
                u["hp"] = HealthOf(ua);
                u["faction"] = FactionOf(ua);
                u["hostile"] = hostile;
                brief.Add(u);
                shown++;
            }
            d["playerFaction"] = playerFaction;
            d["hostile"] = hostileSeen;
            d["units"] = brief;
            return d;
        }

        /// <summary>
        /// Faction name the game keeps on every unit (UnitAvatar.faction, Mirror SyncVar). Empty
        /// until the unit registers with CombatManager.
        /// </summary>
        public static string FactionOf(UnitAvatar u)
        {
            try
            {
                return u == null ? "" : (u.faction == null ? "" : u.faction);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// True when the unit's own attackable layers include the player's faction, i.e. when the
        /// game itself would paint that unit red. The previous test ("not a PlayerAvatar, not a
        /// dummy, hp > 0") reported 20 hostile out of 22 units in TheRabbittown, because town NPCs
        /// pass all three: hostility has to come from the faction tables, not from the class.
        /// </summary>
        public static bool IsHostileToPlayer(UnitAvatar unit, string playerFaction)
        {
            if (unit == null || unit is PlayerAvatar || unit.monsterType == EMonsterType.Dummy)
            {
                return false;
            }
            if (string.IsNullOrEmpty(playerFaction))
            {
                return false;
            }
            try
            {
                if (CombatManager.Instance == null)
                {
                    return false;
                }
                long layers = unit.GetHostileFactionLayers(EDamageFromType.None);
                return CombatManager.ContainsAttackableFaction(layers, playerFaction);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when the combatant is (or is led by) a player avatar.</summary>
        public static bool IsPlayerSide(CombatBehaviour b)
        {
            if (b == null)
            {
                return false;
            }
            if (b is PlayerAvatar)
            {
                return true;
            }
            try
            {
                object leader = null;
                Type t = b.GetType();
                while (t != null && leader == null)
                {
                    PropertyInfo p = t.GetProperty("NetworkLeader", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (p != null && p.CanRead)
                    {
                        leader = p.GetValue(b, null);
                    }
                    t = t.BaseType;
                }
                return leader is PlayerAvatar;
            }
            catch
            {
                return false;
            }
        }

        public static object HealthOf(object unit)
        {
            if (unit == null)
            {
                return null;
            }
            Type t = unit.GetType();
            string[] names = { "currentHp", "currentHP", "hp", "health", "currentHealth", "currentHpValue" };
            while (t != null && t != typeof(object))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    FieldInfo f = t.GetField(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (f != null && (f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(double)))
                    {
                        return f.GetValue(unit);
                    }
                    PropertyInfo p = t.GetProperty(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (p != null && p.CanRead && (p.PropertyType == typeof(float) || p.PropertyType == typeof(int)))
                    {
                        return p.GetValue(unit, null);
                    }
                }
                t = t.BaseType;
            }
            return null;
        }
    }
}
