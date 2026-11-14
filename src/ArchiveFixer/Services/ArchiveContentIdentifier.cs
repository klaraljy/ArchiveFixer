using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// **内容识别**：把"这一份文件到底是什么"从文件头一路问到文件尾（必要时顺序扫），
/// 并把结论落成 <see cref="DetectResult"/>（原文 <c>ArchiveDetectService.DetectAsync</c> 的流水线，判据一条没动）。
///
/// <para>流水线（次序就是判据，⛔ 不许调换）：</para>
/// <list type="number">
/// <item><description>量一次文件身份（大小 + 修改时间）；读 34 KB 文件头；</description></item>
/// <item><description>文件头认识 ⇒ 查 / 存缓存 ⇒ 走加密判读出口 ⇒ 返回（**正常包一个字节都不多读**）；</description></item>
/// <item><description>文件头不认识 ⇒ 看尾部 256 KB 里有没有自洽的 ZIP EOCD（双面文件）；</description></item>
/// <item><description>尾部没命中 ⇒ 顺序扫一遍（上限 <see cref="TailArchiveScanner.MaxScanBytes"/>，命中即停）
/// 同时找 RAR / 7z 魔数与"不在末尾的 ZIP"；</description></item>
/// <item><description>都没有 ⇒ 如实返回"格式未知"。</description></item>
/// </list>
///
/// <para>缓存键里那四样证据（大小 + 修改时间 + 头指纹 + 尾指纹）为什么必须一起量、
/// 为什么加密结论**不进缓存**，见 <see cref="DetectResultCache"/> 的说明。
/// 加密判据仍然只从 <see cref="ArchiveEncryptionVerdict.Apply"/> 出 —— 这里只负责"什么时候问它"。</para>
///
/// <para>注意读入量：文件头 34 KB、认不出来时的文件尾 256 KB、只有尾部那条路也没命中时才顺序扫
/// （上限 512 MiB，命中即停）。**正常包一个字节都不多读。**</para>
/// </summary>
internal sealed class ArchiveContentIdentifier
{
    private static long _probeCalls;

    // 需要读到 ISO 9660 卷描述符位置（偏移 0x8001），所以读取 34KB 文件头。
    private const int HeaderReadLength = 34816;

    /// <summary>
    /// 缓存键里"文件尾指纹"读多少字节（与 <c>EmbeddedArchiveDetector.DefaultTailBytes</c> 同量级）。
    ///
    /// <para>为什么这一档要额外读一次尾部：文件头认不出来时，结论可能出自"尾部内嵌 ZIP"或
    /// "整文件顺序扫"这两条路 —— 而这两条路**都很贵**（顺序扫上限 512 MiB）。
    /// 256 KB 换"相同文件不再扫第二遍"，是这一整套优化里最划算的一笔。</para>
    /// </summary>
    private const int TailFingerprintLength = 262144;

    /// <summary>诊断计数：实际进入内容识别的次数（缓存命中前就计数）。</summary>
    internal static long ProbeCalls => Interlocked.Read(ref _probeCalls);

    internal static void ResetProbeCallsForTests() =>
        Interlocked.Exchange(ref _probeCalls, 0);

    /// <summary>
    /// 要不要用识别结论缓存（默认开）。
    ///
    /// <para>留成**实例**开关而不是静态开关：用例要拿"开缓存的一份"与"关缓存的一份"
    /// 在同一批文件上各跑一遍、逐条对照结论（用户 2026-09-29 任务 B 第 3 条：
    /// 改前改后的识别结果必须逐条相同）。静态开关会串到并行跑的别的用例上。</para>
    ///
    /// <para>公开 API 仍是 <see cref="ArchiveDetectService.UseDetectCache"/>（薄转发到这一条）。</para>
    /// </summary>
    internal bool UseDetectCache { get; set; } = true;

    /// <summary>一份文件的身份（缓存键里"大小 + 修改时间"那两条证据）。</summary>
    private readonly struct DetectFileStamp
    {
        public DetectFileStamp(long length, long lastWriteTicks)
        {
            Length = length;
            LastWriteTicks = lastWriteTicks;
        }

        public long Length { get; }

        public long LastWriteTicks { get; }
    }

    /// <summary>
    /// 识别文件真实格式（原文 <c>ArchiveDetectService.DetectAsync</c>）。
    /// </summary>
    internal async Task<DetectResult> IdentifyAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _probeCalls);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return DetectResultFactory.CreateUnknown("文件路径为空");
        }

        if (!File.Exists(filePath))
        {
            return DetectResultFactory.CreateUnknown("文件不存在");
        }

        try
        {
            /*
             * 先量一次"这一份文件是谁"（字节数 + 修改时间）。
             *
             * 为什么在读文件头**之前**就要量：识别结论会按（大小 + 修改时间 + 内容指纹）进进程内缓存
             * （见 Detection/DetectResultCache，用户 2026-09-29 任务 B），
             * 而那三条证据必须在同一时刻取得，否则一次读写竞争就能把"改之前的内容"配上"改之后的时间"。
             */
            bool identityKnown = TryReadStamp(filePath, out DetectFileStamp before);

            byte[] header = await ReadHeaderAsync(filePath, HeaderReadLength, cancellationToken);

            if (header.Length == 0)
            {
                return DetectResultFactory.CreateUnknown("文件为空");
            }

            /*
             * 文件在读取期间没被动过 → 这一份身份可以用来查/存缓存；动过就整条缓存都不碰
             * （宁可多算一次，也不许把两条不同的内容混成一条结论）。
             */
            bool identityStable = identityKnown
                                  && TryReadStamp(filePath, out DetectFileStamp after)
                                  && after.Length == before.Length
                                  && after.LastWriteTicks == before.LastWriteTicks;

            ulong headFingerprint = DetectResultCache.Fingerprint(header);

            DetectResult headerResult = ArchiveFormatProbe.ProbeHeader(header);

            /*
             * 加密判读（用户 2026-09-29 任务 A 起于 RAR，2026-09-30 扩到 RAR / ZIP / 7z）：
             * 魔数只能告诉我们"这是哪种容器"，"这一包加没加密"写在各自的结构里
             * （RAR4 的 MHD_PASSWORD / LHD_PASSWORD、RAR5 的归档加密头 / File encryption 扩展记录、
             * ZIP 中央目录记录的通用位标志 bit0、7z coder 链里的 AES-256）——
             * 所以这里补一次**只读头尾**的解析。
             *
             * ⛔ **判据只此一处出口**（ArchiveEncryptionVerdict.Apply → Detection/ 下那三个 reader）：
             * 别在识别、解压、界面里各写一套"这包是不是加密的" —— 三套必然漂移，
             * 而漂移的后果是批首那句"本批有 N 个包没有可用密码"与实际跑起来的结果对不上。
             *
             * ⚠ 这一层**不缓存**（缓存里存的是它之前的那份）：它的判据要读头链 / 中央目录 / 头尾，
             * 读到的位置可以越过这 34 KB 文件头，缓存键那四样证据盖不住它 —— 每次现算，代价只有几 KB 的读。
             * （也正因为这样，ZIP 的加密结论从 2026-09-30 起**只由出口给**，文件头那条路里那两处
             * 按"第一个本地头"的粗略判断已经撤掉 —— 它既会漏报、又会把加密标志夹带进缓存。）
             */
            if (headerResult.Format != "Unknown")
            {
                // 文件比读入上限还短 ⇒ 整个文件都在指纹里（结论逐字节可复现）；否则只靠文件头。
                DetectCacheEvidence evidence = header.Length >= before.Length
                    ? DetectCacheEvidence.WholeFile
                    : DetectCacheEvidence.Head;

                DetectCacheKey key = BuildKey(before, headFingerprint, 0UL, evidence);

                if (identityStable
                    && UseDetectCache
                    && DetectResultCache.TryGet(key, out DetectResult cached))
                {
                    return ArchiveEncryptionVerdict.Apply(cached, filePath);
                }

                if (identityStable && UseDetectCache)
                {
                    DetectResultCache.Store(key, headerResult);
                }

                return ArchiveEncryptionVerdict.Apply(headerResult, filePath);
            }

            /*
             * 只有文件头"不认识"时才去看文件尾部。
             *
             * 为什么必须加这个条件：尾部检测要多读 128 KB，而它对已经认出来的格式毫无意义 ——
             * 给每个正常包都多读一次纯属浪费（一批几百个包就是几十 MB 的无用 IO）。
             * 更关键的是**不许**给已识别的格式也去读尾部：自解压安装器（PE 头 + 尾部归档）、
             * 可执行文件后面接数据的形态都很常见，一律去尾部找 ZIP 会大面积误报。
             * 判据用"文件头是否认识"而不是逐个排除格式，所以以后新增魔数也自动受这条保护。
             *
             * 反过来，尾部检测正是"只读文件头"这个设计的补丁：
             * 真实案例里 ZIP 在 17 MB 之后，前 34 KB 全是 MP4 的 ftyp 头，
             * 只看文件头永远得不出结论，这种文件就被判成"格式未知"了。
             *
             * ===== 缓存（第二档）=====
             * 这一档**最贵**：尾部没命中就要顺序扫一遍（上限 512 MiB）。所以：
             * · 文件比已读的头还短（整个文件都在手上）→ 键里放的就是**整个文件**的指纹，结论逐字节可复现；
             * · 否则再读一次文件尾 256 KB 算指纹 —— 这一截本来就要读（尾部内嵌那一条），
             *   而它换来的是"相同文件不再扫第二遍"。
             */
            DetectCacheKey unknownKey = default;
            bool unknownKeyKnown = false;

            if (identityStable && UseDetectCache)
            {
                if (header.Length >= before.Length)
                {
                    unknownKey = BuildKey(before, headFingerprint, 0UL, DetectCacheEvidence.WholeFile);
                    unknownKeyKnown = true;
                }
                else
                {
                    byte[] tail = await ReadTailAsync(filePath, TailFingerprintLength, cancellationToken);

                    if (tail.Length > 0)
                    {
                        unknownKey = BuildKey(
                            before,
                            headFingerprint,
                            DetectResultCache.Fingerprint(tail),
                            DetectCacheEvidence.HeadAndTail);

                        unknownKeyKnown = true;
                    }
                }

                if (unknownKeyKnown && DetectResultCache.TryGet(unknownKey, out DetectResult cachedUnknown))
                {
                    return cachedUnknown;
                }
            }

            /*
             * ===== 名字自报"我是某一组分卷的一片" ⇒ **整段内嵌归档判定不适用** =====
             *
             * <para><b>现场（2026-10-12 大测试困难批，样本 `22222222` 逐字）</b>：顶层三片里
             * `资源222-L2.zip.--p002`（7z 切的 zip 第 2 片）的内容里**恰好含有下一层 RAR 卷的魔数**
             * —— 因为 L2 切的就是 L1 的 RAR 卷 —— 于是顺序扫扫到那个魔数，判成"双面文件"、
             * 从**偏移 162** 抠出 26 214 238 字节（逐字：`检测到内嵌归档：已从偏移 162 处取出 …`）
             * ⇒ 这一片的**原始字节再没被当成这一组的一片** ⇒ 整组凑不齐 ⇒ ①「内容物解出来」红。</para>
             *
             * <para>判据只读**名字**（唯一出口 <see cref="LegacyVolumeNames.IsVolumePart"/>，
             * 与下面"后缀状态"那一处**同一把尺子**）：一片分卷的名字就是它的身份，
             * 而"尾部有自洽归档 / 尾部有魔数"这两条在它身上是**假证据**（切片里本来就可能出现任何字节）。
             * ⛔ 这一档只是**不做**内嵌归档判定（= 少做一次抠取），不改任何其它判据：
             * 头里有魔数（RAR5 各卷、7z 的 `.001`）照旧走头上那条路，一个字节都不受影响。</para>
             */
            bool namedVolumePart = LegacyVolumeNames.IsVolumePart(Path.GetFileName(filePath));

            /*
             * 尾部那 256 KB 里找 ZIP 的 EOCD（最便宜的一条：只读尾部）。
             * 这一条覆盖"视频/图片 + 尾部整包"的全部真实样本（第 33/38 条那批）。
             */
            DetectResult? embeddedResult = namedVolumePart
                ? null
                : DetectEmbeddedArchive(filePath, headerResult);

            if (embeddedResult != null)
            {
                StoreUnknown(unknownKey, unknownKeyKnown, embeddedResult);

                return embeddedResult;
            }

            /*
             * 尾部那条路没命中时，**一遍顺序扫**同时找两样东西（用户 2026-09-25 第 40 条）：
             * ①RAR / 7z 的魔数（第 38 条追加：这两种格式的目录结构不支持从尾部反推起点，
             *   实测前缀 20 MB 时连引擎都报"不是归档"，只能自己定位起点、抠出来再解）；
             * ②ZIP 的 EOCD 候选（第 40 条：ZIP 前面垫了图片、**后面还垫了数据**时，
             *   尾部 256 KB 里根本没有 EOCD，整条"从尾部反推"的路失效）。
             *
             * 为什么合成一遍扫：两条路在"都没命中"时要各读一遍整文件，
             * 一批几十个包就是白读几十遍 —— 一次 IO 拿两个结论，代价只算一次。
             *
             * ⚠ 这一遍就是"识别慢"的大头（实测 2 GiB 认不出格式的文件要 3.5 秒）——
             * 所以上面那道缓存键才必须包含"文件尾 256 KB 的指纹"。
             */
            TailArchiveScanResult scan = TailArchiveScanner.Scan(
                filePath,
                cancellationToken,
                ArchiveFormatProbe.LooksLikeMediaHeader(header)
                    ? 64L * 1024 * 1024
                    : TailArchiveScanner.MaxScanBytes);

            /*
             * 优先级：**自洽校验通过的 ZIP 优先于一个孤零零的魔数**。
             * 理由：魔数只有 7 个字节，它可能只是 ZIP **里面装着的一个 RAR 文件**
             * （打包者常这么套），而 ZIP 候选要过"中央目录签名 + 局部文件头"两条位置校验，
             * 是实打实的"这里有一个 ZIP"（判据见 EmbeddedArchiveDetector，一个字没放宽）。
             */
            if (!namedVolumePart && scan.ZipEocdOffsets.Count > 0)
            {
                EmbeddedArchiveInfo middle = EmbeddedArchiveDetector.DetectAt(filePath, scan.ZipEocdOffsets);

                if (middle.Found)
                {
                    DetectResult middleResult = BuildEmbeddedResult(filePath, middle, headerResult);

                    StoreUnknown(unknownKey, unknownKeyKnown, middleResult);

                    return middleResult;
                }
            }

            if (!namedVolumePart && scan.Archive != null)
            {
                DetectResult tailResult = BuildTailArchiveResult(filePath, scan.Archive, headerResult);

                StoreUnknown(unknownKey, unknownKeyKnown, tailResult);

                return tailResult;
            }

            StoreUnknown(unknownKey, unknownKeyKnown, headerResult);

            return headerResult;
        }
        catch (UnauthorizedAccessException)
        {
            return DetectResultFactory.CreateUnknown("没有权限读取文件");
        }
        catch (IOException ex)
        {
            return DetectResultFactory.CreateUnknown("读取文件失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            return DetectResultFactory.CreateUnknown("识别失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 量一次文件身份；量不到（不存在 / 被占 / 权限）返回 false —— 这时整条缓存都不碰。
    /// </summary>
    private static bool TryReadStamp(string filePath, out DetectFileStamp stamp)
    {
        stamp = default;

        try
        {
            var info = new FileInfo(filePath);

            if (!info.Exists)
            {
                return false;
            }

            stamp = new DetectFileStamp(info.Length, info.LastWriteTimeUtc.Ticks);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DetectCacheKey BuildKey(
        DetectFileStamp stamp,
        ulong headFingerprint,
        ulong tailFingerprint,
        DetectCacheEvidence evidence) => new()
        {
            Length = stamp.Length,
            LastWriteTicks = stamp.LastWriteTicks,
            HeadFingerprint = headFingerprint,
            TailFingerprint = tailFingerprint,
            Evidence = evidence
        };

    /// <summary>把"头不认识"那一档的结论存进缓存（键不可用时什么都不做）。</summary>
    private void StoreUnknown(DetectCacheKey key, bool keyKnown, DetectResult result)
    {
        if (keyKnown && UseDetectCache)
        {
            DetectResultCache.Store(key, result);
        }
    }

    /// <summary>
    /// 读文件**尾部**若干字节（只给缓存键算指纹用）。读不到 / 空文件返回空数组（那就这一档不缓存）。
    ///
    /// <para>文件比这个长度还短时读到的就是整个文件，所以小文件的键天然是"整份内容"。</para>
    /// </summary>
    private static async Task<byte[]> ReadTailAsync(
        string filePath,
        int length,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            long fileLength = stream.Length;

            if (fileLength <= 0)
            {
                return Array.Empty<byte>();
            }

            int want = (int)Math.Min(length, fileLength);

            stream.Seek(fileLength - want, SeekOrigin.Begin);

            byte[] buffer = new byte[want];
            int total = 0;

            while (total < want)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, want - total), cancellationToken);

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total == want ? buffer : buffer.Take(total).ToArray();
        }
        catch
        {
            // 尾部读不到就这一档不缓存：识别本身照旧往下走（绝不因为缓存失败而改变结论）。
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// 安全读取文件头。
    /// </summary>
    private static async Task<byte[]> ReadHeaderAsync(
        string filePath,
        int length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);

        int read = await stream.ReadAsync(buffer.AsMemory(0, length), cancellationToken);

        if (read <= 0)
        {
            return Array.Empty<byte>();
        }

        if (read == length)
        {
            return buffer;
        }

        return buffer.Take(read).ToArray();
    }

    /// <summary>
    /// 文件头不认识时，再看一眼文件尾部有没有"内嵌归档"（双面文件）。
    ///
    /// <para>命中时返回的置信度是 80 而不是 100：结论不是"文件开头就是 ZIP"这种直接证据，
    /// 而是"尾部 EOCD 与中央目录位置自洽、算出来的起点处确实是 PK 03 04"的推断。
    /// 80 也是给后续流程的信号 —— 这种文件要按偏移抠出来才能解压。</para>
    ///
    /// <para>不命中返回 null（而不是一个 IsArchive=false 的结果）：调用方要保留原文件头的 HeaderHex
    /// 与消息，不能被一个"空结果"覆盖掉。</para>
    /// </summary>
    private static DetectResult? DetectEmbeddedArchive(string filePath, DetectResult headerResult)
    {
        EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(filePath);

        return info.Found ? BuildEmbeddedResult(filePath, info, headerResult) : null;
    }

    /// <summary>
    /// 把一条"命中了"的内嵌归档结论落成 <see cref="DetectResult"/>。
    ///
    /// <para>两条路共用这一份：①尾部 256 KB 里找到 EOCD（<see cref="EmbeddedArchiveDetector.Detect"/>）；
    /// ②整文件顺序扫找到 EOCD（<see cref="EmbeddedArchiveDetector.DetectAt"/>，第 40 条）。
    /// 共用是刻意的 —— 直读探查、置信度、文案口径只允许有一处。<b>形状</b>则由
    /// <see cref="DetectResultFactory"/> 统一组装（这里只决定"落哪一档、参数是什么"）。</para>
    /// </summary>
    private static DetectResult BuildEmbeddedResult(string filePath, EmbeddedArchiveInfo info, DetectResult headerResult)
    {
        /*
         * ===== 跨盘 ZIP 的最后一片（用户 2026-10-01 真机：内层 `222.zi删除p`）=====
         *
         * 它与"双面文件"共用同一个检测器（都是"尾部有 EOCD"），但**结论完全不同**：
         *   · 双面文件 = 前面垫着别的数据 + 一个**完整** ZIP ⇒ 要按偏移抠出来才能解（下面那一大段）；
         *   · 跨盘末片 = **整组的一片**，自己没有本地文件头 ⇒ 抠出来也没用，只能按标准卷名跟兄弟重装。
         * 所以这里**先分叉**：分卷这一档不进直读探查（对一片分卷做"抠取直读"没有任何意义），
         * 也**不设** `EmbeddedArchiveOffset`（偏移恒为 0，设了反而会让下游去抠一个不存在的区间）。
         *
         * ⛔ 只在这一档分叉：`IsVolumePart` 只由 EOCD 的盘号字段为真时置起（见检测器里的判据）。
         */
        if (info.IsVolumePart)
        {
            return DetectResultFactory.CreateSpannedZipPart(info.Message, headerResult.HeaderHex);
        }

        /*
         * 顺手做一次**只读**的直读探查（用户 2026-09-24 需求第 7 条）。
         *
         * 为什么要在这里探：空间核算必须在**排任务之前**就知道"这一次要不要那份等大的临时副本"，
         * 否则账面上会一边说"直读省了副本"、一边把它预留出来（用户明确不许）。
         * 代价只有一次尾部读 + 一次中央目录读，而且只对**识别成内嵌归档**的文件做 ——
         * 普通包一个字节都不多读。
         *
         * 探查失败 / 抛异常一律按"不支持"处理（返回 false + 原因），绝不因此让识别失败：
         * 那时解压侧会照旧回落到抠取 + 7z，一切与今天一样。
         */
        EmbeddedZipProbeResult directRead = EmbeddedZipStreamExtractor.Probe(
            filePath,
            info.Offset,
            info.ArchiveEnd);

        /*
         * 加密的 AES 内嵌包（百度网盘那种分享包）在识别阶段**拿不到密码**，探出来是"不支持"；
         * 但它其实只差一个密码 —— 解压那一刻候选密码一到就能直读（见 ExtractionCoordinator）。
         * 所以这里按"能直读"记：空间账面上不该为一笔**根本不会发生**的抠取副本预留空间
         * （40 GB 的双面文件被那笔虚假占用卡在空间门上，是实打实的误拦）。
         * 密码不对时是硬失败（PasswordRejected），同样不会抠副本 —— 这个乐观是准的。
         */
        bool directReadApplies = directRead.Supported || directRead.RequiresPassword;

        /*
         * 内嵌 ZIP 的加密判读（2026-10-01）：以前这里**写死 false** —— 那是**误报"没加密"**。
         *
         * 判据现成：探针在上面已经告诉我们"里面是 AES 条目、这次没给候选密码"（`RequiresPassword`），
         * 那正是"这一份是加密的"这条事实；而 §11.4 的红线是"读不出来一律说不知道，
         * ⛔ 不猜、不误报" —— 写死 false 比"不知道"还糟：它会让下游按"不用密码"去排任务
         * （百度网盘那种分享包本来就是 AES 内嵌，见上面那段注释）。
         *
         * ⛔ 只在这一档给 true：探针**判不出**时 `RequiresPassword` 也是 false，
         * 那时如实落在"没加密"这一档是既有口径（`IsProbablyEncrypted` 是 bool，没有第三态），
         * ⛔ 不许为了"更保守"就把它当成 true —— 那会把一堆普通包误报成加密。
         */
        return DetectResultFactory.CreateEmbeddedZip(
            message: info.Message,
            headerHex: headerResult.HeaderHex,
            offset: info.Offset,
            end: info.ArchiveEnd,
            isProbablyEncrypted: directRead.RequiresPassword,
            directReadSupported: directReadApplies,
            directReadReason: directReadApplies
                ? string.Empty
                : (directRead.PathRejected ? directRead.Message : directRead.Reason));
    }

    /// <summary>
    /// 文件头不认识、尾部那 256 KB 也没命中 ZIP、顺序扫也没找到 RAR / 7z 魔数时，
    /// 把扫出来的归档签名落成识别结论（用户 2026-09-25 第 38 条追加）。
    ///
    /// <para>结论与 ZIP 那条路**同一形状**：<see cref="DetectResult.Format"/> 是真实格式、
    /// <see cref="DetectResult.EmbeddedArchiveOffset"/> 是起点，管线照旧"抠出 [起点, 末尾) → 交给引擎"。</para>
    ///
    /// <para>两点如实说明：①**终点取文件末尾** —— RAR / 7z 没有 ZIP 那种能算出精确终点的结构，
    /// 而两者都能容忍归档结束标记之后的少量尾巴（抠出来的副本照旧能解）；
    /// ②<see cref="DetectResult.EmbeddedDirectReadSupported"/> 一定是 <c>false</c> ——
    /// 内置直读器只会解 ZIP，RAR / 7z 必须走"抠取 + 引擎"。</para>
    /// </summary>
    private static DetectResult BuildTailArchiveResult(string filePath, TailArchiveSignature signature, DetectResult headerResult)
    {
        long end = headerResult.EmbeddedArchiveEnd;

        if (end <= signature.Offset)
        {
            try
            {
                end = new FileInfo(filePath).Length;
            }
            catch
            {
                end = 0;
            }
        }

        DetectResult result = DetectResultFactory.CreateTailArchive(
            format: signature.Format,
            suggestedExtension: signature.SuggestedExtension,
            message: signature.Describe(),
            headerHex: headerResult.HeaderHex,
            offset: signature.Offset,
            end: end);

        /*
         * 尾部内嵌的那一段如果本身是 RAR / 7z，加密判读照样要做 —— 判据仍是同一个出口，
         * 只是把起点挪到抠取偏移上（那一段的头就在那里）。⛔ 不为这条路另写一套判据。
         */
        return ArchiveEncryptionVerdict.Apply(result, filePath, signature.Offset);
    }
}
