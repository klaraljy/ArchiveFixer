using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 分卷索引里的一条**位置锚点**：某一盘上、相对该盘起点的某个偏移处，**应当**有一个本地文件头，
    /// 而且它的文件名就是 <see cref="NameBytes"/>。
    ///
    /// <para>它从哪来：跨盘 zip 的**末片**里那份中央目录。中央目录的每一条记录都写着
    /// "这个文件的本地头在**第几盘**、离那一盘开头多少字节"（<c>disk number start</c> +
    /// <c>relative offset of local header</c>）—— 那是**归档自己**说的话，不是我们猜的。</para>
    /// </summary>
    public sealed class SpannedZipAnchor
    {
        /// <summary>盘号（**0 起**，与 EOCD 里的盘号同一个坐标系）。</summary>
        public int Disk { get; init; }

        /// <summary>相对**该盘起点**的偏移。</summary>
        public long RelativeOffset { get; init; }

        /// <summary>本地头里的文件名（**原始字节**：比字节，不比解码后的字符串 —— 免得编码差异把真锚点判掉）。</summary>
        public byte[] NameBytes { get; init; } = Array.Empty<byte>();

        /// <summary>给日志用的名字（尽力解码，仅供人读）。</summary>
        public string Name { get; init; } = string.Empty;
    }

    /// <summary>
    /// 把"手上的这一堆文件"分配到各盘号上的结论。
    ///
    /// <para><b>只有两种状态是结论</b>：<see cref="Slots"/> 里有路径 = 这一盘**被内容证明了**是谁；
    /// <see cref="UndecidedDisks"/> = 这一盘内容里没有任何锚点（谁的字节都对得上）⇒ **定不下来**；
    /// <see cref="MissingDisks"/> = 这一盘有锚点、可整个候选池里没有一份对得上 ⇒ **那一盘不在手上**。</para>
    /// </summary>
    public sealed class SpannedZipPin
    {
        /// <summary>每一盘 → 文件路径（<c>null</c> = 还没定下来）。长度 = 总片数，最后一位是末片。</summary>
        public IReadOnlyList<string?> Slots { get; init; } = Array.Empty<string?>();

        /// <summary>内容里没有锚点、因此定不下先后的盘号（0 起）。</summary>
        public IReadOnlyList<int> UndecidedDisks { get; init; } = Array.Empty<int>();

        /// <summary>有锚点却没人对得上的盘号（0 起）—— 这几片**不在手上**。</summary>
        public IReadOnlyList<int> MissingDisks { get; init; } = Array.Empty<int>();

        /// <summary>总片数（末片 EOCD 的盘号 + 1）。</summary>
        public int DiskCount { get; init; }

        /// <summary>已经被内容钉住的片数（含末片）。</summary>
        public int PinnedCount => Slots.Count(s => !string.IsNullOrWhiteSpace(s));

        /// <summary>下结论了：每一盘都钉住了、而且一片都不缺。</summary>
        public bool AllPinned => DiskCount > 1
            && Slots.Count == DiskCount
            && UndecidedDisks.Count == 0
            && MissingDisks.Count == 0;

        /// <summary>
        /// 剩下那几片**互相之间**可以互换（都定不下来）。数量 = <see cref="UndecidedDisks"/>.Count 时才自洽；
        /// 对不上说明候选池里多/少了文件，调用方按"判不出"处理。
        /// </summary>
        public IReadOnlyList<string> UndecidedCandidates { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 自洽：候选池里剩下的份数恰好等于定不下来的盘数（少了 = 缺卷，多了 = 混进了无关文件）。
        ///
        /// <para>全都钉住时**不要求**"剩下的候选为 0"：同目录里多一个同样大小的无关文件不影响结论
        /// （每一片都有身份证，谁也冒充不了谁）。只有"还有几片定不下来"时，才对得上号才敢往下走。</para>
        /// </summary>
        public bool Consistent => UndecidedDisks.Count == 0
            || UndecidedCandidates.Count == UndecidedDisks.Count;
    }

    /// <summary>
    /// **跨盘 zip 的"按归档自己的索引定盘"**（用户 2026-10-01 真机 <c>FFF\111</c> 那一组 7 片：
    /// 6 片满片 + 1 片小的末片，名字里一个卷号都没有 —— 名字路与内容路都判不出来）。
    ///
    /// <para><b>为什么这条能成</b>：跨盘 zip 的**末片**里有中央目录，而中央目录的每一条记录都写着
    /// "这个文件的本地头在**第几盘**、离那一盘开头多少字节"。于是每一片都有一张"身份证"：
    /// 把候选文件当第 k 盘，去它 <c>相对偏移</c> 处看一眼 —— 那里必须是本地文件头
    /// <c>PK\x03\x04</c>、而且跟着的文件名必须与中央目录里那一模一样。对得上就是它，对不上就不是。
    /// <b>这条判据不需要名字、不需要引擎、不需要密码</b>（中央目录本身不加密）。</para>
    ///
    /// <para><b>⚠ 什么时候还是定不下来</b>：某一片里**一个条目都没开始**（它的区间整段落在某个文件的数据中间）
    /// ⇒ 那一片里一个本地头都没有 ⇒ 它和另一片同样"没有锚点"的候选**在字节上完全对称**（除末片外每一片都等大），
    /// 谁在前谁在后**内容里没有任何信息**。这一档如实报 <see cref="SpannedZipPin.UndecidedDisks"/>，
    /// 由调用方决定要不要请引擎试拼（⛔ 绝不靠猜排顺序）。</para>
    ///
    /// <para>本类是**纯读**：只读末片的 EOCD 与中央目录、在每个候选的指定偏移处读几十个字节，
    /// 不建文件、不改名字、不调引擎。</para>
    /// </summary>
    public sealed class SpannedZipIndex
    {
        /// <summary>EOCD 定长部分（签名 4 + 18）。</summary>
        private const int EndOfCentralDirectorySize = 22;

        /// <summary>zip 注释最长 65535 —— EOCD 最多往回找这么多字节。</summary>
        private const int ZipCommentMaxLength = 0xFFFF;

        /// <summary>本地文件头签名 <c>PK\x03\x04</c>。</summary>
        private const uint LocalHeaderSignature = 0x04034B50;

        /// <summary>中央目录记录签名 <c>PK\x01\x02</c>。</summary>
        private const uint CentralHeaderSignature = 0x02014B50;

        /// <summary>跨盘标记 <c>PK\x07\x08</c>（真 PKZIP 跨盘的第 1 片以它开头，实测）。</summary>
        private const uint SpannedMarkerSignature = 0x08074B50;

        /// <summary>中央目录一次读进内存的上限（条目极多的大包才会有这么大；超了如实判不出）。</summary>
        private const int CentralDirectoryReadLimit = 32 * 1024 * 1024;

        /// <summary>本地头里从签名到"文件名长度"字段结尾的定长部分。</summary>
        private const int LocalHeaderFixedSize = 30;

        private SpannedZipIndex()
        {
        }

        /// <summary>总片数（末片 EOCD 的盘号 + 1）。</summary>
        public int DiskCount { get; private init; }

        /// <summary>读索引的那一份（= 末片）的完整路径。</summary>
        public string TailPath { get; private init; } = string.Empty;

        /// <summary>末片的字节数。</summary>
        public long TailLength { get; private init; }

        /// <summary>中央目录里的全部锚点（含目录条目）。</summary>
        public IReadOnlyList<SpannedZipAnchor> Anchors { get; private init; } = Array.Empty<SpannedZipAnchor>();

        /// <summary>这一盘上有几条锚点（0 = 内容里没有条目起点）。</summary>
        public int AnchorCountOf(int disk) => Anchors.Count(a => a.Disk == disk);

        /// <summary>
        /// 从一个文件里读"跨盘 zip 的索引"。不是跨盘 zip（单盘 / 不是 zip / 末片读不动）一律返回 <c>null</c>，
        /// **绝不抛**。
        /// </summary>
        public static SpannedZipIndex? TryRead(string? tailPath)
        {
            try
            {
                return TryReadCore(tailPath);
            }
            catch
            {
                // 读不动（被占 / 权限 / 半路被删）= 判不出：调用方什么都不做。
                return null;
            }
        }

        private static SpannedZipIndex? TryReadCore(string? tailPath)
        {
            if (string.IsNullOrWhiteSpace(tailPath) || !File.Exists(tailPath))
            {
                return null;
            }

            long length = new FileInfo(tailPath).Length;

            if (length < EndOfCentralDirectorySize)
            {
                return null;
            }

            int window = (int)Math.Min(length, EndOfCentralDirectorySize + ZipCommentMaxLength);
            byte[] tail = ReadBytes(tailPath, length - window, window);

            if (tail.Length < window)
            {
                return null;
            }

            int eocd = -1;

            for (int index = window - EndOfCentralDirectorySize; index >= 0; index--)
            {
                if (tail[index] != 0x50 || tail[index + 1] != 0x4B
                    || tail[index + 2] != 0x05 || tail[index + 3] != 0x06)
                {
                    continue;
                }

                ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2));

                if (index + EndOfCentralDirectorySize + commentLength != window)
                {
                    continue;
                }

                eocd = index;
                break;
            }

            if (eocd < 0)
            {
                return null;
            }

            Span<byte> record = tail.AsSpan(eocd);
            ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
            ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));
            ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(10, 2));
            uint centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(12, 4));
            uint centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(16, 4));

            /*
             * 三个前提，缺一条就判不出（⛔ 不许硬凑）：
             * ① 盘号 > 0：单盘 zip 没有"第几盘"可言（7-Zip 的 -tzip -v 末片写的就是 0/0）；
             * ② 中央目录整段在末片里（起盘 == 本盘）：跨了盘就读不全，条目会缺；
             * ③ 偏移与大小自洽，而且中央目录正好接在 EOCD 前面。
             */
            if (diskNumber == 0 || centralDirectoryDisk != diskNumber || totalEntries == 0)
            {
                return null;
            }

            if (centralDirectorySize == 0 || centralDirectorySize > CentralDirectoryReadLimit)
            {
                return null;
            }

            if (centralDirectoryOffset > length
                || centralDirectoryOffset + centralDirectorySize != length - window + eocd)
            {
                return null;
            }

            byte[] directory = ReadBytes(tailPath, centralDirectoryOffset, (int)centralDirectorySize);

            if (directory.Length < (int)centralDirectorySize)
            {
                return null;
            }

            List<SpannedZipAnchor>? anchors = ParseAnchors(directory, diskNumber);

            if (anchors == null || anchors.Count == 0)
            {
                return null;
            }

            return new SpannedZipIndex
            {
                DiskCount = diskNumber + 1,
                TailPath = tailPath,
                TailLength = length,
                Anchors = anchors
            };
        }

        /// <summary>逐条读中央目录记录；认不出（签名不对 / ZIP64 偏移读不出来）返回 <c>null</c>。</summary>
        private static List<SpannedZipAnchor>? ParseAnchors(byte[] directory, int lastDisk)
        {
            var anchors = new List<SpannedZipAnchor>();
            int offset = 0;

            while (offset + 46 <= directory.Length)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset, 4)) != CentralHeaderSignature)
                {
                    return null;
                }

                ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 28, 2));
                ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 30, 2));
                ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 32, 2));
                ushort diskStart = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 34, 2));
                uint relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 42, 4));
                uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 20, 4));
                uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 24, 4));
                int nameAt = offset + 46;

                if (nameAt + nameLength + extraLength + commentLength > directory.Length)
                {
                    return null;
                }

                /*
                 * ZIP64：偏移 / 盘号写满了 0xFFFFFFFF / 0xFFFF 时，真值在扩展区 0x0001 里，
                 * 字段顺序是"哪个满了就有哪个"（未压缩大小 → 压缩大小 → 本地头偏移 → 盘号）。
                 * 读得出就用真值，读不出（本程序不猜）就整份判不出。
                 */
                if (diskStart == 0xFFFF || relativeOffset == 0xFFFFFFFF)
                {
                    if (!TryReadZip64Location(
                            directory.AsSpan(nameAt + nameLength, extraLength),
                            uncompressedSize == 0xFFFFFFFF,
                            compressedSize == 0xFFFFFFFF,
                            relativeOffset == 0xFFFFFFFF,
                            diskStart == 0xFFFF,
                            out relativeOffset,
                            out diskStart))
                    {
                        return null;
                    }
                }

                if (diskStart > lastDisk)
                {
                    // 中央目录说这一条起在比末片还靠后的盘上 —— 自相矛盾。
                    return null;
                }

                byte[] nameBytes = directory.AsSpan(nameAt, nameLength).ToArray();

                anchors.Add(new SpannedZipAnchor
                {
                    Disk = diskStart,
                    RelativeOffset = relativeOffset,
                    NameBytes = nameBytes,
                    Name = Encoding.UTF8.GetString(nameBytes)
                });

                offset = nameAt + nameLength + extraLength + commentLength;
            }

            return offset == directory.Length ? anchors : null;
        }

        /// <summary>
        /// 从 ZIP64 扩展区里取"本地头偏移 + 起盘"。字段**按顺序只出现"满的那几个"**：
        /// 未压缩大小(8) → 压缩大小(8) → 本地头偏移(8) → 起盘(4)。⛔ 猜错顺序就会把大小读成偏移。
        /// </summary>
        private static bool TryReadZip64Location(
            ReadOnlySpan<byte> extra,
            bool uncompressedSizeFull,
            bool compressedSizeFull,
            bool relativeOffsetFull,
            bool diskStartFull,
            out uint resolvedOffset,
            out ushort resolvedDisk)
        {
            resolvedOffset = 0;
            resolvedDisk = 0;

            int at = 0;

            while (at + 4 <= extra.Length)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(at, 2));
                ushort size = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(at + 2, 2));
                int body = at + 4;

                if (body + size > extra.Length)
                {
                    return false;
                }

                if (id == 0x0001)
                {
                    int cursor = body;
                    int end = body + size;

                    if (uncompressedSizeFull)
                    {
                        cursor += 8;
                    }

                    if (compressedSizeFull)
                    {
                        cursor += 8;
                    }

                    if (relativeOffsetFull)
                    {
                        if (cursor + 8 > end)
                        {
                            return false;
                        }

                        resolvedOffset = BinaryPrimitives.ReadUInt32LittleEndian(extra.Slice(cursor, 4));
                        cursor += 8;
                    }

                    if (diskStartFull)
                    {
                        if (cursor + 4 > end)
                        {
                            return false;
                        }

                        resolvedDisk = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(cursor, 4));
                    }

                    return true;
                }

                at = body + size;
            }

            return false;
        }

        /// <summary>
        /// 把候选文件分配到各盘号上（判据 = 归档自己的锚点 + "除末片外每一片都等大"这条尺寸事实）。
        ///
        /// <para>⛔ 只认"唯一对得上"：一条锚点被两个候选同时满足 ⇒ 两片都留着不定；
        /// 一条谁都不满足 ⇒ 这一片**不在手上**（<see cref="SpannedZipPin.MissingDisks"/>）。</para>
        /// </summary>
        public SpannedZipPin Pin(IReadOnlyList<VolumeCandidate>? candidates)
        {
            var slots = new string?[DiskCount];
            slots[DiskCount - 1] = TailPath;   // 末片：EOCD 自己说的盘号就是它

            var undecided = new List<int>();
            var missing = new List<int>();

            IReadOnlyList<VolumeCandidate> pool = BuildPool(candidates);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TailPath };
            var missingSet = new HashSet<int>();

            /*
             * 逐盘钉：一条锚点都不满足 ⇒ 这一片不在手上；恰好一个候选满足 ⇒ 就是它；
             * 两个以上满足 ⇒ 这一轮先放一放。反复扫到不再有进展为止
             * （先钉住的盘会把候选从池子里拿走，后面那些"两个都对得上"的盘就只剩一个了）。
             */
            bool progress = true;

            while (progress)
            {
                progress = false;

                for (int disk = 0; disk < DiskCount - 1; disk++)
                {
                    if (!string.IsNullOrWhiteSpace(slots[disk]) || missingSet.Contains(disk))
                    {
                        continue;
                    }

                    List<SpannedZipAnchor> anchors = Anchors.Where(a => a.Disk == disk).ToList();

                    if (anchors.Count == 0 && disk != 0)
                    {
                        /*
                         * 这一盘内容里**一个条目都没开始**（整段落在某个文件的数据中间）⇒ 它没有身份证：
                         * 谁当它都行。留到收尾按"定不下来"如实报，⛔ 绝不按名字/时间猜一个。
                         */
                        continue;
                    }

                    List<string> matches = pool
                        .Where(c => !used.Contains(c.Path))
                        .Where(c => anchors.Count > 0
                            ? MatchesAnchors(c.Path, anchors)
                            : StartsWithSpannedMarker(c.Path))
                        .Select(c => c.Path)
                        .ToList();

                    if (matches.Count == 1)
                    {
                        slots[disk] = matches[0];
                        used.Add(matches[0]);
                        progress = true;
                    }
                    else if (matches.Count == 0 && anchors.Count > 0)
                    {
                        // 有锚点却没人对得上 ⇒ 这一片**不在手上**（不是"判不出"，是"缺"）。
                        missingSet.Add(disk);
                        progress = true;
                    }
                }
            }

            /*
             * 还剩没钉的盘：内容里没有锚点（或者锚点被两个候选同时满足）。
             * 只有在"剩下的候选数 == 剩下的盘数"时才算自洽 —— 那一档如实报"这几片定不下来"，
             * 由调用方决定要不要请引擎试拼。多余的候选（同尺寸的无关文件）会让它不自洽 ⇒ 判不出。
             */
            for (int disk = 0; disk < DiskCount - 1; disk++)
            {
                if (string.IsNullOrWhiteSpace(slots[disk]) && !missingSet.Contains(disk))
                {
                    undecided.Add(disk);
                }
            }

            List<string> leftovers = pool
                .Where(c => !used.Contains(c.Path))
                .Select(c => c.Path)
                .ToList();

            return new SpannedZipPin
            {
                DiskCount = DiskCount,
                Slots = slots,
                UndecidedDisks = undecided,
                MissingDisks = missingSet.OrderBy(d => d).ToList(),
                UndecidedCandidates = leftovers
            };
        }

        /// <summary>
        /// 候选池 = 除末片外、"大小正好等于满片尺寸"的那些。
        ///
        /// <para><b>满片尺寸</b> = 这些候选里出现次数最多的那个大小 —— 跨盘 zip 的**非末片**都是切分上限那么大的满片
        /// （真机与真工具实测：WinRAR 切出来的 <c>.z01…</c> 每一片都正好等于切分大小）。</para>
        ///
        /// <para>⚠ <b>"末卷更小"不是普遍成立</b>（用户 2026-10-01 提的这条直觉，实测被否掉一半）：
        /// WinRAR 在**条目比切分大小还大**时，最后一个 <c>.zip</c> 可以装下比一片更多的数据 ——
        /// 实测 3 个 100 KB 的条目切成 64 KB 一片时，盘上是 <c>.z01=64K / .z02=64K / .zip=172K</c>
        /// （末片比满片大一倍多）。所以这里**只**用"非末片彼此等大"这一条，⛔ **不要求末片更小** ——
        /// 拿一条不成立的规律当闸门，只会把真组挡在门外。</para>
        /// </summary>
        private IReadOnlyList<VolumeCandidate> BuildPool(IReadOnlyList<VolumeCandidate>? candidates)
        {
            var others = (candidates ?? Array.Empty<VolumeCandidate>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path) && c.Size > 0)
                .Where(c => !SamePath(c.Path, TailPath))
                .ToList();

            long full = FullPartSize(others);

            if (full <= 0)
            {
                // 认不出满片尺寸（一个候选都没有 / 众数并列）⇒ 判不出。
                return Array.Empty<VolumeCandidate>();
            }

            return others
                .Where(c => c.Size == full)
                .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>满片尺寸 = 候选里出现次数最多的那个大小（并列就取不出来 ⇒ 0 = 判不出）。</summary>
        private static long FullPartSize(IReadOnlyList<VolumeCandidate> candidates)
        {
            if (candidates.Count == 0)
            {
                return 0;
            }

            List<IGrouping<long, VolumeCandidate>> groups = candidates
                .GroupBy(c => c.Size)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (groups.Count > 1 && groups[0].Count() == groups[1].Count())
            {
                return 0;
            }

            return groups[0].Key;
        }

        /// <summary>这个文件在指定的那些偏移处，是不是真的各有一个"名字对得上"的本地文件头。</summary>
        private static bool MatchesAnchors(string path, IReadOnlyList<SpannedZipAnchor> anchors)
        {
            foreach (SpannedZipAnchor anchor in anchors)
            {
                if (!MatchesAnchor(path, anchor))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesAnchor(string path, SpannedZipAnchor anchor)
        {
            int need = LocalHeaderFixedSize + anchor.NameBytes.Length;
            byte[] head = ReadBytes(path, anchor.RelativeOffset, need);

            if (head.Length < need)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0, 4)) != LocalHeaderSignature)
            {
                return false;
            }

            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(26, 2));

            if (nameLength != anchor.NameBytes.Length)
            {
                return false;
            }

            return head.AsSpan(LocalHeaderFixedSize, nameLength).SequenceEqual(anchor.NameBytes);
        }

        /// <summary>
        /// 这一片是不是"从跨盘标记 <c>PK\x07\x08</c> 开头"—— 真 PKZIP / WinRAR 造的跨盘 zip
        /// **第 1 盘**就长这样（真机两组样本实测：DDD 的 <c>222.z删除01</c>、FFF 的 <c>222.z0删除A</c>）。
        /// 它只是"这是第 1 盘"的**充分**证据（中央目录里的锚点更硬），所以只在第 0 盘没有锚点时用。
        /// </summary>
        private static bool StartsWithSpannedMarker(string path)
        {
            byte[] head = ReadBytes(path, 0, 8);

            return head.Length >= 8
                && BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0, 4)) == SpannedMarkerSignature
                && BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4, 4)) == LocalHeaderSignature;
        }

        private static byte[] ReadBytes(string path, long offset, int count)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                if (offset < 0 || offset + count > stream.Length)
                {
                    return Array.Empty<byte>();
                }

                stream.Seek(offset, SeekOrigin.Begin);

                var buffer = new byte[count];
                int read = 0;

                while (read < count)
                {
                    int step = stream.Read(buffer, read, count - read);

                    if (step <= 0)
                    {
                        break;
                    }

                    read += step;
                }

                return read == count ? buffer : buffer.AsSpan(0, read).ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
