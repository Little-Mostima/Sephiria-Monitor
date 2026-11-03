using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Counts how often the documented combat hook points are actually called.
    /// Patches are installed once at boot; counting only happens between
    /// /hookfreq?action=start and /hookfreq?action=stop, so the cost outside a
    /// measurement window is one boolean test per call.
    /// </summary>
    public static class HookFreq
    {
        private static readonly Dictionary<string, long> Counts = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly List<string> Registered = new List<string>();
        private static readonly List<string> Report = new List<string>();
        private static readonly object Gate = new object();
        private static bool enabled;
        private static float startedAt;
        private static float stoppedAt;
        private static int patched;

        public static void Apply()
        {
            Harmony harmony = new Harmony("com.sephiria.monitor.hookfreq");
            // (type, method, parameter count, include overrides)
            Patch(harmony, typeof(CombatBehaviour), "ApplyDamage", 1, true);
            Patch(harmony, typeof(CombatManager), "SendCreatureApplyDamage", 2, false);
            Patch(harmony, typeof(CombatManager), "AttackEvent", 3, false);
            Patch(harmony, typeof(DamageInstance), "GetDamage", 10, false);
            Patch(harmony, typeof(DamageInstance), "AddFlag", 1, false);
            Patch(harmony, typeof(WeaponSimple), "CreateBasicAttackProjectile", 6, false);
            Patch(harmony, typeof(WeaponSimple), "OnAttack_Basic", 3, true);
            Patch(harmony, typeof(WeaponControllerSimple), "CreateBasicAttackSwing", 1, false);
            Patch(harmony, typeof(DropItemOnDie), "DropEXP", 1, false);
            Patch(harmony, typeof(LevelController), "AddExp", 1, false);
            Patch(harmony, typeof(WeaponSimple), "GetSpecialAttack", 1, true);
            Patch(harmony, typeof(WeaponSimple), "AttackButtonDown", 1, true);
            Patch(harmony, typeof(WeaponSimple), "SubAttackButtonDown", 0, true);
            Patch(harmony, typeof(ItemDatabase), "GetRandomRarity", 4, false);
            MonitorPlugin.LogInfo("HOOKFREQ|patched=" + patched + "|" + string.Join(" ", Report.ToArray()));
        }

        private static void Patch(Harmony harmony, Type type, string name, int argc, bool overrides)
        {
            try
            {
                int n = PatchOne(harmony, type, name, argc);
                if (overrides)
                {
                    foreach (Type t in type.Assembly.GetTypes())
                    {
                        if (t == type || !type.IsAssignableFrom(t)) continue;
                        n += PatchOne(harmony, t, name, argc);
                    }
                }
                Report.Add(type.Name + "." + name + "=" + n);
            }
            catch (Exception ex)
            {
                Report.Add(type.Name + "." + name + "=FAIL:" + ex.GetType().Name);
                MonitorPlugin.LogError("HOOKFREQ|" + type.Name + "." + name + "|" + ex.Message);
            }
        }

        private static int PatchOne(Harmony harmony, Type type, string name, int argc)
        {
            MethodInfo m = Find(type, name, argc);
            if (m == null) return 0;
            harmony.Patch(m, null, new HarmonyMethod(typeof(HookFreq).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.Public)));
            patched++;
            string key = type.Name + "." + name;
            if (!Registered.Contains(key)) Registered.Add(key);
            return 1;
        }

        private static MethodInfo Find(Type type, string name, int argc)
        {
            MethodInfo[] all = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                               BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (MethodInfo m in all)
            {
                if (m.Name != name || m.IsAbstract) continue;
                if (m.GetParameters().Length != argc) continue;
                return m;
            }
            return null;
        }

        public static void Post(MethodBase __originalMethod)
        {
            if (!enabled || __originalMethod == null) return;
            string key = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            lock (Gate)
            {
                long n;
                Counts.TryGetValue(key, out n);
                Counts[key] = n + 1;
            }
        }

        public static string Handle(string action)
        {
            switch (action)
            {
                case "start": return Start();
                case "stop": return Stop();
                case "reset": return Reset();
                default: return Dump();
            }
        }

        private static string Start()
        {
            lock (Gate) Counts.Clear();
            startedAt = Time.realtimeSinceStartup;
            stoppedAt = 0f;
            enabled = true;
            return "{\"ok\":true,\"action\":\"start\",\"patched\":" + patched + "}";
        }

        private static string Stop()
        {
            enabled = false;
            stoppedAt = Time.realtimeSinceStartup;
            return Dump();
        }

        private static string Reset()
        {
            lock (Gate) Counts.Clear();
            startedAt = Time.realtimeSinceStartup;
            stoppedAt = 0f;
            return "{\"ok\":true,\"action\":\"reset\"}";
        }

        private static string Dump()
        {
            float end = stoppedAt > 0f ? stoppedAt : Time.realtimeSinceStartup;
            float seconds = Mathf.Max(0.001f, end - startedAt);
            List<KeyValuePair<string, long>> rows = new List<KeyValuePair<string, long>>();
            lock (Gate)
            {
                foreach (string key in Registered)
                {
                    long n;
                    Counts.TryGetValue(key, out n);
                    rows.Add(new KeyValuePair<string, long>(key, n));
                }
                foreach (KeyValuePair<string, long> kv in Counts)
                {
                    if (!Registered.Contains(kv.Key)) rows.Add(kv);
                }
            }
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"enabled\":").Append(enabled ? "true" : "false");
            sb.Append(",\"seconds\":").Append(seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"patched\":").Append(patched);
            sb.Append(",\"hooks\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"hook\":").Append(Json.Write(rows[i].Key)).Append(",\"calls\":").Append(rows[i].Value);
                sb.Append(",\"perSecond\":").Append((rows[i].Value / seconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
