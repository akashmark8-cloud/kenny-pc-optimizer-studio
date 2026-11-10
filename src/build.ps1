param(
    [string]$OutName = "KennyPCOptimizer.exe"
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$SrcDir = $PSScriptRoot
$Csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $Csc)) {
    throw "csc.exe not found at $Csc"
}

$OutExe = Join-Path $RootDir $OutName
$IconPath = Join-Path $SrcDir "assets\app_icon.ico"
$ManifestPath = Join-Path $SrcDir "app.manifest"

$CsFiles = @(
    (Join-Path $SrcDir "Config.cs"),
    (Join-Path $SrcDir "SystemInfo.cs"),
    (Join-Path $SrcDir "SqliteHelper.cs"),
    (Join-Path $SrcDir "JobManager.cs"),
    (Join-Path $SrcDir "BackupManager.cs"),
    (Join-Path $SrcDir "SystemCleaner.cs"),
    (Join-Path $SrcDir "BrowserCleaner.cs"),
    (Join-Path $SrcDir "RegistryCleaner.cs"),
    (Join-Path $SrcDir "MemoryOptimizer.cs"),
    (Join-Path $SrcDir "SystemAdvisor.cs"),
    (Join-Path $SrcDir "WebServer.cs"),
    (Join-Path $SrcDir "Program.cs")
)

$ArgsList = @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/platform:x64",
    "/out:$OutExe",
    "/win32icon:$IconPath",
    "/win32manifest:$ManifestPath",
    "/resource:$(Join-Path $SrcDir 'ui\index.html'),ui.index.html",
    "/resource:$(Join-Path $SrcDir 'ui\styles.css'),ui.styles.css",
    "/resource:$(Join-Path $SrcDir 'ui\app.js'),ui.app.js",
    "/resource:$IconPath,assets.app_icon.ico",
    "/r:System.dll",
    "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.IO.Compression.dll",
    "/r:System.IO.Compression.FileSystem.dll",
    "/r:System.ServiceProcess.dll",
    "/r:System.Management.dll",
    "/r:System.Web.Extensions.dll"
) + $CsFiles

& $Csc @ArgsList
if ($LASTEXITCODE -ne 0) {
    throw "csc.exe compilation failed with exit code $LASTEXITCODE"
}

$Info = Get-Item $OutExe
Write-Host "Built standalone single-file executable: $($Info.FullName) ($($Info.Length) bytes)"
