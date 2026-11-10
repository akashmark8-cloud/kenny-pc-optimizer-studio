using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KennyPCOptimizer.Tests
{
    static class TestRunner
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr cb, IntPtr arg, out IntPtr err);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr ptr);

        private static void Assert(bool cond, string msg)
        {
            if (!cond) throw new Exception("ASSERTION FAILED: " + msg);
        }

        private static void ExecSqlite(string dbPath, string sql)
        {
            IntPtr db;
            byte[] pBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
            int rc = sqlite3_open_v2(pBytes, out db, 0x00000006, IntPtr.Zero); // RW | CREATE
            Assert(rc == 0, "sqlite3_open_v2 failed: " + rc);
            try
            {
                IntPtr err;
                byte[] sBytes = Encoding.UTF8.GetBytes(sql + "\0");
                int rc2 = sqlite3_exec(db, sBytes, IntPtr.Zero, IntPtr.Zero, out err);
                if (err != IntPtr.Zero) sqlite3_free(err);
                Assert(rc2 == 0, "sqlite3_exec failed: " + rc2);
            }
            finally
            {
                sqlite3_close(db);
            }
        }

        private static void TestSystemCleaner()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "kenny_cs_sys_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string local = Path.Combine(tmp, "Local");
                string tempDir = Path.Combine(tmp, "UserTemp");
                string winRoot = Path.Combine(tmp, "Windows");
                string steamLib = Path.Combine(tmp, "SteamLib");

                Directory.CreateDirectory(Path.Combine(tempDir, "nested"));
                File.WriteAllBytes(Path.Combine(tempDir, "a.tmp"), new byte[1024]);
                File.WriteAllBytes(Path.Combine(tempDir, "nested", "b.log"), new byte[2048]);

                string dx = Path.Combine(local, "NVIDIA", "DXCache");
                Directory.CreateDirectory(dx);
                File.WriteAllBytes(Path.Combine(dx, "shader.bin"), new byte[4096]);

                string cs2 = Path.Combine(steamLib, "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin", "win64");
                Directory.CreateDirectory(cs2);
                File.WriteAllBytes(Path.Combine(cs2, "crash_1.mdmp"), new byte[3000]);
                File.WriteAllText(Path.Combine(cs2, "cs2.exe"), "keep");

                string pubg = Path.Combine(local, "TslGame", "Saved", "Logs");
                Directory.CreateDirectory(pubg);
                File.WriteAllBytes(Path.Combine(pubg, "old.log"), new byte[800]);
                File.WriteAllText(Path.Combine(pubg, "settings.ini"), "keep");

                Dictionary<string, object> ov = new Dictionary<string, object>
                {
                    { "LOCALAPPDATA", local },
                    { "TEMP", tempDir },
                    { "SystemRoot", winRoot },
                    { "STEAM_LIBS", new List<string> { steamLib } }
                };

                Dictionary<string, object> scan = SystemCleaner.ScanSystemTargets(ov);
                List<Dictionary<string, object>> targets = (List<Dictionary<string, object>>)scan["targets"];
                Dictionary<string, Dictionary<string, object>> byId = new Dictionary<string, Dictionary<string, object>>();
                foreach (Dictionary<string, object> t in targets) byId[t["id"].ToString()] = t;

                Assert(Convert.ToInt32(byId["user_temp"]["file_count"]) == 2, "user_temp file_count");
                Assert(Convert.ToInt64(byId["nvidia_dxcache"]["size_bytes"]) == 4096L, "nvidia_dxcache size");
                Assert(Convert.ToInt32(byId["game_dumps"]["file_count"]) == 2, "game_dumps count");

                SystemCleaner.CleanSystemTargets(new List<string> { "user_temp" }, true, ov);
                Assert(File.Exists(Path.Combine(tempDir, "a.tmp")), "dry run must keep files");

                Dictionary<string, object> live = SystemCleaner.CleanSystemTargets(
                    new List<string> { "user_temp", "nvidia_dxcache", "game_dumps" }, false, ov);
                Assert(!File.Exists(Path.Combine(tempDir, "a.tmp")), "user_temp cleaned");
                Assert(!File.Exists(Path.Combine(dx, "shader.bin")), "dxcache cleaned");
                Assert(File.Exists(Path.Combine(cs2, "cs2.exe")), "cs2.exe preserved");
                Assert(File.Exists(Path.Combine(pubg, "settings.ini")), "pubg ini preserved");
                Assert(Convert.ToInt64(live["total_freed_bytes"]) >= 10000L, "freed bytes count");
                Console.WriteLine("[PASS] TestSystemCleaner");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        private static void TestBrowserCleaner()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "kenny_cs_br_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string local = Path.Combine(tmp, "Local");
                string roam = Path.Combine(tmp, "Roaming");
                string pf = Path.Combine(tmp, "PF");

                string braveProf = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default");
                Directory.CreateDirectory(Path.Combine(braveProf, "Cache"));
                File.WriteAllText(Path.Combine(braveProf, "Preferences"), "{}");
                File.WriteAllBytes(Path.Combine(braveProf, "History"), new byte[1500]);
                File.WriteAllBytes(Path.Combine(braveProf, "Cache", "img.bin"), new byte[5000]);
                File.WriteAllText(Path.Combine(braveProf, "Cookies"), "KEEP");

                string ffRoam = Path.Combine(roam, "Mozilla", "Firefox", "Profiles", "testprof.default-release");
                string ffLocal = Path.Combine(local, "Mozilla", "Firefox", "Profiles", "testprof.default-release");
                Directory.CreateDirectory(ffRoam);
                Directory.CreateDirectory(Path.Combine(ffLocal, "cache2"));
                File.WriteAllBytes(Path.Combine(ffLocal, "cache2", "c1"), new byte[3000]);

                string placesDb = Path.Combine(ffRoam, "places.sqlite");
                ExecSqlite(placesDb,
                    "CREATE TABLE moz_origins (id INTEGER PRIMARY KEY, host TEXT);" +
                    "CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, visit_count INTEGER, typed INTEGER, last_visit_date INTEGER, foreign_count INTEGER, origin_id INTEGER);" +
                    "CREATE TABLE moz_historyvisits (id INTEGER PRIMARY KEY, place_id INTEGER);" +
                    "CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, fk INTEGER, title TEXT);" +
                    "INSERT INTO moz_origins VALUES (1, 'example.com'), (2, 'bookmark.org');" +
                    "INSERT INTO moz_places VALUES (1, 'https://example.com', 5, 1, 123, 0, 1), (2, 'https://bookmark.org', 3, 0, 123, 1, 2);" +
                    "INSERT INTO moz_historyvisits VALUES (1, 1), (2, 1), (3, 2);" +
                    "INSERT INTO moz_bookmarks VALUES (1, 2, 'Keep Bookmark');");

                Dictionary<string, string> envOv = new Dictionary<string, string>
                {
                    { "LOCALAPPDATA", local },
                    { "APPDATA", roam },
                    { "ProgramFiles", pf },
                    { "ProgramFiles(x86)", pf }
                };

                Dictionary<string, object> scan = BrowserCleaner.ScanBrowsers(envOv);
                Assert(Convert.ToInt32(scan["browser_count"]) >= 2, "detected brave and firefox");

                BrowserCleaner.CleanBrowsers(
                    new Dictionary<string, List<string>>
                    {
                        { "brave", new List<string> { "history", "cache", "leftovers" } },
                        { "firefox", new List<string> { "history", "cache", "leftovers" } }
                    },
                    false, envOv);

                Assert(!File.Exists(Path.Combine(braveProf, "History")), "brave history deleted");
                Assert(!File.Exists(Path.Combine(braveProf, "Cache", "img.bin")), "brave cache deleted");
                Assert(File.Exists(Path.Combine(braveProf, "Cookies")), "cookies preserved");
                Assert(File.Exists(placesDb), "places.sqlite preserved");

                long visits, bms;
                SqliteHelper.InspectGeckoPlaces(placesDb, out visits, out bms);
                Assert(visits == 0, "firefox history visits scrubbed to 0");
                Assert(bms == 1, "firefox bookmarks preserved");
                Console.WriteLine("[PASS] TestBrowserCleaner (with native winsqlite3 bookmark preservation)");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        private static void TestRegistryAndBackupRestore()
        {
            string tag = "_KennyCSharpTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string baseSub = "Software\\" + tag;
            string tmpBkp = Path.Combine(Path.GetTempPath(), "kenny_cs_bkp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpBkp);

            try
            {
                Registry.CurrentUser.CreateSubKey(baseSub + "\\EmptySubtree\\ChildA\\Leaf1").Close();
                Registry.CurrentUser.CreateSubKey(baseSub + "\\EmptySubtree\\ChildB").Close();
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(baseSub + "\\NonEmptyNamed"))
                    k.SetValue("Keep", "val");
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(baseSub + "\\NonEmptyDefault"))
                    k.SetValue("", "defval");

                Dictionary<string, object> scan = RegistryCleaner.ScanEmptyRegistryKeys("HKCU", baseSub);
                List<Dictionary<string, object>> keys = (List<Dictionary<string, object>>)scan["keys"];
                Assert(keys.Count == 4, "expected 4 empty keys, got " + keys.Count);

                string sampleOrigin = Path.Combine(tmpBkp, "origin_dir", "History");
                Directory.CreateDirectory(Path.GetDirectoryName(sampleOrigin));
                File.WriteAllText(sampleOrigin, "ORIGINAL_HISTORY_BYTES");

                List<string> surgicalPaths = new List<string>();
                foreach (Dictionary<string, object> k in keys) surgicalPaths.Add(k["full_path"].ToString());

                Dictionary<string, object> manifest = BackupManager.CreateBackupSnapshot(
                    "CSharpUnitTest",
                    false,
                    surgicalPaths,
                    new List<Dictionary<string, string>> { new Dictionary<string, string> { { "path", sampleOrigin }, { "category", "test_hist" } } },
                    false,
                    tmpBkp,
                    null
                );
                string snapId = manifest["snapshot_id"].ToString();
                Dictionary<string, object> ver = BackupManager.VerifyBackup(snapId, tmpBkp);
                Assert(Convert.ToBoolean(ver["verified"]), "backup SHA-256 verification passed");

                Dictionary<string, object> cleanRes = RegistryCleaner.CleanEmptyRegistryKeys(keys, false, true, null);
                Assert(Convert.ToInt32(cleanRes["deleted_count"]) == 4, "deleted 4 empty keys");
                Assert(Registry.CurrentUser.OpenSubKey(baseSub + "\\EmptySubtree") == null, "EmptySubtree removed");
                Assert(Registry.CurrentUser.OpenSubKey(baseSub + "\\NonEmptyNamed").GetValue("Keep").ToString() == "val", "NonEmptyNamed kept");
                Assert(Registry.CurrentUser.OpenSubKey(baseSub + "\\NonEmptyDefault").GetValue("").ToString() == "defval", "NonEmptyDefault kept");

                Dictionary<string, object> restReg = BackupManager.RestoreRegistryFromBackup(snapId, "quick", tmpBkp);
                Assert(Convert.ToBoolean(restReg["ok"]), "registry restore ok");
                Assert(Registry.CurrentUser.OpenSubKey(baseSub + "\\EmptySubtree\\ChildA\\Leaf1") != null, "EmptySubtree restored in registry");

                File.Delete(sampleOrigin);
                Assert(!File.Exists(sampleOrigin), "sample file deleted");
                Dictionary<string, object> restFiles = BackupManager.RestoreFilesFromBackup(snapId, tmpBkp);
                Assert(Convert.ToBoolean(restFiles["ok"]), "file restore ok");
                Assert(File.ReadAllText(sampleOrigin) == "ORIGINAL_HISTORY_BYTES", "restored file content matches");

                Console.WriteLine("[PASS] TestRegistryAndBackupRestore");
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(baseSub, false); } catch { }
                try { Directory.Delete(tmpBkp, true); } catch { }
            }
        }

        private static void TestWebServer()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();

            using (WebServer srv = new WebServer(port, "tok999", null, null))
            {
                srv.Start();
                string baseUrl = "http://127.0.0.1:" + port;

                using (WebClient wc = new WebClient())
                {
                    string html = wc.DownloadString(baseUrl + "/");
                    Assert(html.Contains("KENNY STUDIO"), "embedded index.html served");
                    string css = wc.DownloadString(baseUrl + "/static/styles.css");
                    Assert(css.Contains(".app-shell"), "embedded styles.css served");
                    string js = wc.DownloadString(baseUrl + "/static/app.js");
                    Assert(js.Contains("SESSION_TOKEN"), "embedded app.js served");
                    string health = wc.DownloadString(baseUrl + "/api/health");
                    Assert(health.Contains("\"ok\":true"), "health ok");
                }

                // Auth guard check
                bool got401 = false;
                try
                {
                    using (WebClient wc = new WebClient()) wc.DownloadString(baseUrl + "/api/telemetry");
                }
                catch (WebException ex)
                {
                    HttpWebResponse r = ex.Response as HttpWebResponse;
                    if (r != null && r.StatusCode == HttpStatusCode.Unauthorized) got401 = true;
                }
                Assert(got401, "missing token must return 401");

                // Authenticated API endpoints
                using (WebClient wc = new WebClient())
                {
                    wc.Headers["X-Session-Token"] = "tok999";
                    string tele = wc.DownloadString(baseUrl + "/api/telemetry");
                    Assert(tele.Contains("\"drives\""), "telemetry returns drives");

                    wc.Headers["Content-Type"] = "application/json";
                    string dryResp = wc.UploadString(
                        baseUrl + "/api/clean/execute",
                        "{\"system_ids\":[\"user_temp\"],\"dry_run\":true,\"reason\":\"TestDryRun\"}"
                    );
                    Assert(dryResp.Contains("\"job_id\""), "clean execute returns job_id");

                    string memStatus = wc.DownloadString(baseUrl + "/api/memory/status");
                    Assert(memStatus.Contains("\"standby_bytes\""), "memory status returns standby_bytes");
                    wc.Headers["Content-Type"] = "application/json";
                    string memDry = wc.UploadString(baseUrl + "/api/memory/clean", "{\"areas\":[\"working_set\"],\"dry_run\":true}");
                    Assert(memDry.Contains("\"dry_run\":true"), "memory dry run returns ok");
                }
                Console.WriteLine("[PASS] TestWebServer (embedded UI + auth + API)");
            }
        }

        private static void TestMemoryOptimizer()
        {
            Dictionary<string, object> details = MemoryOptimizer.GetMemoryDetails(true);
            List<Dictionary<string, object>> areas = (List<Dictionary<string, object>>)details["areas"];
            Assert(areas.Count == 7, "expected 7 memory optimization areas");
            List<Dictionary<string, object>> procs = (List<Dictionary<string, object>>)details["top_processes"];
            Assert(procs.Count > 0, "expected top running processes list");

            Dictionary<string, object> dry = MemoryOptimizer.CleanMemory(new List<string> { "working_set", "low_standby" }, true, null);
            Assert(Convert.ToBoolean(dry["dry_run"]), "memory dry run flag");

            // Test selective single-process trim against a temporary dummy child process
            System.Diagnostics.Process child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command Start-Sleep -Seconds 10",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            try
            {
                Thread.Sleep(400);
                Dictionary<string, object> trimRes = MemoryOptimizer.TrimSingleProcess(child.Id);
                Assert(Convert.ToBoolean(trimRes["ok"]), "TrimSingleProcess succeeded on own child process");
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
            }
            Console.WriteLine("[PASS] TestMemoryOptimizer");
        }

        private static void TestSystemAdvisor()
        {
            string sampleWinget =
                "\\ \r" +
                "Name              Id                        Version  Available Source\n" +
                "---------------------------------------------------------------------\n" +
                "7-Zip 26.05 (x64) 7zip.7zip                 26.05    26.06     winget\n" +
                "OpenAL            CreativeTechnology.OpenAL Unknown  1.1       winget\n" +
                "2 upgrades available.\n\n" +
                "The following packages have an upgrade available, but require explicit targeting for upgrade:\n" +
                "Name    Id              Version  Available Source\n" +
                "-------------------------------------------------\n" +
                "Discord Discord.Discord 1.0.9267 1.0.9268  winget\n";

            List<Dictionary<string, object>> parsed = SystemAdvisor.ParseWingetUpgradeOutput(sampleWinget);
            Assert(parsed.Count == 3, "expected 3 parsed winget entries, got " + parsed.Count);
            Assert(parsed[0]["id"].ToString() == "7zip.7zip" && parsed[0]["available"].ToString() == "26.06", "7zip parsed");
            Assert(parsed[2]["id"].ToString() == "Discord.Discord" && Convert.ToBoolean(parsed[2]["explicit_only"]), "discord explicit parsed");

            Dictionary<string, object> scan = SystemAdvisor.ScanDriversAndApps(false, false);
            Assert(Convert.ToInt32(scan["driver_count"]) > 0, "detected key signed drivers");
            List<Dictionary<string, object>> ess = (List<Dictionary<string, object>>)scan["essentials"];
            Assert(ess.Count >= 8, "essentials catalog populated");
            Console.WriteLine("[PASS] TestSystemAdvisor");
        }

        static int Main()
        {
            TestSystemCleaner();
            TestBrowserCleaner();
            TestRegistryAndBackupRestore();
            TestMemoryOptimizer();
            TestSystemAdvisor();
            TestWebServer();
            Console.WriteLine("ALL 6 STANDALONE C# TEST SUITES PASSED.");
            return 0;
        }
    }
}
