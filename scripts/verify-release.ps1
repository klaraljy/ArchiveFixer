#Requires -Version 5.1
<#
.SYNOPSIS
    One-command release verification for the current checkout.

.DESCRIPTION
    Runs the fully automatable checks:
      1. Release build (no incremental)
      2. dotnet format --verify-no-changes
      3. full xUnit suite
      4. self-contained package
      5. NSIS setup
      6. clean-install / overwrite-upgrade / uninstall verification
      7. startup + idle-close smoke

    Manual checks (tray/DPI/Narrator/SmartScreen/power loss) are deliberately
    excluded and must be done by a human.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipPackage,
    [switch]$SkipInstallerVerify
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

function Fail([string]$Text) {
    Write-Host ""
    Write-Host "[FAIL] $Text" -ForegroundColor Red
    exit 1
}

function Run([string]$Name, [string]$File, [string[]]$Arguments) {
    Write-Host ""
    Write-Host "== $Name" -ForegroundColor Cyan
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "$Name exited with $LASTEXITCODE." }
}

try {
    Run 'Release build' 'dotnet' @('build', 'ArchiveFixer.slnx', '-c', 'Release', '--no-incremental', '--nologo')
    Run 'dependency vulnerability scan' 'dotnet' @('list', 'src/ArchiveFixer/ArchiveFixer.csproj', 'package', '--vulnerable', '--include-transitive', '--no-restore')
    Run 'secret scan' 'pwsh' @('-NoProfile', '-File', 'scripts/scan-secrets.ps1')
    Run 'format' 'dotnet' @('format', 'ArchiveFixer.slnx', '--verify-no-changes', '--no-restore')

    if (-not $SkipTests) {
        Run 'full tests' 'dotnet' @('test', 'tests/ArchiveFixer.Tests/ArchiveFixer.Tests.csproj', '--nologo')
    }

    if (-not $SkipPackage) {
        Run 'self-contained package' 'pwsh' @('-NoProfile', '-File', 'scripts/package.ps1', '-SelfContained')
        Run 'setup.exe' 'pwsh' @('-NoProfile', '-File', 'scripts/installer.ps1', '-Quiet')
    }

    if (-not $SkipInstallerVerify) {
        Run 'installer clean/upgrade/uninstall' 'pwsh' @('-NoProfile', '-File', 'scripts/verify-installer.ps1')
    }

    Run 'startup smoke' 'pwsh' @('-NoProfile', '-File', 'scripts/verify-startup.ps1')

    Write-Host ""
    Write-Host "[PASS] automated release verification" -ForegroundColor Green
    Write-Host "Manual checks still required: tray busy X/restore, multi-monitor/DPI, high contrast/Narrator, SmartScreen/Defender, power loss."
}
finally {
    Pop-Location
}
