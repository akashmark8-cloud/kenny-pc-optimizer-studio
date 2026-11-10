using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace KennyPCOptimizer
{
    public sealed class WebServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _token;
        private readonly string _exePath;
        private readonly Action _onShutdown;
        private volatile bool _running;
        private Thread _acceptThread;

        public WebServer(int port, string sessionToken, string exePath, Action onShutdown)
        {
            _token = sessionToken ?? "";
            _exePath = exePath;
            _onShutdown = onShutdown;
            _listener = new HttpListener();
            _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", port));
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Start();
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(delegate { HandleContext(ctx); });
                }
                catch
                {
                    if (!_running) break;
                }
            }
        }

        private static byte[] ReadResourceBytes(string resourceName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) return null;
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        private static void WriteBytes(HttpListenerResponse resp, int status, string contentType, byte[] data)
        {
            resp.StatusCode = status;
            resp.ContentType = contentType;
            resp.ContentLength64 = data.Length;
            using (Stream outStream = resp.OutputStream)
            {
                outStream.Write(data, 0, data.Length);
            }
        }

        private static void WriteJson(HttpListenerResponse resp, int status, object payload)
        {
            string json = SystemInfo.CreateSerializer().Serialize(payload);
            WriteBytes(resp, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        private static Dictionary<string, object> ReadJsonBody(HttpListenerRequest req)
        {
            if (!req.HasEntityBody) return new Dictionary<string, object>();
            try
            {
                using (StreamReader sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                {
                    string raw = sr.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, object>();
                    Dictionary<string, object> parsed = SystemInfo.CreateSerializer().Deserialize<Dictionary<string, object>>(raw);
                    return parsed ?? new Dictionary<string, object>();
                }
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private static bool GetBool(Dictionary<string, object> dict, string key, bool fallback)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToBoolean(v); } catch { }
            }
            return fallback;
        }

        private static string GetStr(Dictionary<string, object> dict, string key, string fallback)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v != null) return v.ToString();
            return fallback;
        }

        private void HandleContext(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse resp = ctx.Response;
            try
            {
                string path = (req.Url.AbsolutePath ?? "/").TrimEnd('/');
                if (string.IsNullOrEmpty(path)) path = "/";
                string method = req.HttpMethod.ToUpperInvariant();

                // Static embedded UI & icon routes
                if (method == "GET" && path == "/")
                {
                    byte[] html = ReadResourceBytes("ui.index.html");
                    if (html != null) WriteBytes(resp, 200, "text/html; charset=utf-8", html);
                    else WriteBytes(resp, 404, "text/plain", Encoding.UTF8.GetBytes("Missing embedded index.html"));
                    return;
                }
                if (method == "GET" && path == "/favicon.ico")
                {
                    byte[] ico = ReadResourceBytes("assets.app_icon.ico");
                    if (ico != null) WriteBytes(resp, 200, "image/x-icon", ico);
                    else { resp.StatusCode = 204; resp.Close(); }
                    return;
                }
                if (method == "GET" && path == "/static/styles.css")
                {
                    byte[] css = ReadResourceBytes("ui.styles.css");
                    if (css != null) WriteBytes(resp, 200, "text/css; charset=utf-8", css);
                    else WriteBytes(resp, 404, "text/plain", new byte[0]);
                    return;
                }
                if (method == "GET" && path == "/static/app.js")
                {
                    byte[] jsBytes = ReadResourceBytes("ui.app.js");
                    if (jsBytes != null) WriteBytes(resp, 200, "application/javascript; charset=utf-8", jsBytes);
                    else WriteBytes(resp, 404, "text/plain", new byte[0]);
                    return;
                }

                // Health endpoint open
                if (method == "GET" && path == "/api/health")
                {
                    WriteJson(resp, 200, new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "app", Config.AppName },
                        { "version", Config.AppVersion },
                        { "is_admin", SystemInfo.IsAdmin() }
                    });
                    return;
                }

                // Token guard for /api/*
                if (path.StartsWith("/api/") && !string.IsNullOrEmpty(_token))
                {
                    string provided = req.Headers["X-Session-Token"] ?? req.QueryString["token"];
                    if (!string.Equals(provided, _token, StringComparison.Ordinal))
                    {
                        WriteJson(resp, 401, new Dictionary<string, object> { { "ok", false }, { "error", "Unauthorized session token" } });
                        return;
                    }
                }

                // API Routes
                if (method == "GET" && path == "/api/telemetry")
                {
                    Dictionary<string, object> res = SystemInfo.GetTelemetry();
                    res["ok"] = true;
                    WriteJson(resp, 200, res);
                    return;
                }

                if (path == "/api/settings")
                {
                    if (method == "GET")
                    {
                        WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "settings", SystemInfo.LoadSettings() } });
                        return;
                    }
                    if (method == "POST")
                    {
                        Dictionary<string, object> body = ReadJsonBody(req);
                        HashSet<string> allowed = new HashSet<string>
                        {
                            "backup_dir", "auto_backup_before_clean", "create_restore_point_on_backup",
                            "archive_browser_history_before_clean", "sound_enabled",
                            "ram_auto_threshold_enabled", "ram_auto_threshold_percent",
                            "ram_auto_interval_enabled", "ram_auto_interval_minutes",
                            "ram_auto_areas", "ram_tray_notifications"
                        };
                        Dictionary<string, object> filtered = new Dictionary<string, object>();
                        foreach (KeyValuePair<string, object> kv in body)
                        {
                            if (allowed.Contains(kv.Key)) filtered[kv.Key] = kv.Value;
                        }
                        if (filtered.ContainsKey("backup_dir"))
                        {
                            try { Directory.CreateDirectory(filtered["backup_dir"].ToString()); }
                            catch (Exception ex)
                            {
                                WriteJson(resp, 400, new Dictionary<string, object> { { "ok", false }, { "error", "Invalid backup directory: " + ex.Message } });
                                return;
                            }
                        }
                        WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "settings", SystemInfo.SaveSettings(filtered) } });
                        return;
                    }
                }

                if (method == "GET" && path == "/api/memory/status")
                {
                    Dictionary<string, object> memRes = MemoryOptimizer.GetMemoryDetails(true);
                    memRes["ok"] = true;
                    WriteJson(resp, 200, memRes);
                    return;
                }

                if (method == "POST" && path == "/api/memory/clean")
                {
                    Dictionary<string, object> body = ReadJsonBody(req);
                    List<string> areas = new List<string>();
                    if (body.ContainsKey("areas") && body["areas"] is ArrayList)
                    {
                        foreach (object a in (ArrayList)body["areas"]) areas.Add(a.ToString());
                    }
                    bool dryRun = GetBool(body, "dry_run", false);
                    Dictionary<string, object> res = MemoryOptimizer.CleanMemory(areas, dryRun, null);
                    WriteJson(resp, 200, res);
                    return;
                }

                if (method == "POST" && path == "/api/memory/trim_process")
                {
                    Dictionary<string, object> body = ReadJsonBody(req);
                    int pid = 0;
                    if (body.ContainsKey("pid") && body["pid"] != null)
                    {
                        try { pid = Convert.ToInt32(body["pid"]); } catch { }
                    }
                    if (pid <= 0)
                    {
                        WriteJson(resp, 400, new Dictionary<string, object> { { "ok", false }, { "message", "Invalid PID" } });
                        return;
                    }
                    Dictionary<string, object> res = MemoryOptimizer.TrimSingleProcess(pid);
                    WriteJson(resp, Convert.ToBoolean(res["ok"]) ? 200 : 400, res);
                    return;
                }

                if (method == "POST" && path == "/api/advisor/scan")
                {
                    Dictionary<string, object> body = ReadJsonBody(req);
                    bool fastOnly = GetBool(body, "offline", false);
                    string jid = JobManager.StartJob("Drivers, App Updates & Essentials Scan", delegate(LogCallback logCb, StageCallback stageCb)
                    {
                        stageCb("Inspecting Hardware, BIOS & Installed Drivers...", 25);
                        logCb("Scanning PnP signed drivers, BIOS, CPU, GPU, and disk health...", "info");
                        Dictionary<string, object> res = SystemAdvisor.ScanDriversAndApps(!fastOnly, !fastOnly);
                        stageCb("Scan Complete", 100);
                        logCb(string.Format("Found {0} key driver(s) and {1} app update(s).",
                            res["driver_count"], res["app_updates_count"]), "success");
                        return res;
                    });
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "job_id", jid } });
                    return;
                }

                if (method == "POST" && path == "/api/advisor/winget")
                {
                    Dictionary<string, object> body = ReadJsonBody(req);
                    string action = GetStr(body, "action", "upgrade");
                    List<string> ids = new List<string>();
                    if (body.ContainsKey("package_ids") && body["package_ids"] is ArrayList)
                    {
                        foreach (object o in (ArrayList)body["package_ids"]) ids.Add(o.ToString());
                    }
                    else
                    {
                        string single = GetStr(body, "package_id", "");
                        if (!string.IsNullOrEmpty(single)) ids.Add(single);
                    }
                    string jid = JobManager.StartJob("Winget Package " + (action == "install" ? "Install" : "Update"), delegate(LogCallback logCb, StageCallback stageCb)
                    {
                        int done = 0;
                        int okCount = 0;
                        foreach (string pid in ids)
                        {
                            int pct = ids.Count > 0 ? (20 + (done * 75 / ids.Count)) : 50;
                            stageCb(string.Format("Processing {0}...", pid), pct);
                            Dictionary<string, object> r = SystemAdvisor.RunWingetPackageAction(action, pid, logCb);
                            if (Convert.ToBoolean(r["ok"])) okCount++;
                            done++;
                        }
                        stageCb("Completed", 100);
                        return new Dictionary<string, object> { { "total", ids.Count }, { "succeeded", okCount } };
                    });
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "job_id", jid } });
                    return;
                }

                if (method == "POST" && path == "/api/advisor/open_tool")
                {
                    string tool = GetStr(ReadJsonBody(req), "tool", "windowsupdate");
                    try
                    {
                        if (tool == "devmgmt") System.Diagnostics.Process.Start("devmgmt.msc");
                        else System.Diagnostics.Process.Start("ms-settings:windowsupdate");
                        WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true } });
                    }
                    catch (Exception ex)
                    {
                        WriteJson(resp, 400, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });
                    }
                    return;
                }

                if (method == "POST" && path == "/api/system/elevate")
                {
                    if (SystemInfo.IsAdmin())
                    {
                        WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "already_admin", true }, { "message", "Already running as Administrator." } });
                        return;
                    }
                    bool ok = SystemInfo.TriggerUacElevation(_exePath);
                    if (ok)
                    {
                        WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "already_admin", false }, { "message", "UAC prompt launched. Switching to elevated instance..." } });
                        ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(700); if (_onShutdown != null) _onShutdown(); });
                        return;
                    }
                    WriteJson(resp, 400, new Dictionary<string, object> { { "ok", false }, { "message", "Elevation was cancelled or declined." } });
                    return;
                }

                if (method == "POST" && path == "/api/scan/all")
                {
                    string jid = JobManager.StartJob("Full System Deep Scan", delegate(LogCallback logCb, StageCallback stageCb)
                    {
                        stageCb("Scanning System, Temp, Shader & Game Caches...", 15);
                        logCb("Scanning System, Temp, NVIDIA DXCache, Dev & Game directories...", "info");
                        Dictionary<string, object> sysRes = SystemCleaner.ScanSystemTargets();
                        logCb(string.Format("System scan complete: {0} item(s) ({1}).", sysRes["total_files"], sysRes["total_human"]), "success");

                        stageCb("Discovering & Scanning All Browsers...", 50);
                        logCb("Scanning installed & leftover browsers across Windows profiles...", "info");
                        Dictionary<string, object> brRes = BrowserCleaner.ScanBrowsers();
                        logCb(string.Format("Browser scan complete: {0} browser(s), {1} file(s) ({2}).", brRes["browser_count"], brRes["total_files"], brRes["total_human"]), "success");

                        stageCb("Scanning Registry for Empty Folders...", 80);
                        logCb("Scanning HKCU\\Software & HKLM\\SOFTWARE for empty registry keys...", "info");
                        Dictionary<string, object> regRes = RegistryCleaner.ScanEmptyRegistryKeys();
                        logCb(string.Format("Registry scan complete: {0} empty key(s) found.", regRes["total_count"]), "success");

                        long totalBytes = Convert.ToInt64(sysRes["total_bytes"]) + Convert.ToInt64(brRes["total_bytes"]);
                        stageCb("Scan Complete", 100);
                        return new Dictionary<string, object>
                        {
                            { "system", sysRes },
                            { "browsers", brRes },
                            { "registry", regRes },
                            { "grand_total_bytes", totalBytes },
                            { "grand_total_human", SystemInfo.FormatBytes(totalBytes) }
                        };
                    });
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "job_id", jid } });
                    return;
                }

                if (method == "POST" && path == "/api/scan/system")
                {
                    Dictionary<string, object> res = SystemCleaner.ScanSystemTargets();
                    res["ok"] = true;
                    WriteJson(resp, 200, res);
                    return;
                }

                if (method == "POST" && path == "/api/scan/browsers")
                {
                    Dictionary<string, object> res = BrowserCleaner.ScanBrowsers();
                    res["ok"] = true;
                    WriteJson(resp, 200, res);
                    return;
                }

                if (method == "POST" && path == "/api/scan/registry")
                {
                    Dictionary<string, object> res = RegistryCleaner.ScanEmptyRegistryKeys();
                    res["ok"] = true;
                    WriteJson(resp, 200, res);
                    return;
                }

                if (method == "POST" && path == "/api/browsers/close")
                {
                    Dictionary<string, object> body = ReadJsonBody(req);
                    List<int> pids = null;
                    if (body.ContainsKey("pids") && body["pids"] is ArrayList)
                    {
                        pids = new List<int>();
                        foreach (object p in (ArrayList)body["pids"]) pids.Add(Convert.ToInt32(p));
                    }
                    Dictionary<string, object> res = BrowserCleaner.CloseRunningBrowsers(pids);
                    res["ok"] = true;
                    WriteJson(resp, 200, res);
                    return;
                }

                if (method == "POST" && path == "/api/clean/execute")
                {
                    Dictionary<string, object> payload = ReadJsonBody(req);
                    List<string> systemIds = new List<string>();
                    if (payload.ContainsKey("system_ids") && payload["system_ids"] is ArrayList)
                    {
                        foreach (object o in (ArrayList)payload["system_ids"]) systemIds.Add(o.ToString());
                    }

                    Dictionary<string, List<string>> browserSel = new Dictionary<string, List<string>>();
                    if (payload.ContainsKey("browser_selections") && payload["browser_selections"] is Dictionary<string, object>)
                    {
                        foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)payload["browser_selections"])
                        {
                            List<string> cats = new List<string>();
                            if (kv.Value is ArrayList)
                            {
                                foreach (object c in (ArrayList)kv.Value) cats.Add(c.ToString());
                            }
                            if (cats.Count > 0) browserSel[kv.Key] = cats;
                        }
                    }

                    List<Dictionary<string, object>> regKeys = new List<Dictionary<string, object>>();
                    if (payload.ContainsKey("registry_keys") && payload["registry_keys"] is ArrayList)
                    {
                        foreach (object o in (ArrayList)payload["registry_keys"])
                        {
                            Dictionary<string, object> d = o as Dictionary<string, object>;
                            if (d != null) regKeys.Add(d);
                        }
                    }

                    bool dryRun = GetBool(payload, "dry_run", false);
                    bool includeRp = GetBool(payload, "include_restore_point", true);
                    bool createBackup = GetBool(payload, "create_backup", true) || (regKeys.Count > 0);
                    string reason = GetStr(payload, "reason", "Pre-Clean Safety Backup");

                    string jid = JobManager.StartJob("Safe Cleanup Pipeline", delegate(LogCallback logCb, StageCallback stageCb)
                    {
                        long cBefore = SystemInfo.GetCDriveFreeBytes();
                        Dictionary<string, object> backupManifest = null;
                        Dictionary<string, object> settings = SystemInfo.LoadSettings();

                        if (!dryRun && createBackup)
                        {
                            stageCb("Phase 1/4: Creating Registry & PC Backup...", 15);
                            logCb("Starting Pre-Clean Safety Backup (Registry + PC Snapshot)...", "info");

                            List<Dictionary<string, string>> filesToArchive = new List<Dictionary<string, string>>();
                            bool archiveHist = !settings.ContainsKey("archive_browser_history_before_clean") || Convert.ToBoolean(settings["archive_browser_history_before_clean"]);
                            if (browserSel.Count > 0 && archiveHist)
                            {
                                Dictionary<string, object> brScan = BrowserCleaner.ScanBrowsers();
                                HashSet<string> selectedHistBids = new HashSet<string>();
                                foreach (KeyValuePair<string, List<string>> kv in browserSel)
                                {
                                    if (kv.Value.Contains("history")) selectedHistBids.Add(kv.Key);
                                }
                                List<Dictionary<string, string>> cands = brScan["critical_backup_candidates"] as List<Dictionary<string, string>>;
                                if (cands != null)
                                {
                                    foreach (Dictionary<string, string> cand in cands)
                                    {
                                        foreach (string bid in selectedHistBids)
                                        {
                                            if (cand["category"].Contains("browser_" + bid + "_"))
                                            {
                                                filesToArchive.Add(cand);
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            List<string> surgicalPaths = new List<string>();
                            foreach (Dictionary<string, object> k in regKeys)
                            {
                                if (k.ContainsKey("full_path") && k["full_path"] != null)
                                    surgicalPaths.Add(k["full_path"].ToString());
                            }

                            bool settingRp = !settings.ContainsKey("create_restore_point_on_backup") || Convert.ToBoolean(settings["create_restore_point_on_backup"]);
                            backupManifest = BackupManager.CreateBackupSnapshot(
                                reason,
                                true,
                                surgicalPaths.Count > 0 ? surgicalPaths : null,
                                filesToArchive,
                                includeRp && settingRp,
                                null,
                                logCb
                            );
                        }
                        else if (dryRun)
                        {
                            stageCb("Phase 1/4: Simulation Mode (Backup Preview)...", 15);
                            logCb("[DRY-RUN] Backup gate verified (skipped writing snapshot in simulation mode).", "info");
                        }

                        Dictionary<string, object> sysRes = new Dictionary<string, object> { { "total_freed_bytes", 0L }, { "total_deleted_files", 0 } };
                        if (systemIds.Count > 0)
                        {
                            stageCb("Phase 2/4: Cleaning System, Temp, Shaders & Game Dumps...", 45);
                            sysRes = SystemCleaner.CleanSystemTargets(systemIds, dryRun, null, logCb);
                        }

                        Dictionary<string, object> brRes = new Dictionary<string, object> { { "total_freed_bytes", 0L }, { "total_deleted_files", 0 } };
                        if (browserSel.Count > 0)
                        {
                            stageCb("Phase 3/4: Cleaning Browser History, Caches & Leftovers...", 70);
                            brRes = BrowserCleaner.CleanBrowsers(browserSel, dryRun, null, logCb);
                        }

                        Dictionary<string, object> regRes = new Dictionary<string, object> { { "deleted_count", 0 } };
                        if (regKeys.Count > 0)
                        {
                            stageCb("Phase 4/4: Cleaning Empty Registry Folders...", 90);
                            regRes = RegistryCleaner.CleanEmptyRegistryKeys(regKeys, dryRun, false, logCb);
                        }

                        long cAfter = SystemInfo.GetCDriveFreeBytes();
                        long cDelta = (!dryRun && cAfter > cBefore) ? (cAfter - cBefore) : 0L;
                        long totalFreed = Convert.ToInt64(sysRes["total_freed_bytes"]) + Convert.ToInt64(brRes["total_freed_bytes"]);
                        int totalFiles = Convert.ToInt32(sysRes["total_deleted_files"]) + Convert.ToInt32(brRes["total_deleted_files"]);
                        int totalReg = Convert.ToInt32(regRes["deleted_count"]);

                        Dictionary<string, object> stats = SystemInfo.RecordCleanedStats(totalFreed, totalFiles, totalReg, reason, dryRun);
                        stageCb("Cleanup & Verification Complete", 100);
                        logCb(string.Format("{0}Finished! Space freed: {1} | Files: {2} | Empty Registry Keys: {3}",
                            dryRun ? "[DRY-RUN] " : "", SystemInfo.FormatBytes(totalFreed), totalFiles, totalReg), "success");

                        return new Dictionary<string, object>
                        {
                            { "dry_run", dryRun },
                            { "backup", backupManifest },
                            { "system", sysRes },
                            { "browsers", brRes },
                            { "registry", regRes },
                            { "total_freed_bytes", totalFreed },
                            { "total_freed_human", SystemInfo.FormatBytes(totalFreed) },
                            { "total_deleted_files", totalFiles },
                            { "total_registry_cleaned", totalReg },
                            { "c_drive_free_after_human", SystemInfo.FormatBytes(cAfter) },
                            { "c_drive_delta_human", SystemInfo.FormatBytes(cDelta) },
                            { "stats", stats }
                        };
                    });
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "job_id", jid } });
                    return;
                }

                if (method == "GET" && path == "/api/backups")
                {
                    WriteJson(resp, 200, new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "backup_root", BackupManager.GetBackupRoot() },
                        { "snapshots", BackupManager.ListBackups(null) },
                        { "windows_restore", BackupManager.ListWindowsRestorePoints() }
                    });
                    return;
                }

                if (method == "POST" && path == "/api/backups/create")
                {
                    Dictionary<string, object> data = ReadJsonBody(req);
                    string reason = GetStr(data, "reason", "Manual Full Checkpoint");
                    bool incReg = GetBool(data, "include_registry", true);
                    bool incRp = GetBool(data, "include_restore_point", true);
                    bool incBr = GetBool(data, "include_browser_dbs", true);

                    string jid = JobManager.StartJob("Full Registry & PC Backup", delegate(LogCallback logCb, StageCallback stageCb)
                    {
                        stageCb("Collecting critical system & browser state...", 20);
                        List<Dictionary<string, string>> files = new List<Dictionary<string, string>>();
                        if (incBr)
                        {
                            Dictionary<string, object> br = BrowserCleaner.ScanBrowsers();
                            List<Dictionary<string, string>> cands = br["critical_backup_candidates"] as List<Dictionary<string, string>>;
                            if (cands != null) files = cands;
                        }
                        stageCb("Creating Registry & PC Snapshot Backup...", 55);
                        Dictionary<string, object> manifest = BackupManager.CreateBackupSnapshot(reason, incReg, null, files, incRp, null, logCb);
                        stageCb("Backup Checkpoint Complete", 100);
                        return new Dictionary<string, object> { { "snapshot", manifest } };
                    });
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "job_id", jid } });
                    return;
                }

                if (method == "POST" && path == "/api/backups/open_root")
                {
                    bool ok = BackupManager.OpenBackupFolder(null, null);
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", ok }, { "path", BackupManager.GetBackupRoot() } });
                    return;
                }

                if (method == "POST" && path == "/api/backups/launch_rstrui")
                {
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", BackupManager.LaunchWindowsSystemRestore() } });
                    return;
                }

                if (path.StartsWith("/api/backups/"))
                {
                    string[] parts = path.Substring("/api/backups/".Length).Split('/');
                    string snapId = Uri.UnescapeDataString(parts[0]);
                    try
                    {
                        if (method == "POST" && parts.Length == 2 && parts[1] == "verify")
                        {
                            Dictionary<string, object> v = BackupManager.VerifyBackup(snapId, null);
                            v["ok"] = true;
                            WriteJson(resp, 200, v);
                            return;
                        }
                        if (method == "POST" && parts.Length == 3 && parts[1] == "restore" && parts[2] == "registry")
                        {
                            string mode = GetStr(ReadJsonBody(req), "mode", "quick");
                            Dictionary<string, object> r = BackupManager.RestoreRegistryFromBackup(snapId, mode, null);
                            WriteJson(resp, Convert.ToBoolean(r["ok"]) ? 200 : 400, r);
                            return;
                        }
                        if (method == "POST" && parts.Length == 3 && parts[1] == "restore" && parts[2] == "files")
                        {
                            Dictionary<string, object> r = BackupManager.RestoreFilesFromBackup(snapId, null);
                            WriteJson(resp, Convert.ToBoolean(r["ok"]) ? 200 : 400, r);
                            return;
                        }
                        if (method == "POST" && parts.Length == 2 && parts[1] == "open")
                        {
                            WriteJson(resp, 200, new Dictionary<string, object> { { "ok", BackupManager.OpenBackupFolder(snapId, null) } });
                            return;
                        }
                        if (method == "DELETE" && parts.Length == 1)
                        {
                            WriteJson(resp, 200, BackupManager.DeleteBackup(snapId, null));
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteJson(resp, 400, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message }, { "message", ex.Message } });
                        return;
                    }
                }

                if (method == "GET" && path.StartsWith("/api/jobs/"))
                {
                    string jobId = path.Substring("/api/jobs/".Length);
                    Dictionary<string, object> job = JobManager.GetJob(jobId);
                    if (job == null)
                    {
                        WriteJson(resp, 404, new Dictionary<string, object> { { "ok", false }, { "error", "Job not found" } });
                        return;
                    }
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true }, { "job", job } });
                    return;
                }

                if (method == "POST" && path == "/api/shutdown")
                {
                    WriteJson(resp, 200, new Dictionary<string, object> { { "ok", true } });
                    ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(300); if (_onShutdown != null) _onShutdown(); });
                    return;
                }

                WriteJson(resp, 404, new Dictionary<string, object> { { "ok", false }, { "error", "Not found" } });
            }
            catch (Exception ex)
            {
                try { WriteJson(resp, 500, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } }); } catch { }
            }
        }
    }
}
