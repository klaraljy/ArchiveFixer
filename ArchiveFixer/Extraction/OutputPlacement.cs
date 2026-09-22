using System;
using System.IO;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 输出落点模式（规格 <c>docs/输出与整理模型.md</c> §1.1）。四种互斥。
    ///
    /// <para>
    /// <see cref="CustomRootPerArchive"/> 与 <see cref="CustomRootFlat"/> 必须是**两个独立可选项**
    /// （用户原话："这个也要有单独的文件夹和解压到此目录之分"）。旧的
    /// "布尔 <c>ExtractToOriginalDirectory</c> + 一个自定义路径"表达不了这四种组合，
    /// 硬套的结果就是"想直接解到自定义目录"变成了"在自定义目录下再建一个包名文件夹"。
    /// </para>
    /// </summary>
    public enum OutputPlacementMode
    {
        /// <summary>同名子文件夹（默认）：<c>111\222.rar</c> → <c>111\222\内容物</c>。WinRAR 式地基，不能少。</summary>
        PerArchiveSubfolder = 0,

        /// <summary>当前目录：<c>111\222.rar</c> → <c>111\内容物</c>。</summary>
        SourceDirectoryFlat = 1,

        /// <summary>自定义位置 · 单独建文件夹：<c>&lt;自定义根&gt;\222\内容物</c>。</summary>
        CustomRootPerArchive = 2,

        /// <summary>自定义位置 · 直接解到该目录：<c>&lt;自定义根&gt;\内容物</c>。</summary>
        CustomRootFlat = 3
    }

    /// <summary>
    /// 终端落法（规格 §3.1 的可选项）：内容物最里面那一层文件夹叫什么。
    ///
    /// <para>
    /// <see cref="KeepLastFolder"/>（默认）保留归档内部最后一层文件夹名（<c>111\222\666\…</c>）；
    /// <see cref="UseArchiveName"/> 改用归档基名（<c>111\222\…</c>，"少一层点击"）。
    /// </para>
    /// <para>
    /// ⚠️ 本选项只管**套出来的那一层文件夹叫什么**。判定表情形 1（终端就是单个文件）本来就没有
    /// 文件夹层，两种取值结果相同 —— 见 <see cref="ResultFinalizer"/>。
    /// </para>
    /// </summary>
    public enum TerminalLayoutMode
    {
        /// <summary>保留归档内最后一层文件夹名（默认，更保守，不丢信息）。</summary>
        KeepLastFolder = 0,

        /// <summary>用归档基名当最后那一层文件夹名。</summary>
        UseArchiveName = 1
    }

    /// <summary>
    /// 落点解析的失败原因。**机器可判**（不要去比中文文案）。
    /// </summary>
    public enum OutputPlacementError
    {
        None = 0,

        /// <summary>源包路径为空。</summary>
        EmptySourcePath,

        /// <summary>源包路径里没有目录部分（例如只给了个文件名），推不出"当前目录"。</summary>
        SourceDirectoryUnavailable,

        /// <summary>自定义根为空。</summary>
        EmptyCustomRoot,

        /// <summary>自定义根是相对路径（<c>out\sub</c>）—— 会落到进程当前目录，用户完全没指定过那里。</summary>
        CustomRootNotAbsolute,

        /// <summary>自定义根就是盘符根本身（<c>D:\</c>）—— 等于把内容物直接摊在盘根上。</summary>
        CustomRootIsRootDirectory,

        /// <summary>自定义根的盘符不存在（U 盘拔了 / 盘符写错）。</summary>
        CustomRootDriveMissing,

        /// <summary>路径拼接失败（名字里带 Path.Combine 处理不了的字符）。</summary>
        InvalidDestinationPath
    }

    /// <summary>
    /// 落点解析结果。失败时 <see cref="DestinationDirectory"/> 为空，**绝不静默回退到别的目录**。
    /// </summary>
    public sealed class OutputPlacementResult
    {
        /// <summary>是否解析成功。</summary>
        public bool Success { get; init; }

        /// <summary>失败原因（成功时为 <see cref="OutputPlacementError.None"/>）。</summary>
        public OutputPlacementError Error { get; init; }

        /// <summary>给用户看的一句话（失败原因 / 落点说明）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>目标根：<c>PerArchiveSubfolder</c>/<c>SourceDirectoryFlat</c> 时是源包所在目录，否则是自定义根。</summary>
        public string DestinationRoot { get; init; } = string.Empty;

        /// <summary>最终落点目录（规格 §1.2 的 <c>destDir</c>）。</summary>
        public string DestinationDirectory { get; init; } = string.Empty;

        /// <summary>包基名（已清洗，可直接当文件夹名）。</summary>
        public string ArchiveBaseName { get; init; } = string.Empty;

        /// <summary>是否命中了场景 B 的"重复一层塌缩"（规格 §3.3）。</summary>
        public bool CollapsedRepeatedFolderLayer { get; init; }
    }

    /// <summary>
    /// 输出落点解析（规格 §1，**唯一实现处**；不变量 §6.6：禁止在别处再拼一遍输出路径）。
    ///
    /// <para>
    /// 三条设计约束：
    /// ① **纯函数**：输入输出都是字符串/枚举，不建目录、不写文件、不排除列目录；
    /// ② **失败要显式**：自定义根为空/相对路径/盘符不存在/是盘根本身时返回带原因的失败结果，
    ///    不抛异常，也不"悄悄换一个目录"—— 静默落到别处会让用户的东西出现在他从没指定的地方；
    /// ③ **名字清洗只有一处**：一律走既有的 <see cref="FileNameHelper"/>（Windows 保留名、结尾空格/点、非法字符），
    ///    不在这里另写一套。
    /// </para>
    /// </summary>
    public static class OutputPlacement
    {
        /// <summary>是不是"以自定义根为目标"的模式。</summary>
        public static bool UsesCustomRoot(OutputPlacementMode mode)
        {
            return mode == OutputPlacementMode.CustomRootPerArchive
                   || mode == OutputPlacementMode.CustomRootFlat;
        }

        /// <summary>是不是"每个包单独建一个文件夹"的模式。</summary>
        public static bool CreatesPerArchiveFolder(OutputPlacementMode mode)
        {
            return mode == OutputPlacementMode.PerArchiveSubfolder
                   || mode == OutputPlacementMode.CustomRootPerArchive;
        }

        /// <summary>
        /// 枚举 → 旧的"两个布尔"（<c>ExtractToOriginalDirectory</c> + <c>KeepArchiveNameFolder</c>），
        /// 用于把旧调用点（<c>PathService.BuildOutputPath</c> 只有这两个布尔）平滑接到本实现上。
        ///
        /// <code>
        /// PerArchiveSubfolder  → (true,  true )
        /// SourceDirectoryFlat  → (true,  false)
        /// CustomRootPerArchive → (false, true )
        /// CustomRootFlat       → (false, false)
        /// </code>
        ///
        /// ⚠️ 这是**单向的投影**：两个布尔带不回自定义根，反推要用
        /// <see cref="FromLegacyFlags(bool, bool, string?)"/>。四个枚举值在这里互不撞车，
        /// 所以只要自定义根不丢，往返转换是精确的。
        /// </summary>
        public static (bool ExtractToOriginalDirectory, bool KeepArchiveNameFolder) ToLegacyFlags(OutputPlacementMode mode)
        {
            bool toOriginalDirectory = !UsesCustomRoot(mode);
            bool keepArchiveNameFolder = CreatesPerArchiveFolder(mode);

            return (toOriginalDirectory, keepArchiveNameFolder);
        }

        /// <summary>
        /// 旧的两个布尔 + 自定义根 → 新枚举（旧配置迁移，规格 §1.3）。
        ///
        /// <para>
        /// 唯一的判断题是"自定义根为空时算什么"。旧代码在这时会落到**程序目录**
        /// （<c>PathService.BuildOutputPath</c> 的 <c>?? AppBaseDirectory</c>）—— 那是"用户从来没指定过"
        /// 的兜底，不是用户要的位置，所以这里绝不把它当成一种落点：
        /// 空根一律回到"源目录家族"（<c>KeepArchiveNameFolder</c> 决定是
        /// <see cref="OutputPlacementMode.PerArchiveSubfolder"/> 还是 <see cref="OutputPlacementMode.SourceDirectoryFlat"/>）。
        /// 这也正是规格 §1.3 要求的"`false` + 空根 → <c>PerArchiveSubfolder</c>"。
        /// </para>
        /// <para>
        /// <c>ExtractToOriginalDirectory == true</c> 时自定义根本来就不参与运算（旧代码同样忽略它），
        /// 所以这时传什么根都不影响结果。
        /// </para>
        /// </summary>
        public static OutputPlacementMode FromLegacyFlags(
            bool extractToOriginalDirectory,
            bool keepArchiveNameFolder,
            string? customRoot = null)
        {
            bool hasCustomRoot = !string.IsNullOrWhiteSpace(customRoot);

            if (!extractToOriginalDirectory && hasCustomRoot)
            {
                return keepArchiveNameFolder
                    ? OutputPlacementMode.CustomRootPerArchive
                    : OutputPlacementMode.CustomRootFlat;
            }

            return keepArchiveNameFolder
                ? OutputPlacementMode.PerArchiveSubfolder
                : OutputPlacementMode.SourceDirectoryFlat;
        }

        /// <summary>
        /// 解析目标根（规格 §1.2 的 <c>destRoot</c>）。
        ///
        /// 返回的结果里只填 <see cref="OutputPlacementResult.DestinationRoot"/>；
        /// 失败原因同样有效（自定义根的四类非法写法都在这里拦下）。
        /// </summary>
        /// <param name="sourceArchivePath">源包路径（可以只是文件名，但那样推不出"当前目录"）。</param>
        /// <param name="mode">落点模式。</param>
        /// <param name="customRoot">自定义根（仅自定义模式用得上）。</param>
        /// <param name="driveExists">
        /// 盘符存在性探针，收到的是盘根（如 <c>D:\</c>）。
        /// 默认走真实查询 —— 这是本类唯一一处碰环境的地方，抽成参数是为了让测试不依赖本机盘符。
        /// </param>
        public static OutputPlacementResult ResolveDestinationRoot(
            string? sourceArchivePath,
            OutputPlacementMode mode,
            string? customRoot = null,
            Func<string, bool>? driveExists = null)
        {
            if (string.IsNullOrWhiteSpace(sourceArchivePath))
            {
                return Failure(OutputPlacementError.EmptySourcePath, "源包路径为空，无法判断输出落点");
            }

            if (!UsesCustomRoot(mode))
            {
                string sourceDirectory = FileNameHelper.GetDirectoryName(sourceArchivePath);

                if (string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    return Failure(
                        OutputPlacementError.SourceDirectoryUnavailable,
                        "源包路径里没有目录部分，无法判断\"当前目录\"");
                }

                return new OutputPlacementResult
                {
                    Success = true,
                    DestinationRoot = sourceDirectory,
                    Message = "目标根：" + sourceDirectory
                };
            }

            return ResolveCustomRoot(customRoot, driveExists);
        }

        /// <summary>
        /// 解析最终落点 <c>destDir</c>（规格 §1.2 公式 + §3.3 场景 B 塌缩）。
        ///
        /// <code>
        /// destRoot = 模式 ∈ {PerArchiveSubfolder, SourceDirectoryFlat} ? dir(源包) : 自定义根
        /// destDir  = 模式 ∈ {PerArchiveSubfolder, CustomRootPerArchive}
        ///              ? destRoot \ SafeName(包基名)
        ///              : destRoot
        /// </code>
        /// </summary>
        /// <param name="sourceArchivePath">源包路径。</param>
        /// <param name="mode">落点模式。</param>
        /// <param name="customRoot">自定义根（仅自定义模式用得上）。</param>
        /// <param name="collapseRepeatedFolderLayer">
        /// 场景 B 开关（规格 §3.3，默认开）："包基名 == 其所在目录名"且该目录下只有这一个包时，
        /// 塌缩掉重复的一层，产物直接落在该包自己的目录里（<c>111\222\名字\内容物</c>）。
        /// 只在 <see cref="OutputPlacementMode.PerArchiveSubfolder"/> 下有意义 —— 别的模式本来就没有这一层。
        /// </param>
        /// <param name="sourceDirectoryContainsOnlyThisArchive">
        /// "这个目录下只有这一个包"这个事实**由调用方告知**：
        /// 本类是纯函数，不去扫目录。传 false 就等于关掉塌缩（宁可多一层，也不把多个包的产物混到一个目录里）。
        /// </param>
        /// <param name="volumeGroupBaseName">
        /// 分卷组基名（<see cref="Detection.VolumeGroupDetector"/> 的结果，如 <c>222.7z</c>）。
        /// 给了就用它替代"从文件名剥分卷标记"这一步 —— 上游已经算过就别再算一遍。
        /// </param>
        /// <param name="driveExists">盘符存在性探针，见 <see cref="ResolveDestinationRoot"/>。</param>
        public static OutputPlacementResult ResolveDestinationDirectory(
            string? sourceArchivePath,
            OutputPlacementMode mode,
            string? customRoot = null,
            bool collapseRepeatedFolderLayer = true,
            bool sourceDirectoryContainsOnlyThisArchive = false,
            string? volumeGroupBaseName = null,
            Func<string, bool>? driveExists = null)
        {
            OutputPlacementResult root = ResolveDestinationRoot(sourceArchivePath, mode, customRoot, driveExists);

            if (!root.Success)
            {
                return root;
            }

            string rawBaseName = ComputeRawBaseName(sourceArchivePath, volumeGroupBaseName);
            string safeBaseName = FileNameHelper.SanitizeFileName(rawBaseName);

            if (!CreatesPerArchiveFolder(mode))
            {
                return new OutputPlacementResult
                {
                    Success = true,
                    DestinationRoot = root.DestinationRoot,
                    DestinationDirectory = root.DestinationRoot,
                    ArchiveBaseName = safeBaseName,
                    Message = "落点：" + root.DestinationRoot
                };
            }

            string perArchiveDirectory = SafeCombine(root.DestinationRoot, safeBaseName);

            if (perArchiveDirectory.Length == 0)
            {
                return Failure(OutputPlacementError.InvalidDestinationPath, "目标目录路径拼不出来：" + root.DestinationRoot);
            }

            /*
             * 场景 B：111\222\名字\名字.rar。
             *
             * 不塌缩时 destDir = dir(源包) + 包基名 = 111\222\名字\名字，比用户期望的
             * 111\222\名字\内容物 多出重复的一层。判据是"包基名 == 它所在目录名"，
             * 但**还要**目录下只有这一个包 —— 否则同一个目录里两个包的产物会并在一起，
             * 用户再也分不清哪份内容来自哪个包（所以这一条由调用方告知，猜不得）。
             *
             * 塌缩就是把刚算出来的那一层去掉，即 destDir 回到"源包自己的目录"。
             */
            if (collapseRepeatedFolderLayer
                && mode == OutputPlacementMode.PerArchiveSubfolder
                && sourceDirectoryContainsOnlyThisArchive)
            {
                string sourceDirectory = FileNameHelper.GetDirectoryName(sourceArchivePath);
                string sourceDirectoryName = FileNameHelper.GetFileName(sourceDirectory);

                bool sameName = !string.IsNullOrWhiteSpace(sourceDirectoryName)
                                && (string.Equals(sourceDirectoryName, rawBaseName, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(sourceDirectoryName, safeBaseName, StringComparison.OrdinalIgnoreCase));

                if (sameName)
                {
                    string collapsed = TryGetParentDirectory(perArchiveDirectory);

                    // 盘根不安全：把内容物直接摊在 D:\ 上比多一层糟得多，宁可不动。
                    if (collapsed.Length > 0 && !IsPathRoot(collapsed))
                    {
                        return new OutputPlacementResult
                        {
                            Success = true,
                            DestinationRoot = root.DestinationRoot,
                            DestinationDirectory = collapsed,
                            ArchiveBaseName = safeBaseName,
                            CollapsedRepeatedFolderLayer = true,
                            Message = $"包名与目录同名，已塌缩重复的一层，落点：{collapsed}"
                        };
                    }
                }
            }

            return new OutputPlacementResult
            {
                Success = true,
                DestinationRoot = root.DestinationRoot,
                DestinationDirectory = perArchiveDirectory,
                ArchiveBaseName = safeBaseName,
                Message = "落点：" + perArchiveDirectory
            };
        }

        /// <summary>
        /// 包基名：<c>222.rar</c> / <c>222.7z.001</c> / <c>222.part1.rar</c> / <c>222.z01</c> → <c>222</c>。
        ///
        /// <para>
        /// 为什么不能直接用 <see cref="FileNameHelper.GetArchiveBaseName"/>：
        /// 它只剥一层后缀，<c>222.7z.001</c> 会得到 <c>222.7z</c> —— 于是落点变成 <c>111\222.7z\</c>，
        /// 分卷组里每一卷还会各建一个目录。
        /// </para>
        /// <para>
        /// 为什么也不能用 <see cref="Detection.VolumeGroupDetector"/> 的 <c>BaseName</c> 直接当结果：
        /// 那个基名是"分卷组"的基名（<c>222.7z.001</c> → <c>222.7z</c>，它保留了内层格式段，这是它该做的），
        /// 还要再剥掉归档后缀才是包基名。给了 <paramref name="volumeGroupBaseName"/> 就省掉剥分卷标记那一步。
        /// </para>
        /// <para>返回值已经清洗过，可以直接当文件夹名。</para>
        /// </summary>
        public static string ResolveArchiveBaseName(string? sourceArchivePath, string? volumeGroupBaseName = null)
        {
            return FileNameHelper.SanitizeFileName(ComputeRawBaseName(sourceArchivePath, volumeGroupBaseName));
        }

        /// <summary>清洗前的包基名（场景 B 的"包基名 == 目录名"要用原样名字比，不能拿清洗后的比）。</summary>
        private static string ComputeRawBaseName(string? sourceArchivePath, string? volumeGroupBaseName)
        {
            string fileName = !string.IsNullOrWhiteSpace(volumeGroupBaseName)
                ? FileNameHelper.GetFileName(volumeGroupBaseName)
                : FileNameHelper.GetFileName(sourceArchivePath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            return StripArchiveExtensions(StripVolumeMarkers(fileName));
        }

        /// <summary>
        /// 剥掉末尾的分卷标记：<c>222.7z.001</c> → <c>222.7z</c>、<c>222.z01</c> → <c>222</c>、
        /// <c>222.r00</c> → <c>222</c>、<c>222.part1.rar</c> → <c>222</c>。
        ///
        /// "什么算分卷标记"只留一份定义（<see cref="ExtensionHelper.IsVolumePartExtension"/>），
        /// 这里只负责把它从名字尾部摘掉。
        /// </summary>
        private static string StripVolumeMarkers(string fileName)
        {
            string current = fileName;

            // 连续的标记理论上只有 ".rar" 那一种组合，给 3 次机会足够，同时天然防死循环。
            for (int guard = 0; guard < 3; guard++)
            {
                int lastDot = current.LastIndexOf('.');

                if (lastDot <= 0)
                {
                    break;
                }

                string tail = current[(lastDot + 1)..];

                if (ExtensionHelper.IsVolumePartExtension("." + tail))
                {
                    current = current[..lastDot];
                    continue;
                }

                // xxx.part1.rar / xxx.001.rar：分卷段后面还挂着一个 .rar 尾巴。
                // ⚠️ 这里必须要求分卷段前面**还有内容**（prevDot > 0）：否则 "222.rar" 里的 "222"
                // 会被当成三位数字分卷段，整个名字被吃光。
                if (tail.Equals("rar", StringComparison.OrdinalIgnoreCase))
                {
                    int previousDot = current.LastIndexOf('.', lastDot - 1);

                    if (previousDot > 0
                        && ExtensionHelper.IsVolumePartExtension("." + current[(previousDot + 1)..lastDot]))
                    {
                        current = current[..previousDot];
                        continue;
                    }
                }

                break;
            }

            return current;
        }

        /// <summary>
        /// 剥掉末尾的归档后缀（<c>.rar</c> / <c>.7z</c> / <c>.zip</c> / <c>.tar.gz</c> …），最多 3 段。
        ///
        /// 两条经验规则：
        /// ① 只剥**已知归档后缀**。用 <c>Path.GetFileNameWithoutExtension</c> 无脑剥会把
        ///    <c>movie.2024</c> 变成 <c>movie</c> —— 数字结尾的名字太常见了；
        /// ② 若第一段剥掉的是**伪装后缀**（<c>222.rar.jpg</c>），允许继续剥下一段，
        ///    但只在"还没剥到归档后缀"时允许一次，避免把 <c>movie.mkv.rar</c> 的名字特征也啃掉。
        /// </summary>
        private static string StripArchiveExtensions(string name)
        {
            string current = name;
            bool strippedArchiveExtension = false;

            for (int guard = 0; guard < 3; guard++)
            {
                int lastDot = current.LastIndexOf('.');

                if (lastDot <= 0)
                {
                    break;
                }

                string extension = "." + current[(lastDot + 1)..];

                if (ExtensionHelper.IsKnownArchiveExtension(extension))
                {
                    current = current[..lastDot];
                    strippedArchiveExtension = true;
                    continue;
                }

                if (!strippedArchiveExtension && ExtensionHelper.IsSuspiciousFakeExtension(extension))
                {
                    current = current[..lastDot];
                    continue;
                }

                break;
            }

            return current;
        }

        /// <summary>校验自定义根：空 / 相对路径 / 盘根本身 / 盘符不存在，四类都返回明确原因。</summary>
        private static OutputPlacementResult ResolveCustomRoot(string? customRoot, Func<string, bool>? driveExists)
        {
            if (string.IsNullOrWhiteSpace(customRoot))
            {
                return Failure(OutputPlacementError.EmptyCustomRoot, "自定义输出位置为空");
            }

            string trimmed = customRoot.Trim();

            if (!Path.IsPathRooted(trimmed))
            {
                return Failure(
                    OutputPlacementError.CustomRootNotAbsolute,
                    $"自定义输出位置必须是绝对路径：{trimmed}");
            }

            string full = SafePathHelper.GetFullPathSafe(trimmed);
            string pathRoot = SafeGetPathRoot(full);

            if (pathRoot.Length == 0)
            {
                return Failure(
                    OutputPlacementError.CustomRootNotAbsolute,
                    $"自定义输出位置认不出盘符：{trimmed}");
            }

            // 顺序要紧：盘根本身的判据先于盘符存在性 —— 否则 "Z:\" 会被报成"盘符不存在"，
            // 用户照着改还是错（真正的问题是"不许直接摊在盘根上"）。
            if (IsPathRoot(full) || IsBareDriveSpec(trimmed, pathRoot))
            {
                return Failure(
                    OutputPlacementError.CustomRootIsRootDirectory,
                    $"自定义输出位置不能是盘根本身：{trimmed}");
            }

            Func<string, bool> probe = driveExists ?? DefaultDriveExists;

            if (!probe(pathRoot))
            {
                return Failure(
                    OutputPlacementError.CustomRootDriveMissing,
                    $"自定义输出位置的盘符不存在：{pathRoot}");
            }

            return new OutputPlacementResult
            {
                Success = true,
                DestinationRoot = full,
                Message = "目标根：" + full
            };
        }

        /// <summary>默认探针：盘根目录在不在。只在这里碰一次环境，且失败一律当"不存在"。</summary>
        private static bool DefaultDriveExists(string pathRoot)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(pathRoot) && Directory.Exists(pathRoot);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>路径是不是盘根 / UNC 根的本身（<c>D:\</c>、<c>\\server\share</c>）。</summary>
        private static bool IsPathRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = SafeGetPathRoot(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return root.Length > 0 && string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <c>D:</c> 这种"只有盘符"的写法：<see cref="Path.IsPathRooted"/> 认它，但它是**盘当前目录**，
        /// 会随进程当前目录漂移 —— 当成"盘根本身"一样拒掉最安全。
        /// </summary>
        private static bool IsBareDriveSpec(string trimmed, string pathRoot)
        {
            return trimmed.Length == 2
                   && trimmed[1] == ':'
                   && pathRoot.Length >= 2
                   && char.ToUpperInvariant(trimmed[0]) == char.ToUpperInvariant(pathRoot[0]);
        }

        private static string SafeGetPathRoot(string path)
        {
            try
            {
                return Path.GetPathRoot(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>取父目录；取不出来（已在根上）返回空串。</summary>
        private static string TryGetParentDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                       ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeCombine(string directory, string name)
        {
            return SafePathHelper.Combine(directory, name);
        }

        private static OutputPlacementResult Failure(OutputPlacementError error, string message)
        {
            return new OutputPlacementResult
            {
                Success = false,
                Error = error,
                Message = message
            };
        }
    }
}
