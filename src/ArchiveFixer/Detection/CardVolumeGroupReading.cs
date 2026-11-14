using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection
{
    /// <summary>「这一层里的分卷片归成一组」的结论。</summary>
    public sealed class CardVolumeGroupReading
    {
        /// <summary>可证完整（片数 + 字节数都对得上首卷自述，且体积规律成立）。</summary>
        public bool Complete { get; init; }

        /// <summary>这一层里有没有**首卷**（有它才有"整包应当多少字节"这条自述）。</summary>
        public bool HasFirstPiece { get; init; }

        /// <summary>参与这一组的片数。</summary>
        public int PieceCount { get; init; }

        /// <summary>首卷自述的整包字节数（没有首卷时为 0）。</summary>
        public long DeclaredTotalBytes { get; init; }

        /// <summary>这几片实际的字节数之和。</summary>
        public long SumBytes { get; init; }

        /// <summary>槽位（只在可证完整时有值）。</summary>
        public IReadOnlyList<CardVolumeSlot> Slots { get; init; } = Array.Empty<CardVolumeSlot>();

        /// <summary>
        /// 这一组的**入口那一份**（引擎这一趟要打开的文件）。判据只有一条：卡上自证
        /// `BodyKind = VolumeFirstPiece` 的那一份 —— 它自己就是"从它开始解"的那一片
        /// （7z / RAR 老式 / ZIP 通用分片这一族；跨盘 ZIP 的入口是末片 `X.zip`，由族规则另行回答）。
        /// 空 = 这一层里没有首卷 ⇒ **判不出**（⛔ 不拿名字顶替）。
        /// </summary>
        public string EntryPath { get; init; } = string.Empty;

        /// <summary>
        /// **还差多少字节**（`DeclaredTotalBytes - SumBytes`，负数按 0）。
        /// 唯一口径：批末那道"卡上说还差字节 ⇒ 这一趟先不收"的闸门、以及日志里的那句话，
        /// ⛔ 都从这里读，别各自再算一遍（用户 2026-10-17：「同一件事的真值只允许有一个出口」）。
        /// </summary>
        public long MissingBytes => Math.Max(0L, DeclaredTotalBytes - SumBytes);

        /// <summary>
        /// **这一组的命名族（由内容证据定）**：ZIP 那一族名字分不开的两个子族就靠它分开
        /// （`ZipNumbered` = 通用分片 `X.zip.001`；`ZipSpanned` = 跨盘 `X.z01…X.zip`）。
        /// 判不出 ⇒ null（调用方退回按名字样本定族，与老口径逐字相同）。
        /// </summary>
        public VolumeNamingFamily? NamingFamily { get; init; }

        /// <summary>判据输入（一句人话：为什么是这个结论）。</summary>
        public string Reason { get; init; } = string.Empty;

        internal static CardVolumeGroupReading None(string reason) => new()
        {
            Reason = reason
        };

        internal static CardVolumeGroupReading Incomplete(
            string reason,
            bool hasFirstPiece,
            int pieceCount,
            long declaredTotalBytes,
            long sumBytes,
            string entryPath,
            IReadOnlyList<CardVolumeSlot>? slots = null,
            VolumeNamingFamily? namingFamily = null) => new()
            {
                Complete = false,
                HasFirstPiece = hasFirstPiece,
                PieceCount = pieceCount,
                DeclaredTotalBytes = declaredTotalBytes,
                SumBytes = sumBytes,
                EntryPath = entryPath,
                Slots = slots ?? Array.Empty<CardVolumeSlot>(),
                NamingFamily = namingFamily,
                Reason = reason
            };
    }
}
