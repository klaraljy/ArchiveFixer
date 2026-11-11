#requires -Version 5.1
<#
  125 批测试操作总控。
  只读备份位置，复制/运行由 Batch120OperationTests 完成；
  操作记录和日志固定写 测试\日志位置\<seed>-125批次-日志\操作。
#>
[CmdletBinding()]
param(
    [string]$Seed = '917000000',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $testRoot
$backup = Join-Path $testRoot "备份位置\$Seed-125批次-备份"
$testRootPath = Join-Path $testRoot "测试位置\$Seed-125批次-测试未指定位置"
$logRoot = Join-Path $testRoot "日志位置\$Seed-125批次-日志\操作"

if (-not (Test-Path -LiteralPath $backup)) {
    throw "备份不存在：$backup"
}

New-Item -ItemType Directory -Path $testRootPath -Force | Out-Null
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

$env:ARCHIVEFIXER_120_SEED = $Seed
$env:ARCHIVEFIXER_125_ROOT = $testRoot

$testProject = Join-Path $repoRoot 'tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj'
$args = @('test', $testProject, '--nologo', '--filter', 'FullyQualifiedName~Batch120OperationTests')
if ($NoBuild) { $args += '--no-build' }

Write-Host "125批测试操作：seed=$Seed"
Write-Host "备份：$backup"
Write-Host "测试位置：$testRootPath"
Write-Host "日志：$logRoot"
& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
