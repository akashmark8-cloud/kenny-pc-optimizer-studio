using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KennyPCOptimizer
{
    public static class MemoryOptimizer
    {
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SET_QUOTA = 0x0100;

        private const int SystemFileCacheInformation = 21; // 0x15
        private const int SystemMemoryListInformation = 80; // 0x50
        private const int SystemCombinePhysicalMemoryInformation = 130; // 0x82
        private const int SystemRegistryReconciliationInformation = 155; // 0x9B

        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public IntPtr CommitTotal;
            public IntPtr CommitLimit;
            public IntPtr CommitPeak;
            public IntPtr PhysicalTotal;
            public IntPtr PhysicalAvailable;
            public IntPtr SystemCache;
            public IntPtr KernelTotal;
            public IntPtr KernelPaged;
            public IntPtr KernelNonpaged;
            public IntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_MEMORY_LIST_INFORMATION
        {
            public IntPtr ZeroPageCount;
            public IntPtr FreePageCount;
            public IntPtr ModifiedPageCount;
            public IntPtr ModifiedNoWritePageCount;
            public IntPtr BadPageCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public IntPtr[] PageCountByPriority;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public IntPtr[] RepurposedPagesByPriority;
            public IntPtr ModifiedPageCountPageFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_FILECACHE_INFORMATION
        {
            public IntPtr CurrentSize;
            public IntPtr PeakSize;
            public uint PageFaultCount;
            public IntPtr MinimumWorkingSet;
            public IntPtr MaximumWorkingSet;
            public IntPtr CurrentSizeIncludingTransitionInPages;
            public IntPtr PeakSizeIncludingTransitionInPages;
            public uint TransitionRePurposeCount;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_COMBINE_INFORMATION_EX
        {
            public IntPtr Handle;
            public IntPtr PagesCombined;
            public ulong Flags;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, uint SystemInformationLength, out uint ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, uint SystemInformationLength);

        private static readonly object _historyLock = new object();
        private static readonly List<Dictionary<string, object>> _boostHistory = new List<Dictionary<string, object>>();
        private static DateTime _lastAutoCleanUtc = DateTime.MinValue;
        private static DateTime _lastIntervalCleanUtc = DateTime.UtcNow;

        private static bool EnablePrivilege(string privName)
        {
            IntPtr hToken = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                    return false;
                LUID luid;
                if (!LookupPrivilegeValue(null, privName, out luid))
                    return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;
                if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    return false;
                return Marshal.GetLastWin32Error() == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }
        }

        private static int CallNtSetMemoryListCommand(int command)
        {
            IntPtr buf = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buf, command);
                return NtSetSystemInformation(SystemMemoryListInformation, buf, 4u);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static List<Dictionary<string, object>> GetTopMemoryProcesses(int limit)
        {
            List<Dictionary<string, object>> procs = new List<Dictionary<string, object>>();
            int myPid = Process.GetCurrentProcess().Id;
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id <= 4 || p.Id == myPid) continue;
                        long ws = p.WorkingSet64;
                        long priv = p.PrivateMemorySize64;
                        if (ws < 1024L * 1024L) continue;
                        procs.Add(new Dictionary<string, object>
                        {
                            { "pid", p.Id },
                            { "name", p.ProcessName + ".exe" },
                            { "working_set_bytes", ws },
                            { "working_set_human", SystemInfo.FormatBytes(ws) },
                            { "private_bytes", priv },
                            { "private_human", SystemInfo.FormatBytes(priv) }
                        });
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            procs.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                return Convert.ToInt64(b["working_set_bytes"]).CompareTo(Convert.ToInt64(a["working_set_bytes"]));
            });
            return procs.Count > limit ? procs.GetRange(0, limit) : procs;
        }

        public static Dictionary<string, object> GetMemoryDetails(bool includeProcesses = true)
        {
            Dictionary<string, object> mem = SystemInfo.GetMemoryStats();
            long physTotal = Convert.ToInt64(mem["total_bytes"]);
            long physAvail = Convert.ToInt64(mem["avail_bytes"]);
            long physUsed = Convert.ToInt64(mem["used_bytes"]);
            int physPercent = Convert.ToInt32(mem["percent"]);

            long sysCacheBytes = 0;
            long commitTotal = 0;
            long commitLimit = 0;
            long kernelPaged = 0;
            long kernelNonPaged = 0;
            long pageSize = 4096;
            uint procCount = 0;
            uint threadCount = 0;

            PERFORMANCE_INFORMATION pi = new PERFORMANCE_INFORMATION();
            pi.cb = (uint)Marshal.SizeOf(typeof(PERFORMANCE_INFORMATION));
            if (GetPerformanceInfo(out pi, pi.cb))
            {
                pageSize = (long)pi.PageSize;
                sysCacheBytes = (long)pi.SystemCache * pageSize;
                commitTotal = (long)pi.CommitTotal * pageSize;
                commitLimit = (long)pi.CommitLimit * pageSize;
                kernelPaged = (long)pi.KernelPaged * pageSize;
                kernelNonPaged = (long)pi.KernelNonpaged * pageSize;
                procCount = pi.ProcessCount;
                threadCount = pi.ThreadCount;
            }

            long standbyBytes = 0;
            long lowStandbyBytes = 0;
            long modifiedBytes = 0;
            long freeZeroBytes = 0;

            int mliSz = Marshal.SizeOf(typeof(SYSTEM_MEMORY_LIST_INFORMATION));
            IntPtr mliPtr = Marshal.AllocHGlobal(mliSz);
            try
            {
                uint retLen;
                if (NtQuerySystemInformation(SystemMemoryListInformation, mliPtr, (uint)mliSz, out retLen) == 0)
                {
                    SYSTEM_MEMORY_LIST_INFORMATION mli = (SYSTEM_MEMORY_LIST_INFORMATION)Marshal.PtrToStructure(mliPtr, typeof(SYSTEM_MEMORY_LIST_INFORMATION));
                    for (int i = 0; i < 8; i++)
                    {
                        long pBytes = (long)mli.PageCountByPriority[i] * pageSize;
                        standbyBytes += pBytes;
                        if (i <= 4) lowStandbyBytes += pBytes;
                    }
                    modifiedBytes = ((long)mli.ModifiedPageCount + (long)mli.ModifiedNoWritePageCount) * pageSize;
                    freeZeroBytes = ((long)mli.FreePageCount + (long)mli.ZeroPageCount) * pageSize;
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(mliPtr);
            }

            int commitPercent = commitLimit > 0 ? (int)Math.Min(100L, (commitTotal * 100L) / commitLimit) : 0;
            bool isAdmin = SystemInfo.IsAdmin();

            List<Dictionary<string, object>> areas = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "id", "working_set" },
                    { "name", "Process Working Sets" },
                    { "description", "Trims idle memory pages held by running apps (falls back to user-mode trim if non-admin)." },
                    { "safe_default", true },
                    { "admin_required", false },
                    { "size_bytes", physUsed },
                    { "size_human", SystemInfo.FormatBytes(physUsed) }
                },
                new Dictionary<string, object>
                {
                    { "id", "low_standby" },
                    { "name", "Low-Priority Standby Cache" },
                    { "description", "Safely frees low-priority cached pages without evicting active foreground app cache." },
                    { "safe_default", true },
                    { "admin_required", true },
                    { "size_bytes", lowStandbyBytes },
                    { "size_human", SystemInfo.FormatBytes(lowStandbyBytes) }
                },
                new Dictionary<string, object>
                {
                    { "id", "system_file_cache" },
                    { "name", "System File Cache" },
                    { "description", "Flushes Windows System Working Set file cache via SystemFileCacheInformation." },
                    { "safe_default", true },
                    { "admin_required", true },
                    { "size_bytes", sysCacheBytes },
                    { "size_human", SystemInfo.FormatBytes(sysCacheBytes) }
                },
                new Dictionary<string, object>
                {
                    { "id", "combine_memory" },
                    { "name", "Combine Memory Lists (Win 10+)" },
                    { "description", "Deduplicates identical physical memory pages across running processes." },
                    { "safe_default", true },
                    { "admin_required", true },
                    { "size_bytes", 0L },
                    { "size_human", "Page Dedupe" }
                },
                new Dictionary<string, object>
                {
                    { "id", "registry_cache" },
                    { "name", "Registry Cache" },
                    { "description", "Reconciles and flushes cached Windows Registry hive pages from RAM." },
                    { "safe_default", false },
                    { "admin_required", true },
                    { "size_bytes", 0L },
                    { "size_human", "Kernel Hive" }
                },
                new Dictionary<string, object>
                {
                    { "id", "standby_list" },
                    { "name", "Full Standby List (Aggressive)" },
                    { "description", "Purges ALL standby cached pages. May briefly slow disk-backed apps while warm cache rebuilds." },
                    { "safe_default", false },
                    { "admin_required", true },
                    { "size_bytes", standbyBytes },
                    { "size_human", SystemInfo.FormatBytes(standbyBytes) }
                },
                new Dictionary<string, object>
                {
                    { "id", "modified_list" },
                    { "name", "Modified Page List (Aggressive)" },
                    { "description", "Flushes dirty modified pages to disk/standby. May cause a brief pause during heavy I/O." },
                    { "safe_default", false },
                    { "admin_required", true },
                    { "size_bytes", modifiedBytes },
                    { "size_human", SystemInfo.FormatBytes(modifiedBytes) }
                }
            };

            List<Dictionary<string, object>> histCopy;
            lock (_historyLock) { histCopy = new List<Dictionary<string, object>>(_boostHistory); }

            return new Dictionary<string, object>
            {
                { "is_admin", isAdmin },
                { "physical", new Dictionary<string, object>
                    {
                        { "total_bytes", physTotal }, { "used_bytes", physUsed }, { "avail_bytes", physAvail },
                        { "total_human", SystemInfo.FormatBytes(physTotal) },
                        { "used_human", SystemInfo.FormatBytes(physUsed) },
                        { "avail_human", SystemInfo.FormatBytes(physAvail) },
                        { "percent", physPercent }
                    }
                },
                { "commit", new Dictionary<string, object>
                    {
                        { "total_bytes", commitTotal }, { "limit_bytes", commitLimit },
                        { "total_human", SystemInfo.FormatBytes(commitTotal) },
                        { "limit_human", SystemInfo.FormatBytes(commitLimit) },
                        { "percent", commitPercent }
                    }
                },
                { "breakdown", new Dictionary<string, object>
                    {
                        { "standby_bytes", standbyBytes }, { "standby_human", SystemInfo.FormatBytes(standbyBytes) },
                        { "low_standby_bytes", lowStandbyBytes }, { "low_standby_human", SystemInfo.FormatBytes(lowStandbyBytes) },
                        { "modified_bytes", modifiedBytes }, { "modified_human", SystemInfo.FormatBytes(modifiedBytes) },
                        { "system_cache_bytes", sysCacheBytes }, { "system_cache_human", SystemInfo.FormatBytes(sysCacheBytes) },
                        { "free_zero_bytes", freeZeroBytes }, { "free_zero_human", SystemInfo.FormatBytes(freeZeroBytes) },
                        { "kernel_paged_human", SystemInfo.FormatBytes(kernelPaged) },
                        { "kernel_nonpaged_human", SystemInfo.FormatBytes(kernelNonPaged) },
                        { "process_count", procCount },
                        { "thread_count", threadCount }
                    }
                },
                { "areas", areas },
                { "top_processes", includeProcesses ? GetTopMemoryProcesses(15) : new List<Dictionary<string, object>>() },
                { "history", histCopy },
                { "settings", SystemInfo.LoadSettings() }
            };
        }

        private static int TrimUserAccessibleWorkingSets()
        {
            int trimmed = 0;
            int myPid = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id <= 4 || p.Id == myPid) continue;
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            if (EmptyWorkingSet(h)) trimmed++;
                        }
                        finally
                        {
                            CloseHandle(h);
                        }
                    }
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return trimmed;
        }

        public static Dictionary<string, object> TrimSingleProcess(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    string name = p.ProcessName + ".exe";
                    long before = p.WorkingSet64;
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        return new Dictionary<string, object> { { "ok", false }, { "message", "Access denied opening process (run as Administrator for elevated processes)." } };
                    }
                    bool ok;
                    try
                    {
                        ok = EmptyWorkingSet(h);
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                    Thread.Sleep(100);
                    p.Refresh();
                    long after = p.WorkingSet64;
                    long freed = Math.Max(0L, before - after);
                    return new Dictionary<string, object>
                    {
                        { "ok", ok },
                        { "pid", pid },
                        { "name", name },
                        { "before_human", SystemInfo.FormatBytes(before) },
                        { "after_human", SystemInfo.FormatBytes(after) },
                        { "freed_bytes", freed },
                        { "freed_human", SystemInfo.FormatBytes(freed) },
                        { "message", string.Format("Trimmed {0} (PID {1}): {2} -> {3} (Freed {4})",
                            name, pid, SystemInfo.FormatBytes(before), SystemInfo.FormatBytes(after), SystemInfo.FormatBytes(freed)) }
                    };
                }
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "ok", false }, { "message", ex.Message } };
            }
        }

        public static Dictionary<string, object> CleanMemory(List<string> areas, bool dryRun = false, LogCallback logCb = null)
        {
            LogCallback log = delegate(string m, string l) { if (logCb != null) logCb(m, l); };
            if (areas == null || areas.Count == 0)
                areas = new List<string> { "working_set", "low_standby", "system_file_cache", "combine_memory" };

            Dictionary<string, object> before = GetMemoryDetails(false);
            Dictionary<string, object> beforePhys = (Dictionary<string, object>)before["physical"];
            Dictionary<string, object> beforeBrk = (Dictionary<string, object>)before["breakdown"];
            long usedBefore = Convert.ToInt64(beforePhys["used_bytes"]);
            long standbyBefore = Convert.ToInt64(beforeBrk["standby_bytes"]);
            long cacheBefore = Convert.ToInt64(beforeBrk["system_cache_bytes"]);

            if (dryRun)
            {
                log(string.Format("[DRY-RUN] Would optimize {0} memory area(s).", areas.Count), "info");
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "dry_run", true },
                    { "ram_freed_bytes", 0L },
                    { "ram_freed_human", "0 B (Simulation)" },
                    { "standby_freed_human", "0 B" },
                    { "combined_human", "0 B" },
                    { "results", new List<Dictionary<string, object>>() },
                    { "after", before }
                };
            }

            bool hasProfilePriv = EnablePrivilege("SeProfileSingleProcessPrivilege");
            bool hasQuotaPriv = EnablePrivilege("SeIncreaseQuotaPrivilege");

            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            long combinedBytes = 0;

            foreach (string area in areas)
            {
                if (area == "working_set")
                {
                    int nt = hasProfilePriv ? CallNtSetMemoryListCommand(MemoryEmptyWorkingSets) : -1;
                    if (nt == 0)
                    {
                        log("Trimmed all system process working sets (NT Kernel).", "success");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", true }, { "detail", "Kernel working sets emptied" } });
                    }
                    else
                    {
                        int cnt = TrimUserAccessibleWorkingSets();
                        log(string.Format("Trimmed working sets of {0} user process(es).", cnt), "success");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", cnt > 0 }, { "detail", string.Format("Trimmed {0} user process(es)", cnt) } });
                    }
                }
                else if (area == "system_file_cache")
                {
                    if (!hasQuotaPriv)
                    {
                        log("Skipped System File Cache: Administrator privileges required.", "warn");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", false }, { "admin_required", true }, { "detail", "Requires Administrator" } });
                        continue;
                    }
                    int sz = Marshal.SizeOf(typeof(SYSTEM_FILECACHE_INFORMATION));
                    IntPtr ptr = Marshal.AllocHGlobal(sz);
                    try
                    {
                        SYSTEM_FILECACHE_INFORMATION sfci = new SYSTEM_FILECACHE_INFORMATION();
                        sfci.MinimumWorkingSet = (IntPtr)(-1L);
                        sfci.MaximumWorkingSet = (IntPtr)(-1L);
                        Marshal.StructureToPtr(sfci, ptr, false);
                        int nt = NtSetSystemInformation(SystemFileCacheInformation, ptr, (uint)sz);
                        bool ok = nt == 0;
                        log(ok ? "Flushed System File Cache." : string.Format("System File Cache returned 0x{0:X8}", nt), ok ? "success" : "warn");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", ok }, { "detail", ok ? "Flushed" : string.Format("NTSTATUS 0x{0:X8}", nt) } });
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
                else if (area == "low_standby")
                {
                    if (!hasProfilePriv)
                    {
                        log("Skipped Low-Priority Standby Cache: Administrator privileges required.", "warn");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", false }, { "admin_required", true }, { "detail", "Requires Administrator" } });
                        continue;
                    }
                    int nt = CallNtSetMemoryListCommand(MemoryPurgeLowPriorityStandbyList);
                    bool ok = nt == 0;
                    log(ok ? "Purged Low-Priority Standby List." : string.Format("Low-Priority Standby returned 0x{0:X8}", nt), ok ? "success" : "warn");
                    results.Add(new Dictionary<string, object> { { "id", area }, { "ok", ok }, { "detail", ok ? "Purged" : string.Format("NTSTATUS 0x{0:X8}", nt) } });
                }
                else if (area == "standby_list")
                {
                    if (!hasProfilePriv)
                    {
                        log("Skipped Full Standby List: Administrator privileges required.", "warn");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", false }, { "admin_required", true }, { "detail", "Requires Administrator" } });
                        continue;
                    }
                    int nt = CallNtSetMemoryListCommand(MemoryPurgeStandbyList);
                    bool ok = nt == 0;
                    log(ok ? "Purged Full Standby List." : string.Format("Full Standby returned 0x{0:X8}", nt), ok ? "success" : "warn");
                    results.Add(new Dictionary<string, object> { { "id", area }, { "ok", ok }, { "detail", ok ? "Purged" : string.Format("NTSTATUS 0x{0:X8}", nt) } });
                }
                else if (area == "modified_list")
                {
                    if (!hasProfilePriv)
                    {
                        log("Skipped Modified Page List: Administrator privileges required.", "warn");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", false }, { "admin_required", true }, { "detail", "Requires Administrator" } });
                        continue;
                    }
                    int nt = CallNtSetMemoryListCommand(MemoryFlushModifiedList);
                    bool ok = nt == 0;
                    log(ok ? "Flushed Modified Page List." : string.Format("Modified Page List returned 0x{0:X8}", nt), ok ? "success" : "warn");
                    results.Add(new Dictionary<string, object> { { "id", area }, { "ok", ok }, { "detail", ok ? "Flushed" : string.Format("NTSTATUS 0x{0:X8}", nt) } });
                }
                else if (area == "registry_cache")
                {
                    if (!hasProfilePriv)
                    {
                        log("Skipped Registry Cache: Administrator privileges required.", "warn");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", false }, { "admin_required", true }, { "detail", "Requires Administrator" } });
                        continue;
                    }
                    int nt = NtSetSystemInformation(SystemRegistryReconciliationInformation, IntPtr.Zero, 0u);
                    bool ok = nt == 0;
                    log(ok ? "Reconciled Registry Cache." : string.Format("Registry Cache returned 0x{0:X8}", nt), ok ? "success" : "warn");
                    results.Add(new Dictionary<string, object> { { "id", area }, { "ok", ok }, { "detail", ok ? "Flushed" : string.Format("NTSTATUS 0x{0:X8}", nt) } });
                }
                else if (area == "combine_memory")
                {
                    if (!hasProfilePriv)
                    {
                        log("Skipped Combine Memory Lists: Administrator privileges required.", "warn");
                        results.Add(new Dictionary<string, object> { { "id", area }, { "ok", false }, { "admin_required", true }, { "detail", "Requires Administrator" } });
                        continue;
                    }
                    int sz = Marshal.SizeOf(typeof(MEMORY_COMBINE_INFORMATION_EX));
                    IntPtr ptr = Marshal.AllocHGlobal(sz);
                    try
                    {
                        MEMORY_COMBINE_INFORMATION_EX mci = new MEMORY_COMBINE_INFORMATION_EX();
                        Marshal.StructureToPtr(mci, ptr, false);
                        int nt = NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, ptr, (uint)sz);
                        if (nt == 0)
                        {
                            MEMORY_COMBINE_INFORMATION_EX outInfo = (MEMORY_COMBINE_INFORMATION_EX)Marshal.PtrToStructure(ptr, typeof(MEMORY_COMBINE_INFORMATION_EX));
                            long pages = (long)outInfo.PagesCombined;
                            long bytesSaved = Math.Max(0L, pages * 4096L);
                            combinedBytes += bytesSaved;
                            log(string.Format("Combined {0} identical memory page(s) ({1}).", pages, SystemInfo.FormatBytes(bytesSaved)), "success");
                            results.Add(new Dictionary<string, object> { { "id", area }, { "ok", true }, { "detail", SystemInfo.FormatBytes(bytesSaved) + " deduplicated" } });
                        }
                        else
                        {
                            log(string.Format("Combine Memory Lists returned 0x{0:X8}", nt), "warn");
                            results.Add(new Dictionary<string, object> { { "id", area }, { "ok", false }, { "detail", string.Format("NTSTATUS 0x{0:X8}", nt) } });
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }

            Thread.Sleep(160);
            Dictionary<string, object> after = GetMemoryDetails(true);
            Dictionary<string, object> afterPhys = (Dictionary<string, object>)after["physical"];
            Dictionary<string, object> afterBrk = (Dictionary<string, object>)after["breakdown"];
            long usedAfter = Convert.ToInt64(afterPhys["used_bytes"]);
            long standbyAfter = Convert.ToInt64(afterBrk["standby_bytes"]);
            long cacheAfter = Convert.ToInt64(afterBrk["system_cache_bytes"]);

            long ramFreed = Math.Max(0L, usedBefore - usedAfter);
            long standbyFreed = Math.Max(0L, standbyBefore - standbyAfter);
            long cacheFreed = Math.Max(0L, cacheBefore - cacheAfter);

            lock (_historyLock)
            {
                _boostHistory.Insert(0, new Dictionary<string, object>
                {
                    { "timestamp", DateTime.Now.ToString("HH:mm:ss") },
                    { "used_before_human", SystemInfo.FormatBytes(usedBefore) },
                    { "used_after_human", SystemInfo.FormatBytes(usedAfter) },
                    { "ram_freed_human", SystemInfo.FormatBytes(ramFreed) },
                    { "standby_freed_human", SystemInfo.FormatBytes(standbyFreed) },
                    { "combined_human", SystemInfo.FormatBytes(combinedBytes) },
                    { "areas_count", areas.Count }
                });
                if (_boostHistory.Count > 20) _boostHistory.RemoveRange(20, _boostHistory.Count - 20);
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "dry_run", false },
                { "ram_freed_bytes", ramFreed },
                { "ram_freed_human", SystemInfo.FormatBytes(ramFreed) },
                { "standby_freed_bytes", standbyFreed },
                { "standby_freed_human", SystemInfo.FormatBytes(standbyFreed) },
                { "cache_freed_human", SystemInfo.FormatBytes(cacheFreed) },
                { "combined_bytes", combinedBytes },
                { "combined_human", SystemInfo.FormatBytes(combinedBytes) },
                { "results", results },
                { "after", after }
            };
        }

        public static void CheckAutoOptimizerTick(Action<string> onAutoCleaned)
        {
            try
            {
                Dictionary<string, object> settings = SystemInfo.LoadSettings();
                bool threshEnabled = settings.ContainsKey("ram_auto_threshold_enabled") && Convert.ToBoolean(settings["ram_auto_threshold_enabled"]);
                int threshPct = settings.ContainsKey("ram_auto_threshold_percent") ? Convert.ToInt32(settings["ram_auto_threshold_percent"]) : 80;
                bool intEnabled = settings.ContainsKey("ram_auto_interval_enabled") && Convert.ToBoolean(settings["ram_auto_interval_enabled"]);
                int intMin = settings.ContainsKey("ram_auto_interval_minutes") ? Math.Max(1, Convert.ToInt32(settings["ram_auto_interval_minutes"])) : 30;

                if (!threshEnabled && !intEnabled) return;
                if ((DateTime.UtcNow - _lastAutoCleanUtc).TotalSeconds < 60) return;

                bool trigger = false;
                string reason = "";
                if (threshEnabled)
                {
                    int curPct = Convert.ToInt32(SystemInfo.GetMemoryStats()["percent"]);
                    if (curPct >= threshPct)
                    {
                        trigger = true;
                        reason = string.Format("RAM reached {0}% (threshold {1}%)", curPct, threshPct);
                    }
                }
                if (!trigger && intEnabled && (DateTime.UtcNow - _lastIntervalCleanUtc).TotalMinutes >= intMin)
                {
                    trigger = true;
                    reason = string.Format("Scheduled every {0} min", intMin);
                }
                if (!trigger) return;

                _lastAutoCleanUtc = DateTime.UtcNow;
                _lastIntervalCleanUtc = DateTime.UtcNow;

                List<string> areas = new List<string> { "working_set", "low_standby", "system_file_cache", "combine_memory" };
                if (settings.ContainsKey("ram_auto_areas") && settings["ram_auto_areas"] is ArrayList)
                {
                    List<string> custom = new List<string>();
                    foreach (object o in (ArrayList)settings["ram_auto_areas"]) custom.Add(o.ToString());
                    if (custom.Count > 0) areas = custom;
                }

                Dictionary<string, object> res = CleanMemory(areas, false, null);
                if (onAutoCleaned != null)
                {
                    onAutoCleaned(string.Format("Auto RAM Boost ({0}): Freed {1} RAM + {2} Standby",
                        reason, res["ram_freed_human"], res["standby_freed_human"]));
                }
            }
            catch { }
        }
    }
}
