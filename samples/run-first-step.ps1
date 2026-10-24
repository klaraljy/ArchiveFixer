# 第一大步 runner：按 recipe.json 跑一遍并判红绿（用户 2026-10-24）
#
# 用法：
#   pwsh -NoProfile -File samples/run-first-step.ps1                   # 跑全部样本 × 两种落点模式
#   pwsh -NoProfile -File samples/run-first-step.ps1 -Sample A01,C02   # 只跑指定的几份
#   pwsh -NoProfile -File samples/run-first-step.ps1 -KeepFixture      # 留现场（默认跑完删夹具）
#
# 它做什么（每份样本 × 每个 mode）：
#   ① 把样本文件复制到临时夹具；写密码本；写 `debug-sample.txt`（第 3 行 = 空/`custom`）
#   ② 起无头 runner（DebugRunner，走真管线 + 真 7z），让它把结果写成 result.json
#   ③ 逐条断言：终态 / 内容物 / 落点模式 / 残留 / 源包不动 / 不许逃出目录
#   ④ 输出一张表：样本 / 模式 / 期望 / 实际 / 结论 / 原因
param(
    [string]$Root = 'H:\BaiduNetdiskDownload\大测试\第一大步',
    [string]$Work = 'E:\DeepSeekProjects\_tmp\ArchiveFixer\first-step-run',
    [string]$Sample = 'all',
    [string]$Exe = 'E:\DeepSeekProjects\_tmp\ArchiveFixer\DebugRunner\bin\Debug\net8.0-windows\ArchiveFixer.Tests.exe',
    [switch]$KeepFixture
)

$ErrorActionPreference = 'Stop'

$configPath = 'E:\DeepSeekProjects\_tmp\ArchiveFixer\debug-sample.txt'
$configBackup = if (Test-Path $configPath) { Get-Content -LiteralPath $configPath -Raw } else { $null }

if (-not (Test-Path -LiteralPath $Exe)) { throw "缺 runner：$Exe（先 dotnet build 那个 csproj）" }
if (-not (Test-Path -LiteralPath $Root)) { throw "缺样本根：$Root（先跑 generate-first-step.ps1）" }

New-Item -ItemType Directory -Path $Work -Force | Out-Null

$dirs = Get-ChildItem -LiteralPath $Root -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'recipe.json') }
if ($Sample -ne 'all') {
    $wanted = $Sample.Split(',') | ForEach-Object { $_.Trim() }
    $dirs = $dirs | Where-Object { $wanted -contains $_.Name }
}

$rows = New-Object System.Collections.Generic.List[object]

function Test-ContainsSuffix {
    param([string]$Root, [string]$Suffix)
    $s = $Suffix -replace '/', '\'
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName.EndsWith($s, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
}

try {
    foreach ($dir in $dirs) {
        $recipe = Get-Content -LiteralPath (Join-Path $dir.FullName 'recipe.json') -Raw | ConvertFrom-Json
        $modes = @($recipe.modes)

        foreach ($mode in $modes) {
            $runRoot = Join-Path $Work ("{0}-{1}" -f $recipe.id, $mode)
            if (Test-Path $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
            $stage = Join-Path $runRoot 'src'
            New-Item -ItemType Directory -Path $stage -Force | Out-Null

            # 样本文件复制过去（recipe.json 不参与导入）
            $sampleFiles = @()
            foreach ($f in Get-ChildItem -LiteralPath $dir.FullName -File) {
                if ($f.Name -eq 'recipe.json') { continue }
                Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $stage $f.Name)
                $sampleFiles += $f.Name
            }
            foreach ($sub in Get-ChildItem -LiteralPath $dir.FullName -Directory) {
                Copy-Item -LiteralPath $sub.FullName -Destination (Join-Path $stage $sub.Name) -Recurse
            }

            # 密码本
            $bookPath = Join-Path $runRoot 'book.txt'
            Set-Content -LiteralPath $bookPath -Value (@($recipe.passwordBook) -join "`r`n") -Encoding utf8

            # debug-sample.txt：第 1 行样本、第 2 行密码本、第 3 行档位（空 / custom）
            $modeArg = if ($mode -eq 'custom') { 'custom' } else { '' }
            Set-Content -LiteralPath $configPath -Value (@($stage, $bookPath, $modeArg) -join "`r`n") -Encoding utf8

            $resultPath = Join-Path $runRoot 'result.json'
            $consolePath = Join-Path $runRoot 'console.txt'
            if (Test-Path $resultPath) { Remove-Item -LiteralPath $resultPath -Force }
            if (Test-Path $consolePath) { Remove-Item -LiteralPath $consolePath -Force }

            $env:ARCHIFIXER_RESULT_JSON = $resultPath
            $env:ARCHIFIXER_KEEP_FIXTURE = '1'
            $env:ARCHIFIXER_CONSOLE_TO_FILE = $consolePath

            $proc = Start-Process -FilePath $Exe -PassThru -Wait -NoNewWindow
            $exit = $proc.ExitCode

            Remove-Item Env:\ARCHIFIXER_RESULT_JSON -ErrorAction SilentlyContinue
            Remove-Item Env:\ARCHIFIXER_KEEP_FIXTURE -ErrorAction SilentlyContinue
            Remove-Item Env:\ARCHIFIXER_CONSOLE_TO_FILE -ErrorAction SilentlyContinue

            $reasons = New-Object System.Collections.Generic.List[string]

            if (-not (Test-Path -LiteralPath $resultPath)) {
                $rows.Add([pscustomobject]@{
                        Id = $recipe.id; Mode = $mode; Expected = $recipe.expect.Outcome; Actual = '(无 result.json)'
                        Verdict = '红'; Reason = "runner 退出码 $exit，没写 result.json"
                    })
                continue
            }

            $res = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            $tasks = @($res.tasks)
            $fixtureRoot = Split-Path -Path $res.inputRoot -Parent

            # ① 终态
            $actualOutcomes = ($tasks | ForEach-Object { $_.outcome }) -join '/'
            $hasExpected = @($tasks | Where-Object { $_.outcome -eq $recipe.expect.Outcome }).Count -gt 0
            $unexpectedFailed = @($tasks | Where-Object { $_.outcome -eq 'Failed' -and $recipe.expect.Outcome -ne 'Failed' })
            if (-not $hasExpected) { $reasons.Add("终态里没有「$($recipe.expect.Outcome)」（实际 $actualOutcomes）") }
            if ($unexpectedFailed.Count -gt 0) { $reasons.Add("有任务意外失败：" + (($unexpectedFailed | ForEach-Object { "$($_.name)=$($_.outcome)" }) -join '，')) }

            # ② 内容物（后缀匹配）+ ③ 落点模式
            foreach ($rel in @($recipe.expect.Content)) {
                if ([string]::IsNullOrWhiteSpace($rel)) { continue }
                if (-not (Test-ContainsSuffix -Root $fixtureRoot -Suffix $rel)) {
                    $reasons.Add("缺内容物：$rel")
                }
                else {
                    # 落点：default ⇒ 落在样本目录树里；custom ⇒ 落在自定义输出根里
                    $hit = @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File -ErrorAction SilentlyContinue |
                        Where-Object { $_.FullName.EndsWith(($rel -replace '/', '\'), [StringComparison]::OrdinalIgnoreCase) })[0]
                    if ($mode -eq 'custom') {
                        if (-not $hit.FullName.StartsWith($res.outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
                            $reasons.Add("指定位置模式下内容物不在自定义根里：$($hit.FullName)")
                        }
                    }
                    else {
                        if (-not $hit.FullName.StartsWith($res.inputRoot, [StringComparison]::OrdinalIgnoreCase)) {
                            $reasons.Add("未指定位置模式下内容物不在样本目录树里：$($hit.FullName)")
                        }
                    }
                }
            }

            # ④ 工作区残留
            $residue = @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq '.ArchiveFixer.work' })
            if ($residue.Count -gt 0) { $reasons.Add("工作区残留 $($residue.Count) 处") }

            # ⑤ 源包不动（这一套 runner 用 KeepInPlace 档）
            $lost = @()
            foreach ($f in $sampleFiles) {
                if (-not (Test-Path -LiteralPath (Join-Path $res.inputRoot $f))) { $lost += $f }
            }
            if ($lost.Count -gt 0) { $reasons.Add("源包不见了：" + ($lost -join '，')) }

            # ⑥ 不许逃出目录（G01 那一档）
            if ($recipe.expect.ForbidOutside) {
                $escape = @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -in @('逃出去.txt', '再逃一层.txt') })
                if ($escape.Count -gt 0) { $reasons.Add("穿越条目真的落盘了：" + (($escape | ForEach-Object { $_.FullName }) -join '，')) }
            }

            $verdict = if ($reasons.Count -eq 0) { '绿' } else { '红' }
            $rows.Add([pscustomobject]@{
                    Id = $recipe.id; Mode = $mode
                    Expected = $recipe.expect.Outcome; Actual = $actualOutcomes
                    Verdict = $verdict; Reason = ($reasons -join '；')
                })

            if (-not $KeepFixture -and (Test-Path -LiteralPath $fixtureRoot)) {
                try { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force } catch { }
            }
        }
    }
}
finally {
    if ($null -ne $configBackup) {
        Set-Content -LiteralPath $configPath -Value $configBackup -Encoding utf8
    }
}

$rows | Format-Table Id, Mode, Expected, Actual, Verdict, Reason -AutoSize -Wrap

$red = @($rows | Where-Object { $_.Verdict -eq '红' })
Write-Output ("总计 {0} 次（{1} 份样本 × {2} 种模式）：绿 {3} / 红 {4}" -f $rows.Count, $dirs.Count, 2, ($rows.Count - $red.Count), $red.Count)
if ($red.Count -gt 0) { exit 1 }
