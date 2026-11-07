#Requires -Version 5.1
<#
.SYNOPSIS
    Measure cold-start time and verify that an idle window close exits cleanly.

.DESCRIPTION
    This is an executable smoke check, not a visual/accessibility check.
    It launches the app, waits for a main window, records elapsed time, sends
    WM_CLOSE to that main window, and verifies the process exits.
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$MaxStartupMs = 5000
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Text) {
    Write-Host ""
    Write-Host "[FAIL] $Text" -ForegroundColor Red
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ExePath) {
    $candidate = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter 'ArchiveFixer.exe' -File -ErrorAction SilentlyContinue } |
        Select-Object -First 1

    if (-not $candidate) {
        $candidate = Get-Item -LiteralPath 'E:\ArchiveFixer\ArchiveFixer.exe' -ErrorAction SilentlyContinue
    }
    if (-not $candidate) { Fail "No ArchiveFixer.exe found. Build/package or pass -ExePath." }
    $ExePath = $candidate.FullName
}

$ExePath = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $ExePath)) { Fail "Executable not found: $ExePath" }
if (@(Get-Process ArchiveFixer -ErrorAction SilentlyContinue).Count -gt 0) {
    Fail "ArchiveFixer is already running. Close it before startup verification."
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StartupVerifyNative {
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
'@

$sw = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath) -PassThru

try {
    $window = [IntPtr]::Zero
    for ($i = 0; $i -lt 200; $i++) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { Fail "App exited before showing a window." }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            $window = $process.MainWindowHandle
            break
        }
    }
    $sw.Stop()

    if ($window -eq [IntPtr]::Zero) { Fail "Main window did not appear within 20 seconds." }
    Write-Host "Startup: $($sw.ElapsedMilliseconds) ms (limit $MaxStartupMs ms)"
    if ($sw.ElapsedMilliseconds -gt $MaxStartupMs) {
        Fail "Startup exceeded limit: $($sw.ElapsedMilliseconds) ms."
    }

    [StartupVerifyNative]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { break }
    }

    if (-not $process.HasExited) { Fail "Idle window close did not terminate the process." }
    if ($process.ExitCode -ne 0) { Fail "Process exited with code $($process.ExitCode)." }

    Write-Host "[PASS] startup and idle close" -ForegroundColor Green
}
finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
    }
}
