using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace SephiriaMonitor
{
    /// <summary>
    /// Passive floor recorder. On every floor allocation it reads the generator's own data —
    /// the room table (TileBasedRoomInstance + TestRoomMetadata) and the tilemaps that make up
    /// the collision box (ground / wall / water / cliffCollider) — and dumps it to
    /// &lt;session&gt;/floors/floor-NNNN.json plus a cached copy the bridge serves at /floor.
    /// Nothing is computed on demand: this is capture-only, one dump per floor.
    /// </summary>
    public static class FloorDump
    {
        public static int Count { get; private set; }
        public static string LastFloor { get; private set; }
        public static string LastFile { get; private set; }

        private static string cachedJson = "";
        private static byte[] grid;              // 0 可走, 1 墙/悬崖/虚空, 2 水
        private static int gw;
        private static int gh;
        private static int gx0;
        private static int gy0;
        private static List<object> lastRooms = new List<object>();
        private static List<object> lastRoads = new List<object>();
        private static List<object> lastPassages = new List<object>();
        private static string lastGuid = "";
        private static string lastName = "";
        private static bool pending;
        private static float deadline;
        private static int attempts;
        private static string pendingName = "";
        private static string pendingGuid = "";
        private static float nextTry;

        /// <summary>Called from the floor-allocation hook; the world may still be building, so retry.</summary>
        public static void Request(string guid, string floorName)
        {
            pending = true;
            attempts = 0;
            nextTry = 0f;
            deadline = Time.realtimeSinceStartup + 20f;
            pendingName = floorName == null ? "" : floorName;
            pendingGuid = guid == null ? "" : guid;
        }

        public static void Tick()
        {
            if (!pending)
            {
                return;
            }
            float t = Time.realtimeSinceStartup;
            if (t < nextTry)
            {
                return;
            }
            nextTry = t + 0.5f;
            attempts++;
            try
            {
                string json = Build(pendingGuid, pendingName);
                if (json == null)
                {
                    if (t > deadline)
                    {
                        pending = false;
                        MonitorPlugin.LogInfo("FLOOR|dump|gaveup|" + pendingName);
                    }
                    return;
                }
                cachedJson = json;
                pending = false;
                Count++;
                LastFloor = pendingName;
                LastFile = Save(json, pendingName);
                MonitorPlugin.LogInfo("FLOOR|dump|" + pendingName + "|attempts=" + attempts + "|file=" + LastFile);
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogInfo("FLOOR|dump|error|" + ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static string Render()
        {
            if (cachedJson.Length > 0)
            {
                return cachedJson;
            }
            return Json.Write(Json.Obj().Add("ready", false).Add("hint", "no floor captured yet; enter a dungeon floor"));
        }

        /// <summary>Floor progress: a room counts as cleared when no living hostile stands inside it.</summary>
        public static object RoomProgress()
        {
            int total = 0;
            int cleared = 0;
            try
            {
                List<object> rooms = lastRooms;
                total = rooms == null ? 0 : rooms.Count;
                if (total > 0)
                {
                    List<UnitAvatar> units = UnitRegistry.Snapshot();
                    PlayerAvatar player = null;
                    for (int i = 0; i < units.Count; i++)
                    {
                        if (units[i] is PlayerAvatar) { player = (PlayerAvatar)units[i]; break; }
                    }
                    string faction = player != null ? StateApi.FactionOf(player) : "";
                    for (int i = 0; i < rooms.Count; i++)
                    {
                        Dictionary<string, object> r = rooms[i] as Dictionary<string, object>;
                        float[] rect = r != null && r.ContainsKey("rect") ? r["rect"] as float[] : null;
                        bool busy = false;
                        if (rect != null && rect.Length >= 4)
                        {
                            for (int u = 0; u < units.Count; u++)
                            {
                                UnitAvatar unit = units[u];
                                if (unit == null || unit.hp <= 0f || unit is PlayerAvatar) { continue; }
                                Vector3 p = unit.transform.position;
                                if (p.x < rect[0] || p.x > rect[2] || p.y < rect[1] || p.y > rect[3]) { continue; }
                                if (StateApi.IsHostileToPlayer(unit, faction)) { busy = true; break; }
                            }
                        }
                        if (!busy) { cleared++; }
                    }
                }
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("room progress: " + ex.Message);
            }
            return Json.Obj().Add("total", total).Add("cleared", cleared);
        }

        public static string Summary()
        {
            return Json.Write(Json.Obj()
                .Add("last", LastFloor == null ? "" : LastFloor)
                .Add("dumps", Count)
                .Add("pending", pending));
        }

        /// <summary>重新扫描当前楼层（瓦片可能晚于 floor 事件就绪）。</summary>
        public static void Rescan()
        {
            if (lastGuid.Length == 0 && lastName.Length == 0)
            {
                return;
            }
            Request(lastGuid, lastName);
        }

        /// <summary>房间/走廊/通道清单（来自生成器自己的房间表，不是猜测）。</summary>
        public static string RoomsJson()
        {
            if (lastRooms.Count == 0)
            {
                return Json.Write(Json.Obj().Add("error", "no rooms").Add("hint", "enter a dungeon floor"));
            }
            return Json.Write(Json.Obj()
                .Add("floor", lastName)
                .Add("count", lastRooms.Count)
                .Add("rooms", lastRooms)
                .Add("roads", lastRoads)
                .Add("passages", lastPassages));
        }

        /// <summary>以玩家为中心裁剪的可走地形图。</summary>
        public static string AsciiView(int radius, float? tx, float? ty)
        {
            if (grid == null || gw <= 0)
            {
                return Json.Write(Json.Obj().Add("error", "no floor grid").Add("hint", "enter a dungeon floor"));
            }
            int r = Mathf.Clamp(radius, 3, 60);
            float[] pp = PlayerPos();
            int px = Mathf.FloorToInt(pp[0]) - gx0;
            int py = Mathf.FloorToInt(pp[1]) - gy0;
            int txCell = int.MinValue;
            int tyCell = int.MinValue;
            if (tx.HasValue && ty.HasValue)
            {
                txCell = Mathf.FloorToInt(tx.Value) - gx0;
                tyCell = Mathf.FloorToInt(ty.Value) - gy0;
            }
            int x0 = Mathf.Max(0, px - r);
            int x1 = Mathf.Min(gw - 1, px + r);
            int y0 = Mathf.Max(0, py - r);
            int y1 = Mathf.Min(gh - 1, py + r);
            List<string> rows = new List<string>();
            for (int y = y1; y >= y0; y--)
            {
                StringBuilder sb = new StringBuilder(x1 - x0 + 1);
                for (int x = x0; x <= x1; x++)
                {
                    byte b = grid[y * gw + x];
                    char ch = b == 0 ? '.' : (b == 2 ? '~' : '#');
                    if (x == px && y == py)
                    {
                        ch = 'P';
                    }
                    else if (x == txCell && y == tyCell)
                    {
                        ch = '@';
                    }
                    sb.Append(ch);
                }
                rows.Add(sb.ToString());
            }
            return Json.Write(Json.Obj()
                .Add("floor", lastName)
                .Add("legend", "'.' 可走  '#' 墙/悬崖  '~' 水  'P' 玩家  '@' 目标")
                .Add("center", new float[] { pp[0], pp[1] })
                .Add("from", new int[] { gx0 + x0, gy0 + y0 })
                .Add("radius", r)
                .Add("rows", rows));
        }

        /// <summary>在瓦片网格上做 BFS，返回世界坐标路点（每 2 格一个）。</summary>
        public static string PathJson(float wx, float wy)
        {
            if (grid == null || gw <= 0)
            {
                return Json.Write(Json.Obj().Add("error", "no floor grid").Add("hint", "enter a dungeon floor"));
            }
            float[] pp = PlayerPos();
            int sx = Mathf.FloorToInt(pp[0]) - gx0;
            int sy = Mathf.FloorToInt(pp[1]) - gy0;
            int tx = Mathf.Clamp(Mathf.FloorToInt(wx) - gx0, 0, gw - 1);
            int ty = Mathf.Clamp(Mathf.FloorToInt(wy) - gy0, 0, gh - 1);
            if (sx < 0 || sy < 0 || sx >= gw || sy >= gh)
            {
                return Json.Write(Json.Obj().Add("error", "player outside grid").Add("player", new float[] { pp[0], pp[1] }));
            }
            int n = gw * gh;
            int[] prev = new int[n];
            for (int i = 0; i < n; i++)
            {
                prev[i] = -2;
            }
            int start = sy * gw + sx;
            int goal = ty * gw + tx;
            Queue<int> queue = new Queue<int>();
            prev[start] = -1;
            queue.Enqueue(start);
            bool found = false;
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (cur == goal)
                {
                    found = true;
                    break;
                }
                int cx = cur % gw;
                int cy = cur / gw;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + (k == 0 ? 1 : (k == 1 ? -1 : 0));
                    int ny = cy + (k == 2 ? 1 : (k == 3 ? -1 : 0));
                    if (nx < 0 || ny < 0 || nx >= gw || ny >= gh)
                    {
                        continue;
                    }
                    int ni = ny * gw + nx;
                    if (prev[ni] != -2 || grid[ni] != 0)
                    {
                        continue;
                    }
                    prev[ni] = cur;
                    queue.Enqueue(ni);
                }
            }
            if (!found)
            {
                return Json.Write(Json.Obj()
                    .Add("found", false)
                    .Add("start", new float[] { pp[0], pp[1] })
                    .Add("target", new float[] { wx, wy })
                    .Add("note", "目标不可达（墙/水/虚空阻隔）"));
            }
            List<int> cells = new List<int>();
            for (int c = goal; c != -1 && c != -2; c = prev[c])
            {
                cells.Add(c);
            }
            cells.Reverse();
            List<object> points = new List<object>();
            for (int i = 0; i < cells.Count; i++)
            {
                if (i % 2 == 0 || i == cells.Count - 1)
                {
                    int cx = cells[i] % gw;
                    int cy = cells[i] / gw;
                    points.Add(new float[] { gx0 + cx + 0.5f, gy0 + cy + 0.5f });
                }
            }
            return Json.Write(Json.Obj()
                .Add("found", true)
                .Add("cells", cells.Count)
                .Add("start", new float[] { pp[0], pp[1] })
                .Add("target", new float[] { wx, wy })
                .Add("points", points));
        }

        /// <summary>某一点上有哪些碰撞体（排查"走不过去"用）。</summary>
        public static string Probe(float wx, float wy)
        {
            Vector2 p = new Vector2(wx, wy);
            Collider2D[] hits = Physics2D.OverlapPointAll(p);
            List<object> list = new List<object>();
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null)
                {
                    continue;
                }
                UnitAvatar unit = c.GetComponentInParent<UnitAvatar>();
                list.Add(Json.Obj()
                    .Add("name", c.name)
                    .Add("collider", c.GetType().Name)
                    .Add("layer", LayerMask.LayerToName(c.gameObject.layer))
                    .Add("trigger", c.isTrigger)
                    .Add("min", new float[] { c.bounds.min.x, c.bounds.min.y })
                    .Add("size", new float[] { c.bounds.size.x, c.bounds.size.y })
                    .Add("unit", unit == null ? "" : unit.name));
            }
            return Json.Write(Json.Obj().Add("pos", new float[] { wx, wy }).Add("count", list.Count).Add("colliders", list));
        }

        // ---------------- build ----------------

        private static string Build(string guid, string floorName)
        {
            TileFloorGenerator gen = FindGenerator();
            if (gen == null)
            {
                return null;
            }
            Tilemap ground = gen.ground;
            if (ground == null)
            {
                return null;
            }
            string ascii = TileAscii(gen, out BoundsInt bounds, out int blocked, out int water);
            if (ascii.Length == 0)
            {
                return null;
            }
            JsonObject d = Json.Obj();
            d["floor"] = floorName;
            d["guid"] = guid;
            lastGuid = guid;
            lastName = floorName;
            d["scene"] = SceneTracker.Current;
            d["t"] = Time.realtimeSinceStartup;
            d["wall"] = DateTime.Now.ToString("HH:mm:ss");
            d["bounds"] = new int[] { bounds.xMin, bounds.yMin, bounds.size.x, bounds.size.y };
            d["blocked"] = blocked;
            d["water"] = water;
            d["legend"] = "'.' floor  '#' wall  '~' water  '%' cliff  ' ' void(no ground)";
            d["rows"] = new List<string>(ascii.Split('\n'));
            lastRooms = Rooms(gen, out List<object> roads, out List<object> passages);
            lastRoads = roads;
            lastPassages = passages;
            d["rooms"] = lastRooms;
            d["roads"] = roads;
            d["passages"] = passages;
            d["stairs"] = ObjectList(typeof(DungeonStair));
            d["doors"] = ObjectList(typeof(RoomDoor));
            d["portals"] = ObjectList(typeof(PortalToAnotherFloor));
            d["player"] = PlayerPos();
            return Json.Write(d);
        }

        private static float[] PlayerPos()
        {
            try
            {
                List<UnityEngine.Object> all = StateApi.Instances(typeof(PlayerAvatar));
                for (int i = 0; i < all.Count; i++)
                {
                    PlayerAvatar p = all[i] as PlayerAvatar;
                    if (p != null)
                    {
                        Vector3 v = p.transform.position;
                        return new float[] { v.x, v.y };
                    }
                }
            }
            catch
            {
            }
            return new float[] { 0f, 0f };
        }

        private static string TileAscii(TileFloorGenerator gen, out BoundsInt bounds, out int blocked, out int water)
        {
            blocked = 0;
            water = 0;
            Tilemap ground = gen.ground;
            Tilemap wall = gen.wall;
            Tilemap wet = gen.water;
            Tilemap cliff = gen.cliffCollider != null ? gen.cliffCollider : gen.cliff;
            bounds = ground.cellBounds;
            bounds = Union(bounds, wall == null ? default : wall.cellBounds);
            bounds = Union(bounds, wet == null ? default : wet.cellBounds);
            bounds = Union(bounds, cliff == null ? default : cliff.cellBounds);
            int w = bounds.size.x;
            int h = bounds.size.y;
            if (w <= 0 || h <= 0 || (long)w * h > 4000000L)
            {
                return "";
            }
            grid = new byte[w * h];
            gw = w;
            gh = h;
            gx0 = bounds.xMin;
            gy0 = bounds.yMin;
            StringBuilder sb = new StringBuilder((w + 1) * h);
            for (int y = bounds.yMax - 1; y >= bounds.yMin; y--)
            {
                for (int x = bounds.xMin; x < bounds.xMax; x++)
                {
                    Vector3Int c = new Vector3Int(x, y, 0);
                    char ch;
                    if (cliff != null && cliff.HasTile(c))
                    {
                        ch = '%';
                        blocked++;
                    }
                    else if (wall != null && wall.HasTile(c))
                    {
                        ch = '#';
                        blocked++;
                    }
                    else if (wet != null && wet.HasTile(c))
                    {
                        ch = '~';
                        water++;
                    }
                    else if (!ground.HasTile(c))
                    {
                        ch = ' ';
                        blocked++;
                    }
                    else
                    {
                        ch = '.';
                    }
                    sb.Append(ch);
                    grid[(y - gy0) * gw + (x - gx0)] = ch == '.' ? (byte)0 : (ch == '~' ? (byte)2 : (byte)1);
                }
                if (y > bounds.yMin)
                {
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        private static List<object> Rooms(TileFloorGenerator gen, out List<object> roads, out List<object> passages)
        {
            List<object> rooms = new List<object>();
            roads = new List<object>();
            passages = new List<object>();
            try
            {
                IList list = Field(gen, "roomList") as IList;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        Dictionary<string, object> r = RoomJson(list[i]);
                        if (r != null)
                        {
                            rooms.Add(r);
                        }
                    }
                }
                IList rl = Field(gen, "roadList") as IList;
                if (rl != null)
                {
                    for (int i = 0; i < rl.Count; i++)
                    {
                        Dictionary<string, object> r = RoomJson(rl[i]);
                        if (r != null)
                        {
                            roads.Add(r);
                        }
                    }
                }
                IList pl = Field(gen, "passageDataList") as IList;
                if (pl != null)
                {
                    for (int i = 0; i < pl.Count; i++)
                    {
                        if (pl[i] == null)
                        {
                            continue;
                        }
                        Type t = pl[i].GetType();
                        Vector2Int pos = (Vector2Int)(FieldOf(t, pl[i], "pos") ?? Vector2Int.zero);
                        passages.Add(Json.Obj()
                            .Add("gridPos", new int[] { pos.x, pos.y })
                            .Add("dir", Convert.ToInt32(FieldOf(t, pl[i], "dir") ?? 0))
                            .Add("index", Convert.ToInt32(FieldOf(t, pl[i], "passageIndex") ?? 0))
                            .Add("road", FieldOf(t, pl[i], "isFromRoad") is bool b && b));
                    }
                }
            }
            catch
            {
            }
            return rooms;
        }

        private static Dictionary<string, object> RoomJson(object instance)
        {
            TileBasedRoomInstance room = instance as TileBasedRoomInstance;
            if (room == null)
            {
                return null;
            }
            Dictionary<string, object> r = Json.Obj()
                .Add("id", room.id)
                .Add("gridPos", new int[] { room.pos.x, room.pos.y })
                .Add("size", new int[] { room.size.x, room.size.y })
                .Add("rect", new float[] { room.bottomLeft.x, room.bottomLeft.y, room.topRight.x, room.topRight.y })
                .Add("passage", room.passage)
                .Add("level", room.roomLevel)
                .Add("teleport", new float[] { room.TeleportPoint.x, room.TeleportPoint.y });
            if (room.Metadata != null)
            {
                r.Add("name", room.Metadata.name);
                r.Add("tag", room.Metadata.tag);
                r.Add("type", room.Metadata.type.ToString());
                r.Add("spawnMonster", room.Metadata.spawnMonster);
                r.Add("density", room.Metadata.monsterDensity);
            }
            r.Add("units", UnitsIn(room.bottomLeft, room.topRight));
            return r;
        }

        private static int UnitsIn(Vector2 bottomLeft, Vector2 topRight)
        {
            int n = 0;
            try
            {
                List<UnityEngine.Object> all = StateApi.Instances(typeof(UnitAvatar));
                for (int i = 0; i < all.Count; i++)
                {
                    UnitAvatar u = all[i] as UnitAvatar;
                    if (u == null || u.hp <= 0f)
                    {
                        continue;
                    }
                    Vector3 p = u.transform.position;
                    if (p.x >= bottomLeft.x && p.x <= topRight.x && p.y >= bottomLeft.y && p.y <= topRight.y)
                    {
                        n++;
                    }
                }
            }
            catch
            {
            }
            return n;
        }

        private static List<object> ObjectList(Type type)
        {
            List<object> list = new List<object>();
            try
            {
                List<UnityEngine.Object> all = StateApi.Instances(type);
                for (int i = 0; i < all.Count; i++)
                {
                    Component c = all[i] as Component;
                    if (c == null)
                    {
                        continue;
                    }
                    Vector3 p = c.transform.position;
                    list.Add(Json.Obj().Add("name", c.name).Add("pos", new float[] { p.x, p.y }));
                }
            }
            catch
            {
            }
            return list;
        }

        private static TileFloorGenerator FindGenerator()
        {
            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(TileFloorGenerator));
                for (int i = 0; i < all.Length; i++)
                {
                    TileFloorGenerator g = all[i] as TileFloorGenerator;
                    if (g != null && g.gameObject != null && g.gameObject.scene.IsValid())
                    {
                        return g;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static BoundsInt Union(BoundsInt a, BoundsInt b)
        {
            if (b.size.x <= 0 || b.size.y <= 0)
            {
                return a;
            }
            int xMin = Mathf.Min(a.xMin, b.xMin);
            int yMin = Mathf.Min(a.yMin, b.yMin);
            int xMax = Mathf.Max(a.xMax, b.xMax);
            int yMax = Mathf.Max(a.yMax, b.yMax);
            return new BoundsInt(xMin, yMin, 0, xMax - xMin, yMax - yMin, 1);
        }

        private static string Save(string json, string floorName)
        {
            try
            {
                if (Session.Root == null)
                {
                    return "";
                }
                string dir = Path.Combine(Session.Root, "floors");
                Directory.CreateDirectory(dir);
                string safe = string.IsNullOrEmpty(floorName) ? "floor" : floorName.Replace("(Clone)", "").Replace(" ", "_");
                string file = Path.Combine(dir, "floor-" + (Count + 1).ToString("0000") + "-" + safe + ".json");
                File.WriteAllText(file, json, new UTF8Encoding(false));
                return file;
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogInfo("FLOOR|save|error|" + ex.Message);
                return "";
            }
        }

        private static object Field(object target, string name)
        {
            return target == null ? null : FieldOf(target.GetType(), target, name);
        }

        private static object FieldOf(Type type, object target, string name)
        {
            try
            {
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f == null ? null : f.GetValue(target);
            }
            catch
            {
                return null;
            }
        }
    }
}
