using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static ArchiveFixer.Extraction.VolumeNamePlanner;
using GatherPiece = ArchiveFixer.Extraction.VolumeNameRepair.GatherPiece;
using SpannedZipDiskGather = ArchiveFixer.Extraction.VolumeGatherPlanner.SpannedZipDiskGather;

namespace ArchiveFixer.Extraction;

public static partial class VolumeGatherPlanner
{        /// <summary>
         /// **入口是跨盘 ZIP 的末片时，把同一组的其余几片"接"到它旁边**（用户 2026-10-05 真机第八批：
         /// `111.z0删除1/2/3` 三片散在三个源目录里，末片 `111.zip` 压在两层层层加密的 RAR 里）。
         ///
         /// <para><b>为什么只有这一步做得成</b>：批首那一刻末片还在包里（没解出来）⇒ 源包那条路看不见它；
         /// 递归链把末片解出来之后，其余几片又全在用户源目录里（⛔ 那条路的候选池刻意不碰源目录）。
         /// 而末片的 EOCD 是**明文**（`-p` 加密的是数据，不影响它）⇒ 它自己写着"我是第 k 片、一共 n 片" ——
         /// 这是**硬证据**，不是猜（用户原话：「这不是让你看着答案写过程」）。</para>
         ///
         /// <para><b>四条同时成立才动</b>：① 入口自述 `k == n`（它是末片）且 <c>n ≥ 2</c>；
         /// ② 池里能一片对一片地凑出第 1..n-1 片（同一卷号两份候选 ⇒ 判不出 ⇒ 整档不做）；
         /// ③ 除末片外那几片**彼此等大**（既有实测口径：只有这一半真成立）；
         /// ④ **同一卷**（跨盘 ⇒ 判不出 ⇒ 整档不做，⛔ 绝不复制大文件）。</para>
         ///
         /// <para><b>动作 = 硬链接，不是搬、不是复制、不改名</b>：给用户源目录里那几片在**入口那一层**
         /// 多起一个规范卷名（`111.z0删除1` ⇒ 旁边多一个 `111.z01`），零字节、瞬时，
         /// 源文件一个字节不动、名字一个字符不改；链接随工作区收尾一起消失。
         /// ⛔ 绝不把用户的源片搬进工作区（工作区是整份删的 —— 搬进去等于删数据）。</para>
         ///
         /// <para>判不出 / 凑不齐 ⇒ 什么都不做（<see cref="SpannedZipDiskGather.Complete"/> = false，
         /// 如实报缺哪几片）。</para>
         /// </summary>
    internal static SpannedZipDiskGather ResolveSpannedZipDiskGather(
        string? entryPath,
        IEnumerable<VolumeCandidate>? crossCandidates)
    {
        var notApplicable = new SpannedZipDiskGather { Applicable = false };

        if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
        {
            return notApplicable;
        }

        Detection.SpannedZipIndex? index = Detection.SpannedZipIndex.TryRead(entryPath);

        // 只有"自述是跨盘 zip 的末片、而且不止一片"才进这一档；单盘 zip / 不是 zip ⇒ 不适用。
        if (index == null || index.DiskCount < 2)
        {
            return notApplicable;
        }

        string entryFull = SafePathHelper.GetFullPathSafe(entryPath);
        string entryName = VolumeNameRepair.SafeFileName(entryFull);
        string entryDirectory = Path.GetDirectoryName(entryFull) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(entryName);
        int diskCount = index.DiskCount;

        if (entryDirectory.Length == 0 || baseName.Length == 0)
        {
            return notApplicable;
        }

        /*
         * 入口那一层现在有哪些盘：**只认逐字规范名** —— 脏名（`111.z0删除1`）对引擎不算数，
         * 拿它当"这一片已经在旁边"会让引擎照样报 `Missing volume`。
         */
        var presentSizes = new Dictionary<int, long>();

        foreach (string? name in EnumerateFileNamesInDirectory(entryFull))
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            for (int disk = 1; disk < diskCount; disk++)
            {
                if (string.Equals(name, CanonicalDiskName(baseName, disk), StringComparison.OrdinalIgnoreCase))
                {
                    presentSizes[disk] = LengthOf(Path.Combine(entryDirectory, name!));
                }
            }
        }

        var missing = new List<int>();

        for (int disk = 1; disk < diskCount; disk++)
        {
            if (!presentSizes.ContainsKey(disk))
            {
                missing.Add(disk);
            }
        }

        if (missing.Count == 0)
        {
            return new SpannedZipDiskGather
            {
                Applicable = true,
                Complete = true,
                DiskCount = diskCount,
                Detail = StatusText.SpannedZipDisksAlreadyBeside
            };
        }

        // 池里找缺的那几片：一片对一片（同一卷号两份候选 ⇒ 判不出 ⇒ 整档不做）。
        var picked = new Dictionary<int, VolumeCandidate>();
        bool ambiguous = false;
        bool acrossVolume = false;

        foreach (VolumeCandidate? candidate in crossCandidates ?? Array.Empty<VolumeCandidate>())
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path) || !File.Exists(candidate.Path))
            {
                continue;
            }

            string candidateName = VolumeNameRepair.SafeFileName(candidate.Path);

            if (!LegacyVolumeNames.BelongsToSameGroup(entryName, candidateName))
            {
                continue;
            }

            int? familyIndex = LegacyVolumeNames.TryGetIndex(candidateName);
            int disk;

            if (familyIndex == null)
            {
                /*
                 * ⚠ 2026-10-09 A2（用户原话：「已经知道有多少分卷了…算法已经弄得不错了」）：
                 * 名字读不出盘号时，**用组内事实 + 取值范围再解一次**（真机 `111.z11111111110删除3`：
                 * 骨架档得到 `z111111111103`、不是合法标记 ⇒ 老判据在这里丢掉它 ⇒
                 * 那一片永远接不进来，整组到最后一刻还报"缺 111.z03"）。
                 * ⛔ 只在三条同时成立时认：本族位宽内的最右数字、落在 1..diskCount-1、
                 * **正好是缺的那一片**；判不出 ⇒ 什么都不做。
                 */
                if (!TryResolveDiskByGroupRange(candidateName, diskCount, missing, out disk))
                {
                    continue;
                }
            }
            else
            {
                // 这一族的编号：`.zip` = 1（本体 / 末片）、`.zNN` = NN + 1 ⇒ 真实盘序 = 编号 - 1。
                disk = familyIndex.Value - 1;
            }

            if (disk < 1 || disk >= diskCount || !missing.Contains(disk) || LengthOf(candidate.Path) <= 0)
            {
                continue;
            }

            if (!HardLinkHelper.CanHardLink(candidate.Path, entryDirectory))
            {
                // 跨盘：⛔ 不复制大文件 ⇒ 整档不做（如实说）。
                acrossVolume = true;
                continue;
            }

            if (picked.TryGetValue(disk, out VolumeCandidate? existing))
            {
                if (!SamePath(existing.Path, candidate.Path))
                {
                    ambiguous = true;
                }

                continue;
            }

            picked[disk] = candidate;
        }

        if (ambiguous || acrossVolume)
        {
            return new SpannedZipDiskGather
            {
                Applicable = true,
                DiskCount = diskCount,
                MissingNames = missing.Select(disk => CanonicalDiskName(baseName, disk)).ToList(),
                Detail = ambiguous
                    ? StatusText.SpannedZipDisksAmbiguous
                    : StatusText.SpannedZipDisksAcrossVolume
            };
        }

        List<string> unresolved = missing
            .Where(disk => !picked.ContainsKey(disk))
            .Select(disk => CanonicalDiskName(baseName, disk))
            .ToList();

        if (unresolved.Count > 0)
        {
            return new SpannedZipDiskGather
            {
                Applicable = true,
                DiskCount = diskCount,
                MissingNames = unresolved,
                Detail = DescribeStillMissing(diskCount, unresolved)
            };
        }

        // ③ 除末片外那几片彼此等大（已有的 + 要接的，一起比）。
        var sizes = new List<long>(presentSizes.Values);

        foreach (VolumeCandidate candidate in picked.Values)
        {
            sizes.Add(LengthOf(candidate.Path));
        }

        if (sizes.Count > 1 && sizes.Distinct().Count() > 1)
        {
            return new SpannedZipDiskGather
            {
                Applicable = true,
                DiskCount = diskCount,
                MissingNames = missing.Select(disk => CanonicalDiskName(baseName, disk)).ToList(),
                Detail = StatusText.SpannedZipDisksSizeMismatch
            };
        }

        // 动作：给这几片在入口那一层多起一个规范卷名（硬链接；全成或全不成，失败把建好的删掉）。
        var created = new List<string>();

        foreach ((int disk, VolumeCandidate candidate) in picked.OrderBy(pair => pair.Key))
        {
            string canonical = CanonicalDiskName(baseName, disk);
            string target = Path.Combine(entryDirectory, canonical);

            if (File.Exists(target) || !HardLinkHelper.TryCreateHardLink(target, candidate.Path))
            {
                RollBack();
                return new SpannedZipDiskGather
                {
                    Applicable = true,
                    DiskCount = diskCount,
                    MissingNames = new[] { canonical },
                    Detail = File.Exists(target)
                        ? StatusText.SpannedZipDisksTargetOccupied
                        : StatusText.SpannedZipDisksLinkFailed
                };
            }

            created.Add(target);
        }

        /*
         * 建完**重新读一次盘上事实**：这一组到底齐不齐，只能由那个目录里现在有什么回答 ——
         * ⛔ 不拿"刚刚链接成功了"当结论。
         */
        var nowPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? name in EnumerateFileNamesInDirectory(entryFull))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                nowPresent.Add(name!);
            }
        }

        var stillMissing = new List<string>();

        for (int disk = 1; disk < diskCount; disk++)
        {
            string canonical = CanonicalDiskName(baseName, disk);

            if (!nowPresent.Contains(canonical))
            {
                stillMissing.Add(canonical);
            }
        }

        return new SpannedZipDiskGather
        {
            Applicable = true,
            Complete = stillMissing.Count == 0,
            DiskCount = diskCount,
            LinkedCount = created.Count,
            LinkedPaths = created.ToList(),
            LinkedSources = picked.OrderBy(pair => pair.Key).Select(pair => pair.Value.Path).ToList(),
            MissingNames = stillMissing,
            Detail = stillMissing.Count == 0
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.SpannedZipDisksLinkedFormat,
                    diskCount,
                    created.Count)
                : DescribeStillMissing(diskCount, stillMissing)
        };

        void RollBack()
        {
            foreach (string path in created)
            {
                try
                {
                    FileOperations.Delete(path);
                }
                catch
                {
                    // 删不掉就留着 —— 它只是多出来的一个名字，不碰数据。
                }
            }

            created.Clear();
        }
    }

    /// <summary>这一族第 <paramref name="disk"/> 片的规范卷名（<c>111</c> + 1 ⇒ <c>111.z01</c>）。</summary>
    internal static string CanonicalDiskName(string baseName, int disk) => $"{baseName}.z{disk:00}";

    /// <summary>
    /// **名字读不出盘号时，用组内事实 + 取值范围解出它该是第几片**（用户 2026-10-09 A2）。
    ///
    /// <para>形状：跨盘 ZIP 的片被塞了垃圾、数字还连排（真机 `111.z11111111110删除3`）——
    /// `TrySplitVolumeSegmentLoose` 的骨架档得到 `z111111111103`、不是合法标记 ⇒
    /// 老的接片判据当场丢掉它，于是那一片**永远接不进来**，整组到最后一刻还在报「缺 111.z03」。</para>
    ///
    /// <para>判据（⛔ 只用组内事实 + 取值范围，⛔ 不猜）：把段里的**非 ASCII 字母数字杂质剥掉**，
    /// 要求 ① 骨架形如 <c>z</c>/<c>Z</c> + **至少三位**数字（数字连排正是要救的那一档）；
    /// ② 取**最右两位**当盘号（本族 `zNN` 的位宽是 2 —— 用户口径：「已知 4 片，最大的就是 z03」）；
    /// ③ 这个盘号必须落在这一组的合法范围 <c>1..diskCount-1</c>、且**正好是缺的那一片**。
    /// 三条缺一条 ⇒ 返回 false（判不出 ⇒ 什么都不做）。</para>
    ///
    /// <para>⚠ 为什么敢取最右两位：左边那串数字是**垃圾**（网盘塞的），而本族的位宽由组内事实
    /// 封死（4 片 ⇒ 合法标记只有 `z01`/`z02`/`z03` + 末片 `.zip`）⇒ 只有最右两位能落在取值范围内。
    /// 放在这里之前先做唯一性检查的是调用方（同卷号两份候选 ⇒ 判不出 ⇒ 整档不做）。</para>
    /// </summary>
    internal static bool TryResolveDiskByGroupRange(
        string candidateName,
        int diskCount,
        IReadOnlyCollection<int> missing,
        out int disk)
    {
        disk = 0;

        if (string.IsNullOrWhiteSpace(candidateName) || diskCount < 2 || missing == null || missing.Count == 0)
        {
            return false;
        }

        string[] parts = candidateName.Split('.');

        if (parts.Length < 2)
        {
            return false;
        }

        // 末段（`z11111111110删除3`）；RAR/7z 那两族有自己的位宽，本轮只救跨盘 ZIP 这一族。
        string segment = parts[^1];
        var digits = new System.Text.StringBuilder(segment.Length);

        foreach (char c in segment)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (char.IsAsciiLetter(c))
            {
                // 段里出现字母（`z` 之外）⇒ 不是本族形状，⛔ 不猜。
                if (digits.Length > 0)
                {
                    return false;
                }
            }
        }

        string raw = digits.ToString();

        if (raw.Length < 3)
        {
            // 两位及以下：紧档/骨架档本来就解得出来，轮不到这一档（这一档只救"数字连排"）。
            return false;
        }

        string mark = segment.TrimStart().Length > 0 && (segment[0] == 'z' || segment[0] == 'Z')
            ? segment[..1]
            : string.Empty;

        if (mark.Length == 0)
        {
            return false;
        }

        int candidateDisk = int.Parse(raw[^2..], System.Globalization.CultureInfo.InvariantCulture);

        if (candidateDisk < 1 || candidateDisk >= diskCount || !missing.Contains(candidateDisk))
        {
            return false;
        }

        disk = candidateDisk;
        return true;
    }


}
