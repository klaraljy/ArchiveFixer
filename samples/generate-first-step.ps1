# 第一大步造包器（用户 2026-10-24 规格：只回答"第一大步完成没有"）
#
# 用法：
#   pwsh -NoProfile -File samples/generate-first-step.ps1 -List          # 列出全部样本与预期
#   pwsh -NoProfile -File samples/generate-first-step.ps1                # 全部生成到默认根
#   pwsh -NoProfile -File samples/generate-first-step.ps1 -Sample A01     # 只造一份
#   pwsh -NoProfile -File samples/generate-first-step.ps1 -Sample A01,C02 # 造多份
#
# 输出：<Root>\<Id>\  —— 样本本体 + recipe.json（断言随样本走，判红绿不靠人看）
#
# 边界（用户拍板）：
#   · 只造**单一完整包**及其伪装/加密/递归/损坏/安全/落点；**不造**真正的分卷组（第二大步）。
#   · 负对照只造两种："名字像分卷、内容是单卷"（应照解）与"名字像单卷、内容是分卷首卷"（预期**跳过+交接**，
#     因为第二大步还没接线 —— 用户 2026-10-24 拍板按现状写）。
#   · 每份两种落点模式都跑：`mode=default`（未指定位置）与 `mode=custom`（指定位置）。
param(
    [string]$Sample = 'all',
    [int]$Seed = 20261024,
    [string]$Root = 'H:\BaiduNetdiskDownload\大测试\第一大步',
    [switch]$List
)

$ErrorActionPreference = 'Stop'

$sevenZip = 'E:\DeepSeekProjects\ArchiveFixer\src\ArchiveFixer\tools\7zip\7z.exe'
$rarExe = 'C:\Program Files\WinRAR\Rar.exe'

# 合成密码（⛔ 真实密码不许进仓库；AGENTS.md §8）
$pw = 'TestPass123!'
$pwCn = '31中文41密码59！@。26'
$pwWrong = 'WrongPass987'

foreach ($tool in @($sevenZip, $rarExe)) {
    if (-not (Test-Path $tool)) { Write-Warning "缺工具：$tool（用到它的样本会失败）" }
}

# ══════════════════════════ 样本清单（一份 = 一个断言） ══════════════════════════
# build = 构造方式；expect = 断言（机器判红绿用）
$samples = @(
    # A 识别 / 还原（5）
    @{ Id = 'A01'; Family = '识别'; Title = '干净的 zip'
       Build = 'zip'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt', '数据.bin'); LogForbids = @('第二大步·组分析') } },

    @{ Id = 'A02'; Family = '识别'; Title = '干净的 7z'
       Build = '7z'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'Succeeded'; Content = @('说明.txt', '数据.bin') } },

    @{ Id = 'A03'; Family = '识别'; Title = '干净的 tar.gz（复合后缀不许被破坏）'
       Build = 'targz'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'GZip'; Outcome = 'Succeeded'; Content = @('说明.txt') } },

    @{ Id = 'A04'; Family = '识别'; Title = '伪装成 .jpg 的 zip'
       Build = 'zip'; Disguise = '数据.备份.jpg'
       Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt', '数据.bin') } },

    @{ Id = 'A05'; Family = '识别'; Title = '多重伪装 .pdf.jpg + 中文混入后缀（7z）'
       Build = '7z'; Disguise = '资料.zi删除p.pdf'
       Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'Succeeded'; Content = @('说明.txt', '数据.bin') } },

    # B 加密（6）
    @{ Id = 'B01'; Family = '加密'; Title = '7z 文件名也加密（-mhe=on），密码本命中'
       Build = '7zmhe'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'Succeeded'; Password = $pw; Content = @('说明.txt') } },

    @{ Id = 'B02'; Family = '加密'; Title = '7z 只加密数据（名字可列），密码本命中'
       Build = '7zdata'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'Succeeded'; Password = $pw; Content = @('说明.txt') } },

    @{ Id = 'B03'; Family = '加密'; Title = 'ZIP AES-256，密码本命中'
       Build = 'zipaes'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Password = $pw; Content = @('说明.txt') } },

    @{ Id = 'B04'; Family = '加密'; Title = 'ZIP ZipCrypto，密码本命中'
       Build = 'zipcrypto'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Password = $pw; Content = @('说明.txt') } },

    @{ Id = 'B05'; Family = '加密'; Title = '7z 中文密码（-mhe=on）'
       Build = '7zcn'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'Succeeded'; Password = $pwCn; Content = @('说明.txt') } },

    @{ Id = 'B06'; Family = '加密'; Title = '7z 密码全错（密码本里没有正确密码）'
       Build = '7zwrong'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'Failed'; Password = $pwWrong; Content = @() } },

    # C 负对照：名字骗人（3）
    @{ Id = 'C01'; Family = '负对照'; Title = '内容是单卷、名字被改成 .7z.001（按内容照解）'
       Build = '7z'; Disguise = 'C01.7z.001'
       Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'Succeeded'; Content = @('说明.txt', '数据.bin') } },

    @{ Id = 'C02'; Family = '负对照'; Title = '内容是分卷首卷、名字被改成 .7z（闸门拦下 → 跳过 + 交接）'
       Build = 'volfirst'; SizeKb = 4096; Expect = @{ BodyKind = 'VolumeFirstPiece'; Format = 'SevenZip'; Outcome = 'Skipped'; Content = @(); LogContains = @('格式：', '底层识别', '分卷成员', '本批交接 1 份', '片数来自自述 5 片'); LogForbids = @('解压成功') } },

    @{ Id = 'C03'; Family = '负对照'; Title = '纯文本改名 .zip（判不出 ⇒ 跳过，不算失败）'
       Build = 'textzip'; Expect = @{ BodyKind = 'Unknown'; Format = 'Unknown'; Outcome = 'Skipped'; Content = @(); LogContains = @('格式：'); LogForbids = @('解压成功') } },

    # D 双面文件（3）
    @{ Id = 'D01'; Family = '双面'; Title = 'mp4 前缀 + 尾部 zip'
       Build = 'zip'; Media = 'mp4'; Disguise = 'D01.mp4'
       Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt') } },

    @{ Id = 'D02'; Family = '双面'; Title = 'jpg 前缀 + 尾部 7z'
       Build = '7z'; Media = 'jpg'; Disguise = 'D02.jpg'
       Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'Succeeded'; Content = @('说明.txt') } },

    @{ Id = 'D03'; Family = '双面'; Title = '纯媒体、尾部没有归档（不许当包）'
       Build = 'puremedia'; Expect = @{ BodyKind = 'Unknown'; Format = 'Unknown'; Outcome = 'Skipped'; Content = @(); LogContains = @('格式：'); LogForbids = @('解压成功') } },

    # E 损坏（2）
    @{ Id = 'E01'; Family = '损坏'; Title = '7z 头完好、数据段被改坏（损坏包 ⇒ 部分完成）'
       Build = '7zcrc'; SizeKb = 2048; Expect = @{ BodyKind = 'SingleComplete'; Format = 'SevenZip'; Outcome = 'PartiallyCompleted'; Content = @() } },

    @{ Id = 'E02'; Family = '损坏'; Title = '7z 被截断（内容上与分卷首卷不可分 ⇒ 预期跳过、交给第二大步）'
       Build = '7ztrunc'; Expect = @{ BodyKind = 'VolumeFirstPiece'; Format = 'SevenZip'; Outcome = 'Skipped'; Content = @() } },

    # F 递归（3）
    @{ Id = 'F01'; Family = '递归'; Title = 'zip 里套一层 7z（单链 1 层）'
       Build = 'nested1'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt') } },

    @{ Id = 'F02'; Family = '递归'; Title = 'zip → 7z → zip 三层单链'
       Build = 'nested3'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt') } },

    @{ Id = 'F03'; Family = '递归'; Title = '外层一个包里两个内层包（分支）'
       Build = 'nestedbranch'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt') } },

    # G 安全（3）
    @{ Id = 'G01'; Family = '安全'; Title = 'zip 里有 ../ 路径穿越条目（不许落到父目录）'
       Build = 'traversal'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Failed'; Content = @(); ForbidOutside = $true } },

    @{ Id = 'G02'; Family = '安全'; Title = 'zip 里有 CON / 结尾空格与点（引擎改名后照常落盘）'
       Build = 'reserved'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt'); LogContains = @('格式：', '名字不规范'); LogForbids = @() } },

    @{ Id = 'G03'; Family = '安全'; Title = 'APK（必须拒绝解压）'
       Build = 'apk'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Skipped'; Content = @(); LogContains = @('格式：'); LogForbids = @('解压成功') } },

    # H 落点 / 收尾（3）
    @{ Id = 'H01'; Family = '落点'; Title = '普通落点：包名目录 + 内容物层'
       Build = 'zip'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt'); Landing = '包名目录' } },

    @{ Id = 'H02'; Family = '落点'; Title = '同名冲突：两份同名包（(1) 让位，绝不覆盖）'
       Build = 'zip-duplicate'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt'); Landing = '让位' } },

    @{ Id = 'H03'; Family = '落点'; Title = '指定位置模式（落点 = 指定根 / 包名 / 内容物）'
       Build = 'zip'; Mode = 'custom'; Expect = @{ BodyKind = 'SingleComplete'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt'); Landing = '指定位置' } },

    # V 第二大步（2）
    @{ Id = 'V01'; Family = '第二大步'; Title = '完整的 7z 三卷组（可证完整 ⇒ 第二大步接住、只解一层）'
       Build = 'volcomplete'; SizeKb = 3072
       Expect = @{ BodyKind = 'VolumeFirstPiece'; Format = 'SevenZip'; Outcome = 'Succeeded'; Content = @('说明.txt', '数据.bin'); LogContains = @('格式：', '内容卡', '第二大步入口任务', '第二大步·结论') } },

    @{ Id = 'V02'; Family = '第二大步'; Title = '外层 zip 里装 7z 三卷组（程序产出的片 ⇒ 搬进入口层 + 成功后收片）'
       Build = 'nestedvol'; SizeKb = 3072
       Expect = @{ BodyKind = 'VolumeFirstPiece'; Format = 'Zip'; Outcome = 'Succeeded'; Content = @('说明.txt', '数据.bin'); LogContains = @('格式：', '第二大步·搬进入口层', '第二大步·收片') } },

    @{ Id = 'V03'; Family = '第二大步'; Title = '7z 三卷组散在两个目录（用户原件 ⇒ 入口层只建硬链接、成功后只收链接）'
       Build = 'volscatter'; SizeKb = 3072
       Expect = @{ BodyKind = 'VolumeFirstPiece'; Format = 'SevenZip'; Outcome = 'Succeeded'; Content = @('说明.txt', '数据.bin'); LogContains = @('格式：', '第二大步·搬进入口层', '硬链接', '第二大步·收片', '第二大步·结论') } },

    @{ Id = 'V04'; Family = '第二大步'; Title = '完整 7z 三卷组但密码不在本里（失败 ⇒ 片全部留在入口层）'
       Build = 'volenc'; SizeKb = 3072
       Expect = @{ BodyKind = 'VolumeFirstPiece'; Format = 'SevenZip'; Outcome = 'Failed'; Content = @(); LogContains = @('格式：', '第二大步·收片', '片全部留着', '第二大步·结论') } }
)

if ($List) {
    $samples | ForEach-Object { "{0}  [{1}] {2}  ⇒ {3} / {4}" -f $_.Id, $_.Family, $_.Title, $_.Expect.BodyKind, $_.Expect.Outcome }
    return
}

# ══════════════════════════ 工具函数 ══════════════════════════
function Run-7z {
    param([string[]]$Arguments)
    $out = & $sevenZip @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("7z 失败：{0}`n{1}" -f ($Arguments -join ' '), ($out -join "`n")) }
}

function New-Content {
    param([string]$Dir, [int]$SizeKb = 512, [switch]$ManyFiles)
    $inner = $Dir
    New-Item -ItemType Directory -Path $inner -Force | Out-Null

    # 固定文本（每份都不一样 ⇒ 能证明"解的是这一份"）
    Set-Content -LiteralPath (Join-Path $inner '说明.txt') -Value ("第一大步样本：{0}`n生成时间不参与断言。" -f $Dir) -Encoding utf8

    # 确定性伪随机数据（同一 Seed ⇒ 同一字节）
    $rng = [System.Random]::new(($Seed + $Dir.GetHashCode()) -band 0x7FFFFFFF)
    $buf = New-Object byte[] ($SizeKb * 1024)
    $rng.NextBytes($buf)
    [System.IO.File]::WriteAllBytes((Join-Path $inner '数据.bin'), $buf)

    if ($ManyFiles) {
        $sub = Join-Path $inner '很多小文件'
        New-Item -ItemType Directory -Path $sub -Force | Out-Null
        for ($i = 0; $i -lt 40; $i++) {
            [System.IO.File]::WriteAllText((Join-Path $sub ("f{0:D2}.txt" -f $i)), "小文件 $i")
        }
    }
}

function New-SingleArchive {
    param([string]$Kind, [string]$ContentDir, [string]$OutFile)

    switch ($Kind) {
        'zip' {
            Run-7z @('a', '-tzip', '-mx=1', $OutFile, (Join-Path $ContentDir '*')) ; break
        }
        '7z' {
            Run-7z @('a', '-t7z', '-mx=1', $OutFile, (Join-Path $ContentDir '*')) ; break
        }
        'targz' {
            $tar = [System.IO.Path]::ChangeExtension($OutFile, '.tar')
            Run-7z @('a', '-ttar', '-mx=0', $tar, (Join-Path $ContentDir '*'))
            Run-7z @('a', '-tgzip', '-mx=1', $OutFile, $tar)
            Remove-Item -LiteralPath $tar -Force
            break
        }
        '7zmhe' {
            Run-7z @('a', '-t7z', '-mx=1', ('-p' + $pw), '-mhe=on', $OutFile, (Join-Path $ContentDir '*')) ; break
        }
        '7zdata' {
            Run-7z @('a', '-t7z', '-mx=1', ('-p' + $pw), '-mhe=off', $OutFile, (Join-Path $ContentDir '*')) ; break
        }
        '7zcn' {
            Run-7z @('a', '-t7z', '-mx=1', ('-p' + $pwCn), '-mhe=on', $OutFile, (Join-Path $ContentDir '*')) ; break
        }
        '7zwrong' {
            Run-7z @('a', '-t7z', '-mx=1', ('-p' + $pwWrong), '-mhe=on', $OutFile, (Join-Path $ContentDir '*')) ; break
        }
        'zipaes' {
            Run-7z @('a', '-tzip', '-mx=1', ('-p' + $pw), '-mem=AES256', $OutFile, (Join-Path $ContentDir '*')) ; break
        }
        'zipcrypto' {
            Run-7z @('a', '-tzip', '-mx=1', ('-p' + $pw), $OutFile, (Join-Path $ContentDir '*')) ; break
        }
        default { throw "未知类型：$Kind" }
    }
}

function New-MediaBytes {
    param([string]$Kind, [string]$OutFile)

    switch ($Kind) {
        'png' {
            # 1×1 PNG（以 IEND 结尾）
            $b64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='
            [System.IO.File]::WriteAllBytes($OutFile, [Convert]::FromBase64String($b64))
            break
        }
        'jpg' {
            # 真 JPEG（GDI+ 现画 2×2，保证是合法 JPEG 且以 FF D9 结尾）
            Add-Type -AssemblyName System.Drawing
            $bmp = [System.Drawing.Bitmap]::new(2, 2)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.Clear([System.Drawing.Color]::LightSkyBlue)
                $bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Jpeg)
            }
            finally {
                $g.Dispose()
                $bmp.Dispose()
            }
            break
        }
        'mp4' {
            # ftyp + mdat 两个 box（媒体恰好占满前缀；归档接在 box 之后）
            $ms = [System.IO.MemoryStream]::new()
            $ftyp = [byte[]](0,0,0,0x18) + [System.Text.Encoding]::ASCII.GetBytes('ftypisom') + [byte[]](0,0,2,0) + [System.Text.Encoding]::ASCII.GetBytes('isomiso2')
            $payload = New-Object byte[] 4096
            [System.Random]::new(7).NextBytes($payload)
            $mdatHeader = [byte[]](0,0,0,0) + [System.Text.Encoding]::ASCII.GetBytes('mdat')
            $size = 8 + $payload.Length
            $mdatHeader[0] = [byte](($size -shr 24) -band 0xFF)
            $mdatHeader[1] = [byte](($size -shr 16) -band 0xFF)
            $mdatHeader[2] = [byte](($size -shr 8) -band 0xFF)
            $mdatHeader[3] = [byte]($size -band 0xFF)
            $ms.Write($ftyp, 0, $ftyp.Length)
            $ms.Write($mdatHeader, 0, $mdatHeader.Length)
            $ms.Write($payload, 0, $payload.Length)
            [System.IO.File]::WriteAllBytes($OutFile, $ms.ToArray())
            $ms.Dispose()
            break
        }
        default { throw "未知媒体：$Kind" }
    }
}

function Join-Files {
    param([string[]]$Parts, [string]$OutFile)
    $out = [System.IO.File]::Create($OutFile)
    try {
        foreach ($p in $Parts) {
            $bytes = [System.IO.File]::ReadAllBytes($p)
            $out.Write($bytes, 0, $bytes.Length)
        }
    }
    finally { $out.Close() }
}

# 用 .NET 直接写 zip ⇒ 条目名可以带 `../`、`CON`、结尾空格与点（7z.exe 造不出来）
function New-SafeZip {
    param([string]$OutFile, [string]$ContentDir, [string[]]$Entries)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path $OutFile) { Remove-Item -LiteralPath $OutFile -Force }
    $fs = [System.IO.File]::Create($OutFile)
    $zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($entry in $Entries) {
            # 正常条目：从内容目录取；危险条目：写一段固定文本
            $source = Join-Path $ContentDir ($entry -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            $created = $zip.CreateEntry($entry)
            $es = $created.Open()
            try {
                if (Test-Path -LiteralPath $source -PathType Leaf) {
                    $bytes = [System.IO.File]::ReadAllBytes($source)
                    $es.Write($bytes, 0, $bytes.Length)
                }
                else {
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes("条目：$entry")
                    $es.Write($bytes, 0, $bytes.Length)
                }
            }
            finally { $es.Close() }
        }
    }
    finally {
        $zip.Dispose()
        $fs.Close()
    }
}

# ══════════════════════════ 主循环 ══════════════════════════
$wanted = if ($Sample -eq 'all') { $null } else { $Sample.Split(',') | ForEach-Object { $_.Trim() } }

foreach ($s in $samples) {
    if ($wanted -and ($wanted -notcontains $s.Id)) { continue }

    $dir = Join-Path $Root $s.Id
    if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null

    $contentDir = Join-Path $dir '.content'
    $sizeKb = if ($s.ContainsKey('SizeKb')) { $s.SizeKb } else { 512 }
    New-Content -Dir $contentDir -SizeKb $sizeKb

    $build = $s.Build

    # 最终样本文件的后缀：按"解出来该长什么样"给个正常/合理的名字
    $ext = switch -Wildcard ($build) {
        'volfirst' { '7z' }
        'volcomplete' { '7z' }
        'volenc' { '7z' }
        'nested*' { 'zip' }
        'textzip' { 'zip' }
        'traversal' { 'zip' }
        'reserved' { 'zip' }
        'apk' { 'zip' }
        'puremedia' { 'mp4' }
        'zip*' { 'zip' }
        '7z*' { '7z' }
        'targz' { 'tar.gz' }
        default { 'bin' }
    }
    $outFile = Join-Path $dir ('样本包.' + $ext)
    $recipeMode = if ($s.ContainsKey('Mode')) { $s.Mode } else { 'default' }

    switch -Wildcard ($build) {
        'zip-duplicate' {
            New-SingleArchive -Kind 'zip' -ContentDir $contentDir -OutFile $outFile
            # 第二份同名（落到不同子目录，模拟两份同名包）
            $sub = Join-Path $dir '第二份'
            New-Item -ItemType Directory -Path $sub -Force | Out-Null
            Copy-Item -LiteralPath $outFile -Destination (Join-Path $sub '样本包.zip')
            $s.Expect.Landing = '让位'
            break
        }
        'nested1' {
            $inner = Join-Path $dir '.inner.7z'
            New-SingleArchive -Kind '7z' -ContentDir $contentDir -OutFile $inner
            $wrapDir = Join-Path $dir '.wrap'
            New-Item -ItemType Directory -Path $wrapDir -Force | Out-Null
            Move-Item -LiteralPath $inner -Destination (Join-Path $wrapDir '内层包.7z')
            New-SingleArchive -Kind 'zip' -ContentDir $wrapDir -OutFile $outFile
            break
        }
        'nested3' {
            $inner = Join-Path $dir '.inner.zip'
            New-SingleArchive -Kind 'zip' -ContentDir $contentDir -OutFile $inner
            $wrap1Dir = Join-Path $dir '.wrap1'
            New-Item -ItemType Directory -Path $wrap1Dir -Force | Out-Null
            Move-Item -LiteralPath $inner -Destination (Join-Path $wrap1Dir '第三层.zip')
            $middle = Join-Path $dir '.middle.7z'
            New-SingleArchive -Kind '7z' -ContentDir $wrap1Dir -OutFile $middle
            $wrap2Dir = Join-Path $dir '.wrap2'
            New-Item -ItemType Directory -Path $wrap2Dir -Force | Out-Null
            Move-Item -LiteralPath $middle -Destination (Join-Path $wrap2Dir '第二层.7z')
            New-SingleArchive -Kind 'zip' -ContentDir $wrap2Dir -OutFile $outFile
            break
        }
        'nestedbranch' {
            $a = Join-Path $dir '.a.zip'
            New-SingleArchive -Kind 'zip' -ContentDir $contentDir -OutFile $a
            $b = Join-Path $dir '.b.7z'
            New-SingleArchive -Kind '7z' -ContentDir $contentDir -OutFile $b
            $wrapDir = Join-Path $dir '.wrap'
            New-Item -ItemType Directory -Path $wrapDir -Force | Out-Null
            Move-Item -LiteralPath $a -Destination (Join-Path $wrapDir '分支一.zip')
            Move-Item -LiteralPath $b -Destination (Join-Path $wrapDir '分支二.7z')
            New-SingleArchive -Kind 'zip' -ContentDir $wrapDir -OutFile $outFile
            break
        }
        'volfirst' {
            # 真分卷 2 卷：把 .001 改名成 .7z（= 名字像单卷、内容是分卷首卷）
            $volBase = Join-Path $dir '.vol.7z'
            Run-7z @('a', '-t7z', '-mx=0', '-v1m', $volBase, (Join-Path $contentDir '*'))
            Move-Item -LiteralPath ($volBase + '.001') -Destination $outFile
            Get-ChildItem -LiteralPath $dir -Filter '.vol.7z.*' | Remove-Item -Force
            break
        }
        'volcomplete' {
            # 真·完整的 7z 三卷组（标准卷名、三片全在场）：给第二大步“可证完整 ⇒ 接住 ⇒ 只解一层”用
            $volBase = Join-Path $dir '.vol.7z'
            Run-7z @('a', '-t7z', '-mx=1', '-v1m', $volBase, (Join-Path $contentDir '*'))
            $pieceIndex = 1
            foreach ($piece in Get-ChildItem -LiteralPath $dir -Filter '.vol.7z.*' | Sort-Object Name) {
                Move-Item -LiteralPath $piece.FullName -Destination (Join-Path $dir ("样本包.7z.{0:D3}" -f $pieceIndex))
                $pieceIndex++
            }
            $outFile = Join-Path $dir '样本包.7z.001'
            break
        }
        'nestedvol' {
            # 外层 zip 里装一组 7z 分卷（程序产出）⇒ 第二大步要搬片 + 成功后收片
            $volBase = Join-Path $dir '.inner.7z'
            Run-7z @('a', '-t7z', '-mx=1', '-v1m', $volBase, (Join-Path $contentDir '*'))
            $wrapDir = Join-Path $dir '.wrap'
            New-Item -ItemType Directory -Path $wrapDir -Force | Out-Null
            $pieceIndex = 1
            foreach ($piece in Get-ChildItem -LiteralPath $dir -Filter '.inner.7z.*' | Sort-Object Name) {
                Move-Item -LiteralPath $piece.FullName -Destination (Join-Path $wrapDir ("内层组.7z.{0:D3}" -f $pieceIndex))
                $pieceIndex++
            }
            New-SingleArchive -Kind 'zip' -ContentDir $wrapDir -OutFile $outFile
            break
        }
        'volscatter' {
            # 同一组 7z 分卷散在两个目录：第 1 片在「甲」、其余在「乙」⇒ 第二大步搬片时只建硬链接、原件不动
            $volBase = Join-Path $dir '.vol.7z'
            Run-7z @('a', '-t7z', '-mx=1', '-v1m', $volBase, (Join-Path $contentDir '*'))
            $dirA = Join-Path $dir '甲'
            $dirB = Join-Path $dir '乙'
            New-Item -ItemType Directory -Path $dirA, $dirB -Force | Out-Null
            $pieceIndex = 1
            foreach ($piece in Get-ChildItem -LiteralPath $dir -Filter '.vol.7z.*' | Sort-Object Name) {
                $targetDir = if ($pieceIndex -eq 1) { $dirA } else { $dirB }
                Move-Item -LiteralPath $piece.FullName -Destination (Join-Path $targetDir ("组.7z.{0:D3}" -f $pieceIndex))
                $pieceIndex++
            }
            $outFile = Join-Path $dirA '组.7z.001'
            break
        }
        'volenc' {
            # 完整 7z 三卷组，但密码不在密码本里 ⇒ 第二大步接住后解不开，片必须全部留在入口层
            $volBase = Join-Path $dir '.vol.7z'
            Run-7z @('a', '-t7z', '-mx=1', '-v1m', '-pNotInBook!42', '-mhe=on', $volBase, (Join-Path $contentDir '*'))
            $pieceIndex = 1
            foreach ($piece in Get-ChildItem -LiteralPath $dir -Filter '.vol.7z.*' | Sort-Object Name) {
                Move-Item -LiteralPath $piece.FullName -Destination (Join-Path $dir ("样本包.7z.{0:D3}" -f $pieceIndex))
                $pieceIndex++
            }
            $outFile = Join-Path $dir '样本包.7z.001'
            break
        }
        'textzip' {
            Set-Content -LiteralPath (Join-Path $dir ('样本包.zip')) -Value '这不是压缩包，只是名字像。' -Encoding utf8
            $outFile = Join-Path $dir '样本包.zip'
            break
        }
        'puremedia' {
            New-MediaBytes -Kind 'mp4' -OutFile $outFile
            break
        }
        '7zcrc' {
            New-SingleArchive -Kind '7z' -ContentDir $contentDir -OutFile $outFile
            $bytes = [System.IO.File]::ReadAllBytes($outFile)
            $pos = [int]($bytes.Length / 2)
            for ($i = 0; $i -lt 64 -and ($pos + $i) -lt $bytes.Length; $i++) { $bytes[$pos + $i] = [byte](0x5A -bxor $bytes[$pos + $i]) }
            [System.IO.File]::WriteAllBytes($outFile, $bytes)
            break
        }
        '7ztrunc' {
            New-SingleArchive -Kind '7z' -ContentDir $contentDir -OutFile $outFile
            $bytes = [System.IO.File]::ReadAllBytes($outFile)
            [System.IO.File]::WriteAllBytes($outFile, $bytes[0..([int]($bytes.Length * 0.4))])
            break
        }
        'traversal' {
            New-SafeZip -OutFile $outFile -ContentDir $contentDir -Entries @('说明.txt', '../逃出去.txt', '..\..\再逃一层.txt')
            break
        }
        'reserved' {
            New-SafeZip -OutFile $outFile -ContentDir $contentDir -Entries @('说明.txt', 'CON', '名字带空格 .txt', '结尾点.txt.')
            break
        }
        'apk' {
            New-SafeZip -OutFile $outFile -ContentDir $contentDir -Entries @('AndroidManifest.xml', 'classes.dex', '说明.txt')
            $newName = Join-Path $dir '样本包.apk'
            Move-Item -LiteralPath $outFile -Destination $newName
            $outFile = $newName
            break
        }
        default {
            New-SingleArchive -Kind $build -ContentDir $contentDir -OutFile $outFile
            break
        }
    }

    # 媒体前缀（归档前面接一段真媒体）
    if ($s.ContainsKey('Media')) {
        $media = Join-Path $dir '.media.bin'
        New-MediaBytes -Kind $s.Media -OutFile $media
        $joined = Join-Path $dir '.joined.bin'
        Join-Files -Parts @($media, $outFile) -OutFile $joined
        Remove-Item -LiteralPath $outFile -Force
        Move-Item -LiteralPath $joined -Destination $outFile
        Remove-Item -LiteralPath $media -Force
    }

    # 伪装/改名
    if ($s.ContainsKey('Disguise')) {
        $target = Join-Path $dir $s.Disguise
        if (Test-Path $target) { Remove-Item -LiteralPath $target -Force }
        Move-Item -LiteralPath $outFile -Destination $target
        $outFile = $target
    }

    # 工作目录清掉（样本里只留最终文件）
    foreach ($tmp in @('内容', '.content', '.wrap', '.wrap1', '.wrap2')) {
        $p = Join-Path $dir $tmp
        if (Test-Path $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }

    # 断言默认值：日志里必须记下"文件的信息"（检测行）；⛔ 用户可见文本不许出现 Markdown 加粗
    if (-not $s.Expect.ContainsKey('LogContains')) { $s.Expect.LogContains = if ($s.Expect.Outcome -eq 'Skipped') { @('格式：') } else { @('格式：', '内容卡') } }
    if (-not $s.Expect.ContainsKey('LogForbids')) { $s.Expect.LogForbids = @() }

    # recipe.json（断言随样本走）
    $recipe = [ordered]@{
        id          = $s.Id
        family      = $s.Family
        title       = $s.Title
        seed        = $Seed
        modes       = @('default', 'custom')   # 用户 2026-10-24：两种落点模式都跑
        primaryMode = $recipeMode
        passwordBook = @($pw, $pwCn, '123456')   # ⛔ B06 的密码故意不在本里 ⇒ 全错
        expect      = $s.Expect
    }
    $recipe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dir 'recipe.json') -Encoding utf8

    $files = (Get-ChildItem -LiteralPath $dir -File | ForEach-Object { $_.Name }) -join ', '
    Write-Output ("造好 {0}  [{1}] {2}  ⇒ {3}" -f $s.Id, $s.Family, $s.Title, $files)
}

Write-Output "完成：$Root"
