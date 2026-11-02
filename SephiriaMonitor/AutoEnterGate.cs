using System;
using System.Globalization;
using System.IO;
using BepInEx;

namespace SephiriaMonitor
{
    /// <summary>
    /// One-shot arming gate for auto-loading a save slot.
    ///
    /// The player must never be dragged into a save against their will, so auto-enter needs TWO keys:
    ///   1. config [title] AutoEnter = true (master switch, off by default), and
    ///   2. a marker file that an agent creates right before a debug session and that is deleted as soon as it is used.
    ///
    /// The file contains an absolute expiry (unix seconds, or "expires=&lt;unix&gt;"). Anything past that is ignored
    /// and deleted, so a forgotten marker cannot surprise the player days later. It also limits damage to 2 hours
    /// by default: the plugin never writes it on its own.
    /// </summary>
    internal static class AutoEnterGate
    {
        public const int DefaultTtlMinutes = 120;
        public const string FileName = "autoenter.on";

        private static string cachedPath;

        public static string Path
        {
            get
            {
                if (cachedPath != null)
                {
                    return cachedPath;
                }
                try
                {
                    string dir = System.IO.Path.Combine(Paths.PluginPath, "SephiriaMonitor");
                    Directory.CreateDirectory(dir);
                    cachedPath = System.IO.Path.Combine(dir, FileName);
                }
                catch (Exception ex)
                {
                    MonitorPlugin.LogError("AutoEnterGate.path: " + ex.Message);
                    cachedPath = "";
                }
                return cachedPath;
            }
        }

        public static bool Exists()
        {
            try
            {
                return !string.IsNullOrEmpty(Path) && File.Exists(Path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when a marker file exists and has not expired yet. Never throws.</summary>
        public static bool IsArmed()
        {
            string reason;
            return Check(out reason);
        }

        public static bool Check(out string reason)
        {
            reason = "missing";
            try
            {
                string p = Path;
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                {
                    return false;
                }
                string text = (File.ReadAllText(p) ?? "").Trim();
                long expires = ParseExpiry(text);
                long now = NowUnix();
                if (expires <= 0)
                {
                    reason = "no-expiry";
                    Delete();
                    return false;
                }
                if (now > expires)
                {
                    reason = "expired";
                    Delete();
                    return false;
                }
                reason = "ok|leftMin=" + ((expires - now) / 60);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name;
                return false;
            }
        }

        /// <summary>Writes/refreshes the marker. Called by /arm (and by the agent through the shell).</summary>
        /// <summary>Slot requested by the marker (0 = use the configured default).</summary>
        public static int Slot
        {
            get
            {
                try
                {
                    string p = Path;
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) return 0;
                    foreach (string line in File.ReadAllLines(p))
                    {
                        string t = (line ?? "").Trim();
                        if (t.StartsWith("slot=", StringComparison.OrdinalIgnoreCase))
                        {
                            int n;
                            if (int.TryParse(t.Substring(5).Trim(), out n) && n > 0 && n <= 9) return n;
                        }
                    }
                }
                catch { }
                return 0;
            }
        }

        public static string Arm(int minutes)
        {
            return Arm(minutes, 0);
        }

        public static string Arm(int minutes, int slot)
        {
            if (minutes <= 0 || minutes > 1440)
            {
                minutes = DefaultTtlMinutes;
            }
            string p = Path;
            if (string.IsNullOrEmpty(p))
            {
                return "{\"ok\":false,\"error\":\"no plugin path\"}";
            }
            long expires = NowUnix() + minutes * 60L;
            File.WriteAllText(p, "expires=" + expires.ToString(CultureInfo.InvariantCulture) + "\n" + (slot > 0 ? "slot=" + slot + "\n" : "") + "# written by Sephiria Monitor; delete this file to cancel auto-enter\n");
            return "{\"ok\":true,\"file\":\"" + JsonEsc(p) + "\",\"expires\":" + expires + ",\"minutes\":" + minutes + ",\"slot\":" + slot + "}";
        }

        /// <summary>Consumes the marker: call after the auto-enter attempt so it can never fire twice.</summary>
        public static void Delete()
        {
            try
            {
                string p = Path;
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("AutoEnterGate.delete: " + ex.Message);
            }
        }

        private static long ParseExpiry(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                int eq = line.IndexOf('=');
                string value = eq >= 0 ? line.Substring(eq + 1).Trim() : line;
                long parsed;
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return Normalize(parsed);
                }
            }
            return 0;
        }

        /// <summary>Accepts unix seconds, unix milliseconds and unix nanoseconds (people paste all three).</summary>
        private static long Normalize(long v)
        {
            if (v > 1000000000000000L)
            {
                return v / 1000000000L;
            }
            if (v > 100000000000L)
            {
                return v / 1000L;
            }
            return v;
        }

        private static long NowUnix()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static string JsonEsc(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
