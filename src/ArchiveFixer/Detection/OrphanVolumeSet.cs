using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>
/// 目录里"缺首卷的一大组分卷"（例如只有 <c>set.7z.002</c> / <c>set.7z.003</c>，
/// 没有 <c>set.7z.001</c>）。
///
/// <para><b>为什么单独要这么一个东西</b>（用户 2026-09-25 第 42 条，真 7z 探针实测）：
/// 容器（`封面.jpg`）里装着的**正是**那一组的首卷 —— 后续卷在容器外面。
/// 那种情况下引擎会报"这是后续卷、缺少首卷"，与事实**正好相反**（缺的不是首卷，
/// 而是"名字对得上的后续卷"）；程序要么把两边凑起来，要么至少得把这句话说对。</para>
///
/// <para>判据只有两条，全来自文件系统：①这一组里有卷号 ≥ 2 的文件；②按标准命名推出来的
/// **首卷文件名在目录里不存在**。⛔ 不猜、不按体积配对、不看内容。</para>
/// </summary>
public sealed class OrphanVolumeSet
{
    /// <summary>标准首卷文件名（例如 <c>set.7z.001</c>）—— 目录里**没有**这个文件。</summary>
    public string FirstVolumeName { get; init; } = string.Empty;

    /// <summary>已找到的后续卷文件名（按卷序升序）。</summary>
    public IReadOnlyList<string> ContinuationFileNames { get; init; } = Array.Empty<string>();

    /// <summary>卷号是否从 2 开始连续（<c>.002/.003/…</c>）；缺号时只影响描述口径。</summary>
    public bool IsContiguous { get; init; }

    /// <summary>一行给人看：<c>set.7z.002、set.7z.003（标准首卷名 set.7z.001）</c>。</summary>
    public string Describe() =>
        $"{string.Join("、", ContinuationFileNames)}（标准首卷名 {FirstVolumeName}{(IsContiguous ? string.Empty : "，卷号不连续")}）";
}
