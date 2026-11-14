using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Detection;

/// <summary>
/// **分卷组装判定器 —— 全项目唯一出口**（用户 2026-09-30 真机事故后要求）。
///
/// <para><b>为什么不能"只看内容"</b>：7z 分卷是**裸切**，元数据只在**最后一卷末尾**，
/// 第 2 卷起没有任何格式标记。所以"这一堆文件是不是同一次分卷切出来的"只能靠**证据**
/// （名字 / 体积 / 位置 / 物理同一性 / 试开）回答。本类就是把这组证据收在一起、
/// 并且**只吐一个结论**的地方。</para>
///
/// <para><b>六条证据与权重</b>（逐条实现，逐条进 <see cref="VolumeGroupResolution.Evidence"/>）：</para>
/// <list type="bullet">
/// <item><description><b>基名相等（强）</b>：去卷标记 + 去归档后缀之后名字相同 ——
/// 「后缀可能不同但是名字一定相同」就是这一条：<c>111.7z.001</c> 的基名段是 <c>111</c>，
/// 无后缀的 <c>111</c> 也是 <c>111</c>；<c>一只顶美.z删除ip</c> 的基名段是 <c>一只顶美</c>。</description></item>
/// <item><description><b>卷号连续（强）</b>：转调 <see cref="VolumeGroupDetector"/>（卷名判据的唯一出口），
/// 洞就是"有证据的缺卷"。</description></item>
/// <item><description><b>体积关系（强）</b>：除最后一卷外等大、最后一卷 ≤ 其余。⛔ 只对
/// <c>.00N</c> / <c>.partNN</c> 这两个真·裸切族当硬证据；<c>.z01</c> / <c>.r00</c> 族的首卷天然可能更短，
/// 那一档只记录、不下结论。</description></item>
/// <item><description><b>物理同一性（强）</b>：同一目录 / 同一卷根（跨盘拼不出来）；同 FileId ⇒
/// 一定是同一份，用来把"同一份被数成两卷"挡掉。</description></item>
/// <item><description><b>位置推定（弱）</b>：没有卷号的候选按体积 + 目录内位置/时间插进序列，
/// 一律标成"推定"，⛔ 不许拿它单独升级成"完整"。</description></item>
/// <item><description><b>试开确认（决定性）</b>：按推定的顺序在 <c>.ArchiveFixer.work</c> 里做硬链接
/// 交给引擎试开：列得出清单 / 报"文件名也加密" ⇒ 组对；报 <c>Missing volume</c> / 打不开 ⇒ 组错。
/// ⛔ 只读，源文件一个字节都不动。</description></item>
/// </list>
///
/// <para><b>结论怎么落</b>（可复算，见 <c>docs/分卷组装算法.md</c>）：</para>
/// <list type="number">
/// <item><description>试开成立 ⇒ <see cref="VolumeGroupVerdict.Complete"/>（一票定案）。试开试过但不成立 ⇒
/// 组错；"靠名字就判完整"的那一档因此**降级**，有洞就是"有证据的缺卷"，否则"疑缺"。</description></item>
/// <item><description>没试开（没引擎 / 跨卷 / 导入期 / 定稿闸门）⇒ 只有在
/// <b>同目录里一个「基名段相同、体积不大于满卷」的候选都没有</b>时才敢说
/// <see cref="VolumeGroupVerdict.Complete"/>（名字序号齐全 + 每一卷都是标准卷名）。</description></item>
/// <item><description>洞在**中间槽**（后面还有卷号更大的卷）⇒ **有证据的缺卷** —— 这一档的规范名带卷号，
/// 一个没有卷号的文件**不可能**被断言成"就是那一卷"（真案 ①：<c>111.7z.001</c> + <c>111</c> + <c>111.7z.003</c>）。</description></item>
/// <item><description>洞在**边界槽**（<c>.z01</c>/<c>.r00</c> 族那个没有卷号的本体位），或**候选比名字序号还多**
/// （疑似续卷 / 末卷被改名）⇒ 只有弱证据 ⇒ <see cref="VolumeGroupVerdict.IncompleteSuspected"/>；试开成立才升到"完整"
/// （真案 ②：末卷被改成 <c>一只顶美.z删除ip</c>；真案 ③：续卷伪装成 <c>.mp4</c>）。</description></item>
/// <item><description>其余 ⇒ <see cref="VolumeGroupVerdict.Undetermined"/>，⛔ 判不出 ⇒ 不删源、不移动源。</description></item>
/// </list>
///
/// <para><b>职责分布</b>（2026-11 按职责拆分，⛔ 判据一条没动；本类只留公开 API 与转发）：</para>
/// <list type="bullet">
/// <item><description><see cref="VolumeGroupKeyReader"/>：组键 —— 同目录事实、归组、锚点落在哪一组；</description></item>
/// <item><description><see cref="VolumeGroupCandidateScanner"/> / <see cref="VolumeCandidateScan"/>：候选 ——
/// 在位的卷、同一卷的名字变体、没卷号的候选与槽位；</description></item>
/// <item><description><see cref="VolumeGroupOrdering"/>：排序 —— 候选排序、槽位挑选、试开排列；</description></item>
/// <item><description><see cref="VolumeGroupNaming"/>：命名 —— 族、规范卷名、名字变体判定；</description></item>
/// <item><description><see cref="VolumeGroupEvidenceBuilder"/> / <see cref="Win32FileId"/>：证据 ——
/// 证据行与表、体积关系、物理同一性；</description></item>
/// <item><description><see cref="VolumeGroupFailureRules"/> / <see cref="VolumeGroupTrialGate"/>：失败模型 ——
/// 判不出、可删资格、试开降级；</description></item>
/// <item><description><see cref="VolumeGroupDecision"/>：把六档证据按既有次序落成唯一结论。</description></item>
/// </list>
///
/// <para>本类不引用 WPF；<see cref="VolumeGroupQuery.AllowTrialOpen"/> 为 false 时**一次引擎调用都不做**
/// （导入期的保护名单要的就是这个）。</para>
/// </summary>
public sealed class VolumeGroupResolver
{
    /// <summary>试开最多试几种排列（试开一次是一次进程调用，不能让它在几十种排列上跑）。</summary>
    public const int MaxTrialOrderings = VolumeGroupOrdering.MaxTrialOrderings;

    private readonly VolumeTrialOpen? _trialOpen;

    /// <summary>
    /// </summary>
    /// <param name="trialOpen">
    /// 试开端口。传 null ⇒ 判定器只做"名字 + 体积 + 位置 + 物理同一性"这四档（**同步、零引擎调用**），
    /// 试开那一条证据记为"没试"。删除闸门与导入期保护名单就走这一档。
    /// </param>
    public VolumeGroupResolver(VolumeTrialOpen? trialOpen = null)
    {
        _trialOpen = trialOpen;
    }

    /// <summary>
    /// **同步判定**（不试开）。用在哪：定稿时的删除闸门、导入期的保护名单 —— 这两处要么在同步代码里，
    /// 要么明确"不许调引擎"。
    /// </summary>
    public VolumeGroupResolution Resolve(VolumeGroupQuery? query)
    {
        if (query == null || string.IsNullOrWhiteSpace(query.AnchorPath))
        {
            return VolumeGroupFailureRules.Undetermined(string.Empty, "没有可判定的输入");
        }

        string anchor = SafePathHelper.GetFullPathSafe(query.AnchorPath);

        if (anchor.Length == 0)
        {
            return VolumeGroupFailureRules.Undetermined(string.Empty, "路径形状不合法，取不到名字");
        }

        IReadOnlyList<VolumeGroupEntry> entries = query.DirectoryEntries ?? VolumeGroupKeyReader.EnumerateDirectory(anchor);

        if (entries.Count == 0)
        {
            return VolumeGroupFailureRules.Undetermined(anchor, "同目录里读不到任何文件（目录不在，或者没有读权限）");
        }

        VolumeGroup? group = VolumeGroupKeyReader.LocateAnchorGroup(anchor, entries);

        if (group == null)
        {
            return VolumeGroupFailureRules.Undetermined(
                anchor,
                $"{Path.GetFileName(anchor)} 的名字里没有卷标记，同目录也没有和它同基名的分卷组 —— "
                + "没有证据能说明它属于哪一组（判不出：不删、不移动、不改名）");
        }

        return VolumeGroupDecision.Decide(anchor, entries, group);
    }

    /// <summary>
    /// **带试开的判定**。用在哪：解前预检 —— 它要回答的正是"缺卷就别开解，缺哪一卷、为什么这样判"。
    /// 试开端口没注入 / <see cref="VolumeGroupQuery.AllowTrialOpen"/> 为 false 时等价于 <see cref="Resolve"/>。
    /// </summary>
    public async Task<VolumeGroupResolution> ResolveAsync(
        VolumeGroupQuery? query,
        CancellationToken cancellationToken = default)
    {
        VolumeGroupResolution baseline = Resolve(query);

        if (_trialOpen == null || query == null || !query.AllowTrialOpen ||
            baseline.Verdict == VolumeGroupVerdict.Undetermined)
        {
            // 连组都没认出来：没什么可试的（硬链接一组叫不上名字的文件没有意义）。
            return baseline;
        }

        if (baseline.Volumes.Count == 0 || baseline.Volumes[0].Index != 1)
        {
            return VolumeGroupEvidenceBuilder.AddRow(baseline, VolumeGroupEvidenceBuilder.Row(
                VolumeEvidenceKind.TrialOpen,
                VolumeEvidenceWeight.Decisive,
                VolumeEvidenceOutcome.Unknown,
                "试开确认",
                "第 1 卷还没定位出来，凑不出可试的完整顺序"));
        }

        List<List<VolumeCandidate>> orderings = VolumeGroupOrdering.BuildTrialOrderings(baseline);

        if (orderings.Count == 0)
        {
            return VolumeGroupEvidenceBuilder.AddRow(baseline, VolumeGroupEvidenceBuilder.Row(
                VolumeEvidenceKind.TrialOpen,
                VolumeEvidenceWeight.Decisive,
                VolumeEvidenceOutcome.Unknown,
                "试开确认",
                "按名字与推定凑不出「1 到 N 一卷不缺」的顺序，没法试开"));
        }

        VolumeTrialOutcome trial;

        try
        {
            trial = await _trialOpen(
                    new VolumeTrialRequest
                    {
                        FirstVolumePath = baseline.Volumes[0].Path,
                        Orderings = orderings,
                        PreferredWorkRootDirectory = query.WorkRootDirectory
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VolumeGroupEvidenceBuilder.AddRow(baseline, VolumeGroupEvidenceBuilder.Row(
                VolumeEvidenceKind.TrialOpen,
                VolumeEvidenceWeight.Decisive,
                VolumeEvidenceOutcome.Unknown,
                "试开确认",
                $"试开出错（{ex.GetType().Name}）—— 这一条证据取不到，结论不变"));
        }

        return trial == null ? baseline : VolumeGroupTrialGate.ApplyTrialOutcome(baseline, trial);
    }

    /// <summary>
    /// **这一片按它自己那一族写成的规范卷名** ——「还原成规范名」在单卷尺度上的公开入口
    /// （实现与判据见 <see cref="VolumeGroupNaming.TryGetCanonicalVolumeName"/>）。
    /// </summary>
    internal static bool TryGetCanonicalVolumeName(string fileName, out string canonical) =>
        VolumeGroupNaming.TryGetCanonicalVolumeName(fileName, out canonical);

    /// <summary>
    /// **同一族里第 <paramref name="index"/> 卷该叫什么名字**的公开入口
    /// （实现与判据见 <see cref="VolumeGroupNaming.TryBuildCanonicalVolumeNameForIndex"/>）。
    /// </summary>
    internal static bool TryBuildCanonicalVolumeNameForIndex(
        string sampleVolumeName,
        int index,
        out string canonical) =>
        VolumeGroupNaming.TryBuildCanonicalVolumeNameForIndex(sampleVolumeName, index, out canonical);
}
