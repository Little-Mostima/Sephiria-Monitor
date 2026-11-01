using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Merchant ops for live verification: list merchants with their shelf contents, teleport the
    /// local player next to one, and open the shop panel exactly the way Safe.cs:164 does.
    /// Everything runs on the Unity main thread through MainThreadPump; /tp and /shop mutate game
    /// state (player position, open panel) and are documented as such.
    /// </summary>
    public static class MerchantOps
    {
        public static string Merchants()
        {
            JsonObject root = Json.Obj();
            List<object> list = new List<object>();
            List<UnitAI_NewBasic> all = Collect();
            for (int i = 0; i < all.Count; i++)
            {
                list.Add(Describe(all[i], i));
            }
            root["count"] = list.Count;
            root["merchants"] = list;
            return Json.Write(root);
        }

        /// <summary>
        /// Teleports the local player. Either to absolute world coordinates (x,y), or next to a
        /// merchant picked by index / name substring / type (to=vendor|union|small|potion).
        /// </summary>
        public static string Teleport(string index, string name, string to, float offsetX, float offsetY, float? absX, float? absY)
        {
            PlayerAvatar player = LocalPlayer();
            JsonObject root = Json.Obj();
            if (player == null)
            {
                root["error"] = "no local player";
                return Json.Write(root);
            }
            if (absX.HasValue || absY.HasValue)
            {
                Vector3 from = player.transform.position;
                Vector3 abs = new Vector3(absX ?? from.x, absY ?? from.y, from.z);
                player.ReqSetPosition(abs, true);
                root["teleportedTo"] = "coordinates";
                root["from"] = Pair(from.x, from.y);
                root["position"] = Pair(abs.x, abs.y);
                root["distance"] = R(Vector3.Distance(from, abs));
                return Json.Write(root);
            }
            if (!string.IsNullOrEmpty(index) || !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(to))
            {
                UnitAI_NewBasic target = Pick(index, name, to);
                if (target == null || target.Avatar == null)
                {
                    root["error"] = "merchant not found";
                    root["merchants"] = Merchants();
                    return Json.Write(root);
                }
                Vector3 pos = target.Avatar.transform.position;
                Vector3 dest = new Vector3(pos.x + offsetX, pos.y + offsetY, player.transform.position.z);
                player.ReqSetPosition(dest, true);
                root["teleportedTo"] = target.name;
                root["merchant"] = Describe(target, -1);
                root["position"] = Pair(dest.x, dest.y);
            }
            else
            {
                root["error"] = "pass index=, name=, to=, or x=/y=";
            }
            return Json.Write(root);
        }

        /// <summary>Opens the shop panel for the chosen merchant (same argument list as Safe.cs).</summary>
        public static string OpenShop(string index, string name, string to)
        {
            JsonObject root = Json.Obj();
            PlayerAvatar player = LocalPlayer();
            if (player == null)
            {
                root["error"] = "no local player";
                return Json.Write(root);
            }
            UnitAI_NewBasic merchant = Pick(index, name, to);
            if (merchant == null || merchant.Avatar == null)
            {
                root["error"] = "merchant not found";
                return Json.Write(root);
            }
            GridInventory shelf = merchant.CurrentSelling;
            if (shelf == null)
            {
                root["error"] = "merchant has no shelf (CurrentSelling == null)";
                return Json.Write(root);
            }

            ETradeType tradeType = ETradeType.BuyAndSell;
            SocialIDEntity social = SocialIDDatabase.FindByName(merchant.socialID);
            if (social != null)
            {
                tradeType = social.tradeType;
            }

            UI_ShopPanel panel = UIManager.Instance.GetElement<UI_ShopPanel>();
            if (panel == null)
            {
                root["error"] = "UI_ShopPanel not available yet";
                return Json.Write(root);
            }
            try
            {
                panel.Open(player.Name, player, player.Inventory, merchant.Avatar.Name, merchant.Avatar, shelf, tradeType, merchant.socialID);
                root["opened"] = true;
                root["isOpened"] = panel.IsOpened;
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            root["tradeType"] = tradeType.ToString();
            root["merchant"] = Describe(merchant, -1);
            root["shelf"] = ShelfList(shelf);
            return Json.Write(root);
        }

        /// <summary>Generated floors of the current run with their planned merchant type.</summary>
        public static string Floors()
        {
            JsonObject root = Json.Obj();
            List<object> list = new List<object>();
            DungeonManager dm = DungeonManager.Instance;
            if (dm != null && dm.generatedFloors != null)
            {
                foreach (KeyValuePair<string, FloorData> kv in dm.generatedFloors)
                {
                    FloorData f = kv.Value;
                    if (f == null)
                    {
                        continue;
                    }
                    JsonObject o = Json.Obj();
                    o["guid"] = kv.Key;
                    o["name"] = f.name;
                    o["stageName"] = f.stageName;
                    o["merchantType"] = ((EProceduralMerchantType)f.generateMerchantType).ToString();
                    o["threat"] = f.threatType.ToString();
                    o["mainEvent"] = f.mainEventType.ToString();
                    o["hasTraveler"] = f.hasTraveler;
                    o["isHidden"] = f.isHidden;
                    o["difficulty"] = f.difficulty;
                    o["seed"] = f.seed;
                    list.Add(o);
                }
            }
            root["count"] = list.Count;
            root["floors"] = list;
            root["current"] = CurrentFloorGuid();
            return Json.Write(root);
        }

        /// <summary>Moves the local player to another generated floor (write op, triggers a loading screen).</summary>
        public static string Goto(string target, string to, bool includeHidden)
        {
            JsonObject root = Json.Obj();
            PlayerAvatar player = LocalPlayer();
            DungeonManager dm = DungeonManager.Instance;
            if (player == null || dm == null || dm.generatedFloors == null)
            {
                root["error"] = "not in a run";
                return Json.Write(root);
            }
            FloorData pick = null;
            string pickGuid = null;
            EProceduralMerchantType filter;
            bool useFilter = TryType(to, out filter);
            foreach (KeyValuePair<string, FloorData> kv in dm.generatedFloors)
            {
                FloorData f = kv.Value;
                if (f == null || (f.isHidden && !includeHidden))
                {
                    continue;
                }
                if (useFilter)
                {
                    if ((EProceduralMerchantType)f.generateMerchantType == filter)
                    {
                        pick = f;
                        pickGuid = kv.Key;
                        break;
                    }
                    continue;
                }
                if (!string.IsNullOrEmpty(target))
                {
                    if (string.Equals(kv.Key, target, StringComparison.OrdinalIgnoreCase) ||
                        (f.name ?? "").IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pick = f;
                        pickGuid = kv.Key;
                        break;
                    }
                }
            }
            if (pick == null)
            {
                root["error"] = "floor not found";
                return Json.Write(root);
            }
            root["movedTo"] = pick.name;
            root["guid"] = pickGuid;
            root["merchantType"] = ((EProceduralMerchantType)pick.generateMerchantType).ToString();
            try
            {
                dm.MoveFloor(player, pickGuid, "", 0);
                root["moved"] = true;
            }
            catch (Exception ex)
            {
                root["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return Json.Write(root);
        }

        /// <summary>
        /// Calls the real trader-stock roller (UnitAI_NewBasic.AddTradingItemsToList) with a chosen
        /// extraDamageCharmRatio and counts what lands in the guaranteed extra-damage slot.
        /// Pure data: nothing is written to any inventory.
        /// </summary>
        public static string Sample(int runs, int charms, int tablets, double ratio, int seedBase)
        {
            JsonObject root = Json.Obj();
            if (runs < 1) runs = 1;
            if (runs > 500) runs = 500;
            int withExtra = 0;
            int extraTotal = 0;
            int withTablets = 0;
            Dictionary<int, int> ids = new Dictionary<int, int>();
            List<object> hits = new List<object>();
            for (int i = 0; i < runs; i++)
            {
                List<ItemMetadata> list = new List<ItemMetadata>();
                System.Random random = new System.Random(seedBase + i);
                UnitAI_NewBasic.AddTradingItemsToList(random, list, charms, tablets, ratio);
                int extra = 0;
                bool tablet = false;
                for (int k = 0; k < list.Count; k++)
                {
                    ItemEntity entity = ItemDatabase.FindItemById(list[k].entityID);
                    if (entity == null)
                    {
                        continue;
                    }
                    if (entity.isExtraDamage)
                    {
                        extra++;
                        int n;
                        ids.TryGetValue(entity.id, out n);
                        ids[entity.id] = n + 1;
                    }
                    if (entity.type == EItemType.StoneTablet)
                    {
                        tablet = true;
                    }
                }
                if (tablet) withTablets++;
                if (extra > 0)
                {
                    withExtra++;
                    extraTotal += extra;
                    if (hits.Count < 8)
                    {
                        hits.Add(new List<object> { seedBase + i, extra, list.Count });
                    }
                }
            }
            root["runs"] = runs;
            root["ratio"] = ratio;
            root["charms"] = charms;
            root["tablets"] = tablets;
            root["withExtraDamage"] = withExtra;
            root["extraDamageRate"] = runs == 0 ? 0 : Math.Round((double)withExtra / runs, 3);
            root["extraDamageItems"] = extraTotal;
            root["withTablets"] = withTablets;
            List<object> dist = new List<object>();
            foreach (KeyValuePair<int, int> kv in ids)
            {
                JsonObject d = Json.Obj();
                d["id"] = kv.Key;
                ItemEntity entity = ItemDatabase.FindItemById(kv.Key);
                d["name"] = entity != null ? entity.aName : "";
                d["count"] = kv.Value;
                dist.Add(d);
            }
            root["ids"] = dist;
            root["samples"] = hits;
            return Json.Write(root);
        }

        private static string CurrentFloorGuid()
        {
            DungeonManager dm = DungeonManager.Instance;
            if (dm == null || dm.generatedFloors == null)
            {
                return "";
            }
            foreach (KeyValuePair<string, FloorData> kv in dm.generatedFloors)
            {
                if (kv.Value != null && kv.Value.name == SceneTracker.Current)
                {
                    return kv.Key;
                }
            }
            return "";
        }

        private static bool TryType(string to, out EProceduralMerchantType type)
        {
            switch ((to ?? "").ToLowerInvariant())
            {
                case "vendor": type = EProceduralMerchantType.Vendor; return true;
                case "union": type = EProceduralMerchantType.MerchantUnionVendor; return true;
                case "small": type = EProceduralMerchantType.SmallVendor; return true;
                case "potion": type = EProceduralMerchantType.PotionVendor; return true;
                default: type = EProceduralMerchantType.None; return false;
            }
        }

        private static List<UnitAI_NewBasic> Collect()
        {
            List<UnitAI_NewBasic> result = new List<UnitAI_NewBasic>();
            foreach (UnityEngine.Object o in StateApi.Instances(typeof(UnitAI_NewBasic)))
            {
                UnitAI_NewBasic ai = o as UnitAI_NewBasic;
                if (ai == null || ai.Avatar == null)
                {
                    continue;
                }
                bool shop = ai.CurrentSelling != null;
                bool named = !string.IsNullOrEmpty(ai.socialID);
                if (shop || named)
                {
                    result.Add(ai);
                }
            }
            result.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return result;
        }

        private static UnitAI_NewBasic Pick(string index, string name, string to)
        {
            List<UnitAI_NewBasic> all = Collect();
            if (all.Count == 0)
            {
                return null;
            }
            int wanted;
            if (!string.IsNullOrEmpty(index) && int.TryParse(index, out wanted))
            {
                return wanted >= 0 && wanted < all.Count ? all[wanted] : null;
            }
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < all.Count; i++)
                {
                    string label = all[i].Avatar != null ? all[i].Avatar.Name : "";
                    if ((all[i].name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (label ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (all[i].socialID ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return all[i];
                    }
                }
                return null;
            }
            EProceduralMerchantType filter;
            switch ((to ?? "").ToLowerInvariant())
            {
                case "vendor": filter = EProceduralMerchantType.Vendor; break;
                case "union": filter = EProceduralMerchantType.MerchantUnionVendor; break;
                case "small": filter = EProceduralMerchantType.SmallVendor; break;
                case "potion": filter = EProceduralMerchantType.PotionVendor; break;
                default: filter = EProceduralMerchantType.None; break;
            }
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].CurrentSelling == null)
                {
                    continue;
                }
                if (filter == EProceduralMerchantType.None)
                {
                    return all[i];
                }
                if (TypeOf(all[i]) == filter)
                {
                    return all[i];
                }
            }
            return null;
        }

        private static PlayerAvatar LocalPlayer()
        {
            List<PlayerSpawner> players = PlayerSpawner.MultiplayerList;
            if (players == null)
            {
                return null;
            }
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] != null && players[i].PlayerAvatar != null)
                {
                    return players[i].PlayerAvatar;
                }
            }
            return null;
        }

        private static EProceduralMerchantType TypeOf(UnitAI_NewBasic ai)
        {
            SocialIDEntity e = SocialIDDatabase.FindByName(ai.socialID);
            return e != null ? e.proceduralMerchantType : EProceduralMerchantType.None;
        }

        private static JsonObject Describe(UnitAI_NewBasic ai, int index)
        {
            UnitAvatar avatar = ai.Avatar;
            GridInventory shelf = ai.CurrentSelling;
            JsonObject o = Json.Obj();
            if (index >= 0)
            {
                o["index"] = index;
            }
            o["object"] = ai.name;
            o["name"] = avatar != null ? avatar.Name : "";
            o["socialID"] = ai.socialID ?? "";
            o["merchantType"] = TypeOf(ai).ToString();
            o["hasShelf"] = shelf != null;
            o["shelfItems"] = shelf != null ? CountItems(shelf) : 0;
            if (avatar != null)
            {
                o["hp"] = R(avatar.hp);
                o["negotiation"] = avatar.GetCustomStatUnsafe("NEGOTIATION");
                Vector3 p = avatar.transform.position;
                o["position"] = Pair(p.x, p.y);
            }
            return o;
        }

        private static List<object> ShelfList(GridInventory inv)
        {
            List<object> items = new List<object>();
            if (inv == null)
            {
                return items;
            }
            foreach (KeyValuePair<ItemPosition, NewItemOwnInstance> kv in inv.inventoryMatrix)
            {
                NewItemOwnInstance item = kv.Value;
                if (item == null)
                {
                    continue;
                }
                JsonObject o = Json.Obj();
                o["x"] = kv.Key.x;
                o["y"] = kv.Key.y;
                o["id"] = item.EntityID;
                o["qty"] = item.Quantity;
                o["instanceID"] = item.InstanceID.ToString(CultureInfo.InvariantCulture);
                ItemEntity entity = ItemDatabase.FindItemById(item.EntityID);
                if (entity != null)
                {
                    o["name"] = entity.aName;
                    o["rarity"] = entity.rarity.ToString();
                    o["cost"] = entity.cost;
                    o["type"] = entity.type.ToString();
                    o["extraDamage"] = entity.isExtraDamage;
                }
                if (item.Charm != null)
                {
                    o["prefab"] = item.Charm.name;
                    o["charmLevel"] = item.Charm.DisplayedLevel;
                }
                if (item.StoneTablet != null)
                {
                    o["prefab"] = item.StoneTablet.name;
                    o["rotation"] = item.StoneTablet.rotation;
                }
                items.Add(o);
            }
            return items;
        }

        private static int CountItems(GridInventory inv)
        {
            int n = 0;
            if (inv == null)
            {
                return 0;
            }
            foreach (KeyValuePair<ItemPosition, NewItemOwnInstance> kv in inv.inventoryMatrix)
            {
                if (kv.Value != null)
                {
                    n++;
                }
            }
            return n;
        }

        private static List<object> Pair(float x, float y)
        {
            return new List<object> { R(x), R(y) };
        }

        private static float R(float v)
        {
            return (float)Math.Round(v, 2);
        }
    }
}
