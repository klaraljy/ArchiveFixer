#requires -Version 5.1
<#
  125 批测试检测总控。
  只读 recipe + 操作记录 + 磁盘 + 日志；报告固定写
  测试\日志位置\<seed>-125批次-日志\检测。
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
$logRoot = Join-Path $testRoot "日志位置\$Seed-125批次-日志\检测"

if (-not (Test-Path -LiteralPath $backup)) {
    throw "备份不存在：$backup"
}

New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$env:ARCHIVEFIXER_120_SEED = $Seed
$env:ARCHIVEFIXER_125_ROOT = $testRoot

$testProject = Join-Path $repoRoot 'tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj'
$args = @('test', $testProject, '--nologo', '--filter', 'FullyQualifiedName~Batch120VerifyTests')
if ($NoBuild) { $args += '--no-build' }

Write-Host "125批测试检测：seed=$Seed"
Write-Host "报告目录：$logRoot"
& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
