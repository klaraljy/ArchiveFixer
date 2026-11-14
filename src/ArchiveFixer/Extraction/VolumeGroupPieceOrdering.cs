using ArchiveFixer.Detection;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **定序**（第二大步发现层的第 3 段）：把已经判定"可能是同一组"的片排出顺序，并点名入口片。
///
/// <para>三档顺序与既有判据一个字不改：① 跨盘 ZIP 那一族**只按名字**（`z01`→1 … `.zip`→N，
/// 头尾在名字编号里是反的）；② 7z 族：入口/首卷在前，其余按名字卷号，再按内容卷号；
/// ③ 其余族：内容卷号优先，同号再按大小。</para>
///
/// <para><b>只排顺序、不下结论</b>：齐没齐、缺哪几片由 <see cref="VolumeCandidateVerdict"/> 判；
/// 这里不读盘、不猜缺卷、不改名。</para>
///
/// <para><b>失败模型</b>：排不出（跨盘 ZIP 形状不完整 / 卷号缺失）就按名字与大小兜底或返回 false，
/// 由调用方落到"判不出"那一档；⛔ 不抛。</para>
///
/// <para><b>生命周期</b>：纯静态、无状态；输入是已建好的片证据，输出是一个新列表/顺序，输入不被修改。</para>
/// </summary>
internal static class VolumeGroupPieceOrdering
{
    /// <summary>
    /// 7z 分卷的片顺序：入口/首卷在前，其余按名字卷号；名字判不出再按内容卷号。
    /// ⛔ 非均匀分卷下不能按大小排序：首卷可以比第二卷更大，也可以更小。
    /// </summary>
    public static IReadOnlyList<string> OrderByEntryThenVolumeIdentity(
        IReadOnlyList<VolumeCandidatePieceFacts> pieces,
        string? entryPath)
    {
        return pieces
            .OrderBy(item => !string.IsNullOrWhiteSpace(entryPath)
                             && string.Equals(item.Path, entryPath, StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ThenBy(item => LegacyVolumeNames.TryGetIndex(
                Path.GetFileName(item.Path)) ?? int.MaxValue)
            .ThenBy(item => item.VolumeNumber ?? int.MaxValue)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path)
            .ToList();
    }

    /// <summary>
    /// 判完整性时的顺序（与判据配套）：7z 族按"首片 → 名字卷号 → 内容卷号"；
    /// 其余族按"内容卷号 → 大小"。
    /// </summary>
    public static List<VolumeCandidatePieceFacts> OrderForJudging(
        ArchiveFixer.Detection.VolumeContentFormat format,
        IReadOnlyList<VolumeCandidatePieceFacts> items)
    {
        return format == ArchiveFixer.Detection.VolumeContentFormat.SevenZip
            ? items
                .OrderBy(item => item.IsFirstPiece ? 0 : 1)
                .ThenBy(item => LegacyVolumeNames.TryGetIndex(
                    Path.GetFileName(item.Path)) ?? int.MaxValue)
                .ThenBy(item => item.VolumeNumber ?? int.MaxValue)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : items
                .OrderBy(item => item.VolumeNumber ?? int.MaxValue)
                .ThenBy(item => item.Size)
                .ToList();
    }

    /// <summary>
    /// 入口片：有首片标记或内容卷号 = 1 的那一片；内容判不出时，名字卷号 = 1 的顶替。
    /// 都没有 ⇒ 空串（判不出，⛔ 不挑一个充数）。
    /// </summary>
    public static string FindEntry(IReadOnlyList<VolumeCandidatePieceFacts> items)
    {
        string entry = items
            .FirstOrDefault(item => item.IsFirstPiece || item.VolumeNumber == 1)
            ?.Path ?? string.Empty;

        if (entry.Length == 0)
        {
            entry = items
                .FirstOrDefault(item => LegacyVolumeNames.TryGetIndex(
                    Path.GetFileName(item.Path)) == 1)
                ?.Path ?? string.Empty;
        }

        return entry;
    }

    /// <summary>
    /// 跨盘 ZIP 名字定序：`x.z01`..`x.zNN` + 末片 `x.zip` ⇒ 第 1..N 片。
    /// ⛔ 只要有一份不是这个形状（或 z 号不连续）⇒ 返回 false，交给别的判据。
    /// </summary>
    public static bool TryOrderSpannedZip(
        IReadOnlyList<VolumeCandidatePieceFacts> items,
        out List<VolumeCandidatePieceFacts> ordered,
        out List<int> numbers)
    {
        ordered = new List<VolumeCandidatePieceFacts>();
        numbers = new List<int>();

        if (items.Count < 2)
        {
            return false;
        }

        var disks = new List<(int Disk, VolumeCandidatePieceFacts Item)>();
        VolumeCandidatePieceFacts? tailZip = null;

        foreach (VolumeCandidatePieceFacts item in items)
        {
            string extension = Path.GetExtension(Path.GetFileName(item.Path));

            if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (tailZip != null)
                {
                    return false;
                }

                tailZip = item;
                continue;
            }

            if (extension.Length == 4
                && (extension[1] == 'z' || extension[1] == 'Z')
                && char.IsAsciiDigit(extension[2])
                && char.IsAsciiDigit(extension[3]))
            {
                disks.Add((int.Parse(extension.AsSpan(2, 2), System.Globalization.CultureInfo.InvariantCulture), item));
                continue;
            }

            return false;
        }

        if (tailZip == null || disks.Count == 0)
        {
            return false;
        }

        disks.Sort((left, right) => left.Disk.CompareTo(right.Disk));

        for (int index = 0; index < disks.Count; index++)
        {
            if (disks[index].Disk != index + 1)
            {
                return false;
            }

            ordered.Add(disks[index].Item);
            numbers.Add(index + 1);
        }

        ordered.Add(tailZip);
        numbers.Add(disks.Count + 1);

        return true;
    }
}
