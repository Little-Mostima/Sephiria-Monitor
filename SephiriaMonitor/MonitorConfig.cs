using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace SephiriaMonitor
{
    /// <summary>
    /// Optional BepInEx config (BepInEx/config/com.surveillance.sephiriamonitor.cfg).
    /// Everything has a safe default; the file is created on first launch so other mods
    /// can turn off parts of this plugin they do not want during their own tests.
    /// </summary>
    public static class MonitorConfig
    {
        public const int DefaultBridgePort = 47901;

        public static int BridgePort = DefaultBridgePort;
        public static bool EnableTitleControl = true;
        public static bool EnableTitleHooks = true;
        public static bool EnableGameHooks = true;
        public static bool EnableSnapshots = true;
        public static bool EnableDataExport = true;
        public static bool EmitProbes = false;
        public static bool AutoEnter = false;
        public static string AutoEnterSlot = "1";

        public static void Load()
        {
            try
            {
                string dir = Paths.ConfigPath;
                Directory.CreateDirectory(dir);
                ConfigFile cfg = new ConfigFile(Path.Combine(dir, "com.surveillance.sephiriamonitor.cfg"), true);
                BridgePort = cfg.Bind("bridge", "Port", DefaultBridgePort, "Loopback HTTP port used by the bridge (127.0.0.1 only).").Value;
                EnableTitleControl = cfg.Bind("title", "EnableTitleControl", true, "Allow /enter + title polling (title screen automation).").Value;
                EnableTitleHooks = cfg.Bind("compat", "TitleHarmonyHooks", true, "Harmony postfixes on UI_TitleLobby / UI_SaveProfilePanel. Turn off if another mod patches the same methods.").Value;
                EnableGameHooks = cfg.Bind("compat", "GameEventHooks", true, "Subscribe HorayModAPI / SaveManager events (saves, floors, units, evade).").Value;
                EnableSnapshots = cfg.Bind("data", "Snapshots", true, "Capture a state snapshot on save/floor events and on /snapshot.").Value;
                EnableDataExport = cfg.Bind("data", "DatabaseExport", true, "Export the game databases to CSV once they are ready.").Value;
                AutoEnter = cfg.Bind("title", "AutoEnter", false, "Master switch for auto-loading a save on the title screen. Even when true, it only fires if the one-shot marker file (autoenter.on, 2h TTL) exists. Leave false for normal play.").Value;
                AutoEnterSlot = cfg.Bind("title", "AutoEnterSlot", "1", "Slot used by AutoEnter: 1, 2, 3 or SLOT1..SLOT3.").Value;
                EmitProbes = cfg.Bind("data", "ProbeEvents", false, "Also record self-hit probe entries (attacker == victim, dealt 0). They are hit-test noise for analysis; off by default, only their counter is kept.").Value;
                if (BridgePort < 1024 || BridgePort > 65535)
                {
                    BridgePort = DefaultBridgePort;
                }
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("config: " + ex.Message);
            }
        }

        public static string Describe()
        {
            return "port=" + BridgePort + "|title=" + (EnableTitleControl ? 1 : 0) +
                   "|titleHooks=" + (EnableTitleHooks ? 1 : 0) + "|gameHooks=" + (EnableGameHooks ? 1 : 0) +
                   "|snapshots=" + (EnableSnapshots ? 1 : 0) + "|export=" + (EnableDataExport ? 1 : 0) +
                   "|probes=" + (EmitProbes ? 1 : 0) + "|auto=" + (AutoEnter ? AutoEnterSlot : "off") +
                   "|armed=" + (AutoEnterGate.IsArmed() ? "1" : "0");
        }
    }
}
