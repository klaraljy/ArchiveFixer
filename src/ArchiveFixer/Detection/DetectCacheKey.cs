using System;

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
}
