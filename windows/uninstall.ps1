<#
.SYNOPSIS
  Uninstall to-do-list Windows desktop sticky note.
.PARAMETER RemoveData
  Also delete the local Profile folder (saved tasks, notes, clipboard, and theme preferences).
#>
param(
  [string]$InstallDir = "$env:LOCALAPPDATA\to-do-list",
  [switch]$RemoveData
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $InstallDir "to-do-list.exe"
if (Test-Path $exe) {
  try { & $exe --shutdown } catch { }
  for ($i = 0; $i -lt 20; $i++) {
    if (-not (Get-Process -Name "to-do-list" -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 150
  }
}
Get-Process -Name "to-do-list" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "to-do-list" -ErrorAction SilentlyContinue

if (Test-Path $InstallDir) {
  if ($RemoveData) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "[to-do-list] Uninstalled completely (including saved data)." -ForegroundColor Yellow
  } else {
    Get-ChildItem $InstallDir -Force | Where-Object { $_.Name -ne "Profile" } | Remove-Item -Recurse -Force
    Write-Host "[to-do-list] Uninstalled app & autostart. Your local task data is kept in: $InstallDir\Profile" -ForegroundColor Green
  }
} else {
  Write-Host "[to-do-list] Nothing to uninstall at $InstallDir." -ForegroundColor Gray
}
