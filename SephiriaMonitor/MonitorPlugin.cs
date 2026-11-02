using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SephiriaMonitor
{
    [BepInPlugin(Guid, "SephiriaMonitor", Version)]
    public class MonitorPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.surveillance.sephiriamonitor";
        public const string Version = "0.24.0";

        public static MonitorPlugin Instance { get; private set; }
        public static string OutDir { get; private set; }

        private static readonly List<string> Errors = new List<string>();

        public static int ErrorCount { get { lock (Errors) { return Errors.Count; } } }
        public static string LastError { get { lock (Errors) { return Errors.Count == 0 ? null : Errors[Errors.Count - 1]; } } }

        private void Awake()
        {
            try
            {
                Instance = this;
                MainThreadPump.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                OutDir = Path.GetDirectoryName(typeof(MonitorPlugin).Assembly.Location);
                Directory.CreateDirectory(OutDir);
                Session.Open(OutDir);
                MonitorConfig.Load();
                LogInfo("BOOT|plugin_awake|t=" + F(Time.realtimeSinceStartup) + "|unity=" + Application.unityVersion + "|api=" + Version);
                LogInfo("BOOT|config|" + MonitorConfig.Describe());

                GameObject host = new GameObject("SephiriaMonitorHost");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<MonitorHost>();

                SceneManager.activeSceneChanged += OnSceneChanged;
                Application.logMessageReceivedThreaded += OnUnityLog;
                if (MonitorConfig.EnableGameHooks)
                {
                    SubscribeGameEvents();
                }
                else
                {
                    LogInfo("BOOT|game_hooks|disabled");
                }
                MonitorPatches.Apply();
                PlayerOps.Apply();
                BridgeServer.Start();
                LogInfo("BOOT|ready|bridge=" + BridgeServer.Port + "|session=" + Session.Root);
            }
            catch (Exception ex)
            {
                LogError("BOOT|fatal|" + ex);
            }
        }

        private void SubscribeGameEvents()
        {
            Try(() => HorayModAPI.OnUnitSpawnedServerside += OnUnit);
            Try(() => HorayModAPI.OnUnitDespawnedServerside += OnUnitGone);
            Try(() => HorayModAPI.OnPlayerAvatarSpawnedServerside += OnPlayer);
            Try(() => HorayModAPI.OnPlayerAvatarSpawnedClientside += OnPlayer);
            Try(() => HorayModAPI.OnAllDatabasesReady += OnDatabasesReady);
            Try(() => HorayModAPI.OnStartGameServerside += OnStartServer);
            Try(() => HorayModAPI.OnStartGameClientside += OnStartClient);
            Try(() => HorayModAPI.OnFloorAllocatedServerside += OnFloor);
            Try(() => HorayModAPI.OnFloorAllocatedClientside += OnFloor);
            Try(() => HorayModAPI.OnPlayerEvade += OnEvade);
            Try(() => SaveManager.OnSaveEnd += OnSaveEnd);
            Try(() => SaveManager.OnSaveBegin += OnSaveBegin);
        }

        private static void OnSaveBegin()
        {
            Events.Note("save", "begin");
        }

        private static void OnSaveEnd()
        {
            try
            {
                Events.Note("save", "end");
                if (!MonitorConfig.EnableSnapshots) { return; }
                Snapshot.Capture("save");
                LogInfo("SNAPSHOT|save|keys=" + Snapshot.Count + "|added=" + Snapshot.Added + "|changed=" + Snapshot.Changed);
            }
            catch (Exception ex) { LogError("save snapshot: " + ex.Message); }
        }

        private static void Try(Action subscribe)
        {
            try { subscribe(); }
            catch (Exception ex) { LogError("subscribe: " + ex.Message); }
        }

        private static void OnUnit(UnitAvatar unit)
        {
            try { Events.UnitSpawned(unit); }
            catch (Exception ex) { LogError("unit event: " + ex.Message); }
            HookDie(unit);
        }

        private static readonly Dictionary<int, Action<DamageInstance>> DieHandlers = new Dictionary<int, Action<DamageInstance>>();

        /// <summary>
        /// Die() is overridden by dozens of unit classes, and most overrides never call base, so a
        /// Harmony patch on UnitAvatar.Die silently misses them. The OnDie event always fires, so
        /// subscribe per unit instead (and detach on despawn so the handler does not outlive it).
        /// </summary>
        private static void HookDie(UnitAvatar unit)
        {
            if (unit == null)
            {
                return;
            }
            try
            {
                int id = unit.GetInstanceID();
                if (DieHandlers.ContainsKey(id))
                {
                    return;
                }
                Action<DamageInstance> handler = dmg =>
                {
                    try { Events.Death(unit, dmg); }
                    catch (Exception ex) { LogError("death event: " + ex.Message); }
                };
                DieHandlers[id] = handler;
                unit.OnDie += handler;
            }
            catch (Exception ex) { LogError("hook die: " + ex.Message); }
        }

        private static void UnhookDie(UnitAvatar unit)
        {
            if (unit == null)
            {
                return;
            }
            try
            {
                int id = unit.GetInstanceID();
                Action<DamageInstance> handler;
                if (DieHandlers.TryGetValue(id, out handler))
                {
                    unit.OnDie -= handler;
                    DieHandlers.Remove(id);
                }
            }
            catch (Exception ex) { LogError("unhook die: " + ex.Message); }
        }

        private static void OnPlayer(PlayerAvatar player)
        {
            try { Events.UnitSpawned(player); }
            catch (Exception ex) { LogError("player event: " + ex.Message); }
            HookDie(player);
        }

        private static void OnUnitGone(UnitAvatar unit)
        {
            try { Events.UnitDespawned(unit); }
            catch (Exception ex) { LogError("unit despawn: " + ex.Message); }
            UnhookDie(unit);
        }

        private static void OnFloor(string guid, string floorName, FloorGenerator generator)
        {
            try { Events.Floor(guid, floorName, generator); }
            catch (Exception ex) { LogError("floor event: " + ex.Message); }
            try { Movement.ResetFloor(); }
            catch (Exception ex) { LogError("floor movement: " + ex.Message); }

            try { FloorDump.Request(guid, floorName); }
            catch (Exception ex) { LogError("floor dump: " + ex.Message); }
            try
            {
                if (!MonitorConfig.EnableSnapshots) { return; }
                Snapshot.Capture("floor");
                LogInfo("SNAPSHOT|floor|" + floorName + "|keys=" + Snapshot.Count);
            }
            catch (Exception ex) { LogError("floor snapshot: " + ex.Message); }
        }

        private static void OnEvade(PlayerAvatar player, DamageInstance damage)
        {
            try { Events.Evade(player, damage); }
            catch (Exception ex) { LogError("evade event: " + ex.Message); }
        }

        private static void OnStartClient()
        {
            Events.Note("session", "start_clientside");
        }

        private static void OnDatabasesReady()
        {
            Events.Note("dbs", "ready");
        }

        private static void OnStartServer()
        {
            Events.Note("session", "start_serverside");
        }

        private static void OnSceneChanged(Scene from, Scene to)
        {
            try
            {
                SceneTracker.OnSceneChanged(from, to);
                Events.Scene(from.name, to.name);
                if (to.name == TitleControl.TitleSceneName)
                {
                    TitleControl.OnEnteredTitle();
                }
                LogInfo("SCENE|" + (string.IsNullOrEmpty(from.name) ? "<none>" : from.name) + "|->|" + to.name + "|t=" + F(Time.realtimeSinceStartup));
            }
            catch (Exception ex)
            {
                LogError("scene: " + ex.Message);
            }
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            try { LogRing.Add(condition, stackTrace, type); }
            catch { }
        }

        private static string F(float v)
        {
            return Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static void LogInfo(string message)
        {
            try
            {
                if (Instance != null)
                {
                    Instance.Logger.LogInfo(message);
                }
            }
            catch { }
            Session.RawLog("[monitor] " + message);
        }

        public static void LogError(string message)
        {
            lock (Errors)
            {
                Errors.Add(message);
                if (Errors.Count > 50)
                {
                    Errors.RemoveAt(0);
                }
            }
            try
            {
                if (Instance != null)
                {
                    Instance.Logger.LogError(message);
                }
            }
            catch { }
            Session.RawLog("[monitor][ERROR] " + message);
        }

        private void OnDestroy()
        {
            try { BridgeServer.Stop(); } catch { }
            Session.Close();
        }
    }
}
