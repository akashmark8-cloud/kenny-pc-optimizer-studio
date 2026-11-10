using System;
using System.Collections.Generic;
using System.Threading;

namespace KennyPCOptimizer
{
    public delegate void LogCallback(string message, string level);
    public delegate void StageCallback(string phase, int progress);

    public static class JobManager
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Dictionary<string, object>> _jobs = new Dictionary<string, Dictionary<string, object>>();

        public static string StartJob(string name, Func<LogCallback, StageCallback, Dictionary<string, object>> target)
        {
            string jobId = Guid.NewGuid().ToString("N").Substring(0, 12);
            lock (_lock)
            {
                _jobs[jobId] = new Dictionary<string, object>
                {
                    { "job_id", jobId },
                    { "name", name },
                    { "status", "running" },
                    { "phase", "Initializing..." },
                    { "progress", 5 },
                    { "logs", new List<Dictionary<string, object>>() },
                    { "result", null },
                    { "error", null },
                    { "started_at", DateTime.Now.ToString("HH:mm:ss") },
                    { "finished_at", null }
                };
            }

            LogCallback logCb = delegate(string msg, string level)
            {
                lock (_lock)
                {
                    Dictionary<string, object> job;
                    if (_jobs.TryGetValue(jobId, out job))
                    {
                        List<Dictionary<string, object>> logs = (List<Dictionary<string, object>>)job["logs"];
                        logs.Add(new Dictionary<string, object>
                        {
                            { "ts", DateTime.Now.ToString("HH:mm:ss") },
                            { "level", level ?? "info" },
                            { "message", msg ?? "" }
                        });
                        if (logs.Count > 500)
                        {
                            logs.RemoveRange(0, logs.Count - 500);
                        }
                    }
                }
            };

            StageCallback stageCb = delegate(string phase, int progress)
            {
                lock (_lock)
                {
                    Dictionary<string, object> job;
                    if (_jobs.TryGetValue(jobId, out job))
                    {
                        job["phase"] = phase;
                        job["progress"] = Math.Max(0, Math.Min(100, progress));
                    }
                }
            };

            Thread t = new Thread(delegate()
            {
                try
                {
                    Dictionary<string, object> res = target(logCb, stageCb);
                    lock (_lock)
                    {
                        Dictionary<string, object> job = _jobs[jobId];
                        job["status"] = "completed";
                        job["progress"] = 100;
                        job["phase"] = "Completed";
                        job["result"] = res;
                        job["finished_at"] = DateTime.Now.ToString("HH:mm:ss");
                    }
                }
                catch (Exception ex)
                {
                    logCb("Job failed: " + ex.Message, "error");
                    lock (_lock)
                    {
                        Dictionary<string, object> job = _jobs[jobId];
                        job["status"] = "failed";
                        job["phase"] = "Failed";
                        job["error"] = ex.Message;
                        job["finished_at"] = DateTime.Now.ToString("HH:mm:ss");
                    }
                }
            });
            t.IsBackground = true;
            t.Start();
            return jobId;
        }

        public static Dictionary<string, object> GetJob(string jobId)
        {
            lock (_lock)
            {
                Dictionary<string, object> job;
                if (!_jobs.TryGetValue(jobId, out job)) return null;
                Dictionary<string, object> copy = new Dictionary<string, object>(job);
                copy["logs"] = new List<Dictionary<string, object>>((List<Dictionary<string, object>>)job["logs"]);
                return copy;
            }
        }
    }
}
