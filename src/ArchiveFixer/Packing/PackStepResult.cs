using System;

namespace ArchiveFixer.Packing
{
    /// <summary>一个打包步骤（起一次外部进程）的结论。</summary>
    public sealed class PackStepResult
    {
        public bool Success { get; init; }

        public bool Cancelled { get; init; }

        public bool TimedOut { get; init; }

        public int ExitCode { get; init; }

        /// <summary>标准输出（**已经过脱敏**：密码与 <c>-p</c> / <c>-hp</c> 开关都不会出现在这里）。</summary>
        public string StandardOutput { get; init; } = string.Empty;

        /// <summary>标准错误（同样已脱敏）。</summary>
        public string StandardError { get; init; } = string.Empty;

        /// <summary>给用户看的一句话（成功时为空）。</summary>
        public string Message { get; init; } = string.Empty;

        public TimeSpan Elapsed { get; init; }

        /// <summary>两路输出拼起来（诊断用）。</summary>
        public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
            ? StandardOutput
            : StandardOutput + Environment.NewLine + StandardError;
    }
}
