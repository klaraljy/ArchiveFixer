using System;
using System.Diagnostics;
using System.IO;
using ArchiveFixer.Engines;
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
        /// 生成任务输出目录。
        /// </summary>
        public string BuildOutputPath(ArchiveTask task, ExtractOptions options)
        {
            if (task == null)
            {
                return string.Empty;
            }

            options ??= new ExtractOptions();
            options.Normalize();

            string archivePath = task.CurrentPath;

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                archivePath = task.OriginalPath;
            }

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return string.Empty;
            }

            string baseDirectory;

            if (options.ExtractToOriginalDirectory)
            {
                baseDirectory = Path.GetDirectoryName(archivePath) ?? AppBaseDirectory;
            }
            else
            {
                baseDirectory = string.IsNullOrWhiteSpace(options.CustomOutputDirectory)
                    ? AppBaseDirectory
                    : options.CustomOutputDirectory;
            }

            if (!options.KeepArchiveNameFolder)
            {
                return baseDirectory;
            }

            string archiveBaseName = GetArchiveBaseName(archivePath);
            archiveBaseName = SanitizeFileName(archiveBaseName);

            return Path.Combine(baseDirectory, archiveBaseName);
        }

        /// <summary>
        /// 获取压缩包基础名。
        /// </summary>
        public string GetArchiveBaseName(string filePath)
        {
            return FileNameHelper.GetArchiveBaseName(filePath);
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
