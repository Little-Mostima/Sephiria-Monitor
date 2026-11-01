using System;
using System.IO;
using UnityEngine;

namespace SephiriaMonitor
{
    /// <summary>Screenshot on demand (still available as a fallback; the JSON state API is primary).</summary>
    public static class ScreenshotService
    {
        private static int counter;

        public static string Capture(int maxWidth)
        {
            if (!MainThreadPump.IsMainThread)
            {
                string result = null;
                MainThreadPump.Run(delegate { result = CaptureMainThread(maxWidth); return "ok"; }, 5000);
                return result;
            }
            return CaptureMainThread(maxWidth);
        }

        private static string CaptureMainThread(int maxWidth)
        {
            try
            {
                string dir = Path.Combine(Session.Root ?? ".", "snapshots");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "shot-" + DateTime.Now.ToString("HHmmss") + "-" + (++counter) + ".png");
                ScreenCapture.CaptureScreenshot(path, 1);
                return path;
            }
            catch (Exception ex)
            {
                MonitorPlugin.LogError("screenshot: " + ex.Message);
                return null;
            }
        }
    }
}
