using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;

namespace ArchiveFixer.Storage
{
    /// <summary>危险模式下一次"彻底删其余物"的结论。</summary>
    public sealed class RestPurgeOutcome
    {
        /// <summary>是否真的执行过删除（被门槛挡下 / 没有可删的 → false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        /// <summary>是否**确实释放了空间**（危险模式的全部意义就在这里：净占用基本不变）。</summary>
        public bool Succeeded { get; init; }

        /// <summary>删掉的其余物目录（没删时为空）。</summary>
        public string Directory { get; init; } = string.Empty;

        /// <summary>彻底删除真正释放的字节数。</summary>
        public long FreedBytes { get; init; }

        /// <summary>顶层条目数。</summary>
        public int EntryCount { get; init; }

        /// <summary>一句话结论（进日志与任务详情）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>删除日志（路径 + 理由 + 条目数 + 总大小），调用方原样写进界面/文件日志。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 危险模式（红按钮）的**唯一执行体**：把一个任务自己的 `其余物` **彻底删除**（不进回收站）。
    ///
    /// <para><b>门槛（五条，缺一不可 —— 这是那条红线"失败不删任何东西"的落点）</b>：</para>
    /// <list type="number">
    /// <item><description>任务终态是「解压成功」（<see cref="StatusText.ExtractSuccess"/>）——
    /// 「部分完成」「解压失败」「已取消」「已跳过」一律不动；</description></item>
    /// <item><description>输出校验通过（<see cref="ArchiveTask.IsOutputVerified"/>）；</description></item>
    /// <item><description>没被取消（调用方传进来的 <c>cancelled</c>）；</description></item>
    /// <item><description>`其余物` 目录**是本次真的记下来的那一个**（<see cref="ArchiveTask.RestDirectoryPath"/>）——
    /// 现场重算会在三处算错（内容物那层也叫「其余物」时的 `其余物(1)`、共享根下按包名分的那层、归集把目录整体搬走之后的落点），
    /// 而算错的后果是把**别人的**其余物删掉；</description></item>
    /// <item><description>它落在本任务自己的输出根之内（<see cref="ArchivePathGuard.IsInsideRoot"/>），
    /// 而且路径形状确实是"其余物"（自己就是，或上一级是 —— 共用输出根模式是 `其余物\包名`）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么不用 <c>MaintenanceCleanupService.CleanProcessArtifacts</c></b>：它按任务**现场**解析作用域
    /// （<c>ResolveArtifactScope</c>），而危险模式动作的时刻是"源包刚被搬进其余物之后"——
    /// 那时 <see cref="ArchiveTask.CurrentPath"/> 已经指向其余物里面的新位置，
    /// 现场解析在共享输出根模式下会退化成"整个 `其余物\`"，一删就是同目录里所有包的中间件。
    /// 所以这里只认定稿那一刻记下来的那一条路径（门槛 4），并用"必须在自己的输出根之内 + 形状必须是其余物"
    /// 两道校验兜住（门槛 5），两条都过了才动手。</para>
    ///
    /// <para><b>线程</b>：全是磁盘活，调用方必须放后台线程。</para>
    /// </summary>
    public sealed class RestItemPurger
    {
        private readonly IDeleteExecutor _executor;
        private readonly IDeleteFileSystemProbe _probe;

        public RestItemPurger(IDeleteExecutor? executor = null, IDeleteFileSystemProbe? probe = null)
        {
            _executor = executor ?? new ShellDeleteExecutor();
            _probe = probe ?? WindowsDeleteFileSystemProbe.Instance;
        }

        /// <summary>删除理由（写进删除日志）。必须写清"为什么可以删"与"进了哪里"。</summary>
        public const string DangerModeReason =
            "危险模式：内容物已定稿并按落点策略排好、输出校验通过、未取消 —— 彻底删除本任务的其余物（源包 + 中间件，不进回收站）";

        /// <summary>
        /// 试着删除一个任务的其余物。**任何一条门槛不成立都返回"没动"**，并给出原因（不抛异常）。
        /// </summary>
        /// <param name="task">目标任务。</param>
        /// <param name="cancelled">这一刻是不是已经被取消（用户按了「取消当前」/「停止后续」）。</param>
        public RestPurgeOutcome Purge(ArchiveTask? task, bool cancelled)
        {
            if (task == null)
            {
                return Skip("没有任务，未删除任何东西");
            }

            string name = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            if (cancelled)
            {
                return Skip($"{name}：已取消 —— 其余物一个字节都不删（失败 / 取消 / 校验不通过一律不动）");
            }

            if (!string.Equals(task.Status, StatusText.ExtractSuccess, StringComparison.Ordinal))
            {
                return Skip($"{name}：任务终态是「{task.Status}」而不是「{StatusText.ExtractSuccess}」，其余物一个字节都不删");
            }

            if (!task.IsOutputVerified)
            {
                return Skip($"{name}：输出校验没有通过，其余物一个字节都不删");
            }

            if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                return Skip($"{name}：没记下本次定稿实际用的其余物目录，不敢猜路径，一个字节都不删");
            }

            string directory = SafePathHelper.GetFullPathSafe(task.RestDirectoryPath);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Skip($"{name}：其余物目录不存在（{task.RestDirectoryPath}），没有可删除的内容");
            }

            if (!TryResolveAllowedRoot(task, directory, out string allowedRoot, out string why))
            {
                return Skip($"{name}：{why}，已拒绝删除");
            }

            if (!LooksLikeArtifactDirectory(directory))
            {
                return Skip(
                    $"{name}：{directory} 的形状不像「其余物」目录（既不是 {ProcessArtifactLayout.ArtifactDirectoryName}，"
                    + "上一级也不是），已拒绝删除");
            }

            if (!ArchivePathGuard.IsInsideRoot(allowedRoot, directory, out string outsideReason))
            {
                return Skip($"{name}：其余物目录不在本任务的输出范围之内，已拒绝删除 —— {outsideReason}");
            }

            // logSink 传 null：删除日志由调用方（协调器）原样写进界面与文件日志，
            // 不在这里另开一个落点（两处各写一份会让日志出现两个时间戳）。
            var service = new RecycleBinService(_executor, null, _probe);
            DeleteRequest request = new DeleteRequest(directory, DangerModeReason);

            DeleteResult result;

            try
            {
                result = service.Delete(
                    new[] { request },
                    new DeleteOptions
                    {
                        AllowedRoot = allowedRoot,
                        UserConfirmed = true,
                        Mode = DeleteMode.Permanent,
                        Reason = DangerModeReason
                    });
            }
            catch (Exception ex)
            {
                return Skip($"{name}：删除其余物时出现意外错误（{ex.Message}），源包与中间件都还在");
            }

            var logLines = result.LogEntries.Select(entry => entry.ToDisplayText()).ToList();
            DeleteOutcome? outcome = result.Outcomes.FirstOrDefault();

            if (result.SuccessCount <= 0)
            {
                string failure = result.FailureReasons.Count > 0
                    ? string.Join("；", result.FailureReasons)
                    : result.Message;

                return new RestPurgeOutcome
                {
                    Attempted = true,
                    Succeeded = false,
                    Directory = directory,
                    Message = $"{name}：其余物没能删掉（{failure}）—— 内容物不受影响，但空间没有回来",
                    LogLines = logLines
                };
            }

            return new RestPurgeOutcome
            {
                Attempted = true,
                Succeeded = true,
                Directory = directory,
                FreedBytes = result.FreedBytes,
                EntryCount = outcome?.EntryCount ?? 0,
                Message =
                    $"{name}：危险模式已彻底删除其余物 {directory}"
                    + $"（{outcome?.EntryCount ?? 0} 个条目 / 释放 {TaskSpaceEstimate.FormatSize(result.FreedBytes)}，不进回收站）",
                LogLines = logLines
            };
        }

        /// <summary>
        /// 允许删除的根：本任务的输出目录，或（开了结果归集时）归集之后的落点。
        /// **只认这两个**；都不包含目标目录就拒绝 —— 宁可少删，不可错删。
        /// </summary>
        private static bool TryResolveAllowedRoot(
            ArchiveTask task,
            string directory,
            out string allowedRoot,
            out string why)
        {
            allowedRoot = string.Empty;
            why = string.Empty;

            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(task.CollectedPath))
            {
                candidates.Add(SafePathHelper.GetFullPathSafe(task.CollectedPath));
            }

            if (!string.IsNullOrWhiteSpace(task.OutputPath))
            {
                candidates.Add(SafePathHelper.GetFullPathSafe(task.OutputPath));
            }

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (ArchivePathGuard.IsInsideRoot(candidate, directory, out _))
                {
                    allowedRoot = candidate;
                    return true;
                }
            }

            why = $"其余物目录 {directory} 不在本任务的输出目录（{string.Join(" / ", candidates.Where(c => c.Length > 0))}）之内";
            return false;
        }

        /// <summary>
        /// 路径形状校验：最后一段是「其余物 / 过程物」，**或者**上一级是
        /// （共用输出根模式是 <c>&lt;根&gt;\其余物\&lt;包基名&gt;</c>，最后一段是包名）。
        /// </summary>
        internal static bool LooksLikeArtifactDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (ProcessArtifactLayout.IsArtifactDirectoryName(trimmed))
            {
                return true;
            }

            try
            {
                string? parent = Path.GetDirectoryName(trimmed);

                return !string.IsNullOrWhiteSpace(parent) &&
                       ProcessArtifactLayout.IsArtifactDirectoryName(parent);
            }
            catch
            {
                return false;
            }
        }

        private static RestPurgeOutcome Skip(string message)
        {
            return new RestPurgeOutcome
            {
                Attempted = false,
                Succeeded = false,
                Message = message
            };
        }
    }
}
