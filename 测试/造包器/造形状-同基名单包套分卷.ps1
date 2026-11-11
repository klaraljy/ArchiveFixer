# 120 批形状：**外层单一包 + 同基名的整套 7z 分卷**（真机 2026-11-10 的形状）
#   外层  : aaa.<tar|7z|rar>          （单一包，内容就是那 5 片）
#   内层片: aaa.7z.001 ~ aaa.7z.005   （单卷 7z 按字节切出来的，7z 读成 Type=Split）
#   最终  : content.bin
# 缺陷形状：外层包解成功并定稿后，第二大步仍把它按同基名算进 7z 分卷组 ⇒ 字节和翻倍 ⇒ SizeAnomaly。
# 用法：
#   powershell -NoProfile -File E:\AF\造形状-同基名单包套分卷.ps1 -SampleId 917000099 -OuterFormat tar
param(
    [string]$SampleId = '917000099',
    [string]$OuterFormat = 'tar',
    [string]$Seed = '917000000',
    [string]$BatchRoot = 'E:\DeepSeekProjects\ArchiveFixer\_tmp\困难大测试',
    [int]$ContentBytes = 4 * 1024 * 1024,
    [string]$Base = 'aaa'
)
$ErrorActionPreference = 'Stop'
$sevenZip = 'E:\ArchiveFixer\tools\7zip\7z.exe'
$rarExe = 'C:\Program Files\WinRAR\Rar.exe'
if (-not (Test-Path -LiteralPath $sevenZip)) { throw "找不到 7z：$sevenZip" }
if ($OuterFormat -eq 'rar' -and -not (Test-Path -LiteralPath $rarExe)) { throw "找不到 rar：$rarExe" }

$sampleRoot = Join-Path (Join-Path $BatchRoot ($Seed + '-120批次-备份')) $SampleId
$sourceDir = Join-Path $sampleRoot '原包'
if (Test-Path -LiteralPath $sampleRoot) { Remove-Item -LiteralPath $sampleRoot -Recurse -Force }
New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null

$work = Join-Path $BatchRoot ('work-' + $SampleId)
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

# 1) 内容物
$content = Join-Path $work 'content.bin'
$buf = New-Object byte[] $ContentBytes
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($buf) } finally { $rng.Dispose() }
[IO.File]::WriteAllBytes($content, $buf)

# 2) 单个 7z（文件头加密，和真机一样需要密码）
Push-Location $work
try {
    & $sevenZip a -t7z -mx=0 -y '-pDemo#Pass1' '-mhe=on' ($Base + '.7z') 'content.bin' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '7z 打包失败' }
} finally { Pop-Location }
Remove-Item -LiteralPath $content -Force
$archive = Join-Path $work ($Base + '.7z')
$total = (Get-Item -LiteralPath $archive).Length

# 3) 按字节切 5 片：aaa.7z.001 ~ aaa.7z.005（Split 形态，不是 7z -v）
$partSize = [long][math]::Ceiling($total / 5.0)
$src = [IO.File]::Open($archive, 'Open', 'Read', 'Read')
try {
    for ($i = 1; $i -le 5; $i++) {
        $piecePath = Join-Path $work ('{0}.7z.{1:000}' -f $Base, $i)
        $out = [IO.File]::Open($piecePath, 'Create', 'Write', 'None')
        try {
            $left = [long]$partSize
            $avail = [long]($total - $src.Position)
            if ($avail -lt $left) { $left = $avail }
            $chunk = New-Object byte[] (1024 * 1024)
            while ($left -gt 0) {
                $want = [int]$chunk.Length
                if ([long]$left -lt [long]$want) { $want = [int]$left }
                $read = $src.Read($chunk, 0, $want)
                if ($read -le 0) { break }
                $out.Write($chunk, 0, $read)
                $left = [long]($left - [long]$read)
            }
        } finally { $out.Dispose() }
    }
} finally { $src.Dispose() }
Remove-Item -LiteralPath $archive -Force

$pieces = @(Get-ChildItem -LiteralPath $work -Filter ($Base + '.7z.*') | Sort-Object Name | ForEach-Object Name)
if ($pieces.Count -ne 5) { throw "分卷片数不对：$($pieces.Count)" }

# 4) 外层单一包（同基名）：tar / 7z / rar
$outerPath = Join-Path $sourceDir ($Base + '.' + $OuterFormat)
Push-Location $work
try {
    switch ($OuterFormat) {
        'tar' {
            & tar -cf $outerPath @pieces | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'tar 打包失败' }
        }
        '7z' {
            & $sevenZip a -t7z -mx=0 -y ($Base + '.7z') @pieces | Out-Null
            if ($LASTEXITCODE -ne 0) { throw '7z 外层打包失败' }
            Move-Item -LiteralPath (Join-Path $work ($Base + '.7z')) -Destination $outerPath -Force
        }
        'rar' {
            & $rarExe a -ep1 -m0 -y ($Base + '.rar') @pieces | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'rar 外层打包失败' }
            Move-Item -LiteralPath (Join-Path $work ($Base + '.rar')) -Destination $outerPath -Force
        }
        default { throw "未知外层格式：$OuterFormat" }
    }
} finally { Pop-Location }
Remove-Item -LiteralPath $work -Recurse -Force

# 5) recipe：Normal 类别，形状名给检测用
$outerBytes = (Get-Item -LiteralPath $outerPath).Length
$recipe = [ordered]@{
    sampleId             = $SampleId
    category             = 'Normal'
    kind                 = 'normal'
    shape                = '同基名单包套分卷'
    expectedOutcome      = 'Succeeded'
    expectedFinalContentName = 'content.bin'
    expectedContentBytes = $ContentBytes
    expectedSourceKeep   = $true
    files                = @([ordered]@{ baseName = $Base; bytes = $outerBytes })
    scenario             = [ordered]@{
        placement      = 'Original'
        sourceHandling = 'KeepInPlace'
        contentHandling = 'Keep'
    }
}
$json = $recipe | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $sampleRoot 'recipe.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

"已生成样本：$sampleRoot"
"  外层 $OuterFormat  $([math]::Round($outerBytes/1MB,2)) MB ｜ 内层 5 片（Split 形态，同基名 $Base）"
