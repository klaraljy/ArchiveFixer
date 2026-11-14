using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 顺序 —— 归组结果按"目录 → 基名 → 组键"升序给出，让列表与日志同一输入得到同一输出。
///
/// <para><b>为什么独立成层</b>：顺序不是判据，但它是"可复现"的唯一保证；收集与判定两段都不该关心它。
/// 这里只排已有结果的顺序，⛔ 不改组内容、不改组键、不丢组。</para>
///
/// <para><b>失败模型</b>：空结果 ⇒ 空清单（判定段已经给出空清单，这里不另造一档）；
/// 排序键为空串时照常参与比较（不特殊照顾、不抛）。</para>
///
/// <para>生命周期：纯静态、无状态；只读调用方给的结果列表。</para>
/// </summary>
internal static class VolumeGroupSorting
{
    /// <summary>按目录、基名、组键（都忽略大小写）排序；三个键一起才能让同目录多套命名稳定分列。</summary>
    public static IReadOnlyList<VolumeGroup> Order(IReadOnlyList<VolumeGroup> groups)
    {
        return groups
            .OrderBy(g => g.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.BaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
