using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 判定流水线 —— 把六条证据按固定顺序取齐，并按既定判据落成**唯一结论**。
///
/// <para><b>为什么独立成层</b>：这是 <see cref="VolumeGroupResolver"/> 里原来那一段"真正干活"的逻辑。
/// 它只做两件事：① 按 ①→⑥ 的顺序把证据行放进证据表；② 按"先看物理同一性、再看中间槽、再看边界槽/
/// 多余候选、再看体积规律、最后才敢说完整"的既有次序落结论。⛔ 判据、次序、文案一个字都没有改 ——
/// 只是从公开 API 那个类里搬出来，让公开 API 只剩薄转发。</para>
///
/// <para><b>失败模型</b>（与拆之前逐条一致，⛔ 顺序也不许换）：</para>
/// <list type="number">
/// <item><description>一卷卷号都解析不出 ⇒ 判不出；</description></item>
/// <item><description>物理同一性不成立（跨目录 / 跨卷 / 同一个 FileId）⇒ 判不出；</description></item>
/// <item><description>中间槽有洞 ⇒ 不完整·缺第 N 卷（有证据）；</description></item>
/// <item><description>槽没填满 / 边界槽空着 / 名字后面还有等大候选 ⇒ 不完整·疑缺卷（弱证据）；</description></item>
/// <item><description>裸切族体积规律对不上 ⇒ 不完整·疑缺卷（弱证据）；</description></item>
/// <item><description>以上都不成立 ⇒ 完整（可删资格仍由 <see cref="VolumeGroupFailureRules.Deletable"/> 回答）。</description></item>
/// </list>
///
/// <para>生命周期：纯静态、无状态；一次判定跑一遍，输入是"锚点 + 同目录事实 + 归组结果"，
/// 输出一个 <see cref="VolumeGroupResolution"/>，不缓存、不调引擎、不碰文件。</para>
/// </summary>
internal static class VolumeGroupDecision
{
    /// <summary>真正干活的那一段：组已经认出来了，逐条取证据、落结论。</summary>
    public static VolumeGroupResolution Decide(
        string anchor,
        IReadOnlyList<VolumeGroupEntry> entries,
        VolumeGroup group)
    {
        var evidence = new List<VolumeEvidence>();
        string baseName = group.BaseName;
        VolumeNameFamily family = VolumeGroupNaming.ResolveFamily(group, baseName);

        /*
         * ── 候选池（弱证据的原料）──────────────────────────────────────────────
         * 谁算候选、谁占哪个槽、哪一些只是推定，由 VolumeGroupCandidateScanner 一次算清；
         * 这里只读它交出来的那一份快照（⛔ 不再自己算第二遍，免得槽位两遍不一致）。
         */
        VolumeCandidateScan scan = VolumeGroupCandidateScanner.Scan(entries, group, family, baseName);
        IReadOnlyList<ResolvedVolume> located = scan.Located;

        if (located.Count == 0)
        {
            evidence.Add(VolumeGroupEvidenceBuilder.Row(
                VolumeEvidenceKind.BaseName,
                VolumeEvidenceWeight.Strong,
                VolumeEvidenceOutcome.Unknown,
                "基名相等",
                "同目录里没有一卷带着可解析的卷号"));

            return VolumeGroupEvidenceBuilder.WithEvidence(
                VolumeGroupFailureRules.Undetermined(anchor, "判不出：认出了基名，但一卷的卷号都解析不出来（不删、不移动、不改名）"),
                evidence,
                baseName);
        }

        // ── ① 基名相等（强证据）──────────────────────────────────────────────
        string groupStem = VolumeGroupNaming.StemOf(baseName);
        string anchorStem = VolumeGroupNaming.StemOf(Path.GetFileName(anchor));
        bool anchorMatchesByStem = string.Equals(anchorStem, groupStem, StringComparison.OrdinalIgnoreCase);

        evidence.Add(VolumeGroupEvidenceBuilder.Row(
            VolumeEvidenceKind.BaseName,
            VolumeEvidenceWeight.Strong,
            VolumeEvidenceOutcome.Satisfied,
            "基名相等",
            $"组基名 {baseName}（去归档后缀后是 {groupStem}）；锚点 {Path.GetFileName(anchor)} 的基名段是 {anchorStem}"
            + (anchorMatchesByStem ? "，与组基名段相同" : "，靠卷号归进本组")));

        // ── ② 没卷号的候选：按体积 + 位置（弱证据）────────────────────────────
        int maxFound = scan.MaxFound;
        IReadOnlyList<int> openSlots = scan.OpenSlots;
        bool boundarySlotOpen = scan.BoundarySlotOpen;

        evidence.Add(VolumeGroupEvidenceBuilder.Row(
            VolumeEvidenceKind.PositionInference,
            VolumeEvidenceWeight.Weak,
            scan.Inferred.Count > 0 ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.Unknown,
            "位置推定（弱）",
            scan.Unrecognized.Count == 0
                ? "同目录里没有「基名段相同、名字不带卷号」的候选"
                : scan.InferredNotes.Count > 0
                    ? string.Join("；", scan.InferredNotes)
                    : $"{scan.Unrecognized.Count} 个体积对得上的候选一个槽都分不到（只报不用）"));

        // ── ③ 卷号连续（强证据）──────────────────────────────────────────────
        var missingNames = openSlots
            .Select(index => VolumeGroupNaming.CanonicalNameFor(family, baseName, index))
            .ToList();

        evidence.Add(VolumeGroupEvidenceBuilder.Row(
            VolumeEvidenceKind.Sequence,
            VolumeEvidenceWeight.Strong,
            openSlots.Count == 0 ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.NotSatisfied,
            "卷号连续",
            openSlots.Count == 0
                ? $"第 1..{maxFound.ToString(CultureInfo.InvariantCulture)} 卷一个不缺"
                : $"第 1..{maxFound.ToString(CultureInfo.InvariantCulture)} 卷之间缺 "
                    + string.Join("、", missingNames)
                    + (group.MissingVolumeNames.Count > 0
                        ? $"（卷名判据的说法：{string.Join("、", group.MissingVolumeNames)}）"
                        : string.Empty)));

        // ── ④ 体积关系（强证据）──────────────────────────────────────────────
        (bool sizeRegular, string sizeDetail) = VolumeGroupEvidenceBuilder.CheckSizePattern(located);
        bool sizeIsHardEvidence = family is VolumeNameFamily.Numeric or VolumeNameFamily.PartNumbered;

        evidence.Add(VolumeGroupEvidenceBuilder.Row(
            VolumeEvidenceKind.SizePattern,
            VolumeEvidenceWeight.Strong,
            sizeDetail.Length == 0
                ? VolumeEvidenceOutcome.Unknown
                : sizeRegular ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.NotSatisfied,
            "体积关系",
            sizeDetail.Length == 0
                ? "量不出体积（文件读不到）"
                : sizeDetail + (sizeIsHardEvidence
                    ? string.Empty
                    : "（这一族的首卷天然可能更短，所以这一条只记录、不当硬证据）")));

        // ── ⑤ 物理同一性（强证据）────────────────────────────────────────────
        (bool samePlace, string placeDetail) = VolumeGroupEvidenceBuilder.CheckPhysicalIdentity(located);

        evidence.Add(VolumeGroupEvidenceBuilder.Row(
            VolumeEvidenceKind.PhysicalIdentity,
            VolumeEvidenceWeight.Strong,
            samePlace ? VolumeEvidenceOutcome.Satisfied : VolumeEvidenceOutcome.NotSatisfied,
            "物理同一性",
            placeDetail));

        /*
         * ── ⑥ 试开确认（决定性证据）──────────────────────────────────────────
         *
         * 同步这一档**不试开**（定稿闸门在同步代码里；导入期明确不许调引擎），
         * 但**这一行必须在**：判据表要能一眼看出"试开这一条取到了没有"。
         * ⛔ 记 <see cref="VolumeEvidenceOutcome.Unknown"/>（没试），绝不记成"不成立"——
         * 那是两件完全不同的事。真试过之后由 ResolveAsync 把这一行换掉。
         */
        evidence.Add(VolumeGroupEvidenceBuilder.Row(
            VolumeEvidenceKind.TrialOpen,
            VolumeEvidenceWeight.Decisive,
            VolumeEvidenceOutcome.Unknown,
            "试开确认",
            "这一次没有试开（同步判定：定稿闸门与导入期的保护名单都不调引擎）"));

        // ── 结论 ─────────────────────────────────────────────────────────────
        var allVolumes = new List<ResolvedVolume>(located);

        foreach (ResolvedVolume volume in scan.Inferred)
        {
            if (!allVolumes.Any(v => v.Index == volume.Index))
            {
                allVolumes.Add(volume);
            }
        }

        allVolumes.Sort((a, b) => a.Index.CompareTo(b.Index));

        bool hasRenamedVolume = scan.VariantHasDisguisedName
            || allVolumes.Any(v => !v.NameCarriesVolumeNumber)
            || group.Volumes.Any(v => !VolumeGroupNaming.IsCanonicalVolumeName(Path.GetFileName(v.Path), baseName));

        var groupPaths = allVolumes
            .Select(v => v.Path)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!samePlace)
        {
            return VolumeGroupEvidenceBuilder.WithEvidence(
                Build(
                    anchor,
                    VolumeGroupVerdict.Undetermined,
                    "判不出：" + placeDetail + " —— 位置这一条证据都不成立，不敢下任何结论（不删、不移动、不改名）",
                    allVolumes,
                    Array.Empty<string>(),
                    scan.InferredNotes,
                    hasRenamedVolume),
                evidence,
                baseName);
        }

        /*
         * 中间槽有洞 ⇒ **有证据的缺卷**。
         * 这一档的规范名带卷号，一个没有卷号的文件不可能被断言成"就是那一卷"
         * （真案 ①：111.7z.001 + 111 + 111.7z.003 ⇒ 判缺第 2 卷，并把 111 的推定如实说出来）。
         */
        var middleHoles = openSlots.Where(index => !(boundarySlotOpen && index == 1)).ToList();

        if (middleHoles.Count > 0)
        {
            var middleNames = middleHoles.Select(index => VolumeGroupNaming.CanonicalNameFor(family, baseName, index)).ToList();

            string extra = scan.InferredNotes.Count > 0
                ? "；同目录的 " + string.Join("、", scan.InferredNotes)
                : string.Empty;

            return VolumeGroupEvidenceBuilder.WithEvidence(
                Build(
                    anchor,
                    VolumeGroupVerdict.IncompleteMissingVolume,
                    $"不完整·缺第 {string.Join("、", middleHoles)} 卷（有证据）：卷号 1.."
                    + maxFound.ToString(CultureInfo.InvariantCulture)
                    + " 之间有洞，缺 " + string.Join("、", middleNames)
                    + "。依据：名字里的卷号本身（强证据）" + extra,
                    allVolumes,
                    middleNames,
                    scan.InferredNotes,
                    hasRenamedVolume),
                evidence,
                baseName);
        }

        if (scan.RemainingSlots.Count > 0 || (openSlots.Count > 0 && scan.Inferred.Count > 0))
        {
            /*
             * 边界槽（.z01/.r00 族那个没有卷号的本体位）—— 真案 ② 就落在这里。
             * 名字段对得上、体积也对得上，但名字里没有卷号 ⇒ **弱证据** ⇒ 只报"疑缺"，
             * ⛔ 不许据此说完整（组一旦算完整就会进可删的其余物）。试开成立才升到"完整"。
             */
            string candidate = scan.InferredNotes.Count > 0
                ? "同目录的 " + string.Join("、", scan.InferredNotes)
                    + " —— 体积与位置都对得上，但名字里没有卷号，所以只当推定、不当已找到的卷。"
                : "同目录里也没有「名字段相同、体积对得上」的候选。";

            return VolumeGroupEvidenceBuilder.WithEvidence(
                Build(
                    anchor,
                    VolumeGroupVerdict.IncompleteSuspected,
                    "不完整·疑缺卷（弱证据）：名字上看缺 " + string.Join("、", missingNames)
                    + "；" + candidate
                    + "要确认只能靠试开（有可用引擎时程序会自己试）。⛔ 这一档只报不删、不动你的任何文件",
                    allVolumes,
                    Array.Empty<string>(),
                    scan.InferredNotes,
                    hasRenamedVolume),
                evidence,
                baseName);
        }

        if (scan.Inferred.Count > 0)
        {
            /*
             * 名字上看不出的位子被等大的候选占了（真案 ③：伪装成 .mp4 的续卷）。
             * 没试开就只到"疑"这一档 —— 认错组比不认糟得多，而"完整"会进可删的其余物。
             */
            return VolumeGroupEvidenceBuilder.WithEvidence(
                Build(
                    anchor,
                    VolumeGroupVerdict.IncompleteSuspected,
                    "不完整·疑缺卷（弱证据）：名字序号看不出后面还有卷，但同目录有与满卷等大、"
                    + "基名段相同的候选（" + string.Join("、", scan.InferredNotes)
                    + "）—— 它很可能就是续卷，也可能不是。要确认只能靠试开。⛔ 这一档只报不删、不动你的任何文件",
                    allVolumes,
                    Array.Empty<string>(),
                    scan.InferredNotes,
                    hasRenamedVolume),
                evidence,
                baseName);
        }

        if (sizeIsHardEvidence && !sizeRegular && sizeDetail.Length > 0)
        {
            return VolumeGroupEvidenceBuilder.WithEvidence(
                Build(
                    anchor,
                    VolumeGroupVerdict.IncompleteSuspected,
                    "不完整·疑缺卷（弱证据）：名字序号连续，但体积规律与裸切对不上 —— " + sizeDetail
                    + "。⛔ 这一档只报不删、不动你的任何文件",
                    allVolumes,
                    Array.Empty<string>(),
                    scan.InferredNotes,
                    hasRenamedVolume),
                evidence,
                baseName);
        }

        return VolumeGroupEvidenceBuilder.WithEvidence(
            Build(
                anchor,
                VolumeGroupVerdict.Complete,
                "完整：卷号 1.." + maxFound.ToString(CultureInfo.InvariantCulture)
                + " 连续无洞，每一卷的名字都是标准卷名"
                + (hasRenamedVolume ? "（但组里有名字不标准的卷，按原名 7-Zip 打不开）" : string.Empty),
                allVolumes,
                Array.Empty<string>(),
                scan.InferredNotes,
                hasRenamedVolume,
                scan.VariantPaths),
            evidence,
            baseName);
    }

    /// <summary>按结论拼最终对象（<see cref="VolumeGroupResolution.CanEnterDeletableRestItems"/> 只在这里算一次）。</summary>
    private static VolumeGroupResolution Build(
        string anchor,
        VolumeGroupVerdict verdict,
        string reason,
        IReadOnlyList<ResolvedVolume> volumes,
        IReadOnlyList<string> missingNames,
        IReadOnlyList<string> inferredNotes,
        bool hasRenamedVolume,
        IReadOnlyList<string>? variantPaths = null) => new()
        {
            Verdict = verdict,
            AnchorPath = anchor,
            Volumes = volumes,
            MissingVolumeNames = missingNames,
            PositionInferredNotes = inferredNotes,
            Reason = reason,
            GroupFilePaths = volumes
                .Select(v => v.Path)
                .Concat(variantPaths ?? Array.Empty<string>())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            VariantFilePaths = variantPaths ?? Array.Empty<string>(),
            HasRenamedVolume = hasRenamedVolume,

            /*
             * ⛔ 可删资格 = 结论"完整" **且** 每一卷名字都标准，两条缺一不可。
             * 名字不标准 ⇒ 7-Zip 按原名根本打不开 ⇒ 删了就是不可逆的数据丢失（25 GB 那次）。
             */
            CanEnterDeletableRestItems = VolumeGroupFailureRules.Deletable(verdict, hasRenamedVolume)
        };
}
