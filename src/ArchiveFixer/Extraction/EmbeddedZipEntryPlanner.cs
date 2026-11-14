using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 条目**落点规划**：把中央目录里的名字翻成"目标根之下的相对路径 + 最终落盘路径"。
///
/// <para>与正常解压**同一处实现**的判据：先用 <see cref="FileNameHelper.SanitizeFileName"/> 逐段清洗，
/// 再用 <see cref="ArchivePathGuard.IsInsideRoot"/> 复核完整路径确实在目标根之内 ——
/// 不另写一份路径规则（AGENTS.md §6 第 4 条：判据走唯一出口）。</para>
///
/// <para>识别阶段的探查没有目标根（<c>targetDirectory</c> 为 null）：那时只做"能算出相对路径"这一步，
/// 拿不到"在不在根之内"的结论，也就不假装有。</para>
///
/// <para>⛔ 只算路径、不建目录、不碰盘。</para>
/// </summary>
internal static class EmbeddedZipEntryPlanner
{
    /// <summary>
    /// 给每个条目算出落盘路径：逐段清洗名字，再复核完整路径确实在目标根之内。
    ///
    /// <para><paramref name="targetDirectory"/> 为 null（识别阶段的探查）时只做"能算出相对路径"这一步。</para>
    /// </summary>
    internal static bool TryBuildOutputPaths(
        List<EmbeddedZipEntry> entries,
        string? targetDirectory,
        out string reason)
    {
        reason = string.Empty;

        for (int i = 0; i < entries.Count; i++)
        {
            EmbeddedZipEntry entry = entries[i];

            // 统一按正斜杠切段：ZIP 里两种斜杠混用很常见，只认一种就会漏判。
            string normalized = entry.Name.Replace('\\', '/');
            var parts = new List<string>();

            foreach (string segment in normalized.Split('/'))
            {
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }

                // ".." 已经在 ArchivePathGuard 那一关被拒了；这里再兜一次，绝不让它进落点。
                if (segment == "..")
                {
                    reason = $"条目名里有 .. 片段：{entry.Name}";
                    return false;
                }

                parts.Add(FileNameHelper.SanitizeFileName(segment));
            }

            if (parts.Count == 0)
            {
                /*
                 * 目录条目指向归档根（名字是 "/" 或 "./" 这种）：落点就是目标根本身，什么都不用建。
                 * 文件条目走到这里说明名字里没有可用的一段 —— 那是真写不出来的东西，拒绝整包
                 * （与"归档里有不安全的条目"同一口径，绝不静默丢一个用户以为解出来的文件）。
                 */
                if (!entry.IsDirectory)
                {
                    reason = $"条目名清洗之后没有有效路径：{entry.Name}";
                    return false;
                }

                entries[i] = new EmbeddedZipEntry
                {
                    Name = entry.Name,
                    RelativePath = string.Empty,
                    OutputPath = targetDirectory ?? string.Empty,
                    Size = 0,
                    CompressedSize = entry.CompressedSize,
                    IsDirectory = true,
                    Method = entry.Method,
                    LocalHeaderOffset = entry.LocalHeaderOffset,
                    Crc32 = entry.Crc32,
                    AesStrength = 0,
                    IsEncrypted = entry.IsEncrypted
                };

                continue;
            }

            string relative = string.Join(Path.DirectorySeparatorChar.ToString(), parts);
            string outputPath = string.IsNullOrWhiteSpace(targetDirectory)
                ? string.Empty
                : Path.Combine(targetDirectory!, relative);

            if (!string.IsNullOrWhiteSpace(targetDirectory) &&
                !ArchivePathGuard.IsInsideRoot(targetDirectory, outputPath, out string insideReason))
            {
                reason = $"{entry.Name} —— {insideReason}";
                return false;
            }

            entries[i] = new EmbeddedZipEntry
            {
                Name = entry.Name,
                RelativePath = relative,
                OutputPath = outputPath,
                Size = entry.Size,
                CompressedSize = entry.CompressedSize,
                IsDirectory = entry.IsDirectory,
                Method = entry.Method,
                LocalHeaderOffset = entry.LocalHeaderOffset,
                /*
                 * ⚠ 这里是把条目**重建**一遍（要补相对路径与落点），所以每加一个字段都必须在这里跟着抄一次：
                 * 2026-09-25 就漏过 AesStrength（新增字段），后果是"密码明明校验通过了，
                 * 解压时却按未加密路径去读密文" —— 报出来的错是"字节数与清单不符"，与真实原因毫不相干。
                 */
                Crc32 = entry.Crc32,
                AesStrength = entry.AesStrength,
                IsEncrypted = entry.IsEncrypted
            };
        }

        return true;
    }
}
