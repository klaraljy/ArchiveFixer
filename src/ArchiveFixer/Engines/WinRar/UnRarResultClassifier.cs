using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// UnRAR 的**错误分类与结果成形**这一块：退出码 + 两路输出 → <see cref="ArchiveOperationResult"/>，
/// 以及"测试 / 解压"两种操作在结果上的最终修饰、几种"没跑起来"的结果。
///
/// <para>这里的判定顺序是既有口径，不要重排：先判成功（只有退出码 0），再判错误类型
/// （<see cref="UnRarOutputParser"/>，纯文本层），最后才拼用户文案
/// （缺卷时把 UnRAR 自己点名的卷号带出来，不变量 7）。</para>
///
/// <para>⛔ UnRAR 文本解析、退出码映射、错误类型字符串只允许待在本命名空间；
/// 本类不拼参数、不起进程、不读管道。</para>
/// </summary>
internal static class UnRarResultClassifier
{
    /// <summary>
    /// 分析一次 UnRAR 调用的结果（运行器唯一的结果出口，便于单测直接喂文本）。
    /// </summary>
    internal static ArchiveOperationResult AnalyzeResult(
        int exitCode,
        string? output,
        string? error,
        string? usedPassword,
        TimeSpan elapsed,
        string? archivePath,
        EngineOperation? operation = null,
        IEnumerable<string>? arguments = null)
    {
        output = PasswordMasker.Sanitize(output);
        error = PasswordMasker.Sanitize(error);

        string combined = UnRarOutputParser.CombineOutput(output, error);
        bool success = UnRarOutputParser.LooksLikeSuccess(exitCode);

        string errorType = success
            ? "None"
            : UnRarOutputParser.DetectErrorType(exitCode, output, error, operation, archivePath);

        string status = UnRarOutputParser.ErrorTypeToTaskStatus(errorType);

        string message;

        if (success)
        {
            message = "操作成功";
        }
        else if (string.Equals(errorType, EngineErrorTypes.VolumeMissing, StringComparison.Ordinal))
        {
            // 缺卷时把 UnRAR 明确点名的卷号带出来（不变量 7 要的"缺哪几个"）。
            // UnRAR 自己写了 "Cannot find volume <路径>"，比 7-Zip 那侧靠文件名猜准得多。
            IReadOnlyList<string> missing = UnRarOutputParser.ExtractMissingVolumeNames(combined);

            message = missing.Count > 0
                ? $"分卷压缩包缺少必要分卷，缺少：{string.Join('、', missing)}。请把同一组分卷放在同一目录后重试。"
                : UnRarOutputParser.ErrorTypeToMessage(errorType, combined);
        }
        else
        {
            message = UnRarOutputParser.ErrorTypeToMessage(errorType, combined);
        }

        return new ArchiveOperationResult
        {
            Success = success,
            ExitCode = exitCode,
            StandardOutput = output ?? string.Empty,
            StandardError = error ?? string.Empty,
            Status = status,
            Message = message,
            DetectedErrorType = errorType,
            UsedPasswordMasked = PasswordMasker.Mask(usedPassword),
            Elapsed = elapsed,
            CommandSummary = UnRarCommandLine.BuildCommandSummary(arguments),

            /*
             * 「哪些条目坏了」与"引擎自报坏了几个" —— 部分完成发布那块功能的地基
             * （见 ArchiveOperationResult.FailedEntryNames 的说明）。
             * ⚠ 中文版 UnRAR 打的是中文 ⇒ 这里可能是空的，调用方必须靠"清单 vs 盘上实际"对账 + 计数闸门。
             */
            FailedEntryNames = UnRarOutputParser.ExtractFailedEntryNames(combined),
            ReportedSubItemErrors = UnRarOutputParser.ExtractTotalErrorCount(combined)
        };
    }

    /// <summary>
    /// 引擎不可用时的落点（路径由 <see cref="ToolLocator"/> 解析，⛔ 不在这里拼、不猜安装目录）。
    ///
    /// <para>刻意**不**复用 <see cref="StatusText.SevenZipMissing"/>（那是"7z不存在"的文案）：
    /// 报一句"7z不存在"而其实是 UnRAR 没找到，只会把用户带偏。
    /// 状态用既有的"解压失败"+"明确的消息"，不新造状态常量（<see cref="StatusText"/> 是别人在改的文件）。</para>
    /// </summary>
    internal static ArchiveOperationResult CreateEngineMissingResult(ToolLocator tools)
    {
        return new ArchiveOperationResult
        {
            Success = false,
            ExitCode = -1,
            StandardOutput = string.Empty,
            StandardError = string.Empty,
            Status = StatusText.ExtractFailed,
            Message = $"未找到 RAR 解压引擎 UnRAR：{tools.UnRarExePath}（{tools.DescribeUnRarResolution()}）",
            DetectedErrorType = EngineErrorTypes.EngineUnavailable,
            UsedPasswordMasked = string.Empty,
            Elapsed = TimeSpan.Zero
        };
    }

    /// <summary><c>Process.Start()</c> 返回 false 时的结果。</summary>
    internal static ArchiveOperationResult CreateStartFailureResult(TimeSpan elapsed, string? usedPassword)
    {
        return ArchiveOperationResult.CreateFailure(
            -1,
            string.Empty,
            string.Empty,
            StatusText.ExtractFailed,
            "无法启动 UnRAR",
            EngineErrorTypes.UnknownError,
            elapsed,
            PasswordMasker.Mask(usedPassword));
    }

    /// <summary>
    /// 运行器自己抛出异常时的结果：把异常文本并进 stderr，再按**同一个分类出口**归类。
    /// <paramref name="output"/> / <paramref name="errorText"/> 传进来时已经脱敏（两路缓冲区读出的就是脱敏后的）。
    /// </summary>
    internal static ArchiveOperationResult CreateExceptionResult(
        int exitCode,
        string output,
        string errorText,
        string? usedPassword,
        TimeSpan elapsed,
        EngineOperation? operation)
    {
        string combined = UnRarOutputParser.CombineOutput(output, errorText);
        string detectedErrorType = UnRarOutputParser.DetectErrorType(exitCode, output, errorText, operation);

        return new ArchiveOperationResult
        {
            Success = false,
            ExitCode = exitCode,
            StandardOutput = output,
            StandardError = errorText,
            Status = UnRarOutputParser.ErrorTypeToTaskStatus(detectedErrorType),
            Message = UnRarOutputParser.ErrorTypeToMessage(detectedErrorType, combined),
            DetectedErrorType = detectedErrorType,
            UsedPasswordMasked = PasswordMasker.Mask(usedPassword),
            Elapsed = elapsed
        };
    }

    /// <summary>
    /// 测试操作在通用结果上的最终修饰（成功文案、错误状态、兜底文案）。
    ///
    /// 这是 <c>TestArchiveAsync</c> 的既有口径：<c>ExtractSuccess</c> 映射成"测试通过"，
    /// <c>ExtractFailed</c> 映射成"测试失败"，其余状态原样透传。
    /// </summary>
    internal static void ApplyTestOutcome(ArchiveOperationResult result)
    {
        if (result.Success)
        {
            result.Status = StatusText.TestPassed;
            result.Message = StatusText.TestPassed;
            result.DetectedErrorType = "None";
            return;
        }

        result.Status = MapTestStatus(result.DetectedErrorType);

        if (string.IsNullOrWhiteSpace(result.Message) ||
            result.Message == "操作失败" ||
            result.Message == "未知错误，请查看日志")
        {
            result.Message = UnRarOutputParser.ErrorTypeToMessage(
                result.DetectedErrorType,
                result.CombinedOutput);
        }
    }

    /// <summary>解压操作在通用结果上的最终修饰（成功文案 / 错误状态 / 兜底文案）。</summary>
    internal static void ApplyExtractOutcome(ArchiveOperationResult result)
    {
        if (result.Success)
        {
            result.Status = StatusText.ExtractSuccess;
            result.Message = StatusText.ExtractSuccess;
            result.DetectedErrorType = "None";
            return;
        }

        result.Status = UnRarOutputParser.ErrorTypeToTaskStatus(result.DetectedErrorType);

        if (string.IsNullOrWhiteSpace(result.Message) ||
            result.Message == "操作失败" ||
            result.Message == "未知错误，请查看日志")
        {
            result.Message = UnRarOutputParser.ErrorTypeToMessage(
                result.DetectedErrorType,
                result.CombinedOutput);
        }
    }

    private static string MapTestStatus(string? errorType)
    {
        string mapped = UnRarOutputParser.ErrorTypeToTaskStatus(errorType);

        return mapped switch
        {
            StatusText.ExtractSuccess => StatusText.TestPassed,
            StatusText.ExtractFailed => StatusText.TestFailed,
            _ => mapped
        };
    }
}
