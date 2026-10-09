using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

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

        /// <summary>源本来就在其余物目录里（已经归置过，不需要再搬）。</summary>
        SourceInsideArtifactDirectory,

        /// <summary>
        /// 源在**工作区那棵树**里（程序自己的中间产物，不是用户的源包）—— 用户 2026-09-30：
        /// 工作区默认建在目标目录里面，任何"把东西搬出目标目录"的动作都必须排除它。
        /// </summary>
        SourceInsideWorkspace,

        /// <summary>相对路径不合法（越界 <c>..</c> / 绝对路径 / 盘符 / UNC / 保留名 / 结尾点空格等）。</summary>
        InvalidRelativePath,

        /// <summary>源与目标同一个位置（搬到原地）。</summary>
        SourceEqualsTarget,

        /// <summary>目标落在源目录内部（会自己套自己）。</summary>
        TargetInsideSource,

        /// <summary>源文件已经不存在（用户在批次中途删了 / 上一轮已经搬走）。</summary>
        SourceMissing,

        /// <summary>
        /// 源是**我们自己产出的内容物**（这一单是续解出来的**内层包**）—— 它不是用户的**原包**，
        /// ⛔ 不参与源包处理、一个字节都不搬（用户口令：「**原包就是原包**」）。
        ///
        /// <para>真机 `EEEE` 2026-10-09 19:14 那一趟：`111.rar` 解出来的入口包 `111.zip` 落在
        /// `…\111\111\111.zip`，下一轮它自己成了一单 ⇒ 这里把**我们刚产出的入口包**当"源包"搬进其余物，
        /// 批末「彻底删除」再把它删掉 ⇒ 组入口包没了、第一大步永远闭不了环。</para>
        /// </summary>
        SourceIsOurOwnContent,

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

        /// <summary>为什么算其余物（写报告 / 日志用，例："内层归档"）。</summary>
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
        /// <summary>目标目录 <c>D</c>（内容物目录）；其余物目录 = <c>D\其余物</c>（决策 D-10 见 <see cref="ProcessArtifactLayout.ResolveArtifactDirectory(string?, string?, bool)"/>）。</summary>
        public string TargetDirectory { get; init; } = string.Empty;

        /// <summary>相对路径的基准目录（例：本层产物目录）。留空时按文件名归置。</summary>
        public string BaseDirectory { get; init; } = string.Empty;

        /// <summary>
        /// 归档基名（决策 D-10）：**多个包共用同一个目标目录**时，其余物下按它再分一层。
        /// 留空 = 不分（退回"集中一处"，与旧行为一致）。
        /// </summary>
        public string ArchiveBaseName { get; init; } = string.Empty;

        /// <summary>
        /// 目标目录是不是"多个包共用的根"（`OutputPlacementResult.SharesDestinationWithOtherPackages`：
        /// 默认 false：包本来就有自己目录的模式**不再多套一层**（决策 D-10）。
        /// </summary>
        public bool SharedRoot { get; init; }

        public IReadOnlyList<ArtifactPlacementItem> Items { get; init; } = Array.Empty<ArtifactPlacementItem>();

        /// <summary>目标占用探测；不传时按真实文件系统判断（"目标已存在同名就不覆盖"靠它）。</summary>
        public IArtifactTargetProbe? TargetProbe { get; init; }
    }

    /// <summary>一条移动计划：从哪搬到哪。</summary>
    public sealed class ArtifactMove
    {
        public string SourcePath { get; init; } = string.Empty;

        public string TargetPath { get; init; } = string.Empty;

        /// <summary>落点相对其余物目录的相对路径。</summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>为什么算其余物。</summary>
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
        /// <summary>其余物目录的完整路径（<c>D\其余物</c>）；目标目录不合法时为空。</summary>
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
    /// 「其余物」的**唯一命名来源与唯一归置规则来源**
    /// （docs/输出与整理模型.md §3.2、需求变更 R6、决策 D-8/D-10）。
    ///
    /// 三条硬要求，本类之外不许再出现：
    /// ① 其余物目录名固定叫 <see cref="ArtifactDirectoryName"/>（"其余物"），字面量只在这里写一次；
    /// ② 其余物的落点只有 <c>D\其余物\…</c> 一处（不变量 3：不得东放西放、不得和内容物混在同一层）；
    /// ③ 旧名 <see cref="LegacyArtifactDirectoryName"/>（"过程物"）**只用于识别**，绝不用于新建 ——
    ///    老版本已经在用户目录里留下了 <c>过程物\</c>，删除功能必须还能清掉它们（决策 D-8）。
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

        /// <summary>
        /// 旧目录名（2026-09-22 之前叫「过程物」）。**只用于识别**：新建一律用
        /// <see cref="ArtifactDirectoryName"/>；<see cref="FindExistingArtifactDirectories"/> 与
        /// <see cref="IsArtifactDirectoryName"/> 靠它清掉/认出老版本留下的目录（决策 D-8）。
        /// </summary>
        public const string LegacyArtifactDirectoryName = "过程物";

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
                || path.Contains(LegacyArtifactDirectoryName, StringComparison.OrdinalIgnoreCase)
                || path.Contains(Detection.VolumeContentInference.WorkDirectoryName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 这个名字算不算"其余物目录"：**新旧两个名字都算**（决策 D-8）。
        /// 传名字或完整路径都行（只比较最后一段）—— 调用方手上往往是 <c>D\其余物</c> 这种完整路径。
        /// </summary>
        public static bool IsArtifactDirectoryName(string? nameOrPath)
        {
            string name = LastSegment(nameOrPath);

            return string.Equals(name, ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, LegacyArtifactDirectoryName, StringComparison.OrdinalIgnoreCase);
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
        /// 给定目标目录 → 其余物目录的完整路径（<c>D\其余物</c>）。
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

                return Path.Combine(root, ResolveArtifactDirectoryName(root));
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
                string directory = Path.Combine(SafePathHelper.GetFullPathSafe(contentRoot), name);

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
        /// 给定一个其余物目录，返回**旧名对应的那一个**（决策 D-8：老版本留下的 <c>过程物\</c>）。
        ///
        /// 只换最后一段里出现的 <c>其余物</c>（共享根模式下是 <c>…\其余物\222</c> 的中间那一段，
        /// 所以按"最后一段等于其余物的那一段"去找）。认不出来时返回空串。
        /// </summary>
        public static string ResolveLegacyArtifactDirectory(string? artifactDirectory)
        {
            if (string.IsNullOrWhiteSpace(artifactDirectory))
            {
                return string.Empty;
            }

            try
            {
                string full = SafePathHelper.GetFullPathSafe(artifactDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string[] segments = full.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.None);

                for (int i = segments.Length - 1; i >= 0; i--)
                {
                    if (string.Equals(segments[i], ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        segments[i] = LegacyArtifactDirectoryName;
                        return string.Join(Path.DirectorySeparatorChar, segments);
                    }
                }

                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
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

            foreach (string name in new[] { ArtifactDirectoryName, LegacyArtifactDirectoryName })
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

            foreach (string name in new[] { ArtifactDirectoryName, LegacyArtifactDirectoryName })
            {
                try
                {
                    string directory = Path.Combine(SafePathHelper.GetFullPathSafe(targetDirectory), name);

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

    /// <summary>一条源包搬运计划（纯数据：不代表磁盘上已经发生任何事）。</summary>
    public sealed class SourcePackageMove
    {
        public string SourcePath { get; init; } = string.Empty;

        public string TargetPath { get; init; } = string.Empty;

        /// <summary>源文件字节数（写日志 / 报总大小用）。</summary>
        public long Size { get; init; }

        /// <summary>目标同名，已加序号（<b>绝不覆盖</b>）。</summary>
        public bool Renamed { get; init; }
    }

    /// <summary>源包搬运计划。<b>只是计划</b>。</summary>
    public sealed class SourcePackageMovePlan
    {
        /// <summary>其余物目录（源包搬进去的目标根）；算不出来时为空。</summary>
        public string ArtifactDirectory { get; init; } = string.Empty;

        public IReadOnlyList<SourcePackageMove> Moves { get; init; } = Array.Empty<SourcePackageMove>();

        public IReadOnlyList<ArtifactSkip> Skipped { get; init; } = Array.Empty<ArtifactSkip>();

        /// <summary>整组源包的总字节数。</summary>
        public long TotalSize { get; init; }

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>源包搬运的结论。</summary>
    public sealed class SourcePackageMoveResult
    {
        /// <summary>是否真的动过手（没有可搬的、被跳过时为 false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        public int MovedCount { get; init; }

        /// <summary>搬失败的个数（**源包原地不动**）。</summary>
        public int FailedCount { get; init; }

        public int SkippedCount { get; init; }

        /// <summary>其中几个走了"跨盘：复制成功后再删原件"这条路。</summary>
        public int CrossVolumeCount { get; init; }

        /// <summary>真的搬走的字节数。</summary>
        public long MovedBytes { get; init; }

        /// <summary>成功的搬运（旧 → 新）。任务对象要靠它更新 CurrentPath 与分卷清单。</summary>
        public IReadOnlyList<SourcePackageMove> Moved { get; init; } = Array.Empty<SourcePackageMove>();

        public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

        /// <summary>逐条搬运日志（含从哪到哪 + 总大小），调用方原样写进界面/文件日志。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 源包搬运要碰的那几件文件系统操作。
    ///
    /// <para>
    /// 抽出来只为一件事：**跨盘那条路必须能被验证**。真机上很难临时造出第二个卷，
    /// 而"跨盘 = 复制成功后再删原件、复制失败源包一个字节都不动"是决策 D-12 的红线，
    /// 不能只靠肉眼读代码（单测注入假实现就能把这条路走一遍，见 SourcePackageMoverTests）。
    /// </para>
    /// </summary>
    public interface ISourceMoveFileSystem
    {
        bool FileExists(string path);

        bool DirectoryExists(string path);

        /// <summary>建目录；建不出来返回 false（调用方据此报失败，绝不当成"搬成功了"）。</summary>
        bool CreateDirectory(string path);

        long GetFileSize(string path);

        /// <summary>两个路径是不是同一个卷（同卷才能用原子改名）。</summary>
        bool IsSameVolume(string sourcePath, string targetPath);

        /// <summary>同卷：原子改名。<b>目标已存在必须抛异常，绝不覆盖</b>。</summary>
        void MoveFile(string source, string target);

        /// <summary>跨卷：复制。<b>目标已存在必须抛异常，绝不覆盖</b>。</summary>
        void CopyFile(string source, string target);

        void DeleteFile(string path);
    }

    /// <summary>真实文件系统实现。</summary>
    public sealed class FileSystemSourceMoveFileSystem : ISourceMoveFileSystem
    {
        public static FileSystemSourceMoveFileSystem Instance { get; } = new();

        public bool FileExists(string path) => SafePathHelper.FileExists(path);

        public bool DirectoryExists(string path) => SafePathHelper.DirectoryExists(path);

        public bool CreateDirectory(string path) => SafePathHelper.EnsureDirectoryExists(path);

        public long GetFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                // 量不出大小只影响日志里的数字，不影响搬不搬。
                return 0;
            }
        }

        public bool IsSameVolume(string sourcePath, string targetPath)
        {
            try
            {
                string? sourceRoot = Path.GetPathRoot(SafePathHelper.GetFullPathSafe(sourcePath));
                string? targetRoot = Path.GetPathRoot(SafePathHelper.GetFullPathSafe(targetPath));

                if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(targetRoot))
                {
                    // 判不出来时**保守地当成跨盘**：那条件更严（先复制成功再删原件），
                    // 代价只是多拷一次，而判错成同盘会走 rename —— 跨盘 rename 必然失败。
                    return false;
                }

                return string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // 两参数版本：目标已存在直接抛 IOException，**不会覆盖**（AGENTS.md §6 第 3 条）。
        public void MoveFile(string source, string target) => File.Move(source, target);

        public void CopyFile(string source, string target) => File.Copy(source, target, overwrite: false);

        public void DeleteFile(string path) => File.Delete(path);
    }



    /// <summary>
    /// 把**本任务的源包（整组）**搬进其余物目录（决策 D-9/D-11/D-12，2026-09-22 用户拍板）。
    ///
    /// <para>
    /// 为什么源包也算「其余物」：用户在"其余物"里一次删掉就干净了 ——
    /// 内容物留在原地，源包和过程物一起进同一层，整理完只需删一个目录。
    /// </para>
    /// <para>
    /// 四条硬约束（都在这里落地，调用方不许绕过）：
    /// ① **整组一起移**：分卷组取 <see cref="ArchiveTask.VolumePaths"/> 全卷，单文件任务取它自己；
    ///    清单只来自任务自身，**不扫目录**（与 <c>SourceCleanupService</c> 同一口径）；
    /// ② **绝不覆盖**：目标同名加 <c>(1)(2)</c>；
    /// ③ **跨盘 = 先复制成功、再删原件**：复制失败源包一个字节都不动，绝不"先删后移"；
    /// ④ **搬不动就如实报失败**（只读 / 被占用），由调用方把任务标成"部分完成"——
    ///    内容物已经好了，这件事不该影响内容物的结论。
    /// </para>
    /// <para>
    /// 本类**不判断该不该搬**：那由 <c>ExtractionCoordinator</c> 按"内容物已定稿 + 校验通过 + 未取消 + 一键处理路径"决定（D-11）。
    /// </para>
    /// </summary>
    public sealed class SourcePackageMover
    {
        private readonly ISourceMoveFileSystem _fileSystem;

        public SourcePackageMover(ISourceMoveFileSystem? fileSystem = null)
        {
            _fileSystem = fileSystem ?? FileSystemSourceMoveFileSystem.Instance;
        }

        /// <summary>
        /// 本任务的源包清单：分卷组 = 整组各卷；单文件任务 = 它自己。
        ///
        /// 与 <c>SourceCleanupService.BuildTargetList</c> 同一口径（那边是删、这边是移，清单必须一致）：
        /// 分卷组只认 <see cref="ArchiveTask.VolumePaths"/>，清单为空时**不回退**到 CurrentPath ——
        /// 那说明分组信息不完整，少搬一卷比"整组散在两个目录里"更糟，所以宁可什么都不搬。
        ///
        /// <para>
        /// ⚠ 2026-10-01 补一条（`AaaReplayPipelineTests` 逮到）：清单**非空、却不含任务自己的文件**时，
        /// 这一单按单文件办。现场形状：夹具里 `222.zscip`（真 7z）被「修正后缀」改成 `222.7z`，
        /// 认组时按名字把旁边的 `222.z删除01` 当成同一族的第 2 卷（基名都是 `222`）⇒
        /// `VolumePaths` 里只有兄弟卷、**没有任务自己**。老口径在"清单不完整"这一档什么都不搬，
        /// 结果：成功任务报「已把 1 个源包移入其余物」，而**它自己那份 `222.7z` 一个字节都没动**，
        /// 留在源目录里被下一轮又扫一遍（真机上就是"同一份东西反复解"）。
        /// 判据只用事实：清单非空 + `CurrentPath` 在盘上 + 它不在清单里 ⇒ 这份清单代表不了这一单。
        /// </para>
        /// <para>
        /// ⛔ 清单**为空**那一档一个字不改：分组信息根本没有时一律不搬（老红线，
        /// 用例 `计划_分卷清单不完整时什么都不搬` 钉着）。
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> ResolveSourceGroup(ArchiveTask? task) =>
            ResolveSourceGroup(task, out _);

        /// <summary>
        /// 同上，另外回答"账上不一致"这一档：**分卷清单非空、却不含任务自己**。
        ///
        /// <para>⛔ 这一档现在**什么都不搬**（用户 2026-10-02 改；老口径是"静默降级成只搬它自己一份"）：
        /// 那正是用户 2026-10-01 第四 / 第五报抱怨的形状 —— 一组分卷只搬走一份、其余留在源目录里
        /// 等他手工清（「**`222\222.z01` 依旧还在，你浪费了我两次操作**」）。清单不含自己 =
        /// **账上已经过期**（现实里就是"改了名没同步"那一类），按红线「判不出 ⇒ 什么都不做」，
        /// 整组留在原地、并让调用方把原因写进日志（⛔ 不再静默）。</para>
        /// </summary>
        public static IReadOnlyList<string> ResolveSourceGroup(ArchiveTask? task, out bool groupListInconsistent)
        {
            groupListInconsistent = false;

            var targets = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (task == null)
            {
                return targets;
            }

            void Add(string? path)
            {
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(path!))
                {
                    targets.Add(path!);
                }
            }

            if (!task.IsVolumeGroup)
            {
                // 本来就不是分卷组：只搬它自己（老口径，一个字没改）。
                Add(task.CurrentPath);
                return targets;
            }

            if (task.VolumePaths.Count == 0)
            {
                // 分组信息根本不完整（老红线）：一份都不搬。见 计划_分卷清单不完整时什么都不搬。
                return targets;
            }

            bool selfListed = !string.IsNullOrWhiteSpace(task.CurrentPath)
                && task.VolumePaths.Any(
                    path => string.Equals(path, task.CurrentPath, StringComparison.OrdinalIgnoreCase));

            if (!selfListed && File.Exists(task.CurrentPath))
            {
                // 清单非空、却不含自己，而自己确实还在盘上 ⇒ 账上过期：整组不搬（见上面那段说明）。
                groupListInconsistent = true;
                return targets;
            }

            /*
             * ⛔ **"这一单自己那一份"必须按盘上的真实名字加进来**（真机 2026-10-07 08:20 逮到）：
             * 起点被改写到入口包上之后 `CurrentPath` 指向别处、`OriginalPath` 还停在改名前的脏名上
             * ⇒ 盘上那一片（`…\111(4)\111.z03`）两条都不沾 ⇒ 清单里没有它 ⇒ 整组搬运搬不到它、
             * 源片永远留在用户目录里。同批里**没被改写起点**的那一单（`111.z02`）清单命中 ⇒ 搬走了
             * —— 这就是"同样两片、结局不同"的唯一变量。
             * ⛔ 这是"这一单自己那一份在盘上"的**唯一出口**（`SourcePackageMover.ResolveOwnFileOnDisk`）—— 协调器那边原来的两份同名实现已删。
             */
            Add(ResolveOwnFileOnDisk(task));

            foreach (string path in task.VolumePaths)
            {
                Add(path);
            }

            return targets;
        }

        /// <summary>
        /// 规划"把这一组源包搬进其余物目录"。纯规划：一个字节都不动。
        /// </summary>
        /// <param name="task">要处理的源包任务。</param>
        /// <param name="artifactDirectory">其余物目录（决策 D-10 算出来的那个）。</param>
        /// <param name="probe">目标占用探测；不传时查真实文件系统。</param>
        public SourcePackageMovePlan Plan(
            ArchiveTask? task,
            string? artifactDirectory,
            IArtifactTargetProbe? probe = null)
        {
            IArtifactTargetProbe targetProbe = probe ?? FileSystemArtifactTargetProbe.Instance;

            if (task == null)
            {
                return new SourcePackageMovePlan { Message = "任务为空，没有规划任何源包搬运" };
            }

            IReadOnlyList<string> sources = ResolveSourceGroup(task, out bool groupListInconsistent);

            if (sources.Count == 0)
            {
                return new SourcePackageMovePlan
                {
                    ArtifactDirectory = artifactDirectory ?? string.Empty,
                    Message = groupListInconsistent
                        ? "任务账上的分卷清单里没有它自己（清单可能过期，例如改名之后没同步）—— "
                          + "分卷组整组一律不搬，源包全部留在原地"
                        : "任务没有可搬运的源包路径（分卷清单可能不完整），源包一律不动"
                };
            }

            if (string.IsNullOrWhiteSpace(artifactDirectory))
            {
                return new SourcePackageMovePlan
                {
                    Skipped = SkipAll(sources, ArtifactSkipReason.TargetDirectoryMissing, "其余物目录算不出来，源包留在原地"),
                    Message = "其余物目录算不出来，没有规划任何源包搬运"
                };
            }

            string artifactRoot = artifactDirectory!;

            /*
             * ⛔ 2026-10-09 真机 `EEEE`（19:14 那一趟）：**续解出来的内层包，它的"源"是我们自己产出的内容物，
             * 不是用户的原包** ⇒ 整组不搬（用户口令：「**原包就是原包**」）。
             *
             * 现场（逐条有出处）：`111.rar` 解出来的组入口包 `111.zip` 落在 `…\111\111\111.zip`（行 154）；
             * 下一轮它自己成了两单（行 218/220「内层包，产物归入父任务输出目录」）⇒ `ResolveSourceGroup`
             * 把**我们刚产出的入口包**当成"源包"搬进其余物（行 287/289 两条 `源包移入其余物`，
             * 第三条还把 `111.z01` 一起搬走）⇒ 批末「其余物 = 彻底删除」全删（行 292
             * `彻底删除：111\其余物（5 项 / 545.1 MB）`）⇒ 组入口包没了、**第一大步永远闭不了环**。
             *
             * ⚠ **落在这里、而不是某个调用者的理由**：本方法就是"哪些算源包"的**唯一决策处**，
             * 而调用者有好几个（每单收尾 / 链尾补搬 / 手动档）—— 加在调用者上必然漏一个
             * （2026-10-09 我先加在 `ExtractionCoordinator` 的链尾循环里，真机复跑**完全没拦住**）。
             * 判据唯一出口 = `ArchiveTask.IsContinuationTask`（`ParentOutputDirectory` 非空即内层包）。
             * ⛔ 不影响正当场景：真正要按档处理的是**用户导入的那份原包**，它不是续解任务。
             */
            if (task.IsContinuationTask)
            {
                return new SourcePackageMovePlan
                {
                    ArtifactDirectory = artifactRoot,
                    Skipped = SkipAll(
                        sources,
                        ArtifactSkipReason.SourceIsOurOwnContent,
                        "这一单是续解出来的内层包：它的“源”是我们自己产出的内容物，不是用户的原包"),
                    Message = "续解出来的内层包不参与源包处理 —— 源包一个字节都不搬（我们自己产出的内容物不是原包）"
                };
            }

            var moves = new List<SourcePackageMove>();
            var skipped = new List<ArtifactSkip>();
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalSize = 0;

            foreach (string source in sources)
            {
                if (!_fileSystem.FileExists(source))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = source,
                        Reason = ArtifactSkipReason.SourceMissing,
                        Message = "源文件不存在（可能已被移动或删除），跳过"
                    });

                    continue;
                }

                if (IsAlreadyInsideRest(source, artifactRoot, out string restDirectory))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = source,
                        Reason = ArtifactSkipReason.SourceInsideArtifactDirectory,
                        Message = $"源已经在其余物目录里（{restDirectory}），不需要再搬"
                    });

                    continue;
                }

                /*
                 * ⛔ 源**落在工作区自己那棵树里** → 一个字节都不搬（用户 2026-09-30）。
                 *
                 * 工作区默认建在目标目录里面（<目标目录>\.ArchiveFixer.work），它和"其余物"在同一个父目录下。
                 * 续解任务的源包路径是从产物目录扫出来的 —— 万一哪条岔路把一个中间产物当成了"内层包"，
                 * 这一步就会把工作区里的东西搬进其余物、甚至进入可删清单：那是不可逆的事故。
                 * 判据唯一出口 WorkspaceTree（名字 + 当前生效的根两条），兜底落在"什么都不做"那一档。
                 */
                if (WorkspaceTree.ShouldSkipEntry(source, RecursiveExtractor.ConfiguredWorkspaceRoot))
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = source,
                        Reason = ArtifactSkipReason.SourceInsideWorkspace,
                        Message = "源在工作区目录里（那是程序自己的中间产物，不是用户的源包），一个字节都不搬"
                    });

                    continue;
                }

                string candidate = Path.Combine(artifactRoot, FileNameHelper.SanitizeFileName(Path.GetFileName(source)));
                bool renamed = false;

                if (reserved.Contains(candidate) || targetProbe.Exists(candidate))
                {
                    string unique = ProcessArtifactLayout.MakeUniqueTarget(
                        candidate,
                        false,
                        reserved,
                        targetProbe);

                    renamed = !string.Equals(unique, candidate, StringComparison.OrdinalIgnoreCase);
                    candidate = unique;
                }

                reserved.Add(candidate);

                long size = _fileSystem.GetFileSize(source);
                totalSize += size;

                moves.Add(new SourcePackageMove
                {
                    SourcePath = source,
                    TargetPath = candidate,
                    Size = size,
                    Renamed = renamed
                });
            }

            return new SourcePackageMovePlan
            {
                ArtifactDirectory = artifactRoot,
                Moves = moves,
                Skipped = skipped,
                TotalSize = totalSize,
                Message = BuildPlanMessage(artifactRoot, moves, skipped)
            };
        }

        /// <summary>
        /// 执行搬运。**只允许在后台线程上跑**（跨盘时是整包拷贝）。
        ///
        /// 取消：调用方应当在**开始之前**查一次令牌；这里一旦开始搬这一组就把它搬完 ——
        /// 半组卷散在两个目录里，比"晚几百毫秒才停下来"糟得多（决策 D-12"整组一起移"）。
        /// </summary>
        public SourcePackageMoveResult Execute(SourcePackageMovePlan? plan)
        {
            if (plan == null || plan.Moves.Count == 0)
            {
                return new SourcePackageMoveResult
                {
                    Attempted = false,
                    SkippedCount = plan?.Skipped.Count ?? 0,
                    Message = plan?.Message ?? "没有可搬运的源包"
                };
            }

            var moved = new List<SourcePackageMove>();
            var failures = new List<string>();
            var logLines = new List<string>();
            long movedBytes = 0;
            int crossVolume = 0;
            bool createdArtifactDirectory = false;

            foreach (SourcePackageMove move in plan.Moves)
            {
                string from = move.SourcePath;
                string to = move.TargetPath;

                try
                {
                    if (!_fileSystem.DirectoryExists(plan.ArtifactDirectory))
                    {
                        if (!_fileSystem.CreateDirectory(plan.ArtifactDirectory))
                        {
                            failures.Add($"{Path.GetFileName(from)}（其余物目录创建失败：{plan.ArtifactDirectory}）");
                            continue;
                        }

                        createdArtifactDirectory = true;
                    }

                    if (_fileSystem.FileExists(to) || _fileSystem.DirectoryExists(to))
                    {
                        /*
                         * 计划之后目标被别人占了（并发解压 / 用户手动放了东西进来）。
                         * 计划阶段已经避过一次，这里是最后一道：**重新让名字，绝不覆盖**。
                         */
                        to = MakeFallbackTarget(to);
                    }

                    bool sameVolume = _fileSystem.IsSameVolume(from, to);

                    if (sameVolume)
                    {
                        // 同卷：原子改名。目标已存在时底层直接抛，绝不会覆盖。
                        _fileSystem.MoveFile(from, to);
                    }
                    else
                    {
                        /*
                         * 跨盘：**先复制成功、再删原件**（决策 D-12）。
                         * 顺序绝不能反 —— "先删后移"在复制失败的瞬间就把用户的源包弄丢了。
                         */
                        _fileSystem.CopyFile(from, to);
                        crossVolume++;

                        try
                        {
                            _fileSystem.DeleteFile(from);
                        }
                        catch (Exception deleteError)
                        {
                            // 复制成功但原件删不掉：把副本清掉，宁可"源包没搬成"，也不要凭空多一份。
                            TryDeleteCopy(to, logLines);
                            failures.Add($"{Path.GetFileName(from)}（复制成功但原件删不掉：{deleteError.Message}）");
                            continue;
                        }
                    }

                    moved.Add(new SourcePackageMove
                    {
                        SourcePath = from,
                        TargetPath = to,
                        Size = move.Size,
                        Renamed = move.Renamed
                    });

                    movedBytes += move.Size;

                    // 每次搬运都写日志：从哪到哪 + 这个文件多大（决策 D-12）。
                    logLines.Add($"源包移入其余物（{move.Size} 字节）：{from} → {to}"
                                 + (move.Renamed ? "（目标同名，已加序号，未覆盖）" : string.Empty));
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(from)}（{ex.Message}）");
                }
            }

            /*
             * 一个都没搬成、而且这个目录是我们刚建出来的 → 把空壳收掉。
             *
             * 用户的抱怨原话是"多弄了文件夹"：搬失败时（只读 / 被占用）留下一个空的
             * <c>其余物\</c> 正是这一类 —— 它里面一个字节都没有，用户却看到多了一层目录。
             * 只删**空**目录，而且只删我们这一次建的（非空 / 别人建的都不碰）。
             */
            if (moved.Count == 0 && createdArtifactDirectory)
            {
                TryDeleteEmptyArtifactDirectory(plan.ArtifactDirectory, logLines);
            }

            return new SourcePackageMoveResult
            {
                Attempted = true,
                MovedCount = moved.Count,
                FailedCount = failures.Count,
                SkippedCount = plan.Skipped.Count,
                CrossVolumeCount = crossVolume,
                MovedBytes = movedBytes,
                Moved = moved,
                Failures = failures,
                LogLines = logLines,
                Message = BuildResultMessage(plan.ArtifactDirectory, moved.Count, movedBytes, crossVolume, failures)
            };
        }

        /// <summary>尽力删掉"我们刚建出来、里面一个字节都没有"的其余物目录（非空一律不碰）。</summary>
        private void TryDeleteEmptyArtifactDirectory(string directory, List<string> logLines)
        {
            try
            {
                if (!_fileSystem.DirectoryExists(directory))
                {
                    return;
                }

                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }

                Directory.Delete(directory, recursive: false);
                logLines.Add($"源包一个都没搬成，已收掉空目录：{directory}");
            }
            catch (Exception ex)
            {
                logLines.Add($"警告：没能收掉空的其余物目录（{directory}）：{ex.Message}");
            }
        }

        /// <summary>
        /// 源是不是**已经躺在其余物里**了（新旧两个名字都算，决策 D-8）。
        ///
        /// 判据有三条，缺一不可：
        /// ① 在其余物目录**之内或就是它**（共享根模式下源包会落在 <c>其余物\包名\</c>，
        ///    而这里传进来的就是那一层）；
        /// ② 在"同一个位置的旧名目录"里（<c>其余物</c> ↔ <c>过程物</c>）；
        /// ③ 在其余物目录下的 <c>过程物\</c> 子目录里（老版本把过程物又套了一层的情况）。
        /// </summary>
        /// <param name="restDirectory">命中的那个其余物目录（写日志用）。</param>
        private static bool IsAlreadyInsideRest(string source, string artifactRoot, out string restDirectory)
        {
            if (SafePathHelper.PathEquals(source, artifactRoot) ||
                source.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith(artifactRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                restDirectory = artifactRoot;
                return true;
            }

            string legacy = ProcessArtifactLayout.ResolveLegacyArtifactDirectory(artifactRoot);

            if (legacy.Length > 0 && IsInside(legacy))
            {
                restDirectory = legacy;
                return true;
            }

            if (ProcessArtifactLayout.IsInsideArtifactDirectory(source, artifactRoot))
            {
                restDirectory = artifactRoot;
                return true;
            }

            restDirectory = string.Empty;
            return false;

            bool IsInside(string directory)
            {
                return source.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                       source.StartsWith(directory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>目标被临时占用时的兜底改名（<c>名字(1).ext</c>，绝不覆盖）。</summary>
        private static string MakeFallbackTarget(string target)
        {
            for (int index = 1; index < 10000; index++)
            {
                string candidate = Path.Combine(
                    Path.GetDirectoryName(target) ?? string.Empty,
                    $"{Path.GetFileNameWithoutExtension(target)}({index}){Path.GetExtension(target)}");

                if (!SafePathHelper.FileExists(candidate) && !SafePathHelper.DirectoryExists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(
                Path.GetDirectoryName(target) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(target)}_{Guid.NewGuid():N}{Path.GetExtension(target)}");
        }

        /// <summary>副本清不掉也要说清楚：用户目录里会多出一份，那件事不能被静默吞掉。</summary>
        private void TryDeleteCopy(string copyPath, List<string> logLines)
        {
            try
            {
                _fileSystem.DeleteFile(copyPath);
                logLines.Add($"已回滚跨盘复制出来的副本：{copyPath}");
            }
            catch (Exception ex)
            {
                logLines.Add($"警告：跨盘复制出来的副本没能回滚（{copyPath}）：{ex.Message}；原源包仍在原处，请自行核对");
            }
        }

        private static List<ArtifactSkip> SkipAll(
            IEnumerable<string> sources,
            ArtifactSkipReason reason,
            string message)
        {
            return sources
                .Select(source => new ArtifactSkip { SourcePath = source, Reason = reason, Message = message })
                .ToList();
        }

        private static string BuildPlanMessage(
            string artifactDirectory,
            List<SourcePackageMove> moves,
            List<ArtifactSkip> skipped)
        {
            var parts = new List<string>
            {
                moves.Count > 0
                    ? $"计划把 {moves.Count} 个源包搬进 {artifactDirectory}"
                    : "没有可搬运的源包"
            };

            int renamed = moves.Count(move => move.Renamed);

            if (renamed > 0)
            {
                parts.Add($"其中 {renamed} 个重名，将加序号（不覆盖）");
            }

            if (skipped.Count > 0)
            {
                parts.Add($"{skipped.Count} 个被跳过（原因见跳过清单）");
            }

            return string.Join("；", parts);
        }

        private static string BuildResultMessage(
            string artifactDirectory,
            int movedCount,
            long movedBytes,
            int crossVolumeCount,
            List<string> failures)
        {
            var parts = new List<string>();

            if (movedCount > 0)
            {
                parts.Add($"已把 {movedCount} 个源包移入其余物：{artifactDirectory}（共 {movedBytes} 字节）");

                if (crossVolumeCount > 0)
                {
                    parts.Add($"其中 {crossVolumeCount} 个跨盘，已按“复制成功后再删原件”完成");
                }
            }
            else
            {
                parts.Add("没有源包被搬走");
            }

            if (failures.Count > 0)
            {
                parts.Add(failures.Count > 3
                    ? $"{failures.Count} 个没能搬入其余物，前 3 个：{string.Join("；", failures.Take(3))}"
                    : $"{failures.Count} 个没能搬入其余物：{string.Join("；", failures)}");
            }

            return string.Join("；", parts);
        }
        /// <summary>
        /// 这一单自己那一份**在盘上的真实路径**（改名之后也找得到）：最初导入那个目录里、**同包基名**的那个文件。
        /// 判不出 ⇒ 空串（调用方照旧按账上那几条走）。
        /// </summary>
        public static string ResolveOwnFileOnDisk(ArchiveTask task)
        {
            string original = task.OriginalPath ?? string.Empty;
            string directory = Path.GetDirectoryName(original) ?? string.Empty;

            if (original.Length == 0 || directory.Length == 0)
            {
                return string.Empty;
            }

            if (File.Exists(original))
            {
                return original;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(FileNameHelper.GetFileName(original));

            if (baseName.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (string.Equals(
                            FileNameHelper.GetArchiveBaseName(FileNameHelper.GetFileName(file)),
                            baseName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
            }
            catch
            {
                // 读不动 ⇒ 判不出。
            }

            return string.Empty;
        }
    }
}
