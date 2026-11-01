using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Generic "reach any place" ops: list interactables (Interactable subclasses) with their floor,
    /// teleport to one and fire its interaction (the same path the player's F key uses:
    /// Interactable.Interactive(actor) -> DoInteraction UnityEvent), and enumerate/jump the rooms of
    /// the current procedural floor (TileBasedRoomInstance bounds).
    /// Teleporting and interacting mutate game state; everything runs on the main thread.
    /// </summary>
    public static class WorldOps
    {
        public static string Spots(string type, string name, int max, bool all, float near)
        {
            JsonObject root = Json.Obj();
            PlayerAvatar player = LocalPlayer();
            if (player == null)
            {
                root["error"] = "no local player";
                return Json.Write(root);
            }
            Vector3 p = player.transform.position;
            string current = CurrentFloorName();
            Interactable[] found = UnityEngine.Object.FindObjectsByType<Interactable>(FindObjectsSortMode.None);
            Array.Sort(found, delegate (Interactable a, Interactable b)
            {
                return Vector3.Distance(a.transform.position, p).CompareTo(Vector3.Distance(b.transform.position, p));
            });
            List<object> list = new List<object>();
            int total = 0;
            foreach (Interactable it in found)
            {
                if (it == null)
                {
                    continue;
                }
                string floor = FloorOf(it.transform);
                float d0 = Vector3.Distance(it.transform.position, p);
                if (!all && !string.IsNullOrEmpty(current) && floor != null && floor != current)
                {
                    continue;
                }
                if (!all && floor == null && d0 > 120f)
                {
                    continue;
                }
                string tn = it.GetType().Name;
                if (!string.IsNullOrEmpty(type) && tn.IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(name) && it.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                float d = Vector3.Distance(it.transform.position, p);
                if (near > 0f && d > near)
                {
                    continue;
                }
                int idx = total;
                total++;
                if (list.Count >= max)
                {
                    continue;
                }
                JsonObject o = Json.Obj();
                o["index"] = idx;
                o["name"] = it.name;
                o["type"] = tn;
                o["floor"] = floor;
                o["pos"] = Pair(it.transform.position.x, it.transform.position.y);
                o["distance"] = R(d);
                o["enabled"] = it.enabled;
                o["interactable"] = it.IsInteractable(player.gameObject);
                o["interaction"] = Text(delegate { return it.GetInteractionString(); });
                o["interactionType"] = it.interactionType.ToString();
                list.Add(o);
            }
            root["floor"] = current;
            root["total"] = total;
            root["shown"] = list.Count;
            root["points"] = list;
            return Json.Write(root);
        }

        public static string Use(string index, string name, string type, bool tp, float offsetX, float offsetY)
        {
            JsonObject root = Json.Obj();
            PlayerAvatar player = LocalPlayer();
            if (player == null)
            {
                root["error"] = "no local player";
                return Json.Write(root);
            }
            Interactable target = Pick(index, name, type, player);
            if (target == null)
            {
                root["error"] = "interactable not found";
                root["spots"] = Spots(type, name, 20, false, 0f);
                return Json.Write(root);
            }
            Vector3 from = player.transform.position;
            if (tp)
            {
                Vector3 dest = new Vector3(target.transform.position.x + offsetX, target.transform.position.y + offsetY, from.z);
                player.ReqSetPosition(dest, true);
                root["teleported"] = true;
                root["from"] = Pair(from.x, from.y);
                root["to"] = Pair(dest.x, dest.y);
            }
            root["used"] = true;
            root["name"] = target.name;
            root["type"] = target.GetType().Name;
            root["floor"] = FloorOf(target.transform);
            root["pos"] = Pair(target.transform.position.x, target.transform.position.y);
            root["interaction"] = Text(delegate { return target.GetInteractionString(); });
            root["interactionType"] = target.interactionType.ToString();
            target.Interactive(player.gameObject);
            return Json.Write(root);
        }

        public static string Rooms()
        {
            JsonObject root = Json.Obj();
            PlayerAvatar player = LocalPlayer();
            FloorGenerator gen = NearestGenerator(player);
            if (gen == null)
            {
                root["error"] = "no FloorGenerator";
                return Json.Write(root);
            }
            root["floor"] = gen.name;
            root["generator"] = gen.GetType().Name;
                        List<object> list = new List<object>();
            HashSet<TileBasedRoomInstance> seen = new HashSet<TileBasedRoomInstance>();
            IDictionary rooms = Field(gen, "roomsByPosition") as IDictionary;
            if (rooms != null)
            {
                foreach (DictionaryEntry entry in rooms)
                {
                    TileBasedRoomInstance room = entry.Value as TileBasedRoomInstance;
                    if (room == null || !seen.Add(room))
                    {
                        continue;
                    }
                    list.Add(DescribeRoom(entry.Key, room, list.Count));
                }
            }
            IList hidden = Field(gen, "hiddenRoomInstances") as IList;
            if (hidden != null)
            {
                foreach (object o in hidden)
                {
                    TileBasedRoomInstance room = o as TileBasedRoomInstance;
                    if (room != null)
                    {
                        list.Add(DescribeRoom(null, room, list.Count));
                    }
                }
            }
            root["count"] = list.Count;
            root["rooms"] = list;
            return Json.Write(root);
        }

        public static string Jump(string room, string index, float offsetX, float offsetY)
        {
            JsonObject root = Json.Obj();
            PlayerAvatar player = LocalPlayer();
            if (player == null)
            {
                root["error"] = "no local player";
                return Json.Write(root);
            }
            FloorGenerator gen = NearestGenerator(player);
            if (gen == null)
            {
                root["error"] = "no FloorGenerator";
                return Json.Write(root);
            }
            IDictionary rooms = Field(gen, "roomsByPosition") as IDictionary;
            if (rooms == null)
            {
                root["error"] = "floor is not procedural (no roomsByPosition)";
                root["generator"] = gen.GetType().Name;
                return Json.Write(root);
            }
            TileBasedRoomInstance pick = null;
            int want = -1;
            if (!string.IsNullOrEmpty(index))
            {
                int parsed;
                if (int.TryParse(index, out parsed))
                {
                    want = parsed;
                }
            }
            int i = 0;
            foreach (DictionaryEntry entry in rooms)
            {
                TileBasedRoomInstance cand = entry.Value as TileBasedRoomInstance;
                if (cand == null)
                {
                    continue;
                }
                string nm = cand.Metadata != null ? cand.Metadata.name : null;
                if (want >= 0 ? i == want : (!string.IsNullOrEmpty(room) && nm != null && nm.IndexOf(room, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    pick = cand;
                    break;
                }
                i++;
            }
            if (pick == null)
            {
                root["error"] = "room not found";
                root["rooms"] = Rooms();
                return Json.Write(root);
            }
            Vector2 center = (pick.bottomLeft + pick.topRight) * 0.5f;
            Vector3 from = player.transform.position;
            Vector3 dest = new Vector3(center.x + offsetX, center.y + offsetY, from.z);
            player.ReqSetPosition(dest, true);
            root["jumped"] = true;
            root["room"] = DescribeRoom(null, pick, 0);
            root["from"] = Pair(from.x, from.y);
            root["to"] = Pair(dest.x, dest.y);
            return Json.Write(root);
        }

        private static JsonObject DescribeRoom(object key, TileBasedRoomInstance room, int index)
        {
            JsonObject o = Json.Obj();
            o["index"] = index;
            if (key != null)
            {
                Vector2Int g = (Vector2Int)key;
                o["grid"] = string.Concat(g.x, ",", g.y);
            }
            o["name"] = room.Metadata != null ? room.Metadata.name : "?";
            o["type"] = room.Metadata != null ? room.Metadata.type.ToString() : null;
            o["roomLevel"] = room.roomLevel;
            o["passage"] = room.passage;
            o["size"] = string.Concat(room.size.x, "x", room.size.y);
            o["center"] = Pair((room.bottomLeft.x + room.topRight.x) * 0.5f, (room.bottomLeft.y + room.topRight.y) * 0.5f);
            o["bottomLeft"] = Pair(room.bottomLeft.x, room.bottomLeft.y);
            o["topRight"] = Pair(room.topRight.x, room.topRight.y);
            o["teleportPoint"] = Pair(room.TeleportPoint.x, room.TeleportPoint.y);
            o["revealed"] = room.isRevealedOnServer;
            return o;
        }

        private static Interactable Pick(string index, string name, string type, PlayerAvatar player)
        {
            int want = -1;
            if (!string.IsNullOrEmpty(index))
            {
                int parsed;
                if (int.TryParse(index, out parsed))
                {
                    want = parsed;
                }
            }
            Interactable[] found = UnityEngine.Object.FindObjectsByType<Interactable>(FindObjectsSortMode.None);
            Vector3 p = player.transform.position;
            Array.Sort(found, delegate (Interactable a, Interactable b)
            {
                return Vector3.Distance(a.transform.position, p).CompareTo(Vector3.Distance(b.transform.position, p));
            });
            string current = CurrentFloorName();
            int i = 0;
            foreach (Interactable it in found)
            {
                if (it == null)
                {
                    continue;
                }
                string floor = FloorOf(it.transform);
                if (!string.IsNullOrEmpty(current) && floor != null && floor != current)
                {
                    continue;
                }
                string tn = it.GetType().Name;
                if (!string.IsNullOrEmpty(type) && tn.IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (want >= 0)
                {
                    if (i == want)
                    {
                        return it;
                    }
                    i++;
                    continue;
                }
                if (!string.IsNullOrEmpty(name) && it.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return it;
                }
                if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(type))
                {
                    return it;
                }
            }
            return null;
        }

        private static FloorGenerator NearestGenerator(PlayerAvatar player)
        {
            FloorGenerator[] all = UnityEngine.Object.FindObjectsByType<FloorGenerator>(FindObjectsSortMode.None);
            FloorGenerator best = null;
            float bestD = float.MaxValue;
            Vector3 p = player != null ? player.transform.position : Vector3.zero;
            foreach (FloorGenerator g in all)
            {
                if (g == null)
                {
                    continue;
                }
                float d = Vector3.Distance(g.transform.position, p);
                if (d < bestD)
                {
                    bestD = d;
                    best = g;
                }
            }
            return best;
        }

        private static string CurrentFloorName()
        {
            PlayerAvatar player = LocalPlayer();
            FloorGenerator g = player != null ? NearestGenerator(player) : null;
            return g != null ? g.name : null;
        }

        private static string FloorOf(Transform t)
        {
            Transform cur = t;
            while (cur != null)
            {
                FloorGenerator g = cur.GetComponent<FloorGenerator>();
                if (g != null)
                {
                    return g.name;
                }
                cur = cur.parent;
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

        private static object Field(object obj, string name)
        {
            if (obj == null)
            {
                return null;
            }
            Type t = obj.GetType();
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    return f.GetValue(obj);
                }
                t = t.BaseType;
            }
            return null;
        }

        private static string Text(Func<string> fn)
        {
            try
            {
                return fn();
            }
            catch (Exception e)
            {
                return "(" + e.GetType().Name + ")";
            }
        }

        private static List<object> Pair(float x, float y)
        {
            List<object> p = new List<object>();
            p.Add(R(x));
            p.Add(R(y));
            return p;
        }

        private static float R(float v)
        {
            return (float)Math.Round(v, 2);
        }
    }
}
