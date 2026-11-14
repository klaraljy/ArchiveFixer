#requires -Version 5.1
<#
  125 批测试检测总控。
  只读 recipe + 操作记录 + 磁盘 + 日志；检测读写同一个显式运行根。
  928 备份只读，绝不删除、覆盖或重造。
#>
[CmdletBinding()]
param(
    [string]$Seed = '917000000',
    [string]$RunRoot = '',
    [switch]$NoBuild,
    [switch]$Cleanup
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $testRoot
$backup = Join-Path $testRoot "备份位置\$Seed-125批次-备份"
$runRootsParent = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $repoRoot) "_tmp\ArchiveFixer\125-runs"))

if (-not (Test-Path -LiteralPath $backup)) {
    throw "备份不存在：$backup"
}

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    if (-not (Test-Path -LiteralPath $runRootsParent)) {
        throw "找不到运行根；请先跑 125批测试操作.ps1，或显式传 -RunRoot"
    }

    $latest = Get-ChildItem -LiteralPath $runRootsParent -Directory -Filter "$Seed-*" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $latest) {
        throw "没有 $Seed 的运行根；请先跑 125批测试操作.ps1，或显式传 -RunRoot"
    }

    $RunRoot = $latest.FullName
}

$RunRoot = [System.IO.Path]::GetFullPath($RunRoot)
if (-not $RunRoot.StartsWith($runRootsParent + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "运行根必须位于 $runRootsParent 下：$RunRoot"
}

$logRoot = Join-Path $RunRoot "日志位置\$Seed-125批次-日志\检测"
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

$env:ARCHIVEFIXER_120_SEED = $Seed
$env:ARCHIVEFIXER_125_ROOT = $testRoot
$env:ARCHIVEFIXER_125_RUN_ROOT = $RunRoot
$env:ARCHIVEFIXER_125_KEEP_RUN = '1'

$testProject = Join-Path $repoRoot 'tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj'
$args = @('test', $testProject, '--nologo', '--filter', 'FullyQualifiedName~Batch120VerifyTests')
if ($NoBuild) { $args += '--no-build' }

Write-Host "125批测试检测：seed=$Seed"
Write-Host "备份（只读）：$backup"
Write-Host "运行根：$RunRoot"
Write-Host "报告目录：$logRoot"
& dotnet @args
$code = $LASTEXITCODE
if ($code -eq 0 -and $Cleanup) {
    # 只删本次运行根；备份永远不碰。
    Remove-Item -LiteralPath $RunRoot -Recurse -Force
    Write-Host "运行根已清理：$RunRoot"
} else {
    Write-Host "运行根保留：$RunRoot"
}
if ($code -ne 0) { exit $code }
