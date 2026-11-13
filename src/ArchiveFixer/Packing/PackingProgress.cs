using ArchiveFixer.Models;

namespace ArchiveFixer.Packing
{
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
}
