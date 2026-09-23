using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Packing
{
    /// <summary>打包的结论状态。</summary>
    public enum PackingState
    {
        /// <summary>失败（包括"产物校验对不上"）。</summary>
        Failed = 0,

        /// <summary>用户取消。</summary>
        Cancelled = 1,

        /// <summary>成功（外层 rar 做成了，或者明确按"只做 7z 分卷"完成）。</summary>
        Succeeded = 2
    }

    /// <summary>打包走到哪一步了（界面上的"当前步骤"就用它）。</summary>
    public enum PackingStep
    {
        /// <summary>还没开始 / 空闲。</summary>
        Idle = 0,

        /// <summary>准备（校验落点、算空间）。</summary>
        Preparing = 1,

        /// <summary>第一步：7z 加密分卷。</summary>
        Volumes = 2,

        /// <summary>第二步：外层加密 rar。</summary>
        OuterRar = 3,

        /// <summary>第三步：校验产物。</summary>
        Verifying = 4,

        /// <summary>结束。</summary>
        Finished = 5
    }

    /// <summary>一次打包的进度（只带"到哪了"，**不带密码**）。</summary>
    public sealed class PackingProgress
    {
        /// <summary>百分比未知（还在扫描 / 引擎这一行没给百分比）。**不要把未知当 0**。</summary>
        public const int UnknownPercent = -1;

        public PackingStep Step { get; init; } = PackingStep.Idle;

        /// <summary>当前步骤的人读文本（<see cref="StatusText"/> 里的常量）。</summary>
        public string StepText { get; init; } = string.Empty;

        /// <summary>0–100；未知时是 <see cref="UnknownPercent"/>。</summary>
        public int Percent { get; init; } = UnknownPercent;

        /// <summary>引擎正在处理的条目 / 当前状态行（引擎给什么就是什么，可能是路径）。</summary>
        public string Detail { get; init; } = string.Empty;

        public override string ToString()
        {
            return Percent < 0 ? $"{StepText}（{Detail}）" : $"{StepText} {Percent}%（{Detail}）";
        }
    }

    /// <summary>
    /// 一次打包的结果。
    ///
    /// <para><b>不变量 6 的落点</b>：只有 <see cref="PackingState.Succeeded"/> 才算成功。
    /// 失败 / 取消 / 产物校验不通过一律不是成功，界面上也就不会显示成功。</para>
    /// </summary>
    public sealed class PackingResult
    {
        public PackingState State { get; init; } = PackingState.Failed;

        /// <summary>成功（只有 Succeeded 才是 true）。</summary>
        public bool Success => State == PackingState.Succeeded;

        /// <summary>取消。</summary>
        public bool Cancelled => State == PackingState.Cancelled;

        /// <summary>结论那一句话（成功时是"打包成功"，失败时是原因）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>失败 / 取消时给用户看的那句更具体的话（成功时为空）。</summary>
        public string FailureReason { get; init; } = string.Empty;

        /// <summary>结果 rar 的全路径；没做外层（或失败 / 取消）时为 null。</summary>
        public string? RarPath { get; init; }

        /// <summary>rar 的字节数（拿不到时 0）。</summary>
        public long RarBytes { get; init; }

        /// <summary>本次是否跳过了外层 rar（用户勾了"只做 7z 分卷"）。</summary>
        public bool SkippedOuterRar { get; init; }

        /// <summary>产物校验的说明（列了几条、对不对得上）。</summary>
        public string VerificationDetail { get; init; } = string.Empty;

        /// <summary>分卷清单（名字 + 各自大小）。失败与取消时是"已经切出来的那几卷"。</summary>
        public IReadOnlyList<PackingVolume> Volumes { get; init; } = Array.Empty<PackingVolume>();

        /// <summary>逐步骤日志行（界面结果区与主日志共用；**保证不含密码明文**）。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();

        /// <summary>耗时。</summary>
        public TimeSpan Elapsed { get; init; }

        /// <summary>分卷总字节数。</summary>
        public long TotalVolumeBytes => Volumes.Sum(v => v.Bytes);

        /// <summary>
        /// 结果区那一句话（成功时给出 rar 路径 + 大小 + 分卷数；失败时给出原因）。
        /// </summary>
        public string Describe()
        {
            if (State == PackingState.Succeeded)
            {
                if (SkippedOuterRar)
                {
                    return $"已生成 {Volumes.Count} 个 7z 加密分卷（{TaskSpaceEstimate.FormatSize(TotalVolumeBytes)}），"
                         + "按要求没有做外层 rar。";
                }

                return $"打包成功：{RarPath}（{TaskSpaceEstimate.FormatSize(RarBytes)}），"
                     + $"里面是 {Volumes.Count} 个 7z 加密分卷"
                     + (string.IsNullOrWhiteSpace(VerificationDetail) ? "。" : $"；{VerificationDetail}");
            }

            if (State == PackingState.Cancelled)
            {
                return string.IsNullOrWhiteSpace(FailureReason)
                    ? StatusText.PackCancelled
                    : StatusText.PackCancelled + "：" + FailureReason;
            }

            return string.IsNullOrWhiteSpace(FailureReason) ? Message : FailureReason;
        }
    }
}
