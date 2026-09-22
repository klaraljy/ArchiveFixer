using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;

namespace ArchiveFixer.Extraction
{
    /// <summary>一条待归置项被跳过的原因。</summary>
    public enum ArtifactSkipReason
    {
        /// <summary>没有跳过。</summary>
        None = 0,

        /// <summary>源路径为空。</summary>
        EmptySource,

        /// <summary>同一个源被规划了两次。</summary>
        DuplicateSource,

        /// <summary>源本来就在过程物目录里（已经归置过，不需要再搬）。</summary>
        SourceInsideArtifactDirectory,

        /// <summary>相对路径不合法（越界 <c>..</c> / 绝对路径 / 盘符 / UNC / 保留名 / 结尾点空格等）。</summary>
        InvalidRelativePath,

        /// <summary>源与目标同一个位置（搬到原地）。</summary>
        SourceEqualsTarget,

        /// <summary>目标落在源目录内部（会自己套自己）。</summary>
        TargetInsideSource,

        /// <summary>没有给出目标目录，整批无法规划。</summary>
        TargetDirectoryMissing
    }

    /// <summary>一条待归置的路径。</summary>
    public sealed class ArtifactPlacementItem
    {
        /// <summary>待归置的路径（文件或目录）。</summary>
        public string SourcePath { get; init; } = string.Empty;

        /// <summary>
        /// 相对某个基准的相对路径（例：<c>inner\pack.7z.001</c>）。
        /// 留空时按 <see cref="ArtifactPlacementRequest.BaseDirectory"/> 推算，再不行用文件名。
        /// </summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>为什么算过程物（写报告 / 日志用，例："内层归档"）。</summary>
        public string Reason { get; init; } = string.Empty;

        public ArtifactPlacementItem()
        {
        }

        public ArtifactPlacementItem(string sourcePath, string relativePath = "", string reason = "")
        {
            SourcePath = sourcePath ?? string.Empty;
            RelativePath = relativePath ?? string.Empty;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>一次归置规划请求。</summary>
    public sealed class ArtifactPlacementRequest
    {
        /// <summary>目标目录 <c>D</c>（内容物目录）；过程物目录 = <c>D\过程物</c>。</summary>
        public string TargetDirectory { get; init; } = string.Empty;

        /// <summary>相对路径的基准目录（例：本层产物目录）。留空时按文件名归置。</summary>
        public string BaseDirectory { get; init; } = string.Empty;

        public IReadOnlyList<ArtifactPlacementItem> Items { get; init; } = Array.Empty<ArtifactPlacementItem>();

        /// <summary>目标占用探测；不传时按真实文件系统判断（"目标已存在同名就不覆盖"靠它）。</summary>
        public IArtifactTargetProbe? TargetProbe { get; init; }
    }

    /// <summary>一条移动计划：从哪搬到哪。</summary>
    public sealed class ArtifactMove
    {
        public string SourcePath { get; init; } = string.Empty;

        public string TargetPath { get; init; } = string.Empty;

        /// <summary>落点相对过程物目录的相对路径。</summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>为什么算过程物。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>是否因为重名被加了序号（加过序号的要写进报告，让用户知道名字变了）。</summary>
        public bool Renamed { get; init; }
    }

    /// <summary>一条被跳过的待归置项。</summary>
    public sealed class ArtifactSkip
    {
        public string SourcePath { get; init; } = string.Empty;

        public ArtifactSkipReason Reason { get; init; }

        /// <summary>中文说明，可直接给用户看。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>归置计划。<b>只是计划</b>：本类型不代表磁盘上已经发生了任何移动。</summary>
    public sealed class ArtifactMovePlan
    {
        /// <summary>过程物目录的完整路径（<c>D\过程物</c>）；目标目录不合法时为空。</summary>
        public string ArtifactDirectory { get; init; } = string.Empty;

        public IReadOnlyList<ArtifactMove> Moves { get; init; } = Array.Empty<ArtifactMove>();

        public IReadOnlyList<ArtifactSkip> Skipped { get; init; } = Array.Empty<ArtifactSkip>();

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>目标位置占用探测。默认查真实文件系统；单测可注入"什么都占不到"的实现做纯规划。</summary>
    public interface IArtifactTargetProbe
    {
        /// <summary>目标位置是否已被文件或目录占用。</summary>
        bool Exists(string path);

        /// <summary>源是不是目录（决定加序号时怎么拆扩展名）；null = 不知道。</summary>
        bool? IsDirectory(string path);
    }

    /// <summary>真实文件系统的占用探测。</summary>
    public sealed class FileSystemArtifactTargetProbe : IArtifactTargetProbe
    {
        public static FileSystemArtifactTargetProbe Instance { get; } = new();

        public bool Exists(string path)
        {
            return SafePathHelper.FileExists(path) || SafePathHelper.DirectoryExists(path);
        }

        public bool? IsDirectory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>"什么都占不到"的探测：用于纯规划（测试与"只看计划不碰盘"的场景）。</summary>
    public sealed class EmptyArtifactTargetProbe : IArtifactTargetProbe
    {
        public static EmptyArtifactTargetProbe Instance { get; } = new();

        public bool Exists(string path)
        {
            return false;
        }

        public bool? IsDirectory(string path)
        {
            return null;
        }
    }

    /// <summary>
    /// 过程物的**唯一命名来源与唯一归置规则来源**（docs/输出与整理模型.md §3.2、需求变更 R6）。
    ///
    /// 两条硬要求，本类之外不许再出现：
    /// ① 过程物目录名固定叫 <see cref="ArtifactDirectoryName"/>（"过程物"），字面量只在这里写一次；
    /// ② 过程物的落点只有 <c>D\过程物\…</c> 一处（不变量 3：不得东放西放、不得和内容物混在同一层）。
    ///
    /// <see cref="Plan"/> 是**纯规划**：只算"从哪搬到哪"，不建目录、不移动、不删除 ——
    /// 真正的搬运由调用方接线（失败回滚、跨盘 copy+delete 之类都属于执行层）。
    /// 两条不覆盖保证：
    /// · **本批内同层重名**自动加序号（<c>名字(1).ext</c>），后面的不会盖前面的；
    /// · **目标位置已有同名**（文件或目录）也一样加序号，绝不覆盖。
    /// 另外"源与目标同一个位置""目标落在源目录内部"这类会自己套自己的计划一律不做。
    /// </summary>
    public static class ProcessArtifactLayout
    {
        /// <summary>
        /// 过程物目录名。**全项目唯一来源** —— 任何其它地方要用这三个字，都必须引用这里。
        /// </summary>
        public const string ArtifactDirectoryName = "过程物";

        /// <summary>加序号时的最大尝试次数（与 SafePathHelper 里的自动改名同一量级）。</summary>
        private const int MaxRenameAttempts = 10000;

        /// <summary>
        /// 给定目标目录 → 过程物目录的完整路径（<c>D\过程物</c>）。
        /// 目标目录为空时返回空串（不抛异常：规划阶段拿不到目标目录是调用方的常见状态）。
        /// </summary>
        public static string ResolveArtifactDirectory(string? targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return string.Empty;
            }

            try
            {
                return Path.Combine(SafePathHelper.GetFullPathSafe(targetDirectory), ArtifactDirectoryName);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 规划"把这一批路径归置进过程物目录"。
        ///
        /// 相对路径保持原结构（§3.2："保持它们之间的相对结构"），每一段都走
        /// <see cref="FileNameHelper.SanitizeFileName"/>；相对路径本身先过
        /// <see cref="ArchivePathGuard.CheckEntry"/>（越界 <c>..</c>、绝对路径、盘符、UNC、
        /// 保留设备名、结尾点空格、控制字符、冒号一律判为不合法并跳过，而不是"洗干净了继续搬"）。
        /// </summary>
        public static ArtifactMovePlan Plan(ArtifactPlacementRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.TargetDirectory))
            {
                return new ArtifactMovePlan
                {
                    ArtifactDirectory = string.Empty,
                    Skipped = SkipAll(request, ArtifactSkipReason.TargetDirectoryMissing, "未指定目标目录，无法规划过程物归置"),
                    Message = "未指定目标目录，没有规划任何移动"
                };
            }

            string artifactDirectory = ResolveArtifactDirectory(request.TargetDirectory);

            if (string.IsNullOrWhiteSpace(artifactDirectory))
            {
                return new ArtifactMovePlan
                {
                    ArtifactDirectory = string.Empty,
                    Skipped = SkipAll(request, ArtifactSkipReason.TargetDirectoryMissing, "目标目录无法规范化，无法规划过程物归置"),
                    Message = "目标目录无法规范化，没有规划任何移动"
                };
            }

            IArtifactTargetProbe probe = request.TargetProbe ?? FileSystemArtifactTargetProbe.Instance;
            IReadOnlyList<ArtifactPlacementItem> items = request.Items ?? Array.Empty<ArtifactPlacementItem>();

            var moves = new List<ArtifactMove>();
            var skipped = new List<ArtifactSkip>();

            // 本批已经占掉的落点：同层重名靠它加序号（探测只能看到磁盘上已有的，看不到本批前面的计划）。
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArtifactPlacementItem? item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.SourcePath))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = item?.SourcePath ?? string.Empty,
                        Reason = ArtifactSkipReason.EmptySource,
                        Message = "源路径为空，无法规划移动"
                    });

                    continue;
                }

                string sourceFull = SafePathHelper.GetFullPathSafe(item.SourcePath);

                if (!seenSources.Add(sourceFull))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = sourceFull,
                        Reason = ArtifactSkipReason.DuplicateSource,
                        Message = "同一个源路径在本批里出现了两次，只规划第一次"
                    });

                    continue;
                }

                if (IsSameOrInside(sourceFull, artifactDirectory))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = sourceFull,
                        Reason = ArtifactSkipReason.SourceInsideArtifactDirectory,
                        Message = $"源已经在过程物目录里（{artifactDirectory}），不需要再归置"
                    });

                    continue;
                }

                string relativePath = ResolveRelativePath(item, request.BaseDirectory, sourceFull);

                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = sourceFull,
                        Reason = ArtifactSkipReason.InvalidRelativePath,
                        Message = "无法确定相对路径，已跳过（可显式给出 RelativePath）"
                    });

                    continue;
                }

                UnsafeArchiveEntry? risk = ArchivePathGuard.CheckEntry(relativePath);

                if (risk != null)
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = sourceFull,
                        Reason = ArtifactSkipReason.InvalidRelativePath,
                        Message = $"相对路径不合法，已跳过：{risk.Reason}（相对路径：{relativePath}）"
                    });

                    continue;
                }

                string[] segments = SplitSegments(relativePath);

                if (segments.Length == 0)
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = sourceFull,
                        Reason = ArtifactSkipReason.InvalidRelativePath,
                        Message = $"相对路径没有有效目录段，已跳过（相对路径：{relativePath}）"
                    });

                    continue;
                }

                string candidate = Path.Combine(new[] { artifactDirectory }.Concat(segments).ToArray());

                if (SafePathHelper.PathEquals(sourceFull, candidate))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = sourceFull,
                        Reason = ArtifactSkipReason.SourceEqualsTarget,
                        Message = "源与目标同一个位置，不需要移动"
                    });

                    continue;
                }

                if (IsSameOrInside(candidate, sourceFull))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = sourceFull,
                        Reason = ArtifactSkipReason.TargetInsideSource,
                        Message = $"目标落在源目录内部（{candidate}），会自己套自己，已跳过"
                    });

                    continue;
                }

                bool renamed = false;

                if (reserved.Contains(candidate) || probe.Exists(candidate))
                {
                    string unique = MakeUniqueTarget(candidate, probe.IsDirectory(sourceFull) == true, reserved, probe);
                    renamed = !string.Equals(unique, candidate, StringComparison.OrdinalIgnoreCase);
                    candidate = unique;
                }

                reserved.Add(candidate);

                moves.Add(new ArtifactMove
                {
                    SourcePath = sourceFull,
                    TargetPath = candidate,
                    RelativePath = string.Join(Path.DirectorySeparatorChar, segments),
                    Reason = item.Reason,
                    Renamed = renamed
                });
            }

            return new ArtifactMovePlan
            {
                ArtifactDirectory = artifactDirectory,
                Moves = moves,
                Skipped = skipped,
                Message = BuildMessage(artifactDirectory, moves, skipped)
            };
        }

        /// <summary>把待归置项里的相对路径定下来：显式给的 > 按基准推算的 > 文件名。</summary>
        private static string ResolveRelativePath(
            ArtifactPlacementItem item,
            string baseDirectory,
            string sourceFull)
        {
            if (!string.IsNullOrWhiteSpace(item.RelativePath))
            {
                return item.RelativePath.Trim();
            }

            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                try
                {
                    /*
                     * 不在基准之下时 GetRelativePath 会返回以 .. 开头（跨盘时干脆返回绝对路径），
                     * 两种都会被 CheckEntry 判掉 —— 这里**不**替调用方把它降级成"只用文件名"：
                     * 那会把一个本该被发现的调用方 bug 静默变成"文件被搬到了别处"。
                     */
                    return Path.GetRelativePath(SafePathHelper.GetFullPathSafe(baseDirectory), sourceFull);
                }
                catch
                {
                    // 推算失败就退到文件名，属于"尽力而为"的最后一档。
                }
            }

            try
            {
                return Path.GetFileName(sourceFull);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>拆成清洗过的目录段；<c>.</c> 与空段丢掉。</summary>
        private static string[] SplitSegments(string relativePath)
        {
            var segments = new List<string>();

            foreach (string raw in relativePath.Replace('\\', '/').Split('/'))
            {
                if (raw.Length == 0 || raw == ".")
                {
                    continue;
                }

                segments.Add(FileNameHelper.SanitizeFileName(raw));
            }

            return segments.ToArray();
        }

        /// <summary>给目标位置找一个不冲突的名字：<c>名字(1).ext</c>、<c>名字(2).ext</c>…</summary>
        private static string MakeUniqueTarget(
            string candidate,
            bool isDirectory,
            HashSet<string> reserved,
            IArtifactTargetProbe probe)
        {
            string? directory = Path.GetDirectoryName(candidate);
            string name = Path.GetFileName(candidate);

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
            {
                return candidate;
            }

            /*
             * 目录名的点在 Windows 里是名字的一部分（"v1.2"），不能当成扩展名拆掉，
             * 所以按源的类型决定拆不拆扩展名（源类型未知时按文件处理，观感与 SafePathHelper 一致）。
             */
            string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
            string extension = isDirectory ? string.Empty : Path.GetExtension(name);

            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = name;
                extension = string.Empty;
            }

            for (int index = 1; index < MaxRenameAttempts; index++)
            {
                string proposal = Path.Combine(directory, $"{stem}({index}){extension}");

                if (!reserved.Contains(proposal) && !probe.Exists(proposal))
                {
                    return proposal;
                }
            }

            // 一万个都撞上了：给个几乎不可能重复的名字，但仍然不覆盖（宁可名字难看，不可丢文件）。
            return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{extension}");
        }

        private static List<ArtifactSkip> SkipAll(
            ArtifactPlacementRequest? request,
            ArtifactSkipReason reason,
            string message)
        {
            var skipped = new List<ArtifactSkip>();

            foreach (ArtifactPlacementItem? item in request?.Items ?? Array.Empty<ArtifactPlacementItem>())
            {
                skipped.Add(new ArtifactSkip
                {
                    SourcePath = item?.SourcePath ?? string.Empty,
                    Reason = reason,
                    Message = message
                });
            }

            return skipped;
        }

        private static bool IsSameOrInside(string? candidate, string? parent)
        {
            string fullCandidate = NormalizeForCompare(candidate);
            string fullParent = NormalizeForCompare(parent);

            if (fullCandidate.Length == 0 || fullParent.Length == 0)
            {
                return false;
            }

            if (string.Equals(fullCandidate, fullParent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullCandidate.StartsWith(
                fullParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeForCompare(string? path)
        {
            return SafePathHelper.GetFullPathSafe(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string BuildMessage(
            string artifactDirectory,
            List<ArtifactMove> moves,
            List<ArtifactSkip> skipped)
        {
            var parts = new List<string>
            {
                moves.Count > 0
                    ? $"计划把 {moves.Count} 项过程物归置到 {artifactDirectory}"
                    : "没有可归置的过程物"
            };

            int renamedCount = moves.Count(move => move.Renamed);

            if (renamedCount > 0)
            {
                parts.Add($"其中 {renamedCount} 项因重名加了序号（不覆盖）");
            }

            if (skipped.Count > 0)
            {
                parts.Add($"{skipped.Count} 项被跳过（原因见跳过清单）");
            }

            return string.Join("；", parts);
        }
    }
}
