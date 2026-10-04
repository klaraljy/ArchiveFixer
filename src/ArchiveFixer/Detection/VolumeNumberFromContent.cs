using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 改名时用的**名字族**（用户 2026-09-29 要求：卷名拼法只留**一个**出口）。
    ///
    /// <para>⛔ 任何地方要拼"这一组卷该叫什么"，都必须走
    /// <see cref="VolumeNumberFromContent.BuildStandardNames"/>，不许在别处再写一套 ——
    /// 计划里说的名字与真正改成的名字不是同一个，就会出现"按它说的改完还是解不开"。</para>
    /// </summary>
    public enum VolumeNamingFamily
    {
        /// <summary>7z 的数字族：<c>基名.7z.001</c> / <c>.002</c>……（与 <c>VolumeGroupDetector</c> 的数字族口径一致）。</summary>
        SevenZipNumbered,

        /// <summary>RAR 族：<c>基名.part1.rar</c> / <c>part2.rar</c>……（RAR5 与 RAR 1.5–4.x/RAR4 的新编号都认这一族）。</summary>
        RarPart,

        /// <summary>真·跨盘 zip 族：<c>基名.z01</c>……<c>基名.z99</c> + 末片 <c>基名.zip</c>（PKZIP 跨盘族的拼法）。</summary>
        ZipSpanned
    }

    /// <summary>RAR 1.5–4.x（RAR4）主头里的编号族（<c>MHD_NEWNUMBERING</c>）。</summary>
    public enum RarNumberingFamily
    {
        Unknown,
        New,
        Old
    }

    /// <summary>
    /// "这一卷的内容告诉了我们什么"这一档的机器结论（**面向用户的中文不在这里** —— 文案统一由
    /// <c>VolumeNameRepair</c> 从 <c>StatusText</c> 取，见 AGENTS.md §7）。
    /// </summary>
    public enum VolumeNumberFail
    {
        /// <summary>内容认出来了，卷号也定了。</summary>
        None,

        /// <summary>魔数既不是 RAR 也不是跨盘 zip（7z 那条路走 <see cref="VolumeContentInference"/>）。</summary>
        NotAnArchive,

        /// <summary>认得出是归档，但内容说它是**单卷**（不是分卷组的一员）。</summary>
        NotVolumeMember,

        /// <summary>头被截断 / CRC 对不上 / 归档头被加密 —— 读不出卷号。</summary>
        HeadersUnreadable,

        /// <summary>RAR 1.5–4.x（RAR4）的老式编号族（<c>.rar</c>/<c>.r00</c>）：本程序只认 <c>partN.rar</c> 这一族，不猜。</summary>
        RarOldNumbering,

        /// <summary>RAR 1.5–4.x（RAR4）的"这是第 1 卷"标记与推出来的卷号对不上。</summary>
        RarFirstVolumeMismatch,

        /// <summary>同目录里凑不齐一组（认出来的成员少于 2 卷）。</summary>
        GroupIncomplete,

        /// <summary>整组卷号连不成 1..N（缺卷，或有一卷被改坏后认不出来了）。</summary>
        GroupNotContiguous,

        /// <summary>RAR 1.5–4.x（RAR4）卷号的基数（0 起还是 1 起）两种解释都成立 / 都不成立 —— 不猜。</summary>
        GroupBaseAmbiguous,

        /// <summary>EOCD 说这是**单盘** zip（盘号 0）—— 不是跨盘组。</summary>
        ZipSingleDisk,

        /// <summary>跨盘 zip 的片数 ≥ 3：除末片外内容里没有盘号，中间几片的先后定不下来。</summary>
        ZipTooManyDisks,

        /// <summary>末片说总共 N 片，但同目录里能认出来的片数对不上。</summary>
        ZipPartsMissing,

        /// <summary>当前这一卷不是这一组的第 1 卷（RAR 的入口就是第 1 卷，改它没用）。</summary>
        CurrentNotFirstVolume,

        /// <summary>当前这一卷根本不在这一组里（同目录里还有别的组）。</summary>
        CurrentNotInGroup
    }

    /// <summary>**一卷的内容**读出来的东西（只读头部与尾部若干字节，不解析归档内容）。</summary>
    public sealed class VolumeNumberReading
    {
        /// <summary>这一卷的完整路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>内容格式（7z / RAR / ZIP / 认不出）。</summary>
        public VolumeContentFormat Format { get; init; }

        /// <summary>内容自述"我是分卷组的一员"。</summary>
        public bool IsVolumeMember { get; init; }

        /// <summary>
        /// 这一卷的字节数（读不到就是 0）。**只给"尺寸规律"那一条判据用** ——
        /// 分卷组里除最后一片外都是切分上限那么大的满片，所以"该接在末片前面"的那一片不可能比末片还小。
        /// </summary>
        public long Size { get; init; }

        /// <summary>卷号（**1 起**）。null = 内容里没有盘号（跨盘 zip 的非末片），或还没定（RAR 1.5–4.x 要看整组）。</summary>
        public int? Number { get; init; }

        /// <summary>能确定总片数时给出（跨盘 zip 的末片：盘号 + 1）。</summary>
        public int? Total { get; init; }

        /// <summary>
        /// RAR **1.5–4.x**（WinRAR 里叫 RAR4；签名与 RAR3 是同一个，靠签名分不出 3 与 4）
        /// 卷尾归档结尾块里的**原始**卷号字段值。基数（0 起还是 1 起）在 RAR 的文档里没写，
        /// 只能靠"整组必须连成 1..N"来定 —— 所以这里保留原值，不在单卷上做加减。
        /// </summary>
        public int? RawField { get; init; }

        /// <summary>RAR 1.5–4.x 主头的 <c>MHD_FIRSTVOLUME</c>（"这一卷就是第 1 卷"）。</summary>
        public bool FirstVolumeFlag { get; init; }

        /// <summary>RAR 1.5–4.x 主头的编号族。</summary>
        public RarNumberingFamily RarNumbering { get; init; }

        /// <summary>读不出来 / 认不出来时是哪一档。</summary>
        public VolumeNumberFail Fail { get; init; }

        /// <summary>给文案用的数字（片数等；没有就是 0）。</summary>
        public int Detail { get; init; }
    }

    /// <summary>定序结论里的一卷：路径 + 卷号（1 起）。</summary>
    public sealed class VolumeGroupSlot
    {
        public string Path { get; init; } = string.Empty;

        public int Number { get; init; }
    }

    /// <summary>一组的定序结论（成立 / 不成立都要说清是哪一档）。</summary>
    public sealed class VolumeGroupOrder
    {
        public bool Confirmed { get; init; }

        /// <summary>成立时按卷号升序的整组（第 1 卷在第一位）。</summary>
        public IReadOnlyList<VolumeGroupSlot> Slots { get; init; } = Array.Empty<VolumeGroupSlot>();

        public VolumeNumberFail Fail { get; init; }

        /// <summary>给文案用的数字（片数等）。</summary>
        public int Detail { get; init; }

        public int Count => Slots.Count;
    }

    /// <summary>
    /// **内容级卷号识别**（用户 2026-09-29 任务：RAR / ZIP 分卷不靠名字，靠内容里的卷号）。
    ///
    /// <para><b>为什么 RAR / ZIP 能做、7z 不能</b>：7z 的 <c>-v</c> 分卷内容里**没有卷号**
    /// （只有第一卷有魔数，之后是裸字节流，见 <see cref="VolumeContentInference"/>），
    /// 所以 7z 只能"猜顺序 + 试开验证"。RAR 与跨盘 ZIP 不一样：**卷号写在内容里**——</para>
    /// <list type="bullet">
    /// <item><description><b>RAR5</b>：主归档头（headerType=1）的 archiveFlags 带 <c>0x0001</c> = 这是分卷组的一员；
    /// 带 <c>0x0002</c> = 后面还有卷号字段（**第 1 卷没有这个字段**），字段值 v → 卷号 = v + 1。</description></item>
    /// <item><description><b>RAR 1.5–4.x</b>（WinRAR 里就叫 <b>RAR4</b>；<c>Rar!\x1A\x07\x00</c> 这个签名
    /// RAR3 与 RAR4 是**同一个**，靠它根本分不出 3 与 4，所以本文件一律不断言"这一卷是 RAR3"）：
    /// 卷号**不在**主头里，而在**卷尾归档结尾块**（type <c>0x7B</c>）的 <c>EARC_VOLNUMBER</c> 字段
    /// （flags <c>0x0008</c>）。块里可选字段按 <c>EARC_DATACRC(4)</c> → <c>EARC_VOLNUMBER(2)</c> →
    /// 保留区(7) 的**顺序**排，各自看出没出那一位 flag，所以卷号位置 =
    /// <c>块起点 + 7 + (有 DATACRC ? 4 : 0)</c>。⚠ 基数（0 起还是 1 起）RAR 的文档没写
    /// → 交给"整组必须连成 1..N"自洽判定。</description></item>
    /// <item><description><b>跨盘 ZIP</b>：EOCD（<c>PK\x05\x06</c>）偏移 4 = 本盘号、偏移 6 = 中央目录起始盘号；
    /// 两个都 0 = 单盘。EOCD 只在**末片**且必须在文件最末（注释长度要自洽）→ 本盘号 + 1 = 总片数。
    /// **非末片内容里没有盘号**，只能靠开头是不是 PK 签名认"它是 zip 流"（真 PKZIP 跨盘的第 1 片以
    /// 跨盘标记 <c>PK\x07\x08</c> 开头，不是本地文件头 —— 用户 2026-09-29 真样本），
    /// 位置则由末片的盘号 + 片数 + 尺寸规律消去法定。</description></item>
    /// </list>
    ///
    /// <para><b>⛔ 拿不准一律不认</b>：头截断、CRC 对不上、卷号连不成 1..N、两种基数都成立、
    /// 跨盘 zip 片数 ≥ 3（中间几片没有盘号，消去法只能定"恰好剩一个缺口"的那一组，也就是只有 2 片时）
    /// —— 全部返回"没认出来"，由调用方原样不动（改错名字比不改更糟）。</para>
    ///
    /// <para>本类是**纯内容判断**：只读文件头尾若干字节，不建目录、不改名字、不调引擎。</para>
    /// </summary>
    public static class VolumeNumberFromContent
    {
        /// <summary>RAR5 主归档头的 headerType。</summary>
        private const ulong Rar5MainHeaderType = 1;

        /// <summary>RAR5 归档加密头（这种归档的后续头都是密文，读不出卷号）。</summary>
        private const ulong Rar5EncryptionHeaderType = 4;

        /// <summary>RAR5 archiveFlags：这是分卷组的一员。</summary>
        private const ulong Rar5ArchiveVolume = 0x0001;

        /// <summary>RAR5 archiveFlags：后面还有卷号字段（第 1 卷没有）。</summary>
        private const ulong Rar5ArchiveVolumeNumber = 0x0002;

        /// <summary>RAR5 headerFlags：头里带可选扩展区（扩展区大小字段存在）。</summary>
        private const ulong Rar5HeaderExtraArea = 0x0001;

        /// <summary>RAR5 headerFlags：头里带可选数据区（数据区大小字段存在）。</summary>
        private const ulong Rar5HeaderDataArea = 0x0002;

        /// <summary>RAR 1.5–4.x 主头 TYPE（<c>MHD_HEAD</c>）。</summary>
        private const byte Rar4LegacyMainHeaderType = 0x73;

        /// <summary>RAR 1.5–4.x 归档结尾块 TYPE（<c>ENDARC_HEAD</c>）。</summary>
        private const byte Rar4LegacyEndBlockType = 0x7B;

        /// <summary>RAR 1.5–4.x 主头 flags：这是分卷组的一员（<c>MHD_VOLUME</c>）。</summary>
        private const ushort Rar4LegacyVolume = 0x0001;

        /// <summary>RAR 1.5–4.x 主头 flags：新式编号（<c>partN.rar</c>）；不设 = 老式（<c>.rar</c>/<c>.r00</c>）。</summary>
        private const ushort Rar4LegacyNewNumbering = 0x0010;

        /// <summary>RAR 1.5–4.x 主头 flags：这一卷就是第 1 卷（<c>MHD_FIRSTVOLUME</c>）。</summary>
        private const ushort Rar4LegacyFirstVolume = 0x0100;

        /// <summary>RAR 1.5–4.x 归档结尾块 flags：块里有 <c>EARC_VOLNUMBER</c> 卷号字段。</summary>
        private const ushort Rar4LegacyEndBlockVolumeNumber = 0x0008;

        /// <summary>RAR 1.5–4.x 归档结尾块 flags：块里有 <c>EARC_DATACRC</c>（4 字节，排在卷号**前面**）。</summary>
        private const ushort Rar4LegacyEndBlockDataCrc = 0x0002;

        /// <summary>RAR 1.5–4.x 归档结尾块 flags：块尾有 7 字节保留区（<c>EARC_REVSPACE</c>，排在最后）。</summary>
        private const ushort Rar4LegacyEndBlockReservedSpace = 0x0004;

        /// <summary>RAR 1.5–4.x 归档结尾块末尾的保留区（7 字节，<c>EARC_REVSPACE</c>）。</summary>
        private const int Rar4LegacyEndBlockReservedSize = 7;

        /// <summary>EOCD 定长部分（签名 4 + 18）。</summary>
        private const int EndOfCentralDirectorySize = 22;

        /// <summary>zip 注释最长 65535 —— EOCD 最多往回找这么多字节。</summary>
        private const int ZipCommentMaxLength = 0xFFFF;

        /// <summary>RAR5 头大小上限：格式自己规定"不许超过 3 字节 vint"，也就是 2 MB。</summary>
        private const int Rar5MaxHeaderSize = 2 * 1024 * 1024;

        /// <summary>一次读进内存的 RAR5 头字节上限（头本身的合法上限 + vint 最长 10 字节）。</summary>
        private const int Rar5ReadLimit = Rar5MaxHeaderSize + 16;

        /// <summary>
        /// 读**一卷**的内容，得出"它说自己是不是分卷组的一员、是第几卷"。
        ///
        /// <para>任何 IO 意外都落成"没认出来 + 哪一档原因"，**绝不抛**。</para>
        /// </summary>
        public static VolumeNumberReading Read(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return NotAVolume(string.Empty, VolumeContentFormat.Unknown, VolumeNumberFail.NotAnArchive);
            }

            VolumeContentFormat format = VolumeContentInference.SniffFormat(filePath);

            try
            {
                if (format == VolumeContentFormat.Rar)
                {
                    return ReadRar(filePath);
                }

                if (format == VolumeContentFormat.Zip)
                {
                    return ReadZip(filePath);
                }

                /*
                 * 头不是 PK 也可能是跨盘 zip 的**末片**：分片是从数据中间切开的，末片的开头是上一片剩下来的数据，
                 * 根本没有本地文件头（7z 的 -v 切出来就是这样）。这种文件认不认由 EOCD 说了算 ——
                 * 所以这里补一次内容判断，只有它自述"是跨盘组的一员"时才改口径。
                 */
                VolumeNumberReading fallback = ReadZip(filePath);

                return fallback.IsVolumeMember
                    ? fallback
                    : NotAVolume(filePath, format, VolumeNumberFail.NotAnArchive);
            }
            catch (Exception)
            {
                // 读不到（被占 / 权限 / 半路被删）就是"看不出来"：调用方会因此一个字节都不动。
                return NotAVolume(filePath, format, VolumeNumberFail.HeadersUnreadable);
            }
        }

        /// <summary>
        /// 把"同目录这一批文件"的内容读数**定序成一组**（内容级的唯一出口）。
        ///
        /// <para>只拿与 <paramref name="currentPath"/> **同格式**的读数参与：目录里同时躺着 RAR 与跨盘 zip 时
        /// 不混组（混起来就没有"连成 1..N"可言，那正是最该拒绝的情形）。</para>
        /// </summary>
        public static VolumeGroupOrder ResolveGroup(string? currentPath, IEnumerable<VolumeNumberReading>? readings)
        {
            var list = (readings ?? Array.Empty<VolumeNumberReading>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path))
                .ToList();

            string path = currentPath ?? string.Empty;

            // 调用方给的那一卷必须在里面（枚举同目录时通常已经含它；不含就自己补一次）。
            if (!list.Any(r => SamePath(r.Path, path)))
            {
                VolumeNumberReading self = Read(path);
                list.Add(self);
            }

            VolumeNumberReading? current = list.FirstOrDefault(r => SamePath(r.Path, path));

            if (current == null)
            {
                return Refuse(VolumeNumberFail.NotAnArchive, 0);
            }

            switch (current.Format)
            {
                case VolumeContentFormat.Rar:
                    return ResolveRarGroup(list, path);

                case VolumeContentFormat.Zip:
                    return ResolveZipGroup(list, path);

                default:
                    return Refuse(VolumeNumberFail.NotAnArchive, 0);
            }
        }

        /// <summary>
        /// **卷名拼法的唯一出口**：由"基名（不含归档后缀）+ 族 + 卷数"拼出整组标准名。
        ///
        /// <para>RAR 族带 <c>.rar</c> 尾巴（<c>part1.rar</c>……）；7z 是数字族（<c>.7z.001</c>……）；
        /// 跨盘 zip 是 <c>.z01</c>……+ 末片 <c>.zip</c>（7-Zip 实测认这一族：改名成它之后
        /// <c>7z l</c> 能报出 <c>Multivolume = +</c> 并把里面的文件列出来）。</para>
        /// </summary>
        public static IReadOnlyList<string> BuildStandardNames(string? stem, VolumeNamingFamily family, int count)
        {
            if (string.IsNullOrWhiteSpace(stem) || count < 1)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>(count);

            switch (family)
            {
                case VolumeNamingFamily.SevenZipNumbered:
                    for (int index = 1; index <= count; index++)
                    {
                        names.Add(stem + ".7z." + index.ToString("D3", CultureInfo.InvariantCulture));
                    }

                    break;

                case VolumeNamingFamily.RarPart:
                    /*
                     * 卷号补零宽度按"总卷数有几位"定：1~9 卷是 part1.rar，10~99 卷是 part01.rar……
                     * 这是 WinRAR 自己的口径（卷数多到要两位以上才补零），照着它写才不会造出它不认的名字。
                     */
                    int width = count.ToString(CultureInfo.InvariantCulture).Length;

                    for (int index = 1; index <= count; index++)
                    {
                        names.Add(
                            stem + ".part" + index.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + ".rar");
                    }

                    break;

                case VolumeNamingFamily.ZipSpanned:
                    /*
                     * 跨盘 zip 的族：末片叫 .zip，之前的片叫 .z01/.z02……
                     * 只有 1 片时不存在"跨盘组"，返回空 —— 调用方因此拒绝，不会造出一个假的组。
                     */
                    if (count < 2)
                    {
                        return Array.Empty<string>();
                    }

                    for (int index = 1; index < count; index++)
                    {
                        names.Add(stem + ".z" + index.ToString("D2", CultureInfo.InvariantCulture));
                    }

                    names.Add(stem + ".zip");

                    break;

                default:
                    return Array.Empty<string>();
            }

            return names;
        }

        /// <summary>
        /// 推"基名"（**不含归档后缀**）：去掉末尾的卷号段（<c>amb909.7.01</c> 的 <c>01</c>），
        /// 再把末尾那个"本来该是归档后缀"的段去掉 —— 判据是**只差一个字符且只有一种变法**
        /// （<c>amb909.7</c> 的 <c>7</c> → <c>7z</c>）。
        ///
        /// <para>⛔ 推不出来（名字里已经是后缀 / 认不出）就原样留着，绝不硬剪：基名只影响
        /// "改完的名字像不像人写的"，正确性由卷号与"绝不覆盖"两条钉着。</para>
        ///
        /// <para>⚠ 2026-10-03 阶段 A 收口：判据整体搬进**唯一基名出口**
        /// <see cref="Helpers.FileNameHelper.TryResolveVolumeBaseName"/> 的
        /// <see cref="Helpers.VolumeBaseNameLevel.AnchorStem"/> 档（要传本族的规范后缀），
        /// 本方法只转调（⛔ 这里不再算一遍基名）。</para>
        /// </summary>
        public static bool TryDeriveStem(string? filePath, VolumeContentFormat format, out string stem) =>
            Helpers.FileNameHelper.TryResolveVolumeBaseName(
                filePath,
                Helpers.VolumeBaseNameLevel.AnchorStem,
                out stem,
                out _,
                VolumeContentInference.ExtensionFor(format));

        // ── RAR ──

        private static VolumeNumberReading ReadRar(string filePath)
        {
            byte[] head = ReadBytes(filePath, 0, 8);

            if (head.Length < 8)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            // 签名第 7 字节：0x01 0x00 = RAR5；0x00 = RAR 1.5–4.x（WinRAR 里叫 RAR4；两者签名相同）。
            return head[6] == 0x01 && head[7] == 0x00
                ? ReadRar5(filePath)
                : ReadRar4Legacy(filePath);
        }

        /// <summary>RAR5：解析主归档头（字段布局取自 RAR 5.0 官方格式说明，见类注释里的链接口径）。</summary>
        private static VolumeNumberReading ReadRar5(string filePath)
        {
            byte[] raw = ReadBytes(filePath, 0, 12 + Rar5ReadLimit);

            if (raw.Length < 12 + 4)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4));

            int offset = 12;

            if (!TryReadVint(raw, ref offset, out ulong headerSize)
                || headerSize == 0
                || headerSize > Rar5MaxHeaderSize)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            // HEAD_SIZE 从 Header type 字段算起，而 offset 此刻正指着 Header type 字段。
            int headerEnd = offset + (int)headerSize;

            if (headerEnd <= offset || headerEnd > raw.Length)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            ReadOnlySpan<byte> header = raw.AsSpan(0, headerEnd);

            /*
             * 头部 CRC32 覆盖 [Header size 字段, 头末尾]（官方说明：CRC32 of header data starting from
             * Header size field and up to and including the optional extra area）。它是"这真是 RAR5 头"的硬证据。
             *
             * ⚠ 取不取补两种约定都收：文档只写"CRC32"，而参考实现里这个值取不取补在历史上并不统一，
             * 本机又造不出 RAR 样本可验。收宽一点才不会把真样本挡在门外 —— 真正的闸门是
             * "签名 + 字段自洽 + 整组卷号连成 1..N"，CRC 只用来挡随机字节。
             */
            if (!CrcMatches(storedCrc, header[12..]))
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            int p = offset;

            if (!TryReadVint(header, ref p, out ulong headerType))
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            if (headerType == Rar5EncryptionHeaderType)
            {
                // 头被加密：卷号在密文里，读不出来（如实说不认，绝不猜）。
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            if (headerType != Rar5MainHeaderType)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.NotVolumeMember);
            }

            if (!TryReadVint(header, ref p, out ulong flags))
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            if ((flags & Rar5HeaderExtraArea) != 0 && !TryReadVint(header, ref p, out _))
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            if ((flags & Rar5HeaderDataArea) != 0 && !TryReadVint(header, ref p, out _))
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            if (!TryReadVint(header, ref p, out ulong archiveFlags))
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            if ((archiveFlags & Rar5ArchiveVolume) == 0)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.NotVolumeMember);
            }

            ulong rawField = 0;
            int number = 1;

            if ((archiveFlags & Rar5ArchiveVolumeNumber) != 0)
            {
                if (!TryReadVint(header, ref p, out rawField) || rawField >= int.MaxValue - 1)
                {
                    return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
                }

                // 官方说明：字段值 1 = 第 2 卷、2 = 第 3 卷……（第 1 卷没有这个字段）。
                number = (int)rawField + 1;
            }

            return new VolumeNumberReading
            {
                Path = filePath,
                Format = VolumeContentFormat.Rar,
                Size = LengthOf(filePath),
                IsVolumeMember = true,
                Number = number,
                RawField = (int)rawField,
                RarNumbering = RarNumberingFamily.New,
                Fail = VolumeNumberFail.None
            };
        }

        /// <summary>
        /// RAR 1.5–4.x（WinRAR 里叫 RAR4）：主头读编号族与"第 1 卷"标记，卷号从**卷尾归档结尾块**里取。
        /// </summary>
        private static VolumeNumberReading ReadRar4Legacy(string filePath)
        {
            long length = LengthOf(filePath);

            if (length < 7 + 13 + 7)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            byte[] main = ReadBytes(filePath, 7, 13);

            if (main.Length < 13 || main[2] != Rar4LegacyMainHeaderType)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.NotVolumeMember);
            }

            ushort mainCrc = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(0, 2));
            ushort mainFlags = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(3, 2));
            ushort mainSize = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(5, 2));

            /*
             * RAR 1.5–4.x 的 HEAD_SIZE 把 HEAD_CRC 一起算进去（主头就是 13 字节），块的范围是
             * [起点, 起点 + HEAD_SIZE)，校验范围是 [HEAD_TYPE, 块末尾)。
             */
            if (mainSize < 13 || 7 + mainSize > length)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            byte[] mainFull = ReadBytes(filePath, 7, mainSize);

            if (mainFull.Length < mainSize || !CrcMatches16(mainCrc, mainFull.AsSpan(2)))
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            if ((mainFlags & Rar4LegacyVolume) == 0)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.NotVolumeMember);
            }

            bool newNumbering = (mainFlags & Rar4LegacyNewNumbering) != 0;
            bool firstVolume = (mainFlags & Rar4LegacyFirstVolume) != 0;

            if (!newNumbering)
            {
                /*
                 * 老式编号族（第一卷 .rar，之后 .r00/.r01……）。本程序只留**一个**名字出口
                 * （partN.rar，用户 2026-09-29 要求），所以这一族一律不动 —— 猜错族就是把整组改成解不开的。
                 */
                return new VolumeNumberReading
                {
                    Path = filePath,
                    Format = VolumeContentFormat.Rar,
                    Size = length,
                    IsVolumeMember = true,
                    RawField = null,
                    FirstVolumeFlag = firstVolume,
                    RarNumbering = RarNumberingFamily.Old,
                    Fail = VolumeNumberFail.RarOldNumbering
                };
            }

            int? volumeField = TryReadRar4LegacyEndBlockVolumeNumber(filePath, length, out VolumeNumberFail fail);

            if (volumeField == null)
            {
                return NotAVolume(filePath, VolumeContentFormat.Rar, fail);
            }

            return new VolumeNumberReading
            {
                Path = filePath,
                Format = VolumeContentFormat.Rar,
                Size = length,
                IsVolumeMember = true,
                RawField = volumeField,
                FirstVolumeFlag = firstVolume,
                RarNumbering = RarNumberingFamily.New,
                Fail = VolumeNumberFail.None
            };
        }

        /// <summary>
        /// 找卷尾的归档结尾块并取出卷号字段（原始值，基数待定）。
        ///
        /// <para><b>字段位置按官方布局**顺序**算，不写死偏移</b>：结尾块里可选字段按
        /// <c>EARC_DATACRC(4)</c> → <c>EARC_VOLNUMBER(2)</c> → 保留区(7) 的顺序排，各自看出没出那一位 flag。
        /// 所以卷号的偏移 = <c>7 + (有 DATACRC ? 4 : 0)</c>（7 = HEAD_CRC(2) + TYPE(1) + FLAGS(2) + SIZE(2)）。</para>
        ///
        /// <para><b>⚠ 老写法在这里是真错的（2026-09-29 真样本逮到）</b>：它把卷号当成 4 字节、按
        /// <c>块起点 + HEAD_SIZE − 11</c> 读 —— 真样本两卷因此读出 <c>0xCF7C</c> / <c>0x01EF02</c>
        /// （DATACRC 的尾巴混了进来），整组连不成 1..N，于是"RAR 分卷一律不认"。
        /// 真样本的字节是硬证据：两卷的 <c>HEAD_SIZE = 20 = 7 + 4 + 2 + 7</c>，卷号是紧跟 DATACRC 的
        /// **2 字节**（第 1 卷 0、第 2 卷 1），其后再 7 字节保留区；而且只有第 1 卷带
        /// <c>EARC_NEXT_VOLUME</c>（0x0001）—— 与"它是第 1 卷"的结论互相印证。</para>
        ///
        /// <para>块本身按"TYPE=0x7B、HEAD_SIZE 正好收在文件末尾、头部 CRC 对得上、各可选字段的总长与
        /// HEAD_SIZE 相等"四条一起认；布局与官方说明不符时**不认**（宁可不动，也不拿一个错偏移去凑卷号）。</para>
        /// </summary>
        private static int? TryReadRar4LegacyEndBlockVolumeNumber(
            string filePath,
            long length,
            out VolumeNumberFail fail)
        {
            fail = VolumeNumberFail.HeadersUnreadable;

            int tailLength = (int)Math.Min(length, 512);
            byte[] tail = ReadBytes(filePath, length - tailLength, tailLength);

            if (tail.Length < tailLength)
            {
                return null;
            }

            for (int start = tailLength - 14; start >= 0; start--)
            {
                if (tail[start + 2] != Rar4LegacyEndBlockType)
                {
                    continue;
                }

                ushort size = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(start + 5, 2));

                // 块必须正好收在文件末尾：末尾之后 RAR 不再读任何东西。
                if (start + size != tailLength || size < 7 + Rar4LegacyEndBlockReservedSize)
                {
                    continue;
                }

                ushort storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(start, 2));

                if (!CrcMatches16(storedCrc, tail.AsSpan(start + 2, size - 2).ToArray()))
                {
                    continue;
                }

                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(start + 3, 2));

                if ((flags & Rar4LegacyEndBlockVolumeNumber) == 0)
                {
                    // 结尾块里没有卷号字段：这一卷的内容里就没有卷号（如实说不认）。
                    fail = VolumeNumberFail.HeadersUnreadable;
                    return null;
                }

                int fieldOffset = 7 + ((flags & Rar4LegacyEndBlockDataCrc) != 0 ? 4 : 0);
                int expectedSize = fieldOffset + 2
                    + ((flags & Rar4LegacyEndBlockReservedSpace) != 0 ? Rar4LegacyEndBlockReservedSize : 0);

                if (size != expectedSize)
                {
                    /*
                     * 各可选字段按 flag 算出来的总长与 HEAD_SIZE 对不上 —— 说明这一块的布局不是官方说明的那一套，
                     * 谁也不敢保证那个 2 字节字段就是卷号。⛔ 不认（改错名字比不改更糟）。
                     */
                    fail = VolumeNumberFail.HeadersUnreadable;
                    return null;
                }

                return BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(start + fieldOffset, 2));
            }

            return null;
        }

        // ── 跨盘 ZIP ──

        private static VolumeNumberReading ReadZip(string filePath)
        {
            long length = LengthOf(filePath);

            if (length < EndOfCentralDirectorySize)
            {
                return NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.HeadersUnreadable);
            }

            int tailLength = (int)Math.Min(length, EndOfCentralDirectorySize + ZipCommentMaxLength);
            byte[] tail = ReadBytes(filePath, length - tailLength, tailLength);

            if (tail.Length < tailLength)
            {
                return NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.HeadersUnreadable);
            }

            // EOCD 必须在文件最末：起点 + 22 + 注释长度 == 文件长度（注释长度自洽）。
            for (int index = tailLength - EndOfCentralDirectorySize; index >= 0; index--)
            {
                if (tail[index] != 0x50 || tail[index + 1] != 0x4B
                    || tail[index + 2] != 0x05 || tail[index + 3] != 0x06)
                {
                    continue;
                }

                ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2));

                if (index + EndOfCentralDirectorySize + commentLength != tailLength)
                {
                    continue;
                }

                return FromEndOfCentralDirectory(filePath, tail.AsSpan(index));
            }

            /*
             * 不是末片：**只要开头是任意一个 PK 签名**就算这一组的成员（用户 2026-09-29 真样本）。
             *
             * ⚠ 老写法要求"开头是本地文件头 PK\x03\x04 **且** 结尾是跨盘标记 PK\x07\x08"，真样本两条都不满足：
             * 真 PKZIP / 网盘那种跨盘 zip 的第 1 片**以跨盘标记 PK\x07\x08 开头**（7-Zip 管这 4 个字节叫
             * Embedded Stub），而切点落在数据中间，文件结尾根本不是标记。于是"第一片认不出来" →
             * 整组凑不齐 → 一个字节都不敢动。
             *
             * ⛔ 放松的只有"这一片像不像 zip 流"这一条判据；"哪一片是第几片"仍然只由 EOCD 的盘号 + 片数 +
             * 尺寸规律决定（见 ResolveZipGroup），拿不准一律不认。
             */
            byte[] head = ReadBytes(filePath, 0, 4);

            if (IsZipSignature(head))
            {
                return new VolumeNumberReading
                {
                    Path = filePath,
                    Format = VolumeContentFormat.Zip,
                    Size = LengthOf(filePath),
                    IsVolumeMember = true,
                    Number = null, // 非末片内容里没有盘号：位置只能靠消去法
                    Fail = VolumeNumberFail.None
                };
            }

            return NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.NotVolumeMember);
        }

        /// <summary>
        /// 这 4 个字节是不是 zip 流的开头签名：本地文件头 <c>PK\x03\x04</c>、中央目录 <c>PK\x01\x02</c>、
        /// EOCD <c>PK\x05\x06</c>、数据描述符/跨盘标记 <c>PK\x07\x08</c>。
        ///
        /// <para>四个都收的理由：跨盘 zip 的每一片都是从**字节流中间**切出来的，切点落在哪儿都不奇怪 ——
        /// 真正的闸门不是"开头像不像本地文件头"，而是"整组能不能连成 1..N"（片数、盘号、尺寸）。</para>
        /// </summary>
        private static bool IsZipSignature(ReadOnlySpan<byte> head) =>
            head.Length >= 4
            && head[0] == 0x50
            && head[1] == 0x4B
            && ((head[2] == 0x03 && head[3] == 0x04)
                || (head[2] == 0x01 && head[3] == 0x02)
                || (head[2] == 0x05 && head[3] == 0x06)
                || (head[2] == 0x07 && head[3] == 0x08));


        private static VolumeNumberReading FromEndOfCentralDirectory(string filePath, ReadOnlySpan<byte> eocd)
        {
            ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(4, 2));
            ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(6, 2));

            if (diskNumber == 0 && centralDirectoryDisk == 0)
            {
                // 两个盘号都是 0 = 单盘 zip（7z 的 -v 切出来的"裸分片 zip"末片也是这个样子）。
                return NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.ZipSingleDisk);
            }

            if (diskNumber == 0 || centralDirectoryDisk > diskNumber)
            {
                // 自相矛盾（中央目录不可能起在比本盘更靠后的盘上）。
                return NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.HeadersUnreadable);
            }

            return new VolumeNumberReading
            {
                Path = filePath,
                Format = VolumeContentFormat.Zip,
                Size = LengthOf(filePath),
                IsVolumeMember = true,
                Number = diskNumber + 1, // 盘号 0 起
                Total = diskNumber + 1,  // 末片：本盘号 + 1 = 总片数
                Fail = VolumeNumberFail.None
            };
        }

        // ── 定序 ──

        /// <summary>
        /// RAR 组：RAR5 的内容里就是绝对卷号；RAR 1.5–4.x（RAR4）的字段基数待定 →
        /// 用"整组必须连成 1..N"判，再用主头的"这是第 1 卷"标记交叉核对。
        /// </summary>
        private static VolumeGroupOrder ResolveRarGroup(
            IReadOnlyList<VolumeNumberReading> list,
            string currentPath)
        {
            List<VolumeNumberReading> members = list
                .Where(r => r.Format == VolumeContentFormat.Rar && r.IsVolumeMember)
                .ToList();

            if (members.Count < 2)
            {
                return Refuse(VolumeNumberFail.GroupIncomplete, members.Count);
            }

            bool hasRar4Legacy = members.Any(m => m.Number == null);
            bool hasRar5 = members.Any(m => m.Number != null);

            if (hasRar4Legacy && hasRar5)
            {
                // 同一个目录里混着 RAR 1.5–4.x（RAR4）与 RAR5 的分卷：不是一组，绝不猜。
                return Refuse(VolumeNumberFail.GroupNotContiguous, members.Count);
            }

            if (members.Any(m => m.Fail == VolumeNumberFail.RarOldNumbering))
            {
                return Refuse(VolumeNumberFail.RarOldNumbering, members.Count);
            }

            var numbers = new List<int>(members.Count);

            if (hasRar4Legacy)
            {
                /*
                 * RAR 1.5–4.x 的卷号字段基数文档没写。两种解释各自要求"整组连成 1..N"：
                 * 字段本身就是 1 起的 → 原样；字段是 0 起的 → 全体 +1。
                 * 两种都成立（或都不成立）= 没有任何证据能定基数 → 不认（用户 2026-09-29 明确的规矩）。
                 */
                bool zeroBased = IsContiguousRun(members.Select(m => m.RawField!.Value + 1), members.Count);
                bool oneBased = IsContiguousRun(members.Select(m => m.RawField!.Value), members.Count);

                if (zeroBased == oneBased)
                {
                    return Refuse(VolumeNumberFail.GroupBaseAmbiguous, members.Count);
                }

                numbers.AddRange(members.Select(m => zeroBased ? m.RawField!.Value + 1 : m.RawField!.Value));

                /*
                 * 交叉核对：RAR 1.5–4.x 主头有"这一卷就是第 1 卷"标记（MHD_FIRSTVOLUME）。
                 * 少了这一步，"0 起的整组里恰好缺了第 1 卷"会被读成"1 起的一组"——
                 * 那就会把第 2 卷改名叫 part1.rar（改错名字比不改更糟）。
                 */
                List<VolumeNumberReading> flagged = members.Where(m => m.FirstVolumeFlag).ToList();

                if ((flagged.Count != 1
                    || numbers[members.IndexOf(flagged[0])] != 1))
                {
                    return Refuse(VolumeNumberFail.RarFirstVolumeMismatch, members.Count);
                }
            }
            else
            {
                numbers.AddRange(members.Select(m => m.Number!.Value));

                if (!IsContiguousRun(numbers, members.Count))
                {
                    // 绝对卷号却连不成 1..N = 缺卷或有一卷被改坏后认不出来：整组都不动。
                    return Refuse(VolumeNumberFail.GroupNotContiguous, members.Count);
                }
            }

            var slots = new List<VolumeGroupSlot>(members.Count);

            for (int index = 0; index < members.Count; index++)
            {
                slots.Add(new VolumeGroupSlot { Path = members[index].Path, Number = numbers[index] });
            }

            slots.Sort((a, b) => a.Number.CompareTo(b.Number));

            if (slots[0].Number != 1)
            {
                return Refuse(VolumeNumberFail.CurrentNotFirstVolume, members.Count);
            }

            // RAR 的入口就是第 1 卷：调用方手上这一卷不是第 1 卷时改名解决不了问题。
            if (!SamePath(currentPath, slots[0].Path))
            {
                return Refuse(VolumeNumberFail.CurrentNotFirstVolume, members.Count);
            }

            return new VolumeGroupOrder
            {
                Confirmed = true,
                Slots = slots,
                Fail = VolumeNumberFail.None,
                Detail = slots.Count
            };
        }

        /// <summary>
        /// 跨盘 zip 组：只有末片的内容里有盘号（= 总片数）。非末片内容里没有盘号，
        /// 所以**只有"恰好剩一个缺口"时**才能用消去法定位 —— 也就是 N=2 才能定序；
        /// N ≥ 3 时中间几片的先后无从判断，一律不认（用户 2026-09-29 明确的规矩）。
        /// </summary>
        private static VolumeGroupOrder ResolveZipGroup(IReadOnlyList<VolumeNumberReading> list, string currentPath)
        {
            List<VolumeNumberReading> members = list
                .Where(r => r.Format == VolumeContentFormat.Zip && r.IsVolumeMember)
                .ToList();

            List<VolumeNumberReading> tails = members.Where(m => m.Number != null).ToList();
            List<VolumeNumberReading> segments = members.Where(m => m.Number == null).ToList();

            if (tails.Count == 0)
            {
                if (members.Count == 0 && list.Any(r => r.Format == VolumeContentFormat.Zip))
                {
                    return Refuse(VolumeNumberFail.ZipSingleDisk, 1);
                }

                return Refuse(VolumeNumberFail.ZipPartsMissing, members.Count);
            }

            if (tails.Count > 1)
            {
                // 一个目录里出现两个"末片"：说不清哪一片才是末片，不认。
                return Refuse(VolumeNumberFail.ZipPartsMissing, tails.Count);
            }

            int total = tails[0].Total ?? tails[0].Number!.Value;

            if (total < 2)
            {
                return Refuse(VolumeNumberFail.ZipSingleDisk, total);
            }

            if (total > 2)
            {
                return Refuse(VolumeNumberFail.ZipTooManyDisks, total);
            }

            if (segments.Count != 1)
            {
                return Refuse(VolumeNumberFail.ZipPartsMissing, segments.Count);
            }

            if (!members.Any(m => SamePath(m.Path, currentPath)))
            {
                // 手上的这一片不在这一组里（同目录还躺着别的跨盘组）。
                return Refuse(VolumeNumberFail.CurrentNotInGroup, members.Count);
            }

            /*
             * 尺寸规律：跨盘 zip 的**非末片**都是切分上限那么大的满片，末片是余量 ——
             * 所以"该接在末片前面"的那一片必定**不小于**末片。比末片还小说明这两个文件不是一组，
             * 认了就等于把一个不相干的文件改成 `.z01`（改错名字比不改更糟）。
             * ⚠ 只在两边都取到字节数时判；取不到（被占 / 权限）时不拿 0 去比，免得把真组挡在门外。
             */
            if (segments[0].Size > 0 && tails[0].Size > 0 && segments[0].Size < tails[0].Size)
            {
                return Refuse(VolumeNumberFail.ZipPartsMissing, 1);
            }

            return new VolumeGroupOrder
            {
                Confirmed = true,
                Slots = new List<VolumeGroupSlot>
                {
                    new() { Path = segments[0].Path, Number = 1 },
                    new() { Path = tails[0].Path, Number = 2 }
                },
                Fail = VolumeNumberFail.None,
                Detail = 2
            };
        }

        /// <summary>这一批卷号是不是**恰好** 1..N（各不重复、最小 1、最大 N）。</summary>
        private static bool IsContiguousRun(IEnumerable<int> numbers, int count)
        {
            var sorted = numbers.OrderBy(v => v).ToList();

            if (sorted.Count != count || count == 0)
            {
                return false;
            }

            for (int index = 0; index < count; index++)
            {
                if (sorted[index] != index + 1)
                {
                    return false;
                }
            }

            return true;
        }

        private static VolumeGroupOrder Refuse(VolumeNumberFail fail, int detail) => new()
        {
            Confirmed = false,
            Fail = fail,
            Detail = detail
        };

        // ── 字节与 CRC ──

        private static VolumeNumberReading NotAVolume(string path, VolumeContentFormat format, VolumeNumberFail fail) => new()
        {
            Path = path,
            Format = format,
            IsVolumeMember = false,
            Fail = fail
        };

        /// <summary>读一小段字节（读不到就少给几个字节，**绝不抛**）。</summary>
        private static byte[] ReadBytes(string filePath, long offset, int count)
        {
            try
            {
                using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                if (offset < 0 || count <= 0)
                {
                    return Array.Empty<byte>();
                }

                long available = Math.Min(count, Math.Max(0, stream.Length - offset));

                if (available <= 0)
                {
                    return Array.Empty<byte>();
                }

                stream.Position = offset;

                var buffer = new byte[available];
                int total = 0;

                while (total < buffer.Length)
                {
                    int read = stream.Read(buffer, total, buffer.Length - total);

                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                return total == buffer.Length ? buffer : buffer[..total];
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static long LengthOf(string filePath)
        {
            try
            {
                return new FileInfo(filePath).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>RAR5 风格的 vint：低 7 位是数据、最高位是"还有下一字节"。</summary>
        internal static bool TryReadVint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
        {
            value = 0;
            int shift = 0;

            while (offset < data.Length && shift <= 63)
            {
                byte current = data[offset++];
                value |= (ulong)(current & 0x7F) << shift;

                if ((current & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }

        /// <summary>
        /// 头 CRC 对得上（取补与不取补两种约定都收，理由见 <see cref="ReadRar5"/> 的注释）。
        ///
        /// <para>⚠ <c>internal</c> 而不是 <c>private</c>：RAR 头的 CRC 与 vint 读法在本仓库里**只留一份**
        /// （<c>RarEncryptionReader</c> 解析 RAR4/RAR5 头时用的是同一份实现）——
        /// 两份 CRC 表/两套 vint 约定必然漂移，而漂移的后果是"同一个头，一处认得出、一处认不出"。</para>
        /// </summary>
        internal static bool CrcMatches(uint stored, ReadOnlySpan<byte> data)
        {
            uint raw = Crc32Raw(data);

            return stored == raw || stored == (raw ^ 0xFFFFFFFF);
        }

        /// <summary>RAR 1.5–4.x 的 16 位头 CRC（= 同一个 CRC32 的低 16 位；取补与不取补两种约定都收）。</summary>
        internal static bool CrcMatches16(ushort stored, ReadOnlySpan<byte> data)
        {
            uint raw = Crc32Raw(data);

            return stored == (ushort)(raw & 0xFFFF) || stored == (ushort)((raw ^ 0xFFFFFFFF) & 0xFFFF);
        }

        private static uint Crc32Raw(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte current in data)
            {
                crc = (crc >> 8) ^ CrcTable[(crc ^ current) & 0xFF];
            }

            return crc;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];

            for (uint index = 0; index < 256; index++)
            {
                uint value = index;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        private static bool SamePath(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a)
            && !string.IsNullOrWhiteSpace(b)
            && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    }
}
