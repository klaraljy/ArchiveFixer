using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 两档删除的统一入口（AGENTS.md §9.5、docs/输出与整理模型.md §3.2 / §5）。
    ///
    /// 这个类是全项目**唯一**真正执行"删除用户文件"的地方（源包清理另有 SourceCleanupService），
    /// 所以安全边界全部写死在这里，而且都走 <see cref="DeleteSafetyGuard"/>：
    /// 根内 → 不是根本身 / 驱动器根 → 路径与子树里没有符号链接 / 联接点 → 调用方已确认 → 每一步写日志。
    ///
    /// 两条"绝不做"：
    /// ① 回收站不可用**绝不**降级成永久删除（返回失败 + 原因，由上层决定怎么办）；
    /// ② 目标不存在 / 系统报成功但目标还在，都不算成功。
    ///
    /// 单个目标失败不影响其余目标（AGENTS.md §6 第 9 条），删除跑在批处理收尾阶段，
    /// 一次抛异常把整批带崩的代价比多删几次大得多。
    /// </summary>
    public sealed class RecycleBinService
    {
        private readonly IDeleteExecutor _executor;
        private readonly IDeleteLogSink? _logSink;
        private readonly IDeleteFileSystemProbe _probe;

        /// <param name="executor">真正动手的实现；默认走 Shell 回收站 + .NET 彻底删除。</param>
        /// <param name="logSink">删除日志落点；不传时日志只在 <see cref="DeleteResult.LogEntries"/> 里返回。</param>
        /// <param name="probe">安全检查用的文件系统探针；单测可注入假探针。</param>
        public RecycleBinService(
            IDeleteExecutor? executor = null,
            IDeleteLogSink? logSink = null,
            IDeleteFileSystemProbe? probe = null)
        {
            _executor = executor ?? new ShellDeleteExecutor();
            _logSink = logSink;
            _probe = probe ?? WindowsDeleteFileSystemProbe.Instance;
        }

        /// <summary>删除单个目标。</summary>
        public DeleteResult Delete(DeleteRequest? request, DeleteOptions? options)
        {
            if (request == null)
            {
                return Delete(Array.Empty<DeleteRequest>(), options);
            }

            return Delete(new[] { request }, options);
        }

        /// <summary>
        /// 批量删除。<paramref name="requests"/> 里每一个都会走完整的安全前置并留下一条日志，
        /// 被拒绝的也留日志（"为什么没删"和"删了什么"一样重要）。
        /// </summary>
        public DeleteResult Delete(IEnumerable<DeleteRequest>? requests, DeleteOptions? options)
        {
            var outcomes = new List<DeleteOutcome>();
            var logEntries = new List<DeleteLogEntry>();

            if (requests == null)
            {
                return BuildResult(options, outcomes, logEntries, "没有要删除的目标");
            }

            // options 为空 = 没有允许根、没有确认标志：按"请求参数不成立"逐个拒绝，不是静默跳过。
            if (options == null)
            {
                foreach (DeleteRequest request in requests)
                {
                    if (request == null)
                    {
                        continue;
                    }

                    var outcome = new DeleteOutcome
                    {
                        Path = request.Path,
                        Reason = request.Reason,
                        Success = false,
                        Mode = DeleteMode.RecycleBin,
                        BlockReason = DeleteBlockReason.InvalidRequest,
                        Message = "没有有效的删除选项（缺少允许根 / 用户确认标志），已拒绝删除"
                    };

                    outcomes.Add(outcome);
                    WriteLog(logEntries, outcome, null);
                }

                return BuildResult(null, outcomes, logEntries, "没有有效的删除选项，已拒绝全部删除");
            }

            foreach (DeleteRequest request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                DeleteOutcome outcome = DeleteOne(request, options);

                outcomes.Add(outcome);
                WriteLog(logEntries, outcome, options);
            }

            return BuildResult(options, outcomes, logEntries, null);
        }

        /// <summary>只做安全检查 + 度量，不删除。给"删之前先让用户看清单"用。</summary>
        public DeleteSafetyDecision Evaluate(string? targetPath, DeleteOptions? options)
        {
            if (options == null)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.InvalidRequest,
                    "没有有效的删除选项（缺少允许根 / 用户确认标志），已拒绝删除");
            }

            try
            {
                return DeleteSafetyGuard.Evaluate(
                    options.AllowedRoot,
                    targetPath,
                    options.UserConfirmed,
                    _probe);
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("回收站", "降级处理", ex);
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.DeleteFailed,
                    $"安全检查时出现意外错误：{ex.Message}");
            }
        }

        /// <summary>
        /// 只度量、不删除：条目数与总大小。
        ///
        /// 给 §3.2 的确认框用 —— 那里必须在动手前"列出路径数量与总大小"，
        /// 而真正删除时算出来的数字（<see cref="DeleteOutcome.TotalBytes"/>）事后才拿得到。
        /// 度量不出来时 <see cref="DeleteMeasurement.Determined"/> 为 false（数字宁可没有，也不能是编的）。
        /// </summary>
        public DeleteMeasurement MeasureTarget(string? path)
        {
            try
            {
                return Measure(path ?? string.Empty);
            }
            catch
            {
                return new DeleteMeasurement { Determined = false };
            }
        }

        /// <summary>
        /// 删除单个目标。外面这层 try 是**最后一道保险**：本方法跑在批处理收尾阶段，
        /// 任何意外（注入的探针实现抛异常、文件系统调用抛异常）都只能变成"这一项失败"，
        /// 不能让整批清理跟着崩（AGENTS.md §6 第 9 条）。
        /// </summary>
        private DeleteOutcome DeleteOne(DeleteRequest request, DeleteOptions options)
        {
            try
            {
                return DeleteOneCore(request, options);
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("回收站", "降级处理", ex);
                return Failure(
                    request.Path,
                    string.IsNullOrWhiteSpace(request.Reason) ? options.Reason : request.Reason,
                    options.Mode,
                    DeleteBlockReason.DeleteFailed,
                    $"删除过程中出现意外错误：{ex.Message}");
            }
        }

        private DeleteOutcome DeleteOneCore(DeleteRequest request, DeleteOptions options)
        {
            string reason = string.IsNullOrWhiteSpace(request.Reason) ? options.Reason : request.Reason;

            // ① 安全前置：一条不过就到此为止（连"文件还在不在"都不去碰，保证零副作用）。
            DeleteSafetyDecision decision = Evaluate(request.Path, options);

            if (!decision.IsAllowed)
            {
                return Failure(request.Path, reason, options.Mode, decision.Reason, decision.Message);
            }

            // ② 度量：日志要求报"条目数 + 总大小"，必须在删除前量（删完再问就永远是 0）。
            DeleteMeasurement measurement = Measure(request.Path);

            if (!measurement.Determined)
            {
                return Failure(
                    request.Path,
                    reason,
                    options.Mode,
                    DeleteBlockReason.SafetyProbeUnavailable,
                    "无法完整度量目标（读不到部分目录内容），删除日志会缺少条目数 / 总大小，已拒绝删除");
            }

            // ③ 动手。
            try
            {
                if (options.Mode == DeleteMode.Permanent)
                {
                    _executor.DeletePermanently(request.Path, measurement.IsDirectory);
                }
                else
                {
                    RecycleAttemptResult attempt = _executor.TryMoveToRecycleBin(
                        request.Path,
                        measurement.IsDirectory,
                        out string recycleMessage);

                    if (attempt != RecycleAttemptResult.Recycled)
                    {
                        DeleteBlockReason blockReason = attempt == RecycleAttemptResult.Unavailable
                            ? DeleteBlockReason.RecycleBinUnavailable
                            : DeleteBlockReason.DeleteFailed;

                        return Failure(request.Path, reason, options.Mode, blockReason, recycleMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("回收站", "降级处理", ex);
                return Failure(
                    request.Path,
                    reason,
                    options.Mode,
                    DeleteBlockReason.DeleteFailed,
                    $"删除失败：{ex.Message}");
            }

            // ④ 后校验：系统说成功但目标还在，不算成功（部分成功不得显示为成功，AGENTS.md §6 第 6 条）。
            if (_probe.TryGetAttributes(SafePathHelper.GetFullPathSafe(request.Path)) != null)
            {
                return Failure(
                    request.Path,
                    reason,
                    options.Mode,
                    DeleteBlockReason.TargetStillExists,
                    "删除后目标仍然存在，按失败处理（不假装删掉了）");
            }

            bool permanent = options.Mode == DeleteMode.Permanent;

            return new DeleteOutcome
            {
                Path = request.Path,
                Reason = reason,
                Success = true,
                Mode = options.Mode,
                BlockReason = DeleteBlockReason.None,
                Message = permanent ? "已彻底删除（不可恢复）" : "已移入回收站（可从回收站还原）",
                EntryCount = measurement.EntryCount,
                TotalBytes = measurement.TotalBytes,

                // 移入回收站 = 把文件搬进回收站，空间并没有真正释放，所以分开记，不混进"释放字节数"。
                FreedBytes = permanent ? measurement.TotalBytes : 0L,
                RecycledBytes = permanent ? 0L : measurement.TotalBytes
            };
        }

        /// <summary>
        /// 度量目标的条目数与总大小。读不到一律 <see cref="DeleteMeasurement.Determined"/> = false ——
        /// 日志里的数字宁可没有，也不能是编的。
        /// </summary>
        private DeleteMeasurement Measure(string path)
        {
            string fullPath = SafePathHelper.GetFullPathSafe(path);
            FileAttributes? attributes = _probe.TryGetAttributes(fullPath);

            if (attributes == null)
            {
                return new DeleteMeasurement { Determined = false };
            }

            if ((attributes.Value & FileAttributes.Directory) == 0)
            {
                long length = 0;

                try
                {
                    length = new FileInfo(fullPath).Length;
                }
                catch
                {
                    return new DeleteMeasurement { Determined = false };
                }

                return new DeleteMeasurement
                {
                    Determined = true,
                    IsDirectory = false,
                    EntryCount = 1,
                    FileCount = 1,
                    TotalBytes = length
                };
            }

            int entryCount = 0;
            int fileCount = 0;
            long totalBytes = 0;
            bool determined = true;

            var pending = new Stack<string>();
            pending.Push(fullPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                if (!_probe.TryListChildren(current, out IReadOnlyList<string> children))
                {
                    determined = false;
                    continue;
                }

                foreach (string child in children)
                {
                    entryCount++;

                    FileAttributes? childAttributes = _probe.TryGetAttributes(child);

                    if (childAttributes == null)
                    {
                        determined = false;
                        continue;
                    }

                    if ((childAttributes.Value & FileAttributes.Directory) != 0)
                    {
                        pending.Push(child);
                        continue;
                    }

                    fileCount++;

                    try
                    {
                        totalBytes += new FileInfo(child).Length;
                    }
                    catch
                    {
                        determined = false;
                    }
                }
            }

            return new DeleteMeasurement
            {
                Determined = determined,
                IsDirectory = true,
                EntryCount = entryCount,
                FileCount = fileCount,
                TotalBytes = totalBytes
            };
        }

        private static DeleteOutcome Failure(
            string path,
            string reason,
            DeleteMode mode,
            DeleteBlockReason blockReason,
            string message)
        {
            return new DeleteOutcome
            {
                Path = path,
                Reason = reason,
                Success = false,
                Mode = mode,
                BlockReason = blockReason,
                Message = message
            };
        }

        private void WriteLog(List<DeleteLogEntry> logEntries, DeleteOutcome outcome, DeleteOptions? options)
        {
            var entry = new DeleteLogEntry
            {
                Path = outcome.Path,
                Reason = outcome.Reason,
                Mode = outcome.Mode,
                Success = outcome.Success,
                BlockReason = outcome.BlockReason,
                Message = outcome.Message,
                EntryCount = outcome.EntryCount,
                TotalBytes = outcome.TotalBytes
            };

            logEntries.Add(entry);

            try
            {
                _logSink?.Write(entry);
            }
            catch
            {
                // 日志写不进去（界面集合跨线程、磁盘满）不该让删除结果变形：
                // 结果本身已经带回了同一批日志条目，调用方照样能落盘。
            }
        }

        private static DeleteResult BuildResult(
            DeleteOptions? options,
            List<DeleteOutcome> outcomes,
            List<DeleteLogEntry> logEntries,
            string? overrideMessage)
        {
            var failureReasons = outcomes
                .Where(outcome => !outcome.Success)
                .Select(outcome => string.IsNullOrWhiteSpace(outcome.Path)
                    ? outcome.Message
                    : $"{outcome.Path}：{outcome.Message}")
                .ToList();

            int successCount = outcomes.Count(outcome => outcome.Success);
            int failureCount = outcomes.Count - successCount;
            long freedBytes = outcomes.Sum(outcome => outcome.FreedBytes);
            long recycledBytes = outcomes.Sum(outcome => outcome.RecycledBytes);

            return new DeleteResult
            {
                Mode = options?.Mode ?? DeleteMode.RecycleBin,
                AllowedRoot = options?.AllowedRoot ?? string.Empty,
                Outcomes = outcomes,
                LogEntries = logEntries,
                FailureReasons = failureReasons,
                Message = overrideMessage ?? BuildMessage(successCount, failureCount, freedBytes, recycledBytes)
            };
        }

        private static string BuildMessage(int successCount, int failureCount, long freedBytes, long recycledBytes)
        {
            if (successCount == 0 && failureCount == 0)
            {
                return "没有要删除的目标";
            }

            var parts = new List<string>
            {
                successCount > 0
                    ? $"成功 {successCount} 项（彻底删除释放 {freedBytes} 字节，移入回收站 {recycledBytes} 字节；" +
                      "回收站里的文件并未真正释放空间）"
                    : "没有成功删除任何目标"
            };

            if (failureCount > 0)
            {
                parts.Add($"失败 {failureCount} 项（原因见失败清单，被拒绝的目标一个字节都没有动）");
            }

            return string.Join("；", parts);
        }
    }
}
