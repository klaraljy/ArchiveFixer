using ArchiveFixer.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 一次识别结论的**缓存键**（用户 2026-09-29 任务 B："有很多相同文件的情况…每个都花这么多时间识别肯定不行"）。
    ///
    /// <para><b>为什么要按"内容身份"而不是"路径"做键</b>：用户说的"很多相同文件"是**同一个包的若干份拷贝**
    /// （不同目录、不同文件名），按路径缓存一个都命中不了。</para>
    ///
    /// <para><b>四样证据，缺一不可</b>：字节数 + 修改时间 + 已读文件头的指纹 + （必要时）文件尾的指纹。
    /// 前三样是<see cref="DetectCacheEvidence.Head"/>档的全部依据；<see cref="DetectCacheEvidence.HeadAndTail"/>
    /// 档再加尾 256 KiB 的指纹（"认不出格式"那种文件本来就要读这一截，所以多加这条证据是免费的）。</para>
    ///
    /// <para><b>为什么修改时间必须进键</b>：AGENTS.md §6 不变量 11 要求"源文件变化后不得继续使用旧识别结果"。
    /// 少了它，一个"原地改过、大小没变"的文件会命中旧结论 —— 那正是这条不变量禁止的事。</para>
    /// </summary>
    internal readonly struct DetectCacheKey : IEquatable<DetectCacheKey>
    {
        public long Length { get; init; }

        public long LastWriteTicks { get; init; }

        public ulong HeadFingerprint { get; init; }

        public ulong TailFingerprint { get; init; }

        public DetectCacheEvidence Evidence { get; init; }

        public bool Equals(DetectCacheKey other) =>
            Length == other.Length &&
            LastWriteTicks == other.LastWriteTicks &&
            HeadFingerprint == other.HeadFingerprint &&
            TailFingerprint == other.TailFingerprint &&
            Evidence == other.Evidence;

        public override bool Equals(object? obj) => obj is DetectCacheKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Length, LastWriteTicks, HeadFingerprint, Evidence);
    }

    /// <summary>这一条缓存是拿哪些字节算出来的（见 <see cref="DetectCacheKey"/>）。</summary>
    internal enum DetectCacheEvidence
    {
        /// <summary>整个文件都进了指纹（文件比读入上限还短）—— 结论**逐字节可复现**。</summary>
        WholeFile = 0,

        /// <summary>格式在文件头里就定了（头以后的字节与结论无关）。</summary>
        Head = 1,

        /// <summary>头认不出来：还拿了文件尾 256 KiB 的指纹（结论多半出自那一遍顺序扫）。</summary>
        HeadAndTail = 2
    }

    /// <summary>缓存计数（用例靠它证明"相同文件真的走了缓存"，也供排障）。</summary>
    public sealed class DetectCacheStatistics
    {
        public long Hits { get; internal set; }

        public long Misses { get; internal set; }

        public long Stores { get; internal set; }

        public int Entries { get; internal set; }

        public override string ToString() =>
            $"命中 {Hits} / 未命中 {Misses} / 存入 {Stores} / 当前 {Entries} 条";
    }

    /// <summary>
    /// 识别结论的**进程内缓存**（用户 2026-09-29 任务 B）。
    ///
    /// <para><b>它省掉的是什么</b>（2026-09-29 实测，2 GiB 认不出格式的文件）：
    /// 文件头不认识时识别要**顺序扫一遍**找 RAR / 7z 魔数与 ZIP 的 EOCD 候选（上限 512 MiB）——
    /// 20 个一模一样的 64 MiB 文件因此要扫 20 遍，实测 **16.8 秒**；同一批走缓存后只扫一遍。</para>
    ///
    /// <para><b>为什么敢缓存</b>：键里那四样证据（大小 + 修改时间 + 头指纹 + 尾指纹）全同，
    /// 就意味着"这是同一份内容的拷贝"。⛔ 它**不是**"按文件名猜"（那才是这个项目绝不允许的），
    /// 而是"同一份字节不再算第二遍"。</para>
    ///
    /// <para><b>⚠ 已知边界（如实写在这儿，别当成"绝对安全"）</b>：两份文件若在大小、修改时间、
    /// 头 64 KiB、尾 256 KiB 上全同、**只有中间不同**，缓存会把它们当同一份（结论可能出自中间那一遍扫描）。
    /// 这种文件要刻意构造才做得出来（正常拷贝不会只差中间），而换来的是"相同文件不再重复扫 512 MiB"。
    /// 头就定了结论的那一档（<see cref="DetectCacheEvidence.Head"/>）没有这个边界：那些结论只由文件头决定。</para>
    ///
    /// <para><b>RAR 的加密标志不进缓存</b>：它的判据要读头链（读的位置可以越过 64 KiB），
    /// 所以每次都由 <c>ArchiveDetectService.ApplyRarEncryptionVerdict</c> 现算 —— 缓存里存的是"还没叠这一层"的结论。</para>
    /// </summary>
    public static class DetectResultCache
    {
        /// <summary>最多留多少条（一条几百字节；2048 条 ≈ 几百 KB，一批几百个包完全够用）。</summary>
        public const int Capacity = 2048;

        private static readonly object Sync = new();

        private static readonly Dictionary<DetectCacheKey, DetectResult> Entries = new();

        private static readonly Queue<DetectCacheKey> InsertOrder = new();

        private static readonly DetectCacheStatistics Counters = new();

        /// <summary>累计计数（用例用它断言"第二批相同文件全部命中"）。</summary>
        public static DetectCacheStatistics Statistics => Counters;

        internal static bool TryGet(DetectCacheKey key, out DetectResult value)
        {
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out DetectResult? cached) && cached != null)
                {
                    Counters.Hits++;
                    value = cached.Clone();

                    return true;
                }

                Counters.Misses++;
            }

            value = null!;

            return false;
        }

        internal static void Store(DetectCacheKey key, DetectResult value)
        {
            if (value == null)
            {
                return;
            }

            lock (Sync)
            {
                if (Entries.ContainsKey(key))
                {
                    return;
                }

                while (InsertOrder.Count >= Capacity)
                {
                    Entries.Remove(InsertOrder.Dequeue());
                }

                Entries[key] = value.Clone();
                InsertOrder.Enqueue(key);

                Counters.Stores++;
                Counters.Entries = Entries.Count;
            }
        }

        /// <summary>清空（用例之间互不干扰；也供"换了源文件想重算"的场景）。</summary>
        internal static void Clear()
        {
            lock (Sync)
            {
                Entries.Clear();
                InsertOrder.Clear();
                Counters.Hits = 0;
                Counters.Misses = 0;
                Counters.Stores = 0;
                Counters.Entries = 0;
            }
        }

        /// <summary>
        /// 一段字节的**内容指纹**（FNV-1a 变体：一次吃 8 个字节）。
        ///
        /// <para>⚠ 它**不是**加密哈希，也不用来判"有没有被篡改" —— 只用来回答"这两段是不是同一份"。
        /// 8 字节一组是为了别让"给每个文件算一次指纹"本身变成新的热点（按字节算的话，
        /// 34 KB 的文件头要几百微秒，一批几百个包就是几十毫秒）。</para>
        /// </summary>
        internal static ulong Fingerprint(ReadOnlySpan<byte> data)
        {
            const ulong prime = 1099511628211UL;

            ulong hash = 14695981039346656037UL;
            int index = 0;

            for (; index + 8 <= data.Length; index += 8)
            {
                hash = (hash ^ BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(index, 8))) * prime;
            }

            for (; index < data.Length; index++)
            {
                hash = (hash ^ data[index]) * prime;
            }

            return hash;
        }
    }
}
