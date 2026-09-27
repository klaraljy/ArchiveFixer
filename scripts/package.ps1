#Requires -Version 5.1
<#
.SYNOPSIS
    打 ArchiveFixer 的绿色分发包（解压即用：不写注册表、不写 %AppData%）。

.DESCRIPTION
    四件事，缺一步就报错退出，绝不产出半成品：

      1. 发布：dotnet publish -c Release 到临时目录
         - 默认 = **框架依赖版**（体积小，目标机需要装 .NET 8 桌面运行时 x64）
         - -SelfContained = **独立版**（自带运行时，体积大但目标机什么都不用装）
      2. 组装分发目录：exe 与运行时文件 + tools\7zip\（含 License.txt / README.md）
         + tools\unrar\（含 license.txt / README.md）+ LICENSE + README.md + 使用说明.md
      3. 打 zip 到 dist\ArchiveFixer-<版本>-<框架依赖|独立>.zip
         （zip 里带一层同名顶层文件夹，解压出来就是一个现成目录）
      4. 自检：zip 里必须能找到 ArchiveFixer.exe、tools/7zip/7z.exe、tools/unrar/UnRAR.exe、LICENSE
         四样（另加两份第三方许可文本与两个 README，属分发合规项，同样必须有）；
         并断言包里没有 data\、密码本、日志、样本、测试文件。任一不满足 → 非零退出。

    幂等：同一档重复跑会先清掉**自己那一份**（同名分发目录 + 同名 zip）再重建；
    另一档的产物不受影响（独立版的分发目录叫 ArchiveFixer-<版本>-独立）。

    ⚠ 如果 dist 里的程序正在被占用（例如你正开着它），脚本会**明确报错**并让你先关掉程序，
    不会静默失败、也不会去杀进程。

.PARAMETER SelfContained
    出独立版（自带 .NET 运行时）。不加这个开关 = 框架依赖版。

.PARAMETER OutputDirectory
    成品目录，默认 <仓库根>\dist。

.PARAMETER WorkDirectory
    临时目录，默认 <仓库根的上一级>\_tmp\ArchiveFixer\package
    （全局约定：临时文件统一放 <临时目录>\<项目名>\，不入仓库）。

.PARAMETER IncludeSymbols
    把 .pdb 调试符号一起放进包（默认不放 —— 分发包只需要能跑）。

.PARAMETER KeepWorkDirectory
    保留临时发布目录（默认就保留：发布目录很小，出问题时是唯一的现场）。

.EXAMPLE
    pwsh -File scripts/package.ps1
    pwsh -File scripts/package.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$OutputDirectory,
    [string]$WorkDirectory,
    [switch]$IncludeSymbols,
    [switch]$KeepWorkDirectory
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 小工具

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Write-Detail {
    param([string]$Message)
    Write-Host "    $Message"
}

function Fail {
    param([string]$Message, [string]$Hint)
    Write-Host ''
    Write-Host "[打包失败] $Message" -ForegroundColor Red
    if ($Hint) { Write-Host "           $Hint" -ForegroundColor Yellow }
    exit 1
}

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N2} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return ('{0} B' -f [int]$Bytes)
}

function Get-TreeSize {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [double]$sum
}

# 独占方式打开一下：能打开 = 没被别的进程占着。
function Test-FileLocked {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
                                         [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Close()
        $stream.Dispose()
        return $false
    } catch {
        return $true
    }
}

# ---------------------------------------------------------------- 路径与版本

$repoRoot    = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\ArchiveFixer\ArchiveFixer.csproj'

if (-not (Test-Path -LiteralPath $projectPath)) {
    Fail "找不到项目文件：$projectPath" '请在仓库里运行：pwsh -File scripts/package.ps1'
}

# 版本号只从 csproj 读，绝不在脚本里写死（改了 csproj 就自动跟着走）。
try {
    $projectXml = New-Object System.Xml.XmlDocument
    $projectXml.Load($projectPath)
    $versionNode = $projectXml.SelectSingleNode('//Version')
} catch {
    Fail "读不出项目文件里的版本号：$($_.Exception.Message)"
}
if (-not $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    Fail "ArchiveFixer.csproj 里没有 <Version>，无法生成包名" '请在 csproj 的 PropertyGroup 里补上 <Version>。'
}
$version = $versionNode.InnerText.Trim()

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'dist' }
if (-not $WorkDirectory) {
    # 仓库的上一级 = E:\DeepSeekProjects → 临时目录落 <临时目录>\ArchiveFixer\package
    $WorkDirectory = Join-Path (Split-Path -Parent $repoRoot) '_tmp\ArchiveFixer\package'
}

$flavor      = if ($SelfContained) { '独立' } else { '框架依赖' }
# 分发目录名：默认档就用规范里的 dist\ArchiveFixer-<版本>；独立版另开一个目录，免得两档互相覆盖。
$folderName  = if ($SelfContained) { "ArchiveFixer-$version-独立" } else { "ArchiveFixer-$version" }
$distDir     = Join-Path $OutputDirectory $folderName
$zipPath     = Join-Path $OutputDirectory "ArchiveFixer-$version-$flavor.zip"
$publishDir  = Join-Path $WorkDirectory "publish-$flavor"
$publishLog  = Join-Path $WorkDirectory "publish-$flavor.log"

Write-Host "ArchiveFixer 打包" -ForegroundColor Green
Write-Detail "版本        : $version"
Write-Detail "档          : $flavor$(if ($SelfContained) { '（自带 .NET 运行时）' } else { '（需要目标机装 .NET 8 桌面运行时）' })"
Write-Detail "仓库根      : $repoRoot"
Write-Detail "成品目录    : $OutputDirectory"
Write-Detail "临时目录    : $WorkDirectory"

# ---------------------------------------------------------------- 1. 前置检查

Write-Step '1/5 前置检查'

$requiredSources = @(
    'LICENSE',
    'README.md',
    'docs\使用说明.md',
    'src\ArchiveFixer\tools\7zip\7z.exe',
    'src\ArchiveFixer\tools\7zip\7z.dll',
    'src\ArchiveFixer\tools\7zip\License.txt',
    'src\ArchiveFixer\tools\7zip\README.md',
    'src\ArchiveFixer\tools\unrar\UnRAR.exe',
    'src\ArchiveFixer\tools\unrar\license.txt',
    'src\ArchiveFixer\tools\unrar\README.md'
)
$missing = @()
foreach ($rel in $requiredSources) {
    $full = Join-Path $repoRoot $rel
    if (-not (Test-Path -LiteralPath $full)) { $missing += $rel }
}
if ($missing.Count -gt 0) {
    Fail ("分发必需的源文件缺失：" + ($missing -join '、')) '先把它们补齐再打包（许可文本缺了不许分发，见 docs\引擎与外部工具.md §6）。'
}
Write-Detail "源文件齐（$($requiredSources.Count) 项，含两份第三方许可文本）"

# 正在运行的程序：只报告，不杀进程（AGENTS.md §9.1）。
$running = @(Get-Process -Name 'ArchiveFixer' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $rows = @()
    foreach ($p in $running) {
        $exePath = ''
        try { $exePath = $p.Path } catch { $exePath = '(读不到路径)' }
        $rows += "PID $($p.Id)  $exePath"
        if ($exePath -and $exePath.StartsWith($distDir, [System.StringComparison]::OrdinalIgnoreCase)) {
            Fail "ArchiveFixer 正在从分发目录里运行（$exePath）" '请先关掉程序再打包 —— 脚本不会替你杀进程。'
        }
    }
    Write-Host "    ⚠ 检测到正在运行的 ArchiveFixer（不阻断打包，但打包期间它可能开着别的文件）：" -ForegroundColor Yellow
    foreach ($row in $rows) { Write-Host "      $row" -ForegroundColor Yellow }
}

# 目标被占用就立刻停下（放在 publish 之前：快速失败，且不破坏上一版产物）。
if (Test-FileLocked (Join-Path $distDir 'ArchiveFixer.exe')) {
    Fail "dist 里的 ArchiveFixer.exe 正被占用：$(Join-Path $distDir 'ArchiveFixer.exe')" '请先关掉正在运行的 ArchiveFixer，再重新执行打包。'
}
if (Test-FileLocked $zipPath) {
    Fail "dist 里的 zip 正被占用：$zipPath" '请先关掉正在读它的程序（解压工具 / 资源管理器预览），再重新执行打包。'
}
Write-Detail '目标文件未被占用'

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}
if (-not (Test-Path -LiteralPath $WorkDirectory)) {
    New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
}

# ---------------------------------------------------------------- 2. 发布

Write-Step "2/5 dotnet publish（$flavor）"

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$publishArgs = @('publish', $projectPath, '-c', 'Release', '-o', $publishDir, '--nologo')
if ($SelfContained) {
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'true')
} else {
    $publishArgs += @('--no-self-contained')
}

Write-Detail ("dotnet " + ($publishArgs -join ' '))
Write-Detail "输出日志：$publishLog"

# dotnet publish 是有输出的长任务：日志落文件，同时打到屏幕上（能看见进度）。
& dotnet @publishArgs 2>&1 | Tee-Object -FilePath $publishLog
$publishExit = $LASTEXITCODE
if ($publishExit -ne 0) {
    $tail = ''
    if (Test-Path -LiteralPath $publishLog) {
        $tail = (Get-Content -LiteralPath $publishLog -Tail 25) -join "`n"
    }
    Fail "dotnet publish 失败（退出码 $publishExit）" "日志末尾：`n$tail"
}

$publishedExe = Join-Path $publishDir 'ArchiveFixer.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) {
    Fail "发布目录里没有 ArchiveFixer.exe：$publishDir" '发布产物不完整，先看上面的 publish 日志。'
}

$publishFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
$publishSize  = Get-TreeSize $publishDir
Write-Detail "发布完成：$($publishFiles.Count) 个文件，$(Format-Size $publishSize)"

# ---------------------------------------------------------------- 3. 组装分发目录

Write-Step '3/5 组装分发目录'

if (Test-Path -LiteralPath $distDir) {
    try {
        Remove-Item -LiteralPath $distDir -Recurse -Force
    } catch {
        Fail "清不掉旧的分发目录：$distDir（$($_.Exception.Message)）" '多半是里面还有程序在运行或被占用 —— 请先关掉 ArchiveFixer，再重试。'
    }
}
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# 不许进包的东西：日志 / 工作区 / 用户设置 / 密码本 / 样本 / 测试文件。
# （appsettings.json 是程序自带的**默认配置模板**，不是用户设置，必须留下。）
$forbiddenDirNames  = @('data', 'logs', 'temp', 'work', 'samples', '.git', '.vs')
$forbiddenFileNames = @('password-book.path')
$forbiddenExtensions = @('.log')
if (-not $IncludeSymbols) { $forbiddenExtensions += '.pdb' }

function Test-ForbiddenRelativePath {
    param([string]$RelativePath)
    $parts = $RelativePath -split '[\\/]'
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        if ($forbiddenDirNames -contains $parts[$i].ToLowerInvariant()) { return $true }
    }
    $leaf = $parts[$parts.Length - 1]
    if ($forbiddenFileNames -contains $leaf.ToLowerInvariant()) { return $true }
    if ($RelativePath -like '*ArchiveFixer.Tests*') { return $true }
    $ext = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
    if ($forbiddenExtensions -contains $ext) { return $true }
    return $false
}

$copiedCount = 0
$skippedList = New-Object System.Collections.ArrayList
foreach ($file in $publishFiles) {
    $rel = $file.FullName.Substring($publishDir.Length).TrimStart('\', '/')
    if (Test-ForbiddenRelativePath $rel) {
        [void]$skippedList.Add("$rel ($(Format-Size $file.Length))")
        continue
    }
    $dest = Join-Path $distDir $rel
    $destParent = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $destParent)) {
        New-Item -ItemType Directory -Force -Path $destParent | Out-Null
    }
    Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
    $copiedCount++
}
Write-Detail "运行时文件：$copiedCount 个"
foreach ($skipped in $skippedList) { Write-Detail "已排除：$skipped" }

# 内置工具的文档：csproj 只负责 exe/dll/许可文本，README 由这里补齐（缺了就补，已有就不动）。
$extraToolFiles = @(
    @{ Source = 'src\ArchiveFixer\tools\7zip\README.md';       Target = 'tools\7zip\README.md' },
    @{ Source = 'src\ArchiveFixer\tools\7zip\License.txt';     Target = 'tools\7zip\License.txt' },
    @{ Source = 'src\ArchiveFixer\tools\unrar\license.txt';    Target = 'tools\unrar\license.txt' },
    @{ Source = 'src\ArchiveFixer\tools\unrar\README.md';      Target = 'tools\unrar\README.md' }
)
foreach ($item in $extraToolFiles) {
    $dest = Join-Path $distDir $item.Target
    if (Test-Path -LiteralPath $dest) { continue }
    $src = Join-Path $repoRoot $item.Source
    $destParent = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $destParent)) {
        New-Item -ItemType Directory -Force -Path $destParent | Out-Null
    }
    Copy-Item -LiteralPath $src -Destination $dest -Force
    Write-Detail "补齐：$($item.Target)"
}

# 仓库根的三份文档：LICENSE / README.md / 使用说明.md
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE')   -Destination (Join-Path $distDir 'LICENSE')   -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $distDir 'README.md') -Force
# 使用说明同时放两处：根目录（双击就能看）+ docs\（与仓库同路径，见交付要求）
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\使用说明.md') -Destination (Join-Path $distDir '使用说明.md') -Force
$distDocs = Join-Path $distDir 'docs'
New-Item -ItemType Directory -Force -Path $distDocs | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\使用说明.md') -Destination (Join-Path $distDocs '使用说明.md') -Force

$distFiles = @(Get-ChildItem -LiteralPath $distDir -Recurse -File)
$distSize  = Get-TreeSize $distDir
Write-Detail "分发目录：$distDir"
Write-Detail "$($distFiles.Count) 个文件，$(Format-Size $distSize)"

# ---------------------------------------------------------------- 4. 打 zip

Write-Step "4/5 打 zip（$flavor）"

if (Test-Path -LiteralPath $zipPath) {
    try {
        Remove-Item -LiteralPath $zipPath -Force
    } catch {
        Fail "删不掉旧 zip：$zipPath（$($_.Exception.Message)）" '请先关掉正在读它的程序（解压工具 / 资源管理器预览），再重试。'
    }
}

Add-Type -AssemblyName System.IO.Compression            -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

$zipStream = $null
$zipArchive = $null
try {
    $zipStream  = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew,
                                         [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $zipArchive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)

    $index = 0
    foreach ($file in $distFiles) {
        $index++
        if (($index % 25 -eq 0) -or ($index -eq $distFiles.Count)) {
            Write-Detail "打包中 $index/$($distFiles.Count) …"
        }
        # 条目名一律用 /（zip 规范），顶层带一层同名文件夹 —— 解压出来就是一个现成目录。
        $rel = $file.FullName.Substring($distDir.Length).TrimStart('\', '/').Replace('\', '/')
        $entryName = "$folderName/$rel"
        $entry = $zipArchive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        try { $entry.LastWriteTime = $file.LastWriteTime } catch { }
        $entryStream = $entry.Open()
        try {
            $source = [System.IO.File]::OpenRead($file.FullName)
            try { $source.CopyTo($entryStream) } finally { $source.Dispose() }
        } finally { $entryStream.Dispose() }
    }
} catch {
    Fail "打 zip 失败：$($_.Exception.Message)" "先确认 $zipPath 没有被别的程序占用，然后重试。"
} finally {
    if ($zipArchive) { $zipArchive.Dispose() }
    if ($zipStream)  { $zipStream.Dispose() }
}

$zipInfo = Get-Item -LiteralPath $zipPath
Write-Detail "zip：$zipPath"
Write-Detail "$(Format-Size $zipInfo.Length)"

# ---------------------------------------------------------------- 5. 自检

Write-Step '5/5 自检（zip 里到底有没有那几样）'

$mustHave = @(
    @{ Entry = "$folderName/ArchiveFixer.exe";          Why = '主程序' },
    @{ Entry = "$folderName/tools/7zip/7z.exe";         Why = '7-Zip 引擎' },
    @{ Entry = "$folderName/tools/unrar/UnRAR.exe";     Why = 'UnRAR 引擎' },
    @{ Entry = "$folderName/LICENSE";                   Why = '本软件许可证' },
    @{ Entry = "$folderName/tools/7zip/License.txt";    Why = '7-Zip 许可文本（LGPL 分发义务）' },
    @{ Entry = "$folderName/tools/unrar/license.txt";   Why = 'UnRAR 许可文本（允许随包分发的前提）' },
    @{ Entry = "$folderName/README.md";                 Why = 'README' },
    @{ Entry = "$folderName/使用说明.md";                Why = '使用说明' }
)

$readStream = $null
$readArchive = $null
$entryNames = New-Object System.Collections.Generic.List[string]
try {
    $readStream  = [System.IO.File]::OpenRead($zipPath)
    $readArchive = New-Object System.IO.Compression.ZipArchive($readStream, [System.IO.Compression.ZipArchiveMode]::Read)
    foreach ($entry in $readArchive.Entries) {
        $entryNames.Add($entry.FullName.Replace('\', '/'))
    }
} catch {
    Fail "zip 读不回来（打出来的包是坏的）：$($_.Exception.Message)"
} finally {
    if ($readArchive) { $readArchive.Dispose() }
    if ($readStream)  { $readStream.Dispose() }
}

$failed = @()
foreach ($item in $mustHave) {
    if ($entryNames -contains $item.Entry) {
        Write-Host "    [有] $($item.Entry)  —— $($item.Why)" -ForegroundColor Green
    } else {
        Write-Host "    [缺] $($item.Entry)  —— $($item.Why)" -ForegroundColor Red
        $failed += $item.Entry
    }
}

$unexpected = @()
foreach ($name in $entryNames) {
    $rel = $name
    if ($rel.StartsWith("$folderName/", [System.StringComparison]::Ordinal)) {
        $rel = $rel.Substring($folderName.Length + 1)
    }
    if (Test-ForbiddenRelativePath $rel) { $unexpected += $name }
}
if ($unexpected.Count -gt 0) {
    Write-Host '    [不许进包的东西]' -ForegroundColor Red
    foreach ($name in $unexpected) { Write-Host "      $name" -ForegroundColor Red }
    $failed += $unexpected
} else {
    Write-Host '    [干净] 没有 data\ / 密码本 / 日志 / 样本 / 测试文件' -ForegroundColor Green
}

if ($failed.Count -gt 0) {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Fail "自检不通过（$($failed.Count) 项），已删掉这个残缺的 zip：$zipPath" '照上面 [缺] / [不许进包的东西] 的清单修，不要拿它去分发。'
}

if (-not $KeepWorkDirectory) {
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
}

# ---------------------------------------------------------------- 汇总

$distSize = Get-TreeSize $distDir
$distFiles = @(Get-ChildItem -LiteralPath $distDir -Recurse -File)

Write-Host ''
Write-Host '打包完成（自检通过）' -ForegroundColor Green
Write-Host "  版本        : $version"
Write-Host "  档          : $flavor"
Write-Host "  分发目录    : $distDir"
Write-Host "                 $($distFiles.Count) 个文件，$(Format-Size $distSize)"
Write-Host "  zip         : $zipPath"
Write-Host "                 $(Format-Size (Get-Item -LiteralPath $zipPath).Length)，$($entryNames.Count) 个条目"
Write-Host "  zip 内顶层  : $folderName\（解压即得一个现成目录）"
Write-Host "  自检        : 通过 —— ArchiveFixer.exe / tools\7zip\7z.exe / tools\unrar\UnRAR.exe / LICENSE 四样齐"
Write-Host "  临时目录    : $WorkDirectory"
Write-Host ''
if (-not $SelfContained) {
    Write-Host '  ⚠ 这一档是框架依赖版：目标机需要 .NET 8 桌面运行时（x64）。' -ForegroundColor Yellow
    Write-Host '    对方没装运行时就用 -SelfContained 那一档。' -ForegroundColor Yellow
}

exit 0
