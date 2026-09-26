using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 结果归集的结果。
    /// </summary>
    public sealed class CollectResult
    {
        public bool Success { get; init; }

        public string DestinationPath { get; init; } = string.Empty;

        public int MovedFileCount { get; init; }

        /// <summary>因重名被自动改名 / 未覆盖的文件（源侧文件名）。</summary>
        public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 结果归集：把每个包的产物**移动**到统一目标目录下的一个子目录（AGENTS.md §9.5、设计.md §九）。
    ///
    /// 这个类会动用户的产物文件，所以两条底线写在最前面：
    /// ① **绝不覆盖**已有文件（File.Move 两参数版本目标存在即抛，AutoRename 只是把冲突提前化解）；
    /// ② **绝不删非空目录**（只尝试删"搬空了的"源目录，删不掉只当没发生）。
    /// 单文件搬不动（占用 / 权限）只记录并继续，不让一个文件拖垮整批归集。
    /// </summary>
    public sealed class ResultCollector
    {
        /// <summary>
        /// 把某个任务的产物**移动**到统一目标目录下的一个子目录。
        /// </summary>
        /// <param name="task">要归集的任务（用它的 OutputPath 作源、CurrentPath 的基名作子目录名）。</param>
        /// <param name="targetRootDirectory">归集目标根目录；不存在会被创建。</param>
        /// <param name="flattenSingleWrapper">
        /// true 时，如果产物目录下**只有一个子目录且没有文件**，就搬那个子目录里的内容
        /// （对应设计.md §九 的"去除无意义外层目录"：解压出来是 out/包名/包名/… 时不要套两层）。
        /// </param>
        public CollectResult Collect(ArchiveTask? task, string? targetRootDirectory, bool flattenSingleWrapper = true)
        {
            if (task == null)
            {
                return Failure("任务为空，无法归集");
            }

            if (string.IsNullOrWhiteSpace(task.OutputPath))
            {
                return Failure("任务没有输出目录，无法归集");
            }

            string outputDirectory = task.OutputPath;

            if (!SafeDirectoryExists(outputDirectory))
            {
                return Failure($"产物目录不存在，无法归集：{outputDirectory}");
            }

            /*
             * 目标根目录为空时必须直接失败。
             * 理由：Path.Combine("", name) 会得到一个相对路径，产物会被"归集"到进程当前目录 ——
             * 那是用户完全没指定的地方，比报错难查得多。
             */
            if (string.IsNullOrWhiteSpace(targetRootDirectory))
            {
                return Failure("未指定归集目标目录");
            }

            string targetRoot = targetRootDirectory!;
            string destinationPath = string.Empty;
            int movedCount = 0;

            try
            {
                string sourceRoot = ResolveSourceRoot(outputDirectory, flattenSingleWrapper);

                string folderName = SafePathHelper.SanitizeFileName(FileNameHelper.GetArchiveBaseName(task.CurrentPath));
                destinationPath = Path.Combine(targetRoot, folderName);

                /*
                 * 目标子目录已存在就换成 名字(1)。
                 * 理由：两个不同的包基名撞车很常见（C:\t\a\pack.zip 与 C:\t\b\pack.zip），
                 * 合并进同一个目录会让用户再也分不清哪份产物来自哪个包。
                 *
                 * 同时记下这个"已存在的目标目录"（existingDestination）：
                 * 本次产物会落到 名字(1) 里，但目标位置本来就有哪些同名文件必须让用户看见
                 * —— 这正是"同名文件没有被覆盖"的证据，也是 Conflicts 要报的东西。
                 */
                string? existingDestination = null;

                if (Directory.Exists(destinationPath))
                {
                    existingDestination = destinationPath;
                    destinationPath = SafePathHelper.AutoRenameDirectoryPath(destinationPath);
                }
                else if (File.Exists(destinationPath))
                {
                    // 目标位置被一个同名**文件**占着：也必须让开，否则后面的 CreateDirectory 会直接失败。
                    destinationPath = SafePathHelper.AutoRenameDirectoryPath(destinationPath);
                }

                /*
                 * 归集目标落在产物目录内部时立刻停手，而且要在**建目录之前**判断：
                 * 那样会把目标目录自己当成产物，一边搬一边往自己里面塞，轻则无限套娃，重则丢文件；
                 * 提前判断还能保证"目标不合法"时连空目录都不会留下。
                 */
                if (IsSameOrChildPath(destinationPath, sourceRoot))
                {
                    return Failure("归集目标目录不能位于产物目录内部，已停止归集");
                }

                // 目标根目录不存在就建；建不出来就没必要继续，否则每个文件都要失败一遍。
                if (!SafePathHelper.EnsureDirectoryExists(targetRoot))
                {
                    return Failure($"归集目标目录不存在且创建失败：{targetRoot}");
                }

                if (!SafePathHelper.CanWriteToDirectory(targetRoot))
                {
                    return Failure($"归集目标目录不可写：{targetRoot}");
                }

                if (!SafePathHelper.EnsureDirectoryExists(destinationPath))
                {
                    return Failure($"归集目录创建失败：{destinationPath}", destinationPath);
                }

                var conflicts = new List<string>();
                var errors = new List<string>();

                movedCount = MoveTree(sourceRoot, destinationPath, existingDestination, conflicts, errors);

                if (!SafePathHelper.PathEquals(sourceRoot, outputDirectory))
                {
                    // 摊平后外层空壳还在，顺手删掉；删不掉不算失败。
                    TryDeleteDirectoryIfEmpty(outputDirectory);
                }

                return new CollectResult
                {
                    Success = movedCount > 0,
                    DestinationPath = destinationPath,
                    MovedFileCount = movedCount,
                    Conflicts = conflicts,
                    Message = BuildMessage(destinationPath, movedCount, conflicts, errors)
                };
            }
            catch (Exception ex)
            {
                // 兜底：归集跑在批量管线里，任何意外都不能把整批任务带崩（AGENTS.md §6 第 9 条）。
                return new CollectResult
                {
                    Success = false,
                    DestinationPath = destinationPath,
                    MovedFileCount = movedCount,
                    Message = $"归集过程中出现意外错误：{ex.Message}"
                };
            }
        }

        /// <summary>
        /// 判断产物目录下是否只有一层无意义外壳，是的话返回外壳里面的目录（设计.md §九"去除无意义外层目录"）。
        /// 只在**恰好一个子目录、且没有同级文件**时才摊平 —— 有文件说明这一层本身就是有内容的产物层。
        /// </summary>
        private static string ResolveSourceRoot(string outputDirectory, bool flattenSingleWrapper)
        {
            if (!flattenSingleWrapper)
            {
                return outputDirectory;
            }

            try
            {
                string[] entries = Directory.GetFileSystemEntries(outputDirectory);

                if (entries.Length != 1)
                {
                    return outputDirectory;
                }

                string onlyEntry = entries[0];

                if (!TryReadAttributes(onlyEntry, out FileAttributes attributes))
                {
                    return outputDirectory;
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    return outputDirectory;
                }

                // 外壳是符号链接 / 联接点时不当外壳：跟着它搬等于把别处的文件挪走。
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return outputDirectory;
                }

                return onlyEntry;
            }
            catch
            {
                return outputDirectory;
            }
        }

        /// <summary>
        /// 递归搬运目录内容，返回真正移动成功的文件数。
        /// </summary>
        /// <param name="existingDestination">
        /// 本次被让开的那个"目标位置已有目录"（没有则为 null）。
        /// 每搬一个文件都拿它比一次：同名说明目标位置本来就有这个文件、而我们没有覆盖它，
        /// 这个名字必须进 Conflicts，否则用户会以为产物是"干净地"落进去的。
        /// </param>
        private static int MoveTree(
            string sourceDirectory,
            string destinationDirectory,
            string? existingDestination,
            List<string> conflicts,
            List<string> errors)
        {
            if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
            {
                errors.Add($"{Path.GetFileName(destinationDirectory)}（无法创建目标目录）");
                return 0;
            }

            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(sourceDirectory);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(sourceDirectory)}（无法读取目录：{ex.Message}）");
                return 0;
            }

            int movedCount = 0;

            foreach (string entry in entries)
            {
                if (!TryReadAttributes(entry, out FileAttributes attributes))
                {
                    errors.Add($"{Path.GetFileName(entry)}（读不到文件属性）");
                    continue;
                }

                // 不跟随符号链接 / 联接点：跟着走会把别处的文件搬走，还可能绕圈。
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add($"{Path.GetFileName(entry)}（是符号链接/联接点，未移动）");
                    continue;
                }

                string entryName = Path.GetFileName(entry);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    string childDestination = Path.Combine(destinationDirectory, entryName);
                    string? childExisting = existingDestination == null
                        ? null
                        : Path.Combine(existingDestination, entryName);

                    movedCount += MoveTree(entry, childDestination, childExisting, conflicts, errors);
                    continue;
                }

                if (existingDestination != null &&
                    File.Exists(Path.Combine(existingDestination, entryName)))
                {
                    AddConflict(conflicts, entryName);
                }

                if (TryMoveFile(entry, destinationDirectory, conflicts, errors))
                {
                    movedCount++;
                }
            }

            /*
             * 这个目录已经搬空了就顺手删掉。
             * Directory.Delete 用**非递归**重载：目录里只要还有任何东西就会抛异常，
             * 所以这里不可能删掉非空目录 —— 这是"绝不删非空目录"的硬保证，不靠调用方自觉。
             */
            TryDeleteDirectoryIfEmpty(sourceDirectory);

            return movedCount;
        }

        private static bool TryMoveFile(
            string sourceFile,
            string destinationDirectory,
            List<string> conflicts,
            List<string> errors)
        {
            string fileName = Path.GetFileName(sourceFile);
            string targetPath = Path.Combine(destinationDirectory, fileName);

            try
            {
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    // 目标同名：换成 名字(1).ext，绝不覆盖（AGENTS.md §6 第 3 条）。
                    string renamedPath = SafePathHelper.AutoRenameFilePath(targetPath);

                    if (!string.Equals(renamedPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        AddConflict(conflicts, fileName);
                    }

                    targetPath = renamedPath;
                }

                /*
                 * 两参数 File.Move 在目标已存在时直接抛 IOException，**不会覆盖**。
                 * 这里是最后一道保险：即使 AutoRename 与 Move 之间有并发写入（并行解压时同名包会撞），
                 * 也只会少搬一个文件并记进 errors，不会把别人的文件冲掉。
                 */
                File.Move(sourceFile, targetPath);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName}（{ex.Message}）");
                return false;
            }
        }

        /// <summary>
        /// 尽力删除已经空掉的目录：删不掉（非空 / 被占用 / 没权限）一律当没发生，不算失败。
        /// </summary>
        private static void TryDeleteDirectoryIfEmpty(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                if (Directory.GetFileSystemEntries(directory).Length > 0)
                {
                    return;
                }

                Directory.Delete(directory, recursive: false);
            }
            catch
            {
                // 空目录删不掉不影响归集结果，留给用户自己收拾。
            }
        }

        private static string BuildMessage(
            string destinationPath,
            int movedCount,
            List<string> conflicts,
            List<string> errors)
        {
            var parts = new List<string>();

            parts.Add(movedCount > 0
                ? $"已移动 {movedCount} 个文件到 {destinationPath}"
                : "没有可归集的产物文件");

            if (conflicts.Count > 0)
            {
                string detail = string.Join("、", conflicts.Take(5));

                parts.Add($"{conflicts.Count} 个文件在目标位置已有同名文件，未覆盖原文件（{detail}）");
            }

            if (errors.Count > 0)
            {
                string detail = string.Join("；", errors.Take(3));

                parts.Add(errors.Count > 3
                    ? $"{errors.Count} 个文件没能移动，前 3 个：{detail}"
                    : $"{errors.Count} 个文件没能移动：{detail}");
            }

            return string.Join("；", parts);
        }

        private static void AddConflict(List<string> conflicts, string fileName)
        {
            // 同一个名字可能既在"目标已有目录"里撞、又在落点撞，只报一次。
            if (!conflicts.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                conflicts.Add(fileName);
            }
        }

        private static CollectResult Failure(string message, string destinationPath = "")
        {
            return new CollectResult
            {
                Success = false,
                DestinationPath = destinationPath,
                Message = message
            };
        }

        /// <summary>candidate 与 parent 相同、或位于 parent 之下时返回 true（Windows 下忽略大小写）。</summary>
        private static bool IsSameOrChildPath(string? candidate, string? parent)
        {
            string fullCandidate = SafePathHelper.GetFullPathSafe(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string fullParent = SafePathHelper.GetFullPathSafe(parent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (fullCandidate.Length == 0 || fullParent.Length == 0)
            {
                return false;
            }

            if (string.Equals(fullCandidate, fullParent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SafeDirectoryExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }
    }
}
