param(
    [string]$SpaceRoot = 'H:\BaiduNetdiskDownload\困难大测试',
    [string]$SevenZip = 'E:\ArchiveFixer\tools\7zip\7z.exe',
    [int]$NormalBase = 910300000,
    [int]$SpecificBase = 910400000,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$normalBase = $NormalBase
$specificBase = $SpecificBase
$normalCount = 15
$specificCount = 5
$reserveBytes = 64 * 1024

$normalRoot = Join-Path $SpaceRoot ("大测试\$normalBase")
$specificRoot = Join-Path $SpaceRoot ("大测试\$specificBase")
$normalBackup = Join-Path $SpaceRoot ("大测试备份位置\$normalBase-空间不足-备份")
$specificBackup = Join-Path $SpaceRoot ("大测试备份位置\$specificBase-空间不足-特定解压-备份")
$normalLog = Join-Path $SpaceRoot ("大测试日志\大测试日志-$normalBase-空间不足")
$specificLog = Join-Path $SpaceRoot ("大测试日志\大测试日志-$specificBase-空间不足-特定解压")

if (-not (Test-Path -LiteralPath $SevenZip)) { throw "7z 不存在：$SevenZip" }
if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '找不到 dotnet' }

function Assert-SafeRoot {
    param([string]$Root, [string]$Seed)
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $marker = Join-Path $Root '.space-generated'
    if (-not (Test-Path -LiteralPath $marker)) { throw "拒绝覆盖：$Root 没有空间不足造包标记" }
    if (((Get-Content -LiteralPath $marker -Raw).Trim()) -ne $Seed) { throw "拒绝覆盖：标记不属于 $Seed" }
    $unknown = Get-ChildItem -LiteralPath $Root -Force | Where-Object {
        $_.Name -notmatch '^\d{9}$' -and
        $_.Name -ne '.space-generated' -and
        $_.Name -notmatch '^(空间计划|校验结果|造包清单)\.json$'
    }
    if ($unknown) { throw "拒绝覆盖：$Root 有未知内容：$($unknown.Name -join ', ')" }
}

$normalSeedText = [string]$normalBase
$specificSeedText = [string]$specificBase

foreach ($pair in @(
    @($normalRoot, $normalSeedText),
    @($specificRoot, $specificSeedText),
    @($normalBackup, $normalSeedText + '-空间不足-备份'),
    @($specificBackup, $specificSeedText + '-空间不足-特定解压-备份'),
    @($normalLog, $normalSeedText + '-空间不足'),
    @($specificLog, $specificSeedText + '-空间不足-特定解压')
)) {
    if (Test-Path -LiteralPath $pair[0]) {
        if (-not $Force) { throw "批次已存在：$($pair[0])（确认覆盖请加 -Force）" }
        if ($pair[1] -match '^\d{9}') { Assert-SafeRoot -Root $pair[0] -Seed $pair[1] }
    }
}

foreach ($dir in @($normalRoot, $specificRoot, $normalBackup, $specificBackup, $normalLog, $specificLog)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Set-Content -LiteralPath (Join-Path $normalRoot '.space-generated') -Value $normalSeedText -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $specificRoot '.space-generated') -Value $specificSeedText -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $normalBackup '.space-generated') -Value ($normalSeedText + '-空间不足-备份') -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $specificBackup '.space-generated') -Value ($specificSeedText + '-空间不足-特定解压-备份') -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $normalLog '.space-generated') -Value ($normalSeedText + '-空间不足') -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $specificLog '.space-generated') -Value ($specificSeedText + '-空间不足-特定解压') -Encoding utf8NoBOM

$normalGroups = @(
    [ordered]@{ id = 'N1'; freeBytes = 2200000; samples = @(
        [ordered]@{ mb = 2.0; format = '7z'; corrupt = $false },
        [ordered]@{ mb = 1.0; format = '7z'; corrupt = $false },
        [ordered]@{ mb = 0.3; format = 'zip'; corrupt = $false }
    )},
    [ordered]@{ id = 'N2'; freeBytes = 3800000; samples = @(
        [ordered]@{ mb = 2.0; format = '7z'; corrupt = $false },
        [ordered]@{ mb = 1.5; format = 'zip'; corrupt = $false },
        [ordered]@{ mb = 0.3; format = '7z'; corrupt = $false }
    )},
    [ordered]@{ id = 'N3'; freeBytes = 1200000; samples = @(
        [ordered]@{ mb = 2.0; format = '7z'; corrupt = $false },
        [ordered]@{ mb = 1.0; format = 'zip'; corrupt = $false },
        [ordered]@{ mb = 0.3; format = '7z'; corrupt = $false }
    )},
    [ordered]@{ id = 'N4'; freeBytes = 200000; samples = @(
        [ordered]@{ mb = 2.0; format = '7z'; corrupt = $false },
        [ordered]@{ mb = 1.5; format = 'zip'; corrupt = $false },
        [ordered]@{ mb = 1.0; format = '7z'; corrupt = $false }
    )},
    [ordered]@{ id = 'N5'; freeBytes = 1600000; samples = @(
        [ordered]@{ mb = 1.5; format = '7z'; corrupt = $false },
        [ordered]@{ mb = 1.0; format = 'zip'; corrupt = $false },
        [ordered]@{ mb = 0.6; format = '7z'; corrupt = $true }
    )}
)

$specificSizes = @(1.8, 1.4, 1.0, 0.6, 0.2)

function New-RandomContent {
    param([string]$Path, [long]$Bytes)
    $buffer = [byte[]]::new($Bytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    [IO.File]::WriteAllBytes($Path, $buffer)
}

function New-Archive {
    param(
        [string]$SevenZip,
        [string]$WorkingDirectory,
        [string]$ArchivePath,
        [string]$Format,
        [string[]]$InputNames,
        [string]$Compression = '-mx=1'
    )
    $typeArg = if ($Format -eq 'zip') { '-tzip' } else { '-t7z' }
    $args = @('a', $typeArg, $Compression, '-mmt=1', '-y', $ArchivePath) + $InputNames
    Push-Location $WorkingDirectory
    try {
        & $SevenZip @args | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $ArchivePath)) {
            throw "7z 造包失败：$ArchivePath（退出码 $LASTEXITCODE）"
        }
    }
    finally {
        Pop-Location
    }
}

$normalManifest = New-Object System.Collections.Generic.List[object]
$normalPlan = New-Object System.Collections.Generic.List[object]

$sampleNumber = 0
foreach ($group in $normalGroups) {
    $groupRows = New-Object System.Collections.Generic.List[object]
    foreach ($spec in $group.samples) {
        $sampleNumber++
        $seed = $normalBase + $sampleNumber
        $sample = $seed.ToString([Globalization.CultureInfo]::InvariantCulture)
        $sampleRoot = Join-Path $normalRoot $sample
        $outRoot = Join-Path $sampleRoot '原包'
        $work = Join-Path $sampleRoot '.build'
        New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $work -Force | Out-Null

        try {
            $contentBytes = [long]($spec.mb * 1000000)
            $contentName = 'content.bin'
            New-RandomContent -Path (Join-Path $work $contentName) -Bytes $contentBytes

            $ext = if ($spec.format -eq 'zip') { 'zip' } else { '7z' }
            $archiveName = "space-$sample.$ext"
            $archivePath = Join-Path $outRoot $archiveName
            if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
            New-Archive -SevenZip $SevenZip -WorkingDirectory $work -ArchivePath $archivePath -Format $spec.format -InputNames @($contentName)

            if ($spec.corrupt) {
                # 保留完整归档头/尺寸，只破坏中间的数据字节：让它仍是“单一完整包”，但解压 CRC 失败。
                $all = [IO.File]::ReadAllBytes($archivePath)
                $offset = [Math]::Min($all.Length - 64, [int]($all.Length * 0.55))
                if ($offset -lt 64) { $offset = [Math]::Max(0, $all.Length / 2) }
                $all[$offset] = $all[$offset] -bxor 0xFF
                [IO.File]::WriteAllBytes($archivePath, $all)
            }

            $sourceBytes = (Get-Item -LiteralPath $archivePath).Length
            $contentTotal = $contentBytes
            $freeBytes = [long]$group.freeBytes
            $recipe = [ordered]@{
                schemaVersion = 1
                seedBase = $normalBase
                seed = $seed
                sample = $sample
                kind = 'NormalSpaceTight'
                spaceGroup = $group.id
                source = [ordered]@{ path = "原包\$archiveName"; bytes = $sourceBytes; format = $ext }
                contentBytes = $contentTotal
                processArtifactBytes = 0
                space = [ordered]@{
                    freeBytes = $freeBytes
                    reserveBytes = $reserveBytes
                    concurrency = 2
                    expectedMaxConcurrency = 2
                }
                expected = [ordered]@{
                    sourceDeleted = -not $spec.corrupt
                    contentPublished = -not $spec.corrupt
                    expectedFailure = [bool]$spec.corrupt
                    lastProcessKept = [bool]$spec.corrupt
                }
            }
            $recipe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $sampleRoot 'recipe.json') -Encoding utf8NoBOM

            $backupSample = Join-Path $normalBackup $sample
            New-Item -ItemType Directory -Path $backupSample -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $sampleRoot '原包') -Destination (Join-Path $backupSample '原包') -Recurse -Force
            Copy-Item -LiteralPath (Join-Path $sampleRoot 'recipe.json') -Destination (Join-Path $backupSample 'recipe.json') -Force

            $row = [ordered]@{ sample = $sample; sourceBytes = $sourceBytes; contentBytes = $contentTotal; corrupt = [bool]$spec.corrupt }
            $normalManifest.Add($row)
            $groupRows.Add($row)

            Remove-Item -LiteralPath $work -Recurse -Force
        }
        catch {
            throw "样本 $sample 失败：$($_.Exception.Message)"
        }
    }
    $normalPlan.Add([ordered]@{ group = $group.id; freeBytes = $group.freeBytes; reserveBytes = $reserveBytes; concurrency = 2; samples = $groupRows })
}

$specificManifest = New-Object System.Collections.Generic.List[object]
$specificPlan = New-Object System.Collections.Generic.List[object]

$number = 0
foreach ($mb in $specificSizes) {
    $number++
    $seed = $specificBase + $number
    $sample = $seed.ToString([Globalization.CultureInfo]::InvariantCulture)
    $sampleRoot = Join-Path $specificRoot $sample
    $outRoot = Join-Path $sampleRoot '原包'
    $work = Join-Path $sampleRoot '.build'
    New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    try {
        $totalBytes = [long]($mb * 1000000)
        $each = [long][Math]::Floor($totalBytes / 3)
        $remainder = $totalBytes - ($each * 3)
        $contentNames = @('same-1.bin', 'same-2.bin', 'same-3.bin')
        $content = [byte[]]::new($each)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($content)
        foreach ($name in $contentNames) {
            [IO.File]::WriteAllBytes((Join-Path $work $name), $content)
        }
        if ($remainder -gt 0) {
            $extra = [byte[]]::new($remainder)
            [System.Security.Cryptography.RandomNumberGenerator]::Fill($extra)
            [IO.File]::WriteAllBytes((Join-Path $work 'same-tail.bin'), $extra)
            $contentNames += 'same-tail.bin'
        }

        $archiveName = "specific-$sample.7z"
        $archivePath = Join-Path $outRoot $archiveName
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
        # 特定包内是相同文件；必须存储不压缩，否则源包会远小于内容物，空间场景失真。
        New-Archive -SevenZip $SevenZip -WorkingDirectory $work -ArchivePath $archivePath -Format '7z' -InputNames $contentNames -Compression '-mx=0'

        $sourceBytes = (Get-Item -LiteralPath $archivePath).Length
        $recipe = [ordered]@{
            schemaVersion = 1
            seedBase = $specificBase
            seed = $seed
            sample = $sample
            kind = 'SpecificSpaceTight'
            spaceGroup = 'S'
            source = [ordered]@{ path = "原包\$archiveName"; bytes = $sourceBytes; format = '7z' }
            contentBytes = $totalBytes
            processArtifactBytes = 0
            space = [ordered]@{ freeBytes = 2000000; reserveBytes = $reserveBytes; concurrency = 2; expectedMaxConcurrency = 2 }
            expected = [ordered]@{ sourceDeleted = $true; contentPublished = $true; expectedFailure = $false; lastProcessKept = $false }
        }
        $recipe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $sampleRoot 'recipe.json') -Encoding utf8NoBOM

        $backupSample = Join-Path $specificBackup $sample
        New-Item -ItemType Directory -Path $backupSample -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $sampleRoot '原包') -Destination (Join-Path $backupSample '原包') -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $sampleRoot 'recipe.json') -Destination (Join-Path $backupSample 'recipe.json') -Force

        $row = [ordered]@{ sample = $sample; sourceBytes = $sourceBytes; contentBytes = $totalBytes; files = $contentNames.Count }
        $specificManifest.Add($row)
        $specificPlan.Add($row)

        Remove-Item -LiteralPath $work -Recurse -Force
    }
    catch {
        throw "特定样本 $sample 失败：$($_.Exception.Message)"
    }
}

$normalPlan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $normalLog '空间计划.json') -Encoding utf8NoBOM
$specificPlan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $specificLog '空间计划.json') -Encoding utf8NoBOM
$normalManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $normalLog '造包清单.json') -Encoding utf8NoBOM
$specificManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $specificLog '造包清单.json') -Encoding utf8NoBOM

$validation = [ordered]@{
    normal = $normalManifest.Count
    specific = $specificManifest.Count
    total = $normalManifest.Count + $specificManifest.Count
    normalGroups = $normalPlan.Count
    reserveBytes = $reserveBytes
    passed = ($normalManifest.Count -eq 15 -and $specificManifest.Count -eq 5)
}
$validation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $normalLog '校验结果.json') -Encoding utf8NoBOM
$validation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $specificLog '校验结果.json') -Encoding utf8NoBOM

Write-Output ("空间不足造包完成：普通 {0}/15，特定 {1}/5，总 {2}" -f $normalManifest.Count, $specificManifest.Count, $validation.total)

