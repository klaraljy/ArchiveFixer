#Requires -Version 5.1
<#
.SYNOPSIS
    Automated clean-install / overwrite-upgrade / uninstall verification.

.DESCRIPTION
    Requires an already-built setup.exe (scripts/installer.ps1). It installs into a
    temporary directory using /S /D=, simulates stale program files, upgrades in
    place, checks that stale files disappear, verifies that %LOCALAPPDATA%\ArchiveFixer
    file hashes are unchanged, then uninstalls and checks cleanup.

    This script refuses to run when the machine already has ArchiveFixer installed
    under HKCU, so it never overwrites a real user installation.

    Manual tray/DPI/Narrator/SmartScreen checks are intentionally outside this script.
#>
[CmdletBinding()]
param(
    [string]$SetupPath,
    [string]$TestRoot,
    [switch]$KeepTestRoot
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Text) {
    Write-Host ""
    Write-Host "[FAIL] $Text" -ForegroundColor Red
    exit 1
}

function Step([string]$Text) {
    Write-Host ""
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Get-FileSha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appKey = 'HKCU:\Software\ArchiveFixer'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ArchiveFixer'
$userData = Join-Path $env:LOCALAPPDATA 'ArchiveFixer'

if (-not $SetupPath) {
    $candidate = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') -Filter 'ArchiveFixer-*-setup.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        Fail "No setup.exe found under dist. Run scripts/installer.ps1 first."
    }

    $SetupPath = $candidate.FullName
}

$SetupPath = [IO.Path]::GetFullPath($SetupPath)
if (-not (Test-Path -LiteralPath $SetupPath)) { Fail "Setup not found: $SetupPath" }
if (@(Get-Process ArchiveFixer -ErrorAction SilentlyContinue).Count -gt 0) {
    Fail "ArchiveFixer is running. Close it before running installer verification."
}
if ((Test-Path -LiteralPath $appKey) -or (Test-Path -LiteralPath $uninstallKey)) {
    Fail "ArchiveFixer is already installed for this user. This script refuses to disturb a real installation."
}

if (-not $TestRoot) {
    $TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('ArchiveFixerInstallerVerify-' + [Guid]::NewGuid().ToString('N'))
}
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
$installDir = Join-Path $TestRoot 'install'
$staleEngine = Join-Path $installDir 'tools\7zip\stale-verify.exe'
$staleDoc = Join-Path $installDir 'docs\stale-verify.md'
$staleLanguage = Join-Path $installDir 'cs\stale-verify.resources.dll'

$userDataFiles = @('appsettings.json', 'passwords.txt', 'passwords.meta.json')
$beforeHashes = @{}
foreach ($name in $userDataFiles) {
    $beforeHashes[$name] = Get-FileSha256 (Join-Path $userData $name)
}

Write-Host "Setup       : $SetupPath"
Write-Host "Test root   : $TestRoot"
Write-Host "Install dir : $installDir"

try {
    New-Item -ItemType Directory -Path $TestRoot -Force | Out-Null

    Step '1/5 clean install'
    $clean = Start-Process -FilePath $SetupPath -ArgumentList "/S /D=$installDir" -Wait -PassThru
    if ($clean.ExitCode -ne 0) { Fail "Clean install exited with $($clean.ExitCode)." }

    foreach ($required in @('ArchiveFixer.exe', 'tools\7zip\7z.exe', 'tools\unrar\UnRAR.exe')) {
        $full = Join-Path $installDir $required
        if (-not (Test-Path -LiteralPath $full)) { Fail "Clean install missing: $required" }
    }
    $doc = Get-ChildItem -LiteralPath (Join-Path $installDir 'docs') -Filter '*.md' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $doc) { Fail "Clean install missing docs/*.md." }
    if (Test-Path -LiteralPath (Join-Path $installDir 'data')) {
        Fail "Installer created a program-directory data folder."
    }

    Step '2/5 simulate stale files from an older version'
    Set-Content -LiteralPath $staleEngine -Value 'stale' -Encoding Ascii
    Set-Content -LiteralPath $staleDoc -Value 'stale' -Encoding Ascii
    New-Item -ItemType Directory -Path (Split-Path $staleLanguage -Parent) -Force | Out-Null
    Set-Content -LiteralPath $staleLanguage -Value 'stale' -Encoding Ascii

    Step '3/5 overwrite upgrade'
    $upgrade = Start-Process -FilePath $SetupPath -ArgumentList "/S /D=$installDir" -Wait -PassThru
    if ($upgrade.ExitCode -ne 0) { Fail "Upgrade exited with $($upgrade.ExitCode)." }

    foreach ($stale in @($staleEngine, $staleDoc, $staleLanguage)) {
        if (Test-Path -LiteralPath $stale) { Fail "Stale file survived upgrade: $stale" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installDir 'ArchiveFixer.exe'))) { Fail "Upgrade removed the main executable." }

    Step '4/5 verify user data was not touched'
    foreach ($name in $userDataFiles) {
        $after = Get-FileSha256 (Join-Path $userData $name)
        if ($beforeHashes[$name] -ne $after) {
            Fail "User data changed during install/upgrade: $name"
        }
    }

    Step '5/5 uninstall and verify cleanup'
    $uninstaller = Join-Path $installDir 'Uninstall.exe'
    if (-not (Test-Path -LiteralPath $uninstaller)) { Fail "Uninstall.exe was not installed." }

    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList '/S' -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { Fail "Uninstall exited with $($uninstall.ExitCode)." }

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Test-Path -LiteralPath $installDir) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if (Test-Path -LiteralPath $installDir) { Fail "Uninstall left the install directory behind: $installDir" }
    if ((Test-Path -LiteralPath $appKey) -or (Test-Path -LiteralPath $uninstallKey)) {
        Fail "Uninstall left HKCU registry keys behind."
    }
    if (-not (Test-Path -LiteralPath $userData)) {
        Fail "Uninstall removed %LOCALAPPDATA%\ArchiveFixer."
    }

    Write-Host ""
    Write-Host "[PASS] clean install / overwrite upgrade / uninstall" -ForegroundColor Green
}
finally {
    foreach ($key in @($appKey, $uninstallKey)) {
        if (Test-Path -LiteralPath $key) {
            Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not $KeepTestRoot -and (Test-Path -LiteralPath $TestRoot)) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
