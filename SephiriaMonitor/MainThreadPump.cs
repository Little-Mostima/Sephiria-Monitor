using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SephiriaMonitor
{
    /// <summary>Unity APIs must run on the main thread: HTTP workers hand work over and wait.</summary>
    public static class MainThreadPump
    {
        public static int MainThreadId = -1;

        private class Job
        {
            public Func<string> Work;
            public string Result;
            public Exception Error;
            public volatile bool Cancelled;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly ConcurrentQueue<Job> Queue = new ConcurrentQueue<Job>();
        public static int Pending => Queue.Count;

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        public static string Run(Func<string> work, int timeoutMs)
        {
            if (IsMainThread)
            {
                try { return work(); }
                catch (Exception ex) { return Error(ex); }
            }
            Job job = new Job { Work = work };
            Queue.Enqueue(job);
            bool finished = false;
            try
            {
                finished = job.Done.Wait(timeoutMs);
                if (!finished)
                {
                    // The main thread is stuck: never run this work later, it would start
                    // a request (e.g. /enter) minutes after its caller gave up.
                    job.Cancelled = true;
                    return "{\"error\":\"main thread timeout\",\"pending\":" + Queue.Count + "}";
                }
                if (job.Error != null)
                {
                    return Error(job.Error);
                }
                return job.Result;
            }
            finally
            {
                if (finished)
                {
                    job.Done.Dispose();
                }
            }
        }

        private static string Error(Exception ex)
        {
            return Json.Write(Json.Obj().Add("error", ex.GetType().Name).Add("message", ex.Message));
        }

        /// <summary>Called from the host MonoBehaviour's Update.</summary>
        public static void Drain()
        {
            int budget = 8;
            Job job;
            while (budget-- > 0 && Queue.TryDequeue(out job))
            {
                if (job.Cancelled)
                {
                    try { job.Done.Dispose(); } catch { }
                    continue;
                }
                try { job.Result = job.Work(); }
                catch (Exception ex) { job.Error = ex; }
                finally
                {
                    try { job.Done.Set(); } catch { }
                }
            }
        }
    }
}
