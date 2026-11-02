using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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
                Harmony harmony = new Harmony("com.surveillance.monitor.god");
                harmony.PatchAll(typeof(GodPatch));
                MonitorPlugin.LogInfo("PlayerOps: god patch applied on UnitAvatar.ApplyDefense");
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
                    int want = 0; // always targetable: damage is zeroed by GodPatch instead
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
                try { invulnField.SetValue(p, 0); } catch { }
            }
            Dictionary<string, object> root = Json.Obj();
            root["god"] = God;
            root["invulnMode"] = InvulnMode;
            root["changedAt"] = LastGodChange;
            root["hint"] = !God
                ? "god mode off (invulnerable flag cleared)"
                : (InvulnMode
                    ? "god mode: still targetable, incoming damage forced to 0"
                    : "god mode: still targetable, incoming damage forced to 0");
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

        /// <summary>
        /// Fixed (stone-tablet) engravings currently loaded on the server. They live in
        /// GridInventory.fixedEngravingsOnServer, not in the scene, so /objects cannot see them —
        /// this is the observation point for restore bugs and the regression fixture for engraving-fix.
        /// </summary>
        /// <summary>Gives the local player items by id (test/ops helper: content mods need a way to
        /// put their items in hand without walking to a shop).</summary>
        /// <summary>Reads a custom stat by its string name (the names statuses use, e.g.
        /// DEBUFF_DAMAGE or HP_STEAL). ECustomStat does not cover these, so /inventory cannot show them.</summary>
        public static string CustomStat(string name)
        {
            PlayerAvatar p = Player();
            if (p == null) return Json.Write(Json.Obj().Add("error", "no player"));
            if (string.IsNullOrEmpty(name)) return Json.Write(Json.Obj().Add("error", "name required"));
            // Statuses spell stats with underscores (DEBUFF_DAMAGE) while the damage math reads the
            // custom-stat dictionary without them (DEBUFFDAMAGE), and some are ECustomStat members
            // (HP_STEAL -> HPSteal). Accept all three spellings.
            string key = name.Replace("_", "").ToUpperInvariant();
            int value = p.GetCustomStatUnsafe(key);
            int enumValue = 0;
            try
            {
                ECustomStat stat;
                if (Enum.TryParse<ECustomStat>(name.Replace("_", ""), true, out stat))
                {
                    enumValue = p.GetCustomStat(stat);
                }
            }
            catch (Exception) { }
            if (value == 0 && enumValue != 0) value = enumValue;
            return Json.Write(Json.Obj().Add("name", name).Add("key", key).Add("value", value).Add("enumValue", enumValue));
        }

        public static string Grant(int itemId, int count)
        {
            PlayerAvatar p = Player();
            if (p == null) return Json.Write(Json.Obj().Add("error", "no player"));
            if (itemId <= 0) return Json.Write(Json.Obj().Add("error", "item id required"));
            if (count < 1) count = 1;
            if (count > 20) count = 20;
            ItemMetadata[] items = new ItemMetadata[count];
            for (int i = 0; i < count; i++) items[i] = new ItemMetadata(-1, itemId, 1);
            p.Inventory.AddItemsWithGenerateInstanceID(Environment.TickCount, items);
            MonitorPlugin.LogInfo("PlayerOps.Grant: " + count + "x" + itemId);
            return Json.Write(Json.Obj().Add("granted", count).Add("item", itemId));
        }

        public static string Engravings()
        {
            PlayerAvatar p = Player();
            Dictionary<string, object> root = Json.Obj();
            if (p == null || p.Inventory == null)
            {
                root["error"] = "no player";
                return Json.Write(root);
            }
            List<object> list = new List<object>();
            List<FixedEngraving> src = p.Inventory.fixedEngravingsOnServer;
            if (src != null)
            {
                foreach (FixedEngraving f in src)
                {
                    Dictionary<string, object> row = Json.Obj();
                    row["entityID"] = f.entityID;
                    row["instanceID"] = f.instanceID;
                    row["external"] = f.createdByExternalSystem;
                    row["effects"] = f.effectRange != null ? f.effectRange.Count : 0;
                    list.Add(row);
                }
            }
            root["count"] = list.Count;
            root["items"] = list;
            root["server"] = Mirror.NetworkServer.active;
            root["saveCount"] = SaveManager.CurrentRun != null
                ? SaveManager.CurrentRun.GetInt("Player0Inventory_FixedEngraving_Count", -1)
                : -1;
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
