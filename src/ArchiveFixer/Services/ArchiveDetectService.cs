using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 压缩包真实格式识别服务。
    /// 
    /// 职责：
    /// 1. 读取文件头。
    /// 2. 通过魔数判断真实格式。
    /// 3. 文件头不认识时，再读文件尾部找"内嵌归档"（双面文件）。
    /// 4. 尾部没命中时，顺序扫一遍找 RAR / 7z 魔数与"不在末尾的 ZIP"（第 38 / 40 条）。
    /// 5. 返回 DetectResult。
    /// 6. 判断后缀状态。
    /// 7. 将识别结果应用到 ArchiveTask。
    /// 
    /// 注意：
    /// 这里只读取文件头（34 KB）和"认识不出来时"的文件尾部（256 KB）；
    /// 只有尾部那条路也没命中时才会顺序扫一遍（上限 512 MiB，命中即停），
    /// **正常包一个字节都不多读**。
    /// </summary>
    public class ArchiveDetectService
    {
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

        /// <summary>
        /// 要不要用识别结论缓存（默认开）。
        ///
        /// <para>留成**实例**开关而不是静态开关：用例要拿"开缓存的一份"与"关缓存的一份"
        /// 在同一批文件上各跑一遍、逐条对照结论（用户 2026-09-29 任务 B 第 3 条：
        /// 改前改后的识别结果必须逐条相同）。静态开关会串到并行跑的别的用例上。</para>
        /// </summary>
        internal bool UseDetectCache { get; set; } = true;

        /// <summary>一份文件的身份（缓存键里"大小 + 修改时间"那两条证据）。</summary>
        private readonly struct FileIdentity
        {
            public FileIdentity(long length, long lastWriteTicks)
            {
                Length = length;
                LastWriteTicks = lastWriteTicks;
            }

            public long Length { get; }

            public long LastWriteTicks { get; }
        }

        /// <summary>量一次文件身份；量不到（不存在 / 被占 / 权限）返回 false —— 这时整条缓存都不碰。</summary>
        private static bool TryReadIdentity(string filePath, out FileIdentity identity)
        {
            identity = default;

            try
            {
                var info = new FileInfo(filePath);

                if (!info.Exists)
                {
                    return false;
                }

                identity = new FileIdentity(info.Length, info.LastWriteTimeUtc.Ticks);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static DetectCacheKey BuildKey(
            FileIdentity identity,
            ulong headFingerprint,
            ulong tailFingerprint,
            DetectCacheEvidence evidence) => new()
            {
                Length = identity.Length,
                LastWriteTicks = identity.LastWriteTicks,
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
        /// 识别文件真实格式。
        /// </summary>
        public async Task<DetectResult> DetectAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return CreateUnknownResult("文件路径为空");
            }

            if (!File.Exists(filePath))
            {
                return CreateUnknownResult("文件不存在");
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
                bool identityKnown = TryReadIdentity(filePath, out FileIdentity before);

                byte[] header = await ReadHeaderAsync(filePath, HeaderReadLength, cancellationToken);

                if (header.Length == 0)
                {
                    return CreateUnknownResult("文件为空");
                }

                /*
                 * 文件在读取期间没被动过 → 这一份身份可以用来查/存缓存；动过就整条缓存都不碰
                 * （宁可多算一次，也不许把两条不同的内容混成一条结论）。
                 */
                bool identityStable = identityKnown
                                      && TryReadIdentity(filePath, out FileIdentity after)
                                      && after.Length == before.Length
                                      && after.LastWriteTicks == before.LastWriteTicks;

                ulong headFingerprint = DetectResultCache.Fingerprint(header);

                DetectResult headerResult = DetectByHeader(header);

                /*
                 * 加密判读（用户 2026-09-29 任务 A 起于 RAR，2026-09-30 扩到 RAR / ZIP / 7z）：
                 * 魔数只能告诉我们"这是哪种容器"，"这一包加没加密"写在各自的结构里
                 * （RAR4 的 MHD_PASSWORD / LHD_PASSWORD、RAR5 的归档加密头 / File encryption 扩展记录、
                 * ZIP 中央目录记录的通用位标志 bit0、7z coder 链里的 AES-256）——
                 * 所以这里补一次**只读头尾**的解析。
                 *
                 * ⛔ **判据只此一处出口**（ApplyEncryptionVerdict → Detection/ 下那三个 reader）：
                 * 别在识别、解压、界面里各写一套"这包是不是加密的" —— 三套必然漂移，
                 * 而漂移的后果是批首那句"本批有 N 个包没有可用密码"与实际跑起来的结果对不上。
                 *
                 * ⚠ 这一层**不缓存**（缓存里存的是它之前的那份）：它的判据要读头链 / 中央目录 / 头尾，
                 * 读到的位置可以越过这 34 KB 文件头，缓存键那四样证据盖不住它 —— 每次现算，代价只有几 KB 的读。
                 * （也正因为这样，ZIP 的加密结论从 2026-09-30 起**只由出口给**，DetectByHeader 里那两处
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
                        return ApplyEncryptionVerdict(cached, filePath);
                    }

                    if (identityStable && UseDetectCache)
                    {
                        DetectResultCache.Store(key, headerResult);
                    }

                    return ApplyEncryptionVerdict(headerResult, filePath);
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
                 * 尾部那 256 KB 里找 ZIP 的 EOCD（最便宜的一条：只读尾部）。
                 * 这一条覆盖"视频/图片 + 尾部整包"的全部真实样本（第 33/38 条那批）。
                 */
                DetectResult? embeddedResult = DetectEmbeddedArchive(filePath, headerResult);

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
                TailArchiveScanResult scan = TailArchiveScanner.Scan(filePath);

                /*
                 * 优先级：**自洽校验通过的 ZIP 优先于一个孤零零的魔数**。
                 * 理由：魔数只有 7 个字节，它可能只是 ZIP **里面装着的一个 RAR 文件**
                 * （打包者常这么套），而 ZIP 候选要过"中央目录签名 + 局部文件头"两条位置校验，
                 * 是实打实的"这里有一个 ZIP"（判据见 EmbeddedArchiveDetector，一个字没放宽）。
                 */
                if (scan.ZipEocdOffsets.Count > 0)
                {
                    EmbeddedArchiveInfo middle = EmbeddedArchiveDetector.DetectAt(filePath, scan.ZipEocdOffsets);

                    if (middle.Found)
                    {
                        DetectResult middleResult = BuildEmbeddedResult(filePath, middle, headerResult);

                        StoreUnknown(unknownKey, unknownKeyKnown, middleResult);

                        return middleResult;
                    }
                }

                if (scan.Archive != null)
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
                return CreateUnknownResult("没有权限读取文件");
            }
            catch (IOException ex)
            {
                return CreateUnknownResult("读取文件失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return CreateUnknownResult("识别失败：" + ex.Message);
            }
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
        private DetectResult BuildTailArchiveResult(string filePath, TailArchiveSignature signature, DetectResult headerResult)
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

            /*
             * 尾部内嵌的那一段如果本身是 RAR / 7z，加密判读照样要做 —— 判据仍是同一个出口，
             * 只是把起点挪到抠取偏移上（那一段的头就在那里）。⛔ 不为这条路另写一套判据。
             */
            return ApplyEncryptionVerdict(
                new DetectResult
                {
                    Format = signature.Format,
                    SuggestedExtension = signature.SuggestedExtension,
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = signature.Describe(),
                    HeaderHex = headerResult.HeaderHex,
                    Confidence = 80,
                    EmbeddedArchiveOffset = signature.Offset,
                    EmbeddedArchiveEnd = end,
                    EmbeddedDirectReadSupported = false,
                    EmbeddedDirectReadReason = "内置直读器只解 ZIP；RAR / 7z 这条走「抠取 + 引擎」"
                },
                filePath,
                signature.Offset);
        }

        /// <summary>
        /// **加密判据的唯一出口**（用户 2026-09-30 从"只认 RAR"扩到 **RAR / ZIP / 7z**）：
        /// 三个格式各自用**只读头尾**的判据器解析（<see cref="RarEncryptionReader"/> /
        /// <see cref="ZipEncryptionReader"/> / <see cref="SevenZipEncryptionReader"/>，
        /// ⛔ 都不调引擎、不引依赖、都有读入量硬上限），读出"有加密证据"才置
        /// <see cref="DetectResult.IsProbablyEncrypted"/>；读不出来（截断 / 布局不符 / 结构藏在压缩流里）
        /// 一律**不报加密**（⛔ 宁可漏报，也不误报 —— 见 <see cref="ArchiveEncryptionState.Unknown"/>）。
        ///
        /// <para><b>为什么必须是"一个方法"</b>：识别、"尾部内嵌归档"、多卷卷组三条路都要给同一个结论，
        /// 三处各写一份判据必然漂移，而漂移的后果是批首那句"本批有 N 个包没有可用密码"与实际跑起来的结果对不上
        /// （AGENTS.md §9.5）。</para>
        ///
        /// <para><paramref name="offset"/>：这一段归档在文件里的起点（内嵌归档用；<c>0</c> = 文件开头就是它）。</para>
        /// <para><paramref name="volumePaths"/>：分卷组的全部卷（**只有 7z 用得上** —— 它的元数据在最后一卷）。
        /// 不给（或只有一卷）时按单文件判读，多卷 7z 会如实落到"不知道"。</para>
        /// </summary>
        private static DetectResult ApplyEncryptionVerdict(
            DetectResult result,
            string filePath,
            long offset = 0,
            IReadOnlyList<string>? volumePaths = null)
        {
            if (result == null)
            {
                // result 为 null 时**原样**把它交回调用方（保持"进来什么、出去什么"），用的是可空抑制。
                return result!;
            }

            ArchiveEncryptionReading? reading;

            if (result.Format == "RAR4" || result.Format == "RAR5")
            {
                reading = RarEncryptionReader.Read(filePath, offset);
            }
            else if (IsZipFamilyFormat(result.Format))
            {
                reading = ZipEncryptionReader.Read(filePath, offset);
            }
            else if (result.Format == "7Z")
            {
                reading = SevenZipEncryptionReader.Read(filePath, offset, volumePaths);
            }
            else
            {
                // 别的格式这一轮不判（GZIP / BZIP2 / XZ / TAR / Unknown…），照旧"如实不报"。
                return result;
            }

            if (reading == null || !reading.IsEncrypted)
            {
                return result;
            }

            string note = ResolveEncryptionNote(result.Format, reading.State);

            /*
             * 注脚只追加一次：同一个 result 可能被判两次（识别那一遍 + 拿到卷组后再走一遍，
             * 见 ApplyDetectResultAsync），⛔ 不许出现两个注脚叠在一起。
             */
            if (!result.Message.Contains(note, StringComparison.Ordinal))
            {
                result.Message += note;
            }

            result.IsProbablyEncrypted = true;

            return result;
        }

        /// <summary>
        /// 加密结论对应的**用户可见注脚**（照 RAR 那两条的写法：一句中文，强调用「」）。
        ///
        /// <para>ZIP 只有一条：我们的判据是"至少一个条目置了 bit0"，分不出"头也加密"这一档
        /// （ZIP 的中央目录本身不加密，ZipCrypto / AES 都只盖条目数据）。</para>
        /// </summary>
        private static string ResolveEncryptionNote(string format, ArchiveEncryptionState state)
        {
            if (IsZipFamilyFormat(format))
            {
                return StatusText.DetectZipEncryptedNote;
            }

            return state == ArchiveEncryptionState.HeadersEncrypted
                ? StatusText.DetectSevenZipHeadersEncryptedNote
                : StatusText.DetectSevenZipDataEncryptedNote;
        }

        /// <summary>
        /// 文件头不认识时，再看一眼文件尾部有没有"内嵌归档"（双面文件）。
        ///
        /// 命中时返回的置信度是 80 而不是 100：结论不是"文件开头就是 ZIP"这种直接证据，
        /// 而是"尾部 EOCD 与中央目录位置自洽、算出来的起点处确实是 PK 03 04"的推断。
        /// 80 也是给后续流程的信号 —— 这种文件要按偏移抠出来才能解压。
        ///
        /// 不命中返回 null（而不是一个 IsArchive=false 的结果）：调用方要保留原文件头的 HeaderHex
        /// 与消息，不能被一个"空结果"覆盖掉。
        /// </summary>
        private static DetectResult? DetectEmbeddedArchive(string filePath, DetectResult headerResult)
        {
            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(filePath);

            return info.Found ? BuildEmbeddedResult(filePath, info, headerResult) : null;
        }

        /// <summary>
        /// 把一个"命中了的"内嵌归档结论落成 <see cref="DetectResult"/>。
        ///
        /// <para>两条路共用这一份：①尾部 256 KB 里找到 EOCD（<see cref="EmbeddedArchiveDetector.Detect"/>）；
        /// ②整文件顺序扫找到 EOCD（<see cref="EmbeddedArchiveDetector.DetectAt"/>，第 40 条）。
        /// 共用是刻意的 —— 直读探查、置信度、文案口径只允许有一处。</para>
        /// </summary>
        private static DetectResult BuildEmbeddedResult(string filePath, EmbeddedArchiveInfo info, DetectResult headerResult)
        {
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

            return new DetectResult
            {
                Format = "ZIP",
                SuggestedExtension = ".zip",
                IsArchive = true,
                IsKnownFormat = true,
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
                IsProbablyEncrypted = directRead.RequiresPassword,
                Message = info.Message,
                HeaderHex = headerResult.HeaderHex,
                Confidence = 80,
                EmbeddedArchiveOffset = info.Offset,
                EmbeddedArchiveEnd = info.ArchiveEnd,
                EmbeddedDirectReadSupported = directReadApplies,
                EmbeddedDirectReadReason = directReadApplies
                    ? string.Empty
                    : (directRead.PathRejected ? directRead.Message : directRead.Reason)
            };
        }

        /// <summary>
        /// 「这一份只是分卷组的后续卷」时给用户的那句话（不是这种形状就返回空串）。
        ///
        /// <para>判据（全部来自名字 + 一次"同目录里在不在"的检查）：</para>
        /// <list type="number">
        /// <item><description>名字里带卷号、而且**不是第 1 卷**（第 1 卷没有魔数也一样能开，正是被这条排除的）；</description></item>
        /// <item><description>从它推得出**标准首卷名**（推不出就不猜，退回"格式未知"）。</description></item>
        /// </list>
        ///
        /// <para>句子分两档：首卷**就在同目录里** → 让用户把它也加进来；同目录也没有 → 如实说没找到。
        /// ⛔ 不说"文件损坏"，也不说"格式不支持" —— 那两句都会把人引到错的方向。</para>
        /// </summary>
        private static string BuildLaterVolumeOnlyNote(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                string fileName = Path.GetFileName(path);
                int? index = VolumeGroupDetector.TryGetVolumeIndex(fileName);

                if (index == null || index.Value < 2)
                {
                    return string.Empty;
                }

                string? firstVolumeName = VolumeGroupDetector.TryGetFirstVolumeName(fileName);

                if (string.IsNullOrWhiteSpace(firstVolumeName))
                {
                    return string.Empty;
                }

                string directory = Path.GetDirectoryName(path) ?? string.Empty;
                bool firstVolumeIsNextToIt = !string.IsNullOrWhiteSpace(directory)
                                             && File.Exists(Path.Combine(directory, firstVolumeName!));

                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.LaterVolumeOnlyFormat,
                    fileName,
                    index.Value,
                    firstVolumeName,
                    firstVolumeIsNextToIt
                        ? StatusText.LaterVolumeOnlyFirstVolumeHere
                        : StatusText.LaterVolumeOnlyFirstVolumeMissing);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 扫描期算一次"这一卷的名字要不要修"（见 <c>ApplyDetectResultAsync</c> 里那段注释）。
        ///
        /// <para>只有"第 1 卷"才可能修，所以先用卷号挡一道（省掉绝大多数任务的目录枚举）；
        /// 任何 IO 意外都落成空串（= 按钮不亮），⛔ 绝不抛。</para>
        /// </summary>
        private static string ResolveVolumeRenameSuggestion(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                string fileName = Path.GetFileName(path);

                if (VolumeGroupDetector.TryGetVolumeIndex(fileName) != 1)
                {
                    return string.Empty;
                }

                VolumeNameRepairPlan plan = VolumeNameRepair.Plan(
                    path,
                    VolumeNameRepair.EnumerateFileNamesInDirectory(path));

                return plan.CanRepair ? plan.SuggestedFileName : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 识别并应用到任务。
        /// </summary>
        public async Task<DetectResult> ApplyDetectResultAsync(
            ArchiveTask task,
            CancellationToken cancellationToken = default)
        {
            if (task == null)
            {
                return CreateUnknownResult("任务为空");
            }

            task.Operation = StatusText.OpScan;
            task.Status = StatusText.Scanning;
            task.ProgressText = StatusText.ProgressProcessing;
            task.ErrorMessage = string.Empty;
            task.LastUpdatedTime = DateTime.Now;

            DetectResult result = await DetectAsync(task.CurrentPath, cancellationToken);

            string currentExtension = ExtensionHelper.GetLastExtensionDisplay(task.CurrentPath);

            task.FileName = Path.GetFileName(task.CurrentPath);
            task.DirectoryPath = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;
            task.CurrentExtension = currentExtension;

            task.DetectedFormat = result.Format;
            task.SuggestedExtension = result.SuggestedExtension;
            task.ExtensionStatus = GetExtensionStatus(
                task.FileName,
                currentExtension,
                result);

            task.IsArchive = result.IsArchive;

            /*
             * 加密结论 = **第 1 卷（= task.CurrentPath）那一份的结论**，多卷 RAR 上也是这样。
             *
             * 为什么必须说清这一条：RAR 的主头与第一个文件头都在**第 1 卷**里，后续卷是数据的中段，
             * 直接读它必然得不出结论（RAR4 的续卷连主头都读不像、RAR5 的续卷是第一卷头链的续写）。
             * 所以判据只问 `task.CurrentPath`（分卷归组保证了它就是第一卷），
             * ⛔ **绝不去遍历 VolumePaths 再要求"每一卷都报加密"** ——
             * 那会把"续卷读不出来"当成"没加密"，整组加密包当场判否（用户 2026-09-29 明确点名的坑）。
             *
             * ===== 多卷 7z：只有它需要卷组信息（用户 2026-09-30）=====
             * 7z 分卷是**原样切**的（<c>-v100k</c> 切出来的每一卷就是整包的一段连续字节），
             * 元数据在**最后一卷** —— 所以"只看第 1 卷"读不出 7z 的加密（实测 vol-p.7z 的第 1 卷里
             * <c>32 + NextHeaderOffset + NextHeaderSize</c> 直接超出卷长，判据器如实报"不知道"）。
             *
             * 卷组信息**这时候已经填好了**（`FileScanService.ScanPathsAsync` 里
             * `VolumeGroupingService.ApplyVolumeGrouping` 在归组，`ScanCoordinator.ScanTasksAsync`
             * 之后才调识别），所以这里带卷组**再走一遍同一个出口** —— ⛔ 不为多卷另写一套判据，
             * 出口仍然只有 <see cref="ApplyEncryptionVerdict"/> 一个方法。
             *
             * 单文件任务（卷组只有它自己）不重算：DetectAsync 那一遍问的就是同一个问题，
             * 结果必然一样，白读一次没有意义。
             */
            if (result.IsArchive && task.VolumePaths.Count > 1)
            {
                result = ApplyEncryptionVerdict(result, task.CurrentPath, 0, task.VolumePaths);
            }

            task.IsEncrypted = result.IsProbablyEncrypted;

            // 内嵌归档偏移必须落到任务上：解压管线靠它决定"要不要先抠出来"。
            // 终点（EOCD + 22 + 注释长度）同样要落：真实文件在 EOCD 之后还有十几 KB 正常数据，
            // 抠取按终点截断，不能拿"文件末尾"当终点。
            // 直读探查结论一并落下来：**只用于空间核算**（直读时那笔抠取副本不预留）。
            task.EmbeddedArchiveOffset = result.EmbeddedArchiveOffset;
            task.EmbeddedArchiveEnd = result.EmbeddedArchiveEnd;
            task.EmbeddedDirectReadSupported = result.EmbeddedDirectReadSupported;
            task.EmbeddedDirectReadReason = result.EmbeddedDirectReadReason ?? string.Empty;

            /*
             * 「修复分卷名并重试」那颗建议（用户 2026-09-25 第 41 条；**2026-09-26 审计改口径**）。
             *
             * 以前这里是无条件清空（那建议是"上一轮解压"的产物，重扫之后可能已经改好了），
             * 后果是那颗按钮**要先跑一次失败的解压才可能亮** —— 用户从没见过它亮过。
             *
             * 现在改成**扫描时就从文件系统事实算一遍**（判据是 VolumeNameRepair.Plan 那六条：
             * 源文件在 / 名字带卷号且是第 1 卷 / 同目录有后续卷 / 推得出标准名 / 现名不是标准名 /
             * 目标名没被占）——"名字被改坏的第一卷"这六条当场就成立，于是**扫完按钮就是亮的**；
             * 名字本来就标准、或者是后续卷、或者目标名被占时，算出来是空，按钮照旧熄灭
             * （问题解决了它自己灭，这条语义一个字没变）。
             */
            task.VolumeRenameSuggestion = ResolveVolumeRenameSuggestion(task.CurrentPath);

            if (result.IsArchive)
            {
                task.Status = StatusText.Recognized;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressCompleted;
                task.ErrorMessage = string.Empty;
            }
            else
            {
                /*
                 * 「列表里只有分卷组的后续卷」这一档（用户 2026-09-26 拍板："可以"）。
                 *
                 * 现场（真机代跑时撞到）：一组分卷的**第 1 卷没被加进来**（或名字被改坏、当时不在目录里），
                 * 于是 `set.7z.002` 自己成了一个任务 —— 它的文件头里**没有归档魔数**（那只是数据流的中段），
                 * 识别必然报「格式未知 / 未识别为支持的压缩格式」，用户看到会以为文件坏了；
                 * 可它其实是一组分卷的后续卷，该做的是"把第 1 卷也加进来"。
                 *
                 * ⛔ 判据全在**名字**上（分卷号 ≥ 2 + 推得出标准首卷名），**不改格式结论**
                 * （DetectedFormat 仍然是 Unknown —— 那是识别层的诚实结论），只把状态与那句话换对。
                 */
                string laterVolumeNote = BuildLaterVolumeOnlyNote(task.CurrentPath);

                if (!string.IsNullOrWhiteSpace(laterVolumeNote))
                {
                    task.Status = StatusText.VolumeMissing;
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.ErrorMessage = laterVolumeNote;
                }
                else
                {
                    task.Status = StatusText.UnknownFormat;
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.ErrorMessage = result.Message;
                }
            }

            task.LastUpdatedTime = DateTime.Now;

            return result;
        }

        /// <summary>
        /// 获取后缀状态。
        /// 
        /// 规则：
        /// 如果是内嵌归档（尾部藏着 ZIP）：
        ///     内嵌归档（**这一条最先判**：这种文件不该改名）
        /// 如果 Unknown：
        ///     格式未知
        /// 如果没有后缀：
        ///     后缀缺失
        /// 如果最后后缀等于建议后缀：
        ///     后缀正常
        /// 如果存在多个后缀，并且其中一个后缀等于建议后缀，但最后后缀不等于建议后缀：
        ///     多重后缀疑似伪装
        /// 否则：
        ///     后缀不匹配
        /// </summary>
        public string GetExtensionStatus(
            string fileName,
            string currentExtension,
            DetectResult result)
        {
            /*
             * 内嵌归档排在最前面判，而且**优先于一切后缀结论**。
             *
             * 理由：这种文件"没有可修正的后缀"。它的 ZIP 内部偏移相对它自己，前置数据又远超
             * 7-Zip 的容忍上限（实测 8 MiB），所以改成 .zip 之后 7z 依然打不开；
             * 报成"后缀缺失 / 不匹配 / 多重伪装"都会把用户引向改名这条路 —— 那是一条死路。
             * 它该看到的是"内嵌归档：需要按偏移取出"，所以这一条必须在其它判断之前返回。
             */
            if (result != null && result.EmbeddedArchiveOffset > 0)
            {
                return StatusText.ExtensionEmbedded;
            }

            /*
             * 分卷文件的"后缀"是 .001 / .z01 / .part1 这类卷标记，不是伪装，也不是漏写后缀。
             * 设计.md §七 要求把它单独归一类；报成"多重后缀疑似伪装"会误导用户去"修正"它，
             * 而修正分卷名会直接切断分卷链。
             *
             * ⚠ 这一条排在"格式未知"**之前**（用户 2026-09-26 拍板）：后续卷（`set.7z.002`）的文件头里
             * 没有归档魔数，识别必然说 Unknown —— 可它的**后缀**一点都不神秘，就是分卷标记。
             * 报成"格式未知"会把"缺首卷"这件事藏起来（状态那一列现在会说清，见 ApplyDetectResultAsync）。
             */
            if (FileNameHelper.IsVolumePartFileName(fileName))
            {
                return StatusText.ExtensionVolume;
            }

            if (result == null || !result.IsArchive || !result.IsKnownFormat)
            {
                return StatusText.UnknownFormat;
            }

            string suggestedExtension = ExtensionHelper.NormalizeExtension(result.SuggestedExtension);

            if (string.IsNullOrWhiteSpace(suggestedExtension))
            {
                return StatusText.UnknownFormat;
            }

            if (string.IsNullOrWhiteSpace(currentExtension) ||
                currentExtension == "无")
            {
                return StatusText.ExtensionMissing;
            }

            currentExtension = ExtensionHelper.NormalizeExtension(currentExtension);

            /*
             * 本身就是 ZIP 容器的合法后缀（.apk / .jar / .docx / .xlsx / .epub …）：
             * 内容是 ZIP，但后缀本来就是对的 —— 不许判成"后缀不匹配"。
             * 否则「智能修正后缀」会把 xxx.apk 改成 xxx.zip，把安装包变成打不开的压缩包。
             */
            if (IsZipFamilyFormat(result.Format) &&
                ExtensionHelper.IsZipContainerExtension(currentExtension))
            {
                return StatusText.ExtensionNormal;
            }

            if (string.Equals(
                    currentExtension,
                    suggestedExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return StatusText.ExtensionNormal;
            }

            if (IsMultiExtensionSuspicious(fileName, suggestedExtension))
            {
                return StatusText.ExtensionMultiFake;
            }

            return StatusText.ExtensionMismatch;
        }

        /// <summary>ZIP 家族（含空 ZIP 与分卷 ZIP）—— 它们的容器都是 ZIP 结构。</summary>
        private static bool IsZipFamilyFormat(string? format)
        {
            return format is "ZIP" or "ZIP_EMPTY" or "ZIP_SPANNED";
        }

        /// <summary>
        /// 判断是否是多重后缀疑似伪装。
        /// 例如：
        /// 111.7z.jpg 检测为 7Z，则 true。
        /// 111.rar.pdf.jpg 检测为 RAR，则 true。
        /// </summary>
        public bool IsMultiExtensionSuspicious(
            string fileName,
            string suggestedExtension)
        {
            return FileNameHelper.IsMultiExtensionSuspicious(fileName, suggestedExtension);
        }

        /// <summary>
        /// 根据格式获取建议后缀。
        /// </summary>
        public string GetSuggestedExtension(string format)
        {
            return ExtensionHelper.GetSuggestedExtensionByFormat(format);
        }

        /// <summary>
        /// 根据文件头识别格式。
        /// </summary>
        private DetectResult DetectByHeader(byte[] header)
        {
            if (header == null || header.Length == 0)
            {
                return CreateUnknownResult("文件头为空");
            }

            string headerHex = SafePathHelper.ToHexString(header, 64);

            // 7Z:
            // 37 7A BC AF 27 1C
            if (StartsWith(header, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C))
            {
                return new DetectResult
                {
                    Format = "7Z",
                    SuggestedExtension = ".7z",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 7Z 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // RAR4:
            // 52 61 72 21 1A 07 00
            if (StartsWith(header, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00))
            {
                return new DetectResult
                {
                    Format = "RAR4",
                    SuggestedExtension = ".rar",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 RAR4 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // RAR5:
            // 52 61 72 21 1A 07 01 00
            if (StartsWith(header, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00))
            {
                return new DetectResult
                {
                    Format = "RAR5",
                    SuggestedExtension = ".rar",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 RAR5 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // ZIP:
            // PK 03 04 普通 ZIP
            // PK 05 06 空 ZIP
            // PK 07 08 分卷或数据描述符相关
            if (StartsWith(header, 0x50, 0x4B, 0x03, 0x04))
            {
                return new DetectResult
                {
                    Format = "ZIP",
                    SuggestedExtension = ".zip",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 ZIP 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            if (StartsWith(header, 0x50, 0x4B, 0x05, 0x06))
            {
                return new DetectResult
                {
                    Format = "ZIP_EMPTY",
                    SuggestedExtension = ".zip",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为空 ZIP 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            if (StartsWith(header, 0x50, 0x4B, 0x07, 0x08))
            {
                return new DetectResult
                {
                    Format = "ZIP_SPANNED",
                    SuggestedExtension = ".zip",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 ZIP 分卷或特殊 ZIP",
                    HeaderHex = headerHex,
                    Confidence = 90
                };
            }

            // GZIP:
            // 1F 8B
            if (StartsWith(header, 0x1F, 0x8B))
            {
                return new DetectResult
                {
                    Format = "GZIP",
                    SuggestedExtension = ".gz",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 GZIP 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // BZIP2:
            // 42 5A 68 = BZh
            if (StartsWith(header, 0x42, 0x5A, 0x68))
            {
                return new DetectResult
                {
                    Format = "BZIP2",
                    SuggestedExtension = ".bz2",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 BZIP2 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // XZ:
            // FD 37 7A 58 5A 00
            if (StartsWith(header, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00))
            {
                return new DetectResult
                {
                    Format = "XZ",
                    SuggestedExtension = ".xz",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 XZ 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // TAR:
            // ustar 出现在偏移 257。
            if (IsTar(header))
            {
                return new DetectResult
                {
                    Format = "TAR",
                    SuggestedExtension = ".tar",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 TAR 归档文件",
                    HeaderHex = headerHex,
                    Confidence = 90
                };
            }

            // CAB:
            // 4D 53 43 46 = MSCF
            if (StartsWith(header, 0x4D, 0x53, 0x43, 0x46))
            {
                return new DetectResult
                {
                    Format = "CAB",
                    SuggestedExtension = ".cab",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 CAB 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // ARJ:
            // 60 EA
            if (StartsWith(header, 0x60, 0xEA))
            {
                return new DetectResult
                {
                    Format = "ARJ",
                    SuggestedExtension = ".arj",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 ARJ 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // LZH:
            // 2D 6C 68 = -lh
            if (StartsWith(header, 0x2D, 0x6C, 0x68))
            {
                return new DetectResult
                {
                    Format = "LZH",
                    SuggestedExtension = ".lzh",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 LZH 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // Z（Unix compress）:
            // 1F 9D
            if (StartsWith(header, 0x1F, 0x9D))
            {
                return new DetectResult
                {
                    Format = "Z",
                    SuggestedExtension = ".z",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 Z 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // Zstandard:
            // 28 B5 2F FD
            if (StartsWith(header, 0x28, 0xB5, 0x2F, 0xFD))
            {
                return new DetectResult
                {
                    Format = "ZSTD",
                    SuggestedExtension = ".zst",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 Zstandard 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // LZ4:
            // 04 22 4D 18
            if (StartsWith(header, 0x04, 0x22, 0x4D, 0x18))
            {
                return new DetectResult
                {
                    Format = "LZ4",
                    SuggestedExtension = ".lz4",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 LZ4 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // RPM:
            // ED AB EE DB
            if (StartsWith(header, 0xED, 0xAB, 0xEE, 0xDB))
            {
                return new DetectResult
                {
                    Format = "RPM",
                    SuggestedExtension = ".rpm",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 RPM 软件包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // ISO 9660:
            // 卷描述符 "CD001" 位于偏移 0x8001（扇区 16 起始处）。
            if (IsIso9660(header))
            {
                return new DetectResult
                {
                    Format = "ISO",
                    SuggestedExtension = ".iso",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 ISO 光盘镜像",
                    HeaderHex = headerHex,
                    Confidence = 90
                };
            }

            return new DetectResult
            {
                Format = "Unknown",
                SuggestedExtension = string.Empty,
                IsArchive = false,
                IsKnownFormat = false,
                IsProbablyEncrypted = false,
                Message = "未识别为支持的压缩格式",
                HeaderHex = headerHex,
                Confidence = 0
            };
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
        /// 判断文件头是否以指定字节开头。
        /// </summary>
        private static bool StartsWith(byte[] data, params byte[] signature)
        {
            if (data == null || signature == null)
            {
                return false;
            }

            if (data.Length < signature.Length)
            {
                return false;
            }

            for (int i = 0; i < signature.Length; i++)
            {
                if (data[i] != signature[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 判断 TAR。
        /// TAR 的 ustar 标记通常位于偏移 257。
        /// </summary>
        private static bool IsTar(byte[] header)
        {
            if (header == null || header.Length < 262)
            {
                return false;
            }

            return header[257] == (byte)'u' &&
                   header[258] == (byte)'s' &&
                   header[259] == (byte)'t' &&
                   header[260] == (byte)'a' &&
                   header[261] == (byte)'r';
        }

        /// <summary>
        /// 判断是否为 ISO 9660 镜像。
        /// 主卷描述符的 "CD001" 标记位于文件偏移 0x8001。
        /// </summary>
        private static bool IsIso9660(byte[] header)
        {
            if (header == null || header.Length < 0x8001 + 5)
            {
                return false;
            }

            return header[0x8001] == (byte)'C' &&
                   header[0x8002] == (byte)'D' &&
                   header[0x8003] == (byte)'0' &&
                   header[0x8004] == (byte)'0' &&
                   header[0x8005] == (byte)'1';
        }

        /// <summary>
        /// 创建 Unknown 结果。
        /// </summary>
        private static DetectResult CreateUnknownResult(string message)
        {
            return new DetectResult
            {
                Format = "Unknown",
                SuggestedExtension = string.Empty,
                IsArchive = false,
                IsKnownFormat = false,
                IsProbablyEncrypted = false,
                Message = message ?? StatusText.UnknownFormat,
                HeaderHex = string.Empty,
                Confidence = 0
            };
        }
    }
}
