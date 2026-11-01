using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SephiriaMonitor
{
    /// <summary>Session output: one folder per game run, JSONL events + CSV tables + exports.</summary>
    public static class Session
    {
        private static readonly object Gate = new object();
        private static StreamWriter eventWriter;
        private static StreamWriter logWriter;
        private static string root;
        private static long eventSeq;
        private static int linesSinceFlush;
        private static int dropped;

        public static string Root => root;
        public static int EventCount => (int)eventSeq;
        public static int Dropped => dropped;

        public static void Open(string baseDir)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                root = Path.Combine(baseDir, "out", "session-" + stamp);
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "csv"));
                Directory.CreateDirectory(Path.Combine(root, "db"));
                Directory.CreateDirectory(Path.Combine(root, "snapshots"));
                eventWriter = NewWriter(Path.Combine(root, "events.jsonl"));
                logWriter = NewWriter(Path.Combine(root, "logs.log"));
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("session open failed: " + ex);
            }
        }

        private static StreamWriter NewWriter(string path)
        {
            StreamWriter w = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            w.AutoFlush = false;
            return w;
        }

        public static void Close()
        {
            lock (Gate)
            {
                try { eventWriter?.Flush(); eventWriter?.Dispose(); } catch { }
                try { logWriter?.Flush(); logWriter?.Dispose(); } catch { }
                eventWriter = null;
                logWriter = null;
            }
        }

        public static void Flush()
        {
            lock (Gate)
            {
                try { eventWriter?.Flush(); } catch { }
                try { logWriter?.Flush(); } catch { }
                linesSinceFlush = 0;
            }
        }

        /// <summary>Append a structured event.</summary>
        public static void Emit(Dictionary<string, object> data)
        {
            if (eventWriter == null)
            {
                return;
            }
            long seq = Interlocked.Increment(ref eventSeq);
            try
            {
                if (!data.ContainsKey("seq"))
                {
                    data["seq"] = seq;
                }
                if (!data.ContainsKey("t"))
                {
                    data["t"] = Time.realtimeSinceStartup;
                }
                if (!data.ContainsKey("wall"))
                {
                    data["wall"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                }
                if (!data.ContainsKey("scene"))
                {
                    data["scene"] = SceneTracker.Current;
                }
                string line = Json.Write(data);
                lock (Gate)
                {
                    eventWriter.WriteLine(line);
                    linesSinceFlush++;
                    if (linesSinceFlush >= 64)
                    {
                        try { eventWriter.Flush(); } catch { }
                        linesSinceFlush = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref dropped);
                if (dropped <= 5)
                {
                    MonitorPlugin.LogError("emit failed: " + ex.GetType().Name + ":" + ex.Message);
                }
            }
        }

        public static void RawLog(string line)
        {
            if (logWriter == null)
            {
                return;
            }
            try
            {
                lock (Gate)
                {
                    logWriter.WriteLine(line);
                }
            }
            catch
            {
            }
        }

        public static CsvTable Csv(string fileName, params string[] columns)
        {
            return new CsvTable(Path.Combine(root, "csv", fileName), columns);
        }

        public static void WriteText(string relativePath, string content)
        {
            try
            {
                string path = Path.Combine(root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (StreamWriter w = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)))
                {
                    w.Write(content);
                }
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("write " + relativePath + " failed: " + ex.Message);
            }
        }
    }

    /// <summary>Append-only CSV with buffered flush.</summary>
    public class CsvTable
    {
        private readonly StreamWriter writer;
        private readonly StringBuilder buffer = new StringBuilder();
        private readonly object gate = new object();

        public CsvTable(string path, string[] columns)
        {
            try
            {
                // BOM on purpose: Excel and PowerShell both decode a BOM-less UTF-8 CSV as the
                // system code page (GBK here), which turned "再生药水" into "鍐嶇敓鑽水".
                writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true));
                writer.AutoFlush = false;
                writer.WriteLine(string.Join(",", columns));
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("csv open failed: " + path + " " + ex.Message);
            }
        }

        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            string v = value.Replace("\"", "\"\"");
            return needsQuotes ? "\"" + v + "\"" : v;
        }

        public void Row(params object[] cells)
        {
            if (writer == null)
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                object c = cells[i];
                if (c == null)
                {
                    continue;
                }
                if (c is float f)
                {
                    sb.Append(f.ToString("0.###", CultureInfo.InvariantCulture));
                }
                else if (c is double d)
                {
                    sb.Append(d.ToString("0.####", CultureInfo.InvariantCulture));
                }
                else if (c is bool b)
                {
                    sb.Append(b ? "true" : "false");
                }
                else
                {
                    sb.Append(Escape(Convert.ToString(c, CultureInfo.InvariantCulture)));
                }
            }
            lock (gate)
            {
                buffer.Append(sb).Append('\n');
                if (buffer.Length > 8192)
                {
                    Flush();
                }
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                if (writer == null)
                {
                    return;
                }
                try
                {
                    if (buffer.Length > 0)
                    {
                        writer.Write(buffer.ToString());
                        buffer.Clear();
                    }
                    writer.Flush();
                }
                catch
                {
                }
            }
        }

        public void Close()
        {
            lock (gate)
            {
                if (writer == null)
                {
                    return;
                }
                try
                {
                    if (buffer.Length > 0)
                    {
                        writer.Write(buffer.ToString());
                        buffer.Clear();
                    }
                    writer.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    public static class SceneTracker
    {
        public static string Current = "<none>";
        public static string Previous = "<none>";
        public static int ChangeCount;
        public static float LastChangeAt;

        public static void OnSceneChanged(Scene from, Scene to)
        {
            Previous = Current;
            Current = string.IsNullOrEmpty(to.name) ? "<unnamed>" : to.name;
            ChangeCount++;
            LastChangeAt = Time.realtimeSinceStartup;
        }
    }
}
