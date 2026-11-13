using ArchiveFixer.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ArchiveFixer.Detection
{
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
    /// <para><b>加密标志不进缓存</b>（RAR / ZIP / 7z 三个格式一视同仁，用户 2026-09-30）：
    /// 它们的判据要读头链 / 中央目录 / 7z 的 next header（读的位置可以越过 64 KiB），
    /// 所以每次都由 <c>ArchiveDetectService.ApplyEncryptionVerdict</c> 现算 —— 缓存里存的是"还没叠这一层"的结论。</para>
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
