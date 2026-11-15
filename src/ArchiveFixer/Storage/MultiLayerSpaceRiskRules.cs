using ArchiveFixer.Security;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Storage;

/// <summary>
/// 「多层解压可能中途空间不足」的**唯一判据出口**（用户 2026-09-29 第 2 条）。
///
/// <para>一句话：**本批允许往下多解几层，而各任务加起来要新写的量已经超过盘上的可用空间**，
/// 那就一定会在中途撞上（撞上之后那一单不启动、状态落「磁盘空间不足」，整批照常往下跑）。</para>
///
/// <para><b>为什么会"加起来"超过</b>：每个任务单独看都放得下（所以排计划时一个都不会被点名），
/// 但内容物跑完是**留在盘上**的，一个接一个地占下去迟早会见底；而多层（续解）又让每个包
/// 多出一份"内层包再展开"的量（这一份<b>已经</b>算在
/// <see cref="TaskSpaceEstimate.ProcessArtifactBytes"/> 里，见
/// <c>SpaceEstimator.NestedExpansionAllowance</c>，所以这里不另加 —— 绝不重复计）。</para>
///
/// <para><b>口径只用现成的判据数</b>：<c>Σ 任务的 FreeSpaceDemandBytes</c> 比「可用空间」。
/// ⛔ 不用 <see cref="TaskSpaceEstimate.PeakBytes"/>（含源包）—— 源包已经在盘上、不在可用空间里，
/// 拿它比可用空间正是 2026-09-29 那场真机事故（"17.7 GiB 的包被判成需要 35.41 GiB"）的成因。</para>
/// </summary>
public static class MultiLayerSpaceRiskRules
{
    /// <summary>
    /// 判据本体（纯函数，单独可测）。
    /// </summary>
    /// <param name="multiLayerPossible">
    /// 本批**有没有可能**解出第二层（②页「最大嵌套层数」&gt; 1，或手动档开着递归模式）。
    /// </param>
    /// <param name="maxLayers">本批最多几层（只用于文案，不参与判断）。</param>
    /// <param name="totalDemandBytes">各任务 <c>FreeSpaceDemandBytes</c> 之和（饱和加法，防溢出）。</param>
    /// <param name="availableBytes">排计划那一刻的可用空间（-1 = 取不到 → 不判断）。</param>
    public static MultiLayerSpaceRisk Evaluate(
        bool multiLayerPossible,
        int maxLayers,
        long totalDemandBytes,
        long availableBytes)
    {
        long total = totalDemandBytes > 0 ? totalDemandBytes : 0L;
        int layers = maxLayers > 0 ? maxLayers : 1;

        if (availableBytes < 0)
        {
            // 取不到可用空间：如实标"未判断"，不报警（与空间门"取不到就中性放行"同一条价值观）。
            return new MultiLayerSpaceRisk
            {
                Applies = false,
                MaxLayers = layers,
                TotalDemandBytes = total,
                AvailableBytes = availableBytes
            };
        }

        long shortfall = total > availableBytes ? total - availableBytes : 0L;

        return new MultiLayerSpaceRisk
        {
            Applies = multiLayerPossible && shortfall > 0,
            MaxLayers = layers,
            TotalDemandBytes = total,
            AvailableBytes = availableBytes,
            ShortfallBytes = shortfall
        };
    }

    /// <summary>
    /// 把"各任务的量"加起来（**饱和加法**：回绕成负数等于把这句提醒整个废掉）。
    /// </summary>
    public static long SumDemand(IEnumerable<long>? demands)
    {
        long total = 0;

        foreach (long demand in demands ?? Array.Empty<long>())
        {
            total = TaskSpaceEstimate.SaturatingSum(total, demand);
        }

        return total;
    }
}
