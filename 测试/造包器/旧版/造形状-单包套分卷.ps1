# 形状：外层单一 tar → 里面是一套 7z 分卷（5 片）→ 分卷解开是最终内容 content.bin
# 目的：复现「上游单包解出的整套分卷在定稿后进不了账本」这条缺陷。
param(
    [string]$OutRoot = 'E:\DeepSeekProjects\ArchiveFixer\_tmp\形状-单包套分卷',
    [int]$ContentBytes = 4 * 1024 * 1024
)
$ErrorActionPreference = 'Stop'
$sevenZip = 'E:\ArchiveFixer\tools\7zip\7z.exe'
if (-not (Test-Path -LiteralPath $sevenZip)) { throw "找不到 7z：$sevenZip" }
if (Test-Path -LiteralPath $OutRoot) { Remove-Item -LiteralPath $OutRoot -Recurse -Force }
$work = Join-Path $OutRoot 'work'
New-Item -ItemType Directory -Path $work -Force | Out-Null

# 1) 内容物
$content = Join-Path $work 'content.bin'
$buf = New-Object byte[] $ContentBytes
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($buf) } finally { $rng.Dispose() }
[IO.File]::WriteAllBytes($content, $buf)

# 2) 切 5 片 7z 分卷（-v1048576b，4MB 内容 → 5 片）
Push-Location $work
try {
    & $sevenZip a -t7z -mx=0 -y -v1048576b 'aaa-B1.7z' 'content.bin' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '7z 切分卷失败' }
} finally { Pop-Location }
$pieces = @(Get-ChildItem -LiteralPath $work -Filter 'aaa-B1.7z.*' | Sort-Object Name)
if ($pieces.Count -ne 5) { throw "分卷片数不对：$($pieces.Count)" }
Remove-Item -LiteralPath $content -Force

# 3) 外面套一个单一 tar（Windows 自带 tar，不引入依赖）
$tar = Join-Path $OutRoot 'aaa.tar'
$names = $pieces | ForEach-Object { $_.Name }
Push-Location $work
try {
    & tar -cf $tar @names
    if ($LASTEXITCODE -ne 0) { throw 'tar 打包失败' }
} finally { Pop-Location }

Remove-Item -LiteralPath $work -Recurse -Force
"已生成：$tar"
"  分卷片数 5 / 内容 $(($ContentBytes/1MB)) MB"
Get-ChildItem -LiteralPath $OutRoot | ForEach-Object { '  {0}  {1:N1}MB' -f $_.Name, ($_.Length/1MB) }