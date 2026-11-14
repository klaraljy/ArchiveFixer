using System;
using System.Collections.Generic;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 一批任务**会不会中途因为空间不足跑不动**的判断结果（用户 2026-09-29 第 2 条：
    /// "多层 + 空间小 ⇒ 中途才报空间不足……好是可以提醒用户打开空间不足，坏是中途报错"）。
    ///
    /// <para>它**不是放行判据**（放行仍然是每个任务开工前那一道具名空间门
    /// <see cref="SpaceGate"/> 与账本 <see cref="SpaceReservationLedger"/>，用的是
    /// <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>）：这里只是"动手前先说一句"，
    /// ⛔ 不拦任何任务、不改任何设置、不改任何勾选。</para>
    /// </summary>
    public sealed class MultiLayerSpaceRisk
    {
        /// <summary>要不要说这一句（多层可能 + 加起来的量超过可用空间）。</summary>
        public bool Applies { get; init; }

        /// <summary>本批最多允许几层（②页「最大嵌套层数」；1 = 只解一层）。</summary>
        public int MaxLayers { get; init; }

        /// <summary>各任务要从可用空间里**新写**的字节数之和（同 <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/> 的口径）。</summary>
        public long TotalDemandBytes { get; init; }

        /// <summary>排计划那一刻目标盘的可用字节数（-1 = 取不到）。</summary>
        public long AvailableBytes { get; init; }

        /// <summary>差多少字节（够时为 0）。</summary>
        public long ShortfallBytes { get; init; }

        /// <summary>
        /// 取不到可用空间（<see cref="AvailableBytes"/> 为负）= **不判断、也不吓人**。
        ///
        /// <para>与空间门同一条价值观："不敢说够"不等于"说不清楚就报危险" ——
        /// 拿不到数字时凭空报警只会让用户学会忽略它。</para>
        /// </summary>
        public bool Undetermined => AvailableBytes < 0;
    }
}
