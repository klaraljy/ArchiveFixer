#Requires -Version 5.1
<#
.SYNOPSIS
    生成「地狱」样本：非均匀分卷 + 加密头 + 三种族。

.DESCRIPTION
    - 输出目录默认 samples/generated/地狱（样本本体不入库）。
    - 每次运行都重新随机：载荷大小、分卷切点/前几卷大小、卷数。
    - 7z：真实 7z 非均匀分卷（先建完整 7z，再按随机切点原样切；7z 格式允许）。
    - RAR/ZIP：用 WinRAR 的多个 -v 造“首卷随机小、后续随机大小、末卷收尾”的非均匀卷。
    - 7z 用 -mhe=on 造头加密；RAR 用 -hp 造头加密；ZIP 没有同意义的头加密，
      用 -p 造条目加密，并在 MANIFEST 里如实写“条目加密，不是头加密”。
    - 密码是合成密码；不打印到屏幕。
#>
[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$SevenZip,
    [string]$WinRar = 'C:\Program Files\WinRAR\WinRAR.exe',
    [int]$MinPayloadMb = 8,
    [int]$MaxPayloadMb = 24,
    [string]$SamplePassword = 'HellPass123!',
    [int]$Seed = -1
)

$ErrorActionPreference = 'Stop'

if (-not $OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot 'generated\地狱' }
if (-not $SevenZip)   { $SevenZip   = Join-Path $PSScriptRoot '..\src\ArchiveFixer\tools\7zip\7z.exe' }

$SevenZip = (Resolve-Path -LiteralPath $SevenZip).Path
if (-not (Test-Path -LiteralPath $WinRar)) {
    throw "找不到 WinRAR：$WinRar（RAR/ZIP 非均匀卷必须用 WinRAR 的多个 -v 生成）"
}

if ($Seed -lt 0) { $Seed = Get-Random -Minimum 1 -Maximum 2147483647 }
$rng = New-Object System.Random($Seed)

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$manifest = New-Object System.Collections.Generic.List[string]
$manifest.Add('地狱样本 MANIFEST') | Out-Null
$manifest.Add('Seed = ' + $Seed) | Out-Null
$manifest.Add('密码 = 合成密码，见 generate-hell.ps1；不写入本清单') | Out-Null
$manifest.Add('') | Out-Null

function New-Dir([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    return $Path
}

function Invoke-Tool {
    param([string]$Exe, [string[]]$Arguments, [string]$What)

    & $Exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "失败（$What），退出码 $LASTEXITCODE：$Exe $($Arguments -join ' ')"
    }
}

function New-RandomPayload([string]$Root) {
    $sizeMb = $rng.Next($MinPayloadMb, $MaxPayloadMb + 1)
    $fileCount = $rng.Next(2, 5)
    $payload = New-Dir $Root
    $made = New-Object System.Collections.Generic.List[long]

    for ($i = 1; $i -le $fileCount; $i++) {
        $oneMb = [Math]::Max(1, [int]($sizeMb / $fileCount))
        $bytes = New-Object byte[] ($oneMb * 1MB)
        $rng.NextBytes($bytes)
        $name = if ($i -eq 1) { 'photo-01.jpg' } elseif ($i -eq 2) { 'video-01.mp4' } else { "data-$i.bin" }
        [System.IO.File]::WriteAllBytes((Join-Path $payload $name), $bytes)
        $made.Add($bytes.Length) | Out-Null
    }

    return [pscustomobject]@{ Root = $payload; Bytes = ($made | Measure-Object -Sum).Sum }
}

function New-RandomCuts([long]$Length, [int]$MinPieces, [int]$MaxPieces) {
    $maxPieces = [Math]::Min($MaxPieces, [Math]::Floor($Length / 96KB))
    if ($maxPieces -lt 2) { return @() }

    $count = $rng.Next([Math]::Max(2, $MinPieces), $maxPieces + 1)
    $cuts = New-Object System.Collections.Generic.List[long]
    $min = 64KB
    $max = $Length - 64KB

    if ($max -le $min) { return @() }

    for ($i = 1; $i -lt $count; $i++) {
        $cuts.Add($rng.Next([int]$min, [int]$max)) | Out-Null
    }

    $sorted = $cuts | Sort-Object -Unique
    return @($sorted)
}

function Write-7zNonUniform {
    param(
        [string]$WorkRoot,
        [string]$Payload,
        [string]$Destination,
        [bool]$EncryptedHeader
    )

    $full = Join-Path $WorkRoot ('full-' + [Guid]::NewGuid().ToString('N') + '.7z')
    $args = @('a', '-t7z', '-mx=1')
    if ($EncryptedHeader) {
        $args += ('-p' + $SamplePassword)
        $args += '-mhe=on'
    }
    $args += $full
    $args += $Payload

    Invoke-Tool -Exe $SevenZip -Arguments $args -What '7z 非均匀源包'

    $bytes = [System.IO.File]::ReadAllBytes($full)
    $cuts = New-RandomCuts -Length $bytes.LongLength -MinPieces 2 -MaxPieces 5
    if ($cuts.Count -eq 0) {
        throw "7z 源包太小，随机切不出两卷：$($bytes.Length) 字节"
    }

    $dest = New-Dir $Destination
    $points = @(0) + @($cuts) + @($bytes.LongLength)
    for ($i = 0; $i -lt $points.Count - 1; $i++) {
        $start = [int]$points[$i]
        $end = [int]($points[$i + 1] - 1)
        $len = $end - $start + 1
        if ($len -le 0) { continue }

        $piece = New-Object byte[] $len
        [Array]::Copy($bytes, $start, $piece, 0, $len)
        $name = 'hell-7z.7z.' + (($i + 1).ToString('D3'))
        [System.IO.File]::WriteAllBytes((Join-Path $dest $name), $piece)
    }

    Remove-Item -LiteralPath $full -Force
    return [pscustomobject]@{
        Kind = if ($EncryptedHeader) { '7z 非均匀 + 头加密' } else { '7z 非均匀' }
        Directory = $dest
        Sizes = @(Get-ChildItem -LiteralPath $dest -File | Sort-Object Name | ForEach-Object { $_.Length })
    }
}

function Write-WinRarNonUniform {
    param(
        [string]$WorkRoot,
        [string]$Payload,
        [string]$Destination,
        [string]$Format,
        [bool]$HeaderEncrypted
    )

    $isRar = $Format -eq 'rar'
    $extension = if ($isRar) { '.rar' } else { '.zip' }
    $single = Join-Path $WorkRoot ('single-' + [Guid]::NewGuid().ToString('N') + $extension)
    $args = @('a', '-ibck', '-inul', '-r', '-ep1')

    if ($isRar) {
        if ($HeaderEncrypted) { $args += ('-hp' + $SamplePassword) }
    } else {
        $args += '-afzip'
        $args += ('-p' + $SamplePassword)
    }

    $args += $single
    $args += $Payload
    Invoke-Tool -Exe $WinRar -Arguments $args -What "$Format 单包（用来算随机切点）"

    $length = (Get-Item -LiteralPath $single).Length
    $cuts = New-RandomCuts -Length $length -MinPieces 2 -MaxPieces 5
    if ($cuts.Count -eq 0) {
        Remove-Item -LiteralPath $single -Force
        throw "$Format 源包太小，随机切不出两卷：$length 字节"
    }

    # 先用前 N-1 个随机大小；最后一卷给足够大的上限，保证只剩一个末卷。
    $volumeArgs = @()
    $points = @(0) + @($cuts)
    for ($i = 0; $i -lt $cuts.Count; $i++) {
        $size = [long]$points[$i + 1] - [long]$points[$i]
        $kb = [Math]::Max(64, [int]($size / 1KB))
        $volumeArgs += ('-v' + $kb + 'k')
    }
    $volumeArgs += '-v4096m'

    $dest = New-Dir $Destination
    $base = Join-Path $dest ('hell-' + $Format)
    $args = @('a', '-ibck', '-inul', '-r', '-ep1')
    if ($isRar) {
        if ($HeaderEncrypted) { $args += ('-hp' + $SamplePassword) }
    } else {
        $args += '-afzip'
        $args += ('-p' + $SamplePassword)
    }
    $args += $volumeArgs
    $args += ($base + $extension)
    $args += $Payload
    Invoke-Tool -Exe $WinRar -Arguments $args -What "$Format 非均匀分卷"

    Remove-Item -LiteralPath $single -Force
    return [pscustomobject]@{
        Kind = if ($isRar) {
            if ($HeaderEncrypted) { 'RAR 非均匀 + 头加密' } else { 'RAR 非均匀' }
        } else {
            'ZIP 非均匀 + 条目加密（无头加密）'
        }
        Directory = $dest
        Sizes = @(Get-ChildItem -LiteralPath $dest -File | Sort-Object Name | ForEach-Object { $_.Length })
    }
}

$payloadInfo = New-RandomPayload (Join-Path $OutputRoot '_payload')
$payload = $payloadInfo.Root

$cases = New-Object System.Collections.Generic.List[object]

try {
    $cases.Add((Write-7zNonUniform -WorkRoot $OutputRoot -Payload $payload -Destination (Join-Path $OutputRoot '01_7z_非均匀_明文') -EncryptedHeader $false)) | Out-Null
    $cases.Add((Write-7zNonUniform -WorkRoot $OutputRoot -Payload $payload -Destination (Join-Path $OutputRoot '02_7z_非均匀_头加密') -EncryptedHeader $true)) | Out-Null
    $cases.Add((Write-WinRarNonUniform -WorkRoot $OutputRoot -Payload $payload -Destination (Join-Path $OutputRoot '03_rar_非均匀_明文') -Format 'rar' -HeaderEncrypted $false)) | Out-Null
    $cases.Add((Write-WinRarNonUniform -WorkRoot $OutputRoot -Payload $payload -Destination (Join-Path $OutputRoot '04_rar_非均匀_头加密') -Format 'rar' -HeaderEncrypted $true)) | Out-Null
    $cases.Add((Write-WinRarNonUniform -WorkRoot $OutputRoot -Payload $payload -Destination (Join-Path $OutputRoot '05_zip_非均匀_条目加密') -Format 'zip' -HeaderEncrypted $false)) | Out-Null
}
finally {
    if (Test-Path -LiteralPath $payload) {
        Remove-Item -LiteralPath $payload -Recurse -Force
    }
}

$manifest.Add('载荷约 ' + $payloadInfo.Bytes + ' 字节；Seed = ' + $Seed) | Out-Null
$manifest.Add('') | Out-Null

foreach ($case in $cases) {
    $line = $case.Kind + '：' + (($case.Sizes | ForEach-Object { $_ }) -join ' / ')
    $manifest.Add($line) | Out-Null
    $manifest.Add('  目录：' + $case.Directory) | Out-Null
}

[System.IO.File]::WriteAllLines((Join-Path $OutputRoot 'MANIFEST.txt'), $manifest, (New-Object System.Text.UTF8Encoding($false)))

Write-Host '地狱样本生成完毕：'
$cases | ForEach-Object { Write-Host ('  ' + $_.Kind + ' → ' + (($_.Sizes | ForEach-Object { $_ }) -join ' / ')) }
Write-Host ('目录：' + $OutputRoot)
Write-Host ('Seed：' + $Seed)
