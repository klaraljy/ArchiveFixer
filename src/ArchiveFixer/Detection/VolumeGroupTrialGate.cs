using System;

namespace ArchiveFixer.Detection;

/// <summary>
/// 试开闸门 —— 把**决定性证据**（引擎按推定顺序试开）并进基准结论，并回答"该升、该降、还是保持不变"。
///
/// <para><b>为什么独立成层</b>：这是唯一一条能**一票定案**、也能把"靠名字判完整"降级的证据。
/// 它的三条落法（做不了 ⇒ 判不出 / 成立 ⇒ 完整 / 试过不成立 ⇒ 组错）就是失败模型本身，
/// 与"怎么取名字、怎么排候选"必须分开看：⛔ 它不许反过来放宽任何名字/体积判据。</para>
///
/// <para><b>失败模型</b>（用户 2026-09-30 决定）：</para>
/// <list type="bullet">
/// <item><description><b>做不了</b>（跨盘 / 没有工作区根）⇒ 结论**如实降成判不出**，
/// 但组成员清单照旧带出去（保护/删除闸门一个字不放松），可删资格给 <c>false</c>；</description></item>
/// <item><description><b>没试成</b>（没引擎 / 凑不出顺序）⇒ 这一次没取到证据，结论不变；</description></item>
/// <item><description><b>试过但不成立</b> ⇒ 组错：原来"靠名字判完整"的降成"疑缺卷"，其余保持原结论。</description></item>
/// </list>
///
/// <para>生命周期：纯静态、无状态；不调引擎、不建工作区、不碰文件 —— 试开本身由调用方注入的
/// <see cref="VolumeTrialOpen"/> 端口完成，这里只消费它给出的结构化结论。</para>
/// </summary>
internal static class VolumeGroupTrialGate
{
    /// <summary>把试开的结论并进基准结论（试开是决定性证据，成立就一票升级）。</summary>
    public static VolumeGroupResolution ApplyTrialOutcome(
        VolumeGroupResolution baseline,
        VolumeTrialOutcome trial)
    {
        string detail = trial.Attempted
            ? $"试了 {trial.Attempts} 种排列：{trial.Reason}"
            : VolumeGroupFailureRules.IsUnconfirmable(trial.SkipReason)
                ? $"这一条证据取不到（{VolumeGroupFailureRules.DescribeSkip(trial.SkipReason)}）⇒ 结论如实降成「判不出」：{trial.Reason}"
                : $"没试成（这一档是「没试」，不是「不成立」）：{trial.Reason}";

        VolumeEvidence row = VolumeGroupEvidenceBuilder.Row(
            VolumeEvidenceKind.TrialOpen,
            VolumeEvidenceWeight.Decisive,
            !trial.Attempted
                ? VolumeEvidenceOutcome.Unknown
                : trial.Confirmed ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.NotSatisfied,
            "试开确认",
            detail);

        if (!trial.Attempted)
        {
            /*
             * ===== 试开做不了 ⇒ **判不出**（用户 2026-09-30 决定，协调者复核）=====
             *
             * 两档落在这里（见 <see cref="VolumeGroupFailureRules.IsUnconfirmable"/>）：
             *   · **没有工作区根**（拿不到这一单的目标目录，例如改名那条路）—— 不许另找地方开工作区；
             *   · **跨盘**（硬链接不能跨卷）—— 不许退到源卷根去试。
             *
             * 于是这一档如实降级成 <see cref="VolumeGroupVerdict.Undetermined"/>：
             *   · 组成员的清单**照旧带出去**（GroupFilePaths 不变）⇒ 下游的保护/删除闸门一个字不放松；
             *   · CanEnterDeletableRestItems = false ⇒ 不删源、不移动源；
             *   · 文案如实写"无法确认"，⛔ 绝不许写成"已按试开验证"。
             */
            if (VolumeGroupFailureRules.IsUnconfirmable(trial.SkipReason))
            {
                return new VolumeGroupResolution
                {
                    Verdict = VolumeGroupVerdict.Undetermined,
                    AnchorPath = baseline.AnchorPath,
                    BaseName = baseline.BaseName,
                    Volumes = baseline.Volumes,
                    MissingVolumeNames = Array.Empty<string>(),
                    PositionInferredNotes = baseline.PositionInferredNotes,
                    Evidence = VolumeGroupEvidenceBuilder.Concat(baseline.Evidence, row),
                    Reason = "判不出：" + VolumeGroupFailureRules.DescribeSkip(trial.SkipReason) + "。"
                        + "⛔ 判不出 ⇒ 不删源、不移动源、不改名；要确认就得让这一组和它的工作区"
                        + "在同一个盘上、并且有目标目录可以落工作区",
                    GroupFilePaths = baseline.GroupFilePaths,
                    HasRenamedVolume = baseline.HasRenamedVolume,
                    CanEnterDeletableRestItems = false
                };
            }

            return VolumeGroupEvidenceBuilder.AddRow(baseline, row);
        }

        if (trial.Confirmed)
        {
            // 试开成立 = 一票定案：把引擎认下来的顺序原样收进结论（含靠推定/改名认进来的那一卷）。
            return new VolumeGroupResolution
            {
                Verdict = VolumeGroupVerdict.Complete,
                AnchorPath = baseline.AnchorPath,
                BaseName = baseline.BaseName,
                Volumes = VolumeGroupOrdering.RebuildFromTrial(baseline, trial),
                MissingVolumeNames = Array.Empty<string>(),
                PositionInferredNotes = baseline.PositionInferredNotes,
                Evidence = VolumeGroupEvidenceBuilder.Concat(baseline.Evidence, row),
                Reason = "完整（试开确认）：按推定的顺序硬链接试开成立 —— "
                    + (trial.NeedsPassword
                        ? "引擎认出这一组是一份「文件名也加密」的归档，给不出密码就读不出清单"
                        : "引擎真的读到了里面的条目")
                    + $"。试了 {trial.Attempts} 种排列。"
                    + (baseline.HasRenamedVolume
                        ? "⚠ 组里有名字不标准的卷，按原名交给 7-Zip 打不开（程序不改你的文件名）。"
                        : string.Empty),
                GroupFilePaths = baseline.GroupFilePaths,
                HasRenamedVolume = baseline.HasRenamedVolume,
                CanEnterDeletableRestItems = VolumeGroupFailureRules.Deletable(VolumeGroupVerdict.Complete, baseline.HasRenamedVolume)
            };
        }

        /*
         * 试开**试过但不成立** ⇒ 组错（用户口径："报 Missing volume / 打不开 ⇒ 组错"）。
         * 基准里"靠名字就判完整"的那一档必须因此降级 —— 试开是最高权重的最终判据，
         * 它说不行，就不许再拿名字规律说行。
         */
        bool wasComplete = baseline.Verdict == VolumeGroupVerdict.Complete;

        return new VolumeGroupResolution
        {
            Verdict = wasComplete ? VolumeGroupVerdict.IncompleteSuspected : baseline.Verdict,
            AnchorPath = baseline.AnchorPath,
            BaseName = baseline.BaseName,
            Volumes = baseline.Volumes,
            MissingVolumeNames = baseline.MissingVolumeNames,
            PositionInferredNotes = baseline.PositionInferredNotes,
            Evidence = VolumeGroupEvidenceBuilder.Concat(baseline.Evidence, row),
            Reason = wasComplete
                ? "不完整·疑缺卷（弱证据）：名字与序号本来对得上，但按推定顺序试开不成立 —— "
                    + "这一组很可能不止名字上看出来的这些卷（也可能是别的原因打不开，所以只报疑、不动它）"
                : baseline.Reason + $"；试开也不成立（{trial.Reason}）",
            GroupFilePaths = baseline.GroupFilePaths,
            HasRenamedVolume = baseline.HasRenamedVolume,
            CanEnterDeletableRestItems = false
        };
    }
}
