using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>Loopback HTTP bridge so the agent can read game state without screenshots.</summary>
    public static class BridgeServer
    {
        public const int DefaultPort = MonitorConfig.DefaultBridgePort;

        /// <summary>Loopback port; comes from BepInEx config (default 47901).</summary>
        public static int Port { get { return MonitorConfig.BridgePort; } }

        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes = 64 * 1024;
        private const int SocketTimeoutMs = 3000;
        private const int MaxConcurrentHandlers = 8;

        private static TcpListener listener;
        private static CancellationTokenSource cts = new CancellationTokenSource();
        private static readonly SemaphoreSlim Slots = new SemaphoreSlim(MaxConcurrentHandlers, MaxConcurrentHandlers);
        private static volatile bool running;
        private static long requestCount;
        private static int aborted;

        public static bool IsRunning => running;
        public static long Requests => requestCount;
        public static int Aborted => aborted;

        public static void Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                try
                {
                    listener.Start();
                }
                catch
                {
                    try { listener.Stop(); } catch { }
                    listener = null;
                    throw;
                }
                running = true;
                Thread t = new Thread(AcceptLoop) { IsBackground = true, Name = "SephiriaMonitorBridge" };
                t.Start();
                MonitorPlugin.LogInfo("bridge listening on http://127.0.0.1:" + Port + "/");
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("bridge start failed: " + ex.Message);
            }
        }

        public static void Stop()
        {
            running = false;
            try { cts.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            listener = null;
            cts.Dispose();
            cts = new CancellationTokenSource();
        }

        private static void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    return;
                }
                if (!Slots.Wait(500))
                {
                    RejectBusy(client);
                    continue;
                }
                TcpClient accepted = client;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(accepted); }
                    finally { Slots.Release(); }
                });
            }
        }

        private static void RejectBusy(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = SocketTimeoutMs;
                client.SendTimeout = SocketTimeoutMs;
                byte[] body = Encoding.UTF8.GetBytes("{\"error\":\"busy\",\"message\":\"too many concurrent requests\"}");
                string head = "HTTP/1.1 503 Service Unavailable\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " +
                              body.Length + "\r\nConnection: close\r\n\r\n";
                using (client)
                using (NetworkStream s = client.GetStream())
                {
                    byte[] h = Encoding.ASCII.GetBytes(head);
                    s.Write(h, 0, h.Length);
                    s.Write(body, 0, body.Length);
                    s.Flush();
                }
            }
            catch
            {
                try { client.Close(); } catch { }
            }
        }

        private static void Handle(object state)
        {
            TcpClient client = (TcpClient)state;
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = SocketTimeoutMs;
                    client.SendTimeout = SocketTimeoutMs;
                    using (NetworkStream stream = client.GetStream())
                    {
                        string requestLine;
                        byte[] leftover;
                        Dictionary<string, string> headers = ReadHead(stream, out requestLine, out leftover);
                        if (requestLine == null)
                        {
                            return;
                        }
                        string[] parts = requestLine.Split(' ');
                        if (parts.Length < 2)
                        {
                            return;
                        }
                        // Every route is a GET; request bodies are not read (Connection: close),
                        // so a client that sends one can never stall this thread.
                        int contentLength = 0;
                        string cl;
                        if (headers.TryGetValue("content-length", out cl))
                        {
                            int.TryParse(cl, out contentLength);
                        }
                        if (contentLength > MaxBodyBytes)
                        {
                            return;
                        }
                        Interlocked.Increment(ref requestCount);
                        string path = parts[1];
                        string method = parts[0] == null ? "GET" : parts[0].ToUpperInvariant();
                        string routePath = path;

                        byte[] body = null;
                        if (contentLength > 0)
                        {
                            body = ReadBody(stream, leftover, contentLength);
                        }

                        if (method == "POST" && routePath.StartsWith("/mcp", StringComparison.Ordinal))
                        {
                            string rpc = body == null ? "" : Encoding.UTF8.GetString(body);
                            string answer;
                            try
                            {
                                answer = MainThreadPump.Run(() => McpServer.HandleBody(rpc), 60000);
                            }
                            catch (Exception mcpError)
                            {
                                LogMcpError(mcpError);
                                answer = "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"handler error (see mcp-error.log)\"}}";
                            }
                            if (answer == null) answer = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"no answer\"}}";
                            if (answer.Length == 0)
                            {
                                byte[] only = Encoding.ASCII.GetBytes("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                                stream.Write(only, 0, only.Length);
                                stream.Flush();
                                return;
                            }
                            WriteJson(stream, answer);
                            return;
                        }

                        string payload = MainThreadPump.Run(() => Router.Route(method, routePath), 8000);
                        WriteJson(stream, payload);
                    }
                }
                catch (System.IO.IOException)
                {
                    // The client hung up before the response was written (timeouts, cancelled
                    // requests): normal churn, not a mod error.
                    Interlocked.Increment(ref aborted);
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Increment(ref aborted);
                }
                catch (Exception ex)
                {
                    MonitorPlugin.LogError("bridge handler: " + ex.GetType().Name + ":" + ex.Message);
                }
            }
        }

        private static void LogMcpError(Exception ex)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(typeof(BridgeServer).Assembly.Location);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "mcp-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + "\n\n");
            }
            catch
            {
            }
        }

        private static void WriteJson(NetworkStream stream, string payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload ?? "");
            string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " +
                          bytes.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static byte[] ReadBody(NetworkStream stream, byte[] leftover, int contentLength)
        {
            byte[] body = new byte[contentLength];
            int have = 0;
            if (leftover != null && leftover.Length > 0)
            {
                have = Math.Min(leftover.Length, contentLength);
                Array.Copy(leftover, 0, body, 0, have);
            }
            while (have < contentLength)
            {
                int read = stream.Read(body, have, contentLength - have);
                if (read <= 0) break;
                have += read;
            }
            if (have < contentLength) Array.Resize(ref body, have);
            return body;
        }

        private static Dictionary<string, string> ReadHead(NetworkStream stream, out string requestLine, out byte[] leftover)
        {
            requestLine = null;
            leftover = null;
            MemoryStream buffer = new MemoryStream();
            byte[] chunk = new byte[2048];
            // No body is ever read here; leftover bytes after the header are discarded on purpose.
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    return new Dictionary<string, string>();
                }
                buffer.Write(chunk, 0, read);
                string text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (buffer.Length > MaxHeaderBytes)
                {
                    return new Dictionary<string, string>();
                }
            }
            string head = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
            int bodyStart = headerEnd + 4;
            int remaining = (int)buffer.Length - bodyStart;
            if (remaining > 0)
            {
                leftover = new byte[remaining];
                Array.Copy(buffer.GetBuffer(), bodyStart, leftover, 0, remaining);
            }
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            requestLine = lines.Length > 0 ? lines[0] : null;
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }
            return headers;
        }
    }

    /// <summary>Route table for the bridge. Everything here is read-only.</summary>
    public static class Router
    {
        public static string Route(string method, string rawPath)
        {
            try
            {
                return RouteInner(method, rawPath);
            }
            catch (Exception ex)
            {
                return Json.Write(Json.Obj().Add("error", ex.GetType().Name).Add("message", ex.Message));
            }
        }

        private static string RouteInner(string method, string rawPath)
        {
            string path = rawPath;
            Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = rawPath.IndexOf('?');
            if (q >= 0)
            {
                path = rawPath.Substring(0, q);
                string[] pairs = rawPath.Substring(q + 1).Split('&');
                for (int i = 0; i < pairs.Length; i++)
                {
                    int eq = pairs[i].IndexOf('=');
                    if (eq > 0)
                    {
                        query[Uri.UnescapeDataString(pairs[i].Substring(0, eq))] = Uri.UnescapeDataString(pairs[i].Substring(eq + 1).Replace("+", " "));
                    }
                }
            }
            path = path.TrimEnd('/');
            if (path.Length == 0)
            {
                path = "/";
            }
            switch (path)
            {
                    case "/":
                    case "/health":
                        return Health();
                    case "/run":
                        return Run();
                    case "/player":
                        return Json.Write(PlayerState.Summary());
                    case "/world":
                        return Json.Write(StateApi.WorldSummary());
                    case "/state":
                        return StateJson();
                    case "/scene":
                        return Json.Write(Json.Obj().Add("scene", SceneTracker.Current).Add("prev", SceneTracker.Previous).Add("changes", SceneTracker.ChangeCount));
                    case "/objects":
                        return Objects(query);
                    case "/object":
                        return ObjectDetail(query);
                    case "/logs":
                        return LogRing.Tail(int.Parse(Query(query, "n", "60")));
                    case "/events":
                        return Events.Tail(int.Parse(Query(query, "n", "100")), long.Parse(Query(query, "after", "0")), Query(query, "kind", ""));
                    case "/damage":
                        return Events.DamageTail(int.Parse(Query(query, "n", "50")));
                    case "/combat":
                        return Combat.Stats(int.Parse(Query(query, "top", "12")));
                    case "/runs":
                        return Json.Write(RunTracker.Current());
                    case "/path":
                        return Movement.PathJson(int.Parse(Query(query, "n", "600")));
                    case "/floor":
                        return FloorDump.Render();
                    case "/map":
                        return FloorDump.AsciiView(int.Parse(Query(query, "r", "18")), ParseFloat(Query(query, "tx", "")), ParseFloat(Query(query, "ty", "")));
                    case "/rooms":
                        return FloorDump.RoomsJson();
                    case "/mappath":
                        {
                            float mx = float.Parse(Query(query, "x", "0"), System.Globalization.CultureInfo.InvariantCulture);
                            float my = float.Parse(Query(query, "y", "0"), System.Globalization.CultureInfo.InvariantCulture);
                            return MainThreadPump.Run(delegate { return FloorDump.PathJson(mx, my); }, 6000);
                        }
                    case "/mapscan":
                        {
                            int radius = int.Parse(Query(query, "r", "60"));
                            MainThreadPump.Run(delegate
                            {
                                FloorDump.Rescan();
                                return "ok";
                            }, 3000);
                            return Json.Write(FloorDump.Summary());
                        }
                    case "/probe":
                        {
                            float px = float.Parse(Query(query, "x", "0"), System.Globalization.CultureInfo.InvariantCulture);
                            float py = float.Parse(Query(query, "y", "0"), System.Globalization.CultureInfo.InvariantCulture);
                            return MainThreadPump.Run(delegate { return FloorDump.Probe(px, py); }, 6000);
                        }
                    case "/inventory":
                        return Live.Inventory();
                    case "/inventory_snapshot":
                        return Filtered(new string[] { ".inv.", ".tablet.", "stat.", "tablets" });
                    case "/combos":
                        return Live.Combos();
                    case "/statuses":
                        return Live.Statuses();
                    case "/stage":
                        return MerchantOps.Stage(Query(query, "name", ""), Query(query, "to", ""));

                    case "/merchants":
                        return MerchantOps.Merchants();
                    case "/floors":
                        return MerchantOps.Floors();
                    case "/sample":
                        return MainThreadPump.Run(delegate
                        {
                            return MerchantOps.Sample(Int(query, "n", 100), Int(query, "charms", 4), Int(query, "tablets", 2),
                                Num(query, "ratio", 1.0f), Int(query, "seed", 0));
                        }, 20000);
                    case "/goto":
                        return MerchantOps.Goto(Query(query, "floor", Query(query, "name", "")), Query(query, "to", ""), Query(query, "hidden", "0") == "1");
                    case "/tp":
                        return MerchantOps.Teleport(Query(query, "index", ""), Query(query, "name", ""), Query(query, "to", ""), Num(query, "ox", 1.6f), Num(query, "oy", 0f),
                            Has(query, "x") ? Num(query, "x", 0f) : (float?)null, Has(query, "y") ? Num(query, "y", 0f) : (float?)null);
                    case "/shop":
                        return MerchantOps.OpenShop(Query(query, "index", ""), Query(query, "name", ""), Query(query, "to", ""));

                    case "/spots":
                        return MainThreadPump.Run(delegate
                        {
                            return WorldOps.Spots(Query(query, "type", ""), Query(query, "name", ""), Int(query, "max", 30),
                                Query(query, "all", "0") == "1", Num(query, "near", 0f));
                        }, 8000);
                    case "/use":
                        return MainThreadPump.Run(delegate
                        {
                            return WorldOps.Use(Query(query, "index", ""), Query(query, "name", ""), Query(query, "type", ""),
                                Query(query, "tp", "1") != "0", Num(query, "ox", 1.2f), Num(query, "oy", 0f));
                        }, 8000);
                    case "/gridrooms":
                        return MainThreadPump.Run(delegate { return WorldOps.Rooms(); }, 8000);
                    case "/jump":
                        return MainThreadPump.Run(delegate
                        {
                            return WorldOps.Jump(Query(query, "room", ""), Query(query, "index", ""), Num(query, "ox", 0f), Num(query, "oy", 0f));
                        }, 8000);
                    case "/progress":
                        return Filtered(new string[] { "run.floor", "run.hard", "run.seed", "run.env" });
                    case "/screenshot":
                        return Screenshot();
                    case "/snapshot":
                        return Snapshot.Capture(Query(query, "reason", "bridge"));
                    case "/session":
                        return Json.Write(Json.Obj().Add("root", Session.Root).Add("events", Session.EventCount).Add("requests", BridgeServer.Requests).Add("floorDumps", FloorDump.Count));
                    case "/title":
                        return Json.Write(TitleControl.TitleInfo());
                    case "/saves":
                        return Json.Write(TitleControl.Slots());
                    case "/enter":
                        return Json.Write(TitleControl.Enter(Query(query, "slot", ""), Query(query, "create", "0") == "1"));
                    case "/enterstatus":
                        return Json.Write(Json.Obj().Add("scene", SceneTracker.Current).Add("pending", TitleControl.Pending()));
                    case "/statefull":
                        return StateFull();
                default:
                    return Json.Write(Json.Obj().Add("error", "unknown path").Add("path", path));
            }
        }

        private static int unitCountCached;
        private static int hostileCached;
        private static string playerFactionCached = "";
        private static float nextUnitScan;

        /// <summary>
        /// Cheap per-tick run state for pollers: /player walks every unit in the scene to build its
        /// "nearby" list, which cost 5x the requested interval under a 400 ms poll. This one only
        /// touches the player and refreshes the unit counts at most once a second.
        /// </summary>
        private static string Run()
        {
            float t = Time.realtimeSinceStartup;
            if (t >= nextUnitScan)
            {
                nextUnitScan = t + 1f;
                List<UnityEngine.Object> units = StateApi.Instances(typeof(UnitAvatar));
                unitCountCached = units.Count;
                PlayerAvatar me = null;
                for (int i = 0; i < units.Count; i++)
                {
                    PlayerAvatar pa = units[i] as PlayerAvatar;
                    if (pa != null)
                    {
                        me = pa;
                        break;
                    }
                }
                string faction = StateApi.FactionOf(me);
                int hostile = 0;
                for (int i = 0; i < units.Count; i++)
                {
                    UnitAvatar u = units[i] as UnitAvatar;
                    if (u != null && u.hp > 0f && StateApi.IsHostileToPlayer(u, faction))
                    {
                        hostile++;
                    }
                }
                hostileCached = hostile;
                playerFactionCached = faction;
            }

            Dictionary<string, object> d = Json.Obj();
            d["t"] = t;
            d["wall"] = DateTime.Now.ToString("HH:mm:ss");
            d["scene"] = SceneTracker.Current;
            d["floor"] = Events.LastFloor;
            List<UnityEngine.Object> players = StateApi.Instances(typeof(PlayerAvatar));
            if (players.Count > 0)
            {
                PlayerAvatar p = players[0] as PlayerAvatar;
                if (p != null)
                {
                    d["pos"] = StateApi.Vec(p.transform.position);
                    d["hp"] = Math.Round(p.hp, 1);
                    d["maxHp"] = Math.Round(p.MaxHp, 1);
                    d["mp"] = p.mp;
                    d["maxMp"] = p.MaxMp;
                    d["money"] = p.Money;
                    d["name"] = p.name;
                }
            }
            d["unitCount"] = unitCountCached;
            d["hostile"] = hostileCached;
            d["faction"] = playerFactionCached;
            d["move"] = Movement.Summary();
            d["cursor"] = Events.Cursor;
            d["events"] = Events.Count;
            d["damage"] = Events.DamageCount;
            d["probes"] = Events.ProbeCount;
            d["deduped"] = MonitorPatches.Deduped;
            d["lateDuplicates"] = MonitorPatches.LateDuplicates;
            d["floorDump"] = FloorDump.Summary();
            return Json.Write(d);
        }

        private static string Filtered(string[] needles)
        {
            Dictionary<string, string> map = Snapshot.Current;
            if (map == null)
            {
                return Json.Write(Json.Obj().Add("error", "no snapshot yet").Add("hint", "GET /snapshot"));
            }
            JsonObject o = Json.Obj();
            int n = 0;
            foreach (KeyValuePair<string, string> kv in map)
            {
                bool keep = false;
                for (int i = 0; i < needles.Length; i++)
                {
                    if (kv.Key.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        keep = true;
                        break;
                    }
                }
                if (keep)
                {
                    o[kv.Key] = kv.Value;
                    n++;
                }
            }
            o["keys"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Json.Write(o);
        }

        private static string Query(Dictionary<string, string> q, string key, string fallback)
        {
            string v;
            return q.TryGetValue(key, out v) ? v : fallback;
        }

        private static bool Has(Dictionary<string, string> q, string key)
        {
            return q.ContainsKey(key);
        }

        private static int Int(Dictionary<string, string> q, string key, int fallback)
        {
            int v;
            return int.TryParse(Query(q, key, null), out v) ? v : fallback;
        }

        private static float Num(Dictionary<string, string> q, string key, float fallback)
        {
            string raw = Query(q, key, null);
            float v;
            if (raw != null && float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                return v;
            }
            return fallback;
        }

        private static string StateJson()
        {
            Dictionary<string, object> d = PlayerState.Summary();
            d["logErrors"] = LogRing.Errors;
            d["logWarnings"] = LogRing.Warnings;
            d["logNoise"] = LogRing.Noise;
            d["harmony"] = MonitorPatches.Patched;
            d["db"] = DataExport.Status;
            d["dbRows"] = DataExport.Rows;
            d["pending"] = MainThreadPump.Pending;
            d["modErrors"] = MonitorPlugin.ErrorCount;
            d["lastModError"] = MonitorPlugin.LastError;
            d["sceneChanges"] = SceneTracker.ChangeCount;
            if (SceneTracker.Current == TitleControl.TitleSceneName)
            {
                d["title"] = TitleControl.TitleInfo();
            }
            return Json.Write(d);
        }

        private static string Health()
        {
            Dictionary<string, object> d = Json.Obj();
            d["ok"] = true;
            d["mod"] = "SephiriaMonitor";
            d["version"] = MonitorPlugin.Version;
            d["unity"] = Application.unityVersion;
            d["scene"] = SceneTracker.Current;
            d["t"] = Time.realtimeSinceStartup;
            d["session"] = Session.Root;
            d["events"] = Session.EventCount;
            d["logLines"] = LogRing.Count;
            d["cursor"] = Events.Cursor;
            d["probes"] = Events.ProbeCount;
            d["deduped"] = MonitorPatches.Deduped;
            d["lateDuplicates"] = MonitorPatches.LateDuplicates;
            d["probeEvents"] = MonitorConfig.EmitProbes;
            d["pathPoints"] = Movement.PathCount;

            d["requests"] = BridgeServer.Requests;
            return Json.Write(d);
        }

        /// <summary>List live objects of a game type, e.g. /objects?type=UnitAvatar</summary>

        private static string StateFull()
        {
            string path = Session.Root == null ? null : System.IO.Path.Combine(Session.Root, "state_full.json");
            if (path == null || !System.IO.File.Exists(path))
            {
                return Json.Write(Json.Obj().Add("error", "no snapshot yet").Add("hint", "GET /snapshot"));
            }
            return System.IO.File.ReadAllText(path);
        }

        private static string Objects(Dictionary<string, string> q)
        {
            string typeName = Query(q, "type", "UnitAvatar");
            int limit = int.Parse(Query(q, "limit", "80"));
            Type t = StateApi.FindType(typeName);
            if (t == null)
            {
                return Json.Write(Json.Obj().Add("error", "type not found").Add("type", typeName));
            }
            List<UnityEngine.Object> all = StateApi.Instances(t);
            List<object> items = new List<object>();
            for (int i = 0; i < all.Count && items.Count < limit; i++)
            {
                items.Add(StateApi.Describe(all[i], 0));
            }
            return Json.Write(Json.Obj().Add("type", t.Name).Add("count", all.Count).Add("items", items));
        }

        private static string ObjectDetail(Dictionary<string, string> q)
        {
            string typeName = Query(q, "type", "PlayerAvatar");
            int index = int.Parse(Query(q, "index", "0"));
            int depth = int.Parse(Query(q, "depth", "1"));
            Type t = StateApi.FindType(typeName);
            if (t == null)
            {
                return Json.Write(Json.Obj().Add("error", "type not found").Add("type", typeName));
            }
            List<UnityEngine.Object> all = StateApi.Instances(t);
            if (index < 0 || index >= all.Count)
            {
                return Json.Write(Json.Obj().Add("error", "index out of range").Add("count", all.Count));
            }
            return Json.Write(StateApi.Describe(all[index], depth));
        }

        private static string Screenshot()
        {
            string path = ScreenshotService.Capture(4000);
            return Json.Write(Json.Obj().Add("ok", path != null).Add("path", path).Add("scene", SceneTracker.Current));
        }
private static float? ParseFloat(string text)
        {
            float value;
            if (!string.IsNullOrEmpty(text) && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return null;
        }

        private static string FilteredMarkerHook()
        {
            return "";
        }
    }
}
