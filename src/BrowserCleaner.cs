using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using Microsoft.Win32;

namespace KennyPCOptimizer
{
    public static class BrowserCleaner
    {
        private static readonly string[] ChromiumMarkers = new string[]
        {
            "Preferences", "History", "Bookmarks", "Cache", "Web Data", "Network"
        };

        private static readonly string[] ChromiumHistoryFiles = new string[]
        {
            "History", "History-journal", "History-wal", "History-shm",
            "Visited Links", "Top Sites", "Top Sites-journal",
            "Shortcuts", "Shortcuts-journal",
            "Network Action Predictor", "Network Action Predictor-journal",
            "Media History", "Media History-journal",
            "Reporting and NEL", "Reporting and NEL-journal",
            "DIPS", "DIPS-wal", "BrowsingTopicsSiteData", "BrowsingTopicsState"
        };

        private static readonly string[] ChromiumCacheDirs = new string[]
        {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache",
            "DawnWebGPUCache", "GraphiteDawnCache", "ShaderCache", "GrShaderCache",
            "GPUPersistentCache", "Media Cache", "Application Cache",
            @"Service Worker\CacheStorage", @"Service Worker\ScriptCache",
            "blob_storage", "VideoDecodeStats", "optimization_guide_hint_cache_store"
        };

        private static readonly string[] ChromiumRootCacheDirs = new string[]
        {
            "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "GPUPersistentCache",
            "component_crx_cache", "extensions_crx_cache", "Webstore Downloads"
        };

        private static readonly string[] ChromiumRootLeftoverDirs = new string[]
        {
            @"Crashpad\reports", @"Crashpad\pending", @"Crashpad\completed", "Stability"
        };

        private static readonly string[] ChromiumCookiesItems = new string[]
        {
            "Cookies", "Cookies-journal", @"Network\Cookies", @"Network\Cookies-journal",
            "Local Storage", "Session Storage", "IndexedDB"
        };

        private static readonly string[] GeckoHistoryFiles = new string[]
        {
            "formhistory.sqlite", "formhistory.sqlite-wal", "formhistory.sqlite-shm",
            "webappsstore.sqlite", "webappsstore.sqlite-wal", "sessionstore-backups"
        };

        private static readonly string[] GeckoCacheDirs = new string[]
        {
            "cache2", "startupCache", "thumbnails", "jumpListCache", "shader-cache", "offlineCache"
        };

        private static readonly string[] GeckoLeftoverItems = new string[]
        {
            "crashes", "minidumps", @"datareporting\archived", "saved-telemetry-pings"
        };

        private static readonly string[] GeckoCookiesItems = new string[]
        {
            "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm", @"storage\default"
        };

        private sealed class BrowserSpec
        {
            public string Id;
            public string Name;
            public string Engine;
            public string Color;
            public bool FlatProfile;
            public List<string> ProcessNames = new List<string>();
            public List<string> UserDataDirs = new List<string>();
            public List<string> RoamingRoots = new List<string>();
            public List<string> LocalRoots = new List<string>();
            public List<string> ExtraLeftoverDirs = new List<string>();
            public List<string> ExeCandidates = new List<string>();
            public Dictionary<string, List<string>> IeDirs = new Dictionary<string, List<string>>();
        }

        private static List<BrowserSpec> BuildSpecs(Dictionary<string, string> overrides)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";

            if (overrides != null)
            {
                if (overrides.ContainsKey("LOCALAPPDATA")) local = overrides["LOCALAPPDATA"];
                if (overrides.ContainsKey("APPDATA")) roam = overrides["APPDATA"];
                if (overrides.ContainsKey("ProgramFiles")) pf = overrides["ProgramFiles"];
                if (overrides.ContainsKey("ProgramFiles(x86)")) pf86 = overrides["ProgramFiles(x86)"];
            }

            List<BrowserSpec> specs = new List<BrowserSpec>();

            specs.Add(new BrowserSpec
            {
                Id = "chrome", Name = "Google Chrome", Engine = "chromium", Color = "#4285F4",
                ProcessNames = { "chrome.exe" },
                UserDataDirs = { Path.Combine(local, "Google", "Chrome", "User Data") },
                ExeCandidates =
                {
                    Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "chrome_beta", Name = "Google Chrome Beta / Dev / Canary", Engine = "chromium", Color = "#5B9BFF",
                ProcessNames = { "chrome.exe" },
                UserDataDirs =
                {
                    Path.Combine(local, "Google", "Chrome Beta", "User Data"),
                    Path.Combine(local, "Google", "Chrome Dev", "User Data"),
                    Path.Combine(local, "Google", "Chrome SxS", "User Data")
                },
                ExeCandidates =
                {
                    Path.Combine(pf, "Google", "Chrome Beta", "Application", "chrome.exe"),
                    Path.Combine(local, "Google", "Chrome SxS", "Application", "chrome.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "edge", Name = "Microsoft Edge", Engine = "chromium", Color = "#00A4EF",
                ProcessNames = { "msedge.exe" },
                UserDataDirs = { Path.Combine(local, "Microsoft", "Edge", "User Data") },
                ExeCandidates =
                {
                    Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "edge_preview", Name = "Microsoft Edge Beta / Dev / Canary", Engine = "chromium", Color = "#38BDF8",
                ProcessNames = { "msedge.exe" },
                UserDataDirs =
                {
                    Path.Combine(local, "Microsoft", "Edge Beta", "User Data"),
                    Path.Combine(local, "Microsoft", "Edge Dev", "User Data"),
                    Path.Combine(local, "Microsoft", "Edge SxS", "User Data")
                },
                ExeCandidates =
                {
                    Path.Combine(pf86, "Microsoft", "Edge Beta", "Application", "msedge.exe"),
                    Path.Combine(local, "Microsoft", "Edge SxS", "Application", "msedge.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "brave", Name = "Brave Browser", Engine = "chromium", Color = "#FB542B",
                ProcessNames = { "brave.exe" },
                UserDataDirs =
                {
                    Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
                    Path.Combine(local, "BraveSoftware", "Brave-Browser-Beta", "User Data"),
                    Path.Combine(local, "BraveSoftware", "Brave-Browser-Nightly", "User Data")
                },
                ExeCandidates =
                {
                    Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(pf86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "firefox", Name = "Mozilla Firefox", Engine = "gecko", Color = "#FF7139",
                ProcessNames = { "firefox.exe" },
                RoamingRoots = { Path.Combine(roam, "Mozilla", "Firefox") },
                LocalRoots = { Path.Combine(local, "Mozilla", "Firefox") },
                ExtraLeftoverDirs = { Path.Combine(local, "Mozilla", "updates") },
                ExeCandidates =
                {
                    Path.Combine(pf, "Mozilla Firefox", "firefox.exe"),
                    Path.Combine(pf86, "Mozilla Firefox", "firefox.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "opera", Name = "Opera Browser", Engine = "chromium", Color = "#FF1B2D", FlatProfile = true,
                ProcessNames = { "opera.exe" },
                UserDataDirs =
                {
                    Path.Combine(roam, "Opera Software", "Opera Stable"),
                    Path.Combine(local, "Opera Software", "Opera Stable")
                },
                ExeCandidates =
                {
                    Path.Combine(local, "Programs", "Opera", "opera.exe"),
                    Path.Combine(pf, "Opera", "opera.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "opera_gx", Name = "Opera GX", Engine = "chromium", Color = "#FA1E4E", FlatProfile = true,
                ProcessNames = { "opera.exe" },
                UserDataDirs =
                {
                    Path.Combine(roam, "Opera Software", "Opera GX Stable"),
                    Path.Combine(local, "Opera Software", "Opera GX Stable")
                },
                ExeCandidates =
                {
                    Path.Combine(local, "Programs", "Opera GX", "opera.exe"),
                    Path.Combine(pf, "Opera GX", "opera.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "vivaldi", Name = "Vivaldi", Engine = "chromium", Color = "#EF3939",
                ProcessNames = { "vivaldi.exe" },
                UserDataDirs = { Path.Combine(local, "Vivaldi", "User Data") },
                ExeCandidates =
                {
                    Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe"),
                    Path.Combine(pf, "Vivaldi", "Application", "vivaldi.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "chromium", Name = "Chromium", Engine = "chromium", Color = "#60A5FA",
                ProcessNames = { "chrome.exe" },
                UserDataDirs = { Path.Combine(local, "Chromium", "User Data") },
                ExeCandidates = { Path.Combine(local, "Chromium", "Application", "chrome.exe") }
            });
            specs.Add(new BrowserSpec
            {
                Id = "thorium", Name = "Thorium", Engine = "chromium", Color = "#38BDF8",
                ProcessNames = { "thorium.exe" },
                UserDataDirs = { Path.Combine(local, "Thorium", "User Data") },
                ExeCandidates = { Path.Combine(local, "Thorium", "Application", "thorium.exe") }
            });
            specs.Add(new BrowserSpec
            {
                Id = "librewolf", Name = "LibreWolf", Engine = "gecko", Color = "#00ACFF",
                ProcessNames = { "librewolf.exe" },
                RoamingRoots = { Path.Combine(roam, "librewolf") },
                LocalRoots = { Path.Combine(local, "librewolf") },
                ExeCandidates = { Path.Combine(pf, "LibreWolf", "librewolf.exe") }
            });
            specs.Add(new BrowserSpec
            {
                Id = "waterfox", Name = "Waterfox", Engine = "gecko", Color = "#22D3EE",
                ProcessNames = { "waterfox.exe" },
                RoamingRoots = { Path.Combine(roam, "Waterfox") },
                LocalRoots = { Path.Combine(local, "Waterfox") },
                ExeCandidates = { Path.Combine(pf, "Waterfox", "waterfox.exe") }
            });
            specs.Add(new BrowserSpec
            {
                Id = "zen", Name = "Zen Browser", Engine = "gecko", Color = "#A78BFA",
                ProcessNames = { "zen.exe" },
                RoamingRoots = { Path.Combine(roam, "zen") },
                LocalRoots = { Path.Combine(local, "zen") },
                ExeCandidates =
                {
                    Path.Combine(pf, "Zen Browser", "zen.exe"),
                    Path.Combine(local, "Programs", "Zen Browser", "zen.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "floorp", Name = "Floorp", Engine = "gecko", Color = "#6366F1",
                ProcessNames = { "floorp.exe" },
                RoamingRoots = { Path.Combine(roam, "Floorp") },
                LocalRoots = { Path.Combine(local, "Floorp") },
                ExeCandidates = { Path.Combine(pf, "Ablaze Floorp", "floorp.exe") }
            });
            specs.Add(new BrowserSpec
            {
                Id = "palemoon", Name = "Pale Moon", Engine = "gecko", Color = "#3B82F6",
                ProcessNames = { "palemoon.exe" },
                RoamingRoots = { Path.Combine(roam, "Moonchild Productions", "Pale Moon") },
                LocalRoots = { Path.Combine(local, "Moonchild Productions", "Pale Moon") },
                ExeCandidates =
                {
                    Path.Combine(pf, "Pale Moon", "palemoon.exe"),
                    Path.Combine(pf86, "Pale Moon", "palemoon.exe")
                }
            });
            specs.Add(new BrowserSpec
            {
                Id = "yandex", Name = "Yandex Browser", Engine = "chromium", Color = "#FACC15",
                ProcessNames = { "browser.exe" },
                UserDataDirs = { Path.Combine(local, "Yandex", "YandexBrowser", "User Data") },
                ExeCandidates = { Path.Combine(local, "Yandex", "YandexBrowser", "Application", "browser.exe") }
            });
            specs.Add(new BrowserSpec
            {
                Id = "comet", Name = "Perplexity Comet", Engine = "chromium", Color = "#20B8CD",
                ProcessNames = { "comet.exe" },
                UserDataDirs =
                {
                    Path.Combine(local, "Perplexity", "Comet", "User Data"),
                    Path.Combine(local, "Comet", "User Data")
                },
                ExeCandidates = { Path.Combine(local, "Perplexity", "Comet", "Application", "comet.exe") }
            });
            specs.Add(new BrowserSpec
            {
                Id = "ie_legacy", Name = "Internet Explorer / Windows WebCache", Engine = "ie", Color = "#38BDF8",
                ProcessNames = { "iexplore.exe" },
                IeDirs = new Dictionary<string, List<string>>
                {
                    { "history", new List<string> { Path.Combine(local, "Microsoft", "Windows", "INetHistory"), Path.Combine(local, "Microsoft", "Windows", "History") } },
                    { "cache", new List<string> { Path.Combine(local, "Microsoft", "Windows", "INetCache"), Path.Combine(local, "Microsoft", "Windows", "Temporary Internet Files") } },
                    { "cookies", new List<string> { Path.Combine(roam, "Microsoft", "Windows", "Cookies") } }
                },
                ExeCandidates = { Path.Combine(pf, "Internet Explorer", "iexplore.exe") }
            });

            string pkgRoot = Path.Combine(local, "Packages");
            if (Directory.Exists(pkgRoot))
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(pkgRoot, "TheBrowserCompany.Arc_*"))
                    {
                        string ud = Path.Combine(dir, "LocalCache", "Local", "Arc", "User Data");
                        if (Directory.Exists(ud))
                        {
                            specs.Add(new BrowserSpec
                            {
                                Id = "arc", Name = "Arc Browser", Engine = "chromium", Color = "#F472B6",
                                ProcessNames = { "Arc.exe" }, UserDataDirs = { ud }, ExeCandidates = { dir }
                            });
                        }
                    }
                    foreach (string dir in Directory.GetDirectories(pkgRoot, "DuckDuckGo.DesktopBrowser_*"))
                    {
                        string ud = Path.Combine(dir, "LocalState", "EBWebView");
                        if (Directory.Exists(ud))
                        {
                            specs.Add(new BrowserSpec
                            {
                                Id = "duckduckgo", Name = "DuckDuckGo Browser", Engine = "chromium", Color = "#DE5833",
                                ProcessNames = { "DuckDuckGo.exe" }, UserDataDirs = { ud }, ExeCandidates = { dir }
                            });
                        }
                    }
                }
                catch { }
            }

            return specs;
        }

        private static Dictionary<string, Tuple<string, string>> GetRegisteredBrowsersFromRegistry()
        {
            Dictionary<string, Tuple<string, string>> found = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
            Tuple<RegistryHive, RegistryView, string>[] roots = new Tuple<RegistryHive, RegistryView, string>[]
            {
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Clients\StartMenuInternet"),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Clients\StartMenuInternet"),
                Tuple.Create(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Clients\StartMenuInternet")
            };
            foreach (Tuple<RegistryHive, RegistryView, string> r in roots)
            {
                try
                {
                    using (RegistryKey baseK = RegistryKey.OpenBaseKey(r.Item1, r.Item2))
                    using (RegistryKey k = baseK.OpenSubKey(r.Item3))
                    {
                        if (k == null) continue;
                        foreach (string cname in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey bk = k.OpenSubKey(cname))
                                {
                                    if (bk == null) continue;
                                    object dispObj = bk.GetValue("");
                                    string disp = dispObj != null ? dispObj.ToString() : cname;
                                    string exePath = "";
                                    using (RegistryKey ck = bk.OpenSubKey(@"shell\open\command"))
                                    {
                                        if (ck != null)
                                        {
                                            object cmdObj = ck.GetValue("");
                                            if (cmdObj != null)
                                            {
                                                exePath = cmdObj.ToString().Trim().Trim('"').Split('"')[0];
                                            }
                                        }
                                    }
                                    found[cname] = Tuple.Create(disp, exePath);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return found;
        }

        public static List<Dictionary<string, object>> GetRunningBrowserProcesses()
        {
            HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe",
                "vivaldi.exe", "librewolf.exe", "waterfox.exe", "floorp.exe", "zen.exe",
                "palemoon.exe", "thorium.exe", "comet.exe", "Arc.exe", "DuckDuckGo.exe", "iexplore.exe"
            };
            List<Dictionary<string, object>> running = new List<Dictionary<string, object>>();
            string runtimeMarker = Config.RuntimeProfileDir.ToLowerInvariant();

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId, Name, CommandLine FROM Win32_Process"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"] != null ? obj["Name"].ToString() : "";
                        if (!targets.Contains(name)) continue;
                        string cmd = obj["CommandLine"] != null ? obj["CommandLine"].ToString() : "";
                        string cmdLow = cmd.ToLowerInvariant();
                        // Never touch our own isolated Edge app window!
                        if (cmdLow.Contains(runtimeMarker) || cmdLow.Contains("kennypcoptimizer")) continue;

                        int pid = Convert.ToInt32(obj["ProcessId"]);
                        running.Add(new Dictionary<string, object> { { "pid", pid }, { "name", name } });
                    }
                }
            }
            catch { }
            return running;
        }

        public static Dictionary<string, object> CloseRunningBrowsers(List<int> pids = null)
        {
            List<Dictionary<string, object>> procs = GetRunningBrowserProcesses();
            HashSet<int> filter = pids != null ? new HashSet<int>(pids) : null;
            int closed = 0;
            int failed = 0;

            foreach (Dictionary<string, object> p in procs)
            {
                int pid = Convert.ToInt32(p["pid"]);
                if (filter != null && !filter.Contains(pid)) continue;
                try
                {
                    Process pr = Process.GetProcessById(pid);
                    pr.Kill();
                    pr.WaitForExit(3000);
                    closed++;
                }
                catch
                {
                    failed++;
                }
            }
            Thread.Sleep(400);
            return new Dictionary<string, object>
            {
                { "closed_count", closed },
                { "failed_count", failed },
                { "remaining", GetRunningBrowserProcesses() }
            };
        }

        private static void StatPath(string path, out int fileCount, out long byteSize)
        {
            fileCount = 0;
            byteSize = 0;
            if (string.IsNullOrEmpty(path) || Config.IsProtectedPath(path)) return;
            try
            {
                if (File.Exists(path))
                {
                    fileCount = 1;
                    byteSize = new FileInfo(path).Length;
                    return;
                }
                if (!Directory.Exists(path)) return;

                Stack<DirectoryInfo> stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(path));
                while (stack.Count > 0)
                {
                    DirectoryInfo cur = stack.Pop();
                    if (Config.IsProtectedPath(cur.FullName) || (cur.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    try
                    {
                        foreach (FileInfo fi in cur.GetFiles())
                        {
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            try { fileCount++; byteSize += fi.Length; } catch { }
                        }
                        foreach (DirectoryInfo sub in cur.GetDirectories())
                        {
                            if (!Config.IsProtectedPath(sub.FullName) && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                                stack.Push(sub);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static List<string> FindChromiumProfiles(string userDataDir, bool flat)
        {
            List<string> profiles = new List<string>();
            if (!Directory.Exists(userDataDir)) return profiles;
            if (flat)
            {
                foreach (string m in ChromiumMarkers)
                {
                    if (File.Exists(Path.Combine(userDataDir, m)) || Directory.Exists(Path.Combine(userDataDir, m)))
                    {
                        profiles.Add(userDataDir);
                        break;
                    }
                }
                return profiles;
            }
            try
            {
                foreach (string child in Directory.GetDirectories(userDataDir))
                {
                    string name = Path.GetFileName(child);
                    if (name == "Default" || name.StartsWith("Profile ") || name == "Guest Profile")
                    {
                        foreach (string m in ChromiumMarkers)
                        {
                            if (File.Exists(Path.Combine(child, m)) || Directory.Exists(Path.Combine(child, m)))
                            {
                                profiles.Add(child);
                                break;
                            }
                        }
                    }
                }
            }
            catch { }
            return profiles;
        }

        private sealed class GeckoPair
        {
            public string Name;
            public string Roaming;
            public string Local;
        }

        private static List<GeckoPair> FindGeckoProfiles(string roamingRoot, string localRoot)
        {
            Dictionary<string, GeckoPair> pairs = new Dictionary<string, GeckoPair>(StringComparer.OrdinalIgnoreCase);
            foreach (Tuple<string, string> kindRoot in new Tuple<string, string>[] { Tuple.Create("roaming", roamingRoot), Tuple.Create("local", localRoot) })
            {
                if (string.IsNullOrEmpty(kindRoot.Item2) || !Directory.Exists(kindRoot.Item2)) continue;
                string profParent = Path.Combine(kindRoot.Item2, "Profiles");
                if (!Directory.Exists(profParent)) continue;
                try
                {
                    foreach (string dir in Directory.GetDirectories(profParent))
                    {
                        string nm = Path.GetFileName(dir);
                        GeckoPair gp;
                        if (!pairs.TryGetValue(nm, out gp))
                        {
                            gp = new GeckoPair { Name = nm };
                            pairs[nm] = gp;
                        }
                        if (kindRoot.Item1 == "roaming") gp.Roaming = dir;
                        else gp.Local = dir;
                    }
                }
                catch { }
            }
            return new List<GeckoPair>(pairs.Values);
        }

        public static Dictionary<string, object> ScanBrowsers(Dictionary<string, string> envOverrides = null)
        {
            List<BrowserSpec> specs = BuildSpecs(envOverrides);
            Dictionary<string, Tuple<string, string>> regBrowsers = envOverrides == null
                ? GetRegisteredBrowsersFromRegistry()
                : new Dictionary<string, Tuple<string, string>>();
            List<Dictionary<string, object>> runningProcs = envOverrides == null
                ? GetRunningBrowserProcesses()
                : new List<Dictionary<string, object>>();

            List<Dictionary<string, object>> browsersOut = new List<Dictionary<string, object>>();
            long totalBytes = 0;
            int totalFiles = 0;
            List<Dictionary<string, string>> criticalBackupCandidates = new List<Dictionary<string, string>>();

            foreach (BrowserSpec spec in specs)
            {
                bool installed = false;
                foreach (string exe in spec.ExeCandidates)
                {
                    if (File.Exists(exe) || Directory.Exists(exe)) { installed = true; break; }
                }
                if (!installed)
                {
                    string firstWord = spec.Name.Split(' ')[0].ToLowerInvariant();
                    foreach (KeyValuePair<string, Tuple<string, string>> kv in regBrowsers)
                    {
                        if (kv.Key.ToLowerInvariant().Contains(spec.Id) || kv.Value.Item1.ToLowerInvariant().Contains(firstWord))
                        {
                            if (!string.IsNullOrEmpty(kv.Value.Item2) && File.Exists(kv.Value.Item2))
                            {
                                installed = true;
                                break;
                            }
                        }
                    }
                }

                HashSet<string> procSet = new HashSet<string>(spec.ProcessNames, StringComparer.OrdinalIgnoreCase);
                List<int> matchingPids = new List<int>();
                foreach (Dictionary<string, object> rp in runningProcs)
                {
                    if (procSet.Contains(rp["name"].ToString())) matchingPids.Add(Convert.ToInt32(rp["pid"]));
                }
                bool isRunning = matchingPids.Count > 0;

                Dictionary<string, Dictionary<string, object>> categories = new Dictionary<string, Dictionary<string, object>>
                {
                    { "history", new Dictionary<string, object> { { "label", "Browsing History & Navigation" }, { "items", new List<Dictionary<string, object>>() }, { "file_count", 0 }, { "size_bytes", 0L }, { "notes", new List<string>() } } },
                    { "cache", new Dictionary<string, object> { { "label", "Caches & GPU Shaders" }, { "items", new List<Dictionary<string, object>>() }, { "file_count", 0 }, { "size_bytes", 0L }, { "notes", new List<string>() } } },
                    { "leftovers", new Dictionary<string, object> { { "label", "Leftover, Crash & Unused Files" }, { "items", new List<Dictionary<string, object>>() }, { "file_count", 0 }, { "size_bytes", 0L }, { "notes", new List<string>() } } },
                    { "cookies", new Dictionary<string, object> { { "label", "Cookies & Site Storage (Opt-In)" }, { "items", new List<Dictionary<string, object>>() }, { "file_count", 0 }, { "size_bytes", 0L }, { "notes", new List<string>() } } }
                };

                List<string> profileNames = new List<string>();
                bool hasAnyData = false;

                Action<string, string, string> addItem = delegate(string cat, string pathObj, string action)
                {
                    if ((!File.Exists(pathObj) && !Directory.Exists(pathObj)) || Config.IsProtectedPath(pathObj)) return;
                    int fc;
                    long sb;
                    StatPath(pathObj, out fc, out sb);
                    if (fc == 0 && sb == 0 && action == "delete" && !Directory.Exists(pathObj)) return;
                    hasAnyData = true;
                    Dictionary<string, object> cDict = categories[cat];
                    List<Dictionary<string, object>> itemsList = (List<Dictionary<string, object>>)cDict["items"];
                    itemsList.Add(new Dictionary<string, object>
                    {
                        { "path", pathObj },
                        { "name", Path.GetFileName(pathObj) },
                        { "action", action },
                        { "file_count", fc },
                        { "size_bytes", sb },
                        { "size_human", SystemInfo.FormatBytes(sb) }
                    });
                    cDict["file_count"] = Convert.ToInt32(cDict["file_count"]) + fc;
                    cDict["size_bytes"] = Convert.ToInt64(cDict["size_bytes"]) + sb;
                };

                if (spec.Engine == "chromium")
                {
                    foreach (string ud in spec.UserDataDirs)
                    {
                        if (!Directory.Exists(ud)) continue;
                        hasAnyData = true;
                        foreach (string rel in ChromiumRootCacheDirs) addItem("cache", Path.Combine(ud, rel), "delete");
                        foreach (string rel in ChromiumRootLeftoverDirs) addItem("leftovers", Path.Combine(ud, rel), "delete");
                        foreach (string pat in new string[] { "*.pma", "*.tmp", "*.log", "*.old" })
                        {
                            try { foreach (string f in Directory.GetFiles(ud, pat)) addItem("leftovers", f, "delete"); } catch { }
                        }

                        foreach (string prof in FindChromiumProfiles(ud, spec.FlatProfile))
                        {
                            string profLabel = spec.FlatProfile ? Path.GetFileName(ud) : Path.GetFileName(prof);
                            if (!profileNames.Contains(profLabel)) profileNames.Add(profLabel);
                            foreach (string rel in ChromiumHistoryFiles)
                            {
                                string fp = Path.Combine(prof, rel);
                                addItem("history", fp, "delete");
                                if (File.Exists(fp) && Path.GetFileName(fp) == "History")
                                {
                                    criticalBackupCandidates.Add(new Dictionary<string, string>
                                    {
                                        { "path", fp },
                                        { "category", string.Format("browser_{0}_history", spec.Id) }
                                    });
                                }
                            }
                            foreach (string rel in ChromiumCacheDirs) addItem("cache", Path.Combine(prof, rel), "delete");
                            foreach (string pat in new string[] { "*.tmp", "*.log", "*.old" })
                            {
                                try { foreach (string f in Directory.GetFiles(prof, pat)) addItem("leftovers", f, "delete"); } catch { }
                            }
                            foreach (string rel in ChromiumCookiesItems) addItem("cookies", Path.Combine(prof, rel), "delete");
                        }
                    }
                }
                else if (spec.Engine == "gecko")
                {
                    string roamR = spec.RoamingRoots.Count > 0 ? spec.RoamingRoots[0] : null;
                    string localR = spec.LocalRoots.Count > 0 ? spec.LocalRoots[0] : null;
                    if ((!string.IsNullOrEmpty(roamR) && Directory.Exists(roamR)) || (!string.IsNullOrEmpty(localR) && Directory.Exists(localR)))
                        hasAnyData = true;

                    foreach (GeckoPair gp in FindGeckoProfiles(roamR, localR))
                    {
                        profileNames.Add(gp.Name);
                        if (!string.IsNullOrEmpty(gp.Roaming))
                        {
                            string places = Path.Combine(gp.Roaming, "places.sqlite");
                            if (File.Exists(places))
                            {
                                long visits, bms;
                                SqliteHelper.InspectGeckoPlaces(places, out visits, out bms);
                                long sz = new FileInfo(places).Length;
                                criticalBackupCandidates.Add(new Dictionary<string, string>
                                {
                                    { "path", places },
                                    { "category", string.Format("browser_{0}_places", spec.Id) }
                                });
                                Dictionary<string, object> hCat = categories["history"];
                                ((List<Dictionary<string, object>>)hCat["items"]).Add(new Dictionary<string, object>
                                {
                                    { "path", places },
                                    { "name", string.Format("places.sqlite ({0} visits, {1} bookmarks preserved)", visits, bms) },
                                    { "action", "scrub_gecko_places" },
                                    { "file_count", visits > 0 ? 1 : 0 },
                                    { "visits", visits },
                                    { "bookmarks", bms },
                                    { "size_bytes", visits > 0 ? sz : 0L },
                                    { "size_human", string.Format("{0} visits", visits) }
                                });
                                if (visits > 0)
                                {
                                    hCat["file_count"] = Convert.ToInt32(hCat["file_count"]) + 1;
                                    ((List<string>)hCat["notes"]).Add(string.Format("Bookmark-Safe SQLite Scrub ({0} bookmarks preserved)", bms));
                                }
                            }
                            foreach (string rel in GeckoHistoryFiles) addItem("history", Path.Combine(gp.Roaming, rel), "delete");
                            foreach (string rel in GeckoLeftoverItems) addItem("leftovers", Path.Combine(gp.Roaming, rel), "delete");
                            foreach (string pat in new string[] { "*.tmp", "*.corrupt", "*.bak" })
                            {
                                try { foreach (string f in Directory.GetFiles(gp.Roaming, pat)) addItem("leftovers", f, "delete"); } catch { }
                            }
                            foreach (string rel in GeckoCookiesItems) addItem("cookies", Path.Combine(gp.Roaming, rel), "delete");
                        }
                        if (!string.IsNullOrEmpty(gp.Local))
                        {
                            foreach (string rel in GeckoCacheDirs) addItem("cache", Path.Combine(gp.Local, rel), "delete");
                            foreach (string pat in new string[] { "*.tmp", "*.log" })
                            {
                                try { foreach (string f in Directory.GetFiles(gp.Local, pat)) addItem("leftovers", f, "delete"); } catch { }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(roamR) && Directory.Exists(Path.Combine(roamR, "Crash Reports")))
                        addItem("leftovers", Path.Combine(roamR, "Crash Reports"), "delete");
                    foreach (string ex in spec.ExtraLeftoverDirs) addItem("leftovers", ex, "delete");
                }
                else if (spec.Engine == "ie")
                {
                    foreach (string catName in new string[] { "history", "cache", "cookies" })
                    {
                        List<string> dirs;
                        if (spec.IeDirs.TryGetValue(catName, out dirs))
                        {
                            foreach (string d in dirs) addItem(catName, d, "clean_dir_contents");
                        }
                    }
                }

                if (!installed && !hasAnyData) continue;

                long bTotalBytes = 0;
                int bTotalFiles = 0;
                foreach (KeyValuePair<string, Dictionary<string, object>> kv in categories)
                {
                    long sz = Convert.ToInt64(kv.Value["size_bytes"]);
                    kv.Value["size_human"] = SystemInfo.FormatBytes(sz);
                    if (kv.Key != "cookies")
                    {
                        bTotalBytes += sz;
                        bTotalFiles += Convert.ToInt32(kv.Value["file_count"]);
                    }
                }

                totalBytes += bTotalBytes;
                totalFiles += bTotalFiles;
                string status = isRunning ? "running" : (installed ? "installed" : "leftover");

                browsersOut.Add(new Dictionary<string, object>
                {
                    { "id", spec.Id },
                    { "name", spec.Name },
                    { "engine", spec.Engine },
                    { "color", spec.Color },
                    { "installed", installed },
                    { "is_running", isRunning },
                    { "running_pids", matchingPids },
                    { "status", status },
                    { "status_label", isRunning ? "Running" : (installed ? "Installed" : "Leftover (Uninstalled)") },
                    { "profiles", profileNames },
                    { "categories", categories },
                    { "recommended_bytes", bTotalBytes },
                    { "recommended_human", SystemInfo.FormatBytes(bTotalBytes) },
                    { "recommended_files", bTotalFiles }
                });
            }

            return new Dictionary<string, object>
            {
                { "browsers", browsersOut },
                { "browser_count", browsersOut.Count },
                { "running_browsers", runningProcs },
                { "total_bytes", totalBytes },
                { "total_human", SystemInfo.FormatBytes(totalBytes) },
                { "total_files", totalFiles },
                { "critical_backup_candidates", criticalBackupCandidates }
            };
        }

        private static void DeleteItemSafely(string path, bool dryRun, bool contentsOnly, out int deletedFiles, out long freedBytes, out int skippedLocked)
        {
            deletedFiles = 0;
            freedBytes = 0;
            skippedLocked = 0;
            if (string.IsNullOrEmpty(path) || Config.IsProtectedPath(path)) return;
            if (File.Exists(path))
            {
                try
                {
                    long sz = new FileInfo(path).Length;
                    if (!dryRun)
                    {
                        FileAttributes attrs = File.GetAttributes(path);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                        File.Delete(path);
                    }
                    deletedFiles = 1;
                    freedBytes = sz;
                }
                catch { skippedLocked = 1; }
                return;
            }
            if (Directory.Exists(path))
            {
                SystemCleaner.CleanDirectoryContents(path, null, dryRun, !contentsOnly, out deletedFiles, out freedBytes, out skippedLocked);
            }
        }

        public static Dictionary<string, object> CleanBrowsers(
            Dictionary<string, List<string>> browserSelections,
            bool dryRun,
            Dictionary<string, string> envOverrides = null,
            LogCallback logCb = null)
        {
            LogCallback log = delegate(string m, string l) { if (logCb != null) logCb(m, l); };
            Dictionary<string, object> scan = ScanBrowsers(envOverrides);
            List<Dictionary<string, object>> list = (List<Dictionary<string, object>>)scan["browsers"];
            Dictionary<string, Dictionary<string, object>> byId = new Dictionary<string, Dictionary<string, object>>();
            foreach (Dictionary<string, object> b in list) byId[b["id"].ToString()] = b;

            long totalFreed = 0;
            int totalFiles = 0;
            int totalLocked = 0;
            List<Dictionary<string, object>> perBrowser = new List<Dictionary<string, object>>();
            string prefix = dryRun ? "[DRY-RUN] " : "";

            foreach (KeyValuePair<string, List<string>> kv in browserSelections)
            {
                Dictionary<string, object> b;
                if (!byId.TryGetValue(kv.Key, out b) || kv.Value == null || kv.Value.Count == 0) continue;

                string bName = b["name"].ToString();
                log(string.Format("{0}Cleaning {1} ({2})...", prefix, bName, string.Join(", ", kv.Value.ToArray())), "info");
                long bFreed = 0;
                int bFiles = 0;
                int bLocked = 0;
                List<string> bNotes = new List<string>();

                Dictionary<string, Dictionary<string, object>> cats = (Dictionary<string, Dictionary<string, object>>)b["categories"];
                foreach (string ck in kv.Value)
                {
                    Dictionary<string, object> cat;
                    if (!cats.TryGetValue(ck, out cat)) continue;
                    List<Dictionary<string, object>> items = (List<Dictionary<string, object>>)cat["items"];
                    foreach (Dictionary<string, object> item in items)
                    {
                        string action = item["action"].ToString();
                        string p = item["path"].ToString();
                        if (action == "scrub_gecko_places")
                        {
                            Dictionary<string, object> sRes = SqliteHelper.ScrubGeckoPlacesPreservingBookmarks(p, dryRun);
                            if (Convert.ToBoolean(sRes["ok"]))
                            {
                                long v = Convert.ToInt64(sRes["visits_removed"]);
                                long bm = Convert.ToInt64(sRes["bookmarks_preserved"]);
                                long fb = Convert.ToInt64(sRes["freed_bytes"]);
                                bFreed += fb;
                                if (v > 0) bFiles++;
                                string note = string.Format("Scrubbed {0} history visit(s) in {1}, preserved {2} bookmark(s).",
                                    v, Path.GetFileName(Path.GetDirectoryName(p)), bm);
                                bNotes.Add(note);
                                log("  " + prefix + note, "success");
                            }
                            else
                            {
                                bLocked++;
                                log(string.Format("  Skipped {0} ({1})", Path.GetFileName(p), sRes.ContainsKey("error") ? sRes["error"] : "locked"), "warn");
                            }
                        }
                        else
                        {
                            int df, sl;
                            long fb;
                            DeleteItemSafely(p, dryRun, action == "clean_dir_contents", out df, out fb, out sl);
                            bFreed += fb;
                            bFiles += df;
                            bLocked += sl;
                        }
                    }
                }

                totalFreed += bFreed;
                totalFiles += bFiles;
                totalLocked += bLocked;
                log(string.Format("  {0}{1}: {2} item(s) cleaned ({3}){4}",
                    prefix, bName, bFiles, SystemInfo.FormatBytes(bFreed),
                    bLocked > 0 ? string.Format(", {0} locked skipped", bLocked) : ""),
                    "success");

                perBrowser.Add(new Dictionary<string, object>
                {
                    { "id", kv.Key },
                    { "name", bName },
                    { "deleted_files", bFiles },
                    { "freed_bytes", bFreed },
                    { "freed_human", SystemInfo.FormatBytes(bFreed) },
                    { "skipped_locked", bLocked },
                    { "notes", bNotes }
                });
            }

            return new Dictionary<string, object>
            {
                { "dry_run", dryRun },
                { "results", perBrowser },
                { "total_freed_bytes", totalFreed },
                { "total_freed_human", SystemInfo.FormatBytes(totalFreed) },
                { "total_deleted_files", totalFiles },
                { "total_skipped_locked", totalLocked }
            };
        }
    }
}
