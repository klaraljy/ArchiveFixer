using ArchiveFixer.Detection;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 分卷候选的两个只读入口：**找卷窗口**（最外层源包附近）与**交给引擎的实际文件名**
/// （同目录同物理文件的规范卷名那一份）。
///
/// <para>本类只算候选、不改路径归属：任务账与源包档一个字都不动；判不出 ⇒ 原样返回 / 空池。
/// 缓存与生命周期仍由宿主 <see cref="RecursiveExtractor"/> 持有。</para>
/// </summary>
internal static class RecursiveSourceCandidatePool
{
    /// <summary>
    /// **同目录里若有"同一个物理文件"的规范卷名那一份，就把交给引擎的路径换成它**
    /// （用户 2026-10-16 口径的落点：引擎按"打开的那一份的名字"推兄弟卷名 ⇒ 拿到伪装名必然找不到兄弟卷；
    /// 而规范名那一份就在同目录、同一个 File ID）。真机现场 `21212121` / `24242424` 逐字：
    /// 入口那一份自己叫 `aaa-L2.part0788839中文1.rar` / `111-L2.part020745382.rar`。
    ///
    /// <para>判据全是事实：① 卡上的 `CanonicalVolumeName` 非空且与当前文件名**不同**；
    /// ② 同目录里存在这个名字；③ `FileIdentity.IsSamePhysicalFile` 判**同一份**。
    /// ⛔ 任何一条不成立 / 抛异常 ⇒ **原样返回**（一个字节都不改，老口径）。</para>
    ///
    /// <para>⛔ 只影响"交给引擎的那一份"：任务账（`task.CurrentPath`）与源包档**一个字都不动** ——
    /// 否则「源包彻底删除」会删错名字（删掉那个链接、用户那份伪装名反而留着）。</para>
    /// </summary>
    internal static string PreferCanonicalSiblingName(string archivePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return archivePath;
            }

            FileNatureCard card = FileNature.Inspect(archivePath);
            string canonical = card.CanonicalVolumeName;

            if (canonical.Length == 0
                || string.Equals(canonical, Path.GetFileName(archivePath), StringComparison.OrdinalIgnoreCase))
            {
                return archivePath;
            }

            string? directory = Path.GetDirectoryName(archivePath);

            if (string.IsNullOrEmpty(directory))
            {
                return archivePath;
            }

            string candidate = Path.Combine(directory, canonical);

            return File.Exists(candidate) && FileIdentity.IsSamePhysicalFile(archivePath, candidate)
                ? candidate
                : archivePath;
        }
        catch
        {
            return archivePath;
        }
    }

    /// <summary>
    /// 找卷窗口里那批候选（见 `_rootSourcePath` 的说明；算不出来就是空池）。
    ///
    /// <para>枚举不动 = 判不出：空池 ⇒ 那一档什么都不做（⛔ 不抛异常给上层）。</para>
    /// </summary>
    internal static IReadOnlyList<VolumeCandidate> EnumerateNearby(string rootSourcePath)
    {
        try
        {
            return string.IsNullOrWhiteSpace(rootSourcePath)
                ? Array.Empty<VolumeCandidate>()
                : VolumeNameRepair.EnumerateVolumeCandidatesNearby(rootSourcePath);
        }
        catch
        {
            return Array.Empty<VolumeCandidate>();
        }
    }
}
