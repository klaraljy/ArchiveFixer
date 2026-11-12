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

# ---------------------------------------------------------------- 许可页编码（⛔ 不许再踩）
#
# NSIS 的许可页按 **ANSI** 读取许可文件：0.2.2 实测整页中文乱码 —— 因为仓库 LICENSE 是
# MIT 正文 + 「第三方组件」中文表格（UTF-8），后半段被当 GBK 解码成了「浣跨敤」那种乱码。
# ⇒ 许可页只用 **ASCII 部分**：取 LICENSE 里第一个 `---` 分隔之前的 MIT 正文，再补一句
#   英文指向（第三方组件与完整边界在 LICENSE 原文与 docs\引擎与外部工具.md 里）。
#   中文解释继续由 .nsi 的 MUI_LICENSEPAGE_TEXT_TOP 承担（那一段由 Unicode 编译器保存，显示正常）。
$licenseText = [IO.File]::ReadAllText($licensePath)
$licenseSeparator = $licenseText.IndexOf("`n---", [StringComparison]::Ordinal)
$licenseAsciiPart = if ($licenseSeparator -gt 0) { $licenseText.Substring(0, $licenseSeparator) } else { $licenseText }
$licensePageNote = @(
    "Third-party components (7-Zip / UnRAR) are NOT covered by this MIT license;",
    "their license texts ship in the installation directory under tools\7zip and tools\unrar.",
    "Full license boundaries: docs\engine-and-tools (Chinese) and this LICENSE file."
) -join "`r`n"
$licenseAsciiPart = $licenseAsciiPart.TrimEnd() + "`r`n`r`n" + $licensePageNote + "`r`n"
$licensePagePath = Join-Path ([IO.Path]::GetTempPath()) ("af-license-page-" + [Guid]::NewGuid().ToString('N') + '.txt')
$licensePageBytes = [System.Text.Encoding]::ASCII.GetBytes($licenseAsciiPart)
$licensePageNonAscii = 0
foreach ($byte in $licensePageBytes) { if ($byte -gt 0x7F) { $licensePageNonAscii++ } }
if ($licensePageNonAscii -gt 0) {
    Fail (("许可页 ASCII 化失败（剩 {0} 个非 ASCII 字节）：{1}" -f $licensePageNonAscii, $licensePath) + [Environment]::NewLine + "  NSIS 许可页按 ANSI 读取，含中文必定整页乱码。")
}
[IO.File]::WriteAllBytes($licensePagePath, $licensePageBytes)
# ---------------------------------------------------------------- 版本号（唯一来源：csproj）
if (-not $Version) {
    [xml]$csproj = Get-Content -LiteralPath (Join-Path $repoRoot 'src\ArchiveFixer\ArchiveFixer.csproj') -Raw
    $versionNode = $csproj.SelectSingleNode('//Version')
    if (-not $versionNode) { Fail '从 csproj 里读不到 <Version>' }
    $Version = $versionNode.InnerText.Trim()
}

if (-not $SourceDir) { $SourceDir = Join-Path $repoRoot "dist\ArchiveFixer-$Version-独立" }
if (-not $Output) { $Output = Join-Path $repoRoot "dist\ArchiveFixer-$Version-setup.exe" }

# makensis 3.10 在 Windows 上会把中文 /DSRCDIR 读成乱码（实测 File ... no files found）；
# 源目录含非 ASCII 时临时建一个 ASCII 目录联接再传给编译器。
$SourceDir = [IO.Path]::GetFullPath($SourceDir)
$Output = [IO.Path]::GetFullPath($Output)
$makensisSourceDir = $SourceDir
$sourceJunction = $null

if ($SourceDir -match '[^\x00-\x7F]') {
    $sourceJunction = Join-Path ([IO.Path]::GetTempPath()) ("af-installer-src-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Junction -Path $sourceJunction -Target $SourceDir | Out-Null
    $makensisSourceDir = $sourceJunction
}

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
    "/DSRCDIR=$makensisSourceDir",
    "/DAPPVERSION=$Version",
    "/DOUTFILE=$Output",
    "/DICONFILE=$iconPath",
    "/DLICENSEFILE=$licensePagePath",
    $nsiPath
)
if (-not $Quiet) { Write-Detail ("makensis " + ($args -join ' ')) }
$sw = [System.Diagnostics.Stopwatch]::StartNew()

try {
    & $Makensis @args
    $exit = $LASTEXITCODE
}
finally {
    if ($licensePagePath -and (Test-Path -LiteralPath $licensePagePath)) { Remove-Item -LiteralPath $licensePagePath -Force -ErrorAction SilentlyContinue }
    if ($sourceJunction -and (Test-Path -LiteralPath $sourceJunction)) {
        Remove-Item -LiteralPath $sourceJunction -Force -ErrorAction SilentlyContinue
    }
}

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
Write-Host "  装到哪      : 默认「本机第一个非系统固定盘」上的 <盘>:\ArchiveFixer（例如 D:\ArchiveFixer）；"
Write-Host "                本机没有第二个固定盘时才退 %LOCALAPPDATA%\Programs\ArchiveFixer，向导会明说。"
Write-Host "                每用户安装、免 UAC，安装时可以改目录。"
Write-Host "  用户数据    : 固定 %LOCALAPPDATA%\ArchiveFixer（设置 / 密码 / 日志），不跟安装目录走；"
Write-Host "                旧程序的 data\ 会在第一次启动时只读复制过来。"
Write-Host "  升级        : 装到已有目录直接覆盖程序文件，用户数据保留；不再单开“升级覆盖/先卸载/取消”页。"
Write-Host "  卸载        : 控制面板「应用和功能」里那一项 / 开始菜单 / 安装目录下的 Uninstall.exe；"
Write-Host "                卸载页问一次「保留用户数据（默认）还是全部删除」，静默卸载一律保留。"
Write-Host "  验证办法    : <安装包> /S /D=<临时目录>  （静默安装不建快捷方式），再看目录里有没有 ArchiveFixer.exe"
Write-Host ''
