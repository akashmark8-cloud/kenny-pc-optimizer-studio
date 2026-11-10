using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KennyPCOptimizer
{
    public static class SystemInfo
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        private static bool _hasLastCpu;
        private static ulong _lastIdle;
        private static ulong _lastKernel;
        private static ulong _lastUser;
        private static readonly object _statsLock = new object();

        public static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 64 * 1024 * 1024;
            return js;
        }

        public static bool IsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static ulong FtToUlong(FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        }

        public static double GetCpuUsagePercent()
        {
            FILETIME ftIdle, ftKernel, ftUser;
            if (!GetSystemTimes(out ftIdle, out ftKernel, out ftUser)) return 0.0;
            ulong idle = FtToUlong(ftIdle);
            ulong kernel = FtToUlong(ftKernel);
            ulong user = FtToUlong(ftUser);

            if (!_hasLastCpu)
            {
                _lastIdle = idle;
                _lastKernel = kernel;
                _lastUser = user;
                _hasLastCpu = true;
                Thread.Sleep(80);
                if (!GetSystemTimes(out ftIdle, out ftKernel, out ftUser)) return 0.0;
                idle = FtToUlong(ftIdle);
                kernel = FtToUlong(ftKernel);
                user = FtToUlong(ftUser);
            }

            ulong dIdle = idle >= _lastIdle ? idle - _lastIdle : 0;
            ulong dKernel = kernel >= _lastKernel ? kernel - _lastKernel : 0;
            ulong dUser = user >= _lastUser ? user - _lastUser : 0;
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;

            ulong dTotal = dKernel + dUser;
            if (dTotal == 0) return 0.0;
            double busy = dTotal > dIdle ? (double)(dTotal - dIdle) : 0.0;
            return Math.Round(Math.Min(100.0, Math.Max(0.0, (busy / dTotal) * 100.0)), 1);
        }

        public static Dictionary<string, object> GetMemoryStats()
        {
            MEMORYSTATUSEX mem = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(mem))
            {
                return new Dictionary<string, object>
                {
                    { "total_bytes", 0L }, { "used_bytes", 0L }, { "avail_bytes", 0L },
                    { "total_gb", 0.0 }, { "used_gb", 0.0 }, { "avail_gb", 0.0 }, { "percent", 0 }
                };
            }
            ulong total = mem.ullTotalPhys;
            ulong avail = mem.ullAvailPhys;
            ulong used = total >= avail ? total - avail : 0;
            double gb = 1024.0 * 1024.0 * 1024.0;
            return new Dictionary<string, object>
            {
                { "total_bytes", (long)total },
                { "used_bytes", (long)used },
                { "avail_bytes", (long)avail },
                { "total_gb", Math.Round(total / gb, 2) },
                { "used_gb", Math.Round(used / gb, 2) },
                { "avail_gb", Math.Round(avail / gb, 2) },
                { "percent", (int)mem.dwMemoryLoad }
            };
        }

        public static List<Dictionary<string, object>> GetDrivesStats()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            double gb = 1024.0 * 1024.0 * 1024.0;
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                    long total = d.TotalSize;
                    long free = d.AvailableFreeSpace;
                    long used = Math.Max(0L, total - free);
                    string letter = d.Name.Substring(0, 1).ToUpperInvariant();
                    list.Add(new Dictionary<string, object>
                    {
                        { "letter", letter },
                        { "root", d.Name },
                        { "total_bytes", total },
                        { "free_bytes", free },
                        { "used_bytes", used },
                        { "total_gb", Math.Round(total / gb, 2) },
                        { "free_gb", Math.Round(free / gb, 2) },
                        { "used_gb", Math.Round(used / gb, 2) },
                        { "used_percent", Math.Round((used / (double)total) * 100.0, 1) }
                    });
                }
                catch { }
            }
            return list;
        }

        public static long GetCDriveFreeBytes()
        {
            try
            {
                string sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                DriveInfo d = new DriveInfo(sysDrive);
                if (d.IsReady) return d.AvailableFreeSpace;
            }
            catch { }
            return 0L;
        }

        public static string FormatBytes(long numBytes)
        {
            long n = Math.Max(0L, numBytes);
            if (n >= 1024L * 1024L * 1024L)
                return string.Format("{0:0.00} GB", n / (1024.0 * 1024.0 * 1024.0));
            if (n >= 1024L * 1024L)
                return string.Format("{0:0.00} MB", n / (1024.0 * 1024.0));
            if (n >= 1024L)
                return string.Format("{0:0.0} KB", n / 1024.0);
            return string.Format("{0} B", n);
        }

        public static Dictionary<string, object> LoadSettings()
        {
            Config.EnsureAppDirs();
            Dictionary<string, object> defaults = new Dictionary<string, object>
            {
                { "backup_dir", Config.DefaultBackupDir },
                { "auto_backup_before_clean", true },
                { "create_restore_point_on_backup", true },
                { "archive_browser_history_before_clean", true },
                { "sound_enabled", true },
                { "ram_auto_threshold_enabled", false },
                { "ram_auto_threshold_percent", 80 },
                { "ram_auto_interval_enabled", false },
                { "ram_auto_interval_minutes", 30 },
                { "ram_tray_notifications", true }
            };
            if (File.Exists(Config.SettingsFile))
            {
                try
                {
                    string json = File.ReadAllText(Config.SettingsFile);
                    Dictionary<string, object> saved = CreateSerializer().Deserialize<Dictionary<string, object>>(json);
                    if (saved != null)
                    {
                        foreach (KeyValuePair<string, object> kv in saved) defaults[kv.Key] = kv.Value;
                    }
                }
                catch { }
            }
            return defaults;
        }

        public static Dictionary<string, object> SaveSettings(Dictionary<string, object> updates)
        {
            Dictionary<string, object> cur = LoadSettings();
            if (updates != null)
            {
                foreach (KeyValuePair<string, object> kv in updates) cur[kv.Key] = kv.Value;
            }
            Config.EnsureAppDirs();
            object bkpObj;
            if (cur.TryGetValue("backup_dir", out bkpObj) && bkpObj != null)
            {
                Directory.CreateDirectory(bkpObj.ToString());
            }
            File.WriteAllText(Config.SettingsFile, CreateSerializer().Serialize(cur));
            return cur;
        }

        public static Dictionary<string, object> LoadStats()
        {
            lock (_statsLock)
            {
                Config.EnsureAppDirs();
                Dictionary<string, object> defaults = new Dictionary<string, object>
                {
                    { "total_bytes_freed", 0L },
                    { "total_files_cleaned", 0L },
                    { "total_registry_keys_cleaned", 0L },
                    { "total_runs", 0L },
                    { "last_clean_at", null },
                    { "history", new List<object>() }
                };
                if (File.Exists(Config.StatsFile))
                {
                    try
                    {
                        string json = File.ReadAllText(Config.StatsFile);
                        Dictionary<string, object> saved = CreateSerializer().Deserialize<Dictionary<string, object>>(json);
                        if (saved != null)
                        {
                            foreach (KeyValuePair<string, object> kv in saved) defaults[kv.Key] = kv.Value;
                        }
                    }
                    catch { }
                }
                return defaults;
            }
        }

        private static long ToLong(object obj)
        {
            if (obj == null) return 0L;
            try { return Convert.ToInt64(obj); } catch { return 0L; }
        }

        public static Dictionary<string, object> RecordCleanedStats(
            long bytesFreed,
            long filesCleaned,
            long registryKeysCleaned,
            string summaryLabel,
            bool dryRun)
        {
            lock (_statsLock)
            {
                Dictionary<string, object> stats = LoadStats();
                if (dryRun) return stats;

                stats["total_bytes_freed"] = ToLong(stats["total_bytes_freed"]) + Math.Max(0L, bytesFreed);
                stats["total_files_cleaned"] = ToLong(stats["total_files_cleaned"]) + Math.Max(0L, filesCleaned);
                stats["total_registry_keys_cleaned"] = ToLong(stats["total_registry_keys_cleaned"]) + Math.Max(0L, registryKeysCleaned);
                stats["total_runs"] = ToLong(stats["total_runs"]) + 1L;
                string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                stats["last_clean_at"] = nowStr;

                List<object> hist = new List<object>();
                object existingHist = stats.ContainsKey("history") ? stats["history"] : null;
                if (existingHist is System.Collections.IEnumerable)
                {
                    foreach (object item in (System.Collections.IEnumerable)existingHist) hist.Add(item);
                }

                hist.Insert(0, new Dictionary<string, object>
                {
                    { "timestamp", nowStr },
                    { "label", summaryLabel ?? "Cleanup Run" },
                    { "bytes_freed", bytesFreed },
                    { "bytes_freed_human", FormatBytes(bytesFreed) },
                    { "files_cleaned", filesCleaned },
                    { "registry_keys_cleaned", registryKeysCleaned }
                });
                if (hist.Count > 30) hist = hist.GetRange(0, 30);
                stats["history"] = hist;

                Config.EnsureAppDirs();
                File.WriteAllText(Config.StatsFile, CreateSerializer().Serialize(stats));
                return stats;
            }
        }

        public static Dictionary<string, object> GetWindowsOsInfo()
        {
            string product = Environment.OSVersion.VersionString;
            string build = Environment.OSVersion.Version.Build.ToString();
            string displayVer = "";
            try
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        object p = k.GetValue("ProductName");
                        if (p != null) product = p.ToString();
                        object cb = k.GetValue("CurrentBuild");
                        object ubr = k.GetValue("UBR");
                        if (cb != null)
                        {
                            build = ubr != null ? string.Format("{0}.{1}", cb, ubr) : cb.ToString();
                            int bNum;
                            if (int.TryParse(cb.ToString(), out bNum) && bNum >= 22000 && product.Contains("Windows 10"))
                            {
                                product = product.Replace("Windows 10", "Windows 11");
                            }
                        }
                        object dv = k.GetValue("DisplayVersion");
                        if (dv != null) displayVer = dv.ToString();
                    }
                }
            }
            catch { }
            return new Dictionary<string, object>
            {
                { "product", product },
                { "build", build },
                { "display_version", displayVer }
            };
        }

        public static List<string> GetSteamLibraryPaths()
        {
            List<string> candidates = new List<string>();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k != null)
                    {
                        object val = k.GetValue("SteamPath");
                        if (val != null)
                        {
                            string p = val.ToString().Replace('/', '\\');
                            if (Directory.Exists(p)) candidates.Add(p);
                        }
                    }
                }
            }
            catch { }

            string def = @"C:\Program Files (x86)\Steam";
            if (Directory.Exists(def)) candidates.Add(def);

            List<string> libs = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string steamDir in candidates)
            {
                if (seen.Add(steamDir)) libs.Add(steamDir);
                string vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    try
                    {
                        string text = File.ReadAllText(vdf);
                        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                        {
                            string lp = m.Groups[1].Value.Replace("\\\\", "\\");
                            if (Directory.Exists(lp) && seen.Add(lp)) libs.Add(lp);
                        }
                    }
                    catch { }
                }
            }
            return libs;
        }

        public static Dictionary<string, object> GetTelemetry()
        {
            List<Dictionary<string, object>> drives = GetDrivesStats();
            Dictionary<string, object> cDrive = null;
            foreach (Dictionary<string, object> d in drives)
            {
                if (string.Equals(d["letter"].ToString(), "C", StringComparison.OrdinalIgnoreCase))
                {
                    cDrive = d;
                    break;
                }
            }
            if (cDrive == null && drives.Count > 0) cDrive = drives[0];

            Dictionary<string, object> rawStats = LoadStats();
            Dictionary<string, object> statsOut = new Dictionary<string, object>(rawStats);
            statsOut["total_bytes_freed_human"] = FormatBytes(ToLong(rawStats["total_bytes_freed"]));

            return new Dictionary<string, object>
            {
                { "computer_name", Environment.MachineName },
                { "username", Environment.UserName },
                { "is_admin", IsAdmin() },
                { "os", GetWindowsOsInfo() },
                { "cpu_percent", GetCpuUsagePercent() },
                { "memory", GetMemoryStats() },
                { "drives", drives },
                { "c_drive", cDrive },
                { "stats", statsOut },
                { "settings", LoadSettings() },
                { "timestamp", DateTime.Now.ToString("HH:mm:ss") }
            };
        }

        public static bool TriggerUacElevation(string exePath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
