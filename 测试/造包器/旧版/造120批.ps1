#requires -Version 5.1
<#
  120 批自包含造包器。

  只使用本文件内的函数；不调用、不读取四个旧造包脚本。
  已移植：普通 50（= 旧 大测试-造困难包.ps1 的最新逻辑）。
  待移植：特定 20 / 异常 25 / 空间 25。
#>
[CmdletBinding()]
param(
    [int]$SeedBase = 917000000,
    [string]$OutputRoot = 'H:\BaiduNetdiskDownload\困难大测试\大测试备份位置',
    [string]$ContentRoot = 'H:\BaiduNetdiskDownload\困难大测试\原物\困难内容',
    [string]$MediaRoot = 'H:\BaiduNetdiskDownload\困难大测试\原物\生成的媒体',
    [string]$SevenZip = 'E:\ArchiveFixer\tools\7zip\7z.exe',
    [string]$Rar = 'C:\Program Files\WinRAR\Rar.exe',
    [string]$WinRAR = 'C:\Program Files\WinRAR\WinRAR.exe',
    [string]$Password = '2中！0@文#2删￥6除%',
    [switch]$Force,
    [switch]$KeepWork,
    [ValidateSet('All', 'Single', 'SelfCheck')]
    [string]$Mode = 'All',
    [ValidateSet('All', 'Normal', 'Specific', 'Abnormal', 'SpaceC', 'SpaceB')]
    [string]$Section = 'All',
    [int]$Only = 0
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$seedText = $SeedBase.ToString([Globalization.CultureInfo]::InvariantCulture)
$batchName = $seedText + '-120批次-备份'
$batchRoot = Join-Path $OutputRoot $batchName
$logRoot = Join-Path $batchRoot '.logs'

function Write-Log([string]$Message, [string]$Level = 'INFO') {
    $line = '[{0}] [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    if (Test-Path -LiteralPath $logRoot) { Add-Content -LiteralPath (Join-Path $logRoot '造包.log') -Value $line -Encoding UTF8 }
}

function Fail([string]$Message) {
    Write-Log $Message 'ERROR'
    throw $Message
}

function Assert-BatchRoot([string]$Root) {
    # 只校验，不删任何东西。
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $marker = Join-Path $Root '.120-generated'
    if (-not (Test-Path -LiteralPath $marker)) { Fail "拒绝写入：$Root 没有 120 标记" }
    $markerText = ([IO.File]::ReadAllText($marker)).Trim()
    if ($markerText -ne $seedText) { Fail "拒绝写入：标记 '$markerText' 与种子 '$seedText' 不一致" }
    $unknown = Get-ChildItem -LiteralPath $Root -Force |
        Where-Object { $_.Name -notmatch '^\d{9}$|^[CB]\d{2}$|^\.logs$|^\.120-generated$|^120-manifest\.json$|^120-report\.json$' }
    if ($unknown) { Fail "拒绝写入：$Root 顶层有非本生成器内容：$($unknown.Name -join ', ')" }
}

function Remove-TargetSamples([string]$Root, [object[]]$Targets, [bool]$ForceRebuild) {
    # 只删本次要重建的那几个样本目录，整批根目录永不整删。
    $existing = @($Targets | Where-Object { Test-Path -LiteralPath (Join-Path $Root $_.Id) })
    if ($existing.Count -eq 0) { return }
    if (-not $ForceRebuild) {
        $names = ($existing | ForEach-Object { $_.Id } | Select-Object -First 5) -join ','
        Fail ("目标样本已存在：{0}{1}（加 -Force 重建）" -f $names, $(if ($existing.Count -gt 5) { " 等 $($existing.Count) 份" } else { '' }))
    }
    foreach ($e in $existing) { Remove-Item -LiteralPath (Join-Path $Root $e.Id) -Recurse -Force }
}

function Get-BasePart([string]$Name) {
    $dot = $Name.IndexOf('.')
    if ($dot -lt 0) { return $Name }
    return $Name.Substring(0, $dot)
}

function Get-SuffixPart([string]$Name) {
    $dot = $Name.IndexOf('.')
    if ($dot -lt 0) { return '' }
    return $Name.Substring($dot)
}

function Test-LegalFileName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    if ($Name -ne $Name.TrimEnd('.', ' ')) { return $false }
    if ($Name -match '[\x00-\x1F<>:"/\\|?*]') { return $false }
    return $true
}

function Invoke-Tool([string]$Tool, [string[]]$Arguments, [string]$WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        $output = & $Tool @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            Fail ("命令失败：{0} {1}`n{2}" -f $Tool, ($Arguments -join ' '), ($output -join "`n"))
        }
        return $output
    }
    finally { Pop-Location }
}

function New-Archive {
    param(
        [string]$Format,
        [string]$ArchiveBase,
        [string[]]$Inputs,
        [string]$WorkDirectory,
        [string]$PasswordValue = ''
    )

    $passwordArg = @()
    if ($PasswordValue.Length -gt 0) { $passwordArg = @('-p' + $PasswordValue) }

    switch ($Format) {
        '7z' {
            $archive = $ArchiveBase + '.7z'
            $argList = @('a', '-t7z', '-mx=0', '-y') + $passwordArg + @($archive) + $Inputs
            Invoke-Tool $SevenZip $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            Assert-SevenZipSignature $WorkDirectory $archive
            return @($archive)
        }
        'vol7z' {
            $archive = $ArchiveBase + '.7z'
            $argList = @('a', '-t7z', '-mx=0', '-y', '-v1048576b') + $passwordArg + @($archive) + $Inputs
            Invoke-Tool $SevenZip $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            $volPieces = @(Get-ChildItem -LiteralPath $WorkDirectory -Filter ($ArchiveBase + '.7z.*') | Sort-Object Name | ForEach-Object Name)
            if ($volPieces.Count -gt 0) { Assert-SevenZipSignature $WorkDirectory $volPieces[0] }
            return $volPieces
        }
        'zip7z' {
            $archive = $ArchiveBase + '.zip'
            $argList = @('a', '-tzip', '-mx=0', '-y', $archive) + $Inputs
            Invoke-Tool $SevenZip $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            return @($archive)
        }
        'volzip7z' {
            $archive = $ArchiveBase + '.zip'
            $argList = @('a', '-tzip', '-mx=0', '-y', '-v1m', $archive) + $Inputs
            Invoke-Tool $SevenZip $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            return @(Get-ChildItem -LiteralPath $WorkDirectory -Filter ($ArchiveBase + '.zip.*') | Sort-Object Name | ForEach-Object Name)
        }
        'rar5' {
            $archive = $ArchiveBase + '.rar'
            $argList = @('a', '-ma5', '-m0', '-idq', '-y') + $passwordArg + @($archive) + $Inputs
            Invoke-Tool $Rar $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            return @($archive)
        }
        'rar4' {
            $archive = $ArchiveBase + '.rar'
            $argList = @('a', '-ma4', '-m0', '-idq', '-y') + $passwordArg + @($archive) + $Inputs
            Invoke-Tool $Rar $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            return @($archive)
        }
        'volrar5' {
            $archive = $ArchiveBase + '.rar'
            $argList = @('a', '-ma5', '-m0', '-idq', '-y', '-v1048576b') + $passwordArg + @($archive) + $Inputs
            Invoke-Tool $Rar $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            return @(Get-ChildItem -LiteralPath $WorkDirectory -Filter ($ArchiveBase + '.part*.rar') | Sort-Object Name | ForEach-Object Name)
        }
        'volrar4' {
            $archive = $ArchiveBase + '.rar'
            $argList = @('a', '-ma4', '-m0', '-idq', '-y', '-v1048576b') + $passwordArg + @($archive) + $Inputs
            Invoke-Tool $Rar $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            return @(Get-ChildItem -LiteralPath $WorkDirectory -Filter ($ArchiveBase + '.part*.rar') | Sort-Object Name | ForEach-Object Name)
        }
        'ziprar' {
            $archive = $ArchiveBase + '.zip'
            $argList = @('a', '-tzip', '-mx=0', '-y', $archive) + $Inputs
            Invoke-Tool $SevenZip $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            return @($archive)
        }
        'volziprar' {
            $archive = $ArchiveBase + '.zip'
            $argList = @('a', '-tzip', '-mx=0', '-y', '-v1m', $archive) + $Inputs
            Invoke-Tool $SevenZip $argList $WorkDirectory | Out-Null
            Assert-CreatedArchiveIntact $archive $WorkDirectory $PasswordValue
            return @(Get-ChildItem -LiteralPath $WorkDirectory -Filter ($ArchiveBase + '.zip.*') | Sort-Object Name | ForEach-Object Name)
        }
        default { Fail "未知格式：$Format" }
    }
}

function Assert-CreatedArchiveIntact([string]$ArchivePath, [string]$WorkDirectory, [string]$PasswordValue) {
    # 生成后整包自检：光看 4 字节头挡不住“尾部少几个字节”，必须真的 7z t 一遍。
    # 分卷只测首片（7z 会自己把同目录的后续片串起来）。
    $path = if ([IO.Path]::IsPathRooted($ArchivePath)) { $ArchivePath } else { Join-Path $WorkDirectory $ArchivePath }
    if (-not (Test-Path -LiteralPath $path)) {
        $dir = Split-Path -Parent $path
        $leaf = Split-Path -Leaf $path
        $cand = @(Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like ($leaf + '.*') } | Sort-Object Name | Select-Object -First 1)
        if ($cand.Count -eq 0) {
            $stem = [IO.Path]::GetFileNameWithoutExtension($leaf)
            $cand = @(Get-ChildItem -LiteralPath $dir -File -Filter ($stem + '.part*.rar') -ErrorAction SilentlyContinue |
                Sort-Object Name | Select-Object -First 1)
        }
        if ($cand.Count -eq 0) { Fail "找不到刚造出来的包：$path" }
        $path = $cand[0].FullName
    }
    $argList = @('t', '-y')
    if ($PasswordValue.Length -gt 0) { $argList += ('-p' + $PasswordValue) }
    $argList += $path
    Push-Location $WorkDirectory
    try {
        $out = & $SevenZip @argList 2>&1
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($code -ne 0) {
        Fail ("包不完整（7z t 退出码 {0}）：{1}`n{2}" -f $code, $path, (($out | Select-Object -Last 5) -join "`n"))
    }
}
function Assert-SevenZipSignature([string]$WorkDirectory, [string]$Name) {
    # 生成后自检：7z 头必须是 37 7A BC AF。实测出现过分卷后续片被当成完整包写进样本的情况。
    $path = if ([IO.Path]::IsPathRooted($Name)) { $Name } else { Join-Path $WorkDirectory $Name }
    if (-not (Test-Path -LiteralPath $path)) { Fail "7z 产物不存在：$path" }
    $head = New-Object byte[] 4
    $fs = [IO.File]::OpenRead($path)
    try { [void]$fs.Read($head, 0, 4) } finally { $fs.Dispose() }
    if ($head[0] -ne 0x37 -or $head[1] -ne 0x7A -or $head[2] -ne 0xBC -or $head[3] -ne 0xAF) {
        Fail ("7z 头不对（不是 37 7A BC AF）：{0}" -f $path)
    }
}
function New-Content([string]$Path, [int]$Bytes = 1048576) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    [IO.File]::WriteAllBytes($Path, $buffer)
}

$Junk = 'abcdefghijklmnopqrstuvwxyz0123456789!@#$%^&_-+=~'
$MethodNames = @('中文插入', '非中文杂质', '删加后缀', '常规后缀', '重复字符')

function New-DisguisedName {
    param([string]$OriginalName, [System.Random]$Rng, [ref]$Methods, [string]$RequiredMarker = '', [bool]$ForceMarker = $true)

    $base = Get-BasePart $OriginalName
    $suffix = Get-SuffixPart $OriginalName
    $chosen = New-Object System.Collections.Generic.List[string]

    $count = 2 + $Rng.Next(2)
    for ($i = 0; $i -lt $count; $i++) {
        $method = $MethodNames[$Rng.Next($MethodNames.Count)]
        switch ($method) {
            '中文插入' {
                $token = if ($Rng.Next(2) -eq 0) { '删除' } else { '中文' }
                if ($suffix.Length -eq 0) { $suffix = '.' + $token }
                else {
                    $minPos = if ($suffix.StartsWith('.')) { 1 } else { 0 }; $pos = $Rng.Next($minPos, $suffix.Length + 1)
                    $suffix = $suffix.Insert($pos, $token)
                }
            }
            '非中文杂质' {
                $len = 2 + $Rng.Next(2)
                $junk = -join (1..$len | ForEach-Object { $Junk[$Rng.Next($Junk.Length)] })
                if ($suffix.Length -eq 0) { $suffix = '.' + $junk }
                else {
                    $minPos = if ($suffix.StartsWith('.')) { 1 } else { 0 }; $pos = $Rng.Next($minPos, $suffix.Length + 1)
                    $suffix = $suffix.Insert($pos, $junk)
                }
            }
            '删加后缀' {
                if ($Rng.Next(2) -eq 0) {
                    if ($suffix.Length -gt 0) {
                        $lastDot = $suffix.LastIndexOf('.')
                        $suffix = if ($lastDot -gt 0) { $suffix.Substring(0, $lastDot) } else { '' }
                    }
                }
                else {
                    $fake = if ($RequiredMarker) { $RequiredMarker } else { @(".001", ".004", ".part1.rar")[$Rng.Next(3)] }
                    if ($RequiredMarker) {
                        # 已经有 .NNN 尾巴的先摘掉，避免出现 xxx.001.004 这种双卷号。
                        $suffix = [regex]::Replace($suffix, '\.\d{3}$', '')
                    }
                    if ($suffix.Length -eq 0) { $suffix = $fake } else { $suffix += $fake }
                }
            }
            '常规后缀' {
                $fake = @('.jpg', '.pdf', '.mp4')[$Rng.Next(3)]
                $suffix = $fake
            }
            '重复字符' {
                $seedText2 = if ($suffix.Length -gt 0) { $suffix } else { 'x' }
                $ch = $seedText2[$Rng.Next($seedText2.Length)]
                $rep = -join (1..(2 + $Rng.Next(4)) | ForEach-Object { $ch })
                if ($suffix.Length -eq 0) { $suffix = '.' + $rep }
                else {
                    $minPos = if ($suffix.StartsWith('.')) { 1 } else { 0 }; $pos = $Rng.Next($minPos, $suffix.Length + 1)
                    $suffix = $suffix.Insert($pos, $rep)
                }
            }
        }
        $chosen.Add($method)
    }

    $newName = $base + $suffix
    $newName = [regex]::Replace($newName, '\.z(\d{2})', '.q$1', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $newName = [regex]::Replace($newName, '(?i)(ip)(\d)', '$1.$2')
    $newName = [regex]::Replace($newName, '(?i)(7z|rar|zip)(\d)', '$1.$2')
    $newName = [regex]::Replace($newName, '(?i)(zip|7z|rar)(\d)', '$1.$2')
    if ($newName -eq $OriginalName) {
        $newName = $base + $suffix + '.删除'
        $chosen.Add('中文插入')
    }
    $markerRegex = '\.(7z|zip|rar|z\d\d|part\d+\.rar|7z\.\d{3}|zip\.\d{3}|rar\.\d{3}|\d{3})$'
    if (-not [string]::IsNullOrEmpty($RequiredMarker)) {
        # 卷号以调用方按位置算出的为准：先摘掉前面猜出来的 .NNN 尾巴，再补正确的。
        $newName = [regex]::Replace($newName, '\.\d{3}$', '')
        if (-not $newName.EndsWith($RequiredMarker, [StringComparison]::OrdinalIgnoreCase)) {
            $newName += $RequiredMarker
            $chosen.Add('删加后缀')
        }
    }
    elseif ($ForceMarker -and $newName -notmatch $markerRegex) {
        $volumeDigits = [regex]::Match($OriginalName, '\.(\d{3})$').Groups[1].Value
        if ([string]::IsNullOrEmpty($volumeDigits)) { $volumeDigits = '001' }
        $newName += '.' + $volumeDigits
        $chosen.Add('删加后缀')
    }
    if (-not $ForceMarker -and $newName -match '\.(part\d+\.rar|z\d{2}|\d{3})$') {
        $newName += '.包'
        $chosen.Add('删加后缀')
    }
    $newName = $newName.TrimEnd('.', ' ')
    $chosenArray = $chosen.ToArray()
    return [pscustomobject]@{ Name = $newName; Methods = $chosenArray }
}

function Get-CollisionSafePath {
    param([string]$Directory, [string]$Name)
    $path = Join-Path $Directory $Name
    if (-not (Test-Path -LiteralPath $path)) { return $path }
    $base = Get-BasePart $Name
    $suffix = Get-SuffixPart $Name
    for ($i = 1; $i -lt 1000; $i++) {
        $candidate = Join-Path $Directory ($base + $suffix + '.dup' + $i.ToString('000'))
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    Fail "撞名兜底失败：$Name"
}

$FixedTime = [datetime]'2020-01-01 00:00:00'

function Set-FixedStamp([string]$WorkDirectory, [string[]]$Names) {
    # 固定输入文件的修改时间：7z/rar 会把时间写进压缩头，不固定则同种子产物字节不稳定。
    foreach ($name in $Names) {
        $path = Join-Path $WorkDirectory $name
        if (Test-Path -LiteralPath $path) { [IO.File]::SetLastWriteTime($path, $FixedTime) }
    }
}

function Write-Recipe([string]$SampleRoot, $Recipe) {
    $Recipe | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $SampleRoot 'recipe.json') -Encoding utf8NoBOM
}

function New-SampleRoot([string]$Id) {
    $root = Join-Path $batchRoot $Id
    New-Item -ItemType Directory -Path (Join-Path $root '原包') -Force | Out-Null
    return $root
}

# ---------------------------------------------------------------- 普通 50

$ContentFiles = @(Get-ChildItem -LiteralPath $ContentRoot -File | Sort-Object Name)
$MediaFiles = @(Get-ChildItem -LiteralPath $MediaRoot -File | Sort-Object Name)
$ContentBytes = $null
if ($ContentFiles.Count -gt 0) {
    $largest = ($ContentFiles | Sort-Object Length -Descending | Select-Object -First 1).FullName
    $ContentBytes = [IO.File]::ReadAllBytes($largest)
}

$Scenarios = @(
    @{ placement = 'Original'; sourceHandling = 'KeepInPlace'; contentHandling = 'Keep' },
    @{ placement = 'Original'; sourceHandling = 'KeepInPlace'; contentHandling = 'Delete' },
    @{ placement = 'Original'; sourceHandling = 'DeleteInPlace'; contentHandling = 'Keep' },
    @{ placement = 'Original'; sourceHandling = 'DeleteInPlace'; contentHandling = 'Delete' },
    @{ placement = 'Specified'; sourceHandling = 'KeepInPlace'; contentHandling = 'Keep' },
    @{ placement = 'Specified'; sourceHandling = 'KeepInPlace'; contentHandling = 'Delete' },
    @{ placement = 'Specified'; sourceHandling = 'DeleteInPlace'; contentHandling = 'Keep' },
    @{ placement = 'Specified'; sourceHandling = 'DeleteInPlace'; contentHandling = 'Delete' }
)
$FailureCombos = @(0, 1, 2, 3, 4, 5, 6, 7, 0, 1)
$FailurePlacements = @(
    'Specified', 'Specified', 'Specified', 'Specified',
    'Original', 'Original', 'Original', 'Original',
    'Specified', 'Original'
)

function Generate-Normal([int]$Index) {
    if ($null -eq $ContentBytes) { Fail '内容目录为空，无法生成普通样本' }
    $seed = $SeedBase + $Index
    $id = $seed.ToString([Globalization.CultureInfo]::InvariantCulture)
    $sampleRoot = New-SampleRoot $id
    $outRoot = Join-Path $sampleRoot '原包'
    $workRoot = Join-Path $sampleRoot '.build'
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

    $rng = New-Object System.Random($seed)
    $isFailure = $Index -gt 40
    $failureKind = if ($isFailure) { if ($Index -le 45) { 'missing' } else { 'corrupted' } } else { '' }
    $scenario = if ($isFailure) {
        $baseScenario = $Scenarios[$FailureCombos[$Index - 41]]
        [ordered]@{
            placement = $FailurePlacements[$Index - 41]
            sourceHandling = $baseScenario.sourceHandling
            contentHandling = $baseScenario.contentHandling
            failure = $true
        }
    }
    else { $Scenarios[($Index - 1) % 8] }

    $stem = 'aaa'
    $wantedMb = if ($isFailure) { 5 } elseif ($rng.Next(2) -eq 0) { 4 } else { 5 }
    $wantedBytes = ($wantedMb * 1024 * 1024) - 65536
    if ($wantedBytes -gt $ContentBytes.Length) { $wantedBytes = $ContentBytes.Length }
    $contentName = "content-$Index.bin"
    $contentPath = Join-Path $workRoot $contentName
    $slice = New-Object byte[] $wantedBytes
    [Array]::Copy($ContentBytes, 0, $slice, 0, $wantedBytes)
    [IO.File]::WriteAllBytes($contentPath, $slice)
    $slice = $null
    $current = @($contentName)
    Set-FixedStamp $workRoot $current

    try {
        # 3 单包 + 2 分卷；最后一层必须是分卷
        $singlePositions = New-Object System.Collections.Generic.List[int]
        $volumePositions = New-Object System.Collections.Generic.List[int]
        $volumePositions.Add(4)
        $volumePositions.Add(1 + $rng.Next(3))
        while ($volumePositions.Count -lt 2) { $x = 1 + $rng.Next(3); if (-not $volumePositions.Contains($x)) { $volumePositions.Add($x) } }
        for ($i = 0; $i -lt 5; $i++) { if (-not $volumePositions.Contains($i)) { $singlePositions.Add($i) } }

        $singleFormats = @('7z', 'rar5', 'zip7z') | Sort-Object { $rng.Next() }
        $volumeFormats = @('vol7z', 'volrar5', 'volzip7z', 'volrar4', 'volziprar')
        $layerFormats = @()
        for ($i = 0; $i -lt 5; $i++) { $layerFormats += '' }
        for ($i = 0; $i -lt $singlePositions.Count; $i++) { $layerFormats[$singlePositions[$i]] = $singleFormats[$i] }
        foreach ($vp in $volumePositions) { $layerFormats[$vp] = $volumeFormats[$rng.Next($volumeFormats.Count)] }
        if ($isFailure) { $layerFormats[4] = 'volzip7z' }

        $layers = New-Object System.Collections.Generic.List[object]
        $lastPieces = @()
        for ($level = 0; $level -lt 5; $level++) {
            $fmt = $layerFormats[$level]
            $cls = if ($fmt -like '*7z*') { '7z' } elseif ($fmt -like '*rar*') { 'rar' } else { 'zip' }
            $outBase = "$stem-L$level"
            Set-FixedStamp $workRoot $current
            $outputs = @(New-Archive -Format $fmt -ArchiveBase $outBase -Inputs $current -WorkDirectory $workRoot -PasswordValue $Password)
            if ($outputs.Count -eq 0) { Fail "第 $level 层没有产物 $id" }
            $kind = if ($fmt -like 'vol*') { 'volume' } else { 'single' }
            $layers.Add([ordered]@{
                index = $level
                kind = $kind
                cls = $cls
                format = $fmt
                inputs = @($current)
                outputs = @($outputs)
                volumeArgs = if ($kind -eq 'volume') { if ($fmt -like '*rar*') { @('-v1048576b') } else { @('-v1m') } } else { $null }
                encrypted = ($fmt -notlike '*zip*')
                headerEncrypted = $false
                nonUniform = $false
            })
            $current = @($outputs)
            if ($level -eq 4) { $lastPieces = @($outputs) }
        }

        if ($lastPieces.Count -ne 4 -and $lastPieces.Count -ne 5) { Fail "最后一层分卷片必须是 4 或 5：$($lastPieces.Count)" }

        $files = New-Object System.Collections.ArrayList
        $wrapsSeen = New-Object System.Collections.Generic.HashSet[int]
        $mediaUsedCount = 0

        for ($p = 0; $p -lt $lastPieces.Count; $p++) {
            $piece = $lastPieces[$p]
            $wrapCount = if ($isFailure) { 0 } elseif ($p -eq 0) { 0 } elseif ($p -eq 1) { 1 } elseif ($p -eq 2) { 2 } else { $rng.Next(3) }
            $wrapsSeen.Add($wrapCount) | Out-Null
            $input = $piece
            $wrapRecords = New-Object System.Collections.Generic.List[object]
            for ($w = 1; $w -le $wrapCount; $w++) {
                $wrapFormat = @('7z', 'rar5', 'rar4', 'zip7z')[$rng.Next(4)]
                $wrapperBase = "$stem-W$($p)-$w"
                Set-FixedStamp $workRoot @($input)
                $outer = @(New-Archive -Format $wrapFormat -ArchiveBase $wrapperBase -Inputs @($input) -WorkDirectory $workRoot -PasswordValue $Password)[0]
                $wrapRecords.Add([ordered]@{
                    format = $wrapFormat
                    inner = $input
                    outer = $outer
                    outerCamouflaged = ''
                    outerMethod = ''
                })
                $input = $outer
            }

            $base = Get-BasePart $input
            $methodsRef = $null
            $mediaFile = $null
            $newName = ''
            $requiredMarker = if ($wrapCount -eq 0) { '.' + ($p + 1).ToString('000') } else { '' }

            if ($isFailure) {
                # 失败样本保留标准名，组身份不被伪装/包装破坏。
                $newName = $input
                $methodsRef = @()
            }
            elseif ($mediaUsedCount -lt 2 -and $wrapRecords.Count -gt 0 -and $MediaFiles.Count -gt 0) {
                $mediaFile = $MediaFiles[$rng.Next($MediaFiles.Count)]
                $newName = $base + [IO.Path]::GetExtension($mediaFile.Name)
                $methodsRef = @('媒体前缀')
            }
            else {
                $disguise = New-DisguisedName -OriginalName $input -Rng $rng -Methods ([ref]$methodsRef) -RequiredMarker $requiredMarker -ForceMarker ($wrapCount -eq 0)
                $newName = $disguise.Name
                $methodsRef = $disguise.Methods
            }

            $slot = '片-' + ($p + 1).ToString('000')
            $finalDir = Join-Path (Join-Path (Join-Path $outRoot $stem) 'L4') $slot
            New-Item -ItemType Directory -Path $finalDir -Force | Out-Null
            $newName = $newName.TrimEnd('.', ' ')
            if (-not (Test-LegalFileName $newName)) { Fail "非法文件名：$newName" }
            $target = Get-CollisionSafePath -Directory $finalDir -Name $newName
            Move-Item -LiteralPath (Join-Path $workRoot $input) -Destination $target -Force

            if ($wrapRecords.Count -gt 0) {
                $wrapRecords[$wrapRecords.Count - 1].outerCamouflaged = (Split-Path -Leaf $target)
                $wrapRecords[$wrapRecords.Count - 1].outerMethod = ($methodsRef -join ' + ')
            }

            $mediaPrefix = @()
            if ($null -ne $mediaFile) {
                $mediaBytes = [IO.File]::ReadAllBytes($mediaFile.FullName)
                $archiveBytes = [IO.File]::ReadAllBytes($target)
                [IO.File]::WriteAllBytes($target, ($mediaBytes + $archiveBytes))
                $mediaPrefix = @($mediaFile.Name)
                $mediaUsedCount++
            }

            $record = [ordered]@{
                originalName = $piece
                finalRelative = Join-Path (Join-Path $stem 'L4') (Join-Path $slot (Split-Path -Leaf $target))
                bytes = (Get-Item -LiteralPath $target).Length
                depth = 3
                suffixMethods = @($methodsRef)
                markerTouched = ((Split-Path -Leaf $target) -match '\.(7z|zip|rar|z\d\d|part\d+\.rar|7z\.\d{3}|zip\.\d{3}|rar\.\d{3}|\d{3})$')
                mediaPrefix = $mediaPrefix
                wraps = $wrapRecords.ToArray()
                baseName = $base
            }
            $files.Add($record) | Out-Null
        }

        # 校验（与旧造包器同口径）
        $layerCount = $layers.Count
        $volumeCount = @($layers | Where-Object { $_.kind -eq 'volume' }).Count
        if ($layerCount -ne 5) { Fail "$id 层数不是 5" }
        if ($volumeCount -lt 2) { Fail "$id 分卷层少于 2" }
        if ($layers[4].kind -ne 'volume') { Fail "$id 最后一层不是分卷" }
        if ($files.Count -lt 3) { Fail "$id 最终片少于 3" }

        # 最终片的名字必须互不相同：修完名字再撞上（例如两片都叫 xxx.001）会让整组缺片、定稿发不出内容。
        $pieceNames = @($files | ForEach-Object { Split-Path -Leaf $_.finalRelative })
        if (@($pieceNames | Sort-Object -Unique).Count -ne $pieceNames.Count) { Fail "$id 最终片名字重复：$($pieceNames -join ',')" }
        if (-not $isFailure) {
            foreach ($rc in @(0, 1, 2)) { if (-not $wrapsSeen.Contains($rc)) { Fail "$id 缺少 $rc 次套层" } }
            $mediaFileCount = @($files | Where-Object { @($_.mediaPrefix).Count -gt 0 }).Count
            if ($mediaFileCount -ne 2) { Fail "$id 媒体前缀必须是 2 个：$mediaFileCount" }
            foreach ($file in $files) {
                $finalPath = Join-Path $outRoot $file.finalRelative
                if (-not (Test-Path -LiteralPath $finalPath)) { Fail "$id 最终文件缺失：$finalPath" }
                if ((Get-BasePart (Split-Path -Leaf $finalPath)) -ne $file.baseName) { Fail "$id 基名被改：$finalPath" }
                if (@($file.mediaPrefix).Count -eq 0 -and @($file.suffixMethods).Count -lt 2) { Fail "$id 伪装次数不足：$finalPath" }
                if ((Split-Path -Leaf $finalPath) -match '\.z\d{2}') { Fail "$id ZIP 组混入 .zNN 强锚点：$finalPath" }
                if ($file.wraps.Count -eq 0 -and -not $file.markerTouched) { Fail "$id 分卷标记未命中：$finalPath" }
            }
        }

        # 失败破坏：固定打标准名中间片，不随机破坏族身份
        $victimRel = $null
        if ($isFailure) {
            $victimRel = $files[2].finalRelative
            $victimPath = Join-Path $outRoot $victimRel
            if (-not (Test-Path -LiteralPath $victimPath)) { Fail "$id 失败受害片不存在：$victimPath" }
            if ($failureKind -eq 'missing') {
                Remove-Item -LiteralPath $victimPath -Force
            }
            else {
                $bytes = [IO.File]::ReadAllBytes($victimPath)
                if ($bytes.Length -gt 64) {
                    $count = [Math]::Min(4096, $bytes.Length)
                    for ($i = 0; $i -lt $count; $i++) { $bytes[$i] = 0xA5 }
                    [IO.File]::WriteAllBytes($victimPath, $bytes)
                }
            }
        }

        $placement = $scenario.placement
        $sourceKeep = if ($isFailure) { $true } else { ($scenario.sourceHandling -eq 'KeepInPlace') }
        $processKeep = if ($isFailure) { ($scenario.contentHandling -eq 'Keep') } else { $false }
        $reason = if ($failureKind -eq 'missing') { 'FinalGroupIncomplete' } elseif ($failureKind -eq 'corrupted') { 'FinalGroupCorrupted' } else { '' }
        $groupState = if ($failureKind -eq 'missing') { 'Missing' } else { 'Complete' }

        $recipe = [ordered]@{
            schemaVersion = 2
            seedBase = $SeedBase
            seed = $seed
            sampleNumber = $Index
            stem = $stem
            scenario = $scenario
            sampleId = $id
            category = 'Normal'
            placement = $placement
            expectedOutcome = if ($reason) { 'Failed' } else { 'Succeeded' }
            expectedReason = $reason
            expectedSourceKeep = $sourceKeep
            expectedProcessKeep = $processKeep
            expectedProcessPaths = @($files | ForEach-Object { $_.finalRelative })
            expectedFinalContentPath = if ($reason) { '' } else { $contentName }
            expectedFinalContentName = if ($reason) { '' } else { $contentName }
            expectedContentBytes = if ($reason) { 0 } else { $wantedBytes }
            expectedGroupState = $groupState
            failure = if ($isFailure) {
                [ordered]@{
                    stage = 'final-layer'
                    kind = $failureKind
                    method = if ($failureKind -eq 'missing') { 'remove-one-final-piece' } else { 'zero-one-final-piece' }
                    victimRelative = $victimRel
                }
            } else { $null }
            layers = $layers.ToArray()
            files = $files.ToArray()
            validation = [ordered]@{
                layerCount = $layerCount
                volumeLayerCount = $volumeCount
                lastLayerVolume = ($layers[$layerCount - 1].kind -eq 'volume')
                wrapCounts = @($wrapsSeen | Sort-Object)
                headerEncrypted = $false
                nonUniform = $false
            }
        }

        Write-Recipe $sampleRoot $recipe
        Write-Log ("样本 {0} 完成：层={1} 分卷={2} 片={3} 落点={4}" -f $id, $layerCount, $volumeCount, @($files).Count, $placement)
    }
    finally {
        if (-not $KeepWork) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------- 特定 20

# 1-10：旧 大测试-造特定解压包.ps1 的十个固定布局，原样保留。
$SpecificOldLayouts = @(
    @{ n = 1; layers = 2; parent = '111'; package = '1111'; format = 'rar5'; inner = 'vol7z'; case = 'A'; folder = '111-1'; password = ''; outerPassword = ''; middlePassword = '' },
    @{ n = 2; layers = 2; parent = '222'; package = '2222'; format = 'rar5'; inner = 'vol7z'; case = 'B'; folder = '222-22'; password = 'Demo#Pass1'; outerPassword = 'Demo#Pass1'; middlePassword = '' },
    @{ n = 3; layers = 2; parent = '333'; package = '3333'; format = 'zip7z'; inner = 'vol7z'; case = 'A'; folder = '333-3'; password = ''; outerPassword = ''; middlePassword = '' },
    @{ n = 4; layers = 2; parent = '444'; package = '4444'; format = 'rar5'; inner = 'vol7z'; case = 'B'; folder = '444-44'; password = 'Demo#Pass2'; outerPassword = 'Demo#Pass2'; middlePassword = '' },
    @{ n = 5; layers = 3; parent = '555'; package = '5555'; format = '7z'; inner = 'vol7z'; case = 'A'; folder = '555-5'; password = 'Demo#Pass2'; outerPassword = 'Demo#Pass1'; middlePassword = 'Demo#Pass2' },
    @{ n = 6; layers = 3; parent = '666'; package = '6666'; format = 'rar5'; inner = 'vol7z'; case = 'B'; folder = '666-66'; password = 'Demo#Pass1'; outerPassword = 'Demo#Pass2'; middlePassword = 'Demo#Pass1' },
    @{ n = 7; layers = 3; parent = '777'; package = '7777'; format = 'zip7z'; inner = 'single'; case = 'A'; folder = '777-7'; password = ''; outerPassword = ''; middlePassword = '' },
    @{ n = 8; layers = 3; parent = '888'; package = '8888'; format = '7z'; inner = 'single'; case = 'B'; folder = '888-88'; password = 'Demo#Pass2'; outerPassword = 'Demo#Pass2'; middlePassword = 'Demo#Pass2' },
    @{ n = 9; layers = 2; parent = '999'; packages = @('9991', '9992', '9993'); format = 'rar5'; inner = 'vol7z'; case = 'A'; folder = '999-1'; password = 'Demo#Pass1'; outerPassword = 'Demo#Pass1'; middlePassword = '' },
    @{ n = 10; layers = 2; parent = '111'; nested = @(
            @{ Parent = '111\1111'; Package = '1111'; Case = 'A'; Folder = '111-1' },
            @{ Parent = '222\2222'; Package = '2222'; Case = 'B'; Folder = '222-22' },
            @{ Parent = '333\3333'; Package = '3333'; Case = 'A'; Folder = '333-3' }
        ); format = 'rar5'; inner = 'vol7z'; password = ''; outerPassword = ''; middlePassword = '' }
)

# 11-20：新增十个形状。每条的 Layers/Inner/Middle/Outer 组合互不相同。
$SpecificNewLayouts = @(
    @{ n = 11; shape = '直接单包'; parent = 'p11'; package = 'n11'; case = 'A'; folder = 'n11-单文件夹'; layers = 1; inner = 'none'; middle = 'none'; outer = 'single'; outerFormat = '7z'; middleFormat = '7z'; payload = 1600000; pwOuter = ''; pwMiddle = ''; pwInner = ''; empty = $false },
    @{ n = 12; shape = '两层标准分卷'; parent = 'p12'; package = 'n12'; case = 'B'; folder = 'n12-22'; layers = 1; inner = 'none'; middle = 'none'; outer = 'volume'; outerFormat = '7z'; middleFormat = '7z'; payload = 2500000; pwOuter = ''; pwMiddle = ''; pwInner = ''; empty = $false },
    @{ n = 13; shape = '两层单包'; parent = 'p13'; package = 'n13'; case = 'B'; folder = 'n13-33'; layers = 2; inner = 'single'; middle = 'none'; outer = 'single'; outerFormat = '7z'; middleFormat = '7z'; payload = 1600000; pwOuter = ''; pwMiddle = ''; pwInner = 'Demo#Pass1'; empty = $false },
    @{ n = 14; shape = '三层单包'; parent = 'p14'; package = 'n14'; case = 'A'; folder = 'n14-4'; layers = 3; inner = 'single'; middle = 'single'; outer = 'single'; outerFormat = 'rar5'; middleFormat = 'zip7z'; payload = 1600000; pwOuter = 'Demo#Pass1'; pwMiddle = 'Demo#Pass2'; pwInner = 'Demo#Pass1'; empty = $false },
    @{ n = 15; shape = '三层中间分卷'; parent = 'p15'; package = 'n15'; case = 'B'; folder = 'n15-55'; layers = 3; inner = 'single'; middle = 'volume'; outer = 'single'; outerFormat = '7z'; middleFormat = '7z'; payload = 1600000; pwOuter = ''; pwMiddle = 'Demo#Pass2'; pwInner = ''; empty = $false },
    @{ n = 16; shape = '最后一层落在文件夹'; parent = 'p16'; package = 'n16'; case = 'A'; folder = 'n16-六六六'; layers = 2; inner = 'vol7z'; middle = 'none'; outer = 'single'; outerFormat = 'rar5'; middleFormat = '7z'; payload = 1600000; pwOuter = 'Demo#Pass1'; pwMiddle = ''; pwInner = 'Demo#Pass2'; empty = $false },
    @{ n = 17; shape = '跨盘分卷'; parent = 'p17'; package = 'n17'; case = 'B'; folder = 'n17-77'; layers = 0; inner = 'none'; middle = 'none'; outer = 'spanned'; outerFormat = 'zip'; middleFormat = '7z'; payload = 2500000; pwOuter = ''; pwMiddle = ''; pwInner = ''; empty = $false },
    @{ n = 18; shape = '密码错误'; parent = 'p18'; package = 'n18'; case = 'A'; folder = 'n18-8'; layers = 2; inner = 'vol7z'; middle = 'none'; outer = 'single'; outerFormat = '7z'; middleFormat = '7z'; payload = 1600000; pwOuter = 'Demo#Pass1'; pwMiddle = ''; pwInner = 'Wrong#NoBook'; empty = $false },
    @{ n = 19; shape = '空内容'; parent = 'p19'; package = 'n19'; case = 'A'; folder = 'n19-9'; layers = 1; inner = 'none'; middle = 'none'; outer = 'single'; outerFormat = '7z'; middleFormat = '7z'; payload = 0; pwOuter = ''; pwMiddle = ''; pwInner = ''; empty = $true },
    @{ n = 20; shape = '指定位置/原位置各一份'; parent = 'p20'; package = 'n20'; case = 'B'; folder = 'n20-0'; layers = 2; inner = 'single'; middle = 'none'; outer = 'single'; outerFormat = 'rar5'; middleFormat = '7z'; payload = 1600000; pwOuter = 'Demo#Pass2'; pwMiddle = ''; pwInner = 'Demo#Pass2'; empty = $false }
)

function New-SpecificPayload {
    param([string]$Root, [string]$Case, [string]$FolderName, [int]$Bytes, [bool]$Empty)
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    if ($Empty) { return }
    if ($Bytes -gt $ContentBytes.Length) { $Bytes = $ContentBytes.Length }
    $slice = New-Object byte[] $Bytes
    [Array]::Copy($ContentBytes, 0, $slice, 0, $Bytes)
    if ($Case -eq 'A') {
        $folder = Join-Path $Root $FolderName
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $folder 'a.bin'), $slice)
        Set-Content -LiteralPath (Join-Path $folder '说明.txt') -Value '只有一个文件夹的内容物' -Encoding utf8NoBOM
    }
    else {
        New-Item -ItemType Directory -Path (Join-Path $Root 'folder-A') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $Root 'folder-B') -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $Root 'folder-A\a.bin'), $slice)
        Set-Content -LiteralPath (Join-Path $Root 'folder-B\b.txt') -Value '多文件夹/散文件根' -Encoding utf8NoBOM
        Set-Content -LiteralPath (Join-Path $Root 'top.txt') -Value '根上还有散文件' -Encoding utf8NoBOM
    }
}

function Write-SpecificArchive {
    # 在 $Work 里造一个包；返回叶子名数组。Format 支持 7z / rar5 / zip7z / vol7z。
    param([string]$Format, [string]$Base, [string]$Work, [string]$Password, [string[]]$Inputs = @('*'))
    return @(New-Archive -Format $Format -ArchiveBase $Base -Inputs $Inputs -WorkDirectory $Work -PasswordValue $Password)
}

function New-SpecificPackage {
    param(
        [string]$WorkRoot,
        [string]$OutRoot,
        [string]$Parent,
        [string]$Package,
        [string]$Case,
        [string]$Folder,
        [int]$Layers,
        [string]$InnerKind,
        [string]$MiddleKind,
        [string]$OuterKind,
        [string]$OuterFormat,
        [string]$MiddleFormat,
        [string]$PwOuter,
        [string]$PwMiddle,
        [string]$PwInner,
        [int]$PayloadBytes,
        [bool]$Empty
    )

    $payload = Join-Path $WorkRoot ('payload-' + $Package)
    New-SpecificPayload -Root $payload -Case $Case -FolderName $Folder -Bytes $PayloadBytes -Empty $Empty
    $innerBase = if ($Case -eq 'B') { $Folder } else { $Package }
    $stage = $payload
    $inputs = if ($Empty) { @() } else { @('*') }

    # 最内层
    if ($Layers -ge 2) {
        $flat = Join-Path $WorkRoot ('flat-' + $Package)
        New-Item -ItemType Directory -Path $flat -Force | Out-Null
        if ($InnerKind -eq 'vol7z') {
            $pieces = @(Write-SpecificArchive 'vol7z' $innerBase $payload $PwInner $inputs)
            if ($pieces.Count -lt 2 -or $pieces.Count -gt 3) { Fail "内层分卷片数不是 2–3：$($pieces.Count)" }
            foreach ($pc in $pieces) {
                $new = $pc + '删'
                Move-Item -LiteralPath (Join-Path $payload $pc) -Destination (Join-Path $flat $new) -Force
            }
        }
        else {
            $one = @(Write-SpecificArchive '7z' $innerBase $payload $PwInner $inputs)[0]
            Move-Item -LiteralPath (Join-Path $payload $one) -Destination (Join-Path $flat $one) -Force
        }
        $stage = $flat
    }

    # 中间层
    if ($Layers -ge 3) {
        $middle = Join-Path $WorkRoot ('middle-' + $Package)
        New-Item -ItemType Directory -Path $middle -Force | Out-Null
        Copy-Item -Path (Join-Path $stage '*') -Destination $middle -Force
        if ($MiddleKind -eq 'volume') {
            $mPieces = @(Write-SpecificArchive 'vol7z' ($Package + '-mid') $middle $PwMiddle $inputs)
            if ($mPieces.Count -lt 2 -or $mPieces.Count -gt 3) { Fail "中间分卷片数不是 2–3：$($mPieces.Count)" }
            Get-ChildItem -LiteralPath $middle -Force | Where-Object { $_.Name -notin $mPieces } | Remove-Item -Force -Recurse
        }
        else {
            $mOne = @(Write-SpecificArchive $MiddleFormat ($Package + '-mid') $middle $PwMiddle $inputs)[0]
            Get-ChildItem -LiteralPath $middle -Force | Where-Object { $_.Name -ne $mOne } | Remove-Item -Force -Recurse
        }
        $stage = $middle
    }

    # 外层
    $finalParent = Join-Path $OutRoot $Parent
    New-Item -ItemType Directory -Path $finalParent -Force | Out-Null
    $sources = @()
    if ($OuterKind -eq 'spanned') {
        # 跨盘 ZIP：WinRAR 产 .z01/.z02/….zip
        $winRarArgs = @('a', '-afzip', '-m0', '-ibck', '-y', '-r', '-v1m', ($Package + '.zip'), '*')
        $winRarProc = Start-Process -FilePath $WinRAR -ArgumentList $winRarArgs -WorkingDirectory $stage -WindowStyle Hidden -Wait -PassThru
        if ($winRarProc.ExitCode -ne 0) { Fail "WinRAR 失败：$($winRarProc.ExitCode)" }
        $sources = @(Get-ChildItem -LiteralPath $stage -File | Where-Object { $_.Name -like ($Package + '.z*') -or $_.Name -eq ($Package + '.zip') } | Sort-Object Name | ForEach-Object Name)
        foreach ($s in $sources) { Move-Item -LiteralPath (Join-Path $stage $s) -Destination (Join-Path $finalParent $s) -Force }
    }
    elseif ($OuterKind -eq 'volume') {
        $oSources = @(Write-SpecificArchive 'vol7z' $Package $stage $PwOuter $inputs)
        if ($oSources.Count -lt 2) { Fail "外层分卷片数不足：$($oSources.Count)" }
        foreach ($s in $oSources) { Move-Item -LiteralPath (Join-Path $stage $s) -Destination (Join-Path $finalParent $s) -Force }
        $sources = $oSources
    }
    else {
        $one = @(Write-SpecificArchive $OuterFormat $Package $stage $PwOuter $inputs)[0]
        Move-Item -LiteralPath (Join-Path $stage $one) -Destination (Join-Path $finalParent $one) -Force
        $sources = @($one)
    }

    $outFolder = if ($Case -eq 'A') { $Folder } else { $innerBase }
    $outRel = if ($Empty) { '' } elseif ($Case -eq 'A') { Join-Path $outFolder 'a.bin' } else { Join-Path (Join-Path $outFolder 'folder-A') 'a.bin' }
    $pw = if ($Layers -ge 3) { @($PwOuter, $PwMiddle, $PwInner) } else { @($PwOuter, $PwInner) }

    return [ordered]@{
        package = $Package
        layers = $Layers
        case = $Case
        sourceRelative = Join-Path $Parent $sources[0]
        sourceFiles = @($sources | ForEach-Object { Join-Path $Parent $_ })
        expectedOutputFolder = $outFolder
        expectedOutputRelative = $outRel
        expectedContentBytes = $(if ($Empty) { 0 } else { [Math]::Min($PayloadBytes, $ContentBytes.Length) })
        expectedSourceKeep = $true
        perLayerPasswords = @($pw)
    }
}

function Generate-Specific([int]$Index) {
    $id = ($SeedBase + 100000 + $Index).ToString([Globalization.CultureInfo]::InvariantCulture)
    $sampleRoot = New-SampleRoot $id
    $outRoot = Join-Path $sampleRoot '原包'
    $workRoot = Join-Path $sampleRoot '.build'
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
    $placement = if ($Index -le 10) { 'Original' } else { 'Specified' }
    try {
        $records = @()
        $shape = ''
        if ($Index -le 10) {
            $layout = $SpecificOldLayouts[$Index - 1]
            $shape = '旧形状-' + $Index
            $targets = @()
            if ($layout.ContainsKey('packages')) {
                foreach ($pkg in $layout.packages) {
                    $targets += @{
                        Parent = $layout.parent
                        Package = $pkg
                        Case = $(if ($pkg -eq '9992') { 'B' } else { 'A' })
                        Folder = $(if ($pkg -eq '9991') { '999-1' } elseif ($pkg -eq '9992') { '999-22' } else { '999-3' })
                    }
                }
            }
            elseif ($layout.ContainsKey('nested')) {
                $targets = @($layout.nested)
            }
            else {
                $targets = @(@{ Parent = $layout.parent; Package = $layout.package; Case = $layout.case; Folder = $layout.folder })
            }
            foreach ($t in $targets) {
                $records += New-SpecificPackage -WorkRoot $workRoot -OutRoot $outRoot -Parent $t.Parent -Package $t.Package `
                    -Case $t.Case -Folder $t.Folder -Layers $layout.layers -InnerKind $layout.inner -MiddleKind 'single' `
                    -OuterKind 'single' -OuterFormat $layout.format -MiddleFormat $(if (($layout.n % 2) -eq 0) { 'rar5' } else { '7z' }) `
                    -PwOuter $layout.outerPassword -PwMiddle $layout.middlePassword -PwInner $layout.password `
                    -PayloadBytes 1600000 -Empty $false
            }
        }
        else {
            $plan = $SpecificNewLayouts[$Index - 11]
            $shape = $plan.shape
            $got = New-SpecificPackage -WorkRoot $workRoot -OutRoot $outRoot -Parent $plan.parent -Package $plan.package `
                -Case $plan.case -Folder $plan.folder -Layers $plan.layers -InnerKind $plan.inner -MiddleKind $plan.middle `
                -OuterKind $plan.outer -OuterFormat $plan.outerFormat -MiddleFormat $plan.middleFormat `
                -PwOuter $plan.pwOuter -PwMiddle $plan.pwMiddle -PwInner $plan.pwInner `
                -PayloadBytes $plan.payload -Empty $plan.empty
            $records += $got
            if ($plan.n -eq 20) {
                # 同一形状在两个父目录各放一份，验证落点各自跟随自己的源包父目录。
                $records += New-SpecificPackage -WorkRoot $workRoot -OutRoot $outRoot -Parent 'p20b' -Package 'n20b' `
                    -Case $plan.case -Folder 'n20b-0' -Layers $plan.layers -InnerKind $plan.inner -MiddleKind $plan.middle `
                    -OuterKind $plan.outer -OuterFormat $plan.outerFormat -MiddleFormat $plan.middleFormat `
                    -PwOuter $plan.pwOuter -PwMiddle $plan.pwMiddle -PwInner $plan.pwInner `
                    -PayloadBytes $plan.payload -Empty $plan.empty
            }
        }

        foreach ($r in $records) {
            foreach ($sf in $r.sourceFiles) {
                if (-not (Test-Path -LiteralPath (Join-Path $outRoot $sf))) { Fail "源包不存在：$sf" }
            }
        }
        $failed = ($Index -eq 18)
        $totalBytes = 0
        foreach ($r in $records) { $totalBytes += [int]$r.expectedContentBytes }
        $recipe = [ordered]@{
            schemaVersion = 2
            mode = 'Specific'
            seedBase = $SeedBase
            seed = ($SeedBase + 100000 + $Index)
            sampleNumber = $Index
            sampleId = $id
            category = 'Specific'
            placement = $placement
            shape = $shape
            expectedOutcome = $(if ($failed) { 'Failed' } else { 'Succeeded' })
            expectedReason = $(if ($failed) { 'WrongPassword' } else { '' })
            expectedSourceKeep = $true
            expectedProcessKeep = $false
            expectedProcessPaths = @()
            expectedFinalContentPath = $(if ($records.Count -eq 1 -and -not $failed) { $records[0].expectedOutputRelative } else { '' })
            expectedContentBytes = $(if ($failed) { 0 } else { $totalBytes })
            expectedGroupState = 'Complete'
            sourceRoot = $outRoot
            targets = $records
            validation = [ordered]@{
                targetCount = $records.Count
                maxLayers = 3
                headerEncryption = $false
                shapes = @($SpecificNewLayouts | ForEach-Object { $_.shape })
            }
        }
        Write-Recipe $sampleRoot $recipe
        Write-Log ("样本 {0} 完成：{1}，{2} 个包" -f $id, $shape, $records.Count)
    }
    finally {
        if (-not $KeepWork) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}


# ---------------------------------------------------------------- 异常 25

$LargestMedia = $null
if ($MediaFiles.Count -gt 0) { $LargestMedia = ($MediaFiles | Sort-Object Length -Descending | Select-Object -First 1).FullName }

function New-AbnormalContent {
    param([string]$Root, [string]$Name, [int]$Bytes)
    New-Item -ItemType Directory -Path (Join-Path $Root $Name) -Force | Out-Null
    if ($Bytes -gt $ContentBytes.Length) { $Bytes = $ContentBytes.Length }
    $slice = New-Object byte[] $Bytes
    [Array]::Copy($ContentBytes, 0, $slice, 0, $Bytes)
    [IO.File]::WriteAllBytes((Join-Path $Root ($Name + '\a.bin')), $slice)
    Set-Content -LiteralPath (Join-Path $Root ($Name + '\说明.txt')) -Value '异常样本内容' -Encoding utf8NoBOM
}

function New-ZipEntryArchive {
    param([string]$ArchivePath, [hashtable]$Entries, [switch]$StoreOnly)
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    if (Test-Path -LiteralPath $ArchivePath) { Remove-Item -LiteralPath $ArchivePath -Force }
    $stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in $Entries.Keys) {
                $level = if ($StoreOnly) { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
                $entry = $zip.CreateEntry([string]$name, $level)
                $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
                try { $writer.Write([string]$Entries[$name]) } finally { $writer.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

function New-ZipFromDirectory {
    param([string]$ArchivePath, [string]$WorkingDirectory)
    Invoke-Tool $SevenZip @('a', '-tzip', '-mx=0', '-y', $ArchivePath, '*') $WorkingDirectory | Out-Null
    if (-not (Test-Path -LiteralPath $ArchivePath)) { Fail "zip 没有生成：$ArchivePath" }
}

function New-ZeroFile {
    param([string]$Path, [long]$Bytes)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.SetLength($Bytes) } finally { $stream.Dispose() }
}

function New-AbnormalSevenZip {
    param([string]$ArchivePath, [string[]]$Inputs, [string]$WorkingDirectory)
    if (Test-Path -LiteralPath $ArchivePath) { Remove-Item -LiteralPath $ArchivePath -Force }
    Invoke-Tool $SevenZip (@('a', '-t7z', '-mx=0', '-y', $ArchivePath) + $Inputs) $WorkingDirectory | Out-Null
    if (-not (Test-Path -LiteralPath $ArchivePath)) { Fail "7z 没有生成：$ArchivePath" }
    Assert-SevenZipSignature (Split-Path -Parent $ArchivePath) (Split-Path -Leaf $ArchivePath)
    Assert-CreatedArchiveIntact $ArchivePath (Split-Path -Parent $ArchivePath) ''
}

function New-AbnormalVolume {
    # 旧脚本用 -v512k 切 1.2MB 内容，固定切出 3 片。
    param([string]$ArchiveBase, [string[]]$Inputs, [string]$WorkingDirectory)
    if (Test-Path -LiteralPath ($ArchiveBase + '.7z')) { Remove-Item -LiteralPath ($ArchiveBase + '.7z') -Force }
    Invoke-Tool $SevenZip (@('a', '-t7z', '-mx=0', '-y', '-v512k', ($ArchiveBase + '.7z')) + $Inputs) $WorkingDirectory | Out-Null
    $dir = Split-Path -Parent $ArchiveBase
    $leaf = Split-Path -Leaf $ArchiveBase
    $pieces = @(Get-ChildItem -LiteralPath $dir -Filter ($leaf + '.7z.*') | Sort-Object Name | ForEach-Object Name)
    if ($pieces.Count -lt 2) { Fail "分卷没有切出两片：$ArchiveBase" }
    return $pieces
}

function New-AbnormalRarVolume {
    param([string]$ArchiveBase, [string[]]$Inputs, [string]$WorkingDirectory)
    Invoke-Tool $Rar (@('a', '-ma5', '-m0', '-idq', '-y', '-r', '-v512k', ($ArchiveBase + '.rar')) + $Inputs) $WorkingDirectory | Out-Null
    $dir = Split-Path -Parent $ArchiveBase
    $leaf = Split-Path -Leaf $ArchiveBase
    return @(Get-ChildItem -LiteralPath $dir -Filter ($leaf + '.part*.rar') | Sort-Object Name | ForEach-Object Name)
}

function Generate-Abnormal([int]$Index) {
    $id = ($SeedBase + 200000 + $Index).ToString([Globalization.CultureInfo]::InvariantCulture)
    $sampleRoot = New-SampleRoot $id
    $outRoot = Join-Path $sampleRoot '原包'
    $workRoot = Join-Path $sampleRoot '.build'
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
    try {
        $kind = ''
        $outcome = '失败/跳过'
        $sourceRelative = ''
        $payload = Join-Path $workRoot 'payload'
        New-AbnormalContent -Root $payload -Name ('content-' + $Index) -Bytes 1200000

        switch ($Index) {
            1 {
                $kind = '缺失最后一卷'
                $pieces = @(New-AbnormalVolume -ArchiveBase (Join-Path $workRoot 'missing') -Inputs @('*') -WorkingDirectory $payload)
                foreach ($p in $pieces) { Copy-Item -LiteralPath (Join-Path $workRoot $p) -Destination (Join-Path $outRoot $p) -Force }
                $sourceRelative = 'missing.7z.001'
                Remove-Item -LiteralPath (Join-Path $outRoot $pieces[$pieces.Count - 1]) -Force
                $outcome = '失败/跳过'
            }
            2 {
                $kind = '中间卷损坏'
                $pieces = @(New-AbnormalVolume -ArchiveBase (Join-Path $workRoot 'corrupt') -Inputs @('*') -WorkingDirectory $payload)
                foreach ($p in $pieces) { Copy-Item -LiteralPath (Join-Path $workRoot $p) -Destination (Join-Path $outRoot $p) -Force }
                $sourceRelative = $pieces[0]
                $bad = Join-Path $outRoot $pieces[1]
                $len = (Get-Item -LiteralPath $bad).Length
                $fs = [IO.File]::Open($bad, [IO.FileMode]::Open, [IO.FileAccess]::Write)
                try { $fs.SetLength([int]($len / 2)) } finally { $fs.Dispose() }
                $outcome = '失败/损坏'
            }
            3 {
                $kind = '空归档'
                $empty = Join-Path $outRoot 'empty.7z'
                Invoke-Tool $SevenZip @('a', '-t7z', '-mx=0', '-y', $empty, '-xr!*') $workRoot | Out-Null
                $sourceRelative = 'empty.7z'
                $outcome = '失败/跳过'
            }
            4 {
                $kind = '跨盘'
                $archive = Join-Path $workRoot 'cross.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $payload
                Copy-Item -LiteralPath $archive -Destination (Join-Path $outRoot 'cross.7z') -Force
                $sourceRelative = 'cross.7z'
                $outcome = '成功'
            }
            5 {
                $kind = '长路径/中文'
                $longDir = Join-Path $outRoot ('长路径-' + ((1..4 | ForEach-Object { '子目录-' }) -join '') + [Guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $longDir -Force | Out-Null
                $archive = Join-Path $workRoot 'long.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $payload
                Copy-Item -LiteralPath $archive -Destination (Join-Path $longDir '长名字包.7z') -Force
                $sourceRelative = Join-Path (Split-Path -Leaf $longDir) '长名字包.7z'
                $outcome = '成功'
            }
            6 {
                $kind = '同基名不同目录'
                foreach ($sub in @('A', 'B')) {
                    $dir = Join-Path $outRoot $sub
                    New-Item -ItemType Directory -Path $dir -Force | Out-Null
                    $archive = Join-Path $workRoot ('dup-' + $sub + '.7z')
                    New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $payload
                    Copy-Item -LiteralPath $archive -Destination (Join-Path $dir 'dup.7z') -Force
                }
                $sourceRelative = 'A\dup.7z;B\dup.7z'
                $outcome = '成功两个任务'
            }
            7 {
                $kind = '硬链接双名'
                $archive = Join-Path $workRoot 'hard.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $payload
                $first = Join-Path $outRoot 'hard.7z.001'
                Copy-Item -LiteralPath $archive -Destination $first -Force
                New-Item -ItemType HardLink -Path (Join-Path $outRoot 'hard.7z.001删') -Target $first | Out-Null
                $sourceRelative = 'hard.7z.001'
                $outcome = '原包留下/只留原名'
            }
            8 {
                $kind = '只有媒体前缀'
                if ($null -eq $LargestMedia) { Fail '媒体目录为空' }
                Copy-Item -LiteralPath $LargestMedia -Destination (Join-Path $outRoot 'media-only.mp4') -Force
                $sourceRelative = 'media-only.mp4'
                $outcome = '跳过/失败'
            }
            9 {
                $kind = '伪分卷标记'
                $archive = Join-Path $workRoot 'fake.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $payload
                Copy-Item -LiteralPath $archive -Destination (Join-Path $outRoot 'fake.7z.001') -Force
                $sourceRelative = 'fake.7z.001'
                $outcome = '不强行组卷'
            }
            10 {
                $kind = '重复导入'
                $archive = Join-Path $workRoot 'dup-import.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $payload
                Copy-Item -LiteralPath $archive -Destination (Join-Path $outRoot 'dup-import.7z') -Force
                $sourceRelative = 'dup-import.7z'
                $outcome = '去重或两个独立任务'
            }
            11 {
                $kind = '路径穿越'
                $archive = Join-Path $outRoot 'traversal.zip'
                New-ZipEntryArchive -ArchivePath $archive -Entries @{
                    '../逃逸.txt' = '越界内容'
                    '/绝对.txt' = '绝对路径内容'
                    '..\..\逃逸2.txt' = '越界内容2'
                }
                $sourceRelative = 'traversal.zip'
                $outcome = '失败/跳过'
            }
            12 {
                $kind = 'Windows 保留名/结尾点空格'
                $archive = Join-Path $outRoot 'reserved.zip'
                New-ZipEntryArchive -ArchivePath $archive -Entries @{
                    'CON' = '保留设备名'
                    '尾点.' = '结尾点'
                    '尾空格 ' = '结尾空格'
                    'A:B.txt' = '备用数据流样式'
                }
                $sourceRelative = 'reserved.zip'
                $outcome = '成功/拒绝'
            }
            13 {
                $kind = 'ZIP 数据 CRC 损坏'
                $archive = Join-Path $outRoot 'bad-crc.zip'
                New-ZipEntryArchive -ArchivePath $archive -Entries @{ '坏数据.txt' = 'CRC 应当对不上' } -StoreOnly
                $bytes = [IO.File]::ReadAllBytes($archive)
                $dataOffset = 30 + [BitConverter]::ToUInt16($bytes, 26) + [BitConverter]::ToUInt16($bytes, 28)
                if ($dataOffset -ge $bytes.Length) { Fail "CRC 样本数据偏移越界：$archive" }
                $bytes[$dataOffset] = $bytes[$dataOffset] -bxor 0xFF
                [IO.File]::WriteAllBytes($archive, $bytes)
                $sourceRelative = 'bad-crc.zip'
                $outcome = '失败/部分完成'
            }
            14 {
                $kind = '压缩炸弹'
                $bomb = Join-Path $workRoot 'bomb-source.bin'
                New-ZeroFile -Path $bomb -Bytes 64MB
                $archive = Join-Path $workRoot 'bomb.7z'
                if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
                Invoke-Tool $SevenZip @('a', '-t7z', '-mx=9', '-y', $archive, $bomb) $workRoot | Out-Null
                Copy-Item -LiteralPath $archive -Destination (Join-Path $outRoot 'bomb.7z') -Force
                Remove-Item -LiteralPath $bomb -Force -ErrorAction SilentlyContinue
                $sourceRelative = 'bomb.7z'
                $outcome = '拒绝解压'
            }
            15 {
                $kind = '内层分卷缺最后一卷'
                $pieces = @(New-AbnormalVolume -ArchiveBase (Join-Path $workRoot 'inner-missing') -Inputs @('*') -WorkingDirectory $payload)
                $innerDir = Join-Path $workRoot 'inner-missing-visible'
                New-Item -ItemType Directory -Path $innerDir -Force | Out-Null
                for ($i = 0; $i -lt ($pieces.Count - 1); $i++) {
                    Copy-Item -LiteralPath (Join-Path $workRoot $pieces[$i]) -Destination (Join-Path $innerDir $pieces[$i]) -Force
                }
                $archive = Join-Path $outRoot 'outer-inner-volume.zip'
                New-ZipFromDirectory -ArchivePath $archive -WorkingDirectory $innerDir
                $sourceRelative = 'outer-inner-volume.zip'
                $outcome = '部分完成/失败'
            }
            16 {
                $kind = '内层 docx 默认保护'
                $docx = Join-Path $workRoot '说明.docx'
                New-ZipEntryArchive -ArchivePath $docx -Entries @{
                    'word/document.xml' = '<document>docx</document>'
                    'docx内部内容.txt' = '不该被拆出来'
                }
                $outerDir = Join-Path $workRoot 'container-docx'
                New-Item -ItemType Directory -Path $outerDir -Force | Out-Null
                Copy-Item -LiteralPath $docx -Destination (Join-Path $outerDir '说明.docx') -Force
                Set-Content -LiteralPath (Join-Path $outerDir '正文.txt') -Value '外层正文' -Encoding utf8NoBOM
                $archive = Join-Path $outRoot 'container-docx.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $outerDir
                $sourceRelative = 'container-docx.7z'
                $outcome = '成功'
            }
            17 {
                $kind = 'xlsx 保护与普通 zip 共存'
                $xlsx = Join-Path $workRoot '表格.xlsx'
                New-ZipEntryArchive -ArchivePath $xlsx -Entries @{
                    'xl/workbook.xml' = '<workbook/>'
                    'xlsx内部内容.txt' = '不该被拆出来'
                }
                $plain = Join-Path $workRoot '普通包.zip'
                New-ZipEntryArchive -ArchivePath $plain -Entries @{ '普通内容.txt' = '应该正常解出来' }
                $outerDir = Join-Path $workRoot 'container-xlsx'
                New-Item -ItemType Directory -Path $outerDir -Force | Out-Null
                Copy-Item -LiteralPath $xlsx -Destination (Join-Path $outerDir '表格.xlsx') -Force
                Copy-Item -LiteralPath $plain -Destination (Join-Path $outerDir '普通包.zip') -Force
                $archive = Join-Path $outRoot 'container-xlsx.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $outerDir
                $sourceRelative = 'container-xlsx.7z'
                $outcome = '成功'
            }
            18 {
                $kind = '深层嵌套 docx 保护'
                $deepDir = Join-Path $workRoot 'deep-docx-final'
                New-Item -ItemType Directory -Path $deepDir -Force | Out-Null
                $docx = Join-Path $deepDir '深层文档.docx'
                New-ZipEntryArchive -ArchivePath $docx -Entries @{
                    'word/document.xml' = '<document>deep</document>'
                    '深层内部内容.txt' = '不该被拆出来'
                }
                Set-Content -LiteralPath (Join-Path $deepDir '正文.txt') -Value '深层正文' -Encoding utf8NoBOM
                $layer1 = Join-Path $workRoot 'deep-layer1.zip'
                New-ZipFromDirectory -ArchivePath $layer1 -WorkingDirectory $deepDir
                $layer2Dir = Join-Path $workRoot 'deep-layer2'
                New-Item -ItemType Directory -Path $layer2Dir -Force | Out-Null
                Copy-Item -LiteralPath $layer1 -Destination (Join-Path $layer2Dir 'layer1.zip') -Force
                $layer2 = Join-Path $workRoot 'deep-layer2.7z'
                New-AbnormalSevenZip -ArchivePath $layer2 -Inputs @('*') -WorkingDirectory $layer2Dir
                $layer3Dir = Join-Path $workRoot 'deep-layer3'
                New-Item -ItemType Directory -Path $layer3Dir -Force | Out-Null
                Copy-Item -LiteralPath $layer2 -Destination (Join-Path $layer3Dir 'layer2.7z') -Force
                $archive = Join-Path $outRoot 'nested-docx.zip'
                New-ZipFromDirectory -ArchivePath $archive -WorkingDirectory $layer3Dir
                $sourceRelative = 'nested-docx.zip'
                $outcome = '成功'
            }
            19 {
                $kind = '同基名 docx 与普通 zip'
                $outerDir = Join-Path $workRoot 'same-base-docx'
                New-Item -ItemType Directory -Path $outerDir -Force | Out-Null
                $docx = Join-Path $outerDir '资料.docx'
                New-ZipEntryArchive -ArchivePath $docx -Entries @{
                    'word/document.xml' = '<document>same-base</document>'
                    'docx内部内容.txt' = '不该被拆出来'
                }
                $plain = Join-Path $outerDir '资料.zip'
                New-ZipEntryArchive -ArchivePath $plain -Entries @{ '资料内容.txt' = '同基名 zip 应该正常解出来' }
                $archive = Join-Path $outRoot 'same-base-docx.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $outerDir
                $sourceRelative = 'same-base-docx.7z'
                $outcome = '成功'
            }
            20 {
                $kind = '源包 docx 手动只解压强拆'
                $docx = Join-Path $outRoot '手动源.docx'
                New-ZipEntryArchive -ArchivePath $docx -Entries @{ '手动内容.txt' = '手动只解压应该拆出来' }
                $sourceRelative = '手动源.docx'
                $outcome = '成功'
            }
            21 {
                $kind = 'ZIP EOCD 截断'
                $archive = Join-Path $outRoot 'eocd-cut.zip'
                New-ZipEntryArchive -ArchivePath $archive -Entries @{ '正常内容.txt' = 'EOCD 会被截掉' }
                $bytes = [IO.File]::ReadAllBytes($archive)
                if ($bytes.Length -le 30) { Fail 'EOCD 样本过短' }
                $keep = $bytes.Length - 22
                $cut = New-Object byte[] $keep
                [Array]::Copy($bytes, 0, $cut, 0, $keep)
                [IO.File]::WriteAllBytes($archive, $cut)
                $sourceRelative = 'eocd-cut.zip'
                $outcome = '失败/跳过'
            }
            22 {
                $kind = 'RAR 分卷头冲突'
                $a = Join-Path $workRoot 'rarA'
                $b = Join-Path $workRoot 'rarB'
                New-Item -ItemType Directory -Path $a -Force | Out-Null
                New-Item -ItemType Directory -Path $b -Force | Out-Null
                New-AbnormalContent -Root $a -Name 'pa' -Bytes 1200000
                New-AbnormalContent -Root $b -Name 'pb' -Bytes 1200000
                $pA = @(New-AbnormalRarVolume -ArchiveBase (Join-Path $workRoot 'conflict') -Inputs @('*') -WorkingDirectory $a)
                Invoke-Tool $Rar @('a', '-ma5', '-m0', '-idq', '-y', '-r', '-v512k', (Join-Path $workRoot 'conflictB.rar'), '*') $b | Out-Null
                $pB = @(Get-ChildItem -LiteralPath $workRoot -Filter 'conflictB.part*.rar' | Sort-Object Name | ForEach-Object FullName)
                if ($pB.Count -lt 2) { Fail '冲突样本第二组没有切出两片' }
                Copy-Item -LiteralPath (Join-Path $workRoot $pA[0]) -Destination (Join-Path $outRoot 'conflict.part1.rar') -Force
                Copy-Item -LiteralPath $pB[1] -Destination (Join-Path $outRoot 'conflict.part2.rar') -Force
                $sourceRelative = 'conflict.part1.rar'
                $outcome = '失败/跳过'
            }
            23 {
                $kind = '7z 起始头 CRC 损坏'
                $archive = Join-Path $workRoot 'startcrc.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $payload
                Copy-Item -LiteralPath $archive -Destination (Join-Path $outRoot 'startcrc.7z') -Force
                $bytes = [IO.File]::ReadAllBytes((Join-Path $outRoot 'startcrc.7z'))
                $bytes[8] = $bytes[8] -bxor 0xFF
                [IO.File]::WriteAllBytes((Join-Path $outRoot 'startcrc.7z'), $bytes)
                $sourceRelative = 'startcrc.7z'
                $outcome = '失败/跳过'
            }
            24 {
                $kind = 'ZIP 中央目录偏移越界'
                $archive = Join-Path $outRoot 'cd-offset.zip'
                New-ZipEntryArchive -ArchivePath $archive -Entries @{ '内容.txt' = '中央目录偏移被改坏' }
                $bytes = [IO.File]::ReadAllBytes($archive)
                $eocd = -1
                for ($i = $bytes.Length - 22; $i -ge 0; $i--) {
                    if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4B -and $bytes[$i + 2] -eq 0x05 -and $bytes[$i + 3] -eq 0x06) { $eocd = $i; break }
                }
                if ($eocd -lt 0) { Fail '找不到 EOCD' }
                $bytes[$eocd + 16] = 0xFF
                $bytes[$eocd + 17] = 0xFF
                $bytes[$eocd + 18] = 0xFF
                $bytes[$eocd + 19] = 0x7F
                [IO.File]::WriteAllBytes($archive, $bytes)
                $sourceRelative = 'cd-offset.zip'
                $outcome = '失败/跳过'
            }
            25 {
                $kind = '同基名不同族强锚点'
                $archive = Join-Path $workRoot 'anchor.7z'
                New-AbnormalSevenZip -ArchivePath $archive -Inputs @('*') -WorkingDirectory $payload
                Copy-Item -LiteralPath $archive -Destination (Join-Path $outRoot '同名.7z.001') -Force
                $zipSrc = Join-Path $workRoot 'anchor-src'
                New-Item -ItemType Directory -Path $zipSrc -Force | Out-Null
                Copy-Item -LiteralPath $archive -Destination (Join-Path $zipSrc '转存.bin') -Force
                New-ZipFromDirectory -ArchivePath (Join-Path $outRoot '同名.zip.001') -WorkingDirectory $zipSrc
                $sourceRelative = '同名.7z.001;同名.zip.001'
                # 两个各自完整的包，只是同基名：不混组 = 两边都能解，不是失败。
                $outcome = '成功两个任务'
            }
            default { Fail "未知异常序号：$Index" }
        }

        $recipe = [ordered]@{
            schemaVersion = 2
            mode = 'Extra'
            seedBase = $SeedBase
            seed = ($SeedBase + 200000 + $Index)
            sampleNumber = $Index
            sampleId = $id
            category = 'Abnormal'
            placement = 'Original'
            extraKind = $kind
            expectedOutcome = $(if ($outcome -like '失败*' -or $outcome -like '跳过*' -or $outcome -like '拒绝*') { 'FailedOrSkipped' } elseif ($outcome -like '成功*' -and $outcome -match '[/或]') { 'Either' } elseif ($outcome -like '成功*') { 'Succeeded' } else { 'Either' })
            expectedReason = $outcome
            expectedSourceKeep = $true
            expectedProcessKeep = $true
            expectedProcessPaths = @($sourceRelative)
            expectedFinalContentPath = ''
            expectedContentBytes = 0
            expectedGroupState = 'Unknown'
            sourceRelative = $sourceRelative
            expected = [ordered]@{ outcome = $outcome; sourceKeep = $true }
            noHeaderEncryption = $true
        }
        Write-Recipe $sampleRoot $recipe
        Write-Log ("样本 {0} 完成：{1}" -f $id, $kind)
    }
    finally {
        if (-not $KeepWork) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}


# ---------------------------------------------------------------- 空间 25

# C01-C15 的计划必须与旧矩阵脚本逐位一致：旧脚本用一个 [Random]::new(913) 顺序消费 15 批，
# 所以这里先整体重放一遍（只算随机数、不落盘），再让单份生成取自己那一片。
$SpaceSizes = @(0.3, 0.4, 0.6, 0.8, 1.0, 1.2, 1.5)
$SpaceCTypes = @('ample', 'largest', 'allblocked', 'corrupt', 'ample', 'largest', 'allblocked', 'corrupt', 'ample', 'largest', 'allblocked', 'corrupt', 'ample', 'largest', 'allblocked')
$SpaceCCounts = @(5, 6, 5, 6, 5, 6, 5, 6, 5, 6, 5, 6, 5, 6, 6)
$SpaceBKinds = @('direct', 'two-layer', 'three-layer', 'last-folder', 'volume', 'same-base')
$SpaceBCounts = @(5, 6, 5, 6, 5, 5, 6, 5, 6, 5)
$SpaceBTypes = @('ample', 'largest', 'allblocked', 'corrupt', 'ample', 'largest', 'allblocked', 'corrupt', 'ample', 'largest')
$SpaceReserveBytes = 64 * 1024

function Get-SpaceCPlan {
    $rng = New-Object System.Random(913)
    $plan = New-Object System.Collections.Generic.List[object]
    for ($ci = 1; $ci -le 15; $ci++) {
        $type = $SpaceCTypes[$ci - 1]
        $count = $SpaceCCounts[$ci - 1]
        $items = New-Object System.Collections.Generic.List[object]
        for ($n = 1; $n -le $count; $n++) {
            $mb = $SpaceSizes[$rng.Next($SpaceSizes.Count)]
            $corrupt = ($type -eq 'corrupt' -and $n -eq 1)
            $format = if ($corrupt) { '7z' } elseif ($rng.Next(100) -lt 60) { '7z' } else { 'zip' }
            $nested = (-not $corrupt) -and ($rng.Next(100) -lt 25)
            $items.Add([ordered]@{ mb = $mb; corrupt = $corrupt; format = $format; nested = $nested })
        }
        $plan.Add([ordered]@{ caseId = ('C' + $ci.ToString('D2')); spaceType = $type; count = $count; items = $items })
    }
    return , $plan
}

$SpaceCPlan = Get-SpaceCPlan

function New-SpaceRandomFile {
    param([string]$Path, [long]$Bytes)
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    $header = [Text.Encoding]::ASCII.GetBytes("AFTEST-CONTENT`n")
    [Array]::Copy($header, $buffer, [Math]::Min($header.Length, $buffer.Length))
    [IO.File]::WriteAllBytes($Path, $buffer)
}

function Get-SpaceFreeBytes([string]$Type, [long]$SumSource, $MaxPackage) {
    switch ($Type) {
        'ample' { return [long]$SumSource + (8 * 1024 * 1024) }
        'largest' { return [long][Math]::Max(128 * 1024, ([double]$MaxPackage / 2)) }
        'allblocked' { return 64 * 1024 }
        'corrupt' { return [long]$SumSource + (8 * 1024 * 1024) }
        default { return [long]$SumSource + (8 * 1024 * 1024) }
    }
}

function Invoke-SpaceCorrupt([string]$Path) {
    $all = [IO.File]::ReadAllBytes($Path)
    $offset = [Math]::Min($all.Length - 64, [int]($all.Length * 0.55))
    if ($offset -lt 64) { $offset = [Math]::Max(0, [int]($all.Length / 2)) }
    if ($all.Length -gt 0) { $all[$offset] = $all[$offset] -bxor 0xFF }
    [IO.File]::WriteAllBytes($Path, $all)
}

function Write-SpaceCFile([int]$Index) {
    $plan = $SpaceCPlan[$Index - 1]
    $id = $plan.caseId
    $sampleRoot = New-SampleRoot $id
    $outRoot = Join-Path $sampleRoot '原包'
    $workRoot = Join-Path $sampleRoot '.build'
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
    try {
        $packages = New-Object System.Collections.Generic.List[object]
        $n = 0
        foreach ($item in $plan.items) {
            $n++
            $mb = $item.mb
            $corrupt = $item.corrupt
            $format = $item.format
            $nested = $item.nested
            $name = "pkg-$id-$($n.ToString('D2'))"
            $payload = Join-Path $workRoot 'payload.bin'
            New-SpaceRandomFile -Path $payload -Bytes ([long]($mb * 1000000))
            if ($nested) {
                $innerLeaf = @(New-Archive -Format '7z' -ArchiveBase 'inner' -Inputs @('payload.bin') -WorkDirectory $workRoot -PasswordValue '')[0]
                $topLeaf = @(New-Archive -Format $(if ($format -eq 'zip') { 'zip7z' } else { '7z' }) -ArchiveBase $name -Inputs @($innerLeaf) -WorkDirectory $workRoot -PasswordValue '')[0]
                Remove-Item -LiteralPath (Join-Path $workRoot $innerLeaf) -Force -ErrorAction SilentlyContinue
            }
            else {
                $topLeaf = @(New-Archive -Format $(if ($format -eq 'zip') { 'zip7z' } else { '7z' }) -ArchiveBase $name -Inputs @('payload.bin') -WorkDirectory $workRoot -PasswordValue '')[0]
            }
            if ($corrupt) { Invoke-SpaceCorrupt (Join-Path $workRoot $topLeaf) }
            $sourcePath = Join-Path $outRoot $topLeaf
            Move-Item -LiteralPath (Join-Path $workRoot $topLeaf) -Destination $sourcePath -Force
            $packages.Add([ordered]@{
                fileName = $topLeaf
                sourceBytes = (Get-Item -LiteralPath $sourcePath).Length
                contentBytes = [long]($mb * 1000000)
                format = $format
                shape = $(if ($nested) { 'nested' } else { 'single' })
                corrupt = $corrupt
                expected = $(if ($corrupt) { 'Failed' } else { 'Succeeded' })
            })
            Remove-Item -LiteralPath $payload -Force -ErrorAction SilentlyContinue
        }

        $sumSource = 0
        foreach ($p in $packages) { $sumSource += [long]$p.sourceBytes }
        $maxBytes = 0
        foreach ($p in $packages) { if ([long]$p.sourceBytes -gt $maxBytes) { $maxBytes = [long]$p.sourceBytes } }
        $free = Get-SpaceFreeBytes $plan.spaceType $sumSource $maxBytes
        $blocked = @()
        if ($plan.spaceType -eq 'largest') {
            $maxPkg = $packages | Sort-Object { [long]$_.sourceBytes } -Descending | Select-Object -First 1
            $blocked = @($maxPkg.fileName)
        }
        if ($plan.spaceType -eq 'allblocked') { $blocked = @($packages | ForEach-Object { $_.fileName }) }
        $failed = @($packages | Where-Object { $_.corrupt } | ForEach-Object { $_.fileName })

        $recipe = [ordered]@{
            schemaVersion = 3
            caseId = $id
            sampleId = $id
            category = 'SpaceTight'
            placement = 'Original'
            kind = 'NormalBatch'
            spaceType = $plan.spaceType
            packageCount = $packages.Count
            expectedTaskFileName = $packages[0].fileName
            expectedTaskFileNames = @($packages | ForEach-Object { $_.fileName })
            expectedOutcome = 'Mixed'
            expectedReason = ''
            expectedSourceKeep = $true
            expectedProcessKeep = $true
            expectedProcessPaths = @($packages | ForEach-Object { $_.fileName })
            expectedFinalContentPath = ''
            expectedContentBytes = [long](($packages | ForEach-Object { $_.contentBytes } | Measure-Object -Sum).Sum)
            expectedGroupState = 'Complete'
            packages = $packages.ToArray()
            space = [ordered]@{ freeBytes = $free; reserveBytes = $SpaceReserveBytes; concurrency = 2 }
            expected = [ordered]@{ blocked = $blocked; failed = $failed }
        }
        Write-Recipe $sampleRoot $recipe
        Write-Log ("样本 {0} 完成：{1}，{2} 个包" -f $id, $plan.spaceType, $packages.Count)
    }
    finally {
        if (-not $KeepWork) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Write-SpaceBFile([int]$Index) {
    $id = 'B' + $Index.ToString('D2')
    $count = $SpaceBCounts[$Index - 1]
    $type = $SpaceBTypes[$Index - 1]
    $sampleRoot = New-SampleRoot $id
    $outRoot = Join-Path $sampleRoot '原包'
    $workRoot = Join-Path $sampleRoot '.build'
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
    try {
        $packages = New-Object System.Collections.Generic.List[object]
        for ($n = 1; $n -le $count; $n++) {
            $kind = $SpaceBKinds[($n - 1 + $Index - 1) % $SpaceBKinds.Count]
            $corrupt = ($type -eq 'corrupt' -and $n -eq 1)
            if ($corrupt) { $kind = 'direct' }
            $pkgWork = Join-Path $workRoot ("pkg-$($n.ToString('D2'))")
            $content = Join-Path $pkgWork 'content'
            New-Item -ItemType Directory -Path $content -Force | Out-Null
            $finalNames = @()
            for ($f = 1; $f -le 5; $f++) {
                $finalName = "final-$($f.ToString('D2')).bin"
                New-SpaceRandomFile -Path (Join-Path $content $finalName) -Bytes 250000
                $finalNames += $finalName
            }
            if ($kind -eq 'last-folder') {
                $fold = Join-Path $content ('final-folder-' + $n.ToString('D2'))
                New-Item -ItemType Directory -Path $fold -Force | Out-Null
                foreach ($f in $finalNames) { Move-Item -LiteralPath (Join-Path $content $f) -Destination (Join-Path $fold $f) }
            }

            $innerName = "inner-$id-$($n.ToString('D2'))"
            $topFiles = @()
            switch ($kind) {
                'direct' {
                    $top = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork $innerName) -Inputs $finalNames -WorkDirectory $content -PasswordValue '')[0]
                    $topFiles = @($top)
                }
                'two-layer' {
                    $inner = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork $innerName) -Inputs $finalNames -WorkDirectory $content -PasswordValue '')[0]
                    $top = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork ("outer-$id-$($n.ToString('D2'))")) -Inputs @((Split-Path -Leaf $inner)) -WorkDirectory $pkgWork -PasswordValue '')[0]
                    $topFiles = @($top)
                }
                'three-layer' {
                    $inner = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork $innerName) -Inputs $finalNames -WorkDirectory $content -PasswordValue '')[0]
                    $middle = @(New-Archive -Format 'zip7z' -ArchiveBase (Join-Path $pkgWork ("middle-$id-$($n.ToString('D2'))")) -Inputs @((Split-Path -Leaf $inner)) -WorkDirectory $pkgWork -PasswordValue '')[0]
                    $top = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork ("outer-$id-$($n.ToString('D2'))")) -Inputs @((Split-Path -Leaf $middle)) -WorkDirectory $pkgWork -PasswordValue '')[0]
                    $topFiles = @($top)
                }
                'last-folder' {
                    $folderName = (Get-ChildItem -LiteralPath $content -Directory | Select-Object -First 1).Name
                    $inner = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork $innerName) -Inputs @($folderName) -WorkDirectory $content -PasswordValue '')[0]
                    $top = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork ("outer-$id-$($n.ToString('D2'))")) -Inputs @((Split-Path -Leaf $inner)) -WorkDirectory $pkgWork -PasswordValue '')[0]
                    $topFiles = @($top)
                }
                'volume' {
                    $topFiles = @(New-Archive -Format 'vol7z' -ArchiveBase $innerName -Inputs $finalNames -WorkDirectory $content -PasswordValue '' | ForEach-Object { Join-Path $content $_ })
                }
                'same-base' {
                    $inner = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork 'same-inner') -Inputs $finalNames -WorkDirectory $content -PasswordValue '')[0]
                    $top = @(New-Archive -Format '7z' -ArchiveBase (Join-Path $pkgWork ("same-$id-$($n.ToString('D2'))")) -Inputs @((Split-Path -Leaf $inner)) -WorkDirectory $pkgWork -PasswordValue '')[0]
                    $topFiles = @($top)
                }
            }
            if ($topFiles.Count -eq 0) { Fail "特定样本 $id/$n 没有顶层产物" }
            if ($corrupt) { Invoke-SpaceCorrupt $topFiles[0] }
            $sourceNames = @()
            $pkgBytes = 0
            foreach ($file in $topFiles) {
                $leaf = Split-Path -Leaf $file
                $dest = Join-Path $outRoot $leaf
                Move-Item -LiteralPath $file -Destination $dest -Force
                $sourceNames += $leaf
                $pkgBytes += (Get-Item -LiteralPath $dest).Length
            }
            $packages.Add([ordered]@{
                packageId = "$id-$($n.ToString('D2'))"
                shape = $kind
                sourceFiles = $sourceNames
                sourceBytes = [long]$pkgBytes
                contentFiles = 5
                corrupt = $corrupt
            })
        }

        $sumSource = 0
        foreach ($p in $packages) { $sumSource += [long]$p.sourceBytes }
        $maxBytes = 0
        foreach ($p in $packages) { if ([long]$p.sourceBytes -gt $maxBytes) { $maxBytes = [long]$p.sourceBytes } }
        $free = Get-SpaceFreeBytes $type $sumSource $maxBytes
        $blocked = @()
        if ($type -eq 'largest') {
            $maxPkg = $packages | Sort-Object { [long]$_.sourceBytes } -Descending | Select-Object -First 1
            $blocked = @($maxPkg.packageId)
        }
        if ($type -eq 'allblocked') { $blocked = @($packages | ForEach-Object { $_.packageId }) }
        $failed = @($packages | Where-Object { $_.corrupt } | ForEach-Object { $_.packageId })

        $recipe = [ordered]@{
            schemaVersion = 3
            caseId = $id
            sampleId = $id
            category = 'SpaceTight'
            placement = 'Original'
            kind = 'SpecificBatch'
            spaceType = $type
            packageCount = $packages.Count
            expectedTaskFileName = $packages[0].packageId
            expectedTaskFileNames = @($packages | ForEach-Object { $_.packageId })
            expectedOutcome = 'Mixed'
            expectedReason = ''
            expectedSourceKeep = $true
            expectedProcessKeep = $true
            expectedProcessPaths = @($packages | ForEach-Object { $_.sourceFiles } | ForEach-Object { $_ })
            expectedFinalContentPath = ''
            expectedContentBytes = [long]($packages.Count * 5 * 250000)
            expectedGroupState = 'Complete'
            packages = $packages.ToArray()
            space = [ordered]@{ freeBytes = $free; reserveBytes = $SpaceReserveBytes; concurrency = 2 }
            expected = [ordered]@{ blocked = $blocked; failed = $failed }
        }
        Write-Recipe $sampleRoot $recipe
        Write-Log ("样本 {0} 完成：{1}，{2} 个包" -f $id, $type, $packages.Count)
    }
    finally {
        if (-not $KeepWork) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Generate-Space([string]$Id) {
    if ($Id -match '^C(\d{2})$') { Write-SpaceCFile ([int]$Matches[1]); return }
    if ($Id -match '^B(\d{2})$') { Write-SpaceBFile ([int]$Matches[1]); return }
    Fail "未知空间样本：$Id"
}


# ---------------------------------------------------------------- 范围表

function Get-ScopeIds([string]$Scope) {
    $list = New-Object System.Collections.Generic.List[object]
    $inv = [Globalization.CultureInfo]::InvariantCulture
    if ($Scope -eq 'All' -or $Scope -eq 'Normal') {
        for ($i = 1; $i -le 50; $i++) {
            $list.Add([pscustomobject]@{ Section = 'Normal'; Index = $i; Id = ($SeedBase + $i).ToString($inv) })
        }
    }
    if ($Scope -eq 'All' -or $Scope -eq 'Specific') {
        for ($i = 1; $i -le 20; $i++) {
            $list.Add([pscustomobject]@{ Section = 'Specific'; Index = $i; Id = ($SeedBase + 100000 + $i).ToString($inv) })
        }
    }
    if ($Scope -eq 'All' -or $Scope -eq 'Abnormal') {
        for ($i = 1; $i -le 25; $i++) {
            $list.Add([pscustomobject]@{ Section = 'Abnormal'; Index = $i; Id = ($SeedBase + 200000 + $i).ToString($inv) })
        }
    }
    if ($Scope -eq 'All' -or $Scope -eq 'SpaceC') {
        for ($i = 1; $i -le 15; $i++) {
            $list.Add([pscustomobject]@{ Section = 'SpaceC'; Index = $i; Id = ('C' + $i.ToString('D2')) })
        }
    }
    if ($Scope -eq 'All' -or $Scope -eq 'SpaceB') {
        for ($i = 1; $i -le 10; $i++) {
            $list.Add([pscustomobject]@{ Section = 'SpaceB'; Index = $i; Id = ('B' + $i.ToString('D2')) })
        }
    }
    if ($list.Count -eq 0) { Fail "未知范围：$Scope" }
    return , $list
}

function Invoke-Sample($Item) {
    switch ($Item.Section) {
        'Normal' { Generate-Normal $Item.Index }
        'Specific' { Generate-Specific $Item.Index }
        'Abnormal' { Generate-Abnormal $Item.Index }
        'SpaceC' { Generate-Space $Item.Id }
        'SpaceB' { Generate-Space $Item.Id }
        default { Fail "未知类别：$($Item.Section)" }
    }
}

function Write-BatchIndex {
    # 清单一律按磁盘实际内容重建：跑单份不会把整批清单冲成一条。
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($d in (Get-ChildItem -LiteralPath $batchRoot -Directory | Where-Object { $_.Name -ne '.logs' } | Sort-Object Name)) {
        $recipePath = Join-Path $d.FullName 'recipe.json'
        if (-not (Test-Path -LiteralPath $recipePath)) { continue }
        $r = Get-Content -LiteralPath $recipePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $kind = ''
        if ($null -ne $r.PSObject.Properties['kind']) { $kind = [string]$r.kind }
        $rows.Add([ordered]@{
            sampleId = [string]$r.sampleId
            category = [string]$r.category
            kind = $kind
            placement = [string]$r.placement
            recipe = $recipePath
        })
    }
    ConvertTo-Json -InputObject $rows.ToArray() -Depth 10 | Set-Content -LiteralPath (Join-Path $batchRoot '120-manifest.json') -Encoding utf8NoBOM
    return , $rows.ToArray()
}

function Write-BatchReport($Rows, $Failures) {
    $report = [ordered]@{
        seedBase = $SeedBase
        total = $Rows.Count
        normal = @($Rows | Where-Object { $_.category -eq 'Normal' }).Count
        specific = @($Rows | Where-Object { $_.category -eq 'Specific' }).Count
        abnormal = @($Rows | Where-Object { $_.category -eq 'Abnormal' }).Count
        spaceNormal = @($Rows | Where-Object { $_.category -eq 'SpaceTight' -and $_.kind -eq 'NormalBatch' }).Count
        spaceSpecific = @($Rows | Where-Object { $_.category -eq 'SpaceTight' -and $_.kind -eq 'SpecificBatch' }).Count
        failedCount = $Failures.Count
        failures = $Failures.ToArray()
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $batchRoot '120-report.json') -Encoding utf8NoBOM
    return , $report
}

# ---------------------------------------------------------------- 自检

function Invoke-SelfCheck {
    $errors = New-Object System.Collections.Generic.List[string]
    $all = Get-ScopeIds 'All'

    $ids = New-Object System.Collections.Generic.List[string]
    $counts = @{}
    foreach ($item in $all) {
        $ids.Add($item.Id)
        if ($counts.ContainsKey($item.Section)) { $counts[$item.Section] = $counts[$item.Section] + 1 }
        else { $counts[$item.Section] = 1 }
    }

    if ($all.Count -ne 120) { $errors.Add("总数不是 120：$($all.Count)") }
    if ($counts['Normal'] -ne 50) { $errors.Add("普通不是 50：$($counts['Normal'])") }
    if ($counts['Specific'] -ne 20) { $errors.Add("特定不是 20：$($counts['Specific'])") }
    if ($counts['Abnormal'] -ne 25) { $errors.Add("异常不是 25：$($counts['Abnormal'])") }
    if ($counts['SpaceC'] -ne 15) { $errors.Add("空间 C 不是 15：$($counts['SpaceC'])") }
    if ($counts['SpaceB'] -ne 10) { $errors.Add("空间 B 不是 10：$($counts['SpaceB'])") }

    $dup = @($ids | Group-Object | Where-Object { $_.Count -gt 1 })
    if ($dup.Count -gt 0) { $errors.Add("ID 重复：$($dup.Name -join ',')") }

    $normalOriginal = 0
    $normalSpecified = 0
    $success = 0
    $missing = 0
    $corrupted = 0
    for ($i = 1; $i -le 50; $i++) {
        if ($i -gt 40) {
            if ($i -le 45) { $missing++ } else { $corrupted++ }
            if ($FailurePlacements[$i - 41] -eq 'Original') { $normalOriginal++ } else { $normalSpecified++ }
        }
        else {
            $success++
            if ($Scenarios[($i - 1) % 8].placement -eq 'Original') { $normalOriginal++ } else { $normalSpecified++ }
        }
    }
    if ($normalOriginal -ne 25) { $errors.Add("普通 Original 不是 25：$normalOriginal") }
    if ($normalSpecified -ne 25) { $errors.Add("普通 Specified 不是 25：$normalSpecified") }
    if ($success -ne 40) { $errors.Add("普通成功不是 40：$success") }
    if ($missing -ne 5) { $errors.Add("普通缺片不是 5：$missing") }
    if ($corrupted -ne 5) { $errors.Add("普通坏片不是 5：$corrupted") }

    foreach ($f in @($ContentRoot, $MediaRoot)) {
        if (-not (Test-Path -LiteralPath $f)) { $errors.Add("原物目录不存在：$f") }
    }
    foreach ($t in @($SevenZip, $Rar, $WinRAR)) {
        if (-not (Test-Path -LiteralPath $t)) { $errors.Add("工具不存在：$t") }
    }

    if ($errors.Count -gt 0) {
        foreach ($e in $errors) { Write-Host "SELFCHECK-FAIL $e" }
        throw "自检失败：$($errors.Count) 项"
    }
    Write-Host ("SelfCheck OK: Normal=50(40/5/5, 25/25) Specific=20 Abnormal=25 Space=15+10 total=120 seedBase={0}" -f $SeedBase)
}

# ---------------------------------------------------------------- 启动

if ($Mode -eq 'SelfCheck') { Invoke-SelfCheck; return }

foreach ($tool in @($SevenZip, $Rar, $WinRAR)) {
    if (-not (Test-Path -LiteralPath $tool)) { Fail "工具不存在：$tool" }
}
if ($Mode -eq 'Single' -and $Only -lt 1) { Fail '-Mode Single 需要 -Only N' }
if ($Section -eq 'All' -and $Only -gt 0) { Fail '-Only 需要配合具体 -Section 使用' }

$scope = Get-ScopeIds $Section
if ($Only -gt 0) {
    if ($Only -gt $scope.Count) { Fail "-Only 超出范围：$Section 只有 $($scope.Count) 份" }
    $picked = New-Object System.Collections.Generic.List[object]
    $picked.Add($scope[$Only - 1])
    $scope = $picked
}

Assert-BatchRoot $batchRoot
New-Item -ItemType Directory -Path $batchRoot -Force | Out-Null
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $batchRoot '.120-generated') -Value $seedText -Encoding utf8NoBOM

Remove-TargetSamples $batchRoot $scope.ToArray() $Force.IsPresent

Write-Log ("范围 {0}{1}：{2} 份 → {3}" -f $Section, $(if ($Only -gt 0) { " 第 $Only 份" } else { '' }), $scope.Count, $batchRoot)

$failures = New-Object System.Collections.Generic.List[object]
foreach ($item in $scope) {
    try {
        Invoke-Sample $item
    }
    catch {
        $failures.Add([ordered]@{ sampleId = $item.Id; section = $item.Section; error = $_.Exception.Message })
        Write-Log ("样本 {0} 失败：{1}" -f $item.Id, $_.Exception.Message) 'ERROR'
        Remove-Item -LiteralPath (Join-Path $batchRoot $item.Id) -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$rows = Write-BatchIndex
$report = Write-BatchReport $rows $failures

if ($Section -eq 'All' -and $Only -eq 0 -and $report.total -ne 120) {
    Fail "120 自检失败：实际 $($report.total)"
}
if ($failures.Count -gt 0) {
    Write-Log ("结束：磁盘 {0} 份，失败 {1}：{2}" -f $rows.Count, $failures.Count, (($failures | ForEach-Object { $_.sampleId }) -join ',')) 'ERROR'
    exit 1
}
Write-Log ("{0} 完成：{1}（磁盘 {2} 份）" -f $Section, $batchRoot, $report.total)
