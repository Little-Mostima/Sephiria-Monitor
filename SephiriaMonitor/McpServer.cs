using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SephiriaMonitor
{
    /// <summary>
    /// Minimal MCP (Model Context Protocol) server implemented inside the plugin, so the plugin
    /// alone is the product: an MCP client can POST JSON-RPC to http://127.0.0.1:PORT/mcp
    /// and no Node process is required.
    ///
    /// Transport: "Streamable HTTP" with plain application/json responses (stateless, one
    /// request per POST). Notifications (no "id") are accepted and answered with an empty body.
    /// </summary>
    public static class McpServer
    {
        public const string ProtocolVersion = "2025-06-18";
        public const string ServerName = "sephiria-monitor";

        private const int MaxTextChars = 180000;

        private static string version;
        private static string Version
        {
            get
            {
                if (version == null)
                {
                    try { version = typeof(McpServer).Assembly.GetName().Version.ToString(); }
                    catch { version = "0.0.0"; }
                }
                return version;
            }
        }

        /// <summary>True when the request carries a JSON-RPC message (POST /mcp).</summary>
        public static bool IsRpc(string body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                return c == '{';
            }
            return false;
        }

        /// <summary>Handle one JSON-RPC message; returns the response body (may be empty).</summary>
        public static string HandleBody(string body)
        {
            JObject req;
            try
            {
                req = JObject.Parse(body);
            }
            catch (Exception ex)
            {
                return Error(null, -32700, "parse error: " + ex.Message);
            }

            JToken id = req["id"];
            string method = (string)req["method"];
            if (string.IsNullOrEmpty(method))
            {
                return id == null ? "" : Error(id, -32600, "missing method");
            }

            // Notifications (no id) never get a response body.
            if (id == null) return "";

            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, Initialize());
                    case "ping":
                        return Result(id, new JObject());
                    case "tools/list":
                        return Result(id, new JObject { ["tools"] = Tools() });
                    case "tools/call":
                        return Result(id, CallTool(req["params"] as JObject));
                    case "resources/list":
                        return Result(id, new JObject { ["resources"] = new JArray() });
                    case "prompts/list":
                        return Result(id, new JObject { ["prompts"] = new JArray() });
                    default:
                        return Error(id, -32601, "method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                return Error(id, -32603, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static JObject Initialize()
        {
            return new JObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false }
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = ServerName,
                    ["version"] = Version
                },
                ["instructions"] =
                    "Read-only Sephiria monitor. Call sephiria_brief first; then sephiria_world(type=...), "
                    + "sephiria_events(after=...), sephiria_inventory, sephiria_db(table=...), sephiria_hooks. "
                    + "The plugin also serves the same data as plain GET routes on http://127.0.0.1:47901/."
            };
        }

        private static string Result(JToken id, JObject result)
        {
            var o = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id == null ? JValue.CreateNull() : id,
                ["result"] = result
            };
            return o.ToString(Formatting.None);
        }

        private static string Error(JToken id, int code, string message)
        {
            var o = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id == null ? JValue.CreateNull() : id,
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            };
            return o.ToString(Formatting.None);
        }

        // ---------------------------------------------------------------- tools

        private static JObject CallTool(JObject p)
        {
            string name = p == null ? null : (string)p["name"];
            JObject args = (p == null ? null : p["arguments"] as JObject) ?? new JObject();
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("missing tool name");

            string text;
            switch (name)
            {
                case "sephiria_health": text = Raw("/health"); break;
                case "sephiria_brief": text = Brief(args); break;
                case "sephiria_status": text = Status(args); break;
                case "sephiria_merchant": text = Merchant(args); break;
                case "sephiria_player": text = PlayerTool(args); break;                case "sephiria_mods": text = Mods(args); break;
                case "sephiria_ui": text = Ui(args); break;

                    case "sephiria_travel": text = Travel(args); break;
                case "sephiria_inventory": text = Raw("/inventory"); break;
                case "sephiria_world": text = World(args); break;
                case "sephiria_events": text = Events(args); break;
                case "sephiria_logs": text = Logs(args); break;
                case "sephiria_db": text = Db(args); break;
                case "sephiria_hooks": text = Hooks(args); break;
                case "sephiria_save": text = Save(args); break;
                case "sephiria_screenshot": text = Raw("/screenshot"); break;
                default: throw new ArgumentException("unknown tool: " + name);
            }

            if (text != null && text.Length > MaxTextChars) text = text.Substring(0, MaxTextChars) + "\n...[truncated]";
            return new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text ?? "" } },
                ["isError"] = false
            };
        }

        private static string Mods(JObject args)
        {
            string action = (Str(args, "action") ?? "list").Trim().ToLowerInvariant();
            switch (action)
            {
                case "patches":
                    {
                        var sb = new StringBuilder("/patches?limit=").Append(ClampInt(args, "limit", 20, 1, 200));
                        string q = Str(args, "q");
                        if (!string.IsNullOrEmpty(q)) sb.Append("&q=").Append(Uri.EscapeDataString(q));
                        string owner = Str(args, "owner");
                        if (!string.IsNullOrEmpty(owner)) sb.Append("&owner=").Append(Uri.EscapeDataString(owner));
                        return Raw(sb.ToString());
                    }
                case "data":
                    {
                        var sb = new StringBuilder("/moddata?limit=").Append(ClampInt(args, "limit", 40, 1, 200));
                        string kind = Str(args, "kind");
                        if (!string.IsNullOrEmpty(kind)) sb.Append("&kind=").Append(Uri.EscapeDataString(kind));
                        string q = Str(args, "q");
                        if (!string.IsNullOrEmpty(q)) sb.Append("&q=").Append(Uri.EscapeDataString(q));
                        if (args["min"] != null) sb.Append("&min=").Append(ClampInt(args, "min", 900000, 0, int.MaxValue));
                        return Raw(sb.ToString());
                    }
                case "options":
                    return Raw("/modoptions");
                case "l10n":
                    {
                        var sb = new StringBuilder("/l10n?limit=").Append(ClampInt(args, "limit", 40, 1, 200));
                        string key = Str(args, "key");
                        if (!string.IsNullOrEmpty(key)) sb.Append("&key=").Append(Uri.EscapeDataString(key));
                        string prefix = Str(args, "prefix");
                        if (!string.IsNullOrEmpty(prefix)) sb.Append("&prefix=").Append(Uri.EscapeDataString(prefix));
                        string lang = Str(args, "lang");
                        if (!string.IsNullOrEmpty(lang)) sb.Append("&lang=").Append(Uri.EscapeDataString(lang));
                        return Raw(sb.ToString());
                    }
                default:
                    return Raw("/mods");
            }
        }

        private static string Ui(JObject args)
        {
            string action = (Str(args, "action") ?? "list").Trim().ToLowerInvariant();
            var sb = new StringBuilder("/ui?action=").Append(Uri.EscapeDataString(action));
            string name = Str(args, "name");
            if (!string.IsNullOrEmpty(name)) sb.Append("&name=").Append(Uri.EscapeDataString(name));
            string type = Str(args, "type");
            if (!string.IsNullOrEmpty(type)) sb.Append("&type=").Append(Uri.EscapeDataString(type));
            return Raw(sb.ToString());
        }

        private static string Raw(string route)
        {
            return Router.Route("GET", route);
        }

        private static string Brief(JObject args)
        {
            int top = ClampInt(args, "top", 12, 1, 40);
            var o = new JObject();
            o["health"] = Parse(Raw("/health"));
            o["runs"] = Parse(Raw("/runs"));
            o["player"] = Parse(Raw("/player"));
            o["progress"] = Parse(Raw("/progress"));
            o["combat"] = Parse(Raw("/combat?top=" + top));
            o["combos"] = Parse(Raw("/combos"));
            o["statuses"] = Parse(Raw("/statuses"));
            o["keys"] = "health,runs,player,progress,combat,combos,statuses";
            return o.ToString(Formatting.None);
        }

        private static string Status(JObject args)
        {
            JObject state = (JObject)Parse(Raw("/state"));
            if (Bool(args, "detail"))
            {
                state["progress"] = Parse(Raw("/progress"));
            }
            return state.ToString(Formatting.None);
        }

        private static string World(JObject args)
        {
            string type = Str(args, "type");
            if (!string.IsNullOrEmpty(type))
            {
                if (args["index"] != null)
                {
                    int depth = ClampInt(args, "depth", 1, 1, 3);
                    return Raw("/object?type=" + Uri.EscapeDataString(type) + "&index=" + ClampInt(args, "index", 0, 0, 500) + "&depth=" + depth);
                }
                return Raw("/objects?type=" + Uri.EscapeDataString(type) + "&limit=" + ClampInt(args, "limit", 20, 1, 100));
            }
            if (Bool(args, "map") || Bool(args, "rooms") || Bool(args, "probe") || args["x"] != null)
            {
                var sb = new StringBuilder();
                sb.Append("/map?r=").Append(ClampInt(args, "r", 18, 2, 40));
                if (args["x"] != null) sb.Append("&x=").Append(ClampInt(args, "x", 0, -100000, 100000));
                if (args["y"] != null) sb.Append("&y=").Append(ClampInt(args, "y", 0, -100000, 100000));
                if (Bool(args, "rooms")) sb.Append("&rooms=true");
                if (Bool(args, "probe") && args["x"] != null && args["y"] != null) sb.Append("&probe=true");
                if (Bool(args, "path") && args["x"] != null && args["y"] != null) sb.Append("&path=true");
                return Raw(sb.ToString());
            }
            return Raw("/world");
        }

        private static string Events(JObject args)
        {
            var sb = new StringBuilder("/events?n=").Append(ClampInt(args, "n", 80, 1, 500));
            if (args["after"] != null) sb.Append("&after=").Append(ClampInt(args, "after", 0, 0, int.MaxValue));
            string raw = Raw(sb.ToString());
            string kinds = Str(args, "kinds") ?? Str(args, "kind");
            if (string.IsNullOrEmpty(kinds)) return raw;

            JToken parsed = Parse(raw);
            JArray list = parsed as JArray;
            if (list == null && parsed is JObject) list = parsed["events"] as JArray;
            if (list == null) return raw;

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in kinds.Split(','))
            {
                string t = k.Trim();
                if (t.Length > 0) wanted.Add(t);
            }
            var keep = new JArray();
            foreach (JToken e in list)
            {
                string kind = (string)(e as JObject)?["kind"];
                if (kind != null && wanted.Contains(kind)) keep.Add(e);
            }
            return keep.ToString(Formatting.None);
        }

        private static string Logs(JObject args)
        {
            var sb = new StringBuilder("/logs?n=").Append(ClampInt(args, "n", 60, 1, 500));
            string type = Str(args, "type");
            if (!string.IsNullOrEmpty(type)) sb.Append("&type=").Append(Uri.EscapeDataString(type));
            return Raw(sb.ToString());
        }

        private static string Save(JObject args)
        {
            string action = Str(args, "action") ?? "slots";
            switch (action.ToLowerInvariant())
            {
                case "slots":
                    return Raw("/saves");
                case "title":
                    return Raw("/title");
                case "enter":
                    {
                        string res = Raw("/enter");
                        var deadline = DateTime.UtcNow.AddSeconds(75);
                        while (DateTime.UtcNow < deadline)
                        {
                            string st = Raw("/enterstatus");
                            JObject j = Parse(st) as JObject;
                            if (j != null && ToBool(j["ready"])) return st;
                            if (j != null && j["error"] != null) return st;
                            System.Threading.Thread.Sleep(500);
                        }
                        return res;
                    }
                default:
                    throw new ArgumentException("action must be slots|title|enter");
            }
        }

        private static string Merchant(JObject args)
        {
            string action = (Str(args, "action") ?? "merchants").ToLowerInvariant();
            string route;
            switch (action)
            {
                case "merchants":
                    return Raw("/merchants");
                case "floors":
                    return Raw("/floors");
                case "stage":
                    route = "/stage";
                    break;
                case "goto":
                    route = "/goto";
                    break;
                case "sample":
                    route = "/sample";
                    break;
                case "tp":
                    route = "/tp";
                    break;
                case "shop":
                    route = "/shop";
                    break;
                default:
                    throw new ArgumentException("action must be merchants|floors|stage|goto|tp|shop|sample");
            }
            var query = new StringBuilder();
            string index = Str(args, "index");
            string name = Str(args, "name");
            string to = Str(args, "to");
            if (!string.IsNullOrEmpty(index)) query.Append("&index=").Append(Uri.EscapeDataString(index));
            if (!string.IsNullOrEmpty(name)) query.Append("&name=").Append(Uri.EscapeDataString(name));
            if (!string.IsNullOrEmpty(to)) query.Append("&to=").Append(Uri.EscapeDataString(to));
            if (args["ox"] != null) query.Append("&ox=").Append(args["ox"].ToString());
            if (args["oy"] != null) query.Append("&oy=").Append(args["oy"].ToString());
            foreach (string extra in new[] { "n", "charms", "tablets", "ratio", "seed", "x", "y", "hidden", "floor" })
            {
                if (args[extra] != null) query.Append("&").Append(extra).Append("=").Append(args[extra].ToString());
            }
            if (query.Length > 0) query[0] = '?';
            return Raw(route + query);
        }

        private static string PlayerTool(JObject args)
        {
            string action = (Str(args, "action") ?? "info").ToLowerInvariant();
            switch (action)
            {
                case "god":
                    string g = "/god?on=" + (Bool(args, "on") ? "1" : "0");
                    JToken inv;
                    if (args.TryGetValue("invuln", out inv) && inv != null && inv.Type != JTokenType.Null)
                    {
                        g += "&invuln=" + (Bool(args, "invuln") ? "1" : "0");
                    }
                    return Raw(g);
                case "boss":
                    return Raw("/boss");
                case "config":
                    return Raw("/config?npcDamage=" + (Bool(args, "npcDamage") ? "1" : "0"));
                case "heal":
                    return Raw("/heal?hp=" + ClampInt(args, "hp", 0, 0, 999999));
                case "money":
                    return Raw("/money?set=" + ClampInt(args, "set", 0, 0, 100000000));
                case "level":
                    return Raw("/level?set=" + ClampInt(args, "set", 1, 1, 999));
                case "stat":
                    return Raw("/stat?name=" + UrlEnc(Str(args, "name") ?? "") +
                               "&value=" + ClampInt(args, "value", 0, -1000000, 1000000));
                default:
                    return Raw("/pinfo");
            }
        }

        private static string UrlEnc(string s)
        {
            return Uri.EscapeDataString(s ?? "");
        }

        private static string Travel(JObject args)
        {
            string action = (Str(args, "action") ?? "spots").ToLowerInvariant();
            string route;
            switch (action)
            {
                case "spots":
                    route = "/spots";
                    break;
                case "use":
                    route = "/use";
                    break;
                case "rooms":
                    route = "/gridrooms";
                    break;
                case "jump":
                    route = "/jump";
                    break;
                default:
                    throw new ArgumentException("action must be spots|use|rooms|jump");
            }
            var query = new StringBuilder();
            foreach (string key in new[] { "type", "name", "index", "room", "max", "near", "ox", "oy", "floor" })
            {
                string v = Str(args, key);
                if (string.IsNullOrEmpty(v)) continue;
                query.Append("&").Append(key).Append("=").Append(Uri.EscapeDataString(v));
            }
            foreach (string key in new[] { "all", "tp" })
            {
                string v = Str(args, key);
                if (string.IsNullOrEmpty(v)) continue;
                bool on = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
                query.Append("&").Append(key).Append("=").Append(on ? "1" : "0");
            }
            if (query.Length > 0) query[0] = '?';
            return Raw(route + query);
        }

        // ---------------------------------------------------------------- local files

        private static string Db(JObject args)
        {
            string table = Str(args, "table");
            string dir = Session.Root == null ? null : Path.Combine(Session.Root, "db");
            if (dir == null || !Directory.Exists(dir))
            {
                return new JObject { ["error"] = "no session db yet", ["hint"] = "enter the world once; CSVs are exported at boot" }.ToString(Formatting.None);
            }
            if (string.IsNullOrEmpty(table))
            {
                var tables = new JArray();
                foreach (string f in Directory.GetFiles(dir, "*.csv"))
                {
                    var row = new JObject
                    {
                        ["table"] = Path.GetFileNameWithoutExtension(f),
                        ["bytes"] = new FileInfo(f).Length
                    };
                    tables.Add(row);
                }
                return new JObject { ["dir"] = dir, ["count"] = tables.Count, ["tables"] = tables }.ToString(Formatting.None);
            }

            string path = Path.Combine(dir, table.Replace("..", "") + ".csv");
            if (!File.Exists(path)) throw new ArgumentException("no such table: " + table);

            string q = Str(args, "q") ?? "";
            int limit = ClampInt(args, "limit", 50, 1, 500);
            int offset = ClampInt(args, "offset", 0, 0, 1000000);

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) return new JObject { ["table"] = table, ["count"] = 0, ["rows"] = new JArray() }.ToString(Formatting.None);

            string[] header = SplitCsv(lines[0]);
            var rows = new JArray();
            int matched = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                if (q.Length > 0 && line.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matched++;
                if (matched <= offset) continue;
                if (rows.Count >= limit) continue;
                string[] cells = SplitCsv(line);
                var row = new JObject();
                for (int c = 0; c < header.Length; c++)
                {
                    row[header[c]] = c < cells.Length ? cells[c] : "";
                }
                rows.Add(row);
            }
            return new JObject
            {
                ["table"] = table,
                ["total"] = lines.Length - 1,
                ["matched"] = matched,
                ["offset"] = offset,
                ["count"] = rows.Count,
                ["rows"] = rows
            }.ToString(Formatting.None);
        }

        private static string Hooks(JObject args)
        {
            string path = HookFile();
            if (path == null) return new JObject { ["error"] = "hooks.json not found next to the plugin" }.ToString(Formatting.None);

            JObject root = Parse(File.ReadAllText(path, Encoding.UTF8)) as JObject;
            JArray all = root == null ? null : root["hooks"] as JArray;
            if (all == null) return "{\"error\":\"hooks.json has no hooks array\"}";

            string q = (Str(args, "q") ?? "").ToLowerInvariant();
            string area = Str(args, "area");
            string kind = Str(args, "kind");
            string side = Str(args, "side");
            int limit = ClampInt(args, "limit", 20, 1, 62);

            var keep = new JArray();
            int matched = 0;
            foreach (JToken t in all)
            {
                JObject h = t as JObject;
                if (h == null) continue;
                if (!string.IsNullOrEmpty(area) && !string.Equals((string)h["area"], area, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(kind) && !string.Equals((string)h["kind"], kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(side) && !string.Equals((string)h["side"], side, StringComparison.OrdinalIgnoreCase)) continue;
                if (q.Length > 0)
                {
                    string hay = ((string)h["id"] + " " + (string)h["name"] + " " + (string)h["signature"] + " "
                                  + (string)h["source"] + " " + (string)h["use"] + " " + (string)h["pitfall"]).ToLowerInvariant();
                    if (hay.IndexOf(q, StringComparison.Ordinal) < 0) continue;
                }
                matched++;
                if (keep.Count >= limit) continue;
                keep.Add(new JObject
                {
                    ["id"] = h["id"],
                    ["area"] = h["area"],
                    ["name"] = h["name"],
                    ["signature"] = h["signature"],
                    ["source"] = h["source"],
                    ["side"] = h["side"],
                    ["kind"] = h["kind"],
                    ["use"] = h["use"],
                    ["pitfall"] = h["pitfall"],
                    ["callerCount"] = h["callerCount"],
                    ["callers"] = h["callers"]
                });
            }
            return new JObject { ["total"] = all.Count, ["matched"] = matched, ["shown"] = keep.Count, ["hooks"] = keep }.ToString(Formatting.None);
        }

        private static string HookFile()
        {
            var candidates = new List<string>();
            try
            {
                string asm = typeof(McpServer).Assembly.Location;
                if (!string.IsNullOrEmpty(asm)) candidates.Add(Path.Combine(Path.GetDirectoryName(asm) ?? ".", "hooks.json"));
            }
            catch { }
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "hooks.json"));
            candidates.Add(@"D:\AscensionMod\_re\MODDEV\hooks.json");
            foreach (string c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        // ---------------------------------------------------------------- helpers

        private static JToken Parse(string json)
        {
            try { return JToken.Parse(json); }
            catch { return new JObject { ["raw"] = json }; }
        }

        private static string[] SplitCsv(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else quoted = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { cells.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            cells.Add(sb.ToString());
            return cells.ToArray();
        }

        private static string Str(JObject o, string key)
        {
            JToken t = o == null ? null : o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString();
        }

        private static bool Bool(JObject o, string key)
        {
            JToken t = o == null ? null : o[key];
            return t != null && ToBool(t);
        }

        private static bool ToBool(JToken t)
        {
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Integer: return (long)t != 0;
                case JTokenType.String: return string.Equals((string)t, "true", StringComparison.OrdinalIgnoreCase);
                default: return false;
            }
        }

        private static int ClampInt(JObject o, string key, int fallback, int min, int max)
        {
            JToken t = o == null ? null : o[key];
            int v;
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Integer) v = (int)(long)t;
            else if (!int.TryParse(t.ToString(), out v)) return fallback;
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }

        // ---------------------------------------------------------------- schemas

        private static JArray Tools()
        {
            return new JArray
            {
                Tool("sephiria_health", "Bridge health: version, scene, session dir, counters.", Schema()),
                Tool("sephiria_brief",
                     "One-shot overview: health + run boundaries + player + progress + combat aggregate + combos + statuses.",
                     Schema(new[] { "top" }, new JObject { ["top"] = Prop("integer", "combat rows to keep (1-40, default 12)") })),
                Tool("sephiria_status", "Scene, hp/mp/money, unit counts, counters. detail=true adds run progress.",
                     Schema(new[] { "detail" }, new JObject { ["detail"] = Prop("boolean", "include /progress") })),
                Tool("sephiria_inventory", "Live inventory: 64 custom stats, grid cells, potion slots, stone tablets.", Schema()),
                Tool("sephiria_world", "Units and objects. No args: nearby units. type: list that class. type+index: reflected fields. map/x/y: collision map (dungeon only).",
                     Schema(new[] { "type", "index", "depth", "limit", "map", "r", "x", "y", "path", "probe", "rooms" },
                            new JObject
                            {
                                ["type"] = Prop("string", "exact class name, e.g. Unit_Soldier"),
                                ["index"] = Prop("integer", "instance index for /object"),
                                ["depth"] = Prop("integer", "reflection depth 1-3"),
                                ["limit"] = Prop("integer", "objects limit 1-100"),
                                ["map"] = Prop("boolean", "return the collision map"),
                                ["r"] = Prop("integer", "map radius in world units"),
                                ["x"] = Prop("number", "map target x"),
                                ["y"] = Prop("number", "map target y"),
                                ["path"] = Prop("boolean", "BFS path to x,y"),
                                ["probe"] = Prop("boolean", "list colliders at x,y"),
                                ["rooms"] = Prop("boolean", "include the room table")
                            })),
                Tool("sephiria_events", "Structured event stream (damage/death/loot/floor/save/...). after is an incremental cursor; kinds filters.",
                     Schema(new[] { "after", "n", "kinds" }, new JObject
                     {
                         ["after"] = Prop("integer", "only events with seq > after"),
                         ["n"] = Prop("integer", "how many, 1-500"),
                         ["kinds"] = Prop("string", "comma separated kinds, e.g. damage,death")
                     })),
                Tool("sephiria_logs", "In-process Unity log lines (discord-ipc noise filtered).",
                     Schema(new[] { "n", "type" }, new JObject
                     {
                         ["n"] = Prop("integer", "line count 1-500"),
                         ["type"] = Prop("string", "Error|Warning|Log|Exception|Assert")
                     })),
                Tool("sephiria_db", "Exported game databases (49 CSV tables). No table: list them. table+q: filter rows.",
                     Schema(new[] { "table", "q", "limit", "offset" }, new JObject
                     {
                         ["table"] = Prop("string", "table name, e.g. item"),
                         ["q"] = Prop("string", "substring filter"),
                         ["limit"] = Prop("integer", "rows 1-500"),
                         ["offset"] = Prop("integer", "row offset")
                     })),
                Tool("sephiria_hooks", "Mod hook catalogue (62 entries): signature, file:line, side, use, pitfalls, real call sites.",
                     Schema(new[] { "q", "area", "kind", "side", "limit" }, new JObject
                     {
                         ["q"] = Prop("string", "substring filter"),
                         ["area"] = Prop("string", "lifecycle|data|combat|world|ui|save|net|antipattern"),
                         ["kind"] = Prop("string", "api|event|harmony|avoid"),
                         ["side"] = Prop("string", "server|client|both"),
                         ["limit"] = Prop("integer", "1-62")
                     })),
                Tool("sephiria_save", "Save slots, title-screen state, or enter a slot and wait for the world.",
                     Schema(new[] { "action", "slot" }, new JObject
                     {
                         ["action"] = Prop("string", "slots|title|enter (default slots)"),
                         ["slot"] = Prop("integer", "1-3 for action=enter")
                     })),
                Tool("sephiria_merchant",
                     "Merchants and floors. action=merchants lists loaded merchants + shelves; floors lists the run's floors with planned merchant type; goto moves to a floor (floor=guid/name or to=vendor); tp teleports next to a merchant; shop opens its shop panel. goto/tp/shop are write ops.",
                     Schema(new[] { "action", "index", "name", "to", "floor", "ox", "oy", "x", "y", "hidden", "n", "charms", "tablets", "ratio", "seed" }, new JObject
                     {
                         ["action"] = Prop("string", "merchants|floors|goto|tp|shop (default merchants)"),
                         ["floor"] = Prop("string", "floor guid or name substring for action=goto"),
                         ["index"] = Prop("integer", "merchant index from action=merchants"),
                         ["name"] = Prop("string", "name/socialID substring"),
                         ["to"] = Prop("string", "vendor|union|small|potion (merchant type)"),
                         ["ox"] = Prop("number", "teleport offset x from the merchant (default 1.6)"),
                         ["oy"] = Prop("number", "teleport offset y (default 0)"),
                         ["x"] = Prop("number", "absolute world x (action=tp); overrides merchant pick"),
                         ["y"] = Prop("number", "absolute world y (action=tp)"),
                         ["hidden"] = Prop("boolean", "action=goto: also allow floors with isHidden=true")
                     })),
                Tool("sephiria_travel",
                     "Reach any place. action=spots lists Interactable components (NPCs, portals, altars, beds, selectors) on the current floor with type/name/pos/distance; use teleports next to one and fires its interaction (same event the player's F key fires); rooms lists the current procedural floor's room instances with bounds/center/teleport point; jump teleports to a room by name or index. use/jump are write ops.",
                     Schema(new[] { "action", "type", "name", "index", "room", "max", "near", "tp", "all", "ox", "oy" }, new JObject
                     {
                         ["action"] = Prop("string", "spots|use|rooms|jump (default spots)"),
                         ["type"] = Prop("string", "Interactable subclass substring, e.g. NPC|Miniscript|Victory|Quest"),
                         ["name"] = Prop("string", "gameObject name substring"),
                         ["index"] = Prop("integer", "index from action=spots (or room index for action=jump)"),
                         ["room"] = Prop("string", "room name substring for action=jump"),
                         ["max"] = Prop("integer", "max rows for action=spots (default 30)"),
                         ["near"] = Prop("number", "only points within this distance of the player"),
                         ["tp"] = Prop("boolean", "action=use: teleport next to the target first (default true)"),
                         ["all"] = Prop("boolean", "action=spots: include points on other loaded floors"),
                         ["ox"] = Prop("number", "teleport offset x"),
                         ["oy"] = Prop("number", "teleport offset y")
                     })),
                Tool("sephiria_player",
                     "Player state control (server side, for testing): action=god(on=1|0, invuln=1 for true invulnerability; default god only heals, because invulnerable players are skipped by AI target search); boss lists boss/miniboss hp and what each boss AI is targeting (use it when a boss seems stuck); heal(hp); money(set); level(set); stat(name,value) sets a custom stat absolutely; no action = info.",
                     Schema(new[] { "action", "on", "invuln", "hp", "set", "name", "value" }, new JObject
                     {
                         ["action"] = Prop("string", "god|boss|info|heal|money|level|stat|config"),
                         ["on"] = Prop("boolean", "god on/off"),
                         ["invuln"] = Prop("boolean", "god mode: true invulnerability (AI cannot target you). omit for heal-only"),
                         ["npcDamage"] = Prop("boolean", "config: record NPC-vs-NPC damage events (false = much smaller events.jsonl)"),
                         ["hp"] = Prop("number", "heal target hp, 0 = full"),
                         ["set"] = Prop("integer", "absolute value for money/level"),
                         ["name"] = Prop("string", "custom stat name, e.g. EVASION"),
                         ["value"] = Prop("integer", "absolute stat value")
                     })),
                Tool("sephiria_mods",
                     "Mod testing probes. action=list: every loaded mod (BepInEx plugins, AddOns folder mods, Workshop packages) with state and dll path. patches: Harmony patches grouped by owner, filter q=method substring or owner=modId. data: rows registered by your mod (id >= min, default 900000) from the boot CSV export. options: pages registered in the game Mod Options screen with live values (ours + Workshop mods). l10n: resolve key= across all languages, or audit prefix= (e.g. ModItem_) and see which languages are missing.",
                     Schema(new[] { "action", "q", "owner", "kind", "min", "key", "prefix", "lang", "limit" }, new JObject
                     {
                         ["action"] = Prop("string", "list|patches|data|l10n|options (default list); options = the game's Mod Options pages and current values"),
                         ["q"] = Prop("string", "patches: method substring; data: name substring"),
                         ["owner"] = Prop("string", "patches: Harmony owner (modId) substring"),
                         ["kind"] = Prop("string", "data: csv table(s), comma separated, e.g. item,weapon,status"),
                         ["min"] = Prop("integer", "data: minimum id considered mod-added (default 900000)"),
                         ["key"] = Prop("string", "l10n: exact localization key to resolve"),
                         ["prefix"] = Prop("string", "l10n: key prefix to audit for missing translations"),
                         ["lang"] = Prop("string", "l10n: language for resolved value (default current)"),
                         ["limit"] = Prop("integer", "rows 1-200")
                     })),
                Tool("sephiria_ui",
                     "UI probe: action=list returns the open control stack (depth/name/type/esc); widgets lists registered UI element names; open/close a panel by name or type substring (write ops, useful to check a mod's panel).",
                     Schema(new[] { "action", "name", "type" }, new JObject
                     {
                         ["action"] = Prop("string", "list|widgets|open|close (default list)"),
                         ["name"] = Prop("string", "gameObject or type substring, e.g. Inventory or UI_Charm"),
                         ["type"] = Prop("string", "fallback type substring for open")
                     })),
                Tool("sephiria_screenshot", "Fallback screenshot; prefer the JSON tools.", Schema())
            };
        }

        private static JObject Tool(string name, string description, JObject schema)
        {
            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = schema
            };
        }

        private static JObject Schema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["additionalProperties"] = false
            };
        }

        private static JObject Schema(string[] names, JObject props)
        {
            if (names == null || names.Length == 0) return Schema();
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = props ?? new JObject(),
                ["additionalProperties"] = false
            };
            return schema;
        }

        private static JObject Prop(string type, string description)
        {
            return new JObject { ["type"] = type, ["description"] = description };
        }
    }
}
