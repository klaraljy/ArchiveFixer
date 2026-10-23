using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **"终态由谁落"的唯一出口**（从 `ExtractionCoordinator` 搬出的第 3 个方法，2026-10-23）。
    ///
    /// <para>只在"还停在 <see cref="TaskOutcome.Pending"/>、已结束、且没通过输出校验"时落一次，
    /// 所以缺卷那几条路（批首预检 / 批末补判 / 只写了中文状态没写终态的分支）都经过它。</para>
    ///
    /// <para><b>判据</b>（用户 2026-10-08 / 2026-10-16 拍板）：状态是"分卷缺失" ⇒ 部分完成；
    /// 其余一律按**整体有没有内容物解出**分档（唯一出口 <see cref="TaskOutcomeClassifier.FromProducedContent"/>）。
    /// ⛔ 不比中文文案；判不出 ⇒ `Failed`（保守）。</para>
    /// </summary>
    public static class OutcomeFinalizer
    {
        public static void FinalizeIfPending(ArchiveTask? task, bool producedContent = false)
        {
            if (task == null)
            {
                return;
            }

            if (task.Outcome != TaskOutcome.Pending
                || !task.EndTime.HasValue
                || task.OutputVerification == OutputVerificationOutcome.Passed)
            {
                return;
            }

            task.Outcome = task.Status == StatusText.VolumeMissing
                ? TaskOutcome.PartiallyCompleted
                : TaskOutcomeClassifier.FromProducedContent(producedContent);
        }
    }
}
