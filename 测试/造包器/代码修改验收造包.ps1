#requires -Version 5.1
<#
  代码修改验收造包器（简单验收）。
  与批次造包器完全分离：只造本次修改所需的最小样本，
  产物固定写 测试\修改测试位置\<ChangeId>\样本|结果|日志。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ChangeId,

    [string]$Module = 'General',

    [ValidateSet('single', 'volume', 'content', 'disguised')]
    [string]$Shape = 'single',

    [string]$SevenZip = 'E:\ArchiveFixer\tools\7zip\7z.exe',

    [switch]$Force,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $testRoot

if ($ChangeId -notmatch '^\d{8}-\d{2}-.+$') {
    throw "ChangeId 必须是 yyyyMMdd-序号-名称，例如 20261111-01-内容扫描准入"
}

$changeRoot = Join-Path $testRoot "修改测试位置\$ChangeId"
$sampleRoot = Join-Path $changeRoot '样本'
$resultRoot = Join-Path $changeRoot '结果'
$logRoot = Join-Path $changeRoot '日志'
$workRoot = Join-Path $changeRoot '.build'

if (Test-Path -LiteralPath $changeRoot) {
    if (-not $Force) { throw "已存在：$changeRoot（要重建请加 -Force）" }
    Remove-Item -LiteralPath $changeRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $sampleRoot, $resultRoot, $logRoot, $workRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $SevenZip)) {
    $candidate = 'C:\Program Files\7-Zip\7z.exe'
    if (Test-Path -LiteralPath $candidate) { $SevenZip = $candidate } else { throw "找不到 7z.exe：$SevenZip" }
}

$payload = Join-Path $workRoot 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$content = Join-Path $payload 'content.bin'
$bytes = New-Object byte[] (3 * 1024 * 1024)
$rng = New-Object System.Random(20261111)
$rng.NextBytes($bytes)
[IO.File]::WriteAllBytes($content, $bytes)

$expected = 'Succeeded'
$expectedSourceKeep = $true
$expectedProcessKeep = $true
$sourceRelative = ''
$packageKind = 'single'

switch ($Shape) {
    'single' {
        $archive = Join-Path $sampleRoot '原包\sample.7z'
        New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
        & $SevenZip a -t7z -mx=0 -y $archive $content | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7z single 失败" }
        $sourceRelative = 'sample.7z'
    }
    'volume' {
        $archive = Join-Path $sampleRoot '原包\sample.7z'
        New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
        & $SevenZip a -t7z -mx=0 -v1m -y $archive $content | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7z volume 失败" }
        $sourceRelative = 'sample.7z.001'
        $packageKind = 'volume'
    }
    'content' {
        $zip = Join-Path $sampleRoot '原包\content.zip'
        New-Item -ItemType Directory -Path (Split-Path -Parent $zip) -Force | Out-Null
        $contentDir = Join-Path $workRoot 'content-files'
        New-Item -ItemType Directory -Path $contentDir -Force | Out-Null
        $png = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)
        for ($i = 0; $i -lt 100; $i++) {
            [IO.File]::WriteAllBytes((Join-Path $contentDir ("内容-{0:D3}.png" -f $i)), $png)
        }
        & $SevenZip a -tzip -mx=0 -y $zip (Join-Path $contentDir '*') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7z content 失败" }
        $sourceRelative = 'content.zip'
        $packageKind = 'content'
        $expectedProcessKeep = $true
    }
    'disguised' {
        $archive = Join-Path $sampleRoot '原包\sample.7z'
        New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
        & $SevenZip a -t7z -mx=0 -y $archive $content | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7z disguised 失败" }
        $disguised = Join-Path $sampleRoot '原包\sample.mp4'
        Move-Item -LiteralPath $archive -Destination $disguised -Force
        $sourceRelative = 'sample.mp4'
        $packageKind = 'disguised'
    }
}

$recipe = [ordered]@{
    schemaVersion = 2
    kind = 'CodeModification'
    changeId = $ChangeId
    module = $Module
    shape = $Shape
    sampleId = $ChangeId
    expectedOutcome = $expected
    expectedSourceKeep = $expectedSourceKeep
    expectedProcessKeep = $expectedProcessKeep
    expectedFinalContentPath = 'content.bin'
    expectedContentBytes = $bytes.Length
    sourceRelative = $sourceRelative
    packageKind = $packageKind
}
$recipePath = Join-Path $sampleRoot 'recipe.json'
$recipe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recipePath -Encoding utf8NoBOM

$note = @"
# 代码修改验收样本

- ChangeId：$ChangeId
- Module：$Module
- Shape：$Shape
- 样本：$sampleRoot
- 结果：$resultRoot
- 日志：$logRoot
- 生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@
Set-Content -LiteralPath (Join-Path $changeRoot '说明.md') -Value $note -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $logRoot '造包.log') -Value ("生成 {0} / {1} / {2}" -f $ChangeId, $Module, $Shape) -Encoding utf8NoBOM

if (-not $KeepWork) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "简单验收样本已生成：$changeRoot"
Write-Host "样本：$sampleRoot"
Write-Host "结果：$resultRoot"
Write-Host "日志：$logRoot"
