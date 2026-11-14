#requires -Version 5.1
<#
  125 批测试操作总控。
  只读备份位置，复制/运行由 Batch120OperationTests 完成；
  操作记录和日志写显式运行根（默认 _tmp\ArchiveFixer\125-runs），检测必须读同一个根。
#>
[CmdletBinding()]
param(
    [string]$Seed = '917000000',
    [string]$RunRoot = '',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $testRoot
$backup = Join-Path $testRoot "备份位置\$Seed-125批次-备份"

if (-not (Test-Path -LiteralPath $backup)) {
    throw "备份不存在：$backup"
}

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path (Split-Path -Parent $repoRoot) "_tmp\ArchiveFixer\125-runs\$Seed-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID"
}

$RunRoot = [System.IO.Path]::GetFullPath($RunRoot)
$runRootsParent = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $repoRoot) "_tmp\ArchiveFixer\125-runs"))
if (-not $RunRoot.StartsWith($runRootsParent + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "运行根必须位于 $runRootsParent 下：$RunRoot"
}

$testRootPath = Join-Path $RunRoot "测试位置\$Seed-125批次-测试未指定位置"
$logRoot = Join-Path $RunRoot "日志位置\$Seed-125批次-日志\操作"
New-Item -ItemType Directory -Path $testRootPath -Force | Out-Null
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

$env:ARCHIVEFIXER_120_SEED = $Seed
$env:ARCHIVEFIXER_125_ROOT = $testRoot
$env:ARCHIVEFIXER_125_RUN_ROOT = $RunRoot
$env:ARCHIVEFIXER_125_KEEP_RUN = '1'

$testProject = Join-Path $repoRoot 'tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj'
$args = @('test', $testProject, '--nologo', '--filter', 'FullyQualifiedName~Batch120OperationTests')
if ($NoBuild) { $args += '--no-build' }

Write-Host "125批测试操作：seed=$Seed"
Write-Host "备份（只读）：$backup"
Write-Host "运行根：$RunRoot"
Write-Host "测试位置：$testRootPath"
Write-Host "日志：$logRoot"
& dotnet @args
$code = $LASTEXITCODE
Write-Host "运行根保留给检测：$RunRoot"
if ($code -ne 0) { exit $code }
