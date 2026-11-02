using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Mod-testing probes: what is loaded, what it patched, what it registered,
    /// whether its localization resolves, and whether its UI opens.
    /// Read-only except /ui open|close (write ops, server side).
    /// </summary>
    public static class ModOps
    {
        private const int MaxList = 120;

        // ------------------------------------------------------------------ mods

        public static JObject List()
        {
            var root = new JObject();
            root["live"] = true;
            root["t"] = R(Time.realtimeSinceStartup);

            var mods = new JArray();
            int bep = 0, addon = 0, ws = 0;

            // BepInEx plugins (this monitor and anything else installed under BepInEx/plugins)
            try
            {
                var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
                if (infos != null)
                {
                    foreach (var kv in infos)
                    {
                        bep++;
                        var info = kv.Value;
                        var o = new JObject();
                        o["source"] = "bepinex";
                        o["id"] = kv.Key;
                        o["name"] = info.Metadata == null ? kv.Key : info.Metadata.Name;
                        o["version"] = info.Metadata == null ? "" : info.Metadata.Version.ToString();
                        o["dll"] = SafePath(info.Location);
                        o["entry"] = info.Instance == null ? "" : info.Instance.GetType().FullName;
                        o["loaded"] = info.Instance != null;
                        mods.Add(o);
                        if (mods.Count >= MaxList) break;
                    }
                }
            }
            catch (Exception e) { root["bepinexError"] = e.Message; }

            // AddOns channel (flat metadata.json, loads when MainWorld appears)
            try
            {
                var list = AddOnLoader.LoadedMods;
                if (list != null)
                {
                    foreach (var m in list)
                    {
                        addon++;
                        var o = new JObject();
                        o["source"] = "addons";
                        o["name"] = m.Metadata == null ? "" : m.Metadata.modName;
                        o["version"] = m.Metadata == null ? "" : m.Metadata.modVersion;
                        o["author"] = m.Metadata == null ? "" : m.Metadata.modAuthor;
                        o["folder"] = SafePath(m.FolderPath);
                        o["assembly"] = m.Assembly == null ? "" : m.Assembly.GetName().Name;
                        o["entry"] = m.Instance == null ? "" : m.Instance.GetType().FullName;
                        o["loaded"] = m.Instance != null;
                        mods.Add(o);
                        if (mods.Count >= MaxList) break;
                    }
                }
            }
            catch (Exception e) { root["addonsError"] = e.Message; }

            // Workshop channel (metadata.json nested schema, planning + verification state)
            try
            {
                var mgr = WorkshopModManager.Instance;
                if (mgr != null)
                {
                    var entries = mgr.Entries;
                    if (entries != null)
                    {
                        foreach (var e in entries)
                        {
                            ws++;
                            var o = new JObject();
                            o["source"] = "workshop";
                            o["fileId"] = e.fileId.ToString(CultureInfo.InvariantCulture);
                            o["state"] = e.state.ToString();
                            o["folder"] = SafePath(e.folderPath);
                            o["sizeOnDisk"] = e.sizeOnDisk;
                            o["progress"] = R(e.progress);
                            if (!string.IsNullOrEmpty(e.message)) o["message"] = e.message;
                            mods.Add(o);
                            if (mods.Count >= MaxList) break;
                        }
                    }
                    root["workshopState"] = mgr.State.ToString();
                    root["workshopGeneration"] = mgr.Generation;
                    root["workshopPlanVersion"] = mgr.PlanVersion;
                    var plan = mgr.LoadPlan;
                    if (plan != null) root["loadPlan"] = DescribeCollections(plan);
                }
            }
            catch (Exception e) { root["workshopError"] = e.Message; }

            root["counts"] = new JObject
            {
                ["bepinex"] = bep,
                ["addons"] = addon,
                ["workshop"] = ws,
                ["total"] = bep + addon + ws
            };
            root["mods"] = mods;
            root["keys"] = "counts,mods,workshopState,loadPlan";
            return root;
        }

        // --------------------------------------------------------------- patches

        public static JObject Patches(string q, string owner, int limit)
        {
            var root = new JObject();
            root["live"] = true;
            root["t"] = R(Time.realtimeSinceStartup);
            var rows = new JArray();
            var byOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string needle = (q ?? "").Trim();
            string ownerNeedle = (owner ?? "").Trim();
            int total = 0;
            int matched = 0;

            try
            {
                MethodInfo getInfo = typeof(Harmony).GetMethod("GetPatchInfo", new[] { typeof(MethodBase) });
                foreach (MethodBase m in Harmony.GetAllPatchedMethods())
                {
                    total++;
                    object info = getInfo == null ? null : getInfo.Invoke(null, new object[] { m });
                    if (info == null) continue;

                    string id = (m.DeclaringType == null ? "?" : m.DeclaringType.Name) + "::" + m.Name;
                    var owners = new List<string>();
                    foreach (object o in Items(info, "Owners")) owners.Add(Convert.ToString(o));
                    foreach (string o in owners)
                    {
                        int c;
                        byOwner.TryGetValue(o, out c);
                        byOwner[o] = c + 1;
                    }

                    bool hit = needle.Length == 0 || id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hit && ownerNeedle.Length > 0)
                    {
                        hit = owners.Any(x => x.IndexOf(ownerNeedle, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    if (!hit) continue;
                    matched++;
                    if (rows.Count >= limit) continue;

                    var row = new JObject();
                    row["method"] = id;
                    row["owners"] = new JArray(owners.ToArray());
                    row["prefixes"] = Items(info, "Prefixes").Count;
                    row["postfixes"] = Items(info, "Postfixes").Count;
                    row["transpilers"] = Items(info, "Transpilers").Count;
                    row["finalizers"] = Items(info, "Finalizers").Count;
                    var detail = new JArray();
                    AddPatches(detail, Items(info, "Prefixes"));
                    AddPatches(detail, Items(info, "Postfixes"));
                    AddPatches(detail, Items(info, "Transpilers"));
                    if (detail.Count > 0) row["patches"] = detail;
                    rows.Add(row);
                }
            }
            catch (Exception e) { root["error"] = e.GetType().Name + ": " + e.Message; }

            if (ownerNeedle.Length == 0 && needle.Length == 0 && byOwner.Count > 0)
            {
                var ownersArr = new JArray();
                foreach (var kv in byOwner.OrderByDescending(k => k.Value))
                {
                    ownersArr.Add(new JObject { ["owner"] = kv.Key, ["methods"] = kv.Value });
                }
                root["owners"] = ownersArr;
            }

            root["totalPatchedMethods"] = total;
            root["matched"] = matched;
            root["shown"] = rows.Count;
            root["patches"] = rows;
            root["hint"] = "owner is the Harmony instance id, usually your modId or modName; filter with q=method or owner=modId";
            return root;
        }

        /// <summary>Enumerate a Harmony patch collection (property or field, either casing).</summary>
        private static List<object> Items(object info, string name)
        {
            var list = new List<object>();
            if (info == null) return list;
            Type t = info.GetType();
            object value = null;
            bool found = false;
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                try { value = p.GetValue(info); found = true; } catch { }
                break;
            }
            if (!found)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    try { value = f.GetValue(info); found = true; } catch { }
                    break;
                }
            }
            var seq = value as IEnumerable;
            if (seq == null) return list;
            foreach (object o in seq) list.Add(o);
            return list;
        }

        private static void AddPatches(JArray into, List<object> patches)
        {
            foreach (object p in patches)
            {
                if (into.Count >= 24) return;
                var row = new JObject();
                row["owner"] = Member(p, "owner") as string ?? "";
                object pm = Member(p, "PatchMethod");
                var mi = pm as MethodBase;
                row["patch"] = mi == null || mi.DeclaringType == null ? "" : mi.DeclaringType.Name + "." + mi.Name;
                object prio = Member(p, "priority");
                if (prio != null) row["priority"] = Convert.ToInt32(prio);
                into.Add(row);
            }
        }

        private static object Member(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    try { return p.GetValue(obj); } catch { return null; }
                }
            }
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    try { return f.GetValue(obj); } catch { return null; }
                }
            }
            return null;
        }

        // ------------------------------------------------------------- mod data

        /// <summary>
        /// Read the session CSV snapshot exported after mod DB init and show rows that look
        /// mod-added: id >= min (default 900000) or name matching q.
        /// </summary>
        public static JObject Data(string kind, string q, int min, int limit)
        {
            var root = new JObject();
            root["live"] = true;
            string dir = DbDir();
            if (dir == null)
            {
                root["error"] = "no session db yet";
                root["hint"] = "enter the world once; CSVs are exported at boot (after mod DB init)";
                return root;
            }

            string[] kinds = string.IsNullOrEmpty(kind)
                ? new[] { "item", "weapon", "status", "special_stat_category", "keyword" }
                : kind.Split(',');
            var found = new JArray();
            int scanned = 0;

            foreach (string k in kinds)
            {
                string file = Path.Combine(dir, k.Trim() + ".csv");
                if (!File.Exists(file)) continue;
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (Exception e) { root["readError_" + k] = e.Message; continue; }
                if (lines.Length < 2) continue;

                List<string> header = SplitCsv(lines[0]);
                int idCol = IndexOf(header, "id");
                int nameCol = IndexOf(header, "aName");
                if (nameCol < 0) nameCol = IndexOf(header, "name");
                int rarityCol = IndexOf(header, "rarity");
                int costCol = IndexOf(header, "cost");
                int typeCol = IndexOf(header, "type");

                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i].Trim())) continue;
                    scanned++;
                    List<string> cells = SplitCsv(lines[i]);
                    string idText = idCol >= 0 && idCol < cells.Count ? cells[idCol] : "";
                    string nameText = nameCol >= 0 && nameCol < cells.Count ? cells[nameCol] : "";
                    int idVal;
                    int.TryParse(idText, out idVal);
                    bool hit = idVal >= min;
                    if (!hit && !string.IsNullOrEmpty(q) && nameText.IndexOf(q.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) hit = true;
                    if (!hit) continue;
                    if (found.Count >= limit) break;
                    var row = new JObject();
                    row["table"] = k.Trim();
                    row["id"] = idVal;
                    row["name"] = nameText;
                    if (rarityCol >= 0 && rarityCol < cells.Count) row["rarity"] = cells[rarityCol];
                    if (costCol >= 0 && costCol < cells.Count) row["cost"] = cells[costCol];
                    if (typeCol >= 0 && typeCol < cells.Count) row["type"] = cells[typeCol];
                    found.Add(row);
                }
            }

            root["dbDir"] = dir;
            root["minId"] = min;
            root["scanned"] = scanned;
            root["count"] = found.Count;
            root["rows"] = found;
            root["hint"] = "convention: mod content uses ids >= 900000; this reads the CSV exported after mod DB init, so it shows what your mod registered at load time";
            return root;
        }

        // ---------------------------------------------------------- localization

        public static JObject L10n(string key, string prefix, string lang, int limit)
        {
            var root = new JObject();
            root["live"] = true;
            var mgr = LocalizationManager.Instance;
            if (mgr == null)
            {
                root["error"] = "LocalizationManager.Instance is null (not initialized yet)";
                return root;
            }
            root["current"] = mgr.CurrentLanguage;
            FieldInfo f = typeof(LocalizationManager).GetField("textTable", BindingFlags.Instance | BindingFlags.NonPublic);
            var table = f == null ? null : f.GetValue(mgr) as IDictionary;
            if (table == null)
            {
                root["error"] = "textTable not ready";
                return root;
            }

            var langs = new List<string>();
            foreach (DictionaryEntry e in table) langs.Add(Convert.ToString(e.Key));
            langs.Sort(StringComparer.OrdinalIgnoreCase);
            root["languages"] = new JArray(langs.ToArray());

            string one = (lang ?? "").Trim();
            if (one.Length == 0) one = mgr.CurrentLanguage;

            if (!string.IsNullOrEmpty(key))
            {
                var values = new JObject();
                foreach (string L in langs)
                {
                    values[L] = ValueOf(table, L, key);
                }
                root["key"] = key;
                root["values"] = values;
                root["resolved"] = ValueOf(table, one, key);
                return root;
            }

            if (!string.IsNullOrEmpty(prefix))
            {
                var missing = new JArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int scanned = 0;
                foreach (string L in langs)
                {
                    var dict = ValueDict(table, L);
                    if (dict == null) continue;
                    foreach (var kv in dict)
                    {
                        string k = kv.Key;
                        if (k.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        scanned++;
                        if (!seen.Add(k)) continue;
                        var miss = new JArray();
                        foreach (string L2 in langs)
                        {
                            var d2 = ValueDict(table, L2);
                            if (d2 == null || !d2.ContainsKey(k)) miss.Add(L2);
                        }
                        var row = new JObject();
                        row["key"] = k;
                        row["value"] = kv.Value;
                        if (miss.Count > 0) row["missingIn"] = miss;
                        missing.Add(row);
                        if (missing.Count >= limit) break;
                    }
                    if (missing.Count >= limit) break;
                }
                root["prefix"] = prefix;
                root["scanned"] = scanned;
                root["count"] = missing.Count;
                root["keys"] = missing;
                root["hint"] = "missingIn lists languages that have no entry for that key (falls back to " + mgr.fallbackLanguage + ")";
                return root;
            }

            root["hint"] = "pass key= to resolve one key, or prefix= to audit a namespace (e.g. prefix=ModItem_)";
            return root;
        }

        private static string ValueOf(IDictionary table, string lang, string key)
        {
            var dict = ValueDict(table, lang);
            if (dict == null) return null;
            string v;
            return dict.TryGetValue(key, out v) ? v : null;
        }

        private static SortedDictionary<string, string> ValueDict(IDictionary table, string lang)
        {
            if (table == null || lang == null) return null;
            return table[lang] as SortedDictionary<string, string>;
        }

        // -------------------------------------------------------------------- ui

        public static JObject Ui(string action, string name, string panelType)
        {
            string act = string.IsNullOrEmpty(action) ? "list" : action.Trim().ToLowerInvariant();
            var root = new JObject();
            root["live"] = true;
            var mgr = UIManager.Instance;
            if (mgr == null)
            {
                root["error"] = "UIManager.Instance is null";
                return root;
            }

            if (act == "widgets")
            {
                var typed = new JArray();
                FieldInfo f = typeof(UIManager).GetField("uiElementsByTypename", BindingFlags.Instance | BindingFlags.NonPublic);
                var table = f == null ? null : f.GetValue(mgr) as IDictionary;
                if (table != null) foreach (DictionaryEntry e in table) typed.Add(Convert.ToString(e.Key));
                root["uiElementsByTypename"] = typed;
                root["uiRoots"] = mgr.uiRoots == null ? 0 : mgr.uiRoots.Count;
                root["factory"] = mgr.UIWidgets == null ? "" : mgr.UIWidgets.GetType().Name;
                return root;
            }

            if (act == "text")
            {
                // Read what the open panels actually say, without a screenshot. Text components are
                // matched by type NAME (the game uses TextMeshPro in some panels and uGUI Text in
                // others); reflection keeps this file free of extra assembly references.
                const int max = 400;
                var rows = new JArray();
                var stackAll = mgr.AllControlStack;
                if (stackAll != null)
                {
                    for (int s = 0; s < stackAll.Count; s++)
                    {
                        var layer = stackAll[s];
                        if (layer == null) continue;
                        foreach (UIBase ui in layer)
                        {
                            if (ui == null || !ui.gameObject.activeInHierarchy) continue;
                            var row = new JObject();
                            row["name"] = ui.gameObject.name;
                            row["type"] = ui.GetType().Name;
                            var texts = new JArray();
                            foreach (Component c in ui.GetComponentsInChildren<Component>(true))
                            {
                                if (c == null) continue;
                                string cn = c.GetType().Name;
                                if (cn != "Text" && cn != "TMP_Text" && cn != "TextMeshProUGUI" && cn != "TextMeshPro") continue;
                                PropertyInfo prop = c.GetType().GetProperty("text");
                                if (prop == null) continue;
                                string v = Convert.ToString(prop.GetValue(c, null));
                                if (string.IsNullOrEmpty(v)) continue;
                                v = v.Trim();
                                if (v.Length == 0) continue;
                                if (v.Length > 200) v = v.Substring(0, 200);
                                texts.Add(v);
                                if (texts.Count >= max) break;
                            }
                            row["texts"] = texts;
                            rows.Add(row);
                        }
                    }
                }
                root["panels"] = rows;
                root["keys"] = "panels[].name,type,texts";
                return root;
            }

            if (act == "open" || act == "close")
            {
                var hits = new JArray();
                string needle = name ?? "";
                UIBase[] all = Resources.FindObjectsOfTypeAll<UIBase>();
                foreach (UIBase ui in all)
                {
                    if (ui == null) continue;
                    string n = ui.gameObject.name;
                    string tn = ui.GetType().Name;
                    bool hit = needle.Length == 0
                        ? false
                        : n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                          || tn.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                    var row = new JObject();
                    row["name"] = n;
                    row["type"] = tn;
                    row["active"] = ui.gameObject.activeInHierarchy;
                    try
                    {
                        if (act == "open") { ui.Open(); row["opened"] = true; }
                        else { ui.Close(); row["closed"] = true; }
                    }
                    catch (Exception e)
                    {
                        row["error"] = e.GetType().Name + ": " + e.Message;
                    }
                    hits.Add(row);
                    if (hits.Count >= 12) break;
                }
                if (act == "close" && hits.Count == 0 && string.IsNullOrEmpty(needle))
                {
                    int closed = 0;
                    var stackAll = mgr.AllControlStack;
                    if (stackAll != null)
                    {
                        for (int s = stackAll.Count - 1; s >= 0; s--)
                        {
                            var layer = stackAll[s];
                            if (layer == null) continue;
                            for (int k = layer.Count - 1; k >= 0; k--)
                            {
                                UIBase ui = layer[k];
                                if (ui == null) continue;
                                try { ui.Close(); closed++; } catch { }
                            }
                        }
                    }
                    mgr.CloseAllControl();
                    root["closedAll"] = true;
                    root["closedCount"] = closed;
                }
                else if (act == "open" && hits.Count == 0 && needle.Length > 0)
                {
                    // Panels are created lazily: the component may not exist until the panel is
                    // opened once. UIManager.GetElement(name) resolves the registered transform.
                    string[] tries = needle.StartsWith("UI_", StringComparison.OrdinalIgnoreCase)
                        ? new[] { needle.Substring(3), needle }
                        : new[] { needle };
                    foreach (string candidate in tries)
                    {
                        Transform tf = null;
                        try { tf = mgr.GetElement(candidate); } catch { }
                        if (tf == null) continue;
                        foreach (UIBase ui in tf.GetComponentsInChildren<UIBase>(true))
                        {
                            var row = new JObject();
                            row["name"] = ui.gameObject.name;
                            row["type"] = ui.GetType().Name;
                            row["via"] = "GetElement(" + candidate + ")";
                            try { ui.Open(); row["opened"] = true; }
                            catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
                            hits.Add(row);
                        }
                        if (hits.Count > 0) break;
                    }
                    if (hits.Count == 0)
                    {
                        root["hint"] = "no component and GetElement() found nothing; see /ui?action=widgets for registered names";
                    }
                }
                root["action"] = act;
                root["matched"] = hits.Count;
                root["hits"] = hits;
                root["stackDepth"] = mgr.AllControlStack == null ? 0 : mgr.AllControlStack.Count;
                return root;
            }

            // list
            var stack = new JArray();
            var all2 = mgr.AllControlStack;
            if (all2 != null)
            {
                for (int i = 0; i < all2.Count; i++)
                {
                    var layer = all2[i];
                    if (layer == null) continue;
                    foreach (UIBase ui in layer)
                    {
                        if (ui == null) continue;
                        stack.Add(new JObject
                        {
                            ["depth"] = i,
                            ["name"] = ui.gameObject.name,
                            ["type"] = ui.GetType().Name,
                            ["esc"] = ui.canCloseControlWithESC
                        });
                    }
                }
            }
            root["depth"] = all2 == null ? 0 : all2.Count;
            root["open"] = stack;
            root["keys"] = "depth,open,widgets";
            return root;
        }

        // ---------------------------------------------------------------- helpers

        private static string DbDir()
        {
            try
            {
                string root = Session.Root;
                if (string.IsNullOrEmpty(root)) return null;
                string db = Path.Combine(root, "db");
                return Directory.Exists(db) ? db : null;
            }
            catch { return null; }
        }

        private static string SafePath(string p)
        {
            try { return string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p); }
            catch { return p ?? ""; }
        }

        private static JArray DescribeCollections(object obj)
        {
            var arr = new JArray();
            if (obj == null) return arr;
            foreach (PropertyInfo p in obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                var col = v as ICollection;
                if (col == null) continue;
                arr.Add(new JObject { ["name"] = p.Name, ["count"] = col.Count });
            }
            return arr;
        }

        private static List<string> SplitCsv(string line)
        {
            var cells = new List<string>();
            if (line == null) return cells;
            bool quoted = false;
            var sb = new System.Text.StringBuilder();
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
            return cells;
        }

        private static int IndexOf(List<string> header, string name)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static double R(float v)
        {
            return Math.Round((double)v, 2);
        }
    }
}
