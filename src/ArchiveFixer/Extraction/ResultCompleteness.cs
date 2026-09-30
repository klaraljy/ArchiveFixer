using System;
using System.Globalization;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **这一单的结果能不能被证明是完整的**（检验等级 L4 的三态结论）。
    ///
    /// <para><b>为什么必须是三态</b>：老口径只有"校验通过 / 未通过"两档，而 <c>OutputVerifier</c>
    /// 在**拿不到可信清单**时也会置 <c>Verified = true</c>（只做了"非空 + 非全 0 字节"的底线校验）。
    /// 于是下游所有"校验通过 ⇒ 可以删源包"的闸门<b>分不清"逐条核对过了"与"根本无从判定"</b> ——
    /// 后者恰恰是最危险的那一档：机器手上没有任何证据，却拿到了"可以删"的通行证。</para>
    ///
    /// <para>用户 2026-09-30 定的红线：<b>判不出 ⇒ 一律不删源、结果照常保留、结论如实写"无法确认完整性"</b>。</para>
    ///
    /// <para>⛔ 判据只许读机器终态（枚举 / 校验结果 / 清单）：不许比中文文案，也不许拿任务状态字符串猜。</para>
    /// </summary>
    public enum ResultCompleteness
    {
        /// <summary>
        /// **判不出**：手上没有可信清单（没列过目录 / 列目录失败 / 递归展开 &gt; 1 层），
        /// 或者这一单压根没走到做过校验的那一步。
        ///
        /// <para>这一档的处置**只有一个方向**：不删源、不搬源、结果照常保留，结论如实写"无法确认完整性"。</para>
        /// </summary>
        Undeterminable = 0,

        /// <summary>**可证完整**：有可信清单，且逐条核对通过（文件数 + 总字节都不少于清单）。这是唯一允许删源的档。</summary>
        Complete = 1,

        /// <summary>
        /// **可证不完整（带证据）**：产物为空 / 全是 0 字节 / 少于清单声明的条目。
        /// 证据必须能被机器复述（预期与实际两个数字、或"缺卷名单"）。
        /// </summary>
        Incomplete = 2
    }

    /// <summary>
    /// 一次 L4 裁决（三态 + 证据 + 一句给人看的话）。
    ///
    /// <para>它只描述结论，不做任何动作 —— 删源 / 搬源 / 保留的**判据**读它，
    /// 但动作本身仍归各自的唯一出口（<c>SourceCleanupService</c> / <c>RestItemPurger</c> /
    /// <c>MoveSourcePackageIntoRest</c>）。</para>
    /// </summary>
    public sealed class ResultCompletenessVerdict
    {
        /// <summary>三态结论。</summary>
        public ResultCompleteness State { get; init; } = ResultCompleteness.Undeterminable;

        /// <summary>
        /// 证据的来源字段名（机器可判：<c>OutputVerification.Outcome</c> / <c>ManifestCrossChecked</c> /
        /// <c>MissingVolumeNames</c> 之类）—— 排障时要能一眼看出"这条结论是拿什么判的"。
        /// </summary>
        public string Evidence { get; init; } = string.Empty;

        /// <summary>给人看的一句话（进日志与报告；⛔ 不许拿它当判据）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>可证完整 —— **唯一**允许删除 / 搬走源包的档。</summary>
        public bool AllowsSourceRemoval => State == ResultCompleteness.Complete;
    }

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
        public static ResultCompletenessVerdict Classify(OutputVerificationResult? verification)
        {
            if (verification == null)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Undeterminable,
                    Evidence = "OutputVerification=null",
                    Message = StatusText.CompletenessUndeterminableFormat
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
                    verification.ActualFileCount));
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
                    Message = StatusText.CompletenessUndeterminableFormat
                };
            }

            if (task.MissingVolumeNames.Count > 0)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Incomplete,
                    Evidence = "MissingVolumeNames=" + task.MissingVolumeNames.Count,
                    Message = StatusText.CompletenessIncompleteFormat
                };
            }

            return Classify(
                task.OutputVerification,
                task.OutputManifestCrossChecked,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "OutputVerification={0};ManifestCrossChecked={1}",
                    task.OutputVerification,
                    task.OutputManifestCrossChecked));
        }

        /// <summary>
        /// 三态裁决的**唯一实现**（两个公开重载都收敛到这里）。
        /// </summary>
        private static ResultCompletenessVerdict Classify(
            OutputVerificationOutcome outcome,
            bool manifestCrossChecked,
            string evidence)
        {
            if (outcome == OutputVerificationOutcome.Failed)
            {
                return new ResultCompletenessVerdict
                {
                    State = ResultCompleteness.Incomplete,
                    Evidence = evidence,
                    Message = StatusText.CompletenessIncompleteFormat
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
                Message = StatusText.CompletenessUndeterminableFormat
            };
        }
    }
}
