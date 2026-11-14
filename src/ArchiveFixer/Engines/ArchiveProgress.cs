using System;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 一次归档操作的**统一进度**（AGENTS.md §3.1：统一进度是 M1 骨架的一部分）。
    ///
    /// <para>
    /// 为什么必须只有这一个类型：7-Zip 与 UnRAR 的进度输出形态完全不同
    /// （7z 是 <c>\r</c> 分隔的「百分比 条目序号 命令 条目名」，UnRAR 是同一行里用退格回退的
    /// 「Extracting 路径 … 12%」），但**上层只认这一个类型**。
    /// 各引擎的解析器只允许待在自己的目录里（§3.1 禁止项②），
    /// 归一化后的结果全部走这里。
    /// </para>
    ///
    /// <para>
    /// ⛔ 它<b>不</b>携带密码，也<b>不</b>携带完整输出：只有"到什么程度了"。
    /// </para>
    /// </summary>
    public sealed class ArchiveProgress
    {
        /// <summary>百分比未知（例如 7z 还在扫描阶段、或引擎这一行没给百分比）。</summary>
        public const int UnknownPercent = -1;

        /// <summary>0–100；未知时是 <see cref="UnknownPercent"/>。**不要**把未知当 0。</summary>
        public int Percent { get; init; } = UnknownPercent;

        /// <summary>当前正在处理的条目名（引擎给的是相对/绝对路径都可能；没有时为空串）。</summary>
        public string CurrentEntry { get; init; } = string.Empty;

        /// <summary>
        /// 这一帧属于**续解链的第几层**（0 = 第 0 层 / 不知道）。
        ///
        /// <para>为什么要**结构化**地带上它（用户 2026-10-07：「111.z03 显示解压了三遍 100%」）：
        /// 递归每开一层都会先补一帧 0%（否则进度条会停在上一层的 100% 好几分钟），于是同一行看起来
        /// 像"被解了三遍"。层号落在任务上之后，状态格能如实写成「解压中（整组第 2 层）45%」——
        /// ⛔ 百分比一个数不改，只是把"这是第二层"说清楚。</para>
        /// </summary>
        public int Layer { get; init; }

        /// <summary>已处理字节；引擎不报时为 <see cref="UnknownBytes"/>。</summary>
        public long ProcessedBytes { get; init; } = UnknownBytes;

        /// <summary>总字节；引擎不报时为 <see cref="UnknownBytes"/>。</summary>
        public long TotalBytes { get; init; } = UnknownBytes;

        /// <summary>已处理条目数；引擎不报时为 <see cref="ArchiveProgress.UnknownCount"/>。</summary>
        public int ProcessedEntries { get; init; } = UnknownCount;

        /// <summary>"字节数未知"哨兵。用 -1 而不是 0：0 是"确实一个字节都没处理"。</summary>
        public const long UnknownBytes = -1;

        /// <summary>"条目数未知"哨兵。</summary>
        public const int UnknownCount = -1;

        /// <summary>是不是"只知道在动、不知道到哪了"（扫描 / 分析阶段）。</summary>
        public bool IsIndeterminate => Percent < 0;

        public override string ToString()
        {
            return IsIndeterminate
                ? $"进度未知（{CurrentEntry}）"
                : $"{Percent}%（{CurrentEntry}）";
        }
    }
}
