# 出 ArchiveFixer 的 .exe 安装包（NSIS）。
#
# 用法：
#   pwsh scripts/installer.ps1                          # 拿 dist\ArchiveFixer-<版本>-独立 当内容，出 dist\ArchiveFixer-<版本>-setup.exe
#   pwsh scripts/installer.ps1 -SourceDir <目录>        # 指定要打包的发布目录（默认要独立版那一份）
#   pwsh scripts/installer.ps1 -Output <文件路径>       # 指定产物路径
#
# 为什么默认拿「独立」那一份：安装包是给完全不懂的机器用的，独立版自带 .NET 运行时，
# 目标机器什么都不用装（框架依赖版要先去装 .NET 8 桌面运行时）。
#
# ⛔ 打包 ≠ 安装：本脚本只编译出 exe，不动本机任何目录、不写注册表。
#    要验安装包，用静默模式装到临时目录：<setup>.exe /S /D=<临时目录>（静默时不建快捷方式）。

param(
    [string]$SourceDir,
    [string]$Output,
    [string]$Makensis,
    [string]$Version,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Detail([string]$Text) { Write-Host "   $Text" }
function Fail([string]$Text) { Write-Host "`n✗ $Text" -ForegroundColor Red; exit 1 }

$repoRoot = Split-Path -Parent $PSScriptRoot
$nsiPath = Join-Path $repoRoot 'installer\ArchiveFixer.nsi'
$iconPath = Join-Path $repoRoot 'src\ArchiveFixer\Assets\ArchiveFixer.ico'
$licensePath = Join-Path $repoRoot 'LICENSE'

# ---------------------------------------------------------------- 版本号（唯一来源：csproj）
if (-not $Version) {
    [xml]$csproj = Get-Content -LiteralPath (Join-Path $repoRoot 'src\ArchiveFixer\ArchiveFixer.csproj') -Raw
    $versionNode = $csproj.SelectSingleNode('//Version')
    if (-not $versionNode) { Fail '从 csproj 里读不到 <Version>' }
    $Version = $versionNode.InnerText.Trim()
}

if (-not $SourceDir) { $SourceDir = Join-Path $repoRoot "dist\ArchiveFixer-$Version-独立" }
if (-not $Output) { $Output = Join-Path $repoRoot "dist\ArchiveFixer-$Version-setup.exe" }

# ---------------------------------------------------------------- makensis
if (-not $Makensis) {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe')
    )
    $candidates += Get-ChildItem 'D:\Codex Tools\NSIS' -Recurse -Filter 'makensis.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName
    $onPath = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $Makensis = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $Makensis -or -not (Test-Path -LiteralPath $Makensis)) {
    Fail '找不到 makensis.exe（NSIS）。装一个，或用 -Makensis <路径> 指定。'
}

# ---------------------------------------------------------------- 前置检查
Write-Step '1/4 前置检查'
foreach ($p in @($nsiPath, $iconPath, $licensePath)) {
    if (-not (Test-Path -LiteralPath $p)) { Fail "缺文件：$p" }
}
if (-not (Test-Path -LiteralPath $SourceDir)) {
    Fail "内容目录不存在：$SourceDir`n  先跑 pwsh scripts/package.ps1 -SelfContained 生成它。"
}
foreach ($need in @('ArchiveFixer.exe', 'tools\7zip\7z.exe', 'tools\unrar\UnRAR.exe', 'docs\使用说明.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $need))) { Fail "内容目录里缺：$need（$SourceDir）" }
}
$srcFiles = Get-ChildItem -LiteralPath $SourceDir -Recurse -File
$srcSize = ($srcFiles | Measure-Object Length -Sum).Sum
Write-Detail "版本        : $Version"
Write-Detail "内容目录    : $SourceDir（$($srcFiles.Count) 个文件，$([math]::Round($srcSize/1MB,2)) MB）"
Write-Detail "makensis    : $Makensis"
Write-Detail "产物        : $Output"

if (Test-Path -LiteralPath $Output) {
    try { Remove-Item -LiteralPath $Output -Force }
    catch { Fail "产物被占用，删不掉：$Output（ArchiveFixer 还开着？）" }
}

# ---------------------------------------------------------------- 编译
Write-Step '2/4 编译安装包（LZMA 固实压缩，70 MB 上下，慢一点正常）'
$args = @(
    '/V2',
    "/DSRCDIR=$SourceDir",
    "/DAPPVERSION=$Version",
    "/DOUTFILE=$Output",
    "/DICONFILE=$iconPath",
    "/DLICENSEFILE=$licensePath",
    $nsiPath
)
if (-not $Quiet) { Write-Detail ("makensis " + ($args -join ' ')) }
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $Makensis @args
$exit = $LASTEXITCODE
$sw.Stop()
if ($exit -ne 0) { Fail "makensis 失败（exit=$exit），上面是它自己的报错。" }
Write-Detail "编译完成，用时 $([math]::Round($sw.Elapsed.TotalSeconds,1)) 秒"

# ---------------------------------------------------------------- 自检
Write-Step '3/4 自检'
if (-not (Test-Path -LiteralPath $Output)) { Fail "没生成产物：$Output" }
$item = Get-Item -LiteralPath $Output
$vi = $item.VersionInfo
# NSIS 会把 VIProductVersion 归一化：写 0.1.0.0、读回来可能是 0.1.0 —— 两种都认
if ($vi.FileVersion -ne $Version -and $vi.FileVersion -ne "$Version.0") {
    Fail "产物版本号不对：$($vi.FileVersion)（期望 $Version 或 $Version.0）"
}
$head = [System.IO.File]::ReadAllBytes($Output)[0..1]
if (-not ($head[0] -eq 0x4D -and $head[1] -eq 0x5A)) { Fail '产物不是 PE 可执行文件（MZ 头不对）' }

# 安装包体积应当是"内容压缩后"的量级：太小说明内容没打进去
$ratio = $item.Length / $srcSize
if ($ratio -lt 0.25) { Fail ("安装包只有 {0:N2} MB，内容有 {1:N2} MB —— 内容没打进去？" -f ($item.Length/1MB), ($srcSize/1MB)) }
Write-Detail "产物        : $($item.FullName)"
Write-Detail "体积        : $([math]::Round($item.Length/1MB,2)) MB（内容 $([math]::Round($srcSize/1MB,2)) MB，压缩到 $([math]::Round($ratio*100,1))%）"
Write-Detail "文件版本    : $($vi.FileVersion) / 产品 $($vi.ProductVersion)"
Write-Detail "描述        : $($vi.FileDescription)"

# ---------------------------------------------------------------- 汇总
Write-Step '4/4 完成'
Write-Host ''
Write-Host '安装包已生成（本脚本只编译，没动本机任何目录、没写注册表）' -ForegroundColor Green
Write-Host "  安装包      : $($item.FullName)"
Write-Host "  装到哪      : 默认 %LOCALAPPDATA%\ArchiveFixer（每用户、免 UAC），安装时可以改"
Write-Host "  卸载        : 控制面板「应用和功能」里那一项 / 安装目录下的 Uninstall.exe"
Write-Host "  验证办法    : <安装包> /S /D=<临时目录>  （静默安装不建快捷方式），再看目录里有没有 ArchiveFixer.exe"
Write-Host ''
