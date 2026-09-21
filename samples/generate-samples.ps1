#Requires -Version 5.1
<#
.SYNOPSIS
    生成 ArchiveFixer 的合成测试样本集。

.DESCRIPTION
    - 输出目录：samples/generated/（已在 .gitignore 中忽略，样本本体不入仓库）
    - 只依赖项目内置的 7z.exe（ArchiveFixer/tools/7zip/7z.exe），不需要系统安装 7-Zip
    - 样本里出现的密码全部是**合成密码**，禁止写入任何真实密码（AGENTS.md §8 隐私红线）
    - 覆盖设计.md §三十 里第一批必须有的类别，具体见 samples/MANIFEST.md

.EXAMPLE
    pwsh -File samples/generate-samples.ps1
    pwsh -File samples/generate-samples.ps1 -VolumeSizeMb 2
#>
[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$SevenZip,
    [int]$VolumeSizeMb = 1,
    [string]$SamplePassword = 'TestPass123!'
)

$ErrorActionPreference = 'Stop'

if (-not $OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot 'generated' }
if (-not $SevenZip)   { $SevenZip   = Join-Path $PSScriptRoot '..\ArchiveFixer\tools\7zip\7z.exe' }

$SevenZip = (Resolve-Path -LiteralPath $SevenZip).Path
$pwArg = '-p' + $SamplePassword

Write-Host "7z      : $SevenZip"
Write-Host "输出目录: $OutputRoot"
Write-Host "样本密码: $SamplePassword（合成密码，不是真实密码）"
Write-Host ''

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}

$script:plan = @()

function New-Dir([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
    return $Path
}

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

function Invoke-7z {
    param([string[]]$Arguments, [string]$What)
    & $SevenZip @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "7z 失败（$What），退出码 $LASTEXITCODE : 7z $($Arguments -join ' ')"
    }
}

function Add-Step([string]$Category, [string]$Name, [string]$Note) {
    $script:plan += [pscustomobject]@{ 类别 = $Category; 产物 = $Name; 说明 = $Note }
}

# ---------------- 载荷 ----------------
$payload = New-Dir (Join-Path $OutputRoot '_payload')
New-Dir (Join-Path $payload 'docs') | Out-Null
Write-Utf8NoBom (Join-Path $payload 'hello.txt')     "hello ArchiveFixer`r`n"
Write-Utf8NoBom (Join-Path $payload '中文内容.txt')  "中文内容，用于验证 Unicode 文件名。`r`n"
Write-Utf8NoBom (Join-Path $payload 'docs\说明.txt') "说明文件，用于验证多目录归档。`r`n"

$bigPath = Join-Path $payload 'big.bin'
$bigBytes = New-Object byte[] (2MB + 512KB)
(New-Object System.Random(20260921)).NextBytes($bigBytes)
[System.IO.File]::WriteAllBytes($bigPath, $bigBytes)

$payloadItems = @(
    (Join-Path $payload 'hello.txt'),
    (Join-Path $payload '中文内容.txt'),
    (Join-Path $payload 'docs')
)

# ---------------- 01 普通归档 ----------------
$d = New-Dir (Join-Path $OutputRoot '01_普通')
Write-Host '[1/7] 01_普通 ...'
Invoke-7z -Arguments (@('a', '-tzip', (Join-Path $d 'normal.zip')) + $payloadItems) -What 'zip'
Invoke-7z -Arguments (@('a', '-t7z',  (Join-Path $d 'normal.7z'))  + $payloadItems) -What '7z'
$tarPath = Join-Path $d 'normal.tar'
Invoke-7z -Arguments (@('a', '-ttar', $tarPath) + $payloadItems) -What 'tar'
Invoke-7z -Arguments @('a', '-tgzip', (Join-Path $d 'normal.tar.gz'), $tarPath) -What 'tar.gz'
Remove-Item -LiteralPath $tarPath -Force
Add-Step '01_普通' 'normal.zip / normal.7z / normal.tar.gz' '无密码，后缀与格式一致 → 后缀正常'

# ---------------- 02 加密归档 ----------------
$d = New-Dir (Join-Path $OutputRoot '02_加密')
Write-Host '[2/7] 02_加密 ...'
Invoke-7z -Arguments (@('a', '-t7z', (Join-Path $d 'encrypted.7z'), $pwArg, '-mhe=on') + $payloadItems) -What 'encrypted 7z'
Invoke-7z -Arguments (@('a', '-tzip', (Join-Path $d 'encrypted.zip'), $pwArg) + $payloadItems) -What 'encrypted zip'
Add-Step '02_加密' 'encrypted.7z / encrypted.zip' "密码 = $SamplePassword"

# ---------------- 03 分卷归档 ----------------
$d = New-Dir (Join-Path $OutputRoot '03_分卷')
Write-Host '[3/7] 03_分卷 ...'
Invoke-7z -Arguments @('a', '-t7z', (Join-Path $d 'volume.7z'), "-v${VolumeSizeMb}m", $bigPath) -What 'volume 7z'
$volCount = (Get-ChildItem -LiteralPath $d -Filter 'volume.7z.*').Count
Add-Step '03_分卷' "volume.7z.001 … 共 $volCount 卷" '分卷组必须被当成一个任务，只从 .001 启动'

# ---------------- 04 伪装后缀 ----------------
$d = New-Dir (Join-Path $OutputRoot '04_伪装后缀')
Write-Host '[4/7] 04_伪装后缀 ...'
$src7z = Join-Path $OutputRoot '01_普通\normal.7z'
Copy-Item -LiteralPath $src7z -Destination (Join-Path $d 'fake.jpg')        -Force
Copy-Item -LiteralPath $src7z -Destination (Join-Path $d 'fake.7z.pdf.jpg') -Force
Copy-Item -LiteralPath $src7z -Destination (Join-Path $d 'noextension')     -Force
Add-Step '04_伪装后缀' 'fake.jpg / fake.7z.pdf.jpg / noextension' '真实格式 7Z，后缀错误 / 多重伪装 / 无后缀'

# ---------------- 05 损坏 / 截断 ----------------
$d = New-Dir (Join-Path $OutputRoot '05_损坏')
Write-Host '[5/7] 05_损坏 ...'
$bytes = [System.IO.File]::ReadAllBytes($src7z)
$cut = [int]($bytes.Length * 0.4)
$head = New-Object byte[] $cut
[Array]::Copy($bytes, $head, $cut)
[System.IO.File]::WriteAllBytes((Join-Path $d 'corrupted.7z'), $head)
[System.IO.File]::WriteAllBytes((Join-Path $d 'truncated.zip'), $head)
Add-Step '05_损坏' 'corrupted.7z / truncated.zip' '截断到 40%，解压必须失败且分类为"文件损坏"'

# ---------------- 06 仅名字像压缩包 ----------------
$d = New-Dir (Join-Path $OutputRoot '06_名字像压缩包')
Write-Host '[6/7] 06_名字像压缩包 ...'
Write-Utf8NoBom (Join-Path $d 'text.7z')  "这不是压缩包，只是一个改了后缀的文本文件。`r`n"
Write-Utf8NoBom (Join-Path $d 'text.zip') "这不是压缩包，只是一个改了后缀的文本文件。`r`n"
Add-Step '06_名字像压缩包' 'text.7z / text.zip' '识别必须是 Unknown，不得当成归档'

# ---------------- 07 复合后缀 ----------------
$d = New-Dir (Join-Path $OutputRoot '07_复合后缀')
Write-Host '[7/7] 07_复合后缀 ...'
$tar2 = Join-Path $d 'data.tar'
Invoke-7z -Arguments (@('a', '-ttar', $tar2) + $payloadItems) -What 'data.tar'
Invoke-7z -Arguments @('a', '-tgzip', (Join-Path $d 'data.tar.gz'), $tar2) -What 'data.tar.gz'
Remove-Item -LiteralPath $tar2 -Force
Add-Step '07_复合后缀' 'data.tar.gz' '改名时不得把 tar.gz 破坏成 gz'

Remove-Item -LiteralPath $payload -Recurse -Force

Write-Host ''
Write-Host '=============== 生成完毕 ==============='
$plan | Format-Table -AutoSize
$total = Get-ChildItem -LiteralPath $OutputRoot -Recurse -File | Measure-Object -Property Length -Sum
Write-Host ("文件数: {0}    总大小: {1:N2} MB" -f $total.Count, ($total.Sum / 1MB))
Write-Host "目录  : $OutputRoot"
Write-Host ''
Write-Host '注意：样本本体不入仓库（.gitignore 已忽略 samples/generated/）。'
