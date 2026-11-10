using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace KennyPCOptimizer
{
    public static class RegistryCleaner
    {
        private static string HiveFullName(string hiveCode)
        {
            return hiveCode == "HKLM" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
        }

        private static bool ScanRecursive(
            string hiveCode,
            RegistryKey rootHandle,
            string subkey,
            int view,
            HashSet<string> excludes,
            int depth,
            int maxDepth,
            string vendorName,
            List<Dictionary<string, object>> found)
        {
            int numSub = 0;
            int numVal = 0;
            string[] children = new string[0];

            try
            {
                using (RegistryKey k = rootHandle.OpenSubKey(subkey, false))
                {
                    if (k == null) return false;
                    numSub = k.SubKeyCount;
                    numVal = k.ValueCount;
                    if (numSub > 0) children = k.GetSubKeyNames();
                }
            }
            catch
            {
                return false;
            }

            if (depth == 0)
            {
                foreach (string child in children)
                {
                    if (excludes.Contains(child)) continue;
                    string cSub = subkey + "\\" + child;
                    ScanRecursive(hiveCode, rootHandle, cSub, view, excludes, depth + 1, maxDepth, child, found);
                }
                return false;
            }

            bool allChildrenEmpty = true;
            foreach (string child in children)
            {
                if (depth >= maxDepth)
                {
                    allChildrenEmpty = false;
                    break;
                }
                string cSub = subkey + "\\" + child;
                bool cEmpty = ScanRecursive(hiveCode, rootHandle, cSub, view, excludes, depth + 1, maxDepth, vendorName, found);
                if (!cEmpty) allChildrenEmpty = false;
            }

            bool isEmpty = (numVal == 0) && (numSub == 0 || allChildrenEmpty);
            if (isEmpty)
            {
                string displaySub = subkey;
                if (hiveCode == "HKLM" && view == 32 && subkey.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase))
                {
                    displaySub = "SOFTWARE\\WOW6432Node\\" + subkey.Substring(9);
                }
                string fullPath = HiveFullName(hiveCode) + "\\" + displaySub;
                bool adminReq = hiveCode == "HKLM";
                found.Add(new Dictionary<string, object>
                {
                    { "id", string.Format("{0}:{1}:{2}", hiveCode, view, subkey) },
                    { "hive", hiveCode },
                    { "view", view },
                    { "subkey", subkey },
                    { "display_subkey", displaySub },
                    { "full_path", fullPath },
                    { "vendor", vendorName },
                    { "kind", numSub == 0 ? "Empty Leaf" : "Empty Subtree" },
                    { "depth", depth },
                    { "admin_required", adminReq }
                });
            }
            return isEmpty;
        }

        public static Dictionary<string, object> ScanEmptyRegistryKeys(string testScopeHive = null, string testScopeBase = null)
        {
            bool admin = SystemInfo.IsAdmin();
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();

            if (!string.IsNullOrEmpty(testScopeHive) && !string.IsNullOrEmpty(testScopeBase))
            {
                RegistryHive h = testScopeHive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                using (RegistryKey root = RegistryKey.OpenBaseKey(h, RegistryView.Registry64))
                {
                    ScanRecursive(testScopeHive, root, testScopeBase, 64, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, 10, "", items);
                }
            }
            else
            {
                using (RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                {
                    ScanRecursive("HKCU", hkcu, "Software", 64, Config.ProtectedRegTopKeys, 0, 10, "", items);
                }
                using (RegistryKey hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    ScanRecursive("HKLM", hklm64, "SOFTWARE", 64, Config.ProtectedRegTopKeys, 0, 10, "", items);
                }
                using (RegistryKey hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                {
                    ScanRecursive("HKLM", hklm32, "SOFTWARE", 32, Config.ProtectedRegTopKeys, 0, 10, "", items);
                }
            }

            foreach (Dictionary<string, object> it in items)
            {
                bool adminReq = Convert.ToBoolean(it["admin_required"]);
                it["accessible"] = (!adminReq) || admin;
            }

            items.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                int ah = a["hive"].ToString() == "HKCU" ? 0 : 1;
                int bh = b["hive"].ToString() == "HKCU" ? 0 : 1;
                if (ah != bh) return ah.CompareTo(bh);
                return string.Compare(a["full_path"].ToString(), b["full_path"].ToString(), StringComparison.OrdinalIgnoreCase);
            });

            int hkcuCount = 0;
            int hklmCount = 0;
            foreach (Dictionary<string, object> it in items)
            {
                if (it["hive"].ToString() == "HKCU") hkcuCount++;
                else hklmCount++;
            }

            return new Dictionary<string, object>
            {
                { "keys", items },
                { "total_count", items.Count },
                { "hkcu_count", hkcuCount },
                { "hklm_count", hklmCount },
                { "is_admin", admin }
            };
        }

        private static bool IsProtectedSubkey(string subkey)
        {
            string[] parts = subkey.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return true;
            if (!string.Equals(parts[0], "software", StringComparison.OrdinalIgnoreCase)) return true;
            if (Config.ProtectedRegTopKeys.Contains(parts[1])) return true;
            return false;
        }

        private static int CountSlashes(string s)
        {
            int c = 0;
            foreach (char ch in s) if (ch == '\\') c++;
            return c;
        }

        public static Dictionary<string, object> CleanEmptyRegistryKeys(
            List<Dictionary<string, object>> keysToClean,
            bool dryRun,
            bool bypassTopProtectionForTests = false,
            LogCallback logCb = null)
        {
            LogCallback log = delegate(string m, string l) { if (logCb != null) logCb(m, l); };
            bool admin = SystemInfo.IsAdmin();

            List<Dictionary<string, object>> ordered = new List<Dictionary<string, object>>(keysToClean);
            ordered.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                string sa = a.ContainsKey("subkey") && a["subkey"] != null ? a["subkey"].ToString() : "";
                string sb = b.ContainsKey("subkey") && b["subkey"] != null ? b["subkey"].ToString() : "";
                int cmp = CountSlashes(sb).CompareTo(CountSlashes(sa));
                return cmp != 0 ? cmp : sb.Length.CompareTo(sa.Length);
            });

            List<string> deletedKeys = new List<string>();
            int skippedAdmin = 0;
            int skippedNotEmpty = 0;
            int failed = 0;
            string prefix = dryRun ? "[DRY-RUN] " : "";

            foreach (Dictionary<string, object> item in ordered)
            {
                string hiveCode = item.ContainsKey("hive") && item["hive"] != null ? item["hive"].ToString() : "HKCU";
                int view = item.ContainsKey("view") ? Convert.ToInt32(item["view"]) : 64;
                string subkey = item.ContainsKey("subkey") && item["subkey"] != null ? item["subkey"].ToString() : "";
                string fullPath = item.ContainsKey("full_path") && item["full_path"] != null
                    ? item["full_path"].ToString()
                    : (HiveFullName(hiveCode) + "\\" + subkey);

                if (string.IsNullOrEmpty(subkey) || (!bypassTopProtectionForTests && IsProtectedSubkey(subkey)))
                {
                    log("Blocked protected registry path: " + fullPath, "warn");
                    failed++;
                    continue;
                }

                if (hiveCode == "HKLM" && !admin)
                {
                    skippedAdmin++;
                    continue;
                }

                RegistryHive hive = hiveCode == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                RegistryView regView = view == 32 ? RegistryView.Registry32 : RegistryView.Registry64;

                try
                {
                    using (RegistryKey baseK = RegistryKey.OpenBaseKey(hive, regView))
                    {
                        int numSub;
                        int numVal;
                        using (RegistryKey k = baseK.OpenSubKey(subkey, false))
                        {
                            if (k == null) continue;
                            numSub = k.SubKeyCount;
                            numVal = k.ValueCount;
                        }

                        if (numVal > 0 || (numSub > 0 && !dryRun))
                        {
                            skippedNotEmpty++;
                            continue;
                        }

                        if (dryRun)
                        {
                            deletedKeys.Add(fullPath);
                            continue;
                        }

                        baseK.DeleteSubKey(subkey, false);
                        deletedKeys.Add(fullPath);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    log(string.Format("Could not delete {0}: {1}", fullPath, ex.Message), "warn");
                }
            }

            log(string.Format("{0}Registry empty folder cleanup finished: {1} key(s) removed{2}{3}",
                prefix,
                deletedKeys.Count,
                skippedAdmin > 0 ? string.Format(", {0} skipped (Admin required)", skippedAdmin) : "",
                skippedNotEmpty > 0 ? string.Format(", {0} skipped (non-empty)", skippedNotEmpty) : ""),
                "success");

            return new Dictionary<string, object>
            {
                { "dry_run", dryRun },
                { "deleted_count", deletedKeys.Count },
                { "deleted_keys", deletedKeys },
                { "skipped_admin", skippedAdmin },
                { "skipped_not_empty", skippedNotEmpty },
                { "failed", failed }
            };
        }
    }
}
