using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace KennyPCOptimizer
{
    public static class SystemAdvisor
    {
        public sealed class WingetUpgradeItem
        {
            public string Name;
            public string Id;
            public string Version;
            public string Available;
            public string Source;
            public bool ExplicitOnly;
        }

        public static List<Dictionary<string, object>> ParseWingetUpgradeOutput(string output)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(output)) return results;

            string[] rawLines = output.Split('\n');
            List<string> lines = new List<string>();
            foreach (string rl in rawLines)
            {
                string clean = rl;
                int lastCr = clean.LastIndexOf('\r');
                if (lastCr >= 0) clean = clean.Substring(lastCr + 1);
                lines.Add(clean);
            }

            bool explicitSection = false;
            int idxId = -1, idxVer = -1, idxAvail = -1, idxSrc = -1;
            bool inTable = false;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                string trim = line.Trim();

                if (trim.IndexOf("require explicit targeting", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    explicitSection = true;
                    inTable = false;
                    continue;
                }

                if (line.Contains("Name") && line.Contains("Id") && line.Contains("Version") && line.Contains("Available"))
                {
                    idxId = line.IndexOf("Id", StringComparison.Ordinal);
                    idxVer = line.IndexOf("Version", StringComparison.Ordinal);
                    idxAvail = line.IndexOf("Available", StringComparison.Ordinal);
                    idxSrc = line.IndexOf("Source", StringComparison.Ordinal);
                    if (idxId > 0 && idxVer > idxId && idxAvail > idxVer)
                    {
                        // Next line should be ----
                        if (i + 1 < lines.Count && lines[i + 1].TrimStart().StartsWith("---"))
                        {
                            i++;
                            inTable = true;
                        }
                    }
                    continue;
                }

                if (!inTable) continue;
                if (string.IsNullOrWhiteSpace(trim) ||
                    trim.EndsWith("upgrades available.", StringComparison.OrdinalIgnoreCase) ||
                    trim.Contains("cannot be determined"))
                {
                    inTable = false;
                    continue;
                }

                if (line.Length < idxAvail) continue;
                string name = line.Substring(0, Math.Min(idxId, line.Length)).Trim();
                string id = SafeSlice(line, idxId, idxVer);
                string ver = SafeSlice(line, idxVer, idxAvail);
                string avail = idxSrc > idxAvail ? SafeSlice(line, idxAvail, idxSrc) : line.Substring(idxAvail).Trim();
                string src = (idxSrc > idxAvail && line.Length > idxSrc) ? line.Substring(idxSrc).Trim() : "winget";

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(avail))
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "id", id },
                        { "version", ver },
                        { "available", avail },
                        { "source", src },
                        { "explicit_only", explicitSection }
                    });
                }
            }
            return results;
        }

        private static string SafeSlice(string s, int start, int end)
        {
            if (start >= s.Length) return "";
            int len = Math.Min(end, s.Length) - start;
            if (len <= 0) return "";
            return s.Substring(start, len).Trim();
        }

        private static int RunProcessUtf8(string fileName, string args, int timeoutMs, out string stdout, out string stderr)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = Process.Start(psi))
            {
                StringBuilder outSb = new StringBuilder();
                StringBuilder errSb = new StringBuilder();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) outSb.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) errSb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    stdout = outSb.ToString();
                    stderr = "Timed out";
                    return -1;
                }
                p.WaitForExit();
                stdout = outSb.ToString();
                stderr = errSb.ToString();
                return p.ExitCode;
            }
        }

        private static Dictionary<string, object> GetHardwareProfile()
        {
            string cpuName = "";
            List<string> gpus = new List<string>();
            string board = "";
            string biosVer = "";
            string biosDate = "";
            bool hasBattery = false;
            List<Dictionary<string, object>> disks = new List<Dictionary<string, object>>();

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject o in s.Get()) { cpuName = (o["Name"] ?? "").ToString().Trim(); break; }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string n = (o["Name"] ?? "").ToString().Trim();
                        if (!string.IsNullOrEmpty(n)) gpus.Add(n);
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        board = ((o["Manufacturer"] ?? "") + " " + (o["Product"] ?? "")).Trim();
                        break;
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        biosVer = (o["SMBIOSBIOSVersion"] ?? "").ToString().Trim();
                        string rd = (o["ReleaseDate"] ?? "").ToString();
                        if (rd.Length >= 8) biosDate = rd.Substring(0, 4) + "-" + rd.Substring(4, 2) + "-" + rd.Substring(6, 2);
                        break;
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery"))
                {
                    foreach (ManagementObject o in s.Get()) { hasBattery = true; break; }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Model, Status, Size FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        long sz = 0;
                        try { sz = Convert.ToInt64(o["Size"]); } catch { }
                        disks.Add(new Dictionary<string, object>
                        {
                            { "model", (o["Model"] ?? "").ToString().Trim() },
                            { "status", (o["Status"] ?? "OK").ToString() },
                            { "size_human", SystemInfo.FormatBytes(sz) }
                        });
                    }
                }
            }
            catch { }

            return new Dictionary<string, object>
            {
                { "form_factor", hasBattery ? "Laptop / Battery System" : "Desktop PC" },
                { "is_laptop", hasBattery },
                { "cpu", cpuName },
                { "gpus", gpus },
                { "motherboard", board },
                { "bios_version", biosVer },
                { "bios_date", biosDate },
                { "disks", disks }
            };
        }

        private static List<Dictionary<string, object>> ScanKeyDrivers()
        {
            HashSet<string> keyClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DISPLAY", "NET", "MEDIA", "BLUETOOTH", "SCSIADAPTER", "SYSTEM", "USB"
            };
            List<Dictionary<string, object>> drivers = new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT DeviceClass, DeviceName, DriverVersion, DriverDate, Manufacturer, IsSigned FROM Win32_PnPSignedDriver"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string cls = (o["DeviceClass"] ?? "").ToString().Trim();
                        string name = (o["DeviceName"] ?? "").ToString().Trim();
                        if (string.IsNullOrEmpty(name) || !keyClasses.Contains(cls)) continue;

                        string mfg = (o["Manufacturer"] ?? "").ToString().Trim();
                        // Filter out noisy generic 2006 Microsoft inbox system devices unless critical Display/Net/Storage
                        string rawDate = (o["DriverDate"] ?? "").ToString();
                        string dateFmt = "";
                        int year = 0;
                        if (rawDate.Length >= 8)
                        {
                            dateFmt = rawDate.Substring(0, 4) + "-" + rawDate.Substring(4, 2) + "-" + rawDate.Substring(6, 2);
                            int.TryParse(rawDate.Substring(0, 4), out year);
                        }

                        if (cls == "SYSTEM" && mfg.StartsWith("(", StringComparison.Ordinal) || (cls == "SYSTEM" && mfg.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        if (name.StartsWith("WAN Miniport", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string ver = (o["DriverVersion"] ?? "").ToString().Trim();
                        string dedupeKey = cls + "|" + name + "|" + ver;
                        if (!seen.Add(dedupeKey)) continue;

                        bool isOld = (year > 2006 && year <= DateTime.Now.Year - 2);
                        bool signed = o["IsSigned"] != null && Convert.ToBoolean(o["IsSigned"]);

                        drivers.Add(new Dictionary<string, object>
                        {
                            { "device_class", cls },
                            { "name", name },
                            { "version", ver },
                            { "date", dateFmt },
                            { "manufacturer", mfg },
                            { "signed", signed },
                            { "status_flag", isOld ? "Aged (>2 yrs)" : "Current" }
                        });
                    }
                }
            }
            catch { }

            drivers.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                int cmp = string.Compare(a["device_class"].ToString(), b["device_class"].ToString(), StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.Compare(a["name"].ToString(), b["name"].ToString(), StringComparison.OrdinalIgnoreCase);
            });
            return drivers;
        }

        private static Dictionary<string, object> CheckWindowsUpdatePending()
        {
            List<Dictionary<string, object>> pendingDrivers = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> pendingSoftware = new List<Dictionary<string, object>>();
            string statusMsg = "Up to date";

            try
            {
                Type sessType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessType == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "ok", false }, { "pending_drivers", pendingDrivers }, { "pending_software", pendingSoftware },
                        { "message", "Windows Update COM unavailable." }
                    };
                }
                object session = Activator.CreateInstance(sessType);
                object searcher = sessType.InvokeMember("CreateUpdateSearcher", BindingFlags.InvokeMethod, null, session, null);
                object result = searcher.GetType().InvokeMember("Search", BindingFlags.InvokeMethod, null, searcher, new object[] { "IsInstalled=0 and IsHidden=0" });
                object updates = result.GetType().InvokeMember("Updates", BindingFlags.GetProperty, null, result, null);
                int count = Convert.ToInt32(updates.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, updates, null));

                for (int i = 0; i < count; i++)
                {
                    object upd = updates.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, updates, new object[] { i });
                    string title = (upd.GetType().InvokeMember("Title", BindingFlags.GetProperty, null, upd, null) ?? "").ToString();
                    int uType = Convert.ToInt32(upd.GetType().InvokeMember("Type", BindingFlags.GetProperty, null, upd, null));
                    Dictionary<string, object> item = new Dictionary<string, object>
                    {
                        { "title", title },
                        { "type", uType == 2 ? "Driver" : "Software/Security" }
                    };
                    if (uType == 2) pendingDrivers.Add(item);
                    else pendingSoftware.Add(item);
                }

                statusMsg = string.Format("{0} pending driver update(s), {1} pending OS/security update(s).",
                    pendingDrivers.Count, pendingSoftware.Count);
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "pending_drivers", pendingDrivers },
                    { "pending_software", pendingSoftware },
                    { "message", statusMsg }
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "pending_drivers", pendingDrivers },
                    { "pending_software", pendingSoftware },
                    { "message", "Windows Update check note: " + ex.Message }
                };
            }
        }

        private static HashSet<string> CollectInstalledProgramNames()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Tuple<RegistryHive, RegistryView, string>[] locs = new Tuple<RegistryHive, RegistryView, string>[]
            {
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
            };
            foreach (Tuple<RegistryHive, RegistryView, string> loc in locs)
            {
                try
                {
                    using (RegistryKey baseK = RegistryKey.OpenBaseKey(loc.Item1, loc.Item2))
                    using (RegistryKey k = baseK.OpenSubKey(loc.Item3))
                    {
                        if (k == null) continue;
                        foreach (string sub in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey sk = k.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    object dn = sk.GetValue("DisplayName");
                                    if (dn != null) names.Add(dn.ToString());
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return names;
        }

        private static bool HasInstalledMatch(HashSet<string> installedNames, params string[] substrings)
        {
            foreach (string nm in installedNames)
            {
                foreach (string sub in substrings)
                {
                    if (nm.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        public static List<Dictionary<string, object>> BuildEssentialsCatalog(Dictionary<string, object> hw)
        {
            HashSet<string> installed = CollectInstalledProgramNames();
            bool isLaptop = hw.ContainsKey("is_laptop") && Convert.ToBoolean(hw["is_laptop"]);
            string cpu = hw.ContainsKey("cpu") ? hw["cpu"].ToString() : "";
            List<string> gpus = hw.ContainsKey("gpus") ? (List<string>)hw["gpus"] : new List<string>();
            string gpuStr = string.Join(" ", gpus.ToArray());

            List<Dictionary<string, object>> catalog = new List<Dictionary<string, object>>();
            Action<string, string, string, string, bool, string> addEss = delegate(string id, string name, string cat, string why, bool isInstalled, string wingetId)
            {
                catalog.Add(new Dictionary<string, object>
                {
                    { "id", id },
                    { "name", name },
                    { "category", cat },
                    { "reason", why },
                    { "installed", isInstalled },
                    { "winget_id", wingetId }
                });
            };

            addEss("vcredist_x64", "Visual C++ 2015–2022 Redistributable (x64)", "Core Runtime",
                "Essential C/C++ runtime required by games, browsers, and productivity apps.",
                HasInstalledMatch(installed, "Visual C++ 2022 X64", "Visual C++ v14 Redistributable (x64)"),
                "Microsoft.VCRedist.2015+.x64");

            addEss("dotnet_desktop_9", ".NET Desktop Runtime 9 (x64)", "Core Runtime",
                "Powers modern Windows 11 desktop utilities alongside .NET 8.",
                HasInstalledMatch(installed, "Windows Desktop Runtime - 9."),
                "Microsoft.DotNet.DesktopRuntime.9");

            bool hasDx9 = File.Exists(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32", "d3dx9_43.dll"));
            addEss("directx_legacy", "DirectX End-User Runtime (Legacy DX9/10/11)", "Core Runtime",
                "Prevents missing d3dx9_*.dll errors in older games and launchers.",
                hasDx9,
                "Microsoft.DirectX");

            addEss("webview2", "Microsoft Edge WebView2 Runtime", "Core Runtime",
                "Embedded web runtime used by modern Windows apps and Office.",
                HasInstalledMatch(installed, "Edge WebView2 Runtime"),
                "Microsoft.EdgeWebView2Runtime");

            addEss("7zip", "7-Zip File Archiver", "System Utility",
                "Fast, lightweight, zero-bloat archive manager for ZIP/RAR/7z files.",
                HasInstalledMatch(installed, "7-Zip"),
                "7zip.7zip");

            addEss("everything", "Everything Search (voidtools)", "Smoothness & Speed",
                "Instant file search using near-zero RAM/CPU compared to heavy Windows indexing.",
                HasInstalledMatch(installed, "Everything "),
                "voidtools.Everything");

            addEss("powertoys", "Microsoft PowerToys", "Smoothness & Productivity",
                "Official Microsoft toolkit: FancyZones window tiling, quick launcher, File Locksmith & more.",
                HasInstalledMatch(installed, "PowerToys"),
                "Microsoft.PowerToys");

            addEss("crystaldiskinfo", "CrystalDiskInfo", "Drive & Health Monitor",
                "Monitors NVMe/SSD S.M.A.R.T. health, wear level, and temperatures early.",
                HasInstalledMatch(installed, "CrystalDiskInfo"),
                "CrystalDewWorld.CrystalDiskInfo");

            addEss("hwinfo", "HWiNFO", "Thermal & Hardware Monitor",
                "Diagnoses CPU/GPU temperatures, throttling, and fan speeds cleanly.",
                HasInstalledMatch(installed, "HWiNFO"),
                "REALiX.HWiNFO");

            addEss("autoruns", "Sysinternals Autoruns", "Startup Optimizer",
                "Finds hidden startup entries, scheduled tasks, and shell extensions slowing boot.",
                HasInstalledMatch(installed, "Autoruns") || File.Exists(@"C:\Tools\Autoruns64.exe"),
                "Microsoft.Sysinternals.Autoruns");

            if (gpuStr.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                addEss("nvidia_app", "NVIDIA App (Driver & GPU Manager)", "Hardware Driver Suite",
                    "Keeps your NVIDIA GeForce Game Ready / Studio drivers and shader profiles up to date.",
                    HasInstalledMatch(installed, "NVIDIA App", "GeForce Experience"),
                    "Nvidia.NVIDIAApp");
            }
            if (cpu.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                addEss("amd_chipset", "AMD Chipset Drivers", "Hardware Driver Suite",
                    "Optimizes Ryzen power plans, PCIe bus, USB, and SMBus stability.",
                    HasInstalledMatch(installed, "AMD Chipset"),
                    "");
            }
            if (isLaptop)
            {
                addEss("laptop_battery_care", "OEM Power & 80% Battery Charge Limiter", "Laptop Longevity",
                    "Use your laptop OEM utility (e.g. Lenovo Vantage / MyASUS / Dell Power Manager) to limit charge to 80% when plugged in.",
                    false,
                    "");
            }

            return catalog;
        }

        public static Dictionary<string, object> ScanDriversAndApps(bool checkOnlineWinget = true, bool checkWindowsUpdate = true)
        {
            Dictionary<string, object> hw = GetHardwareProfile();
            List<Dictionary<string, object>> drivers = ScanKeyDrivers();
            Dictionary<string, object> wu = checkWindowsUpdate
                ? CheckWindowsUpdatePending()
                : new Dictionary<string, object> { { "ok", false }, { "pending_drivers", new List<object>() }, { "pending_software", new List<object>() }, { "message", "Skipped" } };

            List<Dictionary<string, object>> appUpdates = new List<Dictionary<string, object>>();
            string wingetNote = "";
            if (checkOnlineWinget)
            {
                string stdout, stderr;
                int rc = RunProcessUtf8("winget", "upgrade --include-unknown --accept-source-agreements --disable-interactivity", 45000, out stdout, out stderr);
                appUpdates = ParseWingetUpgradeOutput(stdout);
                if (rc != 0 && appUpdates.Count == 0)
                {
                    wingetNote = string.IsNullOrWhiteSpace(stderr) ? "winget unavailable or no updates found." : stderr.Trim();
                }
                else
                {
                    wingetNote = string.Format("Found {0} available app update(s) via winget.", appUpdates.Count);
                }
            }

            List<Dictionary<string, object>> essentials = BuildEssentialsCatalog(hw);

            return new Dictionary<string, object>
            {
                { "hardware", hw },
                { "drivers", drivers },
                { "driver_count", drivers.Count },
                { "windows_update", wu },
                { "app_updates", appUpdates },
                { "app_updates_count", appUpdates.Count },
                { "winget_note", wingetNote },
                { "essentials", essentials }
            };
        }

        public static Dictionary<string, object> RunWingetPackageAction(string action, string packageId, LogCallback logCb)
        {
            LogCallback log = delegate(string m, string l) { if (logCb != null) logCb(m, l); };
            if (string.IsNullOrWhiteSpace(packageId))
                return new Dictionary<string, object> { { "ok", false }, { "message", "Missing package ID." } };

            // Strictly validate packageId characters to prevent command injection
            foreach (char c in packageId)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    return new Dictionary<string, object> { { "ok", false }, { "message", "Invalid package ID characters." } };
            }

            string verb = string.Equals(action, "install", StringComparison.OrdinalIgnoreCase) ? "install" : "upgrade";
            log(string.Format("Running winget {0} for {1}...", verb, packageId), "info");
            string args = string.Format(
                "{0} --id \"{1}\" --exact --accept-package-agreements --accept-source-agreements --disable-interactivity",
                verb, packageId);
            string stdout, stderr;
            int rc = RunProcessUtf8("winget", args, 180000, out stdout, out stderr);
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                foreach (string line in stdout.Split('\n'))
                {
                    string t = line.Trim();
                    if (!string.IsNullOrEmpty(t) && t.Length > 2) log(t, "info");
                }
            }
            bool ok = rc == 0;
            string msg = ok
                ? string.Format("Successfully completed winget {0} for {1}.", verb, packageId)
                : string.Format("winget {0} exited with code {1}. {2}", verb, rc, stderr.Trim());
            log(msg, ok ? "success" : "warn");
            return new Dictionary<string, object> { { "ok", ok }, { "exit_code", rc }, { "message", msg } };
        }
    }
}
