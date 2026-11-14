using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// 组键 ——「锚点这条路径属于哪一组」的唯一入口：读同目录事实、交给卷名判据归组、再把锚点对回它那一组。
///
/// <para><b>为什么独立成层</b>：组键是整条判定链的**上游事实**（同目录清单 + 归组结果 + 锚点落点），
/// 而它的错法只有一种、代价最大：把一个"没有卷标记的锚点"（<c>111</c> / <c>电影.mp4</c>）认到错组上去。
/// 所以锚点匹配必须只有一处：先按"卷清单里逐字有它"认，认不到才按**基名段相同**反向找 ——
/// 这就是「后缀可能不同但是名字一定相同」那一条（⛔ 只在同目录内找，跨目录凭什么认定是同一组还没想清楚）。</para>
///
/// <para><b>内容证明</b>：名字被伪装过（<c>001删除</c> / <c>001.txt</c>）且大小不规律的组，只有内容能证明才放行 ——
/// 7z 首卷起始头自述整包字节数 == 组内各卷实际字节和。RAR/ZIP 这里不冒充内容证明，仍走旧的尺寸规律闸门。</para>
///
/// <para><b>失败模型</b>：目录读不到 / 没有读权限 ⇒ 空清单（调用方据此报"判不出"）；
/// 归组为空 / 锚点对不上任何一组 ⇒ <c>null</c>（判不出：不删、不移动、不改名）。
/// ⛔ 一律不猜、不跨目录认亲、绝不抛。</para>
///
/// <para>生命周期：纯静态、无状态；只在同步判定里跑一次，不缓存、不持句柄、不调引擎。</para>
/// </summary>
internal static class VolumeGroupKeyReader
{
    /// <summary>目录里的文件事实（判定器自己去列）。读不到就是空清单（调用方据此报"判不出"）。</summary>
    public static IReadOnlyList<VolumeGroupEntry> EnumerateDirectory(string anchor)
    {
        var result = new List<VolumeGroupEntry>();

        try
        {
            string? directory = Path.GetDirectoryName(anchor);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return result;
            }

            int order = 0;

            foreach (string path in Directory.EnumerateFiles(directory))
            {
                DateTime written = DateTime.MinValue;

                try
                {
                    written = File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    // 时间取不到只影响"与邻居的相对位置"这一条弱证据。
                }

                result.Add(new VolumeGroupEntry
                {
                    Path = SafePathHelper.GetFullPathSafe(path),
                    Size = VolumeFileFacts.SizeOf(path),
                    LastWriteTimeUtc = written,
                    OrderIndex = order++
                });
            }
        }
        catch
        {
            // 读不到目录 ⇒ 空清单 ⇒ 判不出（⛔ 不猜）。
        }

        return result;
    }

    /// <summary>同目录事实 → 归组（唯一出口 <see cref="VolumeGroupDetector"/>）→ 锚点所在的那一组。</summary>
    public static VolumeGroup? LocateAnchorGroup(string anchor, IReadOnlyList<VolumeGroupEntry> entries)
    {
        // ── 命名归组：转调卷名判据的唯一出口 VolumeGroupDetector ──────────────────────
        var candidates = new List<VolumeCandidate>(entries.Count);

        foreach (VolumeGroupEntry entry in entries)
        {
            candidates.Add(new VolumeCandidate { Path = entry.Path, Size = entry.Size });
        }

        IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(
            candidates,
            ProveDisguisedGroupByContent);

        return FindAnchorGroup(groups, anchor);
    }

    /// <summary>
    /// 伪装名字 + 大小不规律的组，只有内容能证明才放行：
    /// 7z 首卷起始头自述整包字节数 == 组内各卷实际字节和。
    /// RAR/ZIP 这里不冒充内容证明，仍走旧的尺寸规律闸门。
    /// </summary>
    private static bool ProveDisguisedGroupByContent(IReadOnlyList<VolumeCandidate> pieces)
    {
        if (pieces == null || pieces.Count < 2)
        {
            return false;
        }

        long actual = 0;
        FileNatureCard? first = null;

        foreach (VolumeCandidate piece in pieces)
        {
            if (piece.Size <= 0)
            {
                return false;
            }

            actual += piece.Size;

            FileNatureCard card = FileNature.Inspect(piece.Path);

            if (card.BodyKind == ArchiveBodyKind.VolumeFirstPiece
                && card.DeclaredTotalBytes > 0)
            {
                first = card;
            }
        }

        return first != null && actual == first.DeclaredTotalBytes;
    }

    /// <summary>从归组结果里找出锚点所在的那一组；锚点自己没卷号时按"基名段相同"反向找。</summary>
    private static VolumeGroup? FindAnchorGroup(IReadOnlyList<VolumeGroup> groups, string anchor)
    {
        foreach (VolumeGroup group in groups)
        {
            foreach (VolumeCandidate volume in group.Volumes)
            {
                if (string.Equals(
                    SafePathHelper.GetFullPathSafe(volume.Path),
                    anchor,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return group;
                }
            }
        }

        /*
         * 锚点自己不是规范卷名（`111` / `一只顶美.z删除ip` / `电影.mp4`）——
         * 那就按"基名段相同"去认门：这正是「后缀可能不同但是名字一定相同」那一条。
         * ⛔ 只在**同目录**里找（跨目录凭什么认定是同一组还没想清楚）。
         */
        string anchorStem = VolumeGroupNaming.StemOf(Path.GetFileName(anchor));

        if (anchorStem.Length == 0)
        {
            return null;
        }

        foreach (VolumeGroup group in groups)
        {
            if (string.Equals(VolumeGroupNaming.StemOf(group.BaseName), anchorStem, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return null;
    }
}
