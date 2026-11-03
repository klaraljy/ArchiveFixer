using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>一次引擎调用的种类。</summary>
    public enum ExtractionAttemptKind
    {
        List = 0,
        Test = 1,
        Extract = 2
    }

    /// <summary>
    /// 一次引擎尝试的**结构化事实**。阶段 5 影子：先把现有 <see cref="ArchiveOperationResult"/>
    /// 能说清的事实收进来，与旧结果逐项对比；确认无差异后再替换调用方的状态解释。
    /// </summary>
    public sealed class ExtractionAttempt
    {
        public ExtractionAttemptKind Kind { get; init; }

        public bool Success { get; init; }

        public int ExitCode { get; init; }

        public string EngineId { get; init; } = string.Empty;

        public string CommandSummary { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public string ErrorType { get; init; } = "None";

        public string UsedPasswordMasked { get; init; } = string.Empty;

        public TimeSpan Elapsed { get; init; }

        public string ArchivePath { get; init; } = string.Empty;

        public string OutputDirectory { get; init; } = string.Empty;

        /// <summary>这一次尝试实际用的隔离目录；空 = 直接写暂存（旧行为）。</summary>
        public string IsolatedDirectory { get; init; } = string.Empty;

        /// <summary>这次调用是否已经对暂存/目标产生副作用。</summary>
        public bool HasSideEffects { get; init; }

        public int OutputFileCount { get; init; }

        public long OutputBytes { get; init; }

        public IReadOnlyList<string> FailedEntryNames { get; init; } = Array.Empty<string>();

        public static ExtractionAttempt FromResult(
            ArchiveOperationResult result,
            ExtractionAttemptKind kind,
            string? archivePath = null,
            string? outputDirectory = null,
            string? isolatedDirectory = null,
            bool hasSideEffects = true,
            int outputFileCount = 0,
            long outputBytes = 0)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return new ExtractionAttempt
            {
                Kind = kind,
                Success = result.Success,
                ExitCode = result.ExitCode,
                EngineId = result.EngineId ?? string.Empty,
                CommandSummary = result.CommandSummary ?? string.Empty,
                Status = result.Status ?? string.Empty,
                Message = result.Message ?? string.Empty,
                ErrorType = result.DetectedErrorType ?? "None",
                UsedPasswordMasked = result.UsedPasswordMasked ?? string.Empty,
                Elapsed = result.Elapsed,
                ArchivePath = archivePath ?? string.Empty,
                OutputDirectory = outputDirectory ?? string.Empty,
                IsolatedDirectory = isolatedDirectory ?? string.Empty,
                HasSideEffects = hasSideEffects,
                OutputFileCount = outputFileCount,
                OutputBytes = outputBytes,
                FailedEntryNames = result.FailedEntryNames?.ToArray() ?? Array.Empty<string>()
            };
        }

        /// <summary>
        /// 影子对比：只比旧结果能表达的机器事实。返回差异项；空 = 完全一致。
        /// ⛔ 不比较可读文案的“等价改写”，只比逐字字段。
        /// </summary>
        public static IReadOnlyList<string> DescribeDifferences(
            ArchiveOperationResult result,
            ExtractionAttempt attempt)
        {
            var differences = new List<string>();

            if (result == null || attempt == null)
            {
                differences.Add("result/attempt 为空");
                return differences;
            }

            AddIfDiff("Success", result.Success, attempt.Success, differences);
            AddIfDiff("ExitCode", result.ExitCode, attempt.ExitCode, differences);
            AddIfDiff("EngineId", result.EngineId ?? string.Empty, attempt.EngineId, differences);
            AddIfDiff("CommandSummary", result.CommandSummary ?? string.Empty, attempt.CommandSummary, differences);
            AddIfDiff("Status", result.Status ?? string.Empty, attempt.Status, differences);
            AddIfDiff("ErrorType", result.DetectedErrorType ?? "None", attempt.ErrorType, differences);
            AddIfDiff("PasswordMasked", result.UsedPasswordMasked ?? string.Empty, attempt.UsedPasswordMasked, differences);
            AddIfDiff("Elapsed", result.Elapsed, attempt.Elapsed, differences);

            return differences;
        }

        private static void AddIfDiff<T>(
            string name,
            T expected,
            T actual,
            List<string> differences)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                differences.Add($"{name}: old={expected}, new={actual}");
            }
        }
    }
}
