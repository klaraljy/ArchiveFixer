using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Storage
{
    /// <summary>工作区里剩下来的一个任务目录（失败 / 取消 / 部分完成留下的）。</summary>
    public sealed class WorkspaceLeftover
    {
        /// <summary>目录全路径。</summary>
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>
        /// 这个目录属于**哪个工作区根**（默认跟输出盘之后根会随输出盘变，所以"它在哪"必须逐条记下来）。
        /// 清理时按它对回各自的根做容器内校验（<see cref="WorkspaceCleanupService.Cleanup"/>）。
        /// </summary>
        public string RootDirectory { get; init; } = string.Empty;

        /// <summary>目录名（就是任务工作区的标识，形如 <c>包名-源路径短哈希</c>）。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>里面的文件数（递归）。</summary>
        public int FileCount { get; init; }

        /// <summary>里面的字节数（递归）。</summary>
        public long TotalBytes { get; init; }

        /// <summary>最后一次写入时间（用来判断"放多久了"）。</summary>
        public DateTime LastWriteTimeUtc { get; init; }

        /// <summary>量不出来（权限 / 路径过长 / 枚举到一半被删）——**不假装它是 0 字节**。</summary>
        public bool MeasurementFailed { get; init; }
    }

    /// <summary>一个工作区目录的清理结果。</summary>
    public sealed class WorkspaceCleanupOutcome
    {
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>真的删掉了（或本来就不在了）。</summary>
        public bool Deleted { get; init; }

        /// <summary>删不掉的原因（删掉了就是空串）。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 工作区残留的**扫描与清理**（用户 2026-09-24 第 22 条）。
    ///
    /// <para><b>为什么要有它</b>：失败 / 取消留下的工作区是**刻意保留**的（里面是用户唯一的一份产物线索，
    /// 见 <c>AGENTS.md</c> §6 不变量 12/13 与 <see cref="Extraction.ExtractionWorkspace"/>），
    /// 但程序以前只在启动时写一行日志说"发现 N 个"，**既不报体积、也没有清理入口** ——
    /// 用户实测攒到 10 个目录 / 5.7 GB 才发现。这个类负责把它变成"看得见、点得动"：
    /// 扫描出每个残留的名字 / 体积 / 时间，并在**用户确认之后**只删这些目录。</para>
    ///
    /// <para><b>四条安全规则（都有测试钉住）</b>：</para>
    /// <list type="number">
    /// <item><description>只认工作区根目录**直属的子目录**：根目录本身、根下面的文件、更深的层级一律不动；</description></item>
    /// <item><description>每个候选删之前再做一次**容器内校验**（确实在根之下），越界就什么都不删并如实说明；</description></item>
    /// <item><description>只删目录、绝不删根目录下的散文件（那不是我们造的）；</description></item>
    /// <item><description>任何异常（占用 / 权限 / 路径过长）都**只报告不抛**：清工作区失败不该让别的事情变成失败。</description></item>
    /// </list>
    ///
    /// <para><b>不引用 WPF</b>（分层铁律）：纯 <c>System.IO</c> 逻辑，可以在无界面宿主里直接跑。</para>
    /// </summary>
    public static class WorkspaceCleanupService
    {
        /// <summary>
        /// 扫一遍工作区根目录下剩下的任务目录。
        ///
        /// <para>只做**元数据**枚举（不读文件内容）；量不出体积的条目照样列出来并标
        /// <see cref="WorkspaceLeftover.MeasurementFailed"/> —— 报"0 字节"会让用户以为没什么可清的。</para>
        /// </summary>
        public static IReadOnlyList<WorkspaceLeftover> Scan(string? workRoot)
        {
            var result = new List<WorkspaceLeftover>();

            if (string.IsNullOrWhiteSpace(workRoot) || !Directory.Exists(workRoot))
            {
                return result;
            }

            string[] directories;

            try
            {
                directories = Directory.GetDirectories(workRoot);
            }
            catch
            {
                // 读不了工作区就当"没有残留"：这只是一条提示，绝不允许它变成启动失败。
                return result;
            }

            foreach (string directory in directories)
            {
                result.Add(Measure(directory, workRoot));
            }

            result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

            return result;
        }

        /// <summary>
        /// 扫**多个**工作区根（用户 2026-09-24 拍板之后的常态）。
        ///
        /// <para><b>为什么必须能扫多个</b>：工作区默认跟输出盘走，于是根会**随输出盘变**
        /// （今天 <c>D:\.ArchiveFixer.work</c>、明天 <c>E:\.ArchiveFixer.work</c>），
        /// 而老位置 <c>&lt;程序目录&gt;\data\work</c> 里可能还留着升级前那一批的残留。
        /// 只扫"当前生效的那一个"会让换过盘的残留彻底看不见 —— 那正是用户抱怨过的那件事
        /// （"你会讲解压失败的残留放在安装包的位置"）。每个条目都带上自己的
        /// <see cref="WorkspaceLeftover.RootDirectory"/>，清理时按它各自回到自己的根做校验。</para>
        ///
        /// <para>重复的根只扫一次（调用方可能同时把"当前生效的根""老位置""账本里的根"都塞进来）。</para>
        /// </summary>
        public static IReadOnlyList<WorkspaceLeftover> ScanMany(IEnumerable<string>? workRoots)
        {
            var result = new List<WorkspaceLeftover>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? root in workRoots ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(root) || !seen.Add(SafeFullPath(root)))
                {
                    continue;
                }

                result.AddRange(Scan(root));
            }

            result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

            return result;
        }

        /// <summary>量一个残留目录的体积（量不出来就标记，不假装是 0）。</summary>
        private static WorkspaceLeftover Measure(string directory, string workRoot)
        {
            int fileCount = 0;
            long totalBytes = 0;
            bool failed = false;
            DateTime lastWrite = DateTime.MinValue;

            try
            {
                lastWrite = Directory.GetLastWriteTimeUtc(directory);

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);

                        fileCount++;
                        totalBytes += info.Length;

                        if (info.LastWriteTimeUtc > lastWrite)
                        {
                            lastWrite = info.LastWriteTimeUtc;
                        }
                    }
                    catch
                    {
                        // 单个文件量不出来（占用 / 路径过长）：继续量别的，但整体标记成"量不准"。
                        failed = true;
                    }
                }
            }
            catch
            {
                failed = true;
            }

            return new WorkspaceLeftover
            {
                DirectoryPath = directory,
                RootDirectory = workRoot,
                Name = Path.GetFileName(directory),
                FileCount = fileCount,
                TotalBytes = totalBytes,
                LastWriteTimeUtc = lastWrite == DateTime.MinValue ? DateTime.UtcNow : lastWrite,
                MeasurementFailed = failed
            };
        }

        /// <summary>
        /// 删掉指定的几个残留目录（**只在用户确认之后调用**）。
        ///
        /// <para>每个目录都要过容器内校验；失败只记结果、不抛异常。传进来的路径哪怕只有一个越界，
        /// 也只是那一个不删，其余照常按用户的选择处理 —— 这与"越界就整体停手"不同：
        /// 越界的那个**绝不动**是红线，而用户勾了别的、别的也确实在工作区里，没有理由不删。</para>
        /// </summary>
        public static IReadOnlyList<WorkspaceCleanupOutcome> Cleanup(
            string? workRoot,
            IEnumerable<string>? directories)
        {
            var outcomes = new List<WorkspaceCleanupOutcome>();

            if (string.IsNullOrWhiteSpace(workRoot) || directories == null)
            {
                return outcomes;
            }

            string root = SafeFullPath(workRoot);

            if (root.Length == 0)
            {
                return outcomes;
            }

            foreach (string? directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                outcomes.Add(DeleteOne(root, directory!));
            }

            return outcomes;
        }

        private static WorkspaceCleanupOutcome DeleteOne(string root, string directory)
        {
            string full = SafeFullPath(directory);

            if (full.Length == 0)
            {
                return new WorkspaceCleanupOutcome
                {
                    DirectoryPath = directory,
                    Deleted = false,
                    Message = "路径形状不合法，未删除任何东西"
                };
            }

            /*
             * 容器内校验（红线，AGENTS.md §6 第 13 条）：
             * ① 必须在工作区根**之下**；② 必须是根**直属**子目录（更深的层级不是我们造的那种目录）。
             * 两条都过不了就一个字节都不动 —— 工作区清理永远不会碰到工作区之外的东西。
             */
            string parent = SafeParent(full);

            if (parent.Length == 0
                || !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            {
                return new WorkspaceCleanupOutcome
                {
                    DirectoryPath = directory,
                    Deleted = false,
                    Message = $"不是工作区根目录的直属子目录，未删除：{directory}"
                };
            }

            if (!Directory.Exists(full))
            {
                // 本来就不在了（用户自己删过 / 上一次清过）：算成功，别报成失败。
                return new WorkspaceCleanupOutcome
                {
                    DirectoryPath = directory,
                    Deleted = true,
                    Message = "目录已不存在（可能已经被清理过）"
                };
            }

            try
            {
                Directory.Delete(full, recursive: true);

                return new WorkspaceCleanupOutcome
                {
                    DirectoryPath = directory,
                    Deleted = true,
                    Message = "已清理"
                };
            }
            catch (Exception ex)
            {
                return new WorkspaceCleanupOutcome
                {
                    DirectoryPath = directory,
                    Deleted = false,
                    Message = $"清理失败（{ex.Message}），目录保留：{directory}"
                };
            }
        }

        /// <summary>
        /// 把扫描结论写成一句给用户看的话（**措辞唯一来源**，界面与日志都引它）。
        /// </summary>
        public static string Describe(IReadOnlyList<WorkspaceLeftover>? leftovers)
        {
            int count = leftovers?.Count ?? 0;

            return FormatSize(TotalBytesOf(leftovers))
                   + (count > 0 ? $"，{count} 个目录" : string.Empty);
        }

        /// <summary>这几个残留一共占多少字节。</summary>
        public static long TotalBytesOf(IReadOnlyList<WorkspaceLeftover>? leftovers)
        {
            if (leftovers == null)
            {
                return 0;
            }

            long total = 0;

            foreach (WorkspaceLeftover item in leftovers)
            {
                total += item.TotalBytes;
            }

            return total;
        }

        /// <summary>字节数写成人话（GB / MB / KB / 字节）。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 字节";
            }

            const double Kb = 1024d;

            if (bytes < Kb)
            {
                return bytes + " 字节";
            }

            if (bytes < Kb * Kb)
            {
                return (bytes / Kb).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " KB";
            }

            if (bytes < Kb * Kb * Kb)
            {
                return (bytes / (Kb * Kb)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " MB";
            }

            return (bytes / (Kb * Kb * Kb)).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) + " GB";
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                // TrimEndingDirectorySeparator 而不是 TrimEnd：盘根（"E:\"）不能被削成 "E:"。
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeParent(string fullPath)
        {
            try
            {
                return Path.GetDirectoryName(fullPath) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
