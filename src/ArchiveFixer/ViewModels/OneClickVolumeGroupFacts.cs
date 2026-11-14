using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理 · **分卷组事实**（只读纯查询）：按目录 / 按名字算出"这一份与哪些文件同属一组"。
///
/// <para>从 <c>OneClickCoordinator</c> 按职责搬出来：这些方法都是**只算不写**的事实查询，
/// 续解（第二大步）与源包整组搬运共用同一份判据；协调器只保留同名的一行转发
/// （既有用例与调用点按那个名字指认，⛔ 判据只有这一处实现）。</para>
/// </summary>
internal static class OneClickVolumeGroupFacts
{
    /// <summary>
    /// **只算不写**：按一个任务**所在目录里的真实文件**算出它属于哪一组（算不出返回 <c>null</c>）。
    ///
    /// <para>为什么要把"算"和"写"分开（用户 2026-10-01）：调用方有两种 ——
    /// 一种要**把结论记到任务上**（续解层补组信息、开工前补整组清单），另一种只想**看一眼**
    /// （"我是不是这一组的后续卷"）。后者若顺手把账改了，就会把别处已经算好的清单**砍短**：
    /// 两卷分处两个目录时，按目录归组只会给出 1 卷，而任务账上那份（来自改名同步 /
    /// 扫描期归组）本来是完整的 —— 那会把"源包按整组搬"变成"只搬第一卷"。
    /// 判据只有这一份（<see cref="VolumeGroupDetector.Group"/>）。</para>
    /// </summary>
    internal static ArchiveFixer.Detection.VolumeGroup? ResolveVolumeGroupFromDirectory(ArchiveTask? task)
    {
        if (task == null)
        {
            return null;
        }

        string path = task.CurrentPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        var candidates = new List<ArchiveFixer.Detection.VolumeCandidate>();

        foreach (string sibling in Directory.EnumerateFiles(directory))
        {
            long size = -1;

            try
            {
                size = new FileInfo(sibling).Length;
            }
            catch
            {
                // 量不出大小不影响归组；"伪装名"那一档要尺寸佐证，量不出就不放行那一档。
            }

            candidates.Add(new ArchiveFixer.Detection.VolumeCandidate { Path = sibling, Size = size });
        }

        string fullPath = Path.GetFullPath(path);

        return ArchiveFixer.Detection.VolumeGroupDetector.Group(candidates)
            .FirstOrDefault(g => g.Volumes.Any(
                v => string.Equals(Path.GetFullPath(v.Path), fullPath, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// **两个归组来源取更全的那一份**（用户 2026-10-01 第三 / 第四 / 第五次真机踩出来的唯一正确问法）。
    ///
    /// <para>现场（`222.zip` + `222.z01`，都是批首改好的标准名）：
    /// <see cref="ResolveVolumeGroupFromDirectory"/> 拿 `222.zip` **返回的不是 null，而是一个"只有 1 卷"的组**
    /// —— 因为按名字归组时 `.zip` 自己成一组、`222.z01` 只是它"没卷号的候选"；而
    /// <see cref="ResolveVolumeGroupFromDirectoryByName"/> 能看出两卷同基名。
    /// 前两轮白改的根因都是"只看能不能归出组"：第一次写成"自己的名字带卷标记"（`.zip` 不是 ⇒ 恒 false），
    /// 第二次写成"归不出组时才问按名字"（这里**归得出**，只是少一卷）⇒ 补清单永远轮不到。</para>
    ///
    /// <para>⛔ 判据只有一句：**谁卷数多就用谁**；一样多时保留按名字归的那份（它对"脏名兄弟"更敏感）。
    /// 调用方仍然走"只增不减"的闸门 —— 这个方法只回答"该用哪份清单"，不写任何账。</para>
    /// </summary>
    internal static ArchiveFixer.Detection.VolumeGroup? PickFullerGroup(
        ArchiveFixer.Detection.VolumeGroup? byDirectory,
        ArchiveFixer.Detection.VolumeGroup? byName)
    {
        if (byDirectory == null)
        {
            return byName;
        }

        if (byName == null)
        {
            return byDirectory;
        }

        return byName.Volumes.Count >= byDirectory.Volumes.Count ? byName : byDirectory;
    }

    /// <summary>
    /// **这一单一开工就必须按名字重算一次分卷清单吗**（用户 2026-10-01 第三 / 第四次真机）。
    ///
    /// <para>判据只有一条：**同目录里有没有和它同基名、名字带卷标记的兄弟**
    /// （`222.zip` 旁边躺着 `222.z01`）。⛔ 不能写成"自己的名字里带卷标记" ——
    /// 跨盘 zip 的**本体**叫 `222.zip`，`.zip` 不是卷标记 ⇒ 那条判断恒为 false，
    /// 补清单永远跑不到（第三报就是这样白改了一轮：400 MB 的 `222.z01` 照旧留在源目录）。</para>
    ///
    /// <para>抽成 internal 静态是为了让守门用例**直接钉这个判据**（不必构造整个 MainViewModel）。</para>
    /// </summary>
    internal static bool NeedsVolumeGroupRefreshByName(ArchiveTask? task, string? currentPath)
    {
        if (task == null || string.IsNullOrWhiteSpace(currentPath))
        {
            return false;
        }

        // 扫描期已经归过组的照旧要重算（账上那份可能只有一卷）。
        if (task.IsVolumeGroup)
        {
            return true;
        }

        // 标准卷名的续卷（`.001` / `.z01` / `.part2`）也要重算。
        if (LegacyVolumeNames.IsVolumePart(FileNameHelper.GetFileName(currentPath)))
        {
            return true;
        }

        // 剩下这一档就是真机那个形状：本体叫 `222.zip`，靠"同目录有没有卷兄弟"回答。
        return ResolveVolumeGroupFromDirectoryByName(task, currentPath) != null;
    }

    /// <summary>
    /// **按名字补整组清单**（用户 2026-10-01 第三报）：账上的分卷清单可能过期（改名 / 扫描期只认出一卷），
    /// 而 `222.zip` 单看又归不出组 ⇒ 这一档按"本目录里基名相同、名字里带卷标记"的文件重新支一组出来。
    ///
    /// <para><b>为什么需要它</b>：整组改名把 `222.zscip` → `222.zip`、`222.z删除01` → `222.z01` 都改对了，
    /// 可任务账上的清单还是扫描期那一份（1 卷）⇒ 源包搬运只搬了 `222.zip` 自己，
    /// 400 MB 的 `222.z01` 留在源目录里。</para>
    ///
    /// <para><b>判据只用事实</b>：基名相同（点号之前那一段）+ 名字里带卷标记（转调
    /// <see cref="ExtensionHelper.TrySplitVolumeSegmentTolerant"/> 这个唯一出口 —— `z01` 与 `z0sc1` 都认）
    /// + 文件真的在盘上。⛔ 目标名 / 卷序都不猜：这里只回答"本目录里哪些文件与它同属一组"，
    /// 整组搬不搬仍由调用方"只增不减"的闸门决定。</para>
    /// </summary>
    internal static ArchiveFixer.Detection.VolumeGroup? ResolveVolumeGroupFromDirectoryByName(
        ArchiveTask? task,
        string currentPath)
    {
        if (task == null || string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            return null;
        }

        string directory = Path.GetDirectoryName(currentPath) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string fileName = Path.GetFileName(currentPath);

        /*
         * 基名 = **第二大步那一份**（唯一出口 `FileNameHelper.SecondStepBaseName`）。
         * 原来这里自己按第一个点切了一份 —— 结论与收口后的规则相同，但那是第二份实现
         * （用户 2026-10-23：「14 处以后统统删掉」）。
         * 没有点 / 以点开头 ⇒ 判不出（与收口前同一结论：这一档按名字归组，名字给不出基名就不动）。
         */
        string stem = FileNameHelper.SecondStepBaseName(fileName);

        if (stem.Length == 0 || stem.Length >= fileName.Length)
        {
            return null;
        }

        var volumes = new List<ArchiveFixer.Detection.VolumeCandidate>();
        int maxIndex = 0;

        foreach (string sibling in Directory.EnumerateFiles(directory))
        {
            string siblingName = Path.GetFileName(sibling);
            string siblingStem = FileNameHelper.SecondStepBaseName(siblingName);

            if (siblingStem.Length == 0
                || siblingStem.Length >= siblingName.Length
                || !string.Equals(siblingStem, stem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string segment = siblingName[(siblingStem.Length + 1)..];
            int index;

            if (segment.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                index = 1;
            }
            else if (ExtensionHelper.TrySplitVolumeSegmentTolerant(segment, out string mark, out _)
                     && mark.Length == 3
                     && (mark[0] == 'z' || mark[0] == 'Z')
                     && int.TryParse(mark.AsSpan(1), out int ordinal)
                     && ordinal >= 1)
            {
                index = ordinal + 1;
            }
            else
            {
                continue;
            }

            long size = -1;

            try
            {
                size = new FileInfo(sibling).Length;
            }
            catch
            {
                // 量不出大小不影响"这一卷在不在"这条事实。
            }

            volumes.Add(new ArchiveFixer.Detection.VolumeCandidate { Path = sibling, Size = size });
            maxIndex = Math.Max(maxIndex, index);
        }

        if (volumes.Count < 2)
        {
            return null;
        }

        /*
         * 组对象只填"清单"这三件事（基名 / 各卷 / 首卷 = 本体那一份）——
         * ⛔ 不编缺卷名、不编卷总数：调用方要的只是"这一组有哪些文件"。
         */
        string body = Path.Combine(directory, stem + ".zip");
        string firstVolumePath = File.Exists(body) ? body : volumes[0].Path;

        return new ArchiveFixer.Detection.VolumeGroup
        {
            GroupKey = directory + "|" + stem,
            DirectoryPath = directory,
            BaseName = stem,
            Volumes = volumes,
            FirstVolumePath = firstVolumePath,
            KnownVolumeCount = maxIndex,
            ExpectedVolumeCount = 0,
            IsComplete = true,
            MissingVolumeNames = Array.Empty<string>(),
            Note = $"按名字补出来的清单：同目录里 {volumes.Count} 个文件与「{fileName}」同基名（{stem}）且带卷标记"
        };
    }
}
