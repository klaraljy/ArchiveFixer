using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{
    /// <summary>一个第一层子目录被跳过的原因。</summary>
    public enum EmptyFolderSkipReason
    {
        /// <summary>没有跳过。</summary>
        None = 0,

        /// <summary>没有传"用户已确认"标志 —— 默认不删（结果里会列出本来会删哪些）。</summary>
        NotConfirmed,

        /// <summary>任意层级存在文件（含空文件），必须保留。</summary>
        HasFiles,

        /// <summary>子树里有符号链接 / 联接点，不跟着删。</summary>
        IsReparsePoint,

        /// <summary>读不到目录内容（权限不足 / 被占用），无法确认"没有文件"，保守保留。</summary>
        NoPermission,

        /// <summary>越出了给定的根目录（容器内校验没过）。</summary>
        OutsideRoot,

        /// <summary>删除动作失败（被占用 / 回收站不可用 / 系统拒绝）。</summary>
        DeleteFailed
    }

    /// <summary>一个被跳过的第一层子目录：路径 + 原因 + 说明。</summary>
    public sealed class EmptyFolderSkip
    {
        public string Path { get; init; } = string.Empty;

        public EmptyFolderSkipReason Reason { get; init; }

        /// <summary>中文说明，可直接给用户看。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 空文件夹清理的选项。
    ///
    /// 默认值按 docs/输出与整理模型.md §5：**默认关**（调用方决定要不要执行）、
    /// **必须二次确认**（<see cref="UserConfirmed"/> 默认 false）、**默认移入回收站**。
    /// </summary>
    public sealed class EmptyFolderCleanOptions
    {
        /// <summary>作用范围：这个根目录的**直接子目录**（递归判断内部有没有文件），不得越出。</summary>
        public string RootDirectory { get; init; } = string.Empty;

        /// <summary>用户是否已确认。<b>默认 false = 一个都不删</b>，只返回"本来会删哪些"。</summary>
        public bool UserConfirmed { get; init; }

        /// <summary>删除档位，默认回收站。</summary>
        public DeleteMode Mode { get; init; } = DeleteMode.RecycleBin;

        /// <summary>写进删除日志的理由。</summary>
        public string Reason { get; init; } = "空文件夹清理";
    }

    /// <summary>空文件夹清理的结果。</summary>
    public sealed class EmptyFolderCleanResult
    {
        public string RootDirectory { get; init; } = string.Empty;

        /// <summary>是否成功读到了根目录并完成了判定（根目录不存在 / 读不到时为 false）。</summary>
        public bool Scanned { get; init; }

        /// <summary>是否真的执行过删除（未确认 / 没有候选时为 false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        /// <summary>判定为"任意层级都没有文件"的第一层子目录（不管最后删没删，都在这里）。</summary>
        public IReadOnlyList<string> EmptyTrees { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> DeletedDirectories { get; init; } = Array.Empty<string>();

        public IReadOnlyList<EmptyFolderSkip> Skipped { get; init; } = Array.Empty<EmptyFolderSkip>();

        /// <summary>彻底删除真正释放的字节数。</summary>
        public long FreedBytes { get; init; }

        /// <summary>移入回收站的字节数（未真正释放空间）。</summary>
        public long RecycledBytes { get; init; }

        /// <summary>本次删除动作产生的日志（由 <see cref="RecycleBinService"/> 统一产出）。</summary>
        public DeleteResult? DeleteResult { get; init; }

        /// <summary>一句话结论，可直接进日志 / 提示框。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 空文件夹清理 —— 原 <c>删除空文件夹.bat</c> 的产品化（docs/输出与整理模型.md §5）。
    ///
    /// 原脚本的语义（逐字对照过 bat）：
    /// <code>
    /// for /d %%D in (*) do (
    ///     dir /a-d /b /s "%%D\*" >nul 2>nul
    ///     if errorlevel 1 rd /s /q "%%D"
    /// )
    /// </code>
    /// 即：只看**当前目录的第一层子目录**；对它做一次"递归列文件"，
    /// **一个文件都没有**（只剩空目录树）就整棵删掉；任意层级只要有一个文件就保留。
    /// 本类保持同一语义，包括"空文件也算文件"（bat 的 <c>dir /a-d</c> 与文件大小无关）。
    ///
    /// 与 bat 的三处差异，都是有意的：
    /// ① 默认**移入回收站**而不是 <c>rd /s /q</c>（§5：默认关 + 二次确认 + 回收站）；
    /// ② 没有确认标志时**一个都不删**，只返回"本来会删哪些"，让界面先给用户看；
    /// ③ 越界、读不到、含链接的目录**一律保留**并说明原因（bat 会照删）。
    ///
    /// ⚠ 作用域（用户 2026-09-22 拍板后的口径）：根固定是**当前任务的输出目录**。
    /// 其余物删除要按包收窄（共享输出目录下只删自己那一份），但空文件夹清理按定义就是"扫这个根"，
    /// 而且只删"任意层级都没有文件"的目录（不含任何文件），所以它保持"根 = 任务输出目录"不变。
    /// 共享模式下这一点会在确认框里写明（见 <c>MainViewModel.BuildSharedScopeNote</c>）。
    ///
    /// 安全上不做第二套判定：真正动手的是 <see cref="RecycleBinService"/>，
    /// 根内校验、根本身 / 驱动器根、符号链接与联接点、确认标志、删除日志全部走它那一套。
    ///
    /// ⚠ 注入约定：如果传入 <see cref="RecycleBinService"/>，请让它用同一个
    /// <see cref="IDeleteFileSystemProbe"/>（构造本类时也传同一个），否则"扫描看到的"与"删除时校验的"
    /// 会是两个世界（单测里尤其明显）。
    /// </summary>
    public sealed class EmptyFolderCleaner
    {
        private readonly RecycleBinService _deleteService;
        private readonly IDeleteFileSystemProbe _probe;

        public EmptyFolderCleaner(RecycleBinService? deleteService = null, IDeleteFileSystemProbe? probe = null)
        {
            _probe = probe ?? WindowsDeleteFileSystemProbe.Instance;
            _deleteService = deleteService ?? new RecycleBinService(probe: _probe);
        }

        /// <summary>
        /// 清理 <see cref="EmptyFolderCleanOptions.RootDirectory"/> 下"任意层级都没有文件"的第一层子目录。
        /// 本方法不抛异常：它跑在批处理收尾阶段，一次抛出会把整批带崩。
        /// </summary>
        public EmptyFolderCleanResult Clean(EmptyFolderCleanOptions? options)
        {
            try
            {
                return CleanCore(options);
            }
            catch (Exception ex)
            {
                return new EmptyFolderCleanResult
                {
                    RootDirectory = options?.RootDirectory ?? string.Empty,
                    Scanned = false,
                    Message = $"空文件夹清理出现意外错误，未删除任何内容：{ex.Message}"
                };
            }
        }

        private EmptyFolderCleanResult CleanCore(EmptyFolderCleanOptions? options)
        {
            if (options == null)
            {
                return Failed(string.Empty, "没有有效的清理选项，未删除任何内容");
            }

            if (string.IsNullOrWhiteSpace(options.RootDirectory))
            {
                return Failed(string.Empty, "未指定根目录，未删除任何内容");
            }

            string rootFull = SafePathHelper.GetFullPathSafe(options.RootDirectory);
            FileAttributes? rootAttributes = _probe.TryGetAttributes(rootFull);

            if (rootAttributes == null)
            {
                return Failed(rootFull, $"根目录不存在或读不到属性（{rootFull}），未删除任何内容");
            }

            if ((rootAttributes.Value & FileAttributes.Directory) == 0)
            {
                return Failed(rootFull, $"根目录不是一个目录（{rootFull}），未删除任何内容");
            }

            if (!_probe.TryListChildDirectories(rootFull, out IReadOnlyList<string> children))
            {
                return Failed(rootFull, $"无法读取根目录内容（权限不足或被占用）：{rootFull}，未删除任何内容");
            }

            var emptyTrees = new List<string>();
            var skipped = new List<EmptyFolderSkip>();

            foreach (string child in children)
            {
                InspectChild(rootFull, child, emptyTrees, skipped);
            }

            if (emptyTrees.Count == 0)
            {
                return new EmptyFolderCleanResult
                {
                    RootDirectory = rootFull,
                    Scanned = true,
                    Attempted = false,
                    Skipped = skipped,
                    Message = children.Count == 0
                        ? "根目录下没有子目录，没有可清理的内容"
                        : $"没有\"任意层级都没有文件\"的子目录，{skipped.Count} 个子目录被保留"
                };
            }

            // 默认不删：没有确认标志时只报告"本来会删哪些"（调用方拿去做二次确认）。
            if (!options.UserConfirmed)
            {
                foreach (string tree in emptyTrees)
                {
                    skipped.Add(new EmptyFolderSkip
                    {
                        Path = tree,
                        Reason = EmptyFolderSkipReason.NotConfirmed,
                        Message = "任意层级都没有文件，可以删除，但本次没有获得用户确认，已保留"
                    });
                }

                return new EmptyFolderCleanResult
                {
                    RootDirectory = rootFull,
                    Scanned = true,
                    Attempted = false,
                    EmptyTrees = emptyTrees,
                    Skipped = skipped,
                    Message = $"发现 {emptyTrees.Count} 个\"任意层级都没有文件\"的子目录，但未获用户确认，一个都没有删除"
                };
            }

            return DeleteEmptyTrees(options, rootFull, emptyTrees, skipped);
        }

        /// <summary>判定单个第一层子目录，命中"任意层级都没有文件"就记进候选。</summary>
        private void InspectChild(
            string rootFull,
            string child,
            List<string> emptyTrees,
            List<EmptyFolderSkip> skipped)
        {
            string childFull = SafePathHelper.GetFullPathSafe(child);

            /*
             * 容器内校验：一个都不能越出给定的根（复用 ArchivePathGuard，不另写一套前缀比较）。
             * 先给 outsideReason 一个初值，是因为"目标是根本身"这一支走的是短路左侧，
             * 此时 IsInsideRoot 根本不会被调用（也就不会给它赋值）。
             */
            string outsideReason = "目标是根目录本身";

            bool isInsideRoot =
                !string.Equals(childFull, rootFull, StringComparison.OrdinalIgnoreCase) &&
                ArchivePathGuard.IsInsideRoot(rootFull, childFull, out outsideReason);

            if (!isInsideRoot)
            {
                skipped.Add(new EmptyFolderSkip
                {
                    Path = childFull,
                    Reason = EmptyFolderSkipReason.OutsideRoot,
                    Message = $"不在给定的根目录之内，已跳过（{outsideReason}）"
                });

                return;
            }

            FileAttributes? attributes = _probe.TryGetAttributes(childFull);

            if (attributes == null)
            {
                skipped.Add(new EmptyFolderSkip
                {
                    Path = childFull,
                    Reason = EmptyFolderSkipReason.NoPermission,
                    Message = "读不到目录属性（权限不足或被占用），无法确认是否为空，已保留"
                });

                return;
            }

            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                skipped.Add(new EmptyFolderSkip
                {
                    Path = childFull,
                    Reason = EmptyFolderSkipReason.IsReparsePoint,
                    Message = "是符号链接 / 联接点，删除它会影响链接指向的别处，已保留"
                });

                return;
            }

            TreeInspection inspection = InspectTree(childFull);

            if (inspection.HasFile)
            {
                skipped.Add(new EmptyFolderSkip
                {
                    Path = childFull,
                    Reason = EmptyFolderSkipReason.HasFiles,
                    Message = $"任意层级存在文件（{inspection.StopPath}），已保留"
                });

                return;
            }

            if (inspection.HasReparsePoint)
            {
                skipped.Add(new EmptyFolderSkip
                {
                    Path = childFull,
                    Reason = EmptyFolderSkipReason.IsReparsePoint,
                    Message = $"目录树里含符号链接 / 联接点（{inspection.StopPath}），不跟着删，已保留"
                });

                return;
            }

            if (!inspection.Determined)
            {
                skipped.Add(new EmptyFolderSkip
                {
                    Path = childFull,
                    Reason = EmptyFolderSkipReason.NoPermission,
                    Message = $"无法读取目录内容（{inspection.StopPath}），不能确认\"没有文件\"，已保留"
                });

                return;
            }

            // 走到这里 = 任意层级都没有文件（只剩空目录树）：与原 bat 的判据一致。
            emptyTrees.Add(childFull);
        }

        /// <summary>递归判断一棵子树里"有没有文件 / 有没有链接 / 能不能判完"。</summary>
        private TreeInspection InspectTree(string directory)
        {
            var inspection = new TreeInspection();
            var pending = new Stack<string>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                if (!_probe.TryListChildren(current, out IReadOnlyList<string> children))
                {
                    inspection.Determined = false;
                    inspection.StopPath = current;
                    return inspection;
                }

                foreach (string child in children)
                {
                    FileAttributes? attributes = _probe.TryGetAttributes(child);

                    if (attributes == null)
                    {
                        inspection.Determined = false;
                        inspection.StopPath = child;
                        return inspection;
                    }

                    /*
                     * 链接先判：一个指向别处的联接点可能"内部有文件"，也可能"内部什么都没有"，
                     * 两种情况都不该由我们去删 —— 前者是别处的数据，后者删掉了链接目标也会让人意外。
                     * 结论先给出来，后面的文件判定就不用做了。
                     */
                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                    {
                        inspection.HasReparsePoint = true;
                        inspection.StopPath = child;
                        return inspection;
                    }

                    if ((attributes.Value & FileAttributes.Directory) != 0)
                    {
                        pending.Push(child);
                        continue;
                    }

                    // 空文件也算文件（与 bat 的 `dir /a-d` 一致：它只看是不是文件，不看大小）。
                    inspection.HasFile = true;
                    inspection.StopPath = child;
                    return inspection;
                }
            }

            return inspection;
        }

        private EmptyFolderCleanResult DeleteEmptyTrees(
            EmptyFolderCleanOptions options,
            string rootFull,
            List<string> emptyTrees,
            List<EmptyFolderSkip> skipped)
        {
            // 真正动手交给 RecycleBinService：根内校验 / 根本身 / 驱动器根 / 链接 / 确认标志 / 日志都在它那一套里。
            var requests = emptyTrees
                .Select(tree => new DeleteRequest(tree, options.Reason))
                .ToList();

            var deleteOptions = new DeleteOptions
            {
                AllowedRoot = rootFull,

                // 到这里说明调用方已经确认过了（CleanCore 里没确认的走的是另一条路）。
                UserConfirmed = true,
                Mode = options.Mode,
                Reason = options.Reason
            };

            DeleteResult deleteResult = _deleteService.Delete(requests, deleteOptions);

            var deleted = new List<string>();

            foreach (DeleteOutcome outcome in deleteResult.Outcomes)
            {
                if (outcome.Success)
                {
                    deleted.Add(outcome.Path);
                    continue;
                }

                skipped.Add(new EmptyFolderSkip
                {
                    Path = outcome.Path,
                    Reason = EmptyFolderSkipReason.DeleteFailed,
                    Message = outcome.Message
                });
            }

            string modeText = options.Mode == DeleteMode.Permanent ? "彻底删除" : "移入回收站";

            return new EmptyFolderCleanResult
            {
                RootDirectory = rootFull,
                Scanned = true,
                Attempted = true,
                EmptyTrees = emptyTrees,
                DeletedDirectories = deleted,
                Skipped = skipped,
                FreedBytes = deleteResult.FreedBytes,
                RecycledBytes = deleteResult.RecycledBytes,
                DeleteResult = deleteResult,
                Message = deleted.Count == 0
                    ? $"发现 {emptyTrees.Count} 个空目录树，但一个都没删成功（原因见跳过清单）"
                    : $"已{modeText} {deleted.Count} 个空目录树" +
                      (deleteResult.FailureCount > 0 ? $"，{deleteResult.FailureCount} 个失败（原因见跳过清单）" : string.Empty)
            };
        }

        private static EmptyFolderCleanResult Failed(string rootFull, string message)
        {
            return new EmptyFolderCleanResult
            {
                RootDirectory = rootFull,
                Scanned = false,
                Attempted = false,
                Message = message
            };
        }

        /// <summary>一棵子树的判定结果。</summary>
        private sealed class TreeInspection
        {
            /// <summary>是否完整判完了（读不到某个目录时为 false）。</summary>
            public bool Determined { get; set; } = true;

            /// <summary>任意层级是否存在文件。</summary>
            public bool HasFile { get; set; }

            /// <summary>是否存在符号链接 / 联接点。</summary>
            public bool HasReparsePoint { get; set; }

            /// <summary>判定停在哪一条（写进跳过原因，方便用户核对）。</summary>
            public string StopPath { get; set; } = string.Empty;
        }
    }
}
