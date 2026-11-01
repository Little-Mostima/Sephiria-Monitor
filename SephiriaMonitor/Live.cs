using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Live reads: computed from the running objects at request time (no snapshot cache).
    /// Used by /inventory (which used to be a stale snapshot filter), /combos and /statuses.
    /// Always called on the Unity main thread through MainThreadPump.
    /// </summary>
    public static class Live
    {
        public static string Inventory()
        {
            JsonObject root = Json.Obj();
            root["live"] = true;
            root["t"] = Time.realtimeSinceStartup;
            int n = 0;
            List<PlayerSpawner> list = PlayerSpawner.MultiplayerList;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    PlayerSpawner spawner = list[i];
                    if (spawner == null)
                    {
                        continue;
                    }
                    PlayerAvatar avatar = spawner.PlayerAvatar;
                    string p = "p" + i + ".";
                    if (avatar == null)
                    {
                        root[p + "missing"] = "true";
                        continue;
                    }
                    root[p + "name"] = spawner.name;
                    root[p + "steamId"] = spawner.steamID.ToString(CultureInfo.InvariantCulture);
                    root[p + "sapphire"] = spawner.sapphireInRun.ToString(CultureInfo.InvariantCulture);
                    root[p + "hp"] = R(avatar.hp);
                    root[p + "maxHp"] = R(avatar.MaxHp);
                    root[p + "mp"] = avatar.mp.ToString(CultureInfo.InvariantCulture);
                    root[p + "maxMp"] = avatar.MaxMp.ToString(CultureInfo.InvariantCulture);
                    root[p + "money"] = avatar.Money.ToString(CultureInfo.InvariantCulture);
                    Vector3 pos = avatar.transform.position;
                    root[p + "pos"] = R(pos.x) + "," + R(pos.y);
                    root[p + "level"] = avatar.GetComponent<LevelController>() != null
                        ? avatar.GetComponent<LevelController>().currentLevel.ToString(CultureInfo.InvariantCulture)
                        : "0";
                    WriteStats(root, p, avatar);
                    WriteInventory(root, p, avatar.Inventory);
                    n++;
                }
            }
            root["players"] = n.ToString(CultureInfo.InvariantCulture);
            root["keys"] = root.Count;
            return Json.Write(root);
        }

        /// <summary>Combo (set) effects currently on the player: category, count, thresholds and the statuses they push.</summary>
        public static string Combos()
        {
            JsonObject root = Json.Obj();
            root["live"] = true;
            List<object> items = new List<object>();
            List<UnityEngine.Object> all = StateApi.Instances(typeof(ComboEffectBase));
            for (int i = 0; i < all.Count; i++)
            {
                ComboEffectBase combo = all[i] as ComboEffectBase;
                if (combo == null)
                {
                    continue;
                }
                JsonObject o = Json.Obj();
                o["object"] = combo.name;
                o["category"] = combo.categoryName;
                o["comboCount"] = combo.comboCount;
                o["applied"] = combo.lastAppliedComboEffectCount;
                o["isPlayer"] = combo.isPlayerCombo;
                o["enabled"] = combo.isEnabled;
                if (combo.avatar != null)
                {
                    o["avatar"] = combo.avatar.name;
                }
                List<object> thresholds = new List<object>();
                if (combo.addStatByCombo != null)
                {
                    for (int s = 0; s < combo.addStatByCombo.Length; s++)
                    {
                        ComboEffectBase.ComboStat st = combo.addStatByCombo[s];
                        if (st == null)
                        {
                            continue;
                        }
                        JsonObject t = Json.Obj();
                        t["count"] = st.comboCount;
                        List<object> sts = new List<object>();
                        if (st.status != null)
                        {
                            for (int k = 0; k < st.status.Length; k++)
                            {
                                sts.Add(st.status[k]);
                            }
                        }
                        t["status"] = sts;
                        thresholds.Add(t);
                    }
                }
                o["thresholds"] = thresholds;
                o["statusInstances"] = StatusList(Field(combo, "statusInstances"));
                items.Add(o);
            }
            root["combos"] = items;
            root["count"] = items.Count;
            return Json.Write(root);
        }

        /// <summary>Buffs, debuffs and orphaned status instances for every player (and optional unit index).</summary>
        public static string Statuses()
        {
            JsonObject root = Json.Obj();
            root["live"] = true;
            List<object> players = new List<object>();
            List<PlayerSpawner> list = PlayerSpawner.MultiplayerList;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    PlayerSpawner spawner = list[i];
                    if (spawner == null || spawner.PlayerAvatar == null)
                    {
                        continue;
                    }
                    UnitAvatar avatar = spawner.PlayerAvatar;
                    JsonObject o = Json.Obj();
                    o["player"] = i;
                    o["name"] = avatar.name;
                    o["hp"] = R(avatar.hp);
                    o["isDead"] = avatar.IsDead;
                    o["buffs"] = BuffList(avatar.Buffs);
                    o["debuffs"] = DebuffList(avatar.Debuffs);
                    List<object> orphaned = new List<object>();
                    if (avatar.orphanedStatusInstancesServerside != null)
                    {
                        for (int k = 0; k < avatar.orphanedStatusInstancesServerside.Count; k++)
                        {
                            orphaned.Add(StatusObject(avatar.orphanedStatusInstancesServerside[k]));
                        }
                    }
                    o["orphanedStatuses"] = orphaned;
                    players.Add(o);
                }
            }
            root["players"] = players;
            root["count"] = players.Count;
            return Json.Write(root);
        }

        private static void WriteStats(JsonObject root, string p, UnitAvatar avatar)
        {
            Array values = Enum.GetValues(typeof(ECustomStat));
            for (int i = 0; i < values.Length; i++)
            {
                ECustomStat stat = (ECustomStat)values.GetValue(i);
                try
                {
                    root[p + "stat." + stat] = avatar.GetCustomStat(stat).ToString(CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }
        }

        private static void WriteInventory(JsonObject root, string p, GridInventory inv)
        {
            if (inv == null)
            {
                return;
            }
            root[p + "inv.storage"] = inv.CurrentInventoryStorage.ToString(CultureInfo.InvariantCulture);
            root[p + "inv.potionSlots"] = inv.numberOfPotionStorage.ToString(CultureInfo.InvariantCulture);
            int n = 0;
            foreach (KeyValuePair<ItemPosition, NewItemOwnInstance> kv in inv.inventoryMatrix)
            {
                ItemPosition pos = kv.Key;
                NewItemOwnInstance item = kv.Value;
                if (item == null)
                {
                    continue;
                }
                string k = p + "inv." + pos.x + "," + pos.y + ".";
                root[k + "id"] = item.EntityID.ToString(CultureInfo.InvariantCulture);
                root[k + "qty"] = item.Quantity.ToString(CultureInfo.InvariantCulture);
                if (item.Charm != null)
                {
                    root[k + "lv"] = item.Charm.DisplayedLevel.ToString(CultureInfo.InvariantCulture);
                    root[k + "prefab"] = item.Charm.name;
                }
                if (item.StoneTablet != null)
                {
                    root[k + "rot"] = item.StoneTablet.rotation.ToString(CultureInfo.InvariantCulture);
                    root[k + "prefab"] = item.StoneTablet.name;
                }
                n++;
            }
            root[p + "inv.items"] = n.ToString(CultureInfo.InvariantCulture);
            int t = 0;
            foreach (KeyValuePair<ItemPosition, StoneTablet> kv in inv.stoneTablets)
            {
                if (kv.Value == null)
                {
                    continue;
                }
                root[p + "tablet." + kv.Key.x + "," + kv.Key.y + ".id"] = kv.Value.entityID.ToString(CultureInfo.InvariantCulture);
                root[p + "tablet." + kv.Key.x + "," + kv.Key.y + ".rot"] = kv.Value.rotation.ToString(CultureInfo.InvariantCulture);
                t++;
            }
            root[p + "tablets"] = t.ToString(CultureInfo.InvariantCulture);
        }

        private static List<object> BuffList(IEnumerable<CharacterBuff> buffs)
        {
            List<object> list = new List<object>();
            if (buffs == null)
            {
                return list;
            }
            foreach (CharacterBuff buff in buffs)
            {
                if (buff == null)
                {
                    continue;
                }
                JsonObject o = Json.Obj();
                try { o["id"] = buff.ID; } catch { o["id"] = buff.name; }
                o["stack"] = buff.CurrentStack;
                o["maxStack"] = buff.MaxStackCount;
                o["ratio"] = R(buff.BuffTimerRatio);
                o["amplified"] = R(buff.Amplified);
                o["hud"] = buff.HUD_ID;
                list.Add(o);
            }
            return list;
        }

        private static List<object> DebuffList(IEnumerable<CharacterDebuff> debuffs)
        {
            List<object> list = new List<object>();
            if (debuffs == null)
            {
                return list;
            }
            foreach (CharacterDebuff debuff in debuffs)
            {
                if (debuff == null)
                {
                    continue;
                }
                JsonObject o = Json.Obj();
                try { o["id"] = debuff.ID; } catch { o["id"] = debuff.name; }
                o["stack"] = debuff.CurrentStack;
                o["maxStack"] = debuff.MaxStackCount;
                o["ratio"] = R(debuff.BuffTimerRatio);
                o["hud"] = debuff.HUD_ID;
                if (debuff.Attacker != null)
                {
                    o["attacker"] = debuff.Attacker.name;
                }
                list.Add(o);
            }
            return list;
        }

        private static List<object> StatusList(object statuses)
        {
            List<object> list = new List<object>();
            IEnumerable seq = statuses as IEnumerable;
            if (seq == null)
            {
                return list;
            }
            foreach (object item in seq)
            {
                if (item is StatusInstance status)
                {
                    list.Add(StatusObject(status));
                }
            }
            return list;
        }

        private static JsonObject StatusObject(StatusInstance status)
        {
            JsonObject o = Json.Obj();
            if (status == null)
            {
                return o;
            }
            try { o["id"] = status.ID; } catch { o["id"] = ""; }
            o["value"] = status.Value;
            o["keyword"] = status.Keyword;
            try { o["text"] = status.ToValueString(); } catch { }
            try { o["name"] = status.Name; } catch { }
            return o;
        }

        private static object Field(object target, string name)
        {
            if (target == null)
            {
                return null;
            }
            Type t = target.GetType();
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    return f.GetValue(target);
                }
                t = t.BaseType;
            }
            return null;
        }

        private static string R(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v) ? "0" : v.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
