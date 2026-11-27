<#
.SYNOPSIS
  One-click installer & builder for to-do-list (Windows 10/11 native desktop sticky note).
.DESCRIPTION
  Zero Electron bloat (~84 KB native C# WinForms + Edge WebView2).
  Supports running either from a cloned repository or remotely via:
    irm https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main/windows/install.ps1 | iex
#>
param(
  [string]$InstallDir = "$env:LOCALAPPDATA\to-do-list",
  [switch]$NoAutoStart,
  [switch]$NoLaunch,
  [string]$RepoBase = "https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main"
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-Step([string]$msg) { Write-Host "[to-do-list] $msg" -ForegroundColor Cyan }

# 1. Locate .NET Framework C# compiler (built into Windows 10/11)
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
  $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
  throw "Could not find .NET Framework csc.exe. Please ensure .NET Framework 4.x is enabled on Windows."
}

# 2. Prepare staging directory & resolve source files (local repo or GitHub raw)
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { "" }
$localRoot = if ($scriptDir -and (Test-Path (Join-Path $scriptDir "DesktopNote.cs"))) { Split-Path $scriptDir -Parent } else { "" }
$stage = Join-Path ([IO.Path]::GetTempPath()) ("to-do-list-build-" + [Guid]::NewGuid().ToString("N"))
$appStage = Join-Path $stage "app"
New-Item -ItemType Directory -Path $appStage -Force | Out-Null

try {
  if ($localRoot -and (Test-Path (Join-Path $localRoot "index.html"))) {
    Write-Step "Using local repository sources from $localRoot"
    Copy-Item (Join-Path $localRoot "windows\DesktopNote.cs") (Join-Path $stage "DesktopNote.cs") -Force
    foreach ($f in @("index.html", "fusion-pixel-sc.woff2", "fusion-pixel-license.txt", "manifest.webmanifest", "icon-192.png", "icon-256.png")) {
      $src = Join-Path $localRoot $f
      if (Test-Path $src) { Copy-Item $src (Join-Path $appStage $f) -Force }
    }
  } else {
    Write-Step "Downloading latest sources from GitHub ($RepoBase)..."
    Invoke-WebRequest -Uri "$RepoBase/windows/DesktopNote.cs" -OutFile (Join-Path $stage "DesktopNote.cs") -UseBasicParsing
    foreach ($f in @("index.html", "fusion-pixel-sc.woff2", "fusion-pixel-license.txt", "manifest.webmanifest", "icon-192.png", "icon-256.png")) {
      Invoke-WebRequest -Uri "$RepoBase/$f" -OutFile (Join-Path $appStage $f) -UseBasicParsing
    }
  }

  # 3. Ensure WebView2 managed & native DLLs (reuse existing install if present, else fetch official NuGet)
  $needDlls = @("Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll")
  $haveExisting = $true
  foreach ($d in $needDlls) {
    if (-not (Test-Path (Join-Path $InstallDir $d))) { $haveExisting = $false; break }
  }

  if ($haveExisting) {
    Write-Step "Reusing installed WebView2 runtime bindings from $InstallDir"
    foreach ($d in $needDlls) { Copy-Item (Join-Path $InstallDir $d) (Join-Path $stage $d) -Force }
  } else {
    Write-Step "Fetching official Microsoft.Web.WebView2 NuGet package..."
    $nupkg = Join-Path $stage "webview2.zip"
    $extract = Join-Path $stage "wv2"
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/1.0.2903.40" -OutFile $nupkg -UseBasicParsing
    Expand-Archive -Path $nupkg -DestinationPath $extract -Force
    Copy-Item (Join-Path $extract "lib\net462\Microsoft.Web.WebView2.Core.dll") (Join-Path $stage "Microsoft.Web.WebView2.Core.dll") -Force
    Copy-Item (Join-Path $extract "lib\net462\Microsoft.Web.WebView2.WinForms.dll") (Join-Path $stage "Microsoft.Web.WebView2.WinForms.dll") -Force
    $arch = if ([Environment]::Is64BitOperatingSystem) { "win-x64" } else { "win-x86" }
    Copy-Item (Join-Path $extract "runtimes\$arch\native\WebView2Loader.dll") (Join-Path $stage "WebView2Loader.dll") -Force
  }

  # 4. Compile lightweight native executable
  Write-Step "Compiling native to-do-list.exe..."
  $outExe = Join-Path $stage "to-do-list.exe"
  $platform = if ([Environment]::Is64BitOperatingSystem) { "x64" } else { "x86" }
  & $csc /nologo /target:winexe /platform:$platform /optimize+ /codepage:65001 `
    "/out:$outExe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "/r:$(Join-Path $stage 'Microsoft.Web.WebView2.Core.dll')" `
    "/r:$(Join-Path $stage 'Microsoft.Web.WebView2.WinForms.dll')" `
    (Join-Path $stage "DesktopNote.cs")
  if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

  # 5. Stop running instance cleanly (preserving Profile data)
  $installedExe = Join-Path $InstallDir "to-do-list.exe"
  if (Test-Path $installedExe) {
    Write-Step "Stopping existing to-do-list instance..."
    try { & $installedExe --shutdown } catch { }
    for ($i = 0; $i -lt 20; $i++) {
      if (-not (Get-Process -Name "to-do-list" -ErrorAction SilentlyContinue)) { break }
      Start-Sleep -Milliseconds 150
    }
    Get-Process -Name "to-do-list" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
  }

  # 6. Deploy files to InstallDir (never touch Profile/)
  $destApp = Join-Path $InstallDir "app"
  New-Item -ItemType Directory -Path $destApp -Force | Out-Null
  Copy-Item $outExe $installedExe -Force
  foreach ($d in $needDlls) { Copy-Item (Join-Path $stage $d) (Join-Path $InstallDir $d) -Force }
  Copy-Item (Join-Path $appStage "*") $destApp -Force

  # 7. Configure Windows logon auto-start
  if (-not $NoAutoStart) {
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "to-do-list" -Value "`"$installedExe`""
    Write-Step "Registered Windows startup autolaunch (HKCU\...\Run\to-do-list)."
  }

  # 8. Launch in background
  if (-not $NoLaunch) {
    Start-Process -FilePath $installedExe -WorkingDirectory $InstallDir
    Write-Step "Installed and started! Move your cursor to the top-left corner of your screen to peek."
  } else {
    Write-Step "Installed to $InstallDir (launch skipped via -NoLaunch)."
  }
} finally {
  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
