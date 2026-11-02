using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Mirror;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Title-screen control surface: read the three save slots and drive "start game" without screenshots.
    /// NetworkHost() is the game's own entry point (UI_SaveProfilePanel.SelectProfile -> UI_TitleLobby.NetworkHost),
    /// but it bails out while the screen fader runs, so the request is retried from MonitorHost.Update.
    /// </summary>
    public static class TitleControl
    {
        public const string TitleSceneName = "Title";
        private static readonly string[] SlotNames = { "SLOT1", "SLOT2", "SLOT3" };

        private static string pendingSlot;
        private static float pendingStartedAt;
        private static float lastAttemptAt;
        private static int pendingAttempts;
        private static string pendingState = "idle";
        private static string pendingDetail = "";
        private static string lastSignature;
        private static float nextLogAt;
        private static float lastLogAt;
        private static bool autoTried;
        private static float titleReadyAt = -1f;

        /// <summary>
        /// Explicit arming may re-open the once-per-process window: only /arm calls this, and /arm is
        /// something a person or an agent types on purpose, never the player's normal menu flow.
        /// </summary>
        public static void ResetAutoOnce()
        {
            autoTried = false;
        }

        public static string PendingState { get { return pendingState; } }
        public static bool IsTitle { get { return SceneTracker.Current == TitleSceneName; } }

        /// <summary>Called from MonitorPlugin when the active scene becomes Title.</summary>
        public static void OnEnteredTitle()
        {
            pendingSlot = null;
            pendingState = "idle";
            pendingDetail = "";
            pendingAttempts = 0;
            lastSignature = null;
            nextLogAt = 0f;
            // autoTried is deliberately NOT reset here: the auto-enter gate is once per process,
            // otherwise every "exit to title" would drag the player back into the save.
            titleReadyAt = Time.realtimeSinceStartup;
            MonitorPlugin.LogInfo("TITLE|ready|t=" + Time.realtimeSinceStartup.ToString("0.00"));
            InvalidateSlotCache();
        }

        // ---------- read ----------

        /// <summary>Reading a slot decrypts a ~1.5 MB save, so /saves, /title and /api share a short TTL cache.</summary>
        public const float SlotsCacheSeconds = 4f;

        private static Dictionary<string, object> slotsCache;
        private static float slotsCacheAt;

        public static void InvalidateSlotCache()
        {
            slotsCache = null;
            slotsCacheAt = 0f;
        }

        /// <summary>Slot list for other callers; never throws when the cache entry is malformed.</summary>
        public static List<object> SlotList()
        {
            Dictionary<string, object> all = Slots();
            object v;
            if (all != null && all.TryGetValue("slots", out v))
            {
                List<object> list = v as List<object>;
                if (list != null) { return list; }
            }
            return new List<object>();
        }

        public static Dictionary<string, object> Slots()
        {
            float now = Time.realtimeSinceStartup;
            if (slotsCache != null && now - slotsCacheAt < SlotsCacheSeconds)
            {
                return slotsCache;
            }
            JsonObject d = Json.Obj();
            d["scene"] = SceneTracker.Current;
            d["commonPath"] = SafeCommonPath();
            d["selected"] = SelectedProfile();
            List<object> list = new List<object>();
            for (int i = 0; i < SlotNames.Length; i++)
            {
                list.Add(Slot(i));
            }
            d["slots"] = list;
            slotsCache = d;
            slotsCacheAt = now;
            return d;
        }

        private static Dictionary<string, object> Slot(int index)
        {
            JsonObject d = Json.Obj();
            string name = SlotNames[index];
            d["slot"] = index + 1;
            d["name"] = name;
            bool exists = false;
            try { exists = SaveData.Exists(name); }
            catch (Exception ex) { d["existsError"] = ex.GetType().Name + ":" + ex.Message; }
            d["exists"] = exists;
            if (!exists)
            {
                return d;
            }
            try
            {
                string path = Path.Combine(SafeCommonPath(), name + ".sav");
                if (File.Exists(path))
                {
                    DateTime write = File.GetLastWriteTime(path);
                    FileInfo fi = new FileInfo(path);
                    d["bytes"] = fi.Length;
                    d["modified"] = write.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
            }
            catch { }
            try
            {
                SaveData data = new SaveData(true);
                if (!data.Load(name))
                {
                    d["loaded"] = false;
                    d["error"] = "Load returned false";
                    return d;
                }
                d["loaded"] = true;
                d["playerName"] = data.GetString("PlayerName", "-");
                d["saveDate"] = data.GetString("SaveDate", "-");
                d["deathCount"] = data.GetInt("DeathCount", 0);
                d["prologueClear"] = data.GetBool("DestinySwitch_PrologueClear", false);
            }
            catch (Exception ex)
            {
                d["loaded"] = false;
                d["error"] = ex.GetType().Name + ":" + ex.Message;
            }
            return d;
        }

        public static Dictionary<string, object> TitleInfo()
        {
            JsonObject d = Json.Obj();
            d["scene"] = SceneTracker.Current;
            d["title"] = IsTitle;
            UI_TitleLobby lobby = FindScene<UI_TitleLobby>();
            UI_SaveProfilePanel panel = FindScene<UI_SaveProfilePanel>(false);
            d["lobby"] = lobby != null;
            d["panel"] = panel != null;
            d["panelOpen"] = panel != null && panel.gameObject.activeInHierarchy;
            d["canEnter"] = IsTitle && lobby != null;
            d["selected"] = SelectedProfile();
            try
            {
                bool server = NetworkServer.active;
                bool client = NetworkClient.active;
                d["network"] = Json.Obj().Add("server", server).Add("client", client);
            }
            catch (Exception ex) { d["network"] = Json.Obj().Add("error", ex.GetType().Name); }
            d["pending"] = Pending();
            d["bridge"] = Json.Obj().Add("port", BridgeServer.Port).Add("running", BridgeServer.IsRunning).Add("requests", BridgeServer.Requests);
            d["slots"] = Slots()["slots"];
            return d;
        }

        public static Dictionary<string, object> Pending()
        {
            JsonObject d = Json.Obj();
            d["state"] = pendingState;
            if (pendingSlot != null)
            {
                d["slot"] = pendingSlot;
                d["attempts"] = pendingAttempts;
                d["elapsedMs"] = (int)Math.Round((Time.realtimeSinceStartup - pendingStartedAt) * 1000.0);
            }
            if (!string.IsNullOrEmpty(pendingDetail))
            {
                d["detail"] = pendingDetail;
            }
            return d;
        }

        // ---------- write (explicit, only via /enter) ----------

        /// <summary>Start loading a save slot exactly like clicking its card: set SelectedProfile, then NetworkHost().</summary>
        public static Dictionary<string, object> Enter(string slotArg, bool allowCreate)
        {
            if (!IsTitle)
            {
                return (Dictionary<string, object>)Json.Obj().Add("ok", false).Add("error", "not on the title screen").Add("scene", SceneTracker.Current);
            }
            int index = ParseSlot(slotArg);
            if (index < 0)
            {
                return (Dictionary<string, object>)Json.Obj().Add("ok", false).Add("error", "bad slot").Add("hint", "use 1, 2, 3 or SLOT1..SLOT3");
            }
            string slot = SlotNames[index];
            if (FindScene<UI_TitleLobby>() == null)
            {
                // NetworkHost() lives on UI_TitleLobby: without it the request could never run.
                return (Dictionary<string, object>)Json.Obj().Add("ok", false).Add("error", "title lobby not loaded yet").Add("slot", slot)
                    .Add("hint", "retry in a second (the title screen is still building)");
            }
            bool exists = false;
            try { exists = SaveData.Exists(slot); } catch { }
            if (!exists && !allowCreate)
            {
                return (Dictionary<string, object>)Json.Obj().Add("ok", false).Add("error", "slot is empty").Add("slot", slot)
                    .Add("hint", "pass create=1 to start a brand new save instead");
            }

            MonitorPlugin.LogInfo("TITLE|enter|request|" + slot + "|exists=" + exists + "|create=" + allowCreate);
            Events.Note("title", "enter_request|" + slot);

            pendingSlot = slot;
            pendingState = "running";
            pendingDetail = "";
            pendingAttempts = 0;
            pendingStartedAt = Time.realtimeSinceStartup;
            lastAttemptAt = 0f;
            Attempt("initial");

            JsonObject r = Json.Obj();
            r["ok"] = true;
            r["requested"] = slot;
            r["mode"] = exists ? "load" : "create";
            r["pending"] = Pending();
            return r;
        }

        private static void Attempt(string reason)
        {
            if (pendingSlot == null)
            {
                return;
            }
            lastAttemptAt = Time.realtimeSinceStartup;
            try
            {
                if (OptionsBinding.Instance != null && OptionsBinding.Instance.Options != null)
                {
                    // Write the option (and the options file) only when it actually changes: the
                    // retry loop used to rewrite the whole options file up to 60 times per request.
                    string current = null;
                    try { current = OptionsBinding.Instance.Options.GetString("SelectedProfile", ""); } catch { }
                    if (current != pendingSlot)
                    {
                        OptionsBinding.Instance.Options.SetString("SelectedProfile", pendingSlot);
                        OptionsBinding.Instance.Save();
                        MonitorPlugin.LogInfo("TITLE|option|SelectedProfile=" + pendingSlot + "|was=" + (current ?? ""));
                    }
                }
                else
                {
                    pendingDetail = "no_options_binding";
                }
            }
            catch (Exception ex) { pendingDetail = "options:" + ex.GetType().Name + ":" + ex.Message; }

            UI_TitleLobby lobby = FindScene<UI_TitleLobby>();
            if (lobby == null)
            {
                pendingDetail = "no_title_lobby";
                return;
            }
            try
            {
                pendingAttempts++;
                lobby.NetworkHost();
                if (string.IsNullOrEmpty(pendingDetail)) pendingDetail = "networkhost_called";
                MonitorPlugin.LogInfo("TITLE|enter|attempt|" + pendingSlot + "|n=" + pendingAttempts + "|via=" + reason +
                                      "|scene=" + SceneTracker.Current + "|server=" + SafeActive(NetworkServer.active) +
                                      "|client=" + SafeActive(NetworkClient.active));
            }
            catch (Exception ex)
            {
                pendingDetail = "networkhost:" + ex.GetType().Name + ":" + ex.Message;
                MonitorPlugin.LogError("TITLE|enter|" + pendingDetail);
            }
        }

        private static string SafeActive(bool b)
        {
            return b ? "1" : "0";
        }

        // ---------- tick ----------

        /// <summary>Driven by MonitorHost.Update: retries the enter request and keeps the title log meaningful.</summary>
        public static void Tick()
        {
            float t = Time.realtimeSinceStartup;

            if (pendingSlot != null)
            {
                bool server = false;
                bool client = false;
                try { server = NetworkServer.active; client = NetworkClient.active; } catch { }
                string scene = SceneTracker.Current;

                if (server || client || scene == "MainWorld")
                {
                    pendingState = "ok";
                    pendingDetail = "entered|server=" + SafeActive(server) + "|client=" + SafeActive(client) + "|scene=" + scene;
                    MonitorPlugin.LogInfo("TITLE|enter|ok|" + pendingSlot + "|attempts=" + pendingAttempts + "|scene=" + scene + "|enterMs=" + ((Time.realtimeSinceStartup - pendingStartedAt) * 1000f).ToString("0"));
                    Events.Note("title", "enter_ok|" + pendingSlot + "|" + scene);
                    pendingSlot = null;
                }
                else if (t - pendingStartedAt > 60f)
                {
                    pendingState = "timeout";
                    pendingDetail = "timeout|scene=" + scene + "|attempts=" + pendingAttempts;
                    MonitorPlugin.LogError("TITLE|enter|timeout|" + pendingDetail);
                    Events.Note("title", "enter_timeout|" + pendingDetail);
                    pendingSlot = null;
                }
                else if (t - lastAttemptAt >= 0.3f)
                {
                    Attempt("retry");
                }
            }

            if (!IsTitle)
            {
                return;
            }
            if (!autoTried && pendingSlot == null)
            {
                if (!MonitorConfig.AutoEnter)
                {
                    autoTried = true;
                    MonitorPlugin.LogInfo("TITLE|auto|skip|reason=config-off");
                }
                else if (FindScene<UI_TitleLobby>() != null)
                {
                    string gateReason;
                    bool armed = AutoEnterGate.Check(out gateReason);
                    autoTried = true;
                    if (!armed)
                    {
                        MonitorPlugin.LogInfo("TITLE|auto|skip|reason=" + gateReason);
                    }
                    else
                    {
                        float waited = titleReadyAt < 0f ? 0f : (Time.realtimeSinceStartup - titleReadyAt) * 1000f;
                        Dictionary<string, object> auto = Enter(MonitorConfig.AutoEnterSlot, false);
                        bool ok = auto.ContainsKey("ok") && auto["ok"] is bool && (bool)auto["ok"];
                        // one-shot: the marker can never cause a second auto-enter, even if the attempt failed
                        AutoEnterGate.Delete();
                        MonitorPlugin.LogInfo("TITLE|auto|slot=" + MonitorConfig.AutoEnterSlot + "|ok=" + ok +
                                              "|gate=" + gateReason + "|afterReadyMs=" + waited.ToString("0") +
                                              (auto.ContainsKey("error") ? "|error=" + auto["error"] : ""));
                        Events.Note("title", "auto_enter|" + MonitorConfig.AutoEnterSlot + "|ok=" + ok);
                    }
                }
            }
            if (t >= nextLogAt)
            {
                LogTitleState(t);
            }
        }

        /// <summary>Emits a compact, machine-readable line about what the title screen currently offers.</summary>
        private static void LogTitleState(float t)
        {
            nextLogAt = t + 3f;
            try
            {
                UI_TitleLobby lobby = FindScene<UI_TitleLobby>();
                UI_SaveProfilePanel panel = FindScene<UI_SaveProfilePanel>();
                bool panelOpen = panel != null && panel.gameObject.activeInHierarchy;
                string profile = SelectedProfile();
                string bits = "";
                for (int i = 0; i < SlotNames.Length; i++)
                {
                    bool exists = false;
                    try { exists = SaveData.Exists(SlotNames[i]); } catch { }
                    bits += exists ? "1" : "0";
                }
                string signature = (lobby != null ? "L1" : "L0") + "|" + (panelOpen ? "P1" : "P0") + "|" + profile + "|" + bits;
                if (signature == lastSignature && t - lastLogAt < 30f)
                {
                    return;
                }
                lastSignature = signature;
                lastLogAt = t;

                JsonObject e = Json.Obj();
                e["kind"] = "title";
                e["phase"] = "state";
                e["lobby"] = lobby != null;
                e["panelOpen"] = panelOpen;
                e["profile"] = profile;
                e["occupancy"] = bits;
                e["pending"] = pendingState;
                // Slot() already decrypts each save once (and caches for a few seconds): reuse that
                // instead of loading all three save files a second and third time here.
                List<object> slots = new List<object>();
                string summary = "TITLE|state|lobby=" + SafeActive(lobby != null) + "|panel=" + SafeActive(panelOpen) +
                                 "|profile=" + profile + "|slots=" + bits + "|pending=" + pendingState +
                                 "|bridge=" + BridgeServer.Port;
                for (int i = 0; i < SlotNames.Length; i++)
                {
                    Dictionary<string, object> s = Slot(i);
                    slots.Add(s);
                    string desc = SlotNames[i];
                    object exists, loaded, playerName, saveDate;
                    if (s.TryGetValue("exists", out exists) && exists is bool && (bool)exists)
                    {
                        if (s.TryGetValue("loaded", out loaded) && loaded is bool && (bool)loaded &&
                            s.TryGetValue("playerName", out playerName) && s.TryGetValue("saveDate", out saveDate))
                        {
                            desc += "(" + playerName + "," + saveDate + ")";
                        }
                        else
                        {
                            desc += "(load-error)";
                        }
                    }
                    summary += "|" + desc;
                }
                e["slots"] = slots;
                Events.Add(e);

                Dictionary<string, object> info = TitleInfo();
                WriteTitleFile(info);

                MonitorPlugin.LogInfo(summary);
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("TITLE|state|" + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private static void WriteTitleFile(Dictionary<string, object> info)
        {
            if (Session.Root == null)
            {
                return;
            }
            try
            {
                File.WriteAllText(Path.Combine(Session.Root, "title.json"), Json.Write(info), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("title.json: " + ex.Message);
            }
        }

        // ---------- helpers ----------

        /// <summary>e.g. "1|0|1": which slots hold a save file.</summary>
        public static string Occupancy()
        {
            string bits = "";
            for (int i = 0; i < SlotNames.Length; i++)
            {
                bool exists = false;
                try { exists = SaveData.Exists(SlotNames[i]); } catch { }
                bits += exists ? "1" : "0";
            }
            return bits;
        }

        public static string SelectedProfile()
        {
            try
            {
                if (OptionsBinding.Instance != null && OptionsBinding.Instance.Options != null)
                {
                    return OptionsBinding.Instance.Options.GetString("SelectedProfile", "SLOT1");
                }
            }
            catch { }
            return "SLOT1";
        }

        private static string SafeCommonPath()
        {
            try { return SaveData.CommonPath; } catch { return ""; }
        }

        private static int ParseSlot(string arg)
        {
            string s = (arg ?? "").Trim().ToUpperInvariant();
            if (s.Length == 0)
            {
                return -1;
            }
            if (s.StartsWith("SLOT", StringComparison.Ordinal))
            {
                s = s.Substring(4);
            }
            int n;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            {
                return -1;
            }
            return (n >= 1 && n <= SlotNames.Length) ? n - 1 : -1;
        }

        private static T FindScene<T>() where T : UnityEngine.Object
        {
            return FindScene<T>(true);
        }

        private static T FindScene<T>(bool requireActive) where T : UnityEngine.Object
        {
            T[] all = Resources.FindObjectsOfTypeAll<T>();
            for (int i = 0; i < all.Length; i++)
            {
                T item = all[i];
                if (item == null)
                {
                    continue;
                }
                Component c = item as Component;
                if (c == null)
                {
                    continue;
                }
                if (c.gameObject.scene.IsValid() && (!requireActive || c.gameObject.activeInHierarchy))
                {
                    return item;
                }
            }
            return null;
        }
    }
}
