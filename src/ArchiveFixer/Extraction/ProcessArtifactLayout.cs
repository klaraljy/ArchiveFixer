using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「其余物」的**唯一命名来源与唯一归置规则来源**
    /// （docs/输出与整理模型.md §3.2、需求变更 R6、决策 D-8/D-10）。
    ///
    /// 三条硬要求，本类之外不许再出现：
    /// ① 其余物目录名固定叫 <see cref="ArtifactDirectoryName"/>（"其余物"），字面量只在这里写一次；
    /// ② 其余物的落点只有 <c>D\其余物\…</c> 一处（不变量 3：不得东放西放、不得和内容物混在同一层）；
    ///
    /// <para>
    /// 2026-09-22 用户拍板：这层目录由「过程物」改名「其余物」，**并且源包也移进来**。
    /// 改名理由很直白：源包进来之后，"过程物"这个词就不准确了 —— 这一层装的是"除内容物之外剩下的东西"。
    /// 源包本身由协调器在定稿 + 校验通过之后单独搬（决策 D-9/D-11/D-12，见 <see cref="SourcePackageMover"/>），
    /// **不是**过程物规划的一部分：<see cref="ResultFinalizer"/> 只负责"别把源包当成内容物"。
    /// </para>
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
        /// 其余物目录名。**全项目唯一来源** —— 任何其它地方要用这三个字，都必须引用这里。
        /// </summary>
        public const string ArtifactDirectoryName = "其余物";

        /// <summary>加序号时的最大尝试次数（与 SafePathHelper 里的自动改名同一量级）。</summary>
        private const int MaxRenameAttempts = 10000;

        /// <summary>
        /// 这个路径是不是落在"**程序自己会整份删掉**"的地方 —— 其余物（含旧名过程物）或工作区
        /// （<c>.ArchiveFixer.work</c>）。
        ///
        /// <para>为什么要这个判据（用户 2026-10-01 真机 `giu910`）：他把上一轮被收进其余物的源包又跑了一遍，
        /// 于是"导出日志"的保存对话框默认开在**其余物**里，那份 txt 就落在了一个**下一批成功就会被整份删掉**的地方。
        /// 判据只有一个出口（这里），导出的起始目录与"记住上次导出到哪儿"都读它。</para>
        /// </summary>
        public static bool IsInsideDeletableProcessFolders(string? fullPath)
        {
            string path = fullPath ?? string.Empty;

            return path.Contains(ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase)

                || path.Contains(Detection.VolumeContentInference.WorkDirectoryName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 这个名字算不算"其余物目录"：**新旧两个名字都算**（决策 D-8）。
        /// 传名字或完整路径都行（只比较最后一段）—— 调用方手上往往是 <c>D\其余物</c> 这种完整路径。
        /// </summary>
        public static bool IsArtifactDirectoryName(string? nameOrPath)
        {
            string name = LastSegment(nameOrPath);

            return string.Equals(name, ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 其余物目录该叫什么：正常就是 <see cref="ArtifactDirectoryName"/>；
        /// 只有**内容物那一层正好也叫这个名字**时才让位成 <c>其余物(1)</c> ——
        /// 否则内容物与其余物会叠进同一个目录，用户再也分不清哪个是哪个。
        /// 新旧两个名字都算重名（老版本留下的内容物目录也可能叫 <c>过程物</c>）。
        /// </summary>
        /// <param name="contentLayerName">内容物那一层的名字（不是路径也可以，只取最后一段）。</param>
        public static string ResolveArtifactDirectoryName(string? contentLayerName)
        {
            return IsArtifactDirectoryName(contentLayerName)
                ? ArtifactDirectoryName + "(1)"
                : ArtifactDirectoryName;
        }

        /// <summary>
        /// 把“程序自己会整份删掉”的目录段从**落点根**里剥掉，返回用户真正看得见的那一层。
        ///
        /// <para>用户可以从 `其余物` 里再次导入源包；那一层只是输入位置，**产物不能继续写回
        /// `其余物` / `过程物` / 工作区**（下一批成功清理会把它们当过程物删掉）。</para>
        /// </summary>
        public static string LiftOutOfProcessArtifactDirectory(string? directory)
        {
            string full = SafePathHelper.GetFullPathSafe(directory);

            if (full.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                string root = Path.GetPathRoot(full) ?? string.Empty;
                string[] segments = full[root.Length..]
                    .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

                for (int index = segments.Length - 1; index >= 0; index--)
                {
                    if (!string.Equals(segments[index], ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase)

                        && !string.Equals(segments[index], Detection.VolumeContentInference.WorkDirectoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isWorkspace =
                        string.Equals(segments[index], Detection.VolumeContentInference.WorkDirectoryName, StringComparison.OrdinalIgnoreCase);

                    if (isWorkspace)
                    {
                        return index == 0
                            ? root
                            : Path.Combine(root, Path.Combine(segments[..index]));
                    }

                    if (index == 0)
                    {
                        return segments.Length == 1
                            ? root
                            : Path.Combine(root, Path.Combine(segments[(index + 1)..]));
                    }

                    string[] visible = segments.Take(index)
                        .Concat(segments.Skip(index + 1))
                        .ToArray();

                    return Path.Combine(root, Path.Combine(visible));
                }
            }
            catch
            {
                // 判不出就原样返回：宁可保持旧落点，也不静默换到别处。
            }

            return full;
        }

        /// <summary>
        /// 给定目标目录 → 其余物目录的完整路径（<c>parent(D)\其余物</c>）。
        /// 目标目录为空时返回空串（不抛异常：规划阶段拿不到目标目录是调用方的常见状态）。
        ///
        /// ⚠ 这是"集中一处"的那一档；**多个包共用同一个目标目录**时要走三参数重载，
        /// 否则几十个包的过程物会在同一个 <c>其余物\</c> 里互相撞名（决策 D-10 / D-2）。
        /// </summary>
        public static string ResolveArtifactDirectory(string? targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return string.Empty;
            }

            try
            {
                string root = SafePathHelper.GetFullPathSafe(targetDirectory);

                /*
                 * 用户 2026-11-02 定案：其余物和**内容物文件夹同级**，不是塞进内容物里面。
                 * 例：内容物 111\1111 → 其余物 111\其余物；内容物 111\1111\222 → 其余物 111\1111\其余物。
                 * ⛔ 根层原包不产生其余物（原包不移动），所以不会出现“原包旁边凭空多一个其余物”。
                 */
                string parent = Path.GetDirectoryName(root) ?? root;

                return Path.Combine(parent, ResolveArtifactDirectoryName(root));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 其余物目录的完整路径（**决策 D-10 的唯一实现处**）。
        ///
        /// <list type="table">
        /// <item><description>包本来就有自己目录（<c>PerArchiveSubfolder</c> / <c>CustomRootPerArchive</c>）→ <c>D\其余物\</c>，例：<c>111\222\其余物\</c></description></item>
        /// <item><description>多个包共用根（"添加文件夹 + 指定位置"那一档）→ <c>D\其余物\包基名\</c>，例：<c>BBB\222\其余物\222\</c></description></item>
        /// </list>
        ///
        /// <para>
        /// 为什么要分这两档：共用根时一个目录里有几十上百个包，其余物再不按包名分一层，
        /// 分卷和过程物就会互相撞名、也分不清是谁的（决策 D-2 的老理由）；
        /// 而包本来就有自己目录时再按包名分一层，就是用户最反感的"凭空多弄一个文件夹"（决策 D-10）。
        /// </para>
        /// </summary>
        /// <param name="contentRoot">内容物根（= 落点公式算出来的 destDir）。</param>
        /// <param name="archiveBaseName">包基名（分卷组取整个组，例 <c>222.7z.001</c> → <c>222</c>）。</param>
        /// <param name="sharedRoot">是不是"多个包共用同一个内容物根"的模式。</param>
        public static string ResolveArtifactDirectory(
            string? contentRoot,
            string? archiveBaseName,
            bool sharedRoot)
        {
            string directory = ResolveArtifactDirectory(contentRoot);

            if (directory.Length == 0 || !sharedRoot)
            {
                return directory;
            }

            // 没有包基名就没法隔离：退回"集中一处"。调用方应当补一条提醒（见 ResultFinalizer）。
            return AppendArchiveBaseName(directory, archiveBaseName);
        }

        /// <summary>
        /// 其余物目录的完整路径，**目录名由调用方指定**（决策 D-10）。
        ///
        /// 只有 <see cref="ResultFinalizer"/> 需要这一档：它知道"内容物那一层"最终叫什么
        /// （可能是套出来的那一层文件夹），能给出比"目标目录自己的名字"更准的重名判断，
        /// 于是由它算好名字（<see cref="ResolveArtifactDirectoryName"/>）再传进来。
        /// 其余调用方一律走三参数重载，别自己拼这个路径。
        /// </summary>
        public static string ResolveArtifactDirectoryWithName(
            string? contentRoot,
            string? artifactDirectoryName,
            string? archiveBaseName,
            bool sharedRoot)
        {
            if (string.IsNullOrWhiteSpace(contentRoot))
            {
                return string.Empty;
            }

            string name = string.IsNullOrWhiteSpace(artifactDirectoryName)
                ? ArtifactDirectoryName
                : artifactDirectoryName!;

            try
            {
                string contentRootFull = SafePathHelper.GetFullPathSafe(contentRoot);
                string parent = Path.GetDirectoryName(contentRootFull) ?? contentRootFull;
                string directory = Path.Combine(parent, name);

                if (!sharedRoot)
                {
                    return directory;
                }

                return AppendArchiveBaseName(directory, archiveBaseName);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 在其余物目录下再按包基名分一层（共享根模式，决策 D-10）。
        ///
        /// ⚠ 空 / 只有空白的基名**不加这一层**：<see cref="FileNameHelper.SanitizeFileName"/> 对空名字
        /// 返回的是占位符"未命名"，直接拿它当目录名会造出 <c>其余物\未命名\</c> 这种谁也看不懂的目录。
        /// </summary>
        private static string AppendArchiveBaseName(string directory, string? archiveBaseName)
        {
            if (string.IsNullOrWhiteSpace(archiveBaseName))
            {
                return directory;
            }

            string safeBaseName = FileNameHelper.SanitizeFileName(archiveBaseName);

            return string.IsNullOrWhiteSpace(safeBaseName)
                ? directory
                : Path.Combine(directory, safeBaseName);
        }

        /// <summary>
        /// 目标目录下**已经存在**的其余物目录（新名优先，其次旧名）；一个都没有时返回空列表。
        ///
        /// 用途是删除功能（决策 D-8）：老版本在用户目录里留下了 <c>过程物\</c>，
        /// 升级之后它既不会被新建、也不该永远清不掉 —— 新名与旧名同时存在时两个都返回。
        /// </summary>
        public static IReadOnlyList<string> FindExistingArtifactDirectories(string? targetDirectory)
        {
            var found = new List<string>();

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return found;
            }

            foreach (string name in new[] { ArtifactDirectoryName })
            {
                try
                {
                    string candidate = Path.Combine(SafePathHelper.GetFullPathSafe(targetDirectory), name);

                    if (SafePathHelper.DirectoryExists(candidate))
                    {
                        found.Add(candidate);
                    }
                }
                catch
                {
                    // 单个名字算不出来不影响另一个。
                }
            }

            return found;
        }

        /// <summary>路径的最后一段（<c>D\其余物</c> → <c>其余物</c>；空/只有分隔符 → 空串）。</summary>
        private static string LastSegment(string? nameOrPath)
        {
            string value = (nameOrPath ?? string.Empty).Trim().TrimEnd('\\', '/');

            if (value.Length == 0)
            {
                return string.Empty;
            }

            int separator = value.LastIndexOfAny(new[] { '\\', '/' });

            return separator >= 0 ? value[(separator + 1)..] : value;
        }

        /// <summary>
        /// 规划"把这一批路径归置进其余物目录"。
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
                    Skipped = SkipAll(request, ArtifactSkipReason.TargetDirectoryMissing, "未指定目标目录，无法规划其余物归置"),
                    Message = "未指定目标目录，没有规划任何移动"
                };
            }

            string artifactDirectory = ResolveArtifactDirectory(
                request.TargetDirectory,
                request.ArchiveBaseName,
                request.SharedRoot);

            if (string.IsNullOrWhiteSpace(artifactDirectory))
            {
                return new ArtifactMovePlan
                {
                    ArtifactDirectory = string.Empty,
                    Skipped = SkipAll(request, ArtifactSkipReason.TargetDirectoryMissing, "目标目录无法规范化，无法规划其余物归置"),
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

                if (IsInsideArtifactDirectory(sourceFull, request.TargetDirectory))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = sourceFull,
                        Reason = ArtifactSkipReason.SourceInsideArtifactDirectory,
                        Message = $"源已经在其余物目录里（{artifactDirectory}），不需要再归置"
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
        internal static string MakeUniqueTarget(
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

        /// <summary>
        /// 这个路径是不是已经在"某个名字的其余物目录"里了 —— <b>新旧两个名字都查</b>
        /// （决策 D-8：老版本留下的 <c>过程物\</c> 里躺着的东西，同样不该再被归置一遍）。
        /// </summary>
        internal static bool IsInsideArtifactDirectory(string? path, string? targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return false;
            }

            foreach (string name in new[] { ArtifactDirectoryName })
            {
                try
                {
                    string targetRoot = SafePathHelper.GetFullPathSafe(targetDirectory);
                    string parent = Path.GetDirectoryName(targetRoot) ?? targetRoot;
                    string directory = Path.Combine(parent, name);

                    if (IsSameOrInside(path, directory))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 单个名字算不出来不影响另一个。
                }
            }

            return false;
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
                    ? $"计划把 {moves.Count} 项其余物归置到 {artifactDirectory}"
                    : "没有可归置的其余物"
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
