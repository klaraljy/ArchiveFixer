using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 证据表 —— 六条证据各行的构造、合并，以及其中两条需要真读盘/真读文件系统的证据
/// （体积关系 / 物理同一性）。
///
/// <para><b>为什么独立成层</b>：结论只能由证据回答，而"证据表要么六行、要么更少"是硬约束
/// （同一件事出现两行就等于自相矛盾）。行的构造与合并放在一处，判定流水线就不再各拼一份证据；
/// 两条要读盘的证据（体积、FileId）也不和落结论的代码混在一起，免得"取不到证据"被顺手写成"证据成立"。</para>
///
/// <para><b>失败模型</b>：量不出体积 ⇒ 这一条记 <see cref="VolumeEvidenceOutcome.Unknown"/>、
/// 详情写"量不出体积"；FileId 取不到 ⇒ 这一条只按目录/卷根回答，⛔ 绝不把"取不到"当"成立"。
/// 两个读数口都**绝不抛**。</para>
///
/// <para>生命周期：纯静态、无状态；只读调用方已经定位好的卷清单（不枚举目录、不缓存、不持句柄）。</para>
/// </summary>
internal static class VolumeGroupEvidenceBuilder
{
    /// <summary>体积关系：除最后一卷外等大 + 最后一卷 ≤ 其余。</summary>
    public static (bool Regular, string Detail) CheckSizePattern(IReadOnlyList<ResolvedVolume> located)
    {
        if (located.Count == 0 || located.Any(v => v.Size <= 0))
        {
            return (false, string.Empty);
        }

        if (located.Count == 1)
        {
            return (true, $"只定位到 1 卷（{FormatBytes(located[0].Size)}），没有可比的对象");
        }

        long full = located[0].Size;

        for (int i = 0; i < located.Count - 1; i++)
        {
            if (located[i].Size != full)
            {
                return (false, $"第 {located[i].Index} 卷 {FormatBytes(located[i].Size)} 与第 1 卷 "
                    + $"{FormatBytes(full)} 不等（裸切应当等大）");
            }
        }

        if (located[^1].Size > full)
        {
            return (false, $"最后一卷 {FormatBytes(located[^1].Size)} 比其余卷 {FormatBytes(full)} 还大（裸切不可能）");
        }

        return (true, $"第 1..{located[^1].Index - 1} 卷都是 {FormatBytes(full)}，最后一卷 {FormatBytes(located[^1].Size)}");
    }

    /// <summary>物理同一性：同一目录 + 同一卷根；同 FileId ⇒ 同一份（硬链接 / 同一个文件的两个名字）。</summary>
    public static (bool SamePlace, string Detail) CheckPhysicalIdentity(IReadOnlyList<ResolvedVolume> located)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int linkNoted = 0;

        foreach (ResolvedVolume volume in located)
        {
            try
            {
                string? directory = Path.GetDirectoryName(volume.Path);
                string root = Path.GetPathRoot(volume.Path) ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    directories.Add(Path.GetFullPath(directory));
                }

                if (root.Length > 0)
                {
                    roots.Add(root);
                }
            }
            catch
            {
                // 路径形状不合法只影响"同一目录"这个判断，不影响别的证据。
            }

            if (!Win32FileId.TryGet(volume.Path, out uint serial, out ulong index, out uint links))
            {
                continue;
            }

            string key = serial.ToString("X8", CultureInfo.InvariantCulture)
                + ":" + index.ToString("X16", CultureInfo.InvariantCulture);

            if (identities.TryGetValue(key, out string? other))
            {
                return (false, $"{Path.GetFileName(volume.Path)} 与 {Path.GetFileName(other)} 是同一个文件"
                    + "（同一个 FileId：硬链接或同一个文件的两个名字）—— 一份数据不能算成两卷");
            }

            identities[key] = volume.Path;

            if (links > 1)
            {
                linkNoted++;
            }
        }

        if (directories.Count > 1)
        {
            return (false, $"这几卷不在同一个目录里（{directories.Count} 个目录）—— "
                + "跨目录凭什么认定是同一组还没想清楚，⛔ 不凭名字像就拼");
        }

        if (roots.Count > 1)
        {
            return (false, $"这几卷跨了 {roots.Count} 个卷（{string.Join("、", roots)}）—— "
                + "跨卷做不了硬链接，装配不了，也没法试开");
        }

        string note = linkNoted > 0
            ? $"；其中 {linkNoted} 卷的硬链接数大于 1（磁盘上另有名字指向同一份数据，但 FileId 各不相同）"
            : string.Empty;

        return (true, $"同一个目录、同一个卷根（{string.Join("、", roots)}），各卷 FileId 互不相同{note}");
    }

    public static VolumeEvidence Row(
        VolumeEvidenceKind kind,
        VolumeEvidenceWeight weight,
        VolumeEvidenceOutcome outcome,
        string title,
        string detail) => new()
        {
            Kind = kind,
            Weight = weight,
            Outcome = outcome,
            Title = title,
            Detail = detail
        };

    public static VolumeGroupResolution Copy(
        VolumeGroupResolution source,
        IReadOnlyList<VolumeEvidence>? evidence = null,
        string? baseName = null) => new()
        {
            Verdict = source.Verdict,
            AnchorPath = source.AnchorPath,
            BaseName = baseName ?? source.BaseName,
            Volumes = source.Volumes,
            MissingVolumeNames = source.MissingVolumeNames,
            PositionInferredNotes = source.PositionInferredNotes,
            Evidence = evidence ?? source.Evidence,
            Reason = source.Reason,
            GroupFilePaths = source.GroupFilePaths,
            VariantFilePaths = source.VariantFilePaths,
            HasRenamedVolume = source.HasRenamedVolume,
            CanEnterDeletableRestItems = source.CanEnterDeletableRestItems
        };

    public static VolumeGroupResolution WithEvidence(
        VolumeGroupResolution resolution,
        IReadOnlyList<VolumeEvidence> evidence,
        string baseName) => Copy(resolution, evidence, baseName);

    public static VolumeGroupResolution AddRow(VolumeGroupResolution resolution, VolumeEvidence row) =>
        Copy(resolution, Concat(resolution.Evidence, row));

    /// <summary>
    /// 把一条证据并进证据表：**同一种证据只留一行**（先加的那一行是"没试"的占位，
    /// 真试过之后由试开那一档替换掉）—— 证据表要么六行、要么更少，⛔ 不许同一件事出现两行。
    /// </summary>
    public static IReadOnlyList<VolumeEvidence> Concat(IReadOnlyList<VolumeEvidence> source, VolumeEvidence row)
    {
        var result = new List<VolumeEvidence>();

        foreach (VolumeEvidence existing in source)
        {
            if (existing.Kind != row.Kind)
            {
                result.Add(existing);
            }
        }

        result.Add(row);

        return result;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "量不出体积";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + units[unit];
    }
}
