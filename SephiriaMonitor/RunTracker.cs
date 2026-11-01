using System;
using System.Collections.Generic;
using System.Globalization;

namespace SephiriaMonitor
{
    /// <summary>
    /// 对局边界跟踪：轮询 DungeonManager 的 (isRunStarted, DestinySeed, sortedStages) 三元组，
    /// 变化时发一条 kind="run" 事件并重置 Combat 聚合，这样摘要可以精确按局切分，
    /// 不再靠"回到城镇"这种启发式。
    /// 事件：{kind:"run", state:"start"|"end", seed, stages, floor, raceId, players}
    /// </summary>
    public static class RunTracker
    {
        private static bool inited;
        private static bool lastStarted;
        private static int lastSeed = int.MinValue;
        private static int lastStages = -1;
        private static string lastFloor = "";
        private static int lastRaceId = int.MinValue;

        private static DungeonManager Dungeon
        {
            get
            {
                try { return DungeonManager.Instance; }
                catch { return null; }
            }
        }

        public static void Tick()
        {
            DungeonManager dm = Dungeon;
            if (dm == null)
            {
                if (inited && lastStarted)
                {
                    Emit("end", lastSeed, lastStages, lastFloor, lastRaceId, "dungeon gone");
                    lastStarted = false;
                }
                return;
            }

            bool started;
            int seed;
            int stages;
            int raceId;
            try
            {
                started = dm.isRunStarted;
                seed = dm.DestinySeed;
                stages = dm.sortedStages == null ? 0 : dm.sortedStages.Count;
                raceId = dm.raceId;
            }
            catch { return; }

            if (!inited)
            {
                inited = true;
                lastStarted = started;
                lastSeed = seed;
                lastStages = stages;
                lastRaceId = raceId;
                return;
            }

            bool changed = started != lastStarted || seed != lastSeed;
            if (changed)
            {
                // 新局 = 从"未开始/旧种子"进入开始，或种子变了
                bool isStart = started && (!lastStarted || seed != lastSeed);
                string state = started ? (isStart ? "start" : "continue") : "end";
                Emit(state, seed, stages, lastFloor, raceId, null);
                if (isStart)
                {
                    Combat.Reset();
                }
                lastStarted = started;
                lastSeed = seed;
            }
            if (stages != lastStages) lastStages = stages;
            if (raceId != lastRaceId) lastRaceId = raceId;
            lastFloor = SafeFloor();
        }

        private static string SafeFloor()
        {
            try { return Events.LastFloor ?? ""; }
            catch { return ""; }
        }

        private static void Emit(string state, int seed, int stages, string floor, int raceId, string note)
        {
            Dictionary<string, object> e = Json.Obj();
            e["kind"] = "run";
            e["state"] = state;
            e["seed"] = seed;
            e["stages"] = stages;
            e["raceId"] = raceId;
            e["floor"] = floor;
            if (note != null) e["note"] = note;
            Events.Add(e);
            MonitorPlugin.LogInfo("[RunTracker] " + state + " seed=" + seed + " stages=" + stages + " race=" + raceId + " floor=" + floor);
        }

        /// <summary>给 /run 用：当前对局的紧凑快照。</summary>
        public static Dictionary<string, object> Current()
        {
            Dictionary<string, object> o = Json.Obj();
            DungeonManager dm = Dungeon;
            if (dm == null)
            {
                o["inRun"] = false;
                o["reason"] = "DungeonManager not present";
                return o;
            }
            try
            {
                o["inRun"] = dm.isRunStarted;
                o["seed"] = dm.DestinySeed;
                o["raceId"] = dm.raceId;
                o["customGameKey"] = dm.customGameKey;
                o["stages"] = dm.sortedStages == null ? 0 : dm.sortedStages.Count;
                o["stageNames"] = dm.sortedStages == null ? (object)new List<object>() : new List<string>(dm.sortedStages);
                o["floors"] = dm.generatedFloors == null ? 0 : dm.generatedFloors.Count;
                o["players"] = dm.runStartPlayerCount;
                o["timeInWorld"] = Math.Round(dm.timeInWorld, 1);
                o["sessionSerial"] = dm.sessionSerial;
                o["isSavedSession"] = dm.isSavedSession;
                o["floor"] = SafeFloor();
                o["combat"] = Json.Obj()
                    .Add("rows", Combat.Rows)
                    .Add("probesSkipped", Events.ProbeCount);
            }
            catch (Exception ex)
            {
                o["error"] = ex.GetType().Name + ":" + ex.Message;
            }
            return o;
        }
    }
}
