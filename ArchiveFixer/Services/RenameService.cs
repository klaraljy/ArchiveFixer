using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 改名服务。
    /// 
    /// 职责：
    /// 1. 根据 RenameOptions 生成改名预览。
    /// 2. 执行用户确认后的改名。
    /// 3. 处理目标文件冲突。
    /// 4. 更新 ArchiveTask 的 CurrentPath / FileName / CurrentExtension / 状态。
    /// 
    /// 注意：
    /// 这里不直接操作 WPF 控件。
    /// 这里不弹窗。
    /// 所有后缀修改都必须先 BuildPreview，再由用户确认 ExecuteRenameAsync。
    /// </summary>
    public class RenameService
    {
        public List<RenamePreviewItem> BuildPreview(
            IEnumerable<ArchiveTask> tasks,
            RenameOptions options)
        {
            List<RenamePreviewItem> result = new();

            if (tasks == null)
            {
                return result;
            }

            options ??= new RenameOptions();
            options.Normalize();

            foreach (ArchiveTask task in tasks)
            {
                try
                {
                    if (task == null)
                    {
                        continue;
                    }

                    if (!task.IsSelected)
                    {
                        continue;
                    }

                    string oldPath = task.CurrentPath;

                    if (string.IsNullOrWhiteSpace(oldPath))
                    {
                        oldPath = task.OriginalPath;
                    }

                    if (string.IsNullOrWhiteSpace(oldPath))
                    {
                        continue;
                    }

                    string newPath = BuildNewPath(task, options);
                    string operationName = GetOperationDisplayName(options);

                    RenamePreviewItem item = new RenamePreviewItem(
                        oldPath,
                        task.DetectedFormat,
                        operationName,
                        newPath,
                        options.ConflictAction);

                    if (!File.Exists(oldPath))
                    {
                        item.MarkInvalid("源文件不存在");
                        result.Add(item);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(newPath))
                    {
                        item.MarkInvalid("无法生成新路径");
                        result.Add(item);
                        continue;
                    }

                    // 必须用规范化路径比较，不能比字符串：
                    // 只要路径写法有差异（正斜杠 / 冗余分隔符 / 大小写），
                    // 下面那句 File.Exists(newPath) 就会把"自己"当成"已存在的目标"，
                    // 于是把 data.tar.gz 这种本来不用改的文件改成 data.tar(1).gz。
                    if (SafePathHelper.PathEquals(oldPath, newPath))
                    {
                        item.MarkSkip("新路径与原路径相同，无需改名");
                        result.Add(item);
                        continue;
                    }

                    string? directory = Path.GetDirectoryName(newPath);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        item.MarkInvalid("无法确定目标目录");
                        result.Add(item);
                        continue;
                    }

                    if (!Directory.Exists(directory))
                    {
                        item.MarkInvalid("目标目录不存在");
                        result.Add(item);
                        continue;
                    }

                    if (File.Exists(newPath))
                    {
                        string resolvedPath = ResolveConflict(newPath, options.ConflictAction);

                        if (string.IsNullOrWhiteSpace(resolvedPath))
                        {
                            item.MarkSkip("目标文件已存在，已跳过");
                        }
                        else if (!string.Equals(resolvedPath, newPath, StringComparison.OrdinalIgnoreCase))
                        {
                            item.NewPath = resolvedPath;
                            item.MarkAutoRename("目标文件已存在，将自动重命名");
                        }
                        else
                        {
                            if (string.Equals(options.ConflictAction, "Overwrite", StringComparison.OrdinalIgnoreCase))
                            {
                                item.Status = StatusText.TargetExists;
                                item.ErrorMessage = "确认后将覆盖目标文件";
                            }
                            else
                            {
                                item.MarkConflict("目标文件已存在");
                            }
                        }
                    }

                    result.Add(item);
                }
                catch (Exception ex)
                {
                    string path = task?.CurrentPath ?? task?.OriginalPath ?? string.Empty;

                    RenamePreviewItem item = new RenamePreviewItem
                    {
                        OriginalPath = path,
                        OriginalFileName = Path.GetFileName(path),
                        DetectedFormat = task?.DetectedFormat ?? "Unknown",
                        Operation = GetOperationDisplayName(options),
                        NewPath = string.Empty,
                        NewFileName = string.Empty,
                        ConflictAction = options.ConflictAction
                    };

                    item.MarkInvalid("生成预览失败：" + ex.Message);
                    result.Add(item);
                }
            }

            return result;
        }

        public string BuildNewPath(ArchiveTask task, RenameOptions options)
        {
            if (task == null)
            {
                return string.Empty;
            }

            options ??= new RenameOptions();
            options.Normalize();

            string oldPath = task.CurrentPath;

            if (string.IsNullOrWhiteSpace(oldPath))
            {
                oldPath = task.OriginalPath;
            }

            if (string.IsNullOrWhiteSpace(oldPath))
            {
                return string.Empty;
            }

            string directory = Path.GetDirectoryName(oldPath) ?? string.Empty;
            string fileName = Path.GetFileName(oldPath);

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            string newFileName;

            switch (options.OperationType)
            {
                case "AddExtension":
                    newFileName = BuildAddExtensionFileName(fileName, options.TargetExtension);
                    break;

                case "ReplaceLastExtension":
                    newFileName = BuildReplaceLastExtensionFileName(fileName, options.TargetExtension);
                    break;

                case "DeleteLastExtension":
                    newFileName = BuildDeleteExtensionFileName(fileName, 1);
                    break;

                case "DeleteMultipleExtensions":
                    newFileName = BuildDeleteExtensionFileName(fileName, options.DeleteExtensionCount);
                    break;

                case "FixByDetectedFormat":
                    newFileName = BuildFixByDetectedFormatFileName(task, options);
                    break;

                default:
                    newFileName = BuildFixByDetectedFormatFileName(task, options);
                    break;
            }

            if (string.IsNullOrWhiteSpace(newFileName))
            {
                return oldPath;
            }

            return Path.Combine(directory, newFileName);
        }

        public async Task ExecuteRenameAsync(
            IEnumerable<RenamePreviewItem> previewItems,
            IEnumerable<ArchiveTask> tasks)
        {
            await Task.Yield();

            if (previewItems == null)
            {
                return;
            }

            List<RenamePreviewItem> itemList = previewItems.ToList();
            List<ArchiveTask> taskList = tasks?.ToList() ?? new List<ArchiveTask>();

            foreach (RenamePreviewItem item in itemList)
            {
                if (item == null)
                {
                    continue;
                }

                if (!item.IsSelected)
                {
                    continue;
                }

                if (item.Status == StatusText.RenameCannot || item.Status == StatusText.RenameWillSkip)
                {
                    continue;
                }

                string oldPath = item.OriginalPath;
                string newPath = item.NewPath;

                if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
                {
                    item.Status = StatusText.RenameCannot;
                    item.ErrorMessage = "源路径或目标路径为空";
                    continue;
                }

                ArchiveTask? matchedTask = FindTaskByPath(taskList, oldPath);

                bool moveSucceeded = false;
                string finalPath = newPath;

                try
                {
                    item.Status = StatusText.Renaming;
                    item.ErrorMessage = string.Empty;

                    if (!File.Exists(oldPath))
                    {
                        item.Status = StatusText.RenameCannot;
                        item.ErrorMessage = "源文件不存在";
                        UpdateTaskRenameFailed(matchedTask, item.ErrorMessage);
                        continue;
                    }

                    if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = StatusText.RenameWillSkip;
                        item.ErrorMessage = "新路径与原路径相同";
                        continue;
                    }

                    string? targetDirectory = Path.GetDirectoryName(newPath);

                    if (string.IsNullOrWhiteSpace(targetDirectory))
                    {
                        item.Status = StatusText.RenameCannot;
                        item.ErrorMessage = "无法确定目标目录";
                        UpdateTaskRenameFailed(matchedTask, item.ErrorMessage);
                        continue;
                    }

                    if (!Directory.Exists(targetDirectory))
                    {
                        item.Status = StatusText.RenameCannot;
                        item.ErrorMessage = "目标目录不存在";
                        UpdateTaskRenameFailed(matchedTask, item.ErrorMessage);
                        continue;
                    }

                    if (File.Exists(newPath))
                    {
                        string conflictAction = item.ConflictAction;

                        if (string.IsNullOrWhiteSpace(conflictAction))
                        {
                            conflictAction = "AutoRename";
                        }

                        if (string.Equals(conflictAction, "Skip", StringComparison.OrdinalIgnoreCase))
                        {
                            item.Status = StatusText.RenameWillSkip;
                            item.ErrorMessage = "目标文件已存在";
                            UpdateTaskSkipped(matchedTask, item.ErrorMessage);
                            continue;
                        }

                        if (string.Equals(conflictAction, "Overwrite", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await Task.Run(() => File.Delete(newPath));
                                finalPath = newPath;
                            }
                            catch (Exception ex)
                            {
                                item.Status = StatusText.RenameCannot;
                                item.ErrorMessage = "无法覆盖目标文件：" + ex.Message;
                                UpdateTaskRenameFailed(matchedTask, item.ErrorMessage);
                                continue;
                            }
                        }
                        else
                        {
                            finalPath = await Task.Run(() => AutoRenamePath(newPath));
                            item.NewPath = finalPath;
                            item.NewFileName = Path.GetFileName(finalPath);
                        }
                    }

                    await Task.Run(() => File.Move(oldPath, finalPath));

                    moveSucceeded = true;
                    item.Status = StatusText.RenameSuccess;
                    item.ErrorMessage = string.Empty;
                }
                catch (Exception ex)
                {
                    if (moveSucceeded || File.Exists(finalPath))
                    {
                        item.Status = StatusText.RenameSuccess;
                        item.ErrorMessage = string.Empty;

                        try
                        {
                            UpdateTaskRenameSuccess(matchedTask, finalPath);
                        }
                        catch
                        {
                            // 文件已经改名成功，不能因为更新任务失败再误报失败。
                        }

                        continue;
                    }

                    item.Status = StatusText.RenameCannot;
                    item.ErrorMessage = "改名失败：" + ex.Message;
                    UpdateTaskRenameFailed(matchedTask, item.ErrorMessage);
                    continue;
                }

                if (moveSucceeded)
                {
                    try
                    {
                        UpdateTaskRenameSuccess(matchedTask, finalPath);
                    }
                    catch
                    {
                        // 这里故意吞掉异常。
                        // 原因：File.Move 已经成功，不能因为 UI/任务对象更新异常再弹“改名失败”。
                        // 后续重新扫描时会以文件系统真实路径为准。
                    }
                }
            }
        }

        public string ResolveConflict(string targetPath, string conflictAction)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return string.Empty;
            }

            if (!File.Exists(targetPath))
            {
                return targetPath;
            }

            if (string.IsNullOrWhiteSpace(conflictAction))
            {
                conflictAction = "AutoRename";
            }

            if (string.Equals(conflictAction, "Skip", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (string.Equals(conflictAction, "Overwrite", StringComparison.OrdinalIgnoreCase))
            {
                return targetPath;
            }

            if (string.Equals(conflictAction, "AutoRename", StringComparison.OrdinalIgnoreCase))
            {
                return AutoRenamePath(targetPath);
            }

            if (string.Equals(conflictAction, "Ask", StringComparison.OrdinalIgnoreCase))
            {
                // Service 不弹窗。
                // Ask 在这里按 AutoRename 处理，避免卡死。
                return AutoRenamePath(targetPath);
            }

            return AutoRenamePath(targetPath);
        }

        public string AutoRenamePath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return string.Empty;
            }

            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                return targetPath;
            }

            string? directory = Path.GetDirectoryName(targetPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(targetPath);
            string extension = Path.GetExtension(targetPath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Environment.CurrentDirectory;
            }

            for (int i = 1; i <= 9999; i++)
            {
                string candidate = Path.Combine(
                    directory,
                    $"{fileNameWithoutExtension}({i}){extension}");

                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            string fallback = Path.Combine(
                directory,
                $"{fileNameWithoutExtension}_{DateTime.Now:yyyyMMddHHmmssfff}{extension}");

            return fallback;
        }

        private static string BuildAddExtensionFileName(string fileName, string extension)
        {
            extension = NormalizeExtension(extension, ".7z");

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            return fileName + extension;
        }

        private static string BuildReplaceLastExtensionFileName(string fileName, string extension)
        {
            extension = NormalizeExtension(extension, ".7z");

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            string currentExtension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(currentExtension))
            {
                return fileName + extension;
            }

            string baseName = fileName[..^currentExtension.Length];

            if (string.IsNullOrWhiteSpace(baseName))
            {
                return fileName;
            }

            return baseName + extension;
        }

        private static string BuildDeleteExtensionFileName(string fileName, int deleteCount)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            if (deleteCount < 1)
            {
                deleteCount = 1;
            }

            string result = fileName;

            for (int i = 0; i < deleteCount; i++)
            {
                string extension = Path.GetExtension(result);

                if (string.IsNullOrWhiteSpace(extension))
                {
                    break;
                }

                result = result[..^extension.Length];

                if (string.IsNullOrWhiteSpace(result))
                {
                    return fileName;
                }
            }

            return result;
        }

        private static string BuildFixByDetectedFormatFileName(ArchiveTask task, RenameOptions options)
        {
            string oldPath = task.CurrentPath;

            if (string.IsNullOrWhiteSpace(oldPath))
            {
                oldPath = task.OriginalPath;
            }

            string fileName = Path.GetFileName(oldPath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            // 分卷文件一律不改名。
            // 例：volume.7z.001 被识别为 7Z 后，后面的"多重后缀修正"会把它改成 volume.7z，
            // 而 7z 只认 .001 这一套命名 —— 改名等于直接切断分卷链，整个包再也解不开。
            // 这里只做"不许改坏"的保守拦截；完整的分卷组识别见 AGENTS.md §9.3（M2）。
            if (FileNameHelper.IsVolumePartFileName(fileName))
            {
                return fileName;
            }

            string suggestedExtension = task.SuggestedExtension;

            if (string.IsNullOrWhiteSpace(suggestedExtension))
            {
                suggestedExtension = GetSuggestedExtensionByFormat(task.DetectedFormat);
            }

            suggestedExtension = NormalizeExtension(suggestedExtension, options.TargetExtension);

            if (string.IsNullOrWhiteSpace(task.DetectedFormat) ||
                string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(options.UnknownFormatAction, "Skip", StringComparison.OrdinalIgnoreCase))
                {
                    return fileName;
                }

                suggestedExtension = NormalizeExtension(options.TargetExtension, ".7z");
            }

            string currentExtension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(currentExtension))
            {
                return fileName + suggestedExtension;
            }

            if (string.Equals(currentExtension, suggestedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            string fixedMultiExtensionName = TryFixMultiExtensionFileName(fileName, suggestedExtension);

            if (!string.IsNullOrWhiteSpace(fixedMultiExtensionName))
            {
                return fixedMultiExtensionName;
            }

            string baseName = fileName[..^currentExtension.Length];

            if (string.IsNullOrWhiteSpace(baseName))
            {
                return fileName;
            }

            return baseName + suggestedExtension;
        }

        private static string TryFixMultiExtensionFileName(string fileName, string suggestedExtension)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(suggestedExtension))
            {
                return string.Empty;
            }

            suggestedExtension = NormalizeExtension(suggestedExtension, ".7z");

            List<string> extensions = GetAllExtensions(fileName);

            if (extensions.Count < 2)
            {
                return string.Empty;
            }

            int matchedIndex = -1;

            for (int i = 0; i < extensions.Count; i++)
            {
                if (string.Equals(extensions[i], suggestedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    matchedIndex = i;
                }
            }

            if (matchedIndex < 0)
            {
                return string.Empty;
            }

            string namePart = fileName;

            int totalRemoveLength = 0;

            for (int i = extensions.Count - 1; i > matchedIndex; i--)
            {
                totalRemoveLength += extensions[i].Length;
            }

            if (totalRemoveLength <= 0 || totalRemoveLength >= fileName.Length)
            {
                return string.Empty;
            }

            namePart = fileName[..^totalRemoveLength];

            if (string.IsNullOrWhiteSpace(namePart))
            {
                return string.Empty;
            }

            return namePart;
        }

        private static List<string> GetAllExtensions(string fileName)
        {
            List<string> result = new();

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return result;
            }

            string[] parts = fileName.Split('.', StringSplitOptions.None);

            if (parts.Length <= 1)
            {
                return result;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                {
                    continue;
                }

                result.Add("." + parts[i]);
            }

            return result;
        }

        private static string GetSuggestedExtensionByFormat(string format)
        {
            return ExtensionHelper.GetSuggestedExtensionByFormat(format);
        }

        private static string NormalizeExtension(string? extension, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(extension)
                ? fallback
                : extension.Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                value = ".7z";
            }

            if (!value.StartsWith(".", StringComparison.Ordinal))
            {
                value = "." + value;
            }

            return value;
        }

        private static string GetOperationDisplayName(RenameOptions options)
        {
            if (options == null)
            {
                return "按真实格式修正";
            }

            return options.OperationType switch
            {
                "AddExtension" => "添加后缀",
                "ReplaceLastExtension" => "替换最后后缀",
                "DeleteLastExtension" => "删除最后后缀",
                "DeleteMultipleExtensions" => $"删除 {options.DeleteExtensionCount} 个后缀",
                "FixByDetectedFormat" => "按真实格式修正",
                _ => options.OperationType
            };
        }

        private static ArchiveTask? FindTaskByPath(IEnumerable<ArchiveTask> tasks, string path)
        {
            if (tasks == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return tasks.FirstOrDefault(x =>
                string.Equals(x.CurrentPath, path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.OriginalPath, path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.RenamePreviewPath, path, StringComparison.OrdinalIgnoreCase));
        }

        private static void UpdateTaskRenameSuccess(ArchiveTask? task, string newPath)
        {
            if (task == null)
            {
                return;
            }

            task.CurrentPath = newPath;
            task.RenamePreviewPath = string.Empty;
            task.FileName = Path.GetFileName(newPath);
            task.DirectoryPath = Path.GetDirectoryName(newPath) ?? string.Empty;
            task.CurrentExtension = string.IsNullOrWhiteSpace(Path.GetExtension(newPath))
                ? "无"
                : Path.GetExtension(newPath);

            task.Operation = StatusText.OpRename;
            task.Status = StatusText.RenameSuccess;
            task.ErrorMessage = string.Empty;
            task.LastUpdatedTime = DateTime.Now;
        }

        private static void UpdateTaskRenameFailed(ArchiveTask? task, string errorMessage)
        {
            if (task == null)
            {
                return;
            }

            task.Operation = StatusText.OpRename;
            task.Status = StatusText.RenameFailed;
            task.ErrorMessage = errorMessage ?? string.Empty;
            task.LastUpdatedTime = DateTime.Now;
        }

        private static void UpdateTaskSkipped(ArchiveTask? task, string message)
        {
            if (task == null)
            {
                return;
            }

            task.Operation = StatusText.OpSkip;
            task.Status = StatusText.Skipped;
            task.ErrorMessage = message ?? string.Empty;
            task.LastUpdatedTime = DateTime.Now;
        }
    }
}
