using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>In-process Unity log capture (discord/NamedPipe output never passes through it).</summary>
    public static class LogRing
    {
        private class Entry
        {
            public long Seq;
            public string Type;
            public string Message;
            public string Stack;
            public float T;
            public string Scene;
        }

        private const int Capacity = 3000;
        private static readonly List<Entry> Ring = new List<Entry>(Capacity);
        private static readonly object Gate = new object();
        private static long seq;
        private static int errors;
        private static int warnings;
        private static int noise;
        private static int total;

        public static int Count { get { lock (Gate) { return (int)seq; } } }
        public static int Errors { get { lock (Gate) { return errors; } } }
        public static int Warnings { get { lock (Gate) { return warnings; } } }
        public static int Noise { get { lock (Gate) { return noise; } } }
        public static int Total { get { lock (Gate) { return total; } } }

        public static bool IsNoise(string msg)
        {
            if (string.IsNullOrEmpty(msg))
            {
                return false;
            }
            if (msg.IndexOf("discord-ipc", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (msg.IndexOf("NamedPipe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return msg.StartsWith("[DRP]", StringComparison.Ordinal);
        }

        public static void Add(string message, string stack, LogType type)
        {
            bool isNoise = IsNoise(message);
            Entry e = new Entry
            {
                Type = type.ToString(),
                Message = Trim(message, 4000),
                Stack = string.IsNullOrEmpty(stack) || (type != LogType.Error && type != LogType.Exception && type != LogType.Warning)
                    ? null
                    : Trim(stack, 600),
                T = SafeTime(),
                Scene = SceneTracker.Current
            };
            lock (Gate)
            {
                total++;
                if (isNoise)
                {
                    noise++;
                    return;
                }
                if (type == LogType.Error || type == LogType.Exception)
                {
                    errors++;
                }
                else if (type == LogType.Warning)
                {
                    warnings++;
                }
                e.Seq = ++seq;
                Ring.Add(e);
                // Trim a quarter at a time: a RemoveRange per logged line is O(n) on every message.
                if (Ring.Count > Capacity + Capacity / 4)
                {
                    Ring.RemoveRange(0, Ring.Count - Capacity);
                }
            }
            Session.RawLog("[" + e.Type + "] " + message + (e.Stack != null ? "\n" + e.Stack : ""));
        }

        private static float SafeTime()
        {
            try { return Time.realtimeSinceStartup; }
            catch { return 0f; }
        }

        private static string Trim(string s, int max)
        {
            if (s.Length <= max)
            {
                return s;
            }
            return s.Substring(0, max) + "...";
        }

        public static List<object> Recent(int n)
        {
            List<object> list = new List<object>();
            lock (Gate)
            {
                int start = Math.Max(0, Ring.Count - n);
                for (int i = start; i < Ring.Count; i++)
                {
                    Entry e = Ring[i];
                    list.Add(Json.Obj().Add("seq", e.Seq).Add("type", e.Type).Add("msg", e.Message).Add("stack", e.Stack).Add("t", e.T));
                }
            }
            return list;
        }

        public static List<object> Recent(int n, long after)
        {
            List<object> list = new List<object>();
            lock (Gate)
            {
                int start = Math.Max(0, Ring.Count - n);
                for (int i = start; i < Ring.Count; i++)
                {
                    Entry e = Ring[i];
                    if (e.Seq <= after)
                    {
                        continue;
                    }
                    list.Add(Json.Obj().Add("seq", e.Seq).Add("type", e.Type).Add("msg", e.Message).Add("stack", e.Stack).Add("t", e.T));
                }
            }
            return list;
        }

        public static string Tail(int n)
        {
            return Json.Write(Json.Obj().Add("count", Count).Add("errors", Errors).Add("warnings", Warnings).Add("noise", Noise)
                .Add("lines", Recent(n)));
        }

        /// <summary>Incremental tail: only lines with seq &gt; after (0 = all in window).</summary>
        public static string Tail(int n, long after)
        {
            return Json.Write(Json.Obj().Add("count", Count).Add("after", after).Add("errors", Errors).Add("warnings", Warnings).Add("noise", Noise)
                .Add("lines", Recent(n, after)));
        }
    }
}
