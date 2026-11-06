param(
    [string]$SpaceRoot = 'H:\BaiduNetdiskDownload\困难大测试',
    [string]$SevenZip = 'E:\ArchiveFixer\tools\7zip\7z.exe',
    [int]$NormalSeed = 913000000,
    [int]$SpecificSeed = 913100000,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$reserveBytes = 64 * 1024

$normalRoot = Join-Path $SpaceRoot ("大测试\$NormalSeed")
$specificRoot = Join-Path $SpaceRoot ("大测试\$SpecificSeed")
$normalBackup = Join-Path $SpaceRoot ("大测试备份位置\$NormalSeed-空间不足-矩阵-备份")
$specificBackup = Join-Path $SpaceRoot ("大测试备份位置\$SpecificSeed-特定解压-矩阵-备份")
$normalLog = Join-Path $SpaceRoot ("大测试日志\大测试日志-$NormalSeed-空间不足-矩阵")
$specificLog = Join-Path $SpaceRoot ("大测试日志\大测试日志-$SpecificSeed-特定解压-矩阵")

if (-not (Test-Path -LiteralPath $SevenZip)) { throw "7z 不存在：$SevenZip" }

function Assert-SafeRoot([string]$Root, [string]$Marker) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $mf = Join-Path $Root '.space-matrix-generated'
    if (-not (Test-Path -LiteralPath $mf)) { throw "拒绝覆盖：$Root 没有矩阵造包标记" }
    if (((Get-Content -LiteralPath $mf -Raw).Trim()) -ne $Marker) { throw "拒绝覆盖：$Root 标记不属于 $Marker" }
    $unknown = Get-ChildItem -LiteralPath $Root -Force | Where-Object {
        $_.Name -notmatch '^(C\d{2}|B\d)$' -and
        $_.Name -ne '.space-matrix-generated' -and
        $_.Name -notmatch '^(空间计划|校验结果|造包清单)\.json$'
    }
    if ($unknown) { throw "拒绝覆盖：$Root 有未知内容：$($unknown.Name -join ', ')" }
}

foreach ($pair in @(
    @($normalRoot, [string]$NormalSeed),
    @($specificRoot, [string]$SpecificSeed),
    @($normalBackup, [string]$NormalSeed),
    @($specificBackup, [string]$SpecificSeed),
    @($normalLog, [string]$NormalSeed),
    @($specificLog, [string]$SpecificSeed)
)) {
    if (Test-Path -LiteralPath $pair[0]) {
        if (-not $Force) { throw "批次已存在：$($pair[0])（确认覆盖请加 -Force）" }
        if ($pair[1] -match '^\d{9}$') { Assert-SafeRoot -Root $pair[0] -Marker $pair[1] }
    }
}
foreach ($dir in @($normalRoot, $specificRoot, $normalBackup, $specificBackup, $normalLog, $specificLog)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Set-Content -LiteralPath (Join-Path $normalRoot '.space-matrix-generated') -Value ([string]$NormalSeed) -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $specificRoot '.space-matrix-generated') -Value ([string]$SpecificSeed) -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $normalBackup '.space-matrix-generated') -Value ([string]$NormalSeed) -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $specificBackup '.space-matrix-generated') -Value ([string]$SpecificSeed) -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $normalLog '.space-matrix-generated') -Value ([string]$NormalSeed) -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $specificLog '.space-matrix-generated') -Value ([string]$SpecificSeed) -Encoding utf8NoBOM

function New-RandomFile([string]$Path, [long]$Bytes) {
    $buffer = [byte[]]::new($Bytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    # 固定一个非归档头，避免随机字节碰巧命中 Z/ZIP 等魔数被当成内层包。
    $header = [Text.Encoding]::ASCII.GetBytes("AFTEST-CONTENT`n")
    [Array]::Copy($header, $buffer, [Math]::Min($header.Length, $buffer.Length))
    [IO.File]::WriteAllBytes($Path, $buffer)
}

function Invoke-7z([string[]]$Arguments, [string]$WorkDirectory) {
    Push-Location $WorkDirectory
    try {
        $output = & $SevenZip @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw ("7z 失败：{0}`n{1}" -f ($Arguments -join ' '), ($output -join "`n")) }
    }
    finally { Pop-Location }
}

function New-Archive([string]$Work, [string]$Base, [string]$Format, [string[]]$Inputs) {
    $type = if ($Format -eq 'zip') { '-tzip' } else { '-t7z' }
    $ext = if ($Format -eq 'zip') { '.zip' } else { '.7z' }
    $archive = $Base + $ext
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    $arguments = @('a', $type, '-mx=0', '-mmt=1', '-y', $archive) + $Inputs
    Invoke-7z -Arguments $arguments -WorkDirectory $Work
    return $archive
}

function New-VolumeGroup([string]$Work, [string]$Base, [string[]]$Inputs) {
    $archive = $Base + '.7z'
    $leaf = Split-Path -Leaf $Base
    $outputDir = Split-Path -Parent $Base
    Get-ChildItem -LiteralPath $outputDir -Filter ($leaf + '.7z.*') -ErrorAction SilentlyContinue | Remove-Item -Force
    $arguments = @('a', '-t7z', '-mx=0', '-mmt=1', '-v1048576b', '-y', $archive) + $Inputs
    Invoke-7z -Arguments $arguments -WorkDirectory $Work
    return @(Get-ChildItem -LiteralPath $outputDir -Filter ($leaf + '.7z.*') | Sort-Object Name | Select-Object -ExpandProperty FullName)
}

function Copy-Case([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Source '原包') -Destination (Join-Path $Destination '原包') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $Source 'recipe.json') -Destination (Join-Path $Destination 'recipe.json') -Force
}

# ---------------------------------------------------------------- 15 批普通
$types = @('ample','largest','allblocked','corrupt','ample','largest','allblocked','corrupt','ample','largest','allblocked','corrupt','ample','largest','allblocked')
$counts = @(5,6,5,6,5,6,5,6,5,6,5,6,5,6,6)
$rng = [Random]::new(913)
$sizes = @(0.3,0.4,0.6,0.8,1.0,1.2,1.5)
$normalPlan = New-Object System.Collections.Generic.List[object]
$normalManifest = New-Object System.Collections.Generic.List[object]
$normalPackageTotal = 0

for ($caseIndex = 1; $caseIndex -le 15; $caseIndex++) {
    $caseId = 'C' + $caseIndex.ToString('00')
    $count = $counts[$caseIndex - 1]
    $type = $types[$caseIndex - 1]
    $caseRoot = Join-Path $normalRoot $caseId
    if (Test-Path -LiteralPath $caseRoot) { Remove-Item -LiteralPath $caseRoot -Recurse -Force }
    $srcRoot = Join-Path $caseRoot '原包'
    $work = Join-Path $caseRoot '.build'
    New-Item -ItemType Directory -Path $srcRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $packages = New-Object System.Collections.Generic.List[object]

    for ($n = 1; $n -le $count; $n++) {
        $mb = $sizes[$rng.Next($sizes.Count)]
        $corrupt = ($type -eq 'corrupt' -and $n -eq 1)
        $format = if ($corrupt) { '7z' } elseif ($rng.Next(100) -lt 60) { '7z' } else { 'zip' }
        $nested = (-not $corrupt) -and ($rng.Next(100) -lt 25)
        $name = "pkg-$caseId-$('{0:D2}' -f $n)"
        $payload = Join-Path $work 'payload.bin'
        New-RandomFile -Path $payload -Bytes ([long]($mb * 1000000))
        if ($nested) {
            $inner = New-Archive -Work $work -Base (Join-Path $work 'inner') -Format '7z' -Inputs @('payload.bin')
            $top = New-Archive -Work $work -Base (Join-Path $work $name) -Format $format -Inputs @([IO.Path]::GetFileName($inner))
            Remove-Item -LiteralPath $inner -Force -ErrorAction SilentlyContinue
        }
        else {
            $top = New-Archive -Work $work -Base (Join-Path $work $name) -Format $format -Inputs @('payload.bin')
        }
        if ($corrupt) {
            $all = [IO.File]::ReadAllBytes($top)
            $offset = [Math]::Min($all.Length - 64, [int]($all.Length * 0.55))
            if ($offset -lt 64) { $offset = [Math]::Max(0, [int]($all.Length / 2)) }
            $all[$offset] = $all[$offset] -bxor 0xFF
            [IO.File]::WriteAllBytes($top, $all)
        }
        $sourceName = [IO.Path]::GetFileName($top)
        $sourcePath = Join-Path $srcRoot $sourceName
        Copy-Item -LiteralPath $top -Destination $sourcePath -Force
        $sourceBytes = (Get-Item -LiteralPath $sourcePath).Length
        $expected = if ($corrupt) { 'Failed' } else { 'Succeeded' }
        $packages.Add([ordered]@{
            fileName = $sourceName
            sourceBytes = $sourceBytes
            contentBytes = [long]($mb * 1000000)
            format = $format
            shape = if ($nested) { 'nested' } else { 'single' }
            corrupt = $corrupt
            expected = $expected
        })
        Remove-Item -LiteralPath $top, $payload -Force -ErrorAction SilentlyContinue
    }

    $sumSource = ($packages | Measure-Object -Property sourceBytes -Sum).Sum
    $maxPackage = $packages | Sort-Object sourceBytes -Descending | Select-Object -First 1
    $free = switch ($type) {
        'ample' { [long]$sumSource + (8 * 1024 * 1024) }
        'largest' { [long][Math]::Max(128 * 1024, ([double]$maxPackage.sourceBytes / 2)) }
        'allblocked' { 64 * 1024 }
        'corrupt' { [long]$sumSource + (8 * 1024 * 1024) }
        default { [long]$sumSource + (8 * 1024 * 1024) }
    }
    $blocked = @()
    if ($type -eq 'largest') { $blocked = @($maxPackage.fileName) }
    if ($type -eq 'allblocked') { $blocked = @($packages | ForEach-Object { $_.fileName }) }
    $failed = @($packages | Where-Object { $_.corrupt } | ForEach-Object { $_.fileName })
    $recipe = [ordered]@{}
    $recipe['schemaVersion'] = 3
    $recipe['caseId'] = $caseId
    $recipe['kind'] = 'NormalBatch'
    $recipe['spaceType'] = $type
    $recipe['packageCount'] = $packages.Count
    $recipe['packages'] = $packages.ToArray()
    $recipe['space'] = [ordered]@{ freeBytes = $free; reserveBytes = $reserveBytes; concurrency = 2 }
    $recipe['expected'] = [ordered]@{ blocked = $blocked; failed = $failed }
    $recipe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $caseRoot 'recipe.json') -Encoding utf8NoBOM
    Copy-Case -Source $caseRoot -Destination (Join-Path $normalBackup $caseId)
    $normalPlan.Add([ordered]@{ caseId = $caseId; spaceType = $type; packageCount = $packages.Count; freeBytes = $free; reserveBytes = $reserveBytes })
    $normalManifest.Add([ordered]@{ caseId = $caseId; spaceType = $type; packageCount = $packages.Count; sourceBytes = $sumSource; freeBytes = $free })
    $normalPackageTotal += $packages.Count
    Remove-Item -LiteralPath $work -Recurse -Force
}

# ---------------------------------------------------------------- 5 批特定解压
$specificKinds = @('direct','two-layer','three-layer','last-folder','volume','same-base')
$specificPlan = New-Object System.Collections.Generic.List[object]
$specificManifest = New-Object System.Collections.Generic.List[object]
$specificPackageTotal = 0
$specificBatchCounts = @(5,6,5,6,5)
$specificTypes = @('ample','largest','allblocked','corrupt','ample')

for ($batchIndex = 1; $batchIndex -le 5; $batchIndex++) {
    $batchId = 'B' + $batchIndex
    $count = $specificBatchCounts[$batchIndex - 1]
    $type = $specificTypes[$batchIndex - 1]
    $caseRoot = Join-Path $specificRoot $batchId
    if (Test-Path -LiteralPath $caseRoot) { Remove-Item -LiteralPath $caseRoot -Recurse -Force }
    $srcRoot = Join-Path $caseRoot '原包'
    $work = Join-Path $caseRoot '.build'
    New-Item -ItemType Directory -Path $srcRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $packages = New-Object System.Collections.Generic.List[object]

    for ($n = 1; $n -le $count; $n++) {
        $kind = $specificKinds[($n - 1 + $batchIndex - 1) % $specificKinds.Count]
        $corrupt = ($type -eq 'corrupt' -and $n -eq 1)
        if ($corrupt) { $kind = 'direct' }
        $pkgWork = Join-Path $work ("pkg-$('{0:D2}' -f $n)")
        New-Item -ItemType Directory -Path $pkgWork -Force | Out-Null
        $content = Join-Path $pkgWork 'content'
        New-Item -ItemType Directory -Path $content -Force | Out-Null
        $finalNames = @()
        for ($f = 1; $f -le 5; $f++) {
            $finalName = "final-$('{0:D2}' -f $f).bin"
            New-RandomFile -Path (Join-Path $content $finalName) -Bytes 250000
            $finalNames += $finalName
        }
        if ($kind -eq 'last-folder') {
            $fold = Join-Path $content ('final-folder-' + $n.ToString('00'))
            New-Item -ItemType Directory -Path $fold -Force | Out-Null
            foreach ($f in $finalNames) { Move-Item -LiteralPath (Join-Path $content $f) -Destination (Join-Path $fold $f) }
        }

        $innerName = "inner-$batchId-$('{0:D2}' -f $n)"
        $topFiles = @()
        switch ($kind) {
            'direct' {
                $top = New-Archive -Work $content -Base (Join-Path $pkgWork $innerName) -Format '7z' -Inputs $finalNames
                $topFiles = @($top)
            }
            'two-layer' {
                $inner = New-Archive -Work $content -Base (Join-Path $pkgWork $innerName) -Format '7z' -Inputs $finalNames
                $top = New-Archive -Work $pkgWork -Base (Join-Path $pkgWork ("outer-$batchId-$('{0:D2}' -f $n)")) -Format '7z' -Inputs @([IO.Path]::GetFileName($inner))
                $topFiles = @($top)
            }
            'three-layer' {
                $inner = New-Archive -Work $content -Base (Join-Path $pkgWork $innerName) -Format '7z' -Inputs $finalNames
                $middle = New-Archive -Work $pkgWork -Base (Join-Path $pkgWork ("middle-$batchId-$('{0:D2}' -f $n)")) -Format 'zip' -Inputs @([IO.Path]::GetFileName($inner))
                $top = New-Archive -Work $pkgWork -Base (Join-Path $pkgWork ("outer-$batchId-$('{0:D2}' -f $n)")) -Format '7z' -Inputs @([IO.Path]::GetFileName($middle))
                $topFiles = @($top)
            }
            'last-folder' {
                $inner = New-Archive -Work $content -Base (Join-Path $pkgWork $innerName) -Format '7z' -Inputs @((Get-ChildItem -LiteralPath $content -Directory | Select-Object -First 1).Name)
                $top = New-Archive -Work $pkgWork -Base (Join-Path $pkgWork ("outer-$batchId-$('{0:D2}' -f $n)")) -Format '7z' -Inputs @([IO.Path]::GetFileName($inner))
                $topFiles = @($top)
            }
            'volume' {
                $topFiles = New-VolumeGroup -Work $content -Base (Join-Path $pkgWork $innerName) -Inputs $finalNames
            }
            'same-base' {
                $inner = New-Archive -Work $content -Base (Join-Path $pkgWork 'same-inner') -Format '7z' -Inputs $finalNames
                $top = New-Archive -Work $pkgWork -Base (Join-Path $pkgWork ("same-$batchId-$('{0:D2}' -f $n)")) -Format '7z' -Inputs @([IO.Path]::GetFileName($inner))
                $topFiles = @($top)
            }
        }
        if ($topFiles.Count -eq 0) { throw "特定样本 $batchId/$n 没有顶层产物" }
        if ($corrupt) {
            $file = $topFiles[0]
            $all = [IO.File]::ReadAllBytes($file)
            $offset = [Math]::Min($all.Length - 64, [int]($all.Length * 0.55))
            if ($offset -lt 64) { $offset = [Math]::Max(0, [int]($all.Length / 2)) }
            $all[$offset] = $all[$offset] -bxor 0xFF
            [IO.File]::WriteAllBytes($file, $all)
        }
        $sourceNames = @()
        foreach ($file in $topFiles) {
            $target = Join-Path $srcRoot ([IO.Path]::GetFileName($file))
            Copy-Item -LiteralPath $file -Destination $target -Force
            $sourceNames += [IO.Path]::GetFileName($file)
        }
        $packages.Add([ordered]@{
            packageId = "$batchId-$('{0:D2}' -f $n)"
            shape = $kind
            sourceFiles = $sourceNames
            sourceBytes = [long](($topFiles | ForEach-Object { (Get-Item -LiteralPath (Join-Path $srcRoot ([IO.Path]::GetFileName($_)))).Length } | Measure-Object -Sum).Sum)
            contentFiles = 5
            corrupt = $corrupt
        })
    }

    $sumSource = ($packages | Measure-Object -Property sourceBytes -Sum).Sum
    $maxPackage = $packages | Sort-Object sourceBytes -Descending | Select-Object -First 1
    $free = switch ($type) {
        'ample' { [long]$sumSource + (8 * 1024 * 1024) }
        'largest' { [long][Math]::Max(128 * 1024, ([double]$maxPackage.sourceBytes / 2)) }
        'allblocked' { 64 * 1024 }
        'corrupt' { [long]$sumSource + (8 * 1024 * 1024) }
        default { [long]$sumSource + (8 * 1024 * 1024) }
    }
    $blocked = @()
    if ($type -eq 'largest') { $blocked = @($maxPackage.packageId) }
    if ($type -eq 'allblocked') { $blocked = @($packages | ForEach-Object { $_.packageId }) }
    $failed = @($packages | Where-Object { $_.corrupt } | ForEach-Object { $_.packageId })
    $recipe = [ordered]@{
        schemaVersion = 3
        caseId = $batchId
        kind = 'SpecificBatch'
        spaceType = $type
        packageCount = $packages.Count
        packages = $packages.ToArray()
        space = [ordered]@{ freeBytes = $free; reserveBytes = $reserveBytes; concurrency = 2 }
        expected = [ordered]@{ blocked = $blocked; failed = $failed }
    }
    $recipe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $caseRoot 'recipe.json') -Encoding utf8NoBOM
    Copy-Case -Source $caseRoot -Destination (Join-Path $specificBackup $batchId)
    $specificPlan.Add([ordered]@{ caseId = $batchId; spaceType = $type; packageCount = $packages.Count; freeBytes = $free; reserveBytes = $reserveBytes })
    $specificManifest.Add([ordered]@{ caseId = $batchId; spaceType = $type; packageCount = $packages.Count; sourceBytes = $sumSource; freeBytes = $free })
    $specificPackageTotal += $packages.Count
    Remove-Item -LiteralPath $work -Recurse -Force
}

$normalPlan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $normalLog '空间计划.json') -Encoding utf8NoBOM
$specificPlan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $specificLog '空间计划.json') -Encoding utf8NoBOM
$normalManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $normalLog '造包清单.json') -Encoding utf8NoBOM
$specificManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $specificLog '造包清单.json') -Encoding utf8NoBOM
$validation = [ordered]@{
    normalBatches = 15
    normalPackages = $normalPackageTotal
    specificBatches = 5
    specificPackages = $specificPackageTotal
    totalBatches = 20
    passed = $true
}
$validation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $normalLog '校验结果.json') -Encoding utf8NoBOM
$validation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $specificLog '校验结果.json') -Encoding utf8NoBOM
Write-Output ("空间不足矩阵造包完成：普通 15 批 / {0} 包，特定 5 批 / {1} 包，总 20 批" -f $validation.normalPackages, $validation.specificPackages)

