using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 引擎操作的统一结果（成功/失败、可读原因、错误分类、耗时）。
    ///
    /// 为什么放在 Engines 而不是 Models：
    /// 它是"引擎调用"的返回类型，GUI / 调度 / 递归只应该看到这个，而不是 7-Zip 的私有结果对象。
    /// 新增引擎时复用同一个结果类型，核心模块就不用改。
    ///
    /// 注意：<see cref="UsedPasswordMasked"/> 只允许放脱敏后的占位符，**绝不能放明文密码**。
    /// </summary>
    public class ArchiveOperationResult
    {
        public bool Success { get; set; }

        public int ExitCode { get; set; }

        public string StandardOutput { get; set; } = string.Empty;

        public string StandardError { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public string DetectedErrorType { get; set; } = "None";

        public string UsedPasswordMasked { get; set; } = string.Empty;

        /// <summary>
        /// **这次到底是哪个引擎干的活**（不变量 14：结果必须能追到具体任务与具体引擎）。
        ///
        /// <para>
        /// 为什么要落在结果对象上，而不是"报告时再去问一遍注册表"：
        /// 报告那一刻的注册表可能已经变了（用户改了优先级、卸载了 WinRAR、换了自选路径），
        /// 而"这个包是被谁解开的"是**已经发生的事实**。由真正执行的那个引擎在返回前盖戳，
        /// 才是可信的溯源；失败清单与正常路径读的是同一份信息。
        /// </para>
        ///
        /// <para>两个字段由引擎自己填（见 <c>SevenZipEngine</c> / <c>UnRarEngine</c>），
        /// 手写 <c>ArchiveOperationResult</c> 的地方（含测试的假引擎）留空即为"未知引擎"。</para>
        /// </summary>
        public string EngineId { get; set; } = string.Empty;

        /// <summary>执行这次操作的引擎版本；取不到时是 <c>unknown</c>，**不编造**。</summary>
        public string EngineVersion { get; set; } = string.Empty;

        /// <summary>执行这次操作的引擎显示名（报告里用；为空时不显示名字）。</summary>
        public string EngineDisplayName { get; set; } = string.Empty;

        /// <summary>把三个引擎字段整理成报告口径的 <see cref="EngineIdentity"/>。</summary>
        public EngineIdentity ToEngineIdentity()
        {
            if (string.IsNullOrWhiteSpace(EngineId))
            {
                return EngineIdentityResolver.Unavailable;
            }

            return new EngineIdentity
            {
                EngineId = EngineId,
                DisplayName = EngineDisplayName ?? string.Empty,
                Version = EngineVersion ?? string.Empty,
                IsAvailable = true
            };
        }

        /// <summary>把"这次是哪个引擎干的"盖到结果上（引擎在返回前调用一次）。</summary>
        public ArchiveOperationResult StampEngine(string? engineId, string? displayName, string? version)
        {
            EngineId = engineId ?? string.Empty;
            EngineDisplayName = displayName ?? string.Empty;
            EngineVersion = version ?? string.Empty;

            return this;
        }

        public TimeSpan Elapsed { get; set; } = TimeSpan.Zero;

        public string CombinedOutput
        {
            get
            {
                if (string.IsNullOrWhiteSpace(StandardOutput))
                {
                    return StandardError ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(StandardError))
                {
                    return StandardOutput ?? string.Empty;
                }

                return StandardOutput + Environment.NewLine + StandardError;
            }
        }

        public bool IsWrongPassword => DetectedErrorType == "WrongPassword";

        /// <summary>
        /// 这次是"**部分完成**"：解出来了一部分，但绝不是成功（AGENTS.md §6 第 6 条）。
        ///
        /// 7-Zip 在"一部分文件解出来、一部分失败"时给的是退出码 1（非致命错误），
        /// 这一条就是它落到调用方手里的判定口 —— 界面上"成功"的那一格永远不许吃到它。
        /// 判定用的是状态字符串（不是 7z 的错误类型名）：这样换引擎也不会漏判。
        /// 退出码 → 错误类型 → 状态的映射只在 <c>Engines/SevenZip/</c> 里做（§3.1 禁止项②的边界）。
        /// </summary>
        public bool IsPartiallyCompleted => !Success && Status == StatusText.PartiallyCompleted;

        public bool IsNeedPassword => DetectedErrorType == "NeedPassword";

        public bool IsCorrupted => DetectedErrorType == "CorruptedArchive";

        public bool IsSevenZipMissing => DetectedErrorType == "SevenZipMissing";

        public bool IsCancelled => DetectedErrorType == "Cancelled";

        public bool IsTimedOut => DetectedErrorType == "TimedOut";

        public static ArchiveOperationResult CreateSuccess(
            int exitCode,
            string output,
            string error,
            TimeSpan elapsed,
            string usedPasswordMasked = "")
        {
            return new ArchiveOperationResult
            {
                Success = true,
                ExitCode = exitCode,
                StandardOutput = output ?? string.Empty,
                StandardError = error ?? string.Empty,
                Status = StatusText.Success,
                Message = "操作成功",
                DetectedErrorType = "None",
                UsedPasswordMasked = usedPasswordMasked ?? string.Empty,
                Elapsed = elapsed
            };
        }

        public static ArchiveOperationResult CreateFailure(
            int exitCode,
            string output,
            string error,
            string status,
            string message,
            string detectedErrorType,
            TimeSpan elapsed,
            string usedPasswordMasked = "")
        {
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = exitCode,
                StandardOutput = output ?? string.Empty,
                StandardError = error ?? string.Empty,
                Status = string.IsNullOrWhiteSpace(status) ? StatusText.ProgressFailed : status,
                Message = message ?? string.Empty,
                DetectedErrorType = string.IsNullOrWhiteSpace(detectedErrorType)
                    ? "UnknownError"
                    : detectedErrorType,
                UsedPasswordMasked = usedPasswordMasked ?? string.Empty,
                Elapsed = elapsed
            };
        }

        public static ArchiveOperationResult CreateEngineMissing(string path)
        {
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.SevenZipMissing,
                Message = $"未找到 7-Zip 程序：{path}",
                DetectedErrorType = "SevenZipMissing",
                UsedPasswordMasked = string.Empty,
                Elapsed = TimeSpan.Zero
            };
        }

        public static ArchiveOperationResult CreateCancelled(TimeSpan elapsed)
        {
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = 255,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.Cancelled,
                Message = "用户取消当前任务",
                DetectedErrorType = "Cancelled",
                UsedPasswordMasked = string.Empty,
                Elapsed = elapsed
            };
        }

        public static ArchiveOperationResult CreateTimedOut(TimeSpan elapsed)
        {
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = -3,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.UnknownError,
                Message = "7-Zip 执行超时",
                DetectedErrorType = "TimedOut",
                UsedPasswordMasked = string.Empty,
                Elapsed = elapsed
            };
        }

        public override string ToString()
        {
            return $"{Status}，ExitCode={ExitCode}，ErrorType={DetectedErrorType}，Elapsed={Elapsed:hh\\:mm\\:ss}";
        }
    }
}
