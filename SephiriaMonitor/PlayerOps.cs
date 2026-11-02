using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>Player state tooling: god mode, heal, money, level, custom stats.
    /// God mode is enforced every frame by keeping the avatar invulnerable (no Harmony needed).</summary>
    public static class PlayerOps
    {
        public static bool God;
        /// <summary>When false (default) god mode only keeps hp topped up and clears the invulnerable flag,
        /// because AI target search skips invulnerable creatures (UnitAI_NewBasic.cs:1489-1505).</summary>
        public static bool InvulnMode;
        public static string LastGodChange = "";
        private static FieldInfo invulnField;
        private static float nextHealCheck;

        public static void Apply()
        {
            try
            {
                invulnField = typeof(UnitAvatar).GetField("invulnerableCount",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MonitorPlugin.LogInfo("PlayerOps: tick mode, invulnerableCount=" + (invulnField != null ? "found" : "missing"));
            }
            catch (Exception e)
            {
                MonitorPlugin.LogError("PlayerOps.Apply: " + e.Message);
            }
        }

        public static void Tick()
        {
            if (!God)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < nextHealCheck)
            {
                return;
            }
            nextHealCheck = now + 0.25f;
            PlayerAvatar p = Player();
            if (p == null)
            {
                return;
            }
            if (invulnField != null)
            {
                try
                {
                    int want = InvulnMode ? 1 : 0;
                    if ((int)invulnField.GetValue(p) != want)
                    {
                        invulnField.SetValue(p, want);
                    }
                }
                catch (Exception e)
                {
                    MonitorPlugin.LogError("PlayerOps.Tick invuln: " + e.Message);
                }
            }
            if (p.IsDead)
            {
                try
                {
                    p.Revive(p.MaxHp);
                }
                catch (Exception e)
                {
                    MonitorPlugin.LogError("PlayerOps.Tick revive: " + e.Message);
                }
            }
            if (p.hp < p.MaxHp)
            {
                p.Networkhp = p.MaxHp;
            }
        }

        private static PlayerAvatar Player()
        {
            List<UnityEngine.Object> players = StateApi.Instances(typeof(PlayerAvatar));
            return players.Count > 0 ? players[0] as PlayerAvatar : null;
        }

        public static string GodSet(bool on, bool? invuln)
        {
            God = on;
            if (invuln.HasValue)
            {
                InvulnMode = invuln.Value;
            }
            LastGodChange = DateTime.Now.ToString("HH:mm:ss");
            PlayerAvatar p = Player();
            if (p != null && invulnField != null)
            {
                try { invulnField.SetValue(p, InvulnMode ? 1 : 0); } catch { }
            }
            Dictionary<string, object> root = Json.Obj();
            root["god"] = God;
            root["invulnMode"] = InvulnMode;
            root["changedAt"] = LastGodChange;
            root["hint"] = !God
                ? "god mode off (invulnerable flag cleared)"
                : (InvulnMode
                    ? "invulnerable pinned: AI cannot target you (UnitAI_NewBasic.cs:1502 skips invulnerable)"
                    : "heal-only: hp kept full every 0.25s, still targetable");
            return Json.Write(root);
        }

        public static string Info()
        {
            PlayerAvatar p = Player();
            Dictionary<string, object> root = Json.Obj();
            root["god"] = God;
            if (p == null)
            {
                root["error"] = "no player";
                return Json.Write(root);
            }
            root["name"] = p.name;
            root["hp"] = Math.Round(p.hp, 1);
            root["maxHp"] = Math.Round(p.MaxHp, 1);
            root["mp"] = p.mp;
            root["money"] = p.Money;
            LevelController lc = p.GetComponent<LevelController>();
            if (lc != null)
            {
                root["level"] = lc.currentLevel;
                root["exp"] = lc.currentExp;
                root["statPoints"] = lc.AvailableStatPoint;
            }
            root["invulnerable"] = p.IsInvulnerable;
            return Json.Write(root);
        }

        public static string Heal(float amount)
        {
            PlayerAvatar p = Player();
            Dictionary<string, object> root = Json.Obj();
            if (p == null)
            {
                root["error"] = "no player";
                return Json.Write(root);
            }
            float before = p.hp;
            float target = amount <= 0f ? p.MaxHp : Mathf.Min(amount, p.MaxHp);
            p.Networkhp = target;
            root["healed"] = true;
            root["hpBefore"] = Math.Round(before, 1);
            root["hp"] = Math.Round(p.hp, 1);
            root["maxHp"] = Math.Round(p.MaxHp, 1);
            return Json.Write(root);
        }

        public static string SetMoney(int value)
        {
            PlayerAvatar p = Player();
            Dictionary<string, object> root = Json.Obj();
            if (p == null)
            {
                root["error"] = "no player";
                return Json.Write(root);
            }
            int before = p.Money;
            p.SetMoney(value);
            root["moneyBefore"] = before;
            root["money"] = p.Money;
            return Json.Write(root);
        }

        public static string SetLevel(int level)
        {
            PlayerAvatar p = Player();
            Dictionary<string, object> root = Json.Obj();
            if (p == null)
            {
                root["error"] = "no player";
                return Json.Write(root);
            }
            LevelController lc = p.GetComponent<LevelController>();
            if (lc == null)
            {
                root["error"] = "no LevelController";
                return Json.Write(root);
            }
            int before = lc.currentLevel;
            lc.currentLevel = Mathf.Max(1, level);
            root["levelBefore"] = before;
            root["level"] = lc.currentLevel;
            root["note"] = "level rewards are not granted retroactively; use action=stat for those";
            return Json.Write(root);
        }

        public static string SetStat(string name, int value)
        {
            PlayerAvatar p = Player();
            Dictionary<string, object> root = Json.Obj();
            if (p == null)
            {
                root["error"] = "no player";
                return Json.Write(root);
            }
            if (string.IsNullOrEmpty(name))
            {
                root["error"] = "stat name required (e.g. EVASION, ATTACK_SPEED, LUCK)";
                return Json.Write(root);
            }
            int before = p.GetCustomStat(name);
            p.AddCustomStatUnsafe(name, value - before);
            root["stat"] = name;
            root["before"] = before;
            root["value"] = p.GetCustomStat(name);
            return Json.Write(root);
        }
        /// <summary>Boss/miniboss snapshot: hp, position and what each AI is currently doing / targeting.
        /// Exists because "boss does nothing" is usually "boss has no target" (e.g. player was invulnerable).</summary>
        public static string Boss()
        {
            Dictionary<string, object> root = Json.Obj();
            List<object> rows = new List<object>();
            List<UnityEngine.Object> all = StateApi.Instances(typeof(UnitAvatar));
            for (int i = 0; i < all.Count; i++)
            {
                UnitAvatar u = all[i] as UnitAvatar;
                if (u == null) { continue; }
                string cls = u.GetType().Name;
                bool interesting = cls.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0;
                MonoBehaviour[] comps = u.GetComponents<MonoBehaviour>();
                List<object> ais = new List<object>();
                for (int c = 0; c < comps.Length; c++)
                {
                    MonoBehaviour mb = comps[c];
                    if (mb == null) { continue; }
                    string t = mb.GetType().Name;
                    if (t.IndexOf("UnitAI", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (t.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0) { interesting = true; }
                    Dictionary<string, object> ai = Json.Obj();
                    ai["type"] = t;
                    foreach (string key in new string[] { "currentAction", "searchType", "bindedDestinyQuest" })
                    {
                        object v = GetMember(mb, key);
                        if (v != null) { ai[key] = Describe(v); }
                    }
                    object tgt = GetMember(mb, "unitAvatar") ?? GetMember(mb, "target") ?? GetMember(mb, "currentTarget");
                    ai["target"] = tgt == null ? null : ((UnityEngine.Object)tgt).name;
                    ais.Add(ai);
                }
                if (!interesting) { continue; }
                Dictionary<string, object> row = Json.Obj();
                row["name"] = u.name;
                row["type"] = cls;
                row["hp"] = Math.Round(u.hp, 1);
                row["maxHp"] = Math.Round(u.MaxHp, 1);
                row["dead"] = u.IsDead;
                row["pos"] = new object[] { Math.Round(u.transform.position.x, 1), Math.Round(u.transform.position.y, 1) };
                row["invulnerable"] = u.IsInvulnerable;
                row["ai"] = ais;
                rows.Add(row);
            }
            root["count"] = rows.Count;
            root["units"] = rows;
            return Json.Write(root);
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) { return null; }
            Type t = obj.GetType();
            PropertyInfo pi = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi != null) { return pi.GetValue(obj); }
            FieldInfo fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi != null) { return fi.GetValue(obj); }
            return null;
        }

        private static object Describe(object v)
        {
            if (v == null) { return null; }
            UnityEngine.Object uo = v as UnityEngine.Object;
            if (uo != null) { return uo.name; }
            return v.ToString();
        }

    }
}
