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
    ///
    /// Entering a save is ONE state machine, not a retry loop:
    ///   idle -> preparing -> waiting_gate -> hosting -> loading -> ready | failed
    ///
    /// Why it used to be a retry loop: UI_TitleLobby.NetworkHost() silently returns while the screen
    /// fader runs, while a workshop download is in flight, or while a session is already up, and it opens
    /// an "enter your name" dialog whenever the selected save has no PlayerName. Both are deterministic
    /// conditions, so this class checks them itself and calls NetworkHost() exactly once - after writing
    /// PlayerName into the save, so the dialog can never appear in the first place.
    /// </summary>
    public static class TitleControl
    {
        public const string TitleSceneName = "Title";
        private static readonly string[] SlotNames = { "SLOT1", "SLOT2", "SLOT3" };

        // ---------- enter state machine ----------

        public const string PhaseIdle = "idle";
        public const string PhasePreparing = "preparing";
        public const string PhaseWaitingGate = "waiting_gate";
        public const string PhaseHosting = "hosting";
        public const string PhaseLoading = "loading";
        public const string PhaseReady = "ready";
        public const string PhaseFailed = "failed";

        private const float GateTimeoutSeconds = 30f;
        private const float LoadTimeoutSeconds = 60f;

        private static string phase = PhaseIdle;
        private static string phaseDetail = "";
        private static string targetSlot;
        private static string targetName;
        private static bool targetSkipPrologue = true;
        private static float phaseStartedAt;
        private static float enterStartedAt;
        private static int hostCalls;
        private static string gateBlockedBy;
        private static string failReason;
        private static float lastNameAt = -10f;

        private static bool autoTried;
        private static float titleReadyAt = -1f;
        private static string lastSignature;
        private static float nextLogAt;
        private static float lastLogAt;

        public static string PendingState { get { return phase; } }
        public static bool IsTitle { get { return SceneTracker.Current == TitleSceneName; } }
        public static bool IsBusy
        {
            get
            {
                return phase == PhasePreparing || phase == PhaseWaitingGate ||
                       phase == PhaseHosting || phase == PhaseLoading;
            }
        }

        /// <summary>
        /// Explicit arming may re-open the once-per-process window: only /arm calls this, and /arm is
        /// something a person or an agent types on purpose, never the player's normal menu flow.
        /// </summary>
        public static void ResetAutoOnce()
        {
            autoTried = false;
        }

        /// <summary>Called from MonitorPlugin when the active scene becomes Title.</summary>
        public static void OnEnteredTitle()
        {
            if (phase == PhaseHosting || phase == PhaseLoading)
            {
                // The host never came up and the game fell back to the title screen.
                Fail("returned to title during enter");
            }
            else
            {
                Reset();
            }
            titleReadyAt = Time.realtimeSinceStartup;
            lastSignature = null;
            nextLogAt = 0f;
            MonitorPlugin.LogInfo("TITLE|ready|t=" + Time.realtimeSinceStartup.ToString("0.00"));
            InvalidateSlotCache();
        }

        private static void Reset()
        {
            phase = PhaseIdle;
            phaseDetail = "";
            targetSlot = null;
            targetName = null;
            targetSkipPrologue = true;
            hostCalls = 0;
            gateBlockedBy = null;
            failReason = null;
            // autoTried is deliberately NOT reset here: the auto-enter gate is once per process,
            // otherwise every "exit to title" would drag the player back into the save.
        }

        // ---------- read ----------

        /// <summary>
        /// Reading a slot decrypts a ~1.5 MB save. The cache is keyed on the file's mtime+size, not on a
        /// timer: a title screen that polls every 3 s used to re-decrypt all three saves every 4 s, which
        /// is exactly the "title screen feels slow" symptom. Now an unchanged file costs one stat().
        /// </summary>
        private sealed class SlotCacheEntry
        {
            public bool exists;
            public long ticks;
            public long length;
            public Dictionary<string, object> data;
        }

        private static readonly SlotCacheEntry[] slotCache = new SlotCacheEntry[3];

        public static void InvalidateSlotCache()
        {
            for (int i = 0; i < slotCache.Length; i++)
            {
                slotCache[i] = null;
            }
        }

        public static Dictionary<string, object> Slots()
        {
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
            return d;
        }

        private static Dictionary<string, object> Slot(int index)
        {
            string name = SlotNames[index];
            bool exists = false;
            string existsError = null;
            try { exists = SaveData.Exists(name); }
            catch (Exception ex) { existsError = ex.GetType().Name + ":" + ex.Message; }

            string path = null;
            long ticks = 0;
            long length = 0;
            DateTime modified = default(DateTime);
            if (exists)
            {
                try
                {
                    path = Path.Combine(SafeCommonPath(), name + ".sav");
                    if (File.Exists(path))
                    {
                        FileInfo fi = new FileInfo(path);
                        ticks = fi.LastWriteTimeUtc.Ticks;
                        length = fi.Length;
                        modified = fi.LastWriteTime;
                    }
                }
                catch { }
            }

            SlotCacheEntry cached = slotCache[index];
            if (cached != null && cached.exists == exists && cached.ticks == ticks && cached.length == length)
            {
                return cached.data;
            }

            JsonObject d = Json.Obj();
            d["slot"] = index + 1;
            d["name"] = name;
            if (existsError != null) { d["existsError"] = existsError; }
            d["exists"] = exists;
            if (exists)
            {
                if (length > 0)
                {
                    d["bytes"] = length;
                    d["modified"] = modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
                try
                {
                    SaveData data = new SaveData(true);
                    if (!data.Load(name))
                    {
                        d["loaded"] = false;
                        d["error"] = "Load returned false";
                    }
                    else
                    {
                        d["loaded"] = true;
                        d["playerName"] = data.GetString("PlayerName", "-");
                        d["saveDate"] = data.GetString("SaveDate", "-");
                        d["deathCount"] = data.GetInt("DeathCount", 0);
                        d["prologueClear"] = data.GetBool("DestinySwitch_PrologueClear", false);
                    }
                }
                catch (Exception ex)
                {
                    d["loaded"] = false;
                    d["error"] = ex.GetType().Name + ":" + ex.Message;
                }
            }

            slotCache[index] = new SlotCacheEntry { exists = exists, ticks = ticks, length = length, data = d };
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
            d["gate"] = GateBlockReason(lobby) ?? "open";
            try
            {
                d["network"] = Json.Obj().Add("server", NetworkServer.active).Add("client", NetworkClient.active);
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
            d["phase"] = phase;
            // Legacy key: scripts written against the old retry loop read "state" and expect
            // idle/running/ok/timeout. Keep it truthful so they do not need to change.
            d["state"] = LegacyState();
            if (targetSlot != null)
            {
                d["slot"] = targetSlot;
                d["name"] = targetName;
                d["hostCalls"] = hostCalls;
                d["elapsedMs"] = (int)Math.Round((Time.realtimeSinceStartup - enterStartedAt) * 1000.0);
            }
            if (!string.IsNullOrEmpty(phaseDetail)) { d["detail"] = phaseDetail; }
            if (!string.IsNullOrEmpty(gateBlockedBy)) { d["blockedBy"] = gateBlockedBy; }
            if (!string.IsNullOrEmpty(failReason)) { d["error"] = failReason; }
            return d;
        }

        private static string LegacyState()
        {
            if (phase == PhaseReady) { return "ok"; }
            if (phase == PhaseFailed) { return "timeout"; }
            if (IsBusy) { return "running"; }
            return "idle";
        }

        // ---------- write ----------

        /// <summary>
        /// Start loading a save slot exactly like clicking its card, but without any dialog:
        /// the save is prepared first (PlayerName written), then NetworkHost() is called once the
        /// game's own gate is open. Poll Pending() for phase == ready | failed.
        /// </summary>
        public static Dictionary<string, object> Enter(string slotArg, bool allowCreate, string autoName = null, bool skipPrologue = true)
        {
            JsonObject r = Json.Obj();
            if (!IsTitle)
            {
                return (Dictionary<string, object>)r.Add("ok", false).Add("error", "not on the title screen").Add("scene", SceneTracker.Current);
            }
            int index = ParseSlot(slotArg);
            if (index < 0)
            {
                return (Dictionary<string, object>)r.Add("ok", false).Add("error", "bad slot").Add("hint", "use 1, 2, 3 or SLOT1..SLOT3");
            }
            string slot = SlotNames[index];
            if (FindScene<UI_TitleLobby>() == null)
            {
                // NetworkHost() lives on UI_TitleLobby: without it the request could never run.
                return (Dictionary<string, object>)r.Add("ok", false).Add("error", "title lobby not loaded yet").Add("slot", slot)
                    .Add("hint", "retry in a second (the title screen is still building)");
            }
            if (IsBusy)
            {
                return (Dictionary<string, object>)r.Add("ok", false).Add("error", "an enter request is already running")
                    .Add("slot", targetSlot).Add("pending", Pending());
            }

            targetSlot = slot;
            targetName = string.IsNullOrWhiteSpace(autoName) ? "Tester" : autoName.Trim();
            targetSkipPrologue = skipPrologue;
            hostCalls = 0;
            gateBlockedBy = null;
            failReason = null;
            phaseDetail = "";
            enterStartedAt = Time.realtimeSinceStartup;
            phase = PhasePreparing;
            phaseStartedAt = enterStartedAt;

            MonitorPlugin.LogInfo("TITLE|enter|request|" + slot + "|create=" + allowCreate + "|name=" + targetName + "|skipPrologue=" + skipPrologue);
            Events.Note("title", "enter_request|" + slot);

            bool existed;
            string error;
            if (!WriteProfile(slot, targetName, targetSkipPrologue, false, allowCreate, out existed, out error))
            {
                Fail(error);
                return (Dictionary<string, object>)r.Add("ok", false).Add("error", error).Add("slot", slot).Add("pending", Pending());
            }
            if (!SetSelectedProfile(slot, out error))
            {
                Fail(error);
                return (Dictionary<string, object>)r.Add("ok", false).Add("error", error).Add("slot", slot).Add("pending", Pending());
            }

            phase = PhaseWaitingGate;
            phaseStartedAt = Time.realtimeSinceStartup;
            phaseDetail = "gate_pending";

            r["ok"] = true;
            r["requested"] = slot;
            r["mode"] = existed ? "load" : "create";
            r["name"] = targetName;
            r["pending"] = Pending();
            return r;
        }

        /// <summary>
        /// Create or fill a profile without starting the game: make the save when missing, write
        /// PlayerName + the prologue-clear switch, save. Enter() does the same thing internally; this
        /// endpoint stays for callers that want to inspect the slot before entering.
        /// </summary>
        public static Dictionary<string, object> PrepareNew(string slotArg, string playerName, bool fresh = false)
        {
            JsonObject r = Json.Obj();
            string slot = string.IsNullOrEmpty(slotArg) ? SaveManager.defaultSlotName : slotArg;
            if (string.IsNullOrEmpty(playerName)) { playerName = "Tester"; }
            if (!IsTitle)
            {
                return (Dictionary<string, object>)r.Add("ok", false).Add("error", "not on title screen").Add("scene", SceneTracker.Current);
            }
            bool existed;
            string error;
            if (!WriteProfile(slot, playerName, true, fresh, true, out existed, out error))
            {
                return (Dictionary<string, object>)r.Add("ok", false).Add("error", error).Add("slot", slot);
            }
            r["ok"] = true;
            r["slot"] = slot;
            r["existed"] = existed;
            r["name"] = playerName;
            return r;
        }

        /// <summary>
        /// Make the slot loadable without any dialog: create it when missing, and write PlayerName
        /// (plus the prologue-clear switch) when it is still empty. A save that already has a name is
        /// left completely untouched - that is the plain load path the game itself takes.
        /// </summary>
        private static bool WriteProfile(string slot, string name, bool skipPrologue, bool fresh, bool createIfMissing,
                                         out bool existed, out string error)
        {
            existed = false;
            error = null;
            try
            {
                try { existed = SaveData.Exists(slot); } catch { existed = false; }
                if (fresh || !existed)
                {
                    if (!fresh && !createIfMissing)
                    {
                        error = "slot is empty";
                        return false;
                    }
                    SaveManager.CreateNew(slot);
                    SaveManager.CreateNewTMP(slot);
                    existed = false;
                }
                else
                {
                    // Cheap read-only probe: never touch SaveManager.Current for a save that needs nothing.
                    SaveData probe = new SaveData(true);
                    if (probe.Load(slot) &&
                        !string.IsNullOrWhiteSpace(probe.GetString("PlayerName", "").Trim().Trim('\u200b')))
                    {
                        return true;
                    }
                }
                if (!SaveManager.Load(slot))
                {
                    error = "SaveManager.Load failed";
                    return false;
                }
                if (!SaveManager.LoadTMP(slot))
                {
                    SaveManager.CreateNewTMP(slot);
                    SaveManager.LoadTMP(slot);
                }
                SaveManager.Current.SetString("PlayerName", name);
                if (skipPrologue)
                {
                    SaveManager.Current.SetBool("DestinySwitch_PrologueClear", true);
                }
                SaveManager.Save(saveCurrent: true, saveCurrentRun: false);
                InvalidateSlotCache();
                MonitorPlugin.LogInfo("TITLE|profile|written|" + slot + "|name=" + name + "|skipPrologue=" + skipPrologue + "|fresh=" + fresh);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":" + ex.Message;
                MonitorPlugin.LogError("TITLE|profile|failed|" + slot + "|" + error);
                return false;
            }
        }

        private static bool SetSelectedProfile(string slot, out string error)
        {
            error = null;
            try
            {
                if (OptionsBinding.Instance == null || OptionsBinding.Instance.Options == null)
                {
                    error = "no_options_binding";
                    return false;
                }
                string current = null;
                try { current = OptionsBinding.Instance.Options.GetString("SelectedProfile", ""); } catch { }
                if (current == slot)
                {
                    return true;
                }
                OptionsBinding.Instance.Options.SetString("SelectedProfile", slot);
                OptionsBinding.Instance.Save();
                MonitorPlugin.LogInfo("TITLE|option|SelectedProfile=" + slot + "|was=" + (current ?? ""));
                return true;
            }
            catch (Exception ex)
            {
                error = "options:" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        // ---------- tick ----------

        /// <summary>Driven by MonitorHost.Update: advances the enter state machine and keeps the title log meaningful.</summary>
        public static void Tick()
        {
            float t = Time.realtimeSinceStartup;
            if (phase == PhaseWaitingGate)
            {
                TickGate(t);
            }
            else if (phase == PhaseLoading)
            {
                TickLoading(t);
            }

            if (!IsTitle)
            {
                return;
            }
            TickAutoEnter(t);
            if (t >= nextLogAt)
            {
                LogTitleState(t);
            }
        }

        /// <summary>
        /// The exact condition UI_TitleLobby.NetworkHost() checks before doing anything. Returns null
        /// when the gate is open, otherwise a short machine-readable reason.
        /// </summary>
        private static string GateBlockReason(UI_TitleLobby lobby)
        {
            if (lobby == null) { return "no_lobby"; }
            try
            {
                ScreenFader fader = ScreenFader.Instance;
                if (fader != null && fader.IsFading) { return "fader:" + fader.FadingState; }
            }
            catch { }
            try { if (SteamInvitation.waitForExternalConnect) { return "steam_wait"; } } catch { }
            try { if (NetworkServer.active) { return "server_active"; } } catch { }
            try { if (NetworkClient.active) { return "client_active"; } } catch { }
            try { if (UI_TitleLobby.IsWorkshopDownloading) { return "workshop_downloading"; } } catch { }
            return null;
        }

        private static void TickGate(float t)
        {
            UI_TitleLobby lobby = FindScene<UI_TitleLobby>();
            string block = GateBlockReason(lobby);
            if (block != null)
            {
                if (gateBlockedBy != block)
                {
                    gateBlockedBy = block;
                    MonitorPlugin.LogInfo("TITLE|enter|gate|" + targetSlot + "|blocked=" + block);
                }
                if (t - phaseStartedAt > GateTimeoutSeconds)
                {
                    Fail("gate blocked for " + GateTimeoutSeconds.ToString("0") + "s: " + block);
                }
                return;
            }

            gateBlockedBy = null;
            phase = PhaseHosting;
            phaseStartedAt = t;
            hostCalls++;
            MonitorPlugin.LogInfo("TITLE|enter|hosting|" + targetSlot + "|n=" + hostCalls +
                                  "|waitedMs=" + ((t - enterStartedAt) * 1000f).ToString("0"));
            try
            {
                lobby.NetworkHost();
            }
            catch (Exception ex)
            {
                Fail("NetworkHost threw " + ex.GetType().Name + ":" + ex.Message);
                return;
            }
            phase = PhaseLoading;
            phaseStartedAt = t;
            phaseDetail = "host_called";
        }

        private static void TickLoading(float t)
        {
            bool server = false;
            bool client = false;
            try { server = NetworkServer.active; client = NetworkClient.active; } catch { }
            string scene = SceneTracker.Current;

            // NetworkServer.active turns true the moment StartHost() runs, before the online scene has
            // finished loading - so "server active" alone is a false positive. The world is only really
            // there once the scene has left the title screen.
            bool inWorld = scene == "MainWorld" || ((server || client) && scene != TitleSceneName && scene != "Intro");
            if (inWorld)
            {
                phase = PhaseReady;
                phaseDetail = "entered|server=" + B(server) + "|client=" + B(client) + "|scene=" + scene;
                MonitorPlugin.LogInfo("TITLE|enter|ready|" + targetSlot + "|hostCalls=" + hostCalls + "|scene=" + scene +
                                      "|totalMs=" + ((t - enterStartedAt) * 1000f).ToString("0"));
                Events.Note("title", "enter_ready|" + targetSlot + "|" + scene);
                return;
            }

            // Safety net: writing PlayerName should have removed the name dialog entirely. If the game
            // still opened one the write did not stick - answer it instead of hanging until the timeout.
            if (targetName != null && TryAnswerNamePrompt(targetName))
            {
                phaseDetail = "fallback_name";
                return;
            }
            if (TrySkipPrologue())
            {
                phaseDetail = "fallback_skip_prologue";
                return;
            }
            if (t - phaseStartedAt > LoadTimeoutSeconds)
            {
                Fail("host did not start in " + LoadTimeoutSeconds.ToString("0") + "s|scene=" + scene +
                     "|prompt=" + (NamePromptOpen() ? "name" : "-"));
            }
        }

        private static void Fail(string reason)
        {
            phase = PhaseFailed;
            failReason = reason;
            phaseDetail = reason;
            MonitorPlugin.LogError("TITLE|enter|failed|" + targetSlot + "|" + reason + "|hostCalls=" + hostCalls);
            Events.Note("title", "enter_failed|" + targetSlot + "|" + reason);
        }

        private static void TickAutoEnter(float t)
        {
            if (autoTried || IsBusy)
            {
                return;
            }
            if (!MonitorConfig.AutoEnter)
            {
                autoTried = true;
                MonitorPlugin.LogInfo("TITLE|auto|skip|reason=config-off");
                return;
            }
            if (FindScene<UI_TitleLobby>() == null)
            {
                return;
            }
            string gateReason;
            bool armed = AutoEnterGate.Check(out gateReason);
            autoTried = true;
            if (!armed)
            {
                MonitorPlugin.LogInfo("TITLE|auto|skip|reason=" + gateReason);
                return;
            }
            float waited = titleReadyAt < 0f ? 0f : (Time.realtimeSinceStartup - titleReadyAt) * 1000f;
            string autoSlot = AutoEnterGate.Slot > 0 ? AutoEnterGate.Slot.ToString() : MonitorConfig.AutoEnterSlot;
            Dictionary<string, object> auto = Enter(autoSlot, false);
            bool ok = auto.ContainsKey("ok") && auto["ok"] is bool && (bool)auto["ok"];
            // one-shot: the marker can never cause a second auto-enter, even if the attempt failed
            AutoEnterGate.Delete();
            MonitorPlugin.LogInfo("TITLE|auto|slot=" + autoSlot + "|ok=" + ok +
                                  "|gate=" + gateReason + "|afterReadyMs=" + waited.ToString("0") +
                                  (auto.ContainsKey("error") ? "|error=" + auto["error"] : ""));
            Events.Note("title", "auto_enter|" + autoSlot + "|ok=" + ok);
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
                string bits = Occupancy();
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
                e["pending"] = phase;
                // Slots() decrypts each save once and caches the result: reuse it instead of loading
                // all three save files a second and third time here.
                List<object> slots = Slots()["slots"] as List<object> ?? new List<object>();
                string summary = "TITLE|state|lobby=" + B(lobby != null) + "|panel=" + B(panelOpen) +
                                 "|profile=" + profile + "|slots=" + bits + "|pending=" + phase +
                                 "|bridge=" + BridgeServer.Port;
                for (int i = 0; i < slots.Count; i++)
                {
                    Dictionary<string, object> s = slots[i] as Dictionary<string, object>;
                    string desc = SlotNames[i];
                    if (s != null)
                    {
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
                    }
                    summary += "|" + desc;
                }
                e["slots"] = slots;
                Events.Add(e);

                WriteTitleFile(TitleInfo());
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

        private static string B(bool b)
        {
            return b ? "1" : "0";
        }

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

        // ---------- dialog fallbacks ----------
        //
        // These only run when the save write did not take effect. They are not part of the normal
        // path: Enter() writes PlayerName first precisely so neither dialog can appear.

        private static UI_MessageBox_InputYesNo FindNamePrompt()
        {
            UI_MessageBox_InputYesNo box = FindScene<UI_MessageBox_InputYesNo>(false);
            if (box == null || !box.gameObject.activeInHierarchy || box.input == null || box.yesButton == null)
            {
                return null;
            }
            return box;
        }

        private static bool NamePromptOpen()
        {
            return FindNamePrompt() != null;
        }

        private static bool TryAnswerNamePrompt(string name)
        {
            float t = Time.realtimeSinceStartup;
            if (t - lastNameAt < 0.5f)
            {
                return false;
            }
            UI_MessageBox_InputYesNo box = FindNamePrompt();
            if (box == null)
            {
                return false;
            }
            lastNameAt = t;
            try
            {
                string wanted = (name ?? "").Trim();
                if (box.input.textComponent != null)
                {
                    box.input.textComponent.text = wanted;
                }
                box.input.text = wanted;
                box.yesButton.onClick.Invoke();
                MonitorPlugin.LogInfo("TITLE|enter|fallback_name|" + targetSlot + "|name=" + wanted);
                Events.Note("title", "fallback_name|" + targetSlot + "|" + wanted);
                return true;
            }
            catch (Exception ex)
            {
                phaseDetail = "fallback_name:" + ex.GetType().Name + ":" + ex.Message;
                MonitorPlugin.LogError("TITLE|enter|" + phaseDetail);
                return false;
            }
        }

        /// <summary>Right after the name, a save with a cleared prologue elsewhere in the slots triggers
        /// the "skip prologue?" YesNo; test profiles always skip it (WriteProfile writes the same switch).</summary>
        private static bool TrySkipPrologue()
        {
            float t = Time.realtimeSinceStartup;
            if (t - lastNameAt < 0.5f)
            {
                return false;
            }
            UI_MessageBox_YesNo box = FindScene<UI_MessageBox_YesNo>(false);
            if (box == null || !box.gameObject.activeInHierarchy || box.text == null || box.yesButton == null)
            {
                return false;
            }
            string wanted = null;
            try
            {
                UI_TitleLobby lobby = FindScene<UI_TitleLobby>();
                if (lobby != null) { wanted = lobby.skipPrologue.ToString(); }
            }
            catch { }
            if (string.IsNullOrEmpty(wanted) || box.text.text == null || box.text.text.Trim() != wanted.Trim())
            {
                return false;
            }
            lastNameAt = t;
            try
            {
                box.yesButton.onClick.Invoke();
                MonitorPlugin.LogInfo("TITLE|enter|fallback_skip_prologue|" + targetSlot);
                Events.Note("title", "fallback_skip_prologue|" + targetSlot);
                return true;
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("TITLE|enter|fallback_skip_prologue|failed|" + ex.Message);
                return false;
            }
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
