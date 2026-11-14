using System.Globalization;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **L4 完整性分类的唯一出口**（用户 2026-09-30 定的检验等级）。
    ///
    /// <para><b>它解决的问题</b>：以前"这个任务的结果可证明完整吗"这个问题没有唯一的答案 ——
    /// 每一道删除闸门各自读 <c>OutputVerification != Passed</c>，于是同一件事在不同地方有不同判法，
    /// 而"拿不到清单"这一档被默默算成了"通过"。</para>
    ///
    /// <list type="number">
    /// <item><description><b>可证不完整</b>（<see cref="ResultCompleteness.Incomplete"/>）：
    /// 校验判否 —— 产物为空、全是 0 字节、或少于清单（证据 = 预期 / 实际两个数字）。</description></item>
    /// <item><description><b>可证完整</b>（<see cref="ResultCompleteness.Complete"/>）：
    /// 校验通过**且**这一次真的拿可信清单逐条核对过
    /// （<see cref="OutputVerificationResult.ManifestCrossChecked"/>）。</description></item>
    /// <item><description><b>判不出</b>（<see cref="ResultCompleteness.Undeterminable"/>，兜底那一档）：
    /// 其余全部情形 —— 没做过校验、只做了非空底线校验、递归多层不给清单。</description></item>
    /// </list>
    ///
    /// <para>⛔ 兜底落在"判不出"（= 什么都不做）那一档，⛔ 不许把"没证据"读成"没问题"。</para>
    /// </summary>
    public static class ResultCompletenessClassifier
    {
        /// <summary>
        /// 按**产物校验结论**分类（校验那一步的原始结论，唯一入口；
        /// <paramref name="verification"/> 为 null = 没做过校验）。
        /// </summary>
        /// <param name="expectation">
        /// 这一次 L3 的预期清单来自哪一层（可选；唯一出口 <see cref="ChainManifestResolver"/>）。
        /// 传进来只为一件事：判不出时能在"为什么没删"那句话里**点名是哪一层、为什么**。
        /// </param>
        public static ResultCompletenessVerdict Classify(
            OutputVerificationResult? verification,
            ManifestExpectation? expectation = null)
        {
            if (verification == null)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Undeterminable,
                    Evidence = "OutputVerification=null",
                    Message = StatusText.CompletenessUndeterminableFormat,
                    Blocker = BuildBlocker(StatusText.CompletenessUndeterminableFormat, expectation)
                };
            }

            if (verification.OnlyZeroByteEntriesMissing)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Undeterminable,
                    Evidence = $"OnlyZeroByteEntriesMissing={verification.MissingEntries.Count}",
                    Message = StatusText.CompletenessOnlyZeroByteGapFormat,
                    Blocker = BuildBlocker(StatusText.CompletenessOnlyZeroByteGapFormat, expectation)
                };
            }

            return Classify(
                verification.Outcome,
                verification.ManifestCrossChecked,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "OutputVerification={0};ManifestCrossChecked={1};Expected={2};Actual={3}",
                    verification.Outcome,
                    verification.ManifestCrossChecked,
                    verification.ExpectedFileCount,
                    verification.ActualFileCount),
                expectation);
        }

        /// <summary>
        /// 按**任务**分类（读的是任务上的机器终态字段，⛔ 不读中文状态文案）。
        ///
        /// <para>缺卷是**先于**校验的硬证据：分卷组不完整时这一单本来就不该开工（不变量 7），
        /// 所以它单独占一档"可证不完整"，排在产物校验之前。</para>
        /// </summary>
        public static ResultCompletenessVerdict Classify(ArchiveTask? task)
        {
            if (task == null)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Undeterminable,
                    Evidence = "task=null",
                    Message = StatusText.CompletenessUndeterminableFormat,
                    Blocker = BuildBlocker(StatusText.CompletenessUndeterminableFormat, null)
                };
            }

            if (task.MissingVolumeNames.Count > 0)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Incomplete,
                    Evidence = "MissingVolumeNames=" + task.MissingVolumeNames.Count,
                    Message = StatusText.CompletenessIncompleteFormat,
                    Blocker = BuildBlocker(StatusText.CompletenessIncompleteFormat, task.ManifestExpectation)
                };
            }

            if (task.OutputOnlyZeroByteEntriesMissing)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Undeterminable,
                    Evidence = "OutputOnlyZeroByteEntriesMissing=true",
                    Message = StatusText.CompletenessOnlyZeroByteGapFormat,
                    Blocker = BuildBlocker(StatusText.CompletenessOnlyZeroByteGapFormat, task.ManifestExpectation)
                };
            }

            return Classify(
                task.OutputVerification,
                task.OutputManifestCrossChecked,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "OutputVerification={0};ManifestCrossChecked={1}",
                    task.OutputVerification,
                    task.OutputManifestCrossChecked),
                task.ManifestExpectation);
        }

        /// <summary>
        /// 三态裁决的**唯一实现**（两个公开重载都收敛到这里）。
        /// </summary>
        private static ResultCompletenessVerdict Classify(
            OutputVerificationOutcome outcome,
            bool manifestCrossChecked,
            string evidence,
            ManifestExpectation? expectation)
        {
            if (outcome == OutputVerificationOutcome.Failed)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Incomplete,
                    Evidence = evidence,
                    Message = StatusText.CompletenessIncompleteFormat,
                    Blocker = BuildBlocker(StatusText.CompletenessIncompleteFormat, expectation)
                };
            }

            /*
             * 校验通过，但**通过的是哪一档**要分开看：
             * · ManifestCrossChecked = true → 拿可信清单逐条核对过 ⇒ 可证完整（唯一允许删源的一档）；
             * · false → 只做了"非空 + 非全 0 字节"的底线校验 ⇒ **判不出**。
             *
             * 这一条正是本类存在的理由：老口径下两者都是 Verified = true，分不开。
             */
            if (outcome == OutputVerificationOutcome.Passed && manifestCrossChecked)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Complete,
                    Evidence = evidence,
                    Message = StatusText.CompletenessCompleteFormat
                };
            }

            // 兜底（NotAttempted / 只做了底线校验）：**判不出** ⇒ 什么都不做。
            return new ResultCompletenessVerdict
            {
                State = ResultCompleteness.Undeterminable,
                Evidence = evidence,
                Message = StatusText.CompletenessUndeterminableFormat,
                Blocker = BuildBlocker(StatusText.CompletenessUndeterminableFormat, expectation)
            };
        }

        /// <summary>
        /// 「为什么没删源包：&lt;判据&gt;；&lt;哪一层 + 原因&gt;」—— 全仓**唯一**一处拼这句话。
        /// </summary>
        private static string BuildBlocker(string criterion, ManifestExpectation? expectation)
        {
            string reason = expectation?.UnavailableReason ?? string.Empty;

            return reason.Length == 0
                ? string.Format(CultureInfo.CurrentCulture, StatusText.SourceRemovalBlockedFormat, criterion)
                : string.Format(CultureInfo.CurrentCulture, StatusText.SourceRemovalBlockedDetailFormat, criterion, reason);
        }
    }
}
