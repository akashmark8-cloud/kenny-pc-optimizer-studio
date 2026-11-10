# Kenny PC Cleaner & Optimization Studio (Standalone Native App)

A **100% standalone, zero-dependency Windows application** (`KennyPCOptimizer_Standalone.exe`) compiled natively for .NET Framework 4.x. All backend cleaning engines, SQLite history scrubbing (`winsqlite3.dll`), registry & backup orchestration, and the interactive UI are compiled/embedded directly inside the single `.exe` — **no Python, no external `backend/` files, and no extracted runtime folders required**.

## Running the App
- Copy or move `KennyPCOptimizer_Standalone.exe` anywhere (Desktop, Downloads, USB drive) and double-click it.
- Right-click → **Run as administrator** (or click **Elevate (UAC)** in the app header) for full access to Windows System Temp, Windows Update Cache (`wuauserv`), `HKLM\SOFTWARE` registry keys, and Windows System Restore Points.

## Core Capabilities
1. **Master_Delete.bat Suite Upgrade**:
   - User Temp (`%TEMP%`), Windows System Temp (`C:\Windows\Temp`), NVIDIA DXCache (+ optional D3DSCache/GLCache/AMD DxCache), Recycle Bin (all drives), Windows Update Download Cache (`wuauserv` safe stop/restart), Developer Caches (`npm-cache`, `pip\cache`), and Game Crash Dumps (`CS2 .mdmp` across Steam libraries + `PUBG Crashes & Logs`).
2. **Universal Multi-Browser Cleaner**:
   - Detects installed & leftover profiles across Chromium, Gecko/Firefox, and IE/WebCache engines.
   - **Bookmark-Safe Firefox History Scrubbing** via Windows' native `winsqlite3.dll` (removes `moz_historyvisits` while preserving 100% of bookmarks).
3. **Registry Empty Folders Cleaner**:
   - Scans `HKCU\Software`, `HKLM\SOFTWARE`, and `HKLM\SOFTWARE\WOW6432Node` for empty registry keys (`ValueCount == 0`, empty subtrees) with strict OS key exclusions and bottom-up re-verified deletion.
4. **Mandatory Pre-Clean Backup & Recovery Vault**:
   - Saves timestamped snapshots to `%USERPROFILE%\Documents\Kenny_PC_Optimizer_Backups` containing surgical `.reg` undo files, compressed full `HKCU\Software` & `HKLM\SOFTWARE` `.reg` backups, `PC_Files_Snapshot.zip`, Windows System Restore Point (when elevated), standalone `EMERGENCY_RESTORE.bat`, and SHA-256 `manifest.json`.
5. **RAM & Kernel Memory Optimizer**:
   - Real-time physical/standby/modified/system-cache breakdown (`NtQuerySystemInformation` + `GetPerformanceInfo`), 7 cleanable NT memory regions (Process Working Sets, Low-Priority Standby Cache, System File Cache, Combine Memory Lists, Registry Cache, Full Standby List, Modified Page List), per-process selective `EmptyWorkingSet` trimming, tray quick-boost, and configurable threshold/interval Auto-Boost rules.
6. **Drivers, App Updates & Smooth-PC Essentials**:
   - Inspects key signed PnP hardware drivers, queries pending Windows Update drivers/security updates, checks `winget upgrade` for outdated apps (with 1-click update), and recommends missing core runtimes & smoothness tools with 1-click `winget` installation.

## Rebuilding from Source
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\build.ps1
```
*(Pass `-OutName KennyPCOptimizer_Standalone.exe` if an older running instance locks `KennyPCOptimizer.exe`.)*
