using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 文件扫描服务。
    /// 
    /// 职责：
    /// 1. 接收文件路径和文件夹路径。
    /// 2. 判断路径类型。
    /// 3. 扫描文件夹。
    /// 4. 支持递归。
    /// 5. 支持扫描模式。
    /// 6. 去重。
    /// 7. 生成 ArchiveTask。
    /// 
    /// 注意：
    /// 只负责扫描和生成任务，不负责识别真实压缩格式。
    /// 真实格式识别交给 ArchiveDetectService。
    /// </summary>
    public class FileScanService
    {
        /// <summary>
        /// 扫描路径集合。
        /// paths 可以同时包含文件和文件夹。
        /// </summary>
        public async Task<List<ArchiveTask>> ScanPathsAsync(
            IEnumerable<string> paths,
            ScanOptions options,
            CancellationToken cancellationToken = default)
        {
            options ??= new ScanOptions();
            options.Normalize();

            return await Task.Run(() =>
            {
                var result = new List<ArchiveTask>();
                var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (paths == null)
                {
                    return result;
                }

                foreach (string rawPath in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        continue;
                    }

                    string path = SafePathHelper.GetFullPathSafe(rawPath);

                    try
                    {
                        if (File.Exists(path))
                        {
                            ArchiveTask? task = ScanFile(path, options);

                            if (task != null && addedPaths.Add(SafePathHelper.GetFullPathSafe(task.CurrentPath)))
                            {
                                task.Index = result.Count + 1;
                                result.Add(task);
                            }
                        }
                        else if (Directory.Exists(path))
                        {
                            List<ArchiveTask> folderTasks = ScanFolder(path, options, cancellationToken);

                            foreach (ArchiveTask task in folderTasks)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                string fullPath = SafePathHelper.GetFullPathSafe(task.CurrentPath);

                                if (addedPaths.Add(fullPath))
                                {
                                    task.Index = result.Count + 1;
                                    result.Add(task);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 单个路径扫描失败不能导致整体扫描崩溃。
                    }
                }

                RebuildIndex(result);
                return result;
            }, cancellationToken);
        }

        /// <summary>
        /// 扫描单个文件。
        /// 如果符合扫描条件，返回 ArchiveTask。
        /// 否则返回 null。
        /// </summary>
        public ArchiveTask? ScanFile(string filePath, ScanOptions options)
        {
            options ??= new ScanOptions();
            options.Normalize();

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                if (!ShouldIncludeFile(filePath, options))
                {
                    return null;
                }

                return CreateArchiveTask(filePath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 扫描文件夹。
        /// </summary>
        public List<ArchiveTask> ScanFolder(
            string folderPath,
            ScanOptions options,
            CancellationToken cancellationToken = default)
        {
            options ??= new ScanOptions();
            options.Normalize();

            var result = new List<ArchiveTask>();

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return result;
            }

            if (!Directory.Exists(folderPath))
            {
                return result;
            }

            try
            {
                SearchOption searchOption = options.RecursiveScan
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                IEnumerable<string> files = EnumerateFilesSafe(folderPath, searchOption, cancellationToken);

                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ArchiveTask? task = ScanFile(file, options);

                    if (task != null)
                    {
                        task.Index = result.Count + 1;
                        result.Add(task);
                    }
                }
            }
            catch
            {
                // 文件夹扫描失败时返回已扫描到的结果。
            }

            return result;
        }

        /// <summary>
        /// 判断文件是否应加入扫描结果。
        /// </summary>
        public bool ShouldIncludeFile(string filePath, ScanOptions options)
        {
            options ??= new ScanOptions();
            options.Normalize();

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            try
            {
                if (!File.Exists(filePath))
                {
                    return false;
                }

                var fileInfo = new FileInfo(filePath);

                if ((fileInfo.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    return false;
                }

                if (!options.IncludeHiddenFiles &&
                    (fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                {
                    return false;
                }

                if (!options.IncludeSystemFiles &&
                    (fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
                {
                    return false;
                }

                if (options.MaxFileSizeLimit > 0)
                {
                    long maxBytes = options.MaxFileSizeLimit * 1024L * 1024L;

                    if (fileInfo.Length > maxBytes)
                    {
                        return false;
                    }
                }

                string extension = ExtensionHelper.GetLastExtension(filePath);

                return options.ScanMode switch
                {
                    "ScanAllFiles" => true,

                    "ScanKnownArchiveExtensions" =>
                        ExtensionHelper.IsKnownArchiveExtension(extension),

                    "ScanSuspiciousFiles" =>
                        ExtensionHelper.IsSuspiciousFile(filePath),

                    _ => true
                };
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 创建 ArchiveTask。
        /// </summary>
        public ArchiveTask CreateArchiveTask(string filePath)
        {
            string fullPath = SafePathHelper.GetFullPathSafe(filePath);

            var task = new ArchiveTask
            {
                IsSelected = true,
                Index = 0,

                OriginalPath = fullPath,
                CurrentPath = fullPath,

                FileName = Path.GetFileName(fullPath),
                DirectoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty,
                CurrentExtension = ExtensionHelper.GetLastExtensionDisplay(fullPath),

                DetectedFormat = "Unknown",
                SuggestedExtension = string.Empty,
                ExtensionStatus = "未检测",
                RenamePreviewPath = string.Empty,

                Password = string.Empty,
                PasswordStatus = "未检测",

                OutputPath = string.Empty,
                Operation = "等待",
                Status = "等待扫描",
                ProgressText = "-",
                ErrorMessage = string.Empty,

                IsArchive = false,
                IsEncrypted = false,

                StartTime = null,
                EndTime = null,
                ElapsedText = "-",
                LastUpdatedTime = DateTime.Now
            };

            return task;
        }

        /// <summary>
        /// 从文件夹安全枚举文件。
        /// 递归时避免因为某个子目录权限不足导致整体失败。
        /// </summary>
        private static IEnumerable<string> EnumerateFilesSafe(
            string rootFolder,
            SearchOption searchOption,
            CancellationToken cancellationToken)
        {
            if (searchOption == SearchOption.TopDirectoryOnly)
            {
                IEnumerable<string> files;

                try
                {
                    files = Directory.EnumerateFiles(rootFolder);
                }
                catch
                {
                    yield break;
                }

                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return file;
                }

                yield break;
            }

            var folders = new Stack<string>();
            folders.Push(rootFolder);

            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string currentFolder = folders.Pop();

                IEnumerable<string> files;

                try
                {
                    files = Directory.EnumerateFiles(currentFolder);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return file;
                }

                IEnumerable<string> subFolders;

                try
                {
                    subFolders = Directory.EnumerateDirectories(currentFolder);
                }
                catch
                {
                    continue;
                }

                foreach (string subFolder in subFolders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    folders.Push(subFolder);
                }
            }
        }

        /// <summary>
        /// 重建序号。
        /// </summary>
        private static void RebuildIndex(IList<ArchiveTask> tasks)
        {
            if (tasks == null)
            {
                return;
            }

            for (int i = 0; i < tasks.Count; i++)
            {
                tasks[i].Index = i + 1;
            }
        }
    }
}
