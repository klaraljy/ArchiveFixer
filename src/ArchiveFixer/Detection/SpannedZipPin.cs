using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection
{
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
}
