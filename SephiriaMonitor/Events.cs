using System;
using System.Collections.Generic;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>Structured event stream (mirrored into events.jsonl).</summary>
    public static class Events
    {
        private const int Capacity = 3000;
        private static readonly List<Dictionary<string, object>> Ring = new List<Dictionary<string, object>>(Capacity);
        private static readonly object Gate = new object();
        private static long seq;
        private static long evicted;
        private static int damageCount;
        private static int deathCount;
        private static int probeCount;
        private static string lastFloor = "";

        public static int Count { get { lock (Gate) { return (int)seq; } } }
        public static long Cursor { get { lock (Gate) { return seq; } } }
        public static long Evicted { get { lock (Gate) { return evicted; } } }
        public static int DamageCount { get { lock (Gate) { return damageCount; } } }
        public static int DeathCount { get { lock (Gate) { return deathCount; } } }
        public static int ProbeCount { get { lock (Gate) { return probeCount; } } }
        private static int kills;
        private static int spawnCount;
        private static int despawnCount;
        /// <summary>true = 丢掉 NPC 互殴的 damage 事件（约占单局伤害事件 1/3）。
        /// 默认 false（保留全部），可用 /config?npcDamage=0 打开。</summary>
        public static bool SkipNpcDamage;

        private static bool IsNpcDamageNoise(Dictionary<string, object> e)
        {
            object kind;
            if (!e.TryGetValue("kind", out kind) || !(kind is string) || (string)kind != "damage")
            {
                return false;
            }
            return !Truthy(e, "byPlayer") && !Truthy(e, "isPlayer");
        }

        private static bool Truthy(Dictionary<string, object> e, string key)
        {
            object v;
            if (!e.TryGetValue(key, out v) || v == null)
            {
                return false;
            }
            try
            {
                return Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }
        /// <summary>玩家侧造成的击杀数（death 事件里 byPlayer 的那些）。</summary>
        public static int KillCount { get { lock (Gate) { return kills; } } }
        public static int SpawnCount { get { lock (Gate) { return spawnCount; } } }
        public static int DespawnCount { get { lock (Gate) { return despawnCount; } } }

        public static void Add(Dictionary<string, object> e)
        {
            Add(e, true);
        }

        /// <summary>persist=false 只进内存环形缓冲（/events 和计数看得到），不写 events.jsonl：
        /// 用于心跳、单位进出场这类高频记账事件。</summary>
        public static void Add(Dictionary<string, object> e, bool persist)
        {
            if (e == null)
            {
                return;
            }
            if (SkipNpcDamage && IsNpcDamageNoise(e))
            {
                return;
            }
            lock (Gate)
            {
                e["eseq"] = ++seq;
                Ring.Add(e);
                if (Ring.Count > Capacity)
                {
                    int drop = Ring.Count - Capacity;
                    Ring.RemoveRange(0, drop);
                    evicted += drop;
                }
            }
            Combat.Observe(e);
            // Session.Emit adds seq/t/wall/scene to the object it receives; hand it a copy so a
            // concurrent /events reader never sees "Collection was modified".
            Dictionary<string, object> copy = new Dictionary<string, object>(e.Count + 4, StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> kv in e)
            {
                copy[kv.Key] = kv.Value;
            }
            if (persist)
            {
                Session.Emit(copy);
            }
        }

        public static void Damage(CombatBehaviour victim, DamageInstance dmg, object result)
        {
            if (victim == null || dmg == null)
            {
                return;
            }
            CombatBehaviour origin = dmg.origin;
            // Hit-test probes: the game feeds the victim itself through ApplyDamage to ask
            // "would this land?". They carry dealt 0 and made up ~78% of all damage rows in the
            // 2026-11-01 boss fight, so they are counted but not stored unless asked for.
            if (!MonitorConfig.EmitProbes && origin != null && (UnityEngine.Object)origin == victim)
            {
                lock (Gate) { probeCount++; }
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "damage";
            e["victim"] = victim.name;
            e["victimType"] = victim.GetType().Name;
            e["victimInst"] = victim.GetInstanceID();
            e["raw"] = Math.Round(dmg.damage, 1);
            e["dealt"] = dmg.damageResult;
            e["damageType"] = dmg.damageType.ToString();
            e["element"] = dmg.elementalType.ToString();
            e["fromType"] = dmg.fromType.ToString();
            e["result"] = result == null ? null : result.ToString();
            e["id"] = dmg.id;
            e["crit"] = dmg.isCriticalAttack;
            e["source"] = dmg.damageObject == null ? null : dmg.damageObject.name;
            if (origin != null)
            {
                e["attacker"] = origin.name;
                e["attackerInst"] = origin.GetInstanceID();
                UnitAvatar attacker = origin as UnitAvatar;
                if (attacker != null)
                {
                    e["attackerType"] = attacker.GetType().Name;
                    e["byPlayer"] = StateApi.IsPlayerSide(attacker);
                }
            }
            e["point"] = StateApi.Vec(dmg.damagePoint);
            e["hp"] = StateApi.HealthOf(victim);
            UnitAvatar ua = victim as UnitAvatar;
            if (ua != null)
            {
                e["isPlayer"] = ua is PlayerAvatar;
                // hp is the post-hit value: "lethal" marks the blow that killed, while the
                // death itself is reported once by the OnDie subscription (Events.Death).
                if (ua.hp <= 0f && ua.hp + dmg.damage > 0.01f)
                {
                    e["lethal"] = true;
                }
            }
            lock (Gate) { damageCount++; }
            Add(e);
        }

        public static void UnitSpawned(UnitAvatar u)
        {
            if (u == null)
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "spawn";
            e["unit"] = u.name;
            e["unitType"] = u.GetType().Name;
            e["inst"] = u.GetInstanceID();
            e["pos"] = StateApi.Vec(u.transform.position);
            e["hp"] = u.hp;
            e["maxHp"] = u.MaxHp;
            e["isPlayer"] = u is PlayerAvatar;
            lock (Gate) { spawnCount++; }
            Add(e, false);
        }

        public static void UnitDespawned(UnitAvatar u)
        {
            if (u == null)
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "despawn";
            e["unit"] = u.name;
            e["unitType"] = u.GetType().Name;
            e["inst"] = u.GetInstanceID();
            e["killed"] = u.hp <= 0f;
            e["pos"] = StateApi.Vec(u.transform.position);
            lock (Gate) { despawnCount++; }
            Add(e, false);
        }

        public static void Floor(string guid, string floorName, FloorGenerator gen)
        {
            lock (Gate) { lastFloor = floorName; }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "floor";
            e["guid"] = guid;
            e["name"] = floorName;
            if (gen != null)
            {
                e["seed"] = gen.seed;
                e["threat"] = gen.floorThreatType.ToString();
                e["mainEvent"] = gen.floorMainEventType.ToString();
                e["lucky"] = gen.luckyType.ToString();
                e["pd"] = gen.pdType.ToString();
                e["hardBattle"] = gen.currentHardBattleMode.ToString();
                e["roomCount"] = gen.randomRoomCount;
                e["finalized"] = gen.IsFloorFinalized;
            }
            Add(e);
        }

        /// <summary>Name of the floor the session is on, cached so /run does not need a snapshot.</summary>
        public static string LastFloor
        {
            get { lock (Gate) { return lastFloor; } }
        }

        public static void Evade(PlayerAvatar p, DamageInstance dmg)
        {
            if (p == null || dmg == null)
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "evade";
            e["player"] = p.name;
            e["raw"] = Math.Round(dmg.damage, 1);
            e["damageType"] = dmg.damageType.ToString();
            e["fromType"] = dmg.fromType.ToString();
            e["id"] = dmg.id;
            e["attacker"] = dmg.origin == null ? null : dmg.origin.name;
            Add(e);
        }


        public static void Death(UnitAvatar victim, DamageInstance diedFrom)
        {
            if (victim == null)
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "death";
            e["victim"] = victim.name;
            e["victimType"] = victim.GetType().Name;
            e["victimInst"] = victim.GetInstanceID();
            e["isPlayer"] = victim is PlayerAvatar;
            e["pos"] = StateApi.Vec(victim.transform.position);
            LevelController lc = victim.GetComponent<LevelController>();
            if (lc != null)
            {
                e["level"] = lc.currentLevel;
                e["exp"] = lc.currentExp;
            }
            if (diedFrom != null)
            {
                e["damageId"] = diedFrom.id;
                e["raw"] = Math.Round(diedFrom.damage, 1);
                e["damageType"] = diedFrom.damageType.ToString();
                CombatBehaviour origin = diedFrom.origin;
                if (origin != null)
                {
                    e["killer"] = origin.name;
                    e["killerType"] = origin.GetType().Name;
                    e["killerInst"] = origin.GetInstanceID();
                    UnitAvatar killer = origin as UnitAvatar;
                    if (killer != null)
                    {
                        bool byPlayer = StateApi.IsPlayerSide(killer);
                        e["byPlayer"] = byPlayer;
                        if (byPlayer)
                        {
                            lock (Gate) { kills++; }
                        }
                    }
                }
            }
            lock (Gate) { deathCount++; }
            Add(e);
        }

        /// <summary>
        /// A unit gained an inventory item (chest, drop pickup, shop, quest reward):
        /// GridInventory.NotifyAddItem is the server-side funnel every one of those goes through.
        /// </summary>
        public static void Loot(GridInventory inventory, NewItemOwnInstance item)
        {
            if (item == null)
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "loot";
            e["entityId"] = item.EntityID;
            e["instanceId"] = item.InstanceID;
            e["qty"] = (int)item.Quantity;
            e["slot"] = new List<object> { (int)item.XIdx, (int)item.YIdx };
            if (inventory != null)
            {
                UnitAvatar owner = inventory.GetComponent<UnitAvatar>();
                if (owner != null)
                {
                    e["unit"] = owner.name;
                    e["unitInst"] = owner.GetInstanceID();
                    e["isPlayer"] = owner is PlayerAvatar;
                }
            }
            ItemEntity entity = null;
            try { entity = ItemDatabase.FindItemById(item.EntityID); }
            catch { }
            if (entity != null)
            {
                e["item"] = entity.name;
                e["type"] = entity.type.ToString();
                e["rarity"] = entity.rarity.ToString();
                e["cost"] = entity.cost;
            }
            Add(e);
        }

        public static void Exp(LevelController lc, int gained)
        {
            if (lc == null)
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "exp";
            e["gained"] = gained;
            e["level"] = lc.currentLevel;
            e["exp"] = lc.currentExp;
            int[] table = LevelController.ExpTableByLevel;
            if (table != null && lc.currentLevel >= 0 && lc.currentLevel < table.Length)
            {
                e["next"] = table[lc.currentLevel];
            }
            PlayerAvatar avatar = lc.Avatar;
            if (avatar != null)
            {
                e["player"] = avatar.name;
                e["isPlayer"] = true;
            }
            Add(e);
        }

        public static void ItemSpawn(Item item)
        {
            if (item == null)
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "item";
            e["entityId"] = item.itemEntityID;
            e["instanceId"] = item.itemInstanceID;
            e["qty"] = item.itemQuantity;
            e["pos"] = StateApi.Vec(item.transform.position);
            ItemEntity entity = null;
            try { entity = ItemDatabase.FindItemById(item.itemEntityID); }
            catch { }
            if (entity != null)
            {
                e["item"] = entity.name;
                e["type"] = entity.type.ToString();
                e["rarity"] = entity.rarity.ToString();
                e["cost"] = entity.cost;
            }
            Add(e);
        }

        public static void Money(UnitAvatar unit, int delta, string source)
        {
            if (unit == null || delta == 0)
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "money";
            e["delta"] = delta;
            e["total"] = unit.Money;
            e["unit"] = unit.name;
            e["isPlayer"] = unit is PlayerAvatar;
            e["source"] = source;
            Add(e);
        }


        public static void GameLog(string message, Color color)
        {
            // 战斗里的刷屏提示不要：只留玩家真正会读的那种（获得/购买/升级/解锁/神器/掉落）。
            if (!IsNoteworthyLog(message))
            {
                return;
            }
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "gamelog";
            e["msg"] = message;
            e["color"] = "#" + ColorUtility.ToHtmlStringRGB(color);
            Add(e);
        }

        private static readonly string[] LogKeywords = new string[]
        {
            "获得", "购买", "購買", "升级", "解鎖", "解锁", "神器", "遗物", "遺物", "掉落", "拾取", "已达上限",
            "Obtained", "Purchased", "Level up", "Unlocked", "Artifact", "Relic", "Learned"
        };

        private static bool IsNoteworthyLog(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }
            for (int i = 0; i < LogKeywords.Length; i++)
            {
                if (message.Contains(LogKeywords[i]))
                {
                    return true;
                }
            }
            return false;
        }

        public static void Scene(string from, string to)
        {
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "scene";
            e["from"] = from;
            e["to"] = to;
            Add(e);
        }

        public static void Note(string kind, string detail)
        {
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = kind;
            e["detail"] = detail;
            Add(e);
        }

        public static string Tail(int n, long after)
        {
            return Tail(n, after, null);
        }

        /// <summary>
        /// Newest <paramref name="n"/> events after <paramref name="after"/>, optionally only one kind
        /// ("damage", "death", "exp", "item", "money", "floor", ...).
        /// </summary>
        public static string Tail(int n, long after, string kind)
        {
            List<object> list = new List<object>();
            Dictionary<string, object> kinds = new Dictionary<string, object>(StringComparer.Ordinal);
            lock (Gate)
            {
                for (int i = Ring.Count - 1; i >= 0 && list.Count < n; i--)
                {
                    Dictionary<string, object> e = Ring[i];
                    object seqObj;
                    long s = 0;
                    if (e.TryGetValue("eseq", out seqObj))
                    {
                        s = Convert.ToInt64(seqObj);
                    }
                    if (s <= after)
                    {
                        break;
                    }
                    if (!string.IsNullOrEmpty(kind))
                    {
                        object k0;
                        if (!e.TryGetValue("kind", out k0) || Convert.ToString(k0) != kind)
                        {
                            continue;
                        }
                    }
                    list.Add(e);
                }
                for (int i = 0; i < Ring.Count; i++)
                {
                    object k1;
                    if (!Ring[i].TryGetValue("kind", out k1))
                    {
                        continue;
                    }
                    string name = Convert.ToString(k1);
                    object prev;
                    if (kinds.TryGetValue(name, out prev))
                    {
                        kinds[name] = Convert.ToInt64(prev) + 1;
                    }
                    else
                    {
                        kinds[name] = 1L;
                    }
                }
            }
            list.Reverse();
            long cursor = Cursor;
            JsonObject root = Json.Obj().Add("count", Count).Add("damage", DamageCount).Add("deaths", DeathCount)
                .Add("probes", ProbeCount).Add("evicted", Evicted)
                // Cursor to pass back as ?after= on the next poll. Without it a client that only
                // reads "events" cannot tell where to resume and re-reads the same rows forever.
                .Add("cursor", cursor)
                .Add("after", after);
            if (after > cursor && after != 0)
            {
                root.Add("reset", true);
            }
            if (!string.IsNullOrEmpty(kind))
            {
                root = root.Add("kind", kind);
            }
            return Json.Write(root.Add("kinds", kinds).Add("events", list));
        }

        public static string DamageTail(int n)
        {
            List<object> list = new List<object>();
            lock (Gate)
            {
                for (int i = Ring.Count - 1; i >= 0 && list.Count < n; i--)
                {
                    Dictionary<string, object> e = Ring[i];
                    object kind;
                    if (e.TryGetValue("kind", out kind) && Convert.ToString(kind) == "damage")
                    {
                        list.Add(e);
                    }
                }
            }
            list.Reverse();
            return Json.Write(Json.Obj().Add("damage", DamageCount).Add("deaths", DeathCount)
                .Add("probes", ProbeCount).Add("cursor", Cursor).Add("events", list));
        }
    }
}
