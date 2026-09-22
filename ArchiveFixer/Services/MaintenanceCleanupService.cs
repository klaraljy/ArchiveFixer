using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Services
{
    /// <summary>清理的作用域（两档互相独立，绝不合并成一个开关）。</summary>
    public enum CleanupScope
    {
        /// <summary>当前任务输出目录下的 <c>过程物</c>（规格 §3.2）。</summary>
        ProcessArtifacts = 0,

        /// <summary>当前任务输出根下"任意层级都没有文件"的第一层子目录（规格 §5）。</summary>
        EmptyFolders = 1
    }

    /// <summary>
    /// 清理**预览**：动手之前给用户看的清单（规格 §3.2 要求"列出路径数量与总大小"）。
    ///
    /// <para>
    /// 预览**不删除任何东西**：<see cref="HasTarget"/> 为 false 时连目标都没有，
    /// 调用方应当直接告诉用户"没有可清理的"，而不是弹一个"确定要删 0 项吗"。
    /// </para>
    /// </summary>
    public sealed class CleanupPreview
    {
        public CleanupScope Scope { get; init; }

        /// <summary>被清理的作用域路径（过程物目录 / 输出根）。</summary>
        public string ScopePath { get; init; } = string.Empty;

        /// <summary>有没有可清理的东西（false 时不要进入确认流程）。</summary>
        public bool HasTarget { get; init; }

        /// <summary>将删除的**顶层条目数**（过程物目录算 1 项；空文件夹清理是候选目录数）。</summary>
        public int ItemCount { get; init; }

        /// <summary>条目总数（含子项；空文件夹清理里没有文件所以恒为 0）。</summary>
        public int EntryCount { get; init; }

        /// <summary>总字节数。</summary>
        public long TotalBytes { get; init; }

        /// <summary>能不能量准（读不到时为 false —— 数字宁可没有，也不能是编的）。</summary>
        public bool Determined { get; init; }

        /// <summary>将删除的顶层条目（最多列 <see cref="MaxListedItems"/> 个，其余用"等 N 项"收尾）。</summary>
        public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();

        /// <summary>一句话结论，可直接进日志 / 确认框。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>确认框里最多列几项 —— 列太多会把对话框撑满，用户反而看不清主要信息。</summary>
        public const int MaxListedItems = 10;
    }

    /// <summary>清理**执行**的结论。</summary>
    public sealed class CleanupOutcome
    {
        public CleanupScope Scope { get; init; }

        public DeleteMode Mode { get; init; }

        /// <summary>是否真的执行过删除（未确认 / 没有候选时为 false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        public int SuccessCount { get; init; }

        public int FailureCount { get; init; }

        /// <summary>彻底删除真正释放的字节数。</summary>
        public long FreedBytes { get; init; }

        /// <summary>移入回收站的字节数（未真正释放空间）。</summary>
        public long RecycledBytes { get; init; }

        /// <summary>失败原因清单（"路径：说明"）。</summary>
        public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

        /// <summary>每一步的删除日志（路径 + 理由 + 条目数 + 总大小），调用方原样写进界面/文件日志。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 「清理过程物…」「清理空文件夹…」两个界面入口背后的本体（规格 §3.2 / §5）。
    ///
    /// <para>
    /// 现状是"服务早就写好了，界面上没有任何按钮"：<see cref="RecycleBinService"/>（两档删除 +
    /// 五条安全前置）、<see cref="EmptyFolderCleaner"/>、<see cref="ProcessArtifactLayout"/> 都实现且测过，
    /// 但用户点不到。本类把三者接成界面能直接调的两步：**先预览，再执行**。
    /// </para>
    /// <para>
    /// 四条硬约束（都来自规格，不在这里重新发明）：
    /// ① 作用域**不得越出**：过程物只能是 <c>&lt;任务输出目录&gt;\过程物</c>
    ///    （路径由 <see cref="ProcessArtifactLayout.ResolveArtifactDirectory"/> 给出，唯一来源），
    ///    空文件夹清理的根就是任务输出目录；两者都以 <c>AllowedRoot</c> 交给
    ///    <see cref="DeleteSafetyGuard"/> 做容器内校验；
    /// ② 默认档 = <b>移入回收站</b>；回收站不可用时**拒绝执行**，绝不降级成永久删除
    ///    （<see cref="RecycleBinService"/> 内部就这么做，本类不绕过它）；
    /// ③ 每一件事都写日志（路径 + 理由 + 条目数 + 总大小）；
    /// ④ **本类全是磁盘活**：调用方必须放后台线程（界面只更新状态）。
    /// </para>
    /// <para>
    /// ⚠ 注入约定：构造函数收的 <see cref="IDeleteExecutor"/> / <see cref="IDeleteFileSystemProbe"/>
    /// 会同时给 <see cref="RecycleBinService"/> 与 <see cref="EmptyFolderCleaner"/> 用同一份 ——
    /// "扫描看到的"和"删除时校验的"必须是同一个世界（单测里尤其明显，见 EmptyFolderCleaner 的注释）。
    /// </para>
    /// </summary>
    public sealed class MaintenanceCleanupService
    {
        private readonly IDeleteExecutor _executor;
        private readonly IDeleteFileSystemProbe _probe;

        public MaintenanceCleanupService(
            IDeleteExecutor? executor = null,
            IDeleteFileSystemProbe? probe = null)
        {
            _executor = executor ?? new ShellDeleteExecutor();
            _probe = probe ?? WindowsDeleteFileSystemProbe.Instance;
        }

        /// <summary>清理理由（写进删除日志；两档共用同一句话）。</summary>
        public const string ProcessArtifactReason = "清理过程物（内层归档 / 分卷 / 中间件）";

        public const string EmptyFolderReason = "清理空文件夹（任意层级都没有文件）";

        /// <summary>过程物清理的作用域路径：<c>&lt;输出目录&gt;\过程物</c>；拿不到时返回空串。</summary>
        public static string ResolveProcessArtifactScope(string? outputDirectory)
        {
            return ProcessArtifactLayout.ResolveArtifactDirectory(outputDirectory);
        }

        /// <summary>
        /// 预览过程物清理：条目数 + 总大小 + 顶层条目清单。**不删除任何东西**。
        /// </summary>
        public CleanupPreview PreviewProcessArtifacts(string? outputDirectory)
        {
            string scope = ResolveProcessArtifactScope(outputDirectory);

            if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(scope))
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.ProcessArtifacts,
                    ScopePath = scope,
                    HasTarget = false,
                    Message = "没有可清理的过程物：当前任务还没有输出目录"
                };
            }

            // 容器内校验：过程物目录必须落在输出目录之内（不变量 4 的同一口径，不另写前缀比较）。
            if (!ArchivePathGuard.IsInsideRoot(outputDirectory, scope, out string reason))
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.ProcessArtifacts,
                    ScopePath = scope,
                    HasTarget = false,
                    Message = $"过程物目录不在当前任务的输出目录之内，已拒绝清理 —— {reason}"
                };
            }

            if (!Directory.Exists(scope))
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.ProcessArtifacts,
                    ScopePath = scope,
                    HasTarget = false,
                    Message = $"没有可清理的过程物：{scope} 不存在"
                };
            }

            var items = new List<string>();

            try
            {
                items.AddRange(Directory.GetDirectories(scope).Select(FileNameHelper.GetFileName));
                items.AddRange(Directory.GetFiles(scope).Select(FileNameHelper.GetFileName));
            }
            catch (Exception ex)
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.ProcessArtifacts,
                    ScopePath = scope,
                    HasTarget = false,
                    Message = $"读不了过程物目录（{ex.Message}），已放弃清理：{scope}"
                };
            }

            if (items.Count == 0)
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.ProcessArtifacts,
                    ScopePath = scope,
                    HasTarget = false,
                    Message = $"过程物目录是空的，没有可清理的内容：{scope}"
                };
            }

            // 度量走 RecycleBinService.MeasureTarget：与真正删除时用的是同一段代码，
            // 预览里报的数字和删除日志里的数字不会互相打架。
            DeleteMeasurement measurement = CreateDeleteService().MeasureTarget(scope);

            return new CleanupPreview
            {
                Scope = CleanupScope.ProcessArtifacts,
                ScopePath = scope,

                // 量不准也能删（删除时服务自己会再判一次并拒绝），但预览要如实说"数字不全"。
                HasTarget = true,
                ItemCount = items.Count,
                EntryCount = measurement.EntryCount,
                TotalBytes = measurement.TotalBytes,
                Determined = measurement.Determined,
                Items = items.Take(CleanupPreview.MaxListedItems).ToList(),
                Message = $"将清理过程物：{scope}（顶层 {items.Count} 项，共 {measurement.EntryCount} 个条目 / {measurement.TotalBytes} 字节）"
                          + (measurement.Determined ? string.Empty : "，其中部分内容读不到，数字可能不全")
            };
        }

        /// <summary>
        /// 预览空文件夹清理：候选目录清单（"任意层级都没有文件"的第一层子目录）。**不删除任何东西**。
        /// </summary>
        public CleanupPreview PreviewEmptyFolders(string? rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.EmptyFolders,
                    ScopePath = rootDirectory ?? string.Empty,
                    HasTarget = false,
                    Message = "没有可清理的空文件夹：当前任务还没有输出目录"
                };
            }

            // UserConfirmed = false：这一步只让它"报告本来会删哪些"，一个都不会删。
            EmptyFolderCleanResult scan = CreateEmptyFolderCleaner().Clean(new EmptyFolderCleanOptions
            {
                RootDirectory = rootDirectory,
                UserConfirmed = false,
                Mode = DeleteMode.RecycleBin,
                Reason = EmptyFolderReason
            });

            if (!scan.Scanned || scan.EmptyTrees.Count == 0)
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.EmptyFolders,
                    ScopePath = scan.RootDirectory,
                    HasTarget = false,
                    Message = scan.Message
                };
            }

            // 这些目录树按定义"任意层级都没有文件"，所以总大小恒为 0；条目数仍然量一遍，
            // 让用户在确认框里看到"要删掉多少个空目录层"。
            long totalBytes = 0;
            int entryCount = 0;
            bool determined = true;
            RecycleBinService deleteService = CreateDeleteService();

            foreach (string tree in scan.EmptyTrees)
            {
                DeleteMeasurement measurement = deleteService.MeasureTarget(tree);

                entryCount += Math.Max(measurement.EntryCount, 1);
                totalBytes += measurement.TotalBytes;
                determined &= measurement.Determined;
            }

            return new CleanupPreview
            {
                Scope = CleanupScope.EmptyFolders,
                ScopePath = scan.RootDirectory,
                HasTarget = true,
                ItemCount = scan.EmptyTrees.Count,
                EntryCount = entryCount,
                TotalBytes = totalBytes,
                Determined = determined,
                Items = scan.EmptyTrees.Take(CleanupPreview.MaxListedItems).ToList(),
                Message = $"将清理空文件夹：{scan.RootDirectory} 下有 {scan.EmptyTrees.Count} 个\"任意层级都没有文件\"的子目录" +
                          $"（共 {entryCount} 个目录 / {totalBytes} 字节）"
            };
        }

        /// <summary>
        /// 执行过程物清理。<paramref name="mode"/> 默认档 = 回收站；
        /// <see cref="DeleteMode.Permanent"/> 只能由界面在"红色 + 二次确认"之后传进来。
        /// </summary>
        public CleanupOutcome CleanProcessArtifacts(string? outputDirectory, DeleteMode mode)
        {
            string scope = ResolveProcessArtifactScope(outputDirectory);

            if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(scope))
            {
                return NotAttempted(CleanupScope.ProcessArtifacts, mode, "没有可清理的过程物：当前任务还没有输出目录");
            }

            if (!Directory.Exists(scope))
            {
                return NotAttempted(CleanupScope.ProcessArtifacts, mode, $"没有可清理的过程物：{scope} 不存在");
            }

            DeleteResult result;

            try
            {
                result = CreateDeleteService().Delete(
                    new DeleteRequest(scope, ProcessArtifactReason),
                    new DeleteOptions
                    {
                        // 允许根是**任务输出目录**：过程物目录必须落在它之内，且不得等于它。
                        AllowedRoot = SafePathHelper.GetFullPathSafe(outputDirectory),
                        UserConfirmed = true,
                        Mode = mode,
                        Reason = ProcessArtifactReason
                    });
            }
            catch (Exception ex)
            {
                return NotAttempted(CleanupScope.ProcessArtifacts, mode, "清理过程物时出现意外错误：" + ex.Message);
            }

            return BuildOutcome(CleanupScope.ProcessArtifacts, mode, result);
        }

        /// <summary>执行空文件夹清理（作用域 = 任务输出根，不得越出）。</summary>
        public CleanupOutcome CleanEmptyFolders(string? rootDirectory, DeleteMode mode)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return NotAttempted(CleanupScope.EmptyFolders, mode, "没有可清理的空文件夹：当前任务还没有输出目录");
            }

            EmptyFolderCleanResult result;

            try
            {
                result = CreateEmptyFolderCleaner().Clean(new EmptyFolderCleanOptions
                {
                    RootDirectory = rootDirectory,
                    UserConfirmed = true,
                    Mode = mode,
                    Reason = EmptyFolderReason
                });
            }
            catch (Exception ex)
            {
                return NotAttempted(CleanupScope.EmptyFolders, mode, "清理空文件夹时出现意外错误：" + ex.Message);
            }

            if (!result.Scanned)
            {
                return NotAttempted(CleanupScope.EmptyFolders, mode, result.Message);
            }

            var logLines = new List<string>();

            if (result.DeleteResult != null)
            {
                logLines.AddRange(result.DeleteResult.LogEntries.Select(entry => entry.ToDisplayText()));
            }

            return new CleanupOutcome
            {
                Scope = CleanupScope.EmptyFolders,
                Mode = mode,
                Attempted = result.Attempted,
                SuccessCount = result.DeletedDirectories.Count,
                FailureCount = result.Skipped.Count(skip => skip.Reason == EmptyFolderSkipReason.DeleteFailed),
                FreedBytes = result.FreedBytes,
                RecycledBytes = result.RecycledBytes,
                FailureReasons = result.Skipped
                    .Where(skip => skip.Reason == EmptyFolderSkipReason.DeleteFailed)
                    .Select(skip => $"{skip.Path}：{skip.Message}")
                    .ToList(),
                LogLines = logLines,
                Message = result.Message
            };
        }

        /// <summary>把一次 <see cref="DeleteResult"/> 翻成界面上要的那几样东西。</summary>
        private static CleanupOutcome BuildOutcome(CleanupScope scope, DeleteMode mode, DeleteResult result)
        {
            return new CleanupOutcome
            {
                Scope = scope,
                Mode = mode,
                Attempted = true,
                SuccessCount = result.SuccessCount,
                FailureCount = result.FailureCount,
                FreedBytes = result.FreedBytes,
                RecycledBytes = result.RecycledBytes,
                FailureReasons = result.FailureReasons,
                LogLines = result.LogEntries.Select(entry => entry.ToDisplayText()).ToList(),
                Message = result.Message
            };
        }

        private static CleanupOutcome NotAttempted(CleanupScope scope, DeleteMode mode, string message)
        {
            return new CleanupOutcome
            {
                Scope = scope,
                Mode = mode,
                Attempted = false,
                Message = message
            };
        }

        /// <summary>删除服务：注入项的**唯一**装配处（保证探针与执行器两条路用的是同一份）。</summary>
        private RecycleBinService CreateDeleteService()
        {
            return new RecycleBinService(_executor, logSink: null, probe: _probe);
        }

        private EmptyFolderCleaner CreateEmptyFolderCleaner()
        {
            return new EmptyFolderCleaner(CreateDeleteService(), _probe);
        }
    }
}
