using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KennyPCOptimizer
{
    public static class BackupManager
    {
        public static string GetBackupRoot()
        {
            Dictionary<string, object> settings = SystemInfo.LoadSettings();
            string root = settings.ContainsKey("backup_dir") && settings["backup_dir"] != null
                ? settings["backup_dir"].ToString()
                : Config.DefaultBackupDir;
            Directory.CreateDirectory(root);
            return root;
        }

        private static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static List<Dictionary<string, string>> CollectInstalledPrograms()
        {
            List<Dictionary<string, string>> items = new List<Dictionary<string, string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                                    object dispObj = sk.GetValue("DisplayName");
                                    if (dispObj == null) continue;
                                    string disp = dispObj.ToString();
                                    object verObj = sk.GetValue("DisplayVersion");
                                    object pubObj = sk.GetValue("Publisher");
                                    string ver = verObj != null ? verObj.ToString() : "";
                                    string pub = pubObj != null ? pubObj.ToString() : "";
                                    string keyId = disp + "|" + ver;
                                    if (seen.Add(keyId))
                                    {
                                        items.Add(new Dictionary<string, string>
                                        {
                                            { "name", disp },
                                            { "version", ver },
                                            { "publisher", pub }
                                        });
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            items.Sort(delegate(Dictionary<string, string> a, Dictionary<string, string> b)
            {
                return string.Compare(a["name"], b["name"], StringComparison.OrdinalIgnoreCase);
            });
            return items;
        }

        private static List<Dictionary<string, string>> CollectStartupEntries()
        {
            List<Dictionary<string, string>> entries = new List<Dictionary<string, string>>();
            Tuple<string, RegistryHive, RegistryView, string>[] locs = new Tuple<string, RegistryHive, RegistryView, string>[]
            {
                Tuple.Create(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                Tuple.Create(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                Tuple.Create(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")
            };

            foreach (Tuple<string, RegistryHive, RegistryView, string> loc in locs)
            {
                try
                {
                    using (RegistryKey baseK = RegistryKey.OpenBaseKey(loc.Item2, loc.Item3))
                    using (RegistryKey k = baseK.OpenSubKey(loc.Item4))
                    {
                        if (k == null) continue;
                        foreach (string valName in k.GetValueNames())
                        {
                            object val = k.GetValue(valName);
                            entries.Add(new Dictionary<string, string>
                            {
                                { "location", loc.Item1 },
                                { "name", valName },
                                { "command", val != null ? val.ToString() : "" }
                            });
                        }
                    }
                }
                catch { }
            }
            return entries;
        }

        public static string BuildSurgicalRegContent(List<string> regKeys)
        {
            List<string> sorted = new List<string>(regKeys);
            sorted.Sort(delegate(string a, string b)
            {
                int cmp = a.Length.CompareTo(b.Length);
                return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            StringBuilder sb = new StringBuilder();
            sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> hiveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "HKCU", "HKEY_CURRENT_USER" },
                { "HKEY_CURRENT_USER", "HKEY_CURRENT_USER" },
                { "HKLM", "HKEY_LOCAL_MACHINE" },
                { "HKEY_LOCAL_MACHINE", "HKEY_LOCAL_MACHINE" },
                { "HKCR", "HKEY_CLASSES_ROOT" },
                { "HKEY_CLASSES_ROOT", "HKEY_CLASSES_ROOT" },
                { "HKU", "HKEY_USERS" },
                { "HKEY_USERS", "HKEY_USERS" }
            };

            foreach (string raw in sorted)
            {
                string norm = (raw ?? "").Trim().TrimEnd('\\');
                if (string.IsNullOrEmpty(norm)) continue;
                int idx = norm.IndexOf('\\');
                string head = idx >= 0 ? norm.Substring(0, idx) : norm;
                string rest = idx >= 0 ? norm.Substring(idx + 1) : "";
                string fullHive;
                if (!hiveMap.TryGetValue(head, out fullHive)) fullHive = head.ToUpperInvariant();
                string fullPath = string.IsNullOrEmpty(rest) ? fullHive : (fullHive + "\\" + rest);
                if (seen.Add(fullPath))
                {
                    sb.Append('[').Append(fullPath).Append("]\r\n\r\n");
                }
            }
            return sb.ToString();
        }

        private static int RunHidden(string fileName, string args, int timeoutMs, out string stdout, out string stderr)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
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
                    stderr = "Process timed out";
                    return -1;
                }
                p.WaitForExit();
                stdout = outSb.ToString();
                stderr = errSb.ToString();
                return p.ExitCode;
            }
        }

        public static Dictionary<string, object> ListWindowsRestorePoints()
        {
            if (!SystemInfo.IsAdmin())
            {
                return new Dictionary<string, object>
                {
                    { "admin_required", true },
                    { "restore_points", new List<object>() },
                    { "message", "Run as Administrator to view and create Windows System Restore Points." }
                };
            }

            try
            {
                List<object> list = new List<object>();
                ManagementScope scope = new ManagementScope(@"\\.\root\default");
                scope.Connect();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    scope, new ObjectQuery("SELECT SequenceNumber, Description, RestorePointType, CreationTime FROM SystemRestore")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string created = "";
                        try
                        {
                            string rawDt = (obj["CreationTime"] ?? "").ToString();
                            if (!string.IsNullOrEmpty(rawDt))
                                created = ManagementDateTimeConverter.ToDateTime(rawDt).ToString("yyyy-MM-dd HH:mm:ss");
                        }
                        catch { }
                        list.Add(new Dictionary<string, object>
                        {
                            { "SequenceNumber", obj["SequenceNumber"] },
                            { "Description", (obj["Description"] ?? "").ToString() },
                            { "RestorePointType", obj["RestorePointType"] },
                            { "CreationTime", created }
                        });
                    }
                }
                return new Dictionary<string, object>
                {
                    { "admin_required", false },
                    { "restore_points", list },
                    { "message", list.Count > 0 ? string.Format("Found {0} Windows Restore Point(s).", list.Count) : "No restore points found yet." }
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "admin_required", false },
                    { "restore_points", new List<object>() },
                    { "message", "System Protection may be disabled: " + ex.Message }
                };
            }
        }

        public static Dictionary<string, object> CreateWindowsRestorePoint(string description)
        {
            if (!SystemInfo.IsAdmin())
            {
                return new Dictionary<string, object>
                {
                    { "status", "skipped_no_admin" },
                    { "created", false },
                    { "message", "Administrator privileges required for Windows System Restore Point (local Registry + PC Snapshot backup still created)." }
                };
            }

            string safeDesc = (description ?? "Kenny PC Optimizer").Replace("\"", "'").Replace("`", "");
            if (safeDesc.Length > 64) safeDesc = safeDesc.Substring(0, 64);
            string sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\default");
                scope.Connect();
                using (ManagementClass mc = new ManagementClass(scope, new ManagementPath("SystemRestore"), new ObjectGetOptions()))
                {
                    try { mc.InvokeMethod("Enable", new object[] { sysDrive }); } catch { }
                    // 12 = MODIFY_SETTINGS, 100 = BEGIN_SYSTEM_CHANGE
                    object ret = mc.InvokeMethod("CreateRestorePoint", new object[] { safeDesc, 12, 100 });
                    uint code = ret != null ? Convert.ToUInt32(ret) : 1u;
                    if (code == 0u)
                    {
                        return new Dictionary<string, object>
                        {
                            { "status", "created" },
                            { "created", true },
                            { "message", string.Format("Created Windows System Restore Point: '{0}'.", safeDesc) }
                        };
                    }
                    // ERROR_SUCCESS / frequency skip or disabled
                    return new Dictionary<string, object>
                    {
                        { "status", "skipped_or_warning" },
                        { "created", false },
                        { "message", string.Format("Windows System Restore returned status code {0} (a recent restore point may already exist within 24 hours).", code) }
                    };
                }
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "status", "failed" },
                    { "created", false },
                    { "message", "System Restore note: " + ex.Message }
                };
            }
        }

        public static bool LaunchWindowsSystemRestore()
        {
            try
            {
                Process.Start("rstrui.exe");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static Dictionary<string, object> CreateBackupSnapshot(
            string reason,
            bool includeRegistryFull,
            List<string> surgicalRegKeys,
            List<Dictionary<string, string>> filesToArchive,
            bool includeRestorePoint,
            string backupRootOverride,
            LogCallback logCb)
        {
            LogCallback log = delegate(string msg, string lvl) { if (logCb != null) logCb(msg, lvl); };

            string root = !string.IsNullOrEmpty(backupRootOverride) ? backupRootOverride : GetBackupRoot();
            Directory.CreateDirectory(root);

            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string shortId = Guid.NewGuid().ToString("N").Substring(0, 6);
            StringBuilder safeTagSb = new StringBuilder();
            foreach (char c in (reason ?? "Backup"))
            {
                safeTagSb.Append((char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_');
            }
            string safeTag = safeTagSb.ToString().Trim('_');
            if (safeTag.Length > 32) safeTag = safeTag.Substring(0, 32).Trim('_');
            if (string.IsNullOrEmpty(safeTag)) safeTag = "Backup";

            string snapshotId = string.Format("Snapshot_{0}_{1}_{2}", ts, safeTag, shortId);
            string snapDir = Path.Combine(root, snapshotId);
            string regDir = Path.Combine(snapDir, "registry");
            string filesDir = Path.Combine(snapDir, "files");
            Directory.CreateDirectory(regDir);
            Directory.CreateDirectory(filesDir);

            Dictionary<string, string> checksums = new Dictionary<string, string>();
            List<string> fullHivesExported = new List<string>();
            string surgicalRegPath = null;
            string fullZipPath = null;
            int surgicalCount = 0;

            if (surgicalRegKeys != null && surgicalRegKeys.Count > 0)
            {
                log(string.Format("Creating surgical Registry Undo .reg for {0} key(s)...", surgicalRegKeys.Count), "info");
                surgicalRegPath = Path.Combine(regDir, "Quick_Restore_Deleted_Keys.reg");
                string regText = BuildSurgicalRegContent(surgicalRegKeys);
                byte[] utf16 = Encoding.Unicode.GetBytes(regText);
                using (FileStream fs = File.Create(surgicalRegPath))
                {
                    fs.WriteByte(0xFF);
                    fs.WriteByte(0xFE);
                    fs.Write(utf16, 0, utf16.Length);
                }
                checksums["registry/Quick_Restore_Deleted_Keys.reg"] = Sha256File(surgicalRegPath);
                surgicalCount = surgicalRegKeys.Count;
            }

            if (includeRegistryFull)
            {
                fullZipPath = Path.Combine(regDir, "Full_Registry_Hives.zip");
                string tmpRegDir = Path.Combine(Path.GetTempPath(), "kenny_reg_" + shortId);
                Directory.CreateDirectory(tmpRegDir);
                try
                {
                    Tuple<string, string>[] hives = new Tuple<string, string>[]
                    {
                        Tuple.Create(@"HKCU\Software", "HKCU_Software.reg"),
                        Tuple.Create(@"HKLM\SOFTWARE", "HKLM_SOFTWARE.reg")
                    };
                    using (ZipArchive zf = ZipFile.Open(fullZipPath, ZipArchiveMode.Create))
                    {
                        foreach (Tuple<string, string> hv in hives)
                        {
                            log("Exporting Registry hive " + hv.Item1 + "...", "info");
                            string outReg = Path.Combine(tmpRegDir, hv.Item2);
                            string stdout, stderr;
                            int rc = RunHidden("reg", string.Format("export \"{0}\" \"{1}\" /y /reg:64", hv.Item1, outReg), 120000, out stdout, out stderr);
                            if (rc == 0 && File.Exists(outReg))
                            {
                                zf.CreateEntryFromFile(outReg, hv.Item2, CompressionLevel.Optimal);
                                fullHivesExported.Add(hv.Item1);
                                log(string.Format("Backed up {0} ({1} raw).", hv.Item1, SystemInfo.FormatBytes(new FileInfo(outReg).Length)), "success");
                            }
                            else
                            {
                                log(string.Format("Warning exporting {0}: {1}", hv.Item1, stderr.Trim()), "warn");
                            }
                        }
                    }
                    if (File.Exists(fullZipPath))
                    {
                        checksums["registry/Full_Registry_Hives.zip"] = Sha256File(fullZipPath);
                    }
                }
                finally
                {
                    try { Directory.Delete(tmpRegDir, true); } catch { }
                }
            }

            log("Creating PC State & Files Snapshot archive...", "info");
            string pcZip = Path.Combine(filesDir, "PC_Files_Snapshot.zip");
            List<Dictionary<string, object>> archivedFiles = new List<Dictionary<string, object>>();
            List<Dictionary<string, string>> progs = CollectInstalledPrograms();
            Dictionary<string, object> systemState = new Dictionary<string, object>
            {
                { "captured_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "computer_name", Environment.MachineName },
                { "username", Environment.UserName },
                { "os", SystemInfo.GetWindowsOsInfo() },
                { "installed_programs_count", progs.Count },
                { "installed_programs", progs },
                { "startup_entries", CollectStartupEntries() }
            };

            JavaScriptSerializer js = SystemInfo.CreateSerializer();
            using (ZipArchive zf = ZipFile.Open(pcZip, ZipArchiveMode.Create))
            {
                ZipArchiveEntry stEntry = zf.CreateEntry("system_state.json", CompressionLevel.Optimal);
                using (StreamWriter sw = new StreamWriter(stEntry.Open(), Encoding.UTF8))
                {
                    sw.Write(js.Serialize(systemState));
                }

                if (filesToArchive != null)
                {
                    for (int i = 0; i < filesToArchive.Count; i++)
                    {
                        Dictionary<string, string> item = filesToArchive[i];
                        string src = item.ContainsKey("path") ? item["path"] : "";
                        if (!File.Exists(src)) continue;
                        string arcName = string.Format("captured_files/{0:D4}_{1}", i, Path.GetFileName(src));
                        try
                        {
                            long sz = new FileInfo(src).Length;
                            zf.CreateEntryFromFile(src, arcName, CompressionLevel.Optimal);
                            archivedFiles.Add(new Dictionary<string, object>
                            {
                                { "archive_path", arcName },
                                { "original_path", src },
                                { "category", item.ContainsKey("category") ? item["category"] : "file" },
                                { "size_bytes", sz }
                            });
                        }
                        catch (Exception ex)
                        {
                            log(string.Format("Skipped locked file during snapshot ({0}): {1}", Path.GetFileName(src), ex.Message), "warn");
                        }
                    }
                }
            }
            checksums["files/PC_Files_Snapshot.zip"] = Sha256File(pcZip);
            log(string.Format("PC Snapshot archived ({0} critical file(s) + system state).", archivedFiles.Count), "success");

            Dictionary<string, object> rpInfo = new Dictionary<string, object>
            {
                { "status", "not_requested" },
                { "created", false },
                { "message", "Skipped per configuration." }
            };
            if (includeRestorePoint)
            {
                log("Checking / creating Windows System Restore Point...", "info");
                rpInfo = CreateWindowsRestorePoint("Kenny Optimizer: " + (reason ?? "Backup"));
                bool created = rpInfo.ContainsKey("created") && Convert.ToBoolean(rpInfo["created"]);
                log(rpInfo["message"].ToString(), created ? "success" : "warn");
            }

            string emergencyBat = Path.Combine(snapDir, "EMERGENCY_RESTORE.bat");
            string batText =
                "@echo off\r\n" +
                "title Kenny PC Optimizer - Emergency Backup Restore\r\n" +
                "echo ============================================================\r\n" +
                "echo   Snapshot: " + snapshotId + "\r\n" +
                "echo   Created : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                "echo ============================================================\r\n" +
                "echo.\r\n" +
                "if exist \"%~dp0registry\\Quick_Restore_Deleted_Keys.reg\" (\r\n" +
                "    echo [1] Restoring deleted registry keys...\r\n" +
                "    reg import \"%~dp0registry\\Quick_Restore_Deleted_Keys.reg\"\r\n" +
                ")\r\n" +
                "echo.\r\n" +
                "echo Full Registry hives are archived in: registry\\Full_Registry_Hives.zip\r\n" +
                "echo Captured PC files are archived in  : files\\PC_Files_Snapshot.zip\r\n" +
                "echo.\r\n" +
                "choice /c YN /m \"Launch Windows System Restore (rstrui.exe)? \"\r\n" +
                "if errorlevel 2 goto END\r\n" +
                "start rstrui.exe\r\n" +
                ":END\r\n" +
                "pause\r\n";
            File.WriteAllText(emergencyBat, batText, Encoding.UTF8);
            checksums["EMERGENCY_RESTORE.bat"] = Sha256File(emergencyBat);

            long totalSize = 0;
            foreach (string f in Directory.GetFiles(snapDir, "*", SearchOption.AllDirectories))
            {
                totalSize += new FileInfo(f).Length;
            }

            Dictionary<string, object> regSummary = new Dictionary<string, object>
            {
                { "full_hives_exported", fullHivesExported },
                { "surgical_keys_count", surgicalCount },
                { "surgical_reg_path", surgicalRegPath },
                { "full_zip_path", fullZipPath }
            };

            Dictionary<string, object> manifest = new Dictionary<string, object>
            {
                { "snapshot_id", snapshotId },
                { "created_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "reason", reason },
                { "computer_name", Environment.MachineName },
                { "username", Environment.UserName },
                { "is_admin", SystemInfo.IsAdmin() },
                { "total_size_bytes", totalSize },
                { "total_size_human", SystemInfo.FormatBytes(totalSize) },
                { "registry", regSummary },
                { "archived_files_count", archivedFiles.Count },
                { "archived_files", archivedFiles },
                { "restore_point", rpInfo },
                { "checksums_sha256", checksums },
                { "path", snapDir }
            };

            File.WriteAllText(Path.Combine(snapDir, "manifest.json"), js.Serialize(manifest), Encoding.UTF8);
            log(string.Format("Backup checkpoint ready: {0} ({1})", snapshotId, SystemInfo.FormatBytes(totalSize)), "success");
            return manifest;
        }

        private static string ResolveSnapshotDir(string snapshotId, string backupRootOverride)
        {
            if (string.IsNullOrWhiteSpace(snapshotId) || snapshotId.Contains("/") || snapshotId.Contains("\\") || snapshotId.Contains(".."))
                throw new ArgumentException("Invalid snapshot_id");
            string root = Path.GetFullPath(!string.IsNullOrEmpty(backupRootOverride) ? backupRootOverride : GetBackupRoot());
            string target = Path.GetFullPath(Path.Combine(root, snapshotId));
            if (!string.Equals(Path.GetDirectoryName(target), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target))
                throw new DirectoryNotFoundException("Snapshot not found: " + snapshotId);
            return target;
        }

        public static List<Dictionary<string, object>> ListBackups(string backupRootOverride)
        {
            string root = !string.IsNullOrEmpty(backupRootOverride) ? backupRootOverride : GetBackupRoot();
            if (!Directory.Exists(root)) return new List<Dictionary<string, object>>();

            DirectoryInfo[] dirs = new DirectoryInfo(root).GetDirectories();
            Array.Sort(dirs, delegate(DirectoryInfo a, DirectoryInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });

            JavaScriptSerializer js = SystemInfo.CreateSerializer();
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (DirectoryInfo d in dirs)
            {
                string mf = Path.Combine(d.FullName, "manifest.json");
                if (!File.Exists(mf)) continue;
                try
                {
                    Dictionary<string, object> data = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(mf, Encoding.UTF8));
                    if (data != null)
                    {
                        data["path"] = d.FullName;
                        list.Add(data);
                    }
                }
                catch { }
            }
            return list;
        }

        public static Dictionary<string, object> VerifyBackup(string snapshotId, string backupRootOverride)
        {
            string snapDir = ResolveSnapshotDir(snapshotId, backupRootOverride);
            JavaScriptSerializer js = SystemInfo.CreateSerializer();
            Dictionary<string, object> manifest = js.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(Path.Combine(snapDir, "manifest.json"), Encoding.UTF8)
            );
            Dictionary<string, object> sums = manifest["checksums_sha256"] as Dictionary<string, object>;
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            bool allOk = true;
            if (sums != null)
            {
                foreach (KeyValuePair<string, object> kv in sums)
                {
                    string rel = kv.Key;
                    string expected = kv.Value != null ? kv.Value.ToString() : "";
                    string fp = Path.Combine(snapDir, rel.Replace('/', '\\'));
                    if (!File.Exists(fp))
                    {
                        allOk = false;
                        results.Add(new Dictionary<string, object> { { "file", rel }, { "ok", false }, { "reason", "Missing" } });
                        continue;
                    }
                    string actual = Sha256File(fp);
                    bool match = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                    if (!match) allOk = false;
                    results.Add(new Dictionary<string, object> { { "file", rel }, { "ok", match }, { "sha256", actual } });
                }
            }
            return new Dictionary<string, object>
            {
                { "snapshot_id", snapshotId },
                { "verified", allOk },
                { "files", results }
            };
        }

        public static Dictionary<string, object> RestoreRegistryFromBackup(string snapshotId, string mode, string backupRootOverride)
        {
            string snapDir = ResolveSnapshotDir(snapshotId, backupRootOverride);
            string stdout, stderr;

            if (string.Equals(mode, "quick", StringComparison.OrdinalIgnoreCase))
            {
                string regFile = Path.Combine(snapDir, "registry", "Quick_Restore_Deleted_Keys.reg");
                if (!File.Exists(regFile))
                {
                    return new Dictionary<string, object> { { "ok", false }, { "message", "No surgical Quick_Restore_Deleted_Keys.reg in this snapshot." } };
                }
                int rc = RunHidden("reg", string.Format("import \"{0}\" /reg:64", regFile), 60000, out stdout, out stderr);
                if (rc == 0)
                {
                    return new Dictionary<string, object> { { "ok", true }, { "message", "Restored registry keys from Quick_Restore_Deleted_Keys.reg." } };
                }
                return new Dictionary<string, object> { { "ok", false }, { "message", !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim() } };
            }

            string fullZip = Path.Combine(snapDir, "registry", "Full_Registry_Hives.zip");
            if (!File.Exists(fullZip))
            {
                return new Dictionary<string, object> { { "ok", false }, { "message", "Full_Registry_Hives.zip not found in this snapshot." } };
            }

            string member = string.Equals(mode, "hkcu", StringComparison.OrdinalIgnoreCase) ? "HKCU_Software.reg" : "HKLM_SOFTWARE.reg";
            if (member == "HKLM_SOFTWARE.reg" && !SystemInfo.IsAdmin())
            {
                return new Dictionary<string, object> { { "ok", false }, { "message", "Administrator privileges required to import HKLM_SOFTWARE.reg." } };
            }

            string tmpDir = Path.Combine(Path.GetTempPath(), "kenny_reg_restore_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string extracted = Path.Combine(tmpDir, member);
                using (ZipArchive zf = ZipFile.OpenRead(fullZip))
                {
                    ZipArchiveEntry entry = zf.GetEntry(member);
                    if (entry == null)
                    {
                        return new Dictionary<string, object> { { "ok", false }, { "message", member + " was not included in this snapshot." } };
                    }
                    entry.ExtractToFile(extracted, true);
                }
                int rc = RunHidden("reg", string.Format("import \"{0}\" /reg:64", extracted), 180000, out stdout, out stderr);
                if (rc == 0)
                {
                    return new Dictionary<string, object> { { "ok", true }, { "message", "Successfully imported " + member + "." } };
                }
                return new Dictionary<string, object> { { "ok", false }, { "message", !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "reg import returned non-zero" } };
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        public static Dictionary<string, object> RestoreFilesFromBackup(string snapshotId, string backupRootOverride)
        {
            string snapDir = ResolveSnapshotDir(snapshotId, backupRootOverride);
            JavaScriptSerializer js = SystemInfo.CreateSerializer();
            Dictionary<string, object> manifest = js.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(Path.Combine(snapDir, "manifest.json"), Encoding.UTF8)
            );
            string pcZip = Path.Combine(snapDir, "files", "PC_Files_Snapshot.zip");
            if (!File.Exists(pcZip))
            {
                return new Dictionary<string, object> { { "ok", false }, { "restored", 0 }, { "failed", 0 }, { "message", "PC_Files_Snapshot.zip missing." } };
            }

            object archivedObj = manifest.ContainsKey("archived_files") ? manifest["archived_files"] : null;
            ArrayList archived = archivedObj as ArrayList;
            if (archived == null || archived.Count == 0)
            {
                return new Dictionary<string, object> { { "ok", true }, { "restored", 0 }, { "failed", 0 }, { "message", "No archived files recorded in this snapshot." } };
            }

            int restored = 0;
            int failed = 0;
            List<string> errors = new List<string>();
            using (ZipArchive zf = ZipFile.OpenRead(pcZip))
            {
                foreach (object o in archived)
                {
                    Dictionary<string, object> item = o as Dictionary<string, object>;
                    if (item == null) continue;
                    string arc = item["archive_path"].ToString();
                    string dest = item["original_path"].ToString();
                    try
                    {
                        string parent = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        if (File.Exists(dest))
                        {
                            try { File.Copy(dest, dest + ".pre_restore_bak", true); } catch { }
                        }
                        ZipArchiveEntry entry = zf.GetEntry(arc);
                        if (entry != null)
                        {
                            entry.ExtractToFile(dest, true);
                            restored++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add(Path.GetFileName(dest) + ": " + ex.Message);
                    }
                }
            }
            return new Dictionary<string, object>
            {
                { "ok", failed == 0 },
                { "restored", restored },
                { "failed", failed },
                { "errors", errors },
                { "message", string.Format("Restored {0} file(s){1}", restored, failed > 0 ? string.Format(" ({0} locked/failed)", failed) : ".") }
            };
        }

        public static Dictionary<string, object> DeleteBackup(string snapshotId, string backupRootOverride)
        {
            string snapDir = ResolveSnapshotDir(snapshotId, backupRootOverride);
            Directory.Delete(snapDir, true);
            return new Dictionary<string, object> { { "ok", true }, { "message", "Deleted backup " + snapshotId + "." } };
        }

        public static bool OpenBackupFolder(string snapshotId, string backupRootOverride)
        {
            try
            {
                string target = !string.IsNullOrEmpty(snapshotId)
                    ? ResolveSnapshotDir(snapshotId, backupRootOverride)
                    : (!string.IsNullOrEmpty(backupRootOverride) ? backupRootOverride : GetBackupRoot());
                Directory.CreateDirectory(target);
                Process.Start("explorer.exe", target);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
