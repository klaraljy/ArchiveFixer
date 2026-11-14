using ArchiveFixer.Helpers;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// **底层识别的唯一出口**：<c>FileNature.Inspect(路径)</c> ⇒ <see cref="FileNatureCard"/>。
///
/// <para>⛔ 上面任何一处判据要问"这一份是什么"，都走这里；⛔ 不许再自己拼名字、自己读魔数、自己判分卷。
/// 它判不出（文件不在 / 读不动）时**照样返回一张卡**（各项落成"判不出"），由调用方按"判不出 ⇒ 什么都不做"办。</para>
/// </summary>
public static class FileNature
{
    /// <summary>
    /// **自述片数的合理性上限**（2026-10-14 实测定的闸）。
    ///
    /// <para>为什么必须有：垃圾头也会"自述"一个荒谬的整包大小 —— 实测那份 `broken.7z`
    /// （7z 魔数 + 随机字节、4096 字节）读出 `自述整包=1252871390030289531 字节`、
    /// `按内容算的片数=2118287382` ⇒ 卡上会写 `VolumeFirstPiece` ⇒ 下游把一份**坏包**
    /// 报成「分卷缺失」（方向完全相反）。</para>
    ///
    /// <para>取值：用户口径是「分卷数目很少大于 5」；这里放到 **1024** —— 是"别把合理的组挡掉"的宽档，
    /// 只用来挡"一眼就知道是噪声"的量级（21 亿片、1.25 EB）。⛔ 它不是"分卷数上限"这条业务判据
    /// （那条判据不在这里、也不存在）。</para>
    /// </summary>
    private const long MaxSaneDeclaredVolumeCount = 1024;

    private const int MaxCacheEntries = 4096;

    /// <summary>
    /// 同一路径 + 大小 + 修改时间的卡片缓存：一次扫描里同一个文件会被多条判据问到
    /// （产物扫描、组分析、轮判定），不缓存就会重复读头/尾/卷号。
    /// ⛔ 只缓存完整卡片；文件变化（大小或 mtime）立即失效。
    /// </summary>
    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, FileNatureCard Card)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<string> CacheOrder = new();

    public static FileNatureCard Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new FileNatureCard { Path = path ?? string.Empty };
        }

        long size;
        long lastWriteTicks;

        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return new FileNatureCard { Path = path };
        }

        if (Cache.TryGetValue(path, out (long Length, long Ticks, FileNatureCard Card) cached)
            && cached.Length == size
            && cached.Ticks == lastWriteTicks)
        {
            return cached.Card;
        }

        string name = FileNameHelper.GetFileName(path);
        VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

        bool hasIdentity = FileIdentity.TryGetIdentity(path, out ulong volumeSerial, out ulong fileIndex);

        LegacyVolumeNames.TryGetCanonicalName(name, out string canonical);

        /*
         * ===== ②b 专属算法：**单一 还是 分卷**（用户 2026-10-13 口径）=====
         *
         * 7z 那一族格式里没有卷号（见 <see cref="VolumeNumberFromContent"/> 的能力矩阵），
         * 于是"这一份到底是什么"只能由**起始头那句明文自述**回答：
         * 自述整包字节数 **≤** 自己 ⇒ 它就是**一份完整的单卷归档**；
         * 自述整包字节数 **>** 自己 ⇒ 它是**这一组的第一片**，而且**片数**也读得出来
         * （整包 ÷ 这一片，向上取整 —— 7z 分卷除末片外等大）。
         * ⛔ 中间片（没有魔数、读不出起始头）照旧"判不出" —— ⛔ 不许当"不是分卷"。
         */
        /*
         * `reading.Format` 已经包含"魔数认不出但尾部 EOCD 认出 ZIP"的兜底
         * （见 VolumeNumberFromContent.Read）；这里不再重复 Read 一遍。
         */
        VolumeContentFormat format = reading.Format != VolumeContentFormat.Unknown
            ? reading.Format
            : VolumeContentInference.SniffFormat(path);
        ArchiveBodyKind bodyKind = ArchiveBodyKind.Unknown;
        long declaredTotalBytes = 0;
        int declaredVolumeCount = 0;

        if (format == VolumeContentFormat.SevenZip
            && SevenZipStartHeader.TryReadExpectedTotalBytes(path, out long expectedTotal)
            && expectedTotal > 0)
        {
            declaredTotalBytes = expectedTotal;

            if (size > 0)
            {
                if (expectedTotal <= size)
                {
                    bodyKind = ArchiveBodyKind.SingleComplete;
                }
                else
                {
                    /*
                     * 7z 格式没有“共几片”字段；原来的 ceil(整包/首卷大小) 只在等大卷时成立。
                     * 非均匀分卷（首卷 18 MiB、第二卷 124 MiB）会被算成 8 片，
                     * 两卷齐全的组因此被误判 Missing。
                     * 现在只保留首卷身份 + 自述整包字节数；片数交给族算法用入口/名字/已收片数推。
                     * 自述字节数的荒谬值仍用“最多 1024 个首卷大小”做上界闸门。
                     */
                    if (expectedTotal / size <= MaxSaneDeclaredVolumeCount)
                    {
                        bodyKind = ArchiveBodyKind.VolumeFirstPiece;
                    }
                }
            }
        }
        else if (reading.IsVolumeMember && reading.Number is int contentNumber && contentNumber > 1)
        {
            // RAR5 / 跨盘 ZIP 那些"内容里就写着卷号"的族：> 1 ⇒ 它是这一组的一片（不是第一片）。
            bodyKind = ArchiveBodyKind.VolumePiece;
        }
        else if (reading.IsVolumeMember && reading.HasZipLocalHeader)
        {
            /*
             * **跨盘 ZIP 的第 1 片**：内容里开头有本地文件头（允许前面先出现 4 字节
             * `PK\x07\x08` 跨盘标记）。它没有 EOCD、没有盘号 ⇒ 以前停在 Unknown，
             * 组里丢第 1 槽（真机 940 批 `16161616`）。
             */
            bodyKind = ArchiveBodyKind.VolumeFirstPiece;
        }
        else if (reading.IsVolumeMember && reading.Number == 1)
        {
            /*
             * ⚠ 2026-10-13（第十四轮读数之后的专属算法补全）：**RAR5 的第 1 卷** ——
             * 官方布局里"第 1 卷没有卷号字段"，所以 <see cref="VolumeNumberFromContent"/> 补 1
             * （`ReadRar5` 的 `number = 1` 初值）。这一条以前没人写 ⇒ 第 1 卷停在 `Unknown`，
             * 而它恰恰是"整组入口那一份"（非 7z 族按内容补片时唯一的锚点）。
             */
            bodyKind = ArchiveBodyKind.VolumeFirstPiece;
        }
        else if (reading.IsVolumeMember && reading.FirstVolumeFlag)
        {
            /*
             * **RAR4**：主头 flags 里的"第 1 卷"标记（`MHD_FIRSTVOLUME 0x0100`，真字节证据见
             * `VolumeNumberContentTests.RAR4_…` 那一组）⇒ 同样自证"我是这一组的第一片"。
             * ⛔ 老编号族（`.rar` + `.r00`）也照样给这个事实（它是**内容**结论）；
             * "要不要按名字动手"由下面那些名字尺子回答，⛔ 两件事不许混在一起。
             */
            bodyKind = ArchiveBodyKind.VolumeFirstPiece;
        }

        /*
         * ===== 专属算法（续）：**其它族**能自证到什么就写什么 =====
         *
         * <list type="bullet">
         * <item><description><b>总片数</b>：跨盘 ZIP 的末片里 EOCD **自己写着总盘数**
         * （<see cref="VolumeNumberReading.Total"/>）⇒ 与 7z 那条"按字节算"共用**同一个字段**
         * （哪个族答得出就用哪个；⛔ 都答不出 = 0 = 判不出，⛔ 不编）。</description></item>
         * <item><description><b>完整的单卷 ZIP</b>：内容证据**可证"不是分卷组的一员"**
         * （EOCD 的盘号只有一盘）⇒ 它就是一份完整的单卷归档 ⇒ <see cref="ArchiveBodyKind.SingleComplete"/>；
         * 这一条专治"把一份普通的单卷 zip 当成某分卷组的一片"。</description></item>
         * </list>
         */
        if (reading.Total is int contentTotal && contentTotal >= 1)
        {
            declaredVolumeCount = contentTotal;
        }

        /*
         * ⚠ 2026-10-13（守门用例实测逮到）：这一档原来是上面那一条的 `else if` ⇒
         * **跨盘 ZIP 的末片**（`reading.Total` 答得出）与**单盘 ZIP** 这两件事被写成了互斥，
         * 于是"内容可证只有一盘"的那一份 bodyKind 停在 `Unknown`（用例逐字：
         * `Expected: SingleComplete / Actual: Unknown`）。两件事本来就不冲突：
         * 一个是"共几片"、一个是"它是不是分卷组的一员" ⇒ 拆成独立的一条。
         */
        /*
         * ⛔ `ZipEndRecordMeansSplit` 为真时**不是**"完整单卷"：那是分片的最后一片
         * （EOCD 的中央目录偏移 ≥ 本文件长度 ⇒ 中央目录起在更早的片里）。
         * 真机 6666 的 `打包111-L2.zip.000003` 就是这一档 —— 写成 SingleComplete 会让它
         * 被判成"可证不是分卷成员"，整组永远凑不起来。
         */
        if (bodyKind == ArchiveBodyKind.Unknown
            && format == VolumeContentFormat.Zip
            && !reading.IsVolumeMember
            && !reading.ZipEndRecordMeansSplit
            /*
             * ⛔ 只认"读成功且自证单盘"这两档：`None`（EOCD 盘号 0/0 且不跨盘）与
             * `ZipSingleDisk`（同上、带专属结论）。`NotVolumeMember` 是"没有 EOCD、开头也不是 PK"
             * 那一档（跨盘中间片）—— 把它升级成"完整单卷"会让整组永远凑不起来。
             */
            && reading.Fail is VolumeNumberFail.None or VolumeNumberFail.ZipSingleDisk)
        {
            bodyKind = ArchiveBodyKind.SingleComplete;
        }

        /*
         * RAR 单卷：内容已读出且证明“不是分卷成员”（Volume 位/首卷标记均为 0），
         * 读取没有失败 ⇒ 这就是一份完整单卷。读不出/损坏（Fail != None）不升级。
         */
        if (bodyKind == ArchiveBodyKind.Unknown
            && format == VolumeContentFormat.Rar
            && !reading.IsVolumeMember
            && reading.MainHeaderRead)
        {
            bodyKind = ArchiveBodyKind.SingleComplete;
        }

        /*
         * ===== 单一完整压缩包的专属算法（媒体前缀）：前面垫着媒体/别的数据、归档从某个偏移才开始 =====
         *
         * <para>只在这一份"文件头不是归档"时才问（format = Unknown）；命中条件由
         * <see cref="EmbeddedArchiveLocator"/> 的三条自洽校验回答（ZIP 的 EOCD 偏移、7z 的
         * StartHeaderCRC + NextHeaderCRC、RAR4 的主头 CRC + MHD_VOLUME = 0）。</para>
         *
         * <para>命中 ⇒ 它就是一份**完整单卷**：`SingleComplete` + `EmbeddedOffset/Length`；
         * 并且 `Membership` 记成"可证不是分卷成员"（自洽校验已经证明它是完整的一份，不是某组的一片）。
         * ⛔ 判不出 ⇒ 一个字不改（仍 Unknown，交给第二大步）。</para>
         */
        long embeddedOffset = 0;
        long embeddedLength = 0;
        VolumeNumberFromContent.VolumeMembershipVerdict membership =
            VolumeNumberFromContent.ClassifyMembershipFromReading(reading);

        /*
         * ⚠ ZIP 这一族有一个坑：`VolumeNumberFromContent.Read` 会从**文件尾的 EOCD** 认出 ZIP，
         * 于是"媒体前缀 + 尾部 ZIP"在走到这里时 format 已经是 Zip、不是 Unknown。
         * 但文件头并不是 ZIP（没有本地文件头）⇒ 它仍然是"尾部归档"，必须让定位器给出偏移；
         * 否则 `EmbeddedOffset` 永远是 0，下游按普通 ZIP 从文件头打开会失败。
         * 普通 ZIP（开头就是 PK）不满足 `!HasZipLocalHeader`，不会被多扫一遍。
         */
        bool archiveMayStartAfterPrefix = format == VolumeContentFormat.Unknown
            || (format == VolumeContentFormat.Zip && !reading.HasZipLocalHeader);

        if (archiveMayStartAfterPrefix
            && EmbeddedArchiveLocator.TryLocate(path) is { } embedded)
        {
            format = embedded.Format;
            bodyKind = ArchiveBodyKind.SingleComplete;
            embeddedOffset = embedded.Offset;
            embeddedLength = embedded.Length;

            membership = VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;
        }

        /*
         * ===== 内容物事实：普通媒体（用户 2026-11-11 口径：媒体前缀只服务"单一完整压缩包"，
         * 分卷不在这一版范围内）=====
         *
         * 只有下面三条同时成立才置 NormalMedia：
         *   ① 文件头不是归档（format 仍是 Unknown —— 媒体前缀的完整单卷此时已经变成内层格式）；
         *   ② 媒体容器按自己的结构走得通；
         *   ③ 媒体结束位置 == 文件长度（媒体正好占满整个文件）。
         *
         * ⛔ 媒体结构后面还有数据（可能是损坏/未识别的尾部归档）⇒ 保持 Unknown：
         * 那是"判不出"，不是"普通内容物"。裸切续卷伪装成 .png 时媒体签名本身就不成立，同样保持 Unknown。
         */
        FileContentKind contentKind = FileContentKind.Unknown;

        if (format == VolumeContentFormat.Unknown
            && EmbeddedArchiveLocator.TryGetMediaExtent(path, out long mediaEnd, out long mediaLength)
            && mediaEnd == mediaLength)
        {
            contentKind = FileContentKind.NormalMedia;
        }

        /*
         * ⛔ 内容物识别不在这里做（2026-11-11）：`FileNature.Inspect` 有大量调用点，
         * 在这里探测会让每个普通图片/视频都读一次头，且会盖住“只有同基名/分卷候选才准探测”的口径。
         * 普通文件保持 Unknown，由准入点按候选上下文显式调 `FileContentProbe`。
         *
         * 唯一可以在这里落的是**已经由 EmbeddedArchiveLocator 证明的媒体前缀归档**：
         * 它首先是归档，不能被后续按“头部像媒体”当内容物。
         */
        FileContentClass contentClass = embeddedOffset > 0
            ? FileContentClass.Archive
            : FileContentClass.Unknown;
        string contentFormat = string.Empty;

        ArchiveEncryptionState encryption = ArchiveEncryptionState.Unknown;

        if (format == VolumeContentFormat.Rar)
        {
            /*
             * RAR 单卷/多卷的加密头只有这一条判据答得出；失败的读法一律 Unknown，
             * ⛔ 不把"读不出"当成"没加密"。
             */
            encryption = RarEncryptionReader.Read(path).State;
        }

        var card = new FileNatureCard
        {
            Path = path!,
            Size = size,
            Format = format,
            Membership = membership,
            VolumeNumber = reading.Number,
            FirstVolumeFlag = reading.FirstVolumeFlag,
            ContentFail = reading.Fail,
            BodyKind = bodyKind,
            ContentKind = contentKind,
            ContentClass = contentClass,
            ContentFormat = contentFormat,
            RarVariant = reading.RarVariant,
            Encryption = encryption,
            DeclaredTotalBytes = declaredTotalBytes,
            DeclaredVolumeCount = declaredVolumeCount,
            EmbeddedOffset = embeddedOffset,
            EmbeddedLength = embeddedLength,
            RawVolumeField = reading.RawField,
            HasZipEndRecord = reading.HasZipEndRecord,
            ZipEndRecordMeansSplit = reading.ZipEndRecordMeansSplit,
            HasZipLocalHeader = reading.HasZipLocalHeader,
            VolumeIndexByName = LegacyVolumeNames.TryGetIndex(name),
            FirstVolumeNameByName = LegacyVolumeNames.TryGetFirstVolumeName(name),
            CanonicalVolumeName = canonical ?? string.Empty,
            ArchiveBaseName = FileNameHelper.GetArchiveBaseName(name),
            HasIdentity = hasIdentity,
            VolumeSerial = volumeSerial,
            FileIndex = fileIndex
        };

        bool firstTime = !Cache.ContainsKey(path!);
        Cache[path!] = (size, lastWriteTicks, card);

        if (firstTime)
        {
            CacheOrder.Enqueue(path!);
        }

        TrimCache();

        return card;
    }

    private static void TrimCache()
    {
        while ((Cache.Count > MaxCacheEntries || CacheOrder.Count > MaxCacheEntries * 2)
            && CacheOrder.TryDequeue(out string? old))
        {
            Cache.TryRemove(old, out _);
        }
    }
}
