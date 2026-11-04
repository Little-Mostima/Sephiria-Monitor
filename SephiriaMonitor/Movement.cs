using System;
using System.Collections.Generic;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>
    /// Per-frame motion tracking for the local player.
    /// Polling /player from outside a ~1 Hz loop cannot see a dash: measured over 90 s of boss
    /// fight the external sampler produced a 0.87 s mean interval and had to average 8.5 u/s
    /// spikes away. So the plugin integrates the real per-frame displacement here and keeps a
    /// 10 Hz path ring that clients pull with /path.
    /// </summary>
    public static class Movement
    {
        private const int PathCapacity = 600;       // 10 Hz * 1 minute（够回溯，别常驻 1800 个字典）
        private const float SampleInterval = 0.1f;
        private const float TeleportDistance = 12f; // one frame jumping this far is a teleport, not walking

        private static readonly List<Dictionary<string, object>> Path = new List<Dictionary<string, object>>(PathCapacity);
        private static readonly object Gate = new object();

        private static PlayerAvatar tracked;
        private static float nextLookup;
        private static float nextSample;
        private static float lastT = -1f;
        private static Vector3 lastPos;
        private static Vector2 lastVel;
        private static bool hasLast;
        private static float speed;
        private static float maxSpeed;
        private static double walked;
        private static double floorWalked;
        private static int teleports;
        private static int samples;
        private static int stalls;

        public static int PathCount { get { lock (Gate) { return Path.Count; } } }
        public static int Teleports { get { lock (Gate) { return teleports; } } }

        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (tracked == null && now >= nextLookup)
            {
                nextLookup = now + 1f;
                List<UnityEngine.Object> players = StateApi.Instances(typeof(PlayerAvatar));
                tracked = players.Count > 0 ? players[0] as PlayerAvatar : null;
                hasLast = false;
                if (tracked == null)
                {
                    return;
                }
            }
            if (tracked == null)
            {
                return;
            }

            Vector3 pos = tracked.transform.position;
            if (!hasLast)
            {
                lastPos = pos;
                lastT = now;
                hasLast = true;
                return;
            }

            float dt = now - lastT;
            if (dt <= 0.0001f)
            {
                return;
            }
            Vector3 delta = pos - lastPos;
            float dist = new Vector2(delta.x, delta.y).magnitude;
            float inst = dist / dt;

            if (dist > TeleportDistance)
            {
                lock (Gate) { teleports++; }
                speed = 0f;
                lastVel = Vector2.zero;
            }
            else
            {
                lock (Gate)
                {
                    walked += dist;
                    floorWalked += dist;
                }
                // A long frame (loading, GC, alt-tab) turns normal walking into a fake dash; keep
                // the distance but do not let that frame set the speed or the session maximum.
                if (dt <= 0.1f)
                {
                    speed = speed * 0.7f + inst * 0.3f;
                    if (speed > maxSpeed)
                    {
                        maxSpeed = speed;
                    }
                    lastVel = new Vector2(delta.x / dt, delta.y / dt);
                }
                else
                {
                    lock (Gate) { stalls++; }
                }
            }

            lastPos = pos;
            lastT = now;

            if (now >= nextSample)
            {
                nextSample = now + SampleInterval;
                Dictionary<string, object> p = Json.Obj();
                p["t"] = Math.Round(now, 2);
                p["x"] = Math.Round(pos.x, 2);
                p["y"] = Math.Round(pos.y, 2);
                p["s"] = Math.Round(inst, 2);
                lock (Gate)
                {
                    Path.Add(p);
                    samples++;
                    if (Path.Count > PathCapacity)
                    {
                        Path.RemoveRange(0, Path.Count - PathCapacity);
                    }
                }
            }
        }

        /// <summary>Called on floor change: distance is tracked per floor as well as per session.</summary>
        public static void ResetFloor()
        {
            lock (Gate) { floorWalked = 0; }
        }

        public static Dictionary<string, object> Summary()
        {
            lock (Gate)
            {
                Dictionary<string, object> d = Json.Obj();
                d["tracked"] = tracked != null;
                d["speed"] = Math.Round(speed, 2);
                d["maxSpeed"] = Math.Round(maxSpeed, 2);
                d["vel"] = new List<object> { Math.Round(lastVel.x, 2), Math.Round(lastVel.y, 2) };
                d["walked"] = Math.Round(walked, 1);
                d["floorWalked"] = Math.Round(floorWalked, 1);


                d["pathPoints"] = Path.Count;
                return d;
            }
        }

        /// <summary>Newest <paramref name="n"/> path points, oldest first.</summary>
        public static string PathJson(int n)
        {
            List<object> list = new List<object>();
            lock (Gate)
            {
                for (int i = Path.Count - 1; i >= 0 && list.Count < n; i--)
                {
                    list.Add(Path[i]);
                }
            }
            list.Reverse();
            return Json.Write(Json.Obj().Add("points", list.Count).Add("samples", samples)
                .Add("t", Math.Round(Time.realtimeSinceStartup, 2)).Add("path", list));
        }
    }
}
