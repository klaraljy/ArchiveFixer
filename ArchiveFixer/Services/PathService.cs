using System;
using System.Diagnostics;
using System.IO;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 路径服务。
    /// 负责输出目录生成、安全文件夹名、路径冲突处理、打开目录等。
    /// </summary>
    public class PathService
    {
        /// <summary>
        /// 程序根目录。
        /// </summary>
        public string AppBaseDirectory => AppContext.BaseDirectory;

        /// <summary>
        /// 用户数据目录（%AppData%\ArchiveFixer）。
        /// </summary>
        public string DataRootDirectory { get; set; } = DefaultDataRootDirectory;

        /// <summary>默认数据根目录：程序目录下的 data。缓存绝不默认写 C 盘。</summary>
        public static string DefaultDataRootDirectory =>
            Path.Combine(AppContext.BaseDirectory, "data");

        /// <summary>
        /// 日志目录。
        /// </summary>
        public string LogsDirectory => Path.Combine(DataRootDirectory, "logs");

        /// <summary>
        /// 临时目录。
        /// </summary>
        public string TempDirectory => Path.Combine(DataRootDirectory, "temp");

        /// <summary>
        /// 递归解压的工作区根目录。
        /// 中间产物放这里，避免直接写用户的最终目录（AGENTS.md §6 第 12 条、设计.md §十三）。
        /// 放在 %AppData% 而不是源目录旁边：源目录可能只读、可能是别人的共享、也可能是 U 盘。
        /// </summary>
        public string WorkDirectory => Path.Combine(DataRootDirectory, "work");

        /// <summary>
        /// 7-Zip 工具目录。
        /// </summary>
        public string SevenZipDirectory => ToolLocator.Default.BundledDirectory;

        /// <summary>
        /// 7z.exe 路径。
        /// </summary>
        public string SevenZipExePath => ToolLocator.Default.SevenZipExePath;

        /// <summary>
        /// 7z.dll 路径。
        /// </summary>
        public string SevenZipDllPath => ToolLocator.Default.SevenZipDllPath;

        /// <summary>
        /// appsettings.json 路径。
        /// </summary>
        public string SettingsFilePath => Path.Combine(DataRootDirectory, "appsettings.json");

        /// <summary>
        /// 损坏配置备份路径。
        /// </summary>
        public string BrokenSettingsFilePath => Path.Combine(DataRootDirectory, "appsettings.broken.json");

        /// <summary>
        /// 暂存子目录名：<c>&lt;任务工作区&gt;\stage</c>。
        /// 入仓（stage）阶段所有中间动作都在这里做，**一个字节都不写源目录、也不写最终目录**（契约 §2.1）。
        /// </summary>
        public const string StageDirectoryName = "stage";

        /// <summary>
        /// 生成任务输出目录（契约 §1.2 的落点公式）。
        ///
        /// <para>
        /// 公式的**唯一实现处**是 <see cref="OutputPlacement.ResolveDestinationDirectory"/>，本方法只做三件事：
        /// ① 续解出来的内层包优先用父任务的落点（见下）；
        /// ② 把旧的三个布尔/字符串设置翻译成落点模式（<see cref="OutputPlacement.FromLegacyFlags"/>），
        ///    旧配置的行为不变（契约 §1.3：不得让用户升级后行为突变）；
        /// ③ 解不出来时返回**空串**，绝不回落到程序安装目录。
        /// </para>
        ///
        /// <para>
        /// 为什么解不出来必须返回空（父代理 2026-09-21 明确要求的必修项）：
        /// 旧实现在"自定义位置"模式下把空的 <c>CustomOutputDirectory</c> 回落成 <c>AppBaseDirectory</c>，
        /// 于是用户没设输出目录时，内容物会被解进**程序自己的安装目录**（data 旁边、和 exe 混在一起）。
        /// 返回空串之后由调用方给出"输出目录无效"的明确状态，用户重新选一个目录即可 ——
        /// 少一个产物目录，好过多一堆没人找得到的文件。
        /// </para>
        /// </summary>
        /// <param name="collapseRepeatedFolderLayer">
        /// 场景 B 塌缩开关（规格 §3.3，设置项 <see cref="AppSettings.CollapseRepeatedFolderLayer"/>，默认开）。
        /// </param>
        /// <param name="sourceDirectoryContainsOnlyThisArchive">
        /// "这个目录下只有这一个包"这个事实**必须由调用方查出来再告知**（目录扫描是磁盘活，
        /// 见 <see cref="SourceFolderScanService.Inspect"/>，由解压管线在后台线程上跑）。
        /// 默认 false = 不塌缩：宁可多一层，也不把多个包的产物混到一个目录里。
        ///
        /// ⚠ 界面刷新落点（<c>MainViewModel.RefreshOutputPaths</c>）走的是默认值 +
        /// <paramref name="collapseRepeatedFolderLayer"/> = false，**不扫目录** ——
        /// 那条路在 UI 线程上（AGENTS.md：UI 线程不许做目录扫描）；解压管线跑完会把
        /// 真实的实际落点回写进 <c>task.OutputPath</c>，所以界面最终显示的是真值。
        /// </param>
        public string BuildOutputPath(
            ArchiveTask task,
            ExtractOptions options,
            bool collapseRepeatedFolderLayer = false,
            bool sourceDirectoryContainsOnlyThisArchive = false)
        {
            if (task == null)
            {
                return string.Empty;
            }

            options ??= new ExtractOptions();
            options.Normalize();

            /*
             * 续解出来的内层包：落点就是**父任务那一个**最终目录，绝不再套一层。
             *
             * 这是"一个源包 = 一个最终目录"的第一道闸（用户诉求）。
             * 放在这里而不是调用方：MainViewModel.RefreshOutputPaths（界面"输出目录"列）、
             * 解压管线的落点计算全都走这一个方法，谁都不会算出第二个答案。
             */
            if (!string.IsNullOrWhiteSpace(task.ParentOutputDirectory))
            {
                return task.ParentOutputDirectory;
            }

            string archivePath = task.CurrentPath;

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                archivePath = task.OriginalPath;
            }

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return string.Empty;
            }

            OutputPlacementMode mode = OutputPlacement.FromLegacyFlags(
                options.ExtractToOriginalDirectory,
                options.KeepArchiveNameFolder,
                options.CustomOutputDirectory);

            OutputPlacementResult placement = OutputPlacement.ResolveDestinationDirectory(
                archivePath,
                mode,
                options.CustomOutputDirectory,
                collapseRepeatedFolderLayer,
                sourceDirectoryContainsOnlyThisArchive);

            return placement.Success ? placement.DestinationDirectory : string.Empty;
        }

        /// <summary>
        /// 获取压缩包基础名。
        /// </summary>
        public string GetArchiveBaseName(string filePath)
        {
            return FileNameHelper.GetArchiveBaseName(filePath);
        }

        /// <summary>
        /// 本任务的私有工作区目录：<c>&lt;work&gt;\&lt;任务名&gt;-&lt;源路径短哈希&gt;</c>。
        ///
        /// 沿用既有工作区规范（<c>.data\work\&lt;taskId&gt;\</c>，绝不落 C 盘），只加了一段源路径哈希：
        /// 两个**同名但不同目录**的包（<c>111\222\1.rar</c> 与 <c>333\1.rar</c>）在批量处理里很常见，
        /// 只用文件名当 taskId 会让它们共用同一个暂存目录 —— 并发解压时互相踩，
        /// 串行时前一个失败留下的产物会被后一个当成自己的产物搬运出去。
        /// 哈希只取决于**源文件全路径**，所以同一个包的多次尝试仍然落在同一个目录（失败后接着看、接着清）。
        /// </summary>
        public string BuildTaskWorkDirectory(ArchiveTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            string name = string.IsNullOrWhiteSpace(task.FileName)
                ? FileNameHelper.GetFileName(task.CurrentPath)
                : task.FileName;

            string safeName = FileNameHelper.SanitizeFileName(name);

            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = "task";
            }

            string identity = string.IsNullOrWhiteSpace(task.CurrentPath)
                ? task.OriginalPath
                : task.CurrentPath;

            return Path.Combine(WorkDirectory, $"{safeName}-{BuildPathIdentityHash(identity)}");
        }

        /// <summary>
        /// 本任务的暂存目录：<c>&lt;work&gt;\&lt;taskId&gt;\stage</c>（契约 §2.1）。
        /// 所有中间动作（抠内嵌归档之后的解压、第一层、内层分卷、递归）都落在这里。
        /// </summary>
        public string BuildTaskStageDirectory(ArchiveTask task)
        {
            string taskDirectory = BuildTaskWorkDirectory(task);

            return string.IsNullOrWhiteSpace(taskDirectory)
                ? string.Empty
                : Path.Combine(taskDirectory, StageDirectoryName);
        }

        /// <summary>
        /// 源文件全路径的稳定短哈希（FNV-1a，8 位十六进制）。
        /// 不用 <c>string.GetHashCode()</c>：它跨进程不稳定，而工作区目录要能被"下一次启动"
        /// 认出来（失败留下的暂存区、启动时的未完成工作区提示都依赖这个稳定性）。
        /// </summary>
        private static string BuildPathIdentityHash(string path)
        {
            unchecked
            {
                uint hash = 2166136261;

                foreach (char c in path.ToUpperInvariant())
                {
                    hash ^= c;
                    hash *= 16777619;
                }

                return hash.ToString("x8");
            }
        }

        /// <summary>
        /// 获取安全文件名。
        /// </summary>
        public string SanitizeFileName(string name)
        {
            return FileNameHelper.SanitizeFileName(name);
        }

        /// <summary>
        /// 确保目录存在。
        /// </summary>
        public bool EnsureDirectoryExists(string path)
        {
            return SafePathHelper.EnsureDirectoryExists(path);
        }

        /// <summary>
        /// 确保基础目录存在。
        /// </summary>
        public void EnsureBaseDirectories()
        {
            SafePathHelper.EnsureDirectoryExists(LogsDirectory);
            SafePathHelper.EnsureDirectoryExists(TempDirectory);
            SafePathHelper.EnsureDirectoryExists(WorkDirectory);
            SafePathHelper.EnsureDirectoryExists(WorkDirectory);
            SafePathHelper.EnsureDirectoryExists(SevenZipDirectory);
        }

        /// <summary>
        /// 自动重命名文件路径。
        /// </summary>
        public string AutoRenameFilePath(string path)
        {
            return SafePathHelper.AutoRenameFilePath(path);
        }

        /// <summary>
        /// 自动重命名目录路径。
        /// </summary>
        public string AutoRenameDirectoryPath(string path)
        {
            return SafePathHelper.AutoRenameDirectoryPath(path);
        }

        /// <summary>
        /// 打开目录。
        /// </summary>
        public bool OpenDirectory(string path)
        {
            return SafePathHelper.OpenDirectory(path);
        }

        /// <summary>
        /// 在资源管理器中选中文件。
        /// </summary>
        public bool OpenFileInExplorer(string filePath)
        {
            return SafePathHelper.OpenFileInExplorer(filePath);
        }

        /// <summary>
        /// 检查 7z.exe 是否存在。
        /// </summary>
        public bool SevenZipExeExists()
        {
            return File.Exists(SevenZipExePath);
        }

        /// <summary>
        /// 检查 7z.dll 是否存在。
        /// </summary>
        public bool SevenZipDllExists()
        {
            return File.Exists(SevenZipDllPath);
        }

        /// <summary>
        /// 检查输出目录是否可写。
        /// </summary>
        public bool CanWriteOutputDirectory(string directory)
        {
            return SafePathHelper.CanWriteToDirectory(directory);
        }

        /// <summary>
        /// 判断路径是否过长。
        /// </summary>
        public bool IsPathTooLong(string path)
        {
            return SafePathHelper.IsPathTooLong(path);
        }

        /// <summary>
        /// 处理目录冲突。
        /// </summary>
        public string ResolveDirectoryConflict(string targetDirectory, string conflictAction)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return targetDirectory;
            }

            conflictAction = string.IsNullOrWhiteSpace(conflictAction)
                ? "AutoRename"
                : conflictAction;

            if (!Directory.Exists(targetDirectory) && !File.Exists(targetDirectory))
            {
                return targetDirectory;
            }

            return conflictAction switch
            {
                "Skip" => targetDirectory,
                "Overwrite" => targetDirectory,
                "AutoRename" => AutoRenameDirectoryPath(targetDirectory),
                "Ask" => AutoRenameDirectoryPath(targetDirectory),
                _ => AutoRenameDirectoryPath(targetDirectory)
            };
        }

        /// <summary>
        /// 处理文件路径冲突。
        /// </summary>
        public string ResolveFileConflict(string targetPath, string conflictAction)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return targetPath;
            }

            conflictAction = string.IsNullOrWhiteSpace(conflictAction)
                ? "AutoRename"
                : conflictAction;

            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                return targetPath;
            }

            return conflictAction switch
            {
                "Skip" => targetPath,
                "Overwrite" => targetPath,
                "AutoRename" => AutoRenameFilePath(targetPath),
                "Ask" => AutoRenameFilePath(targetPath),
                _ => AutoRenameFilePath(targetPath)
            };
        }

        /// <summary>
        /// 获取相对程序目录的路径。
        /// </summary>
        public string GetAppRelativePath(params string[] parts)
        {
            if (parts == null || parts.Length == 0)
            {
                return AppBaseDirectory;
            }

            string[] allParts = new string[parts.Length + 1];
            allParts[0] = AppBaseDirectory;
            Array.Copy(parts, 0, allParts, 1, parts.Length);

            return Path.Combine(allParts);
        }

        /// <summary>
        /// 尝试用默认程序打开文件。
        /// </summary>
        public bool OpenFile(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };

                using Process? process = Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

    }
}
