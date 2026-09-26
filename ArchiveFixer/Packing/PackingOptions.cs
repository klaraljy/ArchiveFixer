using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Packing
{
    /// <summary>用户选的东西是**文件夹**还是**单个文件**（用户 2026-09-26 第 46 条要求支持单文件）。</summary>
    public enum PackingSourceKind
    {
        /// <summary>文件夹（主用法：里面装着资源）。</summary>
        Folder = 0,

        /// <summary>单个文件（例如 <c>111.mp4</c>）：程序在它旁边生成同名文件夹把它装进去再打包。</summary>
        File = 1
    }

    /// <summary>最终 <c>.rar</c> 放在哪（用户 2026-09-26 第 46 条 + 当天追加）。</summary>
    public enum PackingTargetMode
    {
        /// <summary>
        /// **默认**：跟①页「输出位置」那一档走 —— 用户设过指定位置就用它，没设过就是源旁边。
        ///
        /// <para>用户原话："如果用户默认不去选择位置就将压缩至选择的目录位置"。</para>
        /// </summary>
        FollowOutputDirectory = 0,

        /// <summary>本地 = 源文件夹（或单文件）**旁边**。</summary>
        Local = 1,

        /// <summary>指定位置（用户在⑤页挑一个目录）。</summary>
        Custom = 2
    }

    /// <summary>打包里的**原包操作**（用户 2026-09-26；⛔ 与解压那套完全独立、各自记忆）。</summary>
    public enum PackingSourceHandling
    {
        /// <summary>不动（默认）：源文件夹/文件一个字节都不碰。</summary>
        KeepInPlace = 0,

        /// <summary>移入其余物（= 装 7z 分卷的那个文件夹里面）。</summary>
        MoveToRest = 1
    }

    /// <summary>打包里的**其余物操作**（= 装 7z 分卷的那个文件夹；用户 2026-09-26）。</summary>
    public enum PackingRestHandling
    {
        /// <summary>不动：分卷文件夹留着（可以自己检查分卷）。</summary>
        Keep = 0,

        /// <summary>移入回收站（可还原）。</summary>
        RecycleBin = 1,

        /// <summary>彻底删除（默认）——用户原话："这个对用户来说一点用没有"。</summary>
        Delete = 2
    }

    /// <summary>
    /// 分卷规则：**按卷数**（用户 2026-09-26 拍板的 (B) 方案）。
    ///
    /// <para>内容 &lt; 1 GiB → 切 **2** 卷；≥ 1 GiB → 切 **3** 卷。
    /// 每卷上限 = ⌈内容 ÷ 卷数⌉，**向上取整到整 MiB**（<c>7z -v</c> 的单位是 MiB）。</para>
    ///
    /// <para>⚠ 为什么不是"平均分"：7-Zip 的 <c>-v&lt;大小&gt;</c> 是**每卷装满到上限、最后一卷拿余数**
    /// （本机实测：1 MiB 分卷 → <c>.001</c> 正好 1048576 字节 + <c>.002</c> 是零头）。
    /// 所以"目标卷数"是**上限**：内容能压缩时成品更小，实际可能切出更少的卷（更小是好事，不是出错）。
    /// 日志里两个数都会写（目标 N 卷 / 实际 M 卷）。</para>
    /// </summary>
    public static class PackingVolumeRule
    {
        /// <summary>1 GiB（分档线）。</summary>
        public const long OneGiB = 1024L * 1024 * 1024;

        /// <summary>小于 1 GiB 的目标卷数。</summary>
        public const int SmallTargetCount = 2;

        /// <summary>大于等于 1 GiB 的目标卷数。</summary>
        public const int LargeTargetCount = 3;

        /// <summary>每卷上限的下限（7z 的 <c>-v</c> 以 MiB 为单位，再小没有意义）。</summary>
        public const long MinVolumeBytes = 1L * 1024 * 1024;

        /// <summary>
        /// 自动算出来的每卷上限的**天花板**（16 GiB）。
        ///
        /// <para>为什么留着天花板：某些文件系统（FAT32）单文件上限是 4 GiB，卷比它更大就写不下去；
        /// 而"内容极大"时（例如 100 GiB）不夹一下会切出一个几十 GiB 的卷。夹到 16 GiB 之后
        /// **卷数会多于目标卷数** —— 这一点由 <see cref="Describe"/> 如实说出来，⛔ 不许假装还是 3 卷。</para>
        /// </summary>
        public const long AutoMaxVolumeBytes = 16L * 1024 * 1024 * 1024;

        /// <summary>目标卷数：&lt;1 GiB → 2；≥1 GiB → 3。</summary>
        public static int TargetCount(long contentBytes) =>
            contentBytes < OneGiB ? SmallTargetCount : LargeTargetCount;

        /// <summary>
        /// 按目标卷数算出每卷上限（字节，整 MiB，已夹在 [1 MiB, 16 GiB] 内）。
        /// 内容为 0（空文件夹）时给下限 1 MiB —— 那种请求在上游就会被拒。
        /// </summary>
        public static long ComputeVolumeSize(long contentBytes, int targetCount)
        {
            if (contentBytes <= 0)
            {
                return MinVolumeBytes;
            }

            if (targetCount < 1)
            {
                targetCount = 1;
            }

            const long MiB = 1024 * 1024;

            // 先除再补，避免 content + count - 1 在极端值上溢出（与 ComputeVolumeCount 同一手法）。
            long quotient = contentBytes / targetCount;
            long remainder = contentBytes % targetCount;
            long raw = remainder == 0 ? quotient : TaskSpaceEstimate.SaturatingSum(quotient, 1);

            // 向上取整到整 MiB。
            long mib = raw / MiB + (raw % MiB == 0 ? 0 : 1);

            if (mib < 1)
            {
                mib = 1;
            }

            long bytes = mib * MiB;

            if (bytes > AutoMaxVolumeBytes)
            {
                return AutoMaxVolumeBytes;
            }

            return bytes < MinVolumeBytes ? MinVolumeBytes : bytes;
        }

        /// <summary>按算出来的每卷上限，**预计**切出几卷（内容为 0 时 0）。</summary>
        public static long PlannedCount(long contentBytes, long volumeSizeBytes) =>
            PackingPlan.ComputeVolumeCount(contentBytes, volumeSizeBytes);

        /// <summary>一句话说清"目标几卷、每卷上限多少、预计切出几卷"（日志 / 摘要用，⛔ 不撒谎）。</summary>
        public static string Describe(long contentBytes, long volumeSizeBytes)
        {
            int target = TargetCount(contentBytes);
            long planned = PlannedCount(contentBytes, volumeSizeBytes);

            return $"按 {target} 卷切（内容 {TaskSpaceEstimate.FormatSize(contentBytes)}，"
                 + $"每卷上限 {PackingPlan.FormatVolumeSize(volumeSizeBytes)}）—— 预计 {planned} 卷；"
                 + "实际按压缩后的体积算，前面每卷装满、最后一卷是零头。";
        }
    }

    /// <summary>选中的东西 → 真正要打包的那个文件夹（单文件会先在旁边生成同名文件夹）。</summary>
    public sealed class PackingSourceResolution
    {
        public bool Success { get; init; }

        /// <summary>不能打包时那句给用户看的话。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>用户选的那个路径（文件夹或文件）。</summary>
        public string SourcePath { get; init; } = string.Empty;

        public PackingSourceKind Kind { get; init; } = PackingSourceKind.Folder;

        /// <summary>真正要打包的文件夹（单文件时是那个新建的同名文件夹）。</summary>
        public string FolderToPack { get; init; } = string.Empty;

        /// <summary>
        /// 单文件时**要新建**的同名文件夹（例如 <c>111.mp4</c> → <c>111\</c>）；文件夹时是空串。
        /// 它算**其余物**（用户 2026-09-26 原话："他不算是原包的内容"）—— 默认会被删掉。
        /// </summary>
        public string WrapperFolderToCreate { get; init; } = string.Empty;

        /// <summary>命名基准：文件夹名 / 文件基名（<c>1111</c>、<c>111</c>）。</summary>
        public string SourceName { get; init; } = string.Empty;
    }

    /// <summary>
    /// 把"用户选的东西"翻成"要打包哪个文件夹"（纯逻辑，除了 <c>File/Directory.Exists</c> 不碰文件系统）。
    /// </summary>
    public static class PackingSourceResolver
    {
        public static PackingSourceResolution Resolve(string? selectedPath)
        {
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return Fail("请先选择要打包的文件夹（或一个文件）。");
            }

            string full;

            try
            {
                full = Path.GetFullPath(selectedPath);
            }
            catch (Exception ex)
            {
                return Fail($"这个路径用不了（{ex.Message}）。");
            }

            if (Directory.Exists(full))
            {
                string folderName = Path.GetFileName(full.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (string.IsNullOrWhiteSpace(folderName))
                {
                    return Fail($"这是盘根，不能直接打包：{full}。请选一个文件夹。");
                }

                return new PackingSourceResolution
                {
                    Success = true,
                    SourcePath = full,
                    Kind = PackingSourceKind.Folder,
                    FolderToPack = full,
                    SourceName = folderName
                };
            }

            if (File.Exists(full))
            {
                string fileName = Path.GetFileName(full);
                string baseName = Path.GetFileNameWithoutExtension(full);

                if (string.IsNullOrWhiteSpace(baseName))
                {
                    return Fail($"这个文件取不出名字（{fileName}），没法给它建同名文件夹。");
                }

                string? parent = Path.GetDirectoryName(full);

                if (string.IsNullOrWhiteSpace(parent))
                {
                    return Fail($"这个文件没有所在目录（{full}），没法在它旁边生成文件夹。");
                }

                string wrapper = PackingNaming.ResolveUniqueFolderPath(parent!, baseName);

                return new PackingSourceResolution
                {
                    Success = true,
                    SourcePath = full,
                    Kind = PackingSourceKind.File,
                    FolderToPack = wrapper,
                    WrapperFolderToCreate = wrapper,
                    SourceName = baseName
                };
            }

            return Fail($"这个路径不存在：{full}");
        }

        private static PackingSourceResolution Fail(string reason) => new()
        {
            Success = false,
            Reason = reason
        };
    }

    /// <summary>命名：装分卷的文件夹 / 最终 rar 的取名规则（用户 2026-09-26 第 5 条）。</summary>
    public static class PackingNaming
    {
        /// <summary>
        /// 装分卷的文件夹：**以源名命名**；重名时依次 <c>1111(1)</c>、<c>1111(2)</c>…
        /// （用户原话："如果用户选择不操作原包，那就是 1111(1) 文件夹"）。
        ///
        /// <para>⛔ 只看**磁盘上有没有**同名文件/文件夹，不看"用户选了哪一档" —— 那样最稳。
        /// <paramref name="reservedPaths"/> 是"这一轮马上要建、但现在还不存在"的路径
        /// （单文件时那个同名文件夹就是它）—— 不把它们算进去，
        /// 分卷文件夹会和"正要打包的那个文件夹"撞名，于是分卷落进被扫描的源文件夹里。</para>
        /// </summary>
        public static string ResolveUniqueFolderPath(
            string parentDirectory,
            string baseName,
            IReadOnlyCollection<string>? reservedPaths = null)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(baseName))
            {
                return string.Empty;
            }

            string candidate = Path.Combine(parentDirectory, baseName);

            if (!Exists(candidate) && !IsReserved(candidate, reservedPaths))
            {
                return candidate;
            }

            for (int index = 1; index < 10000; index++)
            {
                candidate = Path.Combine(parentDirectory, $"{baseName}({index})");

                if (!Exists(candidate) && !IsReserved(candidate, reservedPaths))
                {
                    return candidate;
                }
            }

            return Path.Combine(parentDirectory, $"{baseName}({DateTime.Now:yyyyMMddHHmmss})");
        }

        /// <summary>
        /// 最终 rar：**永远以源名命名**（<c>1111.rar</c>），重名时同样让位成 <c>1111(1).rar</c>
        /// —— ⛔ 绝不覆盖已有的文件。
        /// </summary>
        public static string ResolveUniqueFilePath(string directory, string baseName, string extension)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
            {
                return string.Empty;
            }

            extension ??= string.Empty;

            string candidate = Path.Combine(directory, baseName + extension);

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }

            for (int index = 1; index < 10000; index++)
            {
                candidate = Path.Combine(directory, $"{baseName}({index}){extension}");

                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{baseName}({DateTime.Now:yyyyMMddHHmmss}){extension}");
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private static bool IsReserved(string candidate, IReadOnlyCollection<string>? reservedPaths)
        {
            if (reservedPaths == null || reservedPaths.Count == 0)
            {
                return false;
            }

            foreach (string reserved in reservedPaths)
            {
                if (!string.IsNullOrWhiteSpace(reserved) &&
                    string.Equals(
                        Path.GetFullPath(reserved),
                        Path.GetFullPath(candidate),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 小确认弹窗里的那几个选择（用户 2026-09-26 第 6 条：点「打包」→ 一个小窗，
    /// 只显示"最终 rar 放在哪 + 原包操作 + 其余物操作"）。
    ///
    /// <para>⛔ 与解压侧的 <c>SourceHandling</c> / <c>RestHandlingAfterVerify</c> **完全独立**：
    /// 两边各有各的设置项、各存各的，任何一边改了都不许影响另一边（用户明确要求"两者绝对不能同步"）。</para>
    /// </summary>
    public sealed class PackingRunOptions
    {
        /// <summary>最终 <c>.rar</c> 放哪：默认（跟①页「输出位置」）/ 本地（源旁边）/ 指定位置。</summary>
        public PackingTargetMode TargetMode { get; init; } = PackingTargetMode.FollowOutputDirectory;

        /// <summary>指定位置的目录（<see cref="PackingTargetMode.Custom"/> 时用）。</summary>
        public string CustomOutputDirectory { get; init; } = string.Empty;

        /// <summary>
        /// **默认档**要落到的目录（由界面从①页「输出位置」解析好塞进来；空 = 源旁边）。
        ///
        /// <para>为什么由界面解析：那一档的真值在 <c>AppSettings.ExtractToOriginalDirectory</c> /
        /// <c>CustomOutputDirectory</c> 上，规划层（纯逻辑、不碰设置）不该去读它 —— 于是"解析"只有一处
        /// （`PackingViewModel`），值随请求传进来。</para>
        /// </summary>
        public string DefaultOutputDirectory { get; init; } = string.Empty;

        /// <summary>原包操作：不动（默认）/ 移入其余物。</summary>
        public PackingSourceHandling SourceHandling { get; init; } = PackingSourceHandling.KeepInPlace;

        /// <summary>其余物操作：不动 / 回收站 / 彻底删除（**默认彻底删除**）。</summary>
        public PackingRestHandling RestHandling { get; init; } = PackingRestHandling.Delete;

        /// <summary>从设置里读一套（打开弹窗时的初值）。</summary>
        public static PackingRunOptions FromSettings(AppSettings? settings)
        {
            if (settings == null)
            {
                return new PackingRunOptions();
            }

            return new PackingRunOptions
            {
                TargetMode = ParseTargetMode(settings.PackTargetMode),
                CustomOutputDirectory = settings.PackCustomOutputDirectory ?? string.Empty,
                DefaultOutputDirectory = ResolveDefaultOutputDirectory(settings),
                SourceHandling = ParseSourceHandling(settings.PackSourceHandling),
                RestHandling = ParseRestHandling(settings.PackRestHandling)
            };
        }

        /// <summary>
        /// 默认档要落在哪：**跟①页「输出位置」**（用户 2026-09-26 原话："如果用户默认不去选择位置
        /// 就将压缩至选择的目录位置"）。
        ///
        /// <list type="bullet">
        /// <item><description>①页设了「指定位置」→ 用它（<c>AppSettings.CustomOutputDirectory</c>）；</description></item>
        /// <item><description>①页是「未指定位置」（<c>ExtractToOriginalDirectory = true</c>）→ 空串 = 源旁边。</description></item>
        /// </list>
        /// ⛔ 这是"打包默认落点"的**唯一解析处**（`PackingViewModel` 与测试都引它）。
        /// </summary>
        public static string ResolveDefaultOutputDirectory(AppSettings? settings)
        {
            if (settings == null || settings.ExtractToOriginalDirectory)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(settings.CustomOutputDirectory)
                ? string.Empty
                : settings.CustomOutputDirectory;
        }

        /// <summary>写回设置（"相应的保存记忆操作"）。</summary>
        public void SaveTo(AppSettings? settings)
        {
            if (settings == null)
            {
                return;
            }

            settings.PackTargetMode = TargetMode.ToString();
            settings.PackCustomOutputDirectory = CustomOutputDirectory ?? string.Empty;
            settings.PackSourceHandling = SourceHandling.ToString();
            settings.PackRestHandling = RestHandling.ToString();
        }

        /// <summary>
        /// ⚠ 这一档会**连原包一起删掉**：原包移入其余物 + 其余物被删。
        /// 界面必须当场红字说清（用户 2026-09-26 见过这条说明）。
        /// </summary>
        public bool WouldDeleteSource =>
            SourceHandling == PackingSourceHandling.MoveToRest && RestHandling != PackingRestHandling.Keep;

        /// <summary>那句红字（不发生时为 <c>null</c>）。</summary>
        public string? SourceLossWarning => WouldDeleteSource
            ? "⚠ 你选的是「原包移入其余物」+「其余物" + RestHandlingText + "」—— "
              + "这等于**连你的原文件夹/原文件一起" + (RestHandling == PackingRestHandling.Delete ? "彻底删除" : "丢进回收站") + "**。"
              + "只想留 .rar 的话，把原包操作改成「不动」。"
            : null;

        /// <summary>其余物那一档的中文（界面上单选项上的字）。</summary>
        public string RestHandlingText => RestHandling switch
        {
            PackingRestHandling.RecycleBin => "移入回收站",
            PackingRestHandling.Delete => "彻底删除",
            _ => "不动"
        };

        /// <summary>原包那一档的中文。</summary>
        public string SourceHandlingText => SourceHandling switch
        {
            PackingSourceHandling.MoveToRest => "移入其余物",
            _ => "不动"
        };

        /// <summary>落点那一档的中文。</summary>
        public string TargetModeText => TargetMode switch
        {
            PackingTargetMode.Custom => "指定位置",
            PackingTargetMode.Local => "本地（源旁边）",
            _ => "默认（跟①页「输出位置」）"
        };

        /// <summary>进日志的一句话（⛔ 不含密码）。</summary>
        public string DescribeForLog() =>
            $"落点：{TargetModeText}"
            + (TargetMode == PackingTargetMode.Custom && !string.IsNullOrWhiteSpace(CustomOutputDirectory)
                ? $"（{CustomOutputDirectory}）"
                : string.Empty)
            + $"；原包操作：{SourceHandlingText}；其余物操作：{RestHandlingText}";

        public static PackingTargetMode ParseTargetMode(string? value) =>
            Enum.TryParse(value, ignoreCase: true, out PackingTargetMode parsed) ? parsed : PackingTargetMode.FollowOutputDirectory;

        public static PackingSourceHandling ParseSourceHandling(string? value) =>
            Enum.TryParse(value, ignoreCase: true, out PackingSourceHandling parsed)
                ? parsed
                : PackingSourceHandling.KeepInPlace;

        public static PackingRestHandling ParseRestHandling(string? value) =>
            Enum.TryParse(value, ignoreCase: true, out PackingRestHandling parsed)
                ? parsed
                : PackingRestHandling.Delete;
    }

    /// <summary>
    /// 落点规划（用户 2026-09-26 第 5/7 条）：最终 rar 与"装分卷的文件夹"分别落在哪、叫什么。
    ///
    /// <para>默认**本地** = 源（文件夹或文件）**旁边**；选"指定位置"就用那个目录。
    /// 两者同名重名时都自动让位（<c>1111(1)</c> / <c>1111(1).rar</c>），⛔ 绝不覆盖。</para>
    /// </summary>
    public static class PackingPaths
    {
        /// <summary>落点目录（默认 = 跟①页输出位置 / 本地 = 源的父目录）。拿不到时返回空串，由调用方报错。</summary>
        public static string ResolveTargetDirectory(PackingSourceResolution source, PackingRunOptions options)
        {
            if (source == null || !source.Success)
            {
                return string.Empty;
            }

            options ??= new PackingRunOptions();

            // 指定位置 → 就是它。
            if (options.TargetMode == PackingTargetMode.Custom &&
                !string.IsNullOrWhiteSpace(options.CustomOutputDirectory))
            {
                try
                {
                    return Path.GetFullPath(options.CustomOutputDirectory);
                }
                catch
                {
                    return string.Empty;
                }
            }

            // 默认档 → 跟①页「输出位置」（没设过就是源旁边）。
            if (options.TargetMode == PackingTargetMode.FollowOutputDirectory &&
                !string.IsNullOrWhiteSpace(options.DefaultOutputDirectory))
            {
                try
                {
                    return Path.GetFullPath(options.DefaultOutputDirectory);
                }
                catch
                {
                    return string.Empty;
                }
            }

            string? parent = Path.GetDirectoryName(
                source.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return string.IsNullOrWhiteSpace(parent) ? string.Empty : parent!;
        }

        /// <summary>
        /// 装 7z 分卷的文件夹（= 其余物）：源名，重名时 <c>源名(1)</c>。
        /// <paramref name="reservedPaths"/> 见 <see cref="PackingNaming.ResolveUniqueFolderPath"/>。
        /// </summary>
        public static string ResolveVolumesFolder(
            string targetDirectory,
            string sourceName,
            IReadOnlyCollection<string>? reservedPaths = null) =>
            PackingNaming.ResolveUniqueFolderPath(targetDirectory, sourceName, reservedPaths);

        /// <summary>最终 rar：源名 + <c>.rar</c>，重名时 <c>源名(1).rar</c>。</summary>
        public static string ResolveRarPath(string targetDirectory, string sourceName) =>
            PackingNaming.ResolveUniqueFilePath(targetDirectory, sourceName, ".rar");

        /// <summary>外层容器是 7z 时的产物：源名 + <c>.7z</c>（与 rar 一一对应）。</summary>
        public static string ResolveSevenZipOuterPath(string targetDirectory, string sourceName) =>
            PackingNaming.ResolveUniqueFilePath(targetDirectory, sourceName, ".7z");

        /// <summary>把源文件夹/文件搬进其余物时，它在里面的目标路径。</summary>
        public static string ResolveSourceMoveTarget(string volumesFolder, string sourceName) =>
            Path.Combine(volumesFolder, sourceName);

        /// <summary>路径 <paramref name="candidate"/> 是不是在 <paramref name="root"/> 里面（容器内校验用）。</summary>
        public static bool IsInside(string? root, string? candidate)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                return Path.GetFullPath(candidate).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 外层容器"**实际会用哪一个**"的结论（用户 2026-09-26 第 46 条：界面不再让用户选，
    /// 没装 WinRAR 时自动改用 7z）。
    ///
    /// <para>⛔ 为什么要单独有这么一处：这个结论**界面也要用** —— ⑤页摘要、确认弹窗里那句
    /// "最终产物：…"都得写实际会产出的那个文件名。以前只有 <c>PackingService</c> 内部算它，
    /// 于是没装 WinRAR 的机器上弹窗会写"最终产物：<c>1111.rar</c>"、跑完却得到
    /// <c>1111.7z</c> —— **界面撒谎**（第 36/45 条那类）。现在服务与界面引同一份计算。</para>
    /// </summary>
    public sealed class PackingOuterContainerResolution
    {
        /// <summary>本来要做的（请求里那个）。</summary>
        public PackOuterContainer Requested { get; init; } = PackOuterContainer.Rar;

        /// <summary>实际会做的。</summary>
        public PackOuterContainer Effective { get; init; } = PackOuterContainer.Rar;

        /// <summary>是不是"要 rar 但本机没有 <c>Rar.exe</c>，于是自动改用 7z"。</summary>
        public bool FellBackToSevenZip =>
            Requested == PackOuterContainer.Rar && Effective == PackOuterContainer.SevenZip;

        /// <summary>要外层、却连内置 7-Zip 都没有 → 什么外层都做不出来（调用方据此报错）。</summary>
        public bool Impossible => Requested.HasOuterArtifact() && !Effective.HasOuterArtifact();

        /// <summary>给用户与日志的一句话（没有发生回退时是空串）。</summary>
        public string Note => FellBackToSevenZip
            ? "本机没有 Rar.exe（WinRAR 是共享软件，程序不随包分发它）—— 自动改用 7z 外层："
              + "产物会是一个 .7z，而不是 .rar。"
            : string.Empty;
    }

    /// <summary>把"想做的容器"翻成"实际会做的容器"（**唯一出口**，见 <see cref="PackingOuterContainerResolution"/>）。</summary>
    public static class PackingOuterContainerResolver
    {
        /// <summary>
        /// 解析规则（用户原话："如果用户没有装 WinRAR，那就弄 7z 吧"）：
        /// 要 rar → 有 <c>Rar.exe</c> 就用 rar，没有但有内置 7-Zip 就**自动 7z**，两个都没有则做不出来；
        /// 要 7z → 有内置 7-Zip 就用它，没有则做不出来；不做外层 → 照旧（永远成立）。
        /// ⛔ **绝不把 7z 的产物叫成 <c>.rar</c>** —— 产物名跟着实际容器走。
        /// </summary>
        public static PackingOuterContainerResolution Resolve(
            PackOuterContainer requested,
            bool rarExists,
            bool sevenZipExists)
        {
            PackOuterContainer effective = requested switch
            {
                PackOuterContainer.Rar => rarExists
                    ? PackOuterContainer.Rar
                    : sevenZipExists ? PackOuterContainer.SevenZip : PackOuterContainer.None,
                PackOuterContainer.SevenZip => sevenZipExists ? PackOuterContainer.SevenZip : PackOuterContainer.None,
                _ => PackOuterContainer.None
            };

            return new PackingOuterContainerResolution
            {
                Requested = requested,
                Effective = effective
            };
        }
    }
}
