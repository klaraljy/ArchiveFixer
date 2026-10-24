# 第一大步：一轮造包 + 一轮跑，连续 N 轮（默认 5）。每轮全绿才进下一轮。
#
# 用法：pwsh -NoProfile -File samples/rounds-first-step.ps1 [-Rounds 5]
param(
    [int]$Rounds = 5,
    [string]$Root = 'H:\BaiduNetdiskDownload\大测试\第一大步',
    [string]$Work = 'E:\DeepSeekProjects\_tmp\ArchiveFixer\first-step-rounds',
    [string]$Exe = 'E:\DeepSeekProjects\_tmp\ArchiveFixer\DebugRunner\bin\Debug\net8.0-windows\ArchiveFixer.Tests.exe'
)

$ErrorActionPreference = 'Stop'

for ($round = 1; $round -le $Rounds; $round++) {
    Write-Output ""
    Write-Output ("================ 第 {0}/{1} 轮：造包 ================" -f $round, $Rounds)

    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'generate-first-step.ps1') -Root $Root -Seed (1000 + $round * 7)
    if ($LASTEXITCODE -ne 0) { Write-Output "第 $round 轮造包失败 ⇒ 停"; exit 2 }

    Write-Output ("================ 第 {0}/{1} 轮：跑并判红绿 ================" -f $round, $Rounds)

    $roundWork = Join-Path $Work ("round{0}" -f $round)
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'run-first-step.ps1') -Root $Root -Work $roundWork -Exe $Exe

    if ($LASTEXITCODE -ne 0) {
        Write-Output ("第 {0} 轮有红 ⇒ 按规则停在这里，不进入下一轮。" -f $round)
        exit 1
    }

    Write-Output ("第 {0} 轮全绿 ⇒ 进入第 {1} 轮。" -f $round, ($round + 1))
}

Write-Output ""
Write-Output ("{0} 轮全部全绿。" -f $Rounds)
