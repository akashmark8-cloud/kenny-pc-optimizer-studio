using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace KennyPCOptimizer
{
    public static class SystemCleaner
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHQUERYRBINFO
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string pszRootPath, uint dwFlags);

        private sealed class PathSpec
        {
            public string Path;
            public string Pattern;
            public bool RemoveRoot;
            public PathSpec(string p, string pat = null, bool removeRoot = false)
            {
                Path = p;
                Pattern = pat;
                RemoveRoot = removeRoot;
            }
        }

        private sealed class TargetDef
        {
            public string Id;
            public string Name;
            public string Category;
            public string Description;
            public bool AdminRequired;
            public bool Recommended;
            public string BatRef;
            public string Special;
            public List<PathSpec> Paths = new List<PathSpec>();
        }

        private static void QueryRecycleBin(out int count, out long bytes)
        {
            count = 0;
            bytes = 0;
            try
            {
                SHQUERYRBINFO info = new SHQUERYRBINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(SHQUERYRBINFO));
                if (SHQueryRecycleBinW(null, ref info) == 0)
                {
                    count = (int)Math.Max(0L, info.i64NumItems);
                    bytes = Math.Max(0L, info.i64Size);
                }
            }
            catch { }
        }

        private static bool EmptyRecycleBin(out string msg)
        {
            try
            {
                // SHERB_NOCONFIRMATION (1) | SHERB_NOPROGRESSUI (2) | SHERB_NOSOUND (4)
                int hr = SHEmptyRecycleBinW(IntPtr.Zero, null, 7u);
                msg = hr == 0 ? "Recycle Bin emptied across all drives." : "Recycle Bin already empty or cleared.";
                return true;
            }
            catch (Exception ex)
            {
                msg = ex.Message;
                return false;
            }
        }

        private static bool IsReparsePoint(FileSystemInfo info)
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static void ScanDirStats(string directory, string pattern, int maxSamples, out int fileCount, out long totalBytes, List<string> samples, out bool accessDenied)
        {
            fileCount = 0;
            totalBytes = 0;
            accessDenied = false;

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || Config.IsProtectedPath(directory))
                return;

            if (!string.IsNullOrEmpty(pattern))
            {
                try
                {
                    DirectoryInfo di = new DirectoryInfo(directory);
                    foreach (FileInfo fi in di.GetFiles(pattern))
                    {
                        if (Config.IsProtectedPath(fi.FullName) || IsReparsePoint(fi)) continue;
                        try
                        {
                            fileCount++;
                            totalBytes += fi.Length;
                            if (samples.Count < maxSamples) samples.Add(fi.FullName);
                        }
                        catch { }
                    }
                }
                catch (UnauthorizedAccessException) { accessDenied = true; }
                catch { }
                return;
            }

            Stack<DirectoryInfo> stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(directory));
            while (stack.Count > 0)
            {
                DirectoryInfo cur = stack.Pop();
                if (Config.IsProtectedPath(cur.FullName)) continue;
                try
                {
                    if (IsReparsePoint(cur)) continue;
                    foreach (FileInfo fi in cur.GetFiles())
                    {
                        if (Config.IsProtectedPath(fi.FullName) || IsReparsePoint(fi)) continue;
                        try
                        {
                            fileCount++;
                            totalBytes += fi.Length;
                            if (samples.Count < maxSamples) samples.Add(fi.FullName);
                        }
                        catch (UnauthorizedAccessException) { accessDenied = true; }
                        catch { }
                    }
                    foreach (DirectoryInfo sub in cur.GetDirectories())
                    {
                        if (!Config.IsProtectedPath(sub.FullName) && !IsReparsePoint(sub))
                        {
                            stack.Push(sub);
                        }
                    }
                }
                catch (UnauthorizedAccessException) { accessDenied = true; }
                catch { }
            }
        }

        public static void CleanDirectoryContents(string directory, string pattern, bool dryRun, bool removeRoot, out int deletedFiles, out long freedBytes, out int skippedLocked)
        {
            deletedFiles = 0;
            freedBytes = 0;
            skippedLocked = 0;

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || Config.IsProtectedPath(directory))
                return;

            if (!string.IsNullOrEmpty(pattern))
            {
                try
                {
                    DirectoryInfo di = new DirectoryInfo(directory);
                    foreach (FileInfo fi in di.GetFiles(pattern))
                    {
                        if (Config.IsProtectedPath(fi.FullName) || IsReparsePoint(fi)) continue;
                        try
                        {
                            long sz = fi.Length;
                            if (!dryRun)
                            {
                                if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                                    fi.Attributes &= ~FileAttributes.ReadOnly;
                                fi.Delete();
                            }
                            deletedFiles++;
                            freedBytes += sz;
                        }
                        catch { skippedLocked++; }
                    }
                }
                catch { skippedLocked++; }
                return;
            }

            CleanRecursive(new DirectoryInfo(directory), dryRun, removeRoot, ref deletedFiles, ref freedBytes, ref skippedLocked);
        }

        private static void CleanRecursive(DirectoryInfo dir, bool dryRun, bool deleteSelf, ref int deletedFiles, ref long freedBytes, ref int skippedLocked)
        {
            if (!dir.Exists || Config.IsProtectedPath(dir.FullName) || IsReparsePoint(dir)) return;
            try
            {
                foreach (DirectoryInfo sub in dir.GetDirectories())
                {
                    CleanRecursive(sub, dryRun, true, ref deletedFiles, ref freedBytes, ref skippedLocked);
                }
                foreach (FileInfo fi in dir.GetFiles())
                {
                    if (Config.IsProtectedPath(fi.FullName) || IsReparsePoint(fi)) continue;
                    try
                    {
                        long sz = fi.Length;
                        if (!dryRun)
                        {
                            if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                                fi.Attributes &= ~FileAttributes.ReadOnly;
                            fi.Delete();
                        }
                        deletedFiles++;
                        freedBytes += sz;
                    }
                    catch { skippedLocked++; }
                }
            }
            catch { skippedLocked++; }

            if (deleteSelf && !dryRun)
            {
                try { dir.Delete(false); } catch { }
            }
        }

        private static List<TargetDef> BuildTargets(Dictionary<string, object> overrides)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            string userTemp = Path.GetTempPath().TrimEnd('\\');
            List<string> steamLibs = null;

            if (overrides != null)
            {
                if (overrides.ContainsKey("LOCALAPPDATA")) localApp = overrides["LOCALAPPDATA"].ToString();
                if (overrides.ContainsKey("SystemRoot")) sysRoot = overrides["SystemRoot"].ToString();
                if (overrides.ContainsKey("TEMP")) userTemp = overrides["TEMP"].ToString();
                if (overrides.ContainsKey("STEAM_LIBS")) steamLibs = overrides["STEAM_LIBS"] as List<string>;
            }
            if (steamLibs == null) steamLibs = SystemInfo.GetSteamLibraryPaths();

            List<PathSpec> cs2Targets = new List<PathSpec>();
            foreach (string lib in steamLibs)
            {
                string p = Path.Combine(lib, "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin", "win64");
                if (Directory.Exists(p)) cs2Targets.Add(new PathSpec(p, "*.mdmp"));
            }
            if (cs2Targets.Count == 0)
            {
                cs2Targets.Add(new PathSpec(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64", "*.mdmp"));
            }

            List<TargetDef> list = new List<TargetDef>();
            list.Add(new TargetDef
            {
                Id = "user_temp",
                Name = "User Temp Files",
                Category = "System & Cache",
                Description = "Clears temporary files inside your user AppData\\Local\\Temp folder.",
                AdminRequired = false,
                Recommended = true,
                BatRef = "[1]",
                Paths = { new PathSpec(userTemp) }
            });
            list.Add(new TargetDef
            {
                Id = "system_temp",
                Name = "Windows System Temp",
                Category = "System & Cache",
                Description = "Clears system-wide temporary installer and runtime logs in C:\\Windows\\Temp.",
                AdminRequired = true,
                Recommended = true,
                BatRef = "[2]",
                Paths = { new PathSpec(Path.Combine(sysRoot, "Temp")) }
            });
            list.Add(new TargetDef
            {
                Id = "nvidia_dxcache",
                Name = "NVIDIA DirectX Shader Cache",
                Category = "System & Cache",
                Description = "Clears compiled NVIDIA DXCache shader files (rebuilt automatically when needed).",
                AdminRequired = false,
                Recommended = true,
                BatRef = "[3]",
                Paths = { new PathSpec(Path.Combine(localApp, "NVIDIA", "DXCache")) }
            });
            list.Add(new TargetDef
            {
                Id = "gpu_extra_caches",
                Name = "Extended GPU & DirectX Shader Caches",
                Category = "System & Cache",
                Description = "Optional extra shader caches: Direct3D D3DSCache, NVIDIA GLCache, and AMD DxCache.",
                AdminRequired = false,
                Recommended = false,
                BatRef = "Bonus",
                Paths =
                {
                    new PathSpec(Path.Combine(localApp, "D3DSCache")),
                    new PathSpec(Path.Combine(localApp, "NVIDIA", "GLCache")),
                    new PathSpec(Path.Combine(localApp, "AMD", "DxCache"))
                }
            });
            list.Add(new TargetDef
            {
                Id = "recycle_bin",
                Name = "Empty Recycle Bin",
                Category = "System & Cache",
                Description = "Permanently empties deleted items from Recycle Bin across all fixed drives.",
                AdminRequired = false,
                Recommended = true,
                BatRef = "[4]",
                Special = "recycle_bin"
            });
            list.Add(new TargetDef
            {
                Id = "windows_update",
                Name = "Windows Update Download Cache",
                Category = "System & Cache",
                Description = "Safely pauses wuauserv, clears SoftwareDistribution\\Download, then restarts wuauserv.",
                AdminRequired = true,
                Recommended = true,
                BatRef = "[5]",
                Special = "windows_update",
                Paths = { new PathSpec(Path.Combine(sysRoot, "SoftwareDistribution", "Download")) }
            });
            list.Add(new TargetDef
            {
                Id = "dev_caches",
                Name = "Developer Caches (npm & pip)",
                Category = "Developer & Gaming",
                Description = "Removes cached Node npm packages and Python pip wheel downloads.",
                AdminRequired = false,
                Recommended = false,
                BatRef = "[6]",
                Paths =
                {
                    new PathSpec(Path.Combine(localApp, "npm-cache"), null, true),
                    new PathSpec(Path.Combine(localApp, "pip", "cache"), null, true)
                }
            });

            TargetDef gameT = new TargetDef
            {
                Id = "game_dumps",
                Name = "Game Crash Dumps & Logs (CS2 & PUBG)",
                Category = "Developer & Gaming",
                Description = "Cleans CS2 .mdmp memory dumps across all Steam libraries + PUBG crash reports/logs safely.",
                AdminRequired = false,
                Recommended = true,
                BatRef = "[7]"
            };
            gameT.Paths.AddRange(cs2Targets);
            gameT.Paths.Add(new PathSpec(Path.Combine(localApp, "TslGame", "Saved", "Crashes")));
            gameT.Paths.Add(new PathSpec(Path.Combine(localApp, "TslGame", "Saved", "Logs"), "*.log"));
            list.Add(gameT);

            list.Add(new TargetDef
            {
                Id = "windows_crash_dumps",
                Name = "Windows App Crash Dumps",
                Category = "Developer & Gaming",
                Description = "Removes leftover local application crash .dmp files in AppData\\Local\\CrashDumps.",
                AdminRequired = false,
                Recommended = false,
                BatRef = "Bonus",
                Paths = { new PathSpec(Path.Combine(localApp, "CrashDumps")) }
            });

            return list;
        }

        public static Dictionary<string, object> ScanSystemTargets(Dictionary<string, object> overrides = null)
        {
            List<TargetDef> targets = BuildTargets(overrides);
            bool admin = SystemInfo.IsAdmin();
            List<Dictionary<string, object>> scanned = new List<Dictionary<string, object>>();
            int totalFiles = 0;
            long totalBytes = 0;

            foreach (TargetDef t in targets)
            {
                int cnt = 0;
                long sz = 0;
                bool accessDenied = false;
                List<string> pathsStr = new List<string>();
                List<string> samples = new List<string>();

                if (t.Special == "recycle_bin" && overrides == null)
                {
                    QueryRecycleBin(out cnt, out sz);
                    pathsStr.Add("All Fixed Drives ($Recycle.Bin)");
                }
                else
                {
                    foreach (PathSpec spec in t.Paths)
                    {
                        pathsStr.Add(!string.IsNullOrEmpty(spec.Pattern) ? Path.Combine(spec.Path, spec.Pattern) : spec.Path);
                        int c;
                        long b;
                        bool denied;
                        ScanDirStats(spec.Path, spec.Pattern, 8, out c, out b, samples, out denied);
                        cnt += c;
                        sz += b;
                        if (denied) accessDenied = true;
                    }
                }

                totalFiles += cnt;
                totalBytes += sz;
                string sizeHuman = (accessDenied && cnt == 0) ? "Admin Req" : SystemInfo.FormatBytes(sz);

                scanned.Add(new Dictionary<string, object>
                {
                    { "id", t.Id },
                    { "name", t.Name },
                    { "category", t.Category },
                    { "description", t.Description },
                    { "admin_required", t.AdminRequired },
                    { "accessible", (!t.AdminRequired) || admin },
                    { "access_denied", accessDenied },
                    { "recommended", t.Recommended },
                    { "bat_ref", t.BatRef },
                    { "file_count", cnt },
                    { "size_bytes", sz },
                    { "size_human", sizeHuman },
                    { "paths", pathsStr },
                    { "samples", samples.Count > 6 ? samples.GetRange(0, 6) : samples }
                });
            }

            return new Dictionary<string, object>
            {
                { "targets", scanned },
                { "total_files", totalFiles },
                { "total_bytes", totalBytes },
                { "total_human", SystemInfo.FormatBytes(totalBytes) },
                { "is_admin", admin }
            };
        }

        private static bool ControlService(string name, bool stop, out string msg)
        {
            msg = "";
            try
            {
                using (ServiceController sc = new ServiceController(name))
                {
                    if (stop)
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
                        }
                    }
                    else
                    {
                        if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending)
                        {
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                msg = ex.Message;
                return false;
            }
        }

        public static Dictionary<string, object> CleanSystemTargets(
            List<string> targetIds,
            bool dryRun,
            Dictionary<string, object> overrides = null,
            LogCallback logCb = null)
        {
            LogCallback log = delegate(string m, string l) { if (logCb != null) logCb(m, l); };
            Dictionary<string, TargetDef> map = new Dictionary<string, TargetDef>();
            foreach (TargetDef t in BuildTargets(overrides)) map[t.Id] = t;

            bool admin = SystemInfo.IsAdmin();
            long cBefore = SystemInfo.GetCDriveFreeBytes();
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            long totalFreed = 0;
            int totalFiles = 0;
            int totalLocked = 0;
            string prefix = dryRun ? "[DRY-RUN] " : "";

            foreach (string tid in targetIds)
            {
                TargetDef t;
                if (!map.TryGetValue(tid, out t)) continue;

                if (t.AdminRequired && !admin && overrides == null)
                {
                    string warnMsg = string.Format("Skipped '{0}': Administrator privileges required.", t.Name);
                    log(warnMsg, "warn");
                    results.Add(new Dictionary<string, object>
                    {
                        { "id", tid }, { "name", t.Name }, { "status", "skipped_admin" },
                        { "message", warnMsg }, { "freed_bytes", 0L }, { "deleted_files", 0 }
                    });
                    continue;
                }

                log(string.Format("{0}Cleaning {1}...", prefix, t.Name), "info");

                if (t.Special == "recycle_bin" && overrides == null)
                {
                    int cnt;
                    long sz;
                    QueryRecycleBin(out cnt, out sz);
                    bool ok = true;
                    string rbMsg;
                    if (dryRun)
                    {
                        rbMsg = string.Format("[DRY-RUN] Would empty {0} item(s) ({1}) from Recycle Bin.", cnt, SystemInfo.FormatBytes(sz));
                    }
                    else
                    {
                        ok = EmptyRecycleBin(out rbMsg);
                    }
                    log(rbMsg, ok ? "success" : "warn");
                    if (ok) { totalFreed += sz; totalFiles += cnt; }
                    results.Add(new Dictionary<string, object>
                    {
                        { "id", tid }, { "name", t.Name }, { "status", ok ? "cleaned" : "error" },
                        { "message", rbMsg }, { "freed_bytes", ok ? sz : 0L },
                        { "freed_human", SystemInfo.FormatBytes(ok ? sz : 0L) }, { "deleted_files", ok ? cnt : 0 }
                    });
                    continue;
                }

                if (t.Special == "windows_update" && !dryRun && overrides == null)
                {
                    log("Stopping Windows Update service (wuauserv)...", "info");
                    string svcErr;
                    if (!ControlService("wuauserv", true, out svcErr))
                        log("wuauserv stop note: " + svcErr, "warn");
                }

                long itemFreed = 0;
                int itemFiles = 0;
                int itemLocked = 0;
                try
                {
                    foreach (PathSpec spec in t.Paths)
                    {
                        int df, sl;
                        long fb;
                        CleanDirectoryContents(spec.Path, spec.Pattern, dryRun, spec.RemoveRoot, out df, out fb, out sl);
                        itemFreed += fb;
                        itemFiles += df;
                        itemLocked += sl;
                    }
                }
                finally
                {
                    if (t.Special == "windows_update" && !dryRun && overrides == null)
                    {
                        log("Restarting Windows Update service (wuauserv)...", "info");
                        string svcErr;
                        if (!ControlService("wuauserv", false, out svcErr))
                            log("Note starting wuauserv: " + svcErr, "warn");
                    }
                }

                totalFreed += itemFreed;
                totalFiles += itemFiles;
                totalLocked += itemLocked;
                string msg = string.Format("{0}{1}: {2} file(s) ({3}){4}",
                    prefix, t.Name, itemFiles, SystemInfo.FormatBytes(itemFreed),
                    itemLocked > 0 ? string.Format(", {0} in-use skipped", itemLocked) : "");
                log(msg, "success");
                results.Add(new Dictionary<string, object>
                {
                    { "id", tid }, { "name", t.Name }, { "status", "cleaned" },
                    { "message", msg }, { "freed_bytes", itemFreed },
                    { "freed_human", SystemInfo.FormatBytes(itemFreed) },
                    { "deleted_files", itemFiles }, { "skipped_locked", itemLocked }
                });
            }

            long cAfter = SystemInfo.GetCDriveFreeBytes();
            long cDelta = (!dryRun && cAfter > cBefore) ? (cAfter - cBefore) : 0L;
            return new Dictionary<string, object>
            {
                { "dry_run", dryRun },
                { "results", results },
                { "total_freed_bytes", totalFreed },
                { "total_freed_human", SystemInfo.FormatBytes(totalFreed) },
                { "total_deleted_files", totalFiles },
                { "total_skipped_locked", totalLocked },
                { "c_drive_free_after_bytes", cAfter },
                { "c_drive_free_after_human", SystemInfo.FormatBytes(cAfter) },
                { "c_drive_delta_bytes", cDelta },
                { "c_drive_delta_human", SystemInfo.FormatBytes(cDelta) }
            };
        }
    }
}
