using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Full in-memory character / run / save snapshot. Values are flattened into key paths so two
    /// snapshots can be diffed without knowing the game's types (inventory, stats, progress, save keys).
    /// </summary>
    public static class Snapshot
    {
        private static Dictionary<string, string> previous;
        private static int total;
        private static int added;
        private static int removed;
        private static int changed;

        public static int Count { get { return total; } }
        public static int Added { get { return added; } }
        public static int Removed { get { return removed; } }
        public static int Changed { get { return changed; } }
        public static string LastPath { get; private set; }
        public static string LastReason { get; private set; }

        /// <summary>Key/value map of the latest capture (null before the first one).</summary>
        public static Dictionary<string, string> Current { get { return previous; } }

        public static string Capture(string reason)
        {
            if (!MainThreadPump.IsMainThread)
            {
                return MainThreadPump.Run(() => Capture(reason), 8000);
            }
            try
            {
                Dictionary<string, string> map = Build();
                List<string> addedKeys = new List<string>();
                List<string> removedKeys = new List<string>();
                List<string> changedKeys = new List<string>();
                int addedCount = 0;
                int removedCount = 0;
                int changedCount = 0;
                if (previous != null)
                {
                    foreach (KeyValuePair<string, string> kv in map)
                    {
                        string old;
                        if (!previous.TryGetValue(kv.Key, out old))
                        {
                            addedCount++;
                            if (addedKeys.Count < 200) addedKeys.Add(kv.Key);
                        }
                        else if (old != kv.Value)
                        {
                            changedCount++;
                            if (changedKeys.Count < 200) changedKeys.Add(kv.Key + ": " + old + " -> " + kv.Value);
                        }
                    }
                    foreach (KeyValuePair<string, string> kv in previous)
                    {
                        if (!map.ContainsKey(kv.Key))
                        {
                            removedCount++;
                            if (removedKeys.Count < 200) removedKeys.Add(kv.Key);
                        }
                    }
                }
                added = addedCount;
                removed = removedCount;
                changed = changedCount;
                total = map.Count;
                previous = map;
                LastReason = reason;
                LastPath = WriteFile(reason, map);

                Dictionary<string, object> e = Json.Obj();
                e["kind"] = "snapshot";
                e["reason"] = reason;
                e["keys"] = map.Count;
                e["added"] = addedCount;
                e["removed"] = removedCount;
                e["changed"] = changedCount;
                e["path"] = LastPath;
                e["addedKeys"] = addedKeys;
                e["removedKeys"] = removedKeys;
                e["changedKeys"] = changedKeys;
                Events.Add(e);
                return Json.Write(e);
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("snapshot: " + ex);
                return Json.Write(Json.Obj().Add("error", ex.GetType().Name).Add("message", ex.Message));
            }
        }

        private static string WriteFile(string reason, Dictionary<string, string> map)
        {
            string dir = Path.Combine(Session.Root ?? ".", "snapshots");
            Directory.CreateDirectory(dir);
            string json = Json.Write(map);
            // Two captures with the same reason inside one second must not overwrite each other.
            string stamp = DateTime.Now.ToString("HHmmss-fff");
            string path = Path.Combine(dir, "snapshot-" + reason + "-" + stamp + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            // state_full.json is the compact copy: keep level/exp/weapon/tablet/stat/run keys instead of ~930 save keys.
            Dictionary<string, string> slim = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in map)
            {
                if (IsInterestingKey(kv.Key)) { slim[kv.Key] = kv.Value; }
            }
            File.WriteAllText(Path.Combine(Session.Root ?? ".", "state_full.json"), Json.Write(slim), new UTF8Encoding(false));
            return path;
        }

        private static bool IsInterestingKey(string key)
        {
            if (string.IsNullOrEmpty(key)) { return false; }
            return key.StartsWith("p0.level", StringComparison.Ordinal)
                || key.StartsWith("p0.exp", StringComparison.Ordinal)
                || key.StartsWith("p0.weapon.", StringComparison.Ordinal)
                || key.StartsWith("p0.tablet", StringComparison.Ordinal)
                || key.StartsWith("p0.stat.", StringComparison.Ordinal)
                || key.StartsWith("p0.inv.items", StringComparison.Ordinal)
                || key.StartsWith("run.floor", StringComparison.Ordinal)
                || key.StartsWith("run.hard", StringComparison.Ordinal)
                || key.StartsWith("run.seed", StringComparison.Ordinal);
        }

        private static Dictionary<string, string> Build()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            d["scene"] = SceneTracker.Current;
            try
            {
                BuildPlayers(d);
            }
            catch (Exception ex)
            {
                d["error.players"] = ex.GetType().Name + ": " + ex.Message;
            }
            try
            {
                BuildRun(d);
            }
            catch (Exception ex)
            {
                d["error.run"] = ex.GetType().Name + ": " + ex.Message;
            }
            try
            {
                BuildSave(d);
            }
            catch (Exception ex)
            {
                d["error.save"] = ex.GetType().Name + ": " + ex.Message;
            }
            return d;
        }

        private static void BuildPlayers(Dictionary<string, string> d)
        {
            List<PlayerSpawner> list = PlayerSpawner.MultiplayerList;
            if (list == null || list.Count == 0)
            {
                d["players.count"] = "0";
                return;
            }
            d["players.count"] = list.Count.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < list.Count; i++)
            {
                PlayerSpawner spawner = list[i];
                if (spawner == null)
                {
                    continue;
                }
                string p = "p" + i + ".";
                PlayerAvatar avatar = spawner.PlayerAvatar;
                if (avatar == null)
                {
                    d[p + "missing"] = "true";
                    continue;
                }
                d[p + "name"] = spawner.name;
                d[p + "steamId"] = spawner.steamID.ToString(CultureInfo.InvariantCulture);
                d[p + "sapphire"] = N(spawner.sapphireInRun);
                d[p + "hp"] = F(avatar.hp);
                d[p + "maxHp"] = F(avatar.MaxHp);
                d[p + "mp"] = N(avatar.mp);
                d[p + "maxMp"] = N(avatar.MaxMp);
                d[p + "shield"] = F(avatar.Shield);
                d[p + "money"] = N(avatar.Money);
                d[p + "pos"] = avatar.transform.position.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                               avatar.transform.position.y.ToString("R", CultureInfo.InvariantCulture);
                d[p + "floorGuid"] = avatar.currentFloorGuid;
                d[p + "inDungeon"] = avatar.isInDungeon.ToString(CultureInfo.InvariantCulture);
                d[p + "dead"] = avatar.IsDead ? "1" : "0";
                d[p + "lifeUsed"] = avatar.additionalLifeUsed.ToString(CultureInfo.InvariantCulture);

                LevelController level = avatar.GetComponent<LevelController>();
                if (level != null)
                {
                    d[p + "level"] = N(level.currentLevel);
                    d[p + "exp"] = N(level.currentExp);
                    d[p + "statPoints"] = N(level.AvailableStatPoint);
                }
                WeaponControllerSimple weapons = avatar.GetComponent<WeaponControllerSimple>();
                if (weapons != null && weapons.currentWeapon != null)
                {
                    d[p + "weapon.type"] = weapons.currentWeapon.weaponType.ToString();
                    d[p + "weapon.entityId"] = N(weapons.currentWeapon.entityId);
                }
                BuildStats(d, p, avatar);
                BuildInventory(d, p, avatar.Inventory);
            }
        }

        private static void BuildStats(Dictionary<string, string> d, string p, UnitAvatar avatar)
        {
            Array values = Enum.GetValues(typeof(ECustomStat));
            for (int i = 0; i < values.Length; i++)
            {
                ECustomStat stat = (ECustomStat)values.GetValue(i);
                try
                {
                    d[p + "stat." + stat] = avatar.GetCustomStat(stat).ToString(CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }
        }

        private static void BuildInventory(Dictionary<string, string> d, string p, GridInventory inv)
        {
            if (inv == null)
            {
                return;
            }
            d[p + "inv.storage"] = N(inv.CurrentInventoryStorage);
            d[p + "inv.potionSlots"] = inv.numberOfPotionStorage.ToString(CultureInfo.InvariantCulture);
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
                d[k + "id"] = N(item.EntityID);
                d[k + "qty"] = item.Quantity.ToString(CultureInfo.InvariantCulture);
                if (item.Charm != null)
                {
                    d[k + "lv"] = N(item.Charm.DisplayedLevel);
                }
                if (item.StoneTablet != null)
                {
                    d[k + "rot"] = N(item.StoneTablet.rotation);
                }
                n++;
            }
            d[p + "inv.items"] = n.ToString(CultureInfo.InvariantCulture);
            int t = 0;
            foreach (KeyValuePair<ItemPosition, StoneTablet> kv in inv.stoneTablets)
            {
                if (kv.Value == null)
                {
                    continue;
                }
                d[p + "tablet." + kv.Key.x + "," + kv.Key.y + ".id"] = N(kv.Value.entityID);
                d[p + "tablet." + kv.Key.x + "," + kv.Key.y + ".rot"] = N(kv.Value.rotation);
                t++;
            }
            d[p + "tablets"] = t.ToString(CultureInfo.InvariantCulture);
        }

        private static void BuildRun(Dictionary<string, string> d)
        {
            DungeonManager dm = DungeonManager.Instance;
            if (dm == null)
            {
                d["run.dm"] = "null";
                return;
            }
            d["run.seed"] = N(dm.DestinySeed);
            foreach (KeyValuePair<string, int> kv in dm.dungeonEnvironment)
            {
                d["run.env." + kv.Key] = kv.Value.ToString(CultureInfo.InvariantCulture);
            }
            foreach (KeyValuePair<string, int> kv in dm.hardModeEnvironment)
            {
                d["run.hard." + kv.Key] = kv.Value.ToString(CultureInfo.InvariantCulture);
            }
            string guid = null;
            List<PlayerSpawner> list = PlayerSpawner.MultiplayerList;
            if (list != null && list.Count > 0 && list[0] != null && list[0].PlayerAvatar != null)
            {
                guid = list[0].PlayerAvatar.currentFloorGuid;
            }
            if (!string.IsNullOrEmpty(guid))
            {
                d["run.floorGuid"] = guid;
                FloorGenerator gen = FloorGenerator.FindByGuid(guid);
                if (gen != null)
                {
                    d["run.floor.name"] = gen.FloorDataName;
                    d["run.floor.seed"] = N(gen.seed);
                    d["run.floor.threat"] = gen.floorThreatType.ToString();
                    d["run.floor.mainEvent"] = gen.floorMainEventType.ToString();
                    d["run.floor.lucky"] = gen.luckyType.ToString();
                    d["run.floor.pocket"] = gen.pdType.ToString();
                    d["run.floor.rooms"] = N(gen.randomRoomCount);
                    d["run.floor.finalized"] = gen.IsFloorFinalized ? "1" : "0";
                }
            }
        }

        private static void BuildSave(Dictionary<string, string> d)
        {
            ReadSaveData(d, "save.", SaveManager.Current);
            ReadSaveData(d, "runSave.", SaveManager.CurrentRun);
        }

        private static void ReadSaveData(Dictionary<string, string> d, string prefix, SaveData data)
        {
            if (data == null)
            {
                d[prefix + "null"] = "1";
                return;
            }
            int n = 0;
            foreach (KeyValuePair<string, object> kv in data.BakedData)
            {
                object v = kv.Value;
                string text;
                if (v == null)
                {
                    text = "";
                }
                else if (v is string s)
                {
                    if (s.Length > 300)
                    {
                        continue;
                    }
                    text = s;
                }
                else if (v is float f)
                {
                    text = f.ToString("R", CultureInfo.InvariantCulture);
                }
                else
                {
                    text = v.ToString();
                    if (text.Length > 300)
                    {
                        continue;
                    }
                }
                d[prefix + kv.Key] = text;
                n++;
                if (n > 800)
                {
                    break;
                }
            }
            d[prefix + "keys"] = n.ToString(CultureInfo.InvariantCulture);
        }

        private static string N(int v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        private static string F(float v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
