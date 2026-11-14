using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>
/// 分卷组识别（AGENTS.md §9.3）—— 公开 API 门面：只做薄转发，判据在协作者里。
///
/// 认的命名（左列是第 1 卷，右列是后续卷）：
///
/// <code>
/// volume.7z.001   →  .002 / .003 …        基名 volume.7z
/// archive.001     →  .002 / .003 …        基名 archive
/// x.zip           →  x.z01 / x.z02 …      基名 x（本体就是第 1 卷）
/// x.rar           →  x.r00 / x.r01 …      基名 x（本体就是第 1 卷）
/// x.part1.rar     →  x.part2.rar …        基名 x
/// </code>
///
/// 三个最容易搞错的地方，先说清楚为什么：
///
/// 1. <b>卷序是"绝对卷序"，不是"后缀里的数字"</b>。<c>x.r00</c> 返回 2 而不是 1：
///    rar 老式分卷里 <c>x.rar</c> 才是第 1 卷，<c>r00</c> 只是"第 1 个后续卷"，
///    所以 <c>rNN</c> 的卷序是 NN + 2，<c>zNN</c> 的卷序是 NN + 1（zip 分卷的本体是 <c>x.zip</c>）。
///    这个偏移写错，后果是永远报"缺第 1 卷"或把两卷当成一卷。
///
/// 2. <b>卷总数推不出来，就不推</b>。这些命名都只有"第几卷"没有"共几卷"，
///    所以 <see cref="VolumeGroup.ExpectedVolumeCount"/> 恒为 0、<see cref="VolumeGroup.IsComplete"/>
///    只管"从 1 开始连续无缺号"。缺号（<c>.001</c>+<c>.003</c>）能确定，就列进
///    <see cref="VolumeGroup.MissingVolumeNames"/>；最大卷号之后的缺号无法得知，不猜。
///
/// 3. <b>普通压缩包绝不能被吞成分卷组</b>。<c>x.zip</c>/<c>x.rar</c> 单看名字确实"是第 1 卷"，
///    但只有当同目录同基名下真的存在 <c>.z01</c>/<c>.r00</c> 时才算一组
///    —— 否则一个装满普通 zip 的目录会被整体识别成分卷组，那比漏识别糟得多。
///
/// 明确的能力边界：本模块**只按命名归组**，而且**不碰文件系统**（不查存在性、不读内容）。
/// "同名但其实是两个不同归档"这种情况，需要大小/首卷主头/引擎探测结果来复核，
/// 那是调用方的事（设计.md §十六；AGENTS.md §9.3 要求不得只凭 .001/.002 就认定同一组）。
///
/// 职责分布（本类只留公开 API）：名字证据 <see cref="VolumeNameEvidence"/>、
/// 桶与尺寸证据 <see cref="VolumeGroupBucket"/>、收集 <see cref="VolumeGroupEvidenceCollector"/>、
/// 组判定 <see cref="VolumeGroupJudge"/>、缺卷/完整度/文案 <see cref="VolumeGroupGap"/>、
/// 组键 <see cref="VolumeGroupKey"/>、输出顺序 <see cref="VolumeGroupSorting"/>。
/// </summary>
public static class VolumeGroupDetector
{
    /// <summary>把一个目录下的候选文件按分卷组归并；非分卷文件不返回。</summary>
    public static IReadOnlyList<VolumeGroup> Group(
        IEnumerable<VolumeCandidate> files,
        Func<IReadOnlyList<VolumeCandidate>, bool>? contentProof = null)
    {
        IReadOnlyList<VolumeGroupBucket> buckets = VolumeGroupEvidenceCollector.Collect(files);
        IReadOnlyList<VolumeGroup> groups = VolumeGroupJudge.Judge(buckets, contentProof);

        return VolumeGroupSorting.Order(groups);
    }

    /// <summary>取单个文件的卷序（1 表示第一卷）；不是分卷返回 null。</summary>
    public static int? TryGetVolumeIndex(string fileName)
    {
        return VolumeNameEvidence.Analyze(fileName)?.Index;
    }

    /// <summary>
    /// 由任意一卷推出该组的"第一卷文件名"；不是分卷返回 null。
    ///
    /// 只给文件名、不查存在性：<c>x.z02</c> 推出的是 <c>x.zip</c>，即便目录里根本没有这个本体
    /// （这种情况由 <see cref="Group"/> 在缺卷清单和 Note 里点名）。
    /// </summary>
    public static string? TryGetFirstVolumeName(string fileName)
    {
        VolumeNameInfo? info = VolumeNameEvidence.Analyze(fileName);
        if (info == null)
        {
            return null;
        }

        return info.TryFormat(1, out string firstVolumeName) ? firstVolumeName : null;
    }

    /// <summary>
    /// 由任意一卷推出**名字前缀**（<see cref="VolumeNameInfo.TryFormat"/> 往它后面接卷标记的那一段）：
    /// <c>set.7z.001</c> → <c>set.7z</c>、<c>x.zip</c>（本体）→ <c>x</c>、<c>444.part1.rar</c> → <c>444</c>。
    ///
    /// <para>⚠ <b>这不是基名</b>：基名只有两份（<c>FileNameHelper.FirstStepBaseName</c> /
    /// <c>SecondStepBaseName</c>，第一个点之前），用于归组 / 落点 / 匹配。
    /// 这里回答的是"写出来的规范卷名该长什么样" —— 只有同一个名字前缀才写得出同一族的一组名字。
    /// 判不出（不是分卷/本体）⇒ 空串。</para>
    /// </summary>
    public static string TryGetBaseName(string? fileName) => VolumeNameEvidence.Analyze(fileName)?.BaseName ?? string.Empty;

    /// <summary>
    /// 判断两个文件是否属于同一分卷组的同一基名（同目录比较由调用方负责）。
    ///
    /// 只比"族 + 基名"：<c>x.zip</c> 与 <c>x.z01</c> 同组（同一族的本体与后续卷），
    /// 而 <c>x.rar</c> 与 <c>x.part1.rar</c> **不同组** —— 老式与新式是两套互不兼容的命名，
    /// 合并会让两个集的第一卷撞在同一个卷号上。
    /// </summary>
    public static bool BelongsToSameGroup(string fileNameA, string fileNameB)
    {
        VolumeNameInfo? a = VolumeNameEvidence.Analyze(fileNameA);
        VolumeNameInfo? b = VolumeNameEvidence.Analyze(fileNameB);

        return a != null
               && b != null
               && a.Family == b.Family
               && string.Equals(a.BaseName, b.BaseName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 这两个名字是不是**可证不属于同一组**（两边都解析得出来，而且**族不同**）。
    ///
    /// <para><b>它存在的唯一理由</b>：「半套分卷」那道删除闸门（<c>RestVolumeCompletenessGate</c>）
    /// 是**保守**闸门 —— 判不出就拦下、整组一个字节都不删。它原来只比**基名**，
    /// 于是把"跨族同基名"的无关文件当成"同组的另一片"误拦：真机 EEEE 2026-10-10 实测，
    /// `111.rar`（**RarOld 族**，归档本体）与 `111.zip`（**ZipSpanned 族**，跨盘 ZIP 末片）
    /// 只共享基名，却拦下了 `111.rar` 源包的逐层回收（空间不足模式下 48 MB 没能在定稿那一刻还回去）。</para>
    ///
    /// <para>⛔ 调用方的用法必须是「**能证明不同族才放行，判不出照旧拦**」——
    /// 这条判据**只回答"可证不同族"**：有一边解析不出来时它返回 <c>false</c>，
    /// 那是"判不出"、**不是**"同组"。25 GB 那次事故的形状（同族 + 同基名，末片名字还被改坏）
    /// 要么两边同族、要么有一边根本解析不出来 ⇒ 照旧拦得住。</para>
    ///
    /// <para>族与基名的真值只有这一处（与 <see cref="BelongsToSameGroup"/> 用的是同一份
    /// <see cref="VolumeNameEvidence.Analyze"/>），⛔ 调用方不许自己再判一遍族。</para>
    /// </summary>
    public static bool AreProvablyDifferentFamilies(string fileNameA, string fileNameB)
    {
        VolumeNameInfo? a = VolumeNameEvidence.Analyze(fileNameA);
        VolumeNameInfo? b = VolumeNameEvidence.Analyze(fileNameB);

        return a != null && b != null && a.Family != b.Family;
    }
}
