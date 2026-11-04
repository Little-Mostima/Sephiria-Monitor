using System;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Unity 主线程宿主：泵送桥接队列、发心跳、定时落盘。
    /// 不在游戏里画任何东西（数据全部走 HTTP 桥）。
    /// </summary>
    public class MonitorHost : MonoBehaviour
    {
        private float nextHeartbeat;
        private float nextState;
        private int heartbeats;
        private string lastScene;

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("host update: " + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private void Tick()
        {
            MainThreadPump.Drain();
            if (F8Pressed())
            {
                ModOps.GrantAllTestItems();
            }
            Movement.Tick();
            PlayerOps.Tick();
            ModOptionsBridge.Tick();
            FloorDump.Tick();
            RunTracker.Tick();
            if (MonitorConfig.EnableTitleControl)
            {
                TitleControl.Tick();
            }

            float t = Time.realtimeSinceStartup;
            string scene = SceneTracker.Current;
            if (scene != lastScene)
            {
                lastScene = scene;
                StateApi.InvalidateCache();
                UnitRegistry.Reseed();
            }

            if (t >= nextHeartbeat)
            {
                nextHeartbeat = t + 5f;
                heartbeats++;
                System.Collections.Generic.Dictionary<string, object> hb = Json.Obj();
                hb["kind"] = "hb";
                hb["n"] = heartbeats;
                hb["unity"] = LogRing.Total;
                hb["noise"] = LogRing.Noise;
                hb["err"] = LogRing.Errors;
                hb["warn"] = LogRing.Warnings;
                hb["dmg"] = Events.DamageCount;
                hb["deaths"] = Events.DeathCount;
                hb["kills"] = Events.KillCount;
                hb["events"] = Events.Count;
                hb["cursor"] = Events.Cursor;
                hb["deduped"] = MonitorPatches.Deduped;
                hb["lateDup"] = MonitorPatches.LateDuplicates;
                hb["probes"] = Events.ProbeCount;
                hb["units"] = UnitRegistry.Count;
                hb["pending"] = MainThreadPump.Pending;
                hb["requests"] = BridgeServer.Requests;
                Events.Add(hb, false);
                // 写入器是带缓冲的（关掉了 AutoFlush），不主动 flush 的话磁盘上的日志会滞后一整个 4KB。
                Session.Flush();
            }

            if (t >= nextState)
            {
                nextState = t + 1f;
                bool inWorld = scene == "MainWorld" || SceneTracker.ChangeCount > 2;
                if (inWorld)
                {
                    PlayerState.WriteFile();
                }
            }

            if (MonitorConfig.EnableDataExport)
            {
                DataExport.Tick();
            }
        }

        private void OnDestroy()
        {
            Events.Note("shutdown", "host destroyed");
            Session.Close();
        }

        /// <summary>
        /// F8 test-grant hotkey. The game runs the Input System package, so the
        /// legacy UnityEngine.Input API throws; read the new Keyboard via
        /// reflection (no hard assembly reference) and fall back to legacy Input.
        /// </summary>
        private static bool F8Pressed()
        {
            try
            {
                Type kbd = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                if (kbd == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        kbd = asm.GetType("UnityEngine.InputSystem.Keyboard", false);
                        if (kbd != null) break;
                    }
                }
                if (kbd != null)
                {
                    object current = kbd.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
                    if (current == null) return false;
                    object key = kbd.GetProperty("f8Key", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(current, null);
                    if (key == null) return false;
                    object pressed = key.GetType().GetProperty("wasPressedThisFrame", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(key, null);
                    return pressed is bool b && b;
                }
                return Input.GetKeyDown(KeyCode.F8);
            }
            catch
            {
                return false;
            }
        }

        private void OnApplicationQuit()
        {
            Session.Flush();
        }
    }
}
