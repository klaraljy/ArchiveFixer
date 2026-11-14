using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// 一次 UnRAR 运行的**编排**：拿整机名额 → 启动进程 → 接两路输出 → 空闲/总时长超时看门狗 →
/// 等退出 / 取消 → 收尸 → 把输出交给错误分类。
///
/// <para>职责都在协作类里：参数在 <see cref="UnRarCommandLine"/>，启动在
/// <see cref="UnRarProcessLauncher"/>，输出读取在 <see cref="UnRarOutputCapture"/>，
/// 超时取消在 <see cref="UnRarIdleWatch"/>，收尸在 <see cref="UnRarProcessTerminator"/>，
/// 分类在 <see cref="UnRarResultClassifier"/>。本类只负责它们的**先后顺序与生命周期**，
/// 换顺序就可能重新引入"迟到回调"或"没等尾巴输出"这两类问题，注释里写了原因的地方不要动。</para>
///
/// <para>外部进程的 8 条纪律（不变量 10）逐条对应：
/// <see cref="ProcessStartInfo.ArgumentList"/> 安全传参（不拼 cmd 字符串）、重定向 stdout/stderr、
/// 超时、取消、正确关句柄、只终止自己起的 PID 及其子进程。</para>
/// </summary>
internal static class UnRarProcessSession
{
    /// <summary>
    /// 起进程、收输出、按退出码与输出分类。
    ///
    /// <para><b>进度与卡住提示的接线</b>与 7-Zip 那一侧完全对称（见
    /// <c>SevenZipProcessSession</c>）：解析器只在本命名空间内
    /// （<see cref="UnRarProgressParser"/>），经节流后投递；返回之前必定收口，不会有迟到回调。</para>
    /// </summary>
    internal static async Task<ArchiveOperationResult> RunAsync(
        ToolLocator tools,
        IEnumerable<string> arguments,
        string? usedPassword,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        if (!tools.UnRarExists)
        {
            return UnRarResultClassifier.CreateEngineMissingResult(tools);
        }

        using IDisposable throttleLease = await GlobalExtractionThrottle
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();

        EngineOperation? operation = UnRarCommandLine.ResolveOperation(arguments);
        string? archivePath = UnRarCommandLine.FindArchiveArgument(arguments);

        var capture = new UnRarOutputCapture(operation, progressContext);

        using var timeoutCts = new CancellationTokenSource();
        using var absoluteTimeoutCts = new CancellationTokenSource(UnRarIdleWatch.MaxTotalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token,
            absoluteTimeoutCts.Token);

        using var watchdogCts = new CancellationTokenSource();
        Task timeoutWatch = Task.CompletedTask;

        Process? process = null;
        Task watchdog = Task.CompletedTask;

        try
        {
            process = UnRarProcessLauncher.CreateProcess(tools.UnRarExePath, arguments);

            if (!UnRarProcessLauncher.TryStart(process))
            {
                stopwatch.Stop();

                return UnRarResultClassifier.CreateStartFailureResult(
                    stopwatch.Elapsed,
                    usedPassword);
            }

            /*
             * 旧写法是 BeginOutputReadLine：UnRAR 的百分比是**同一个文件那一行内用退格回写**的，
             * 按行读意味着"一个 5GB 的单文件在解完之前一条进度都看不到"（见 ProcessOutputPump）。
             */
            capture.StartPumps(process, linkedCts.Token);

            watchdog = EngineOutputActivityMonitor.WatchAsync(
                capture.Monitor,
                EngineOutputActivityMonitor.ResolvePollInterval(
                    progressContext?.StallThreshold ?? EngineOutputActivityMonitor.DefaultStallThreshold),
                watchdogCts.Token);

            timeoutWatch = UnRarIdleWatch.WatchAsync(
                capture.Monitor,
                UnRarIdleWatch.DefaultIdleTimeout,
                timeoutCts);

            // ⛔ 立刻关掉标准输入（见 UnRarProcessLauncher.CloseStandardInput 的说明）。
            UnRarProcessLauncher.CloseStandardInput(process);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                bool isTimeout = UnRarIdleWatch.IsTimeout(
                    timeoutCts,
                    absoluteTimeoutCts,
                    cancellationToken);

                await UnRarProcessTerminator.KillTreeAsync(process).ConfigureAwait(false);

                stopwatch.Stop();

                (string outputOnCancel, string errorOnCancel) = capture.ReadBuffers();

                return new ArchiveOperationResult
                {
                    Success = false,
                    ExitCode = isTimeout ? -3 : -2,
                    StandardOutput = outputOnCancel,
                    StandardError = errorOnCancel,
                    Status = isTimeout ? StatusText.UnknownError : StatusText.Cancelled,
                    Message = isTimeout
                        ? "UnRAR 执行超时（30 分钟无输出或超过 6 小时总上限），已强制结束进程"
                        : "操作已取消，已强制结束 UnRAR 进程",
                    DetectedErrorType = isTimeout ? EngineErrorTypes.TimedOut : EngineErrorTypes.Cancelled,
                    UsedPasswordMasked = PasswordMasker.Mask(usedPassword),
                    Elapsed = stopwatch.Elapsed
                };
            }

            try
            {
                process.WaitForExit();
            }
            catch
            {
            }

            // 进程已退出，但两路输出的尾巴还在管道里：等泵读完再取缓冲区。
            await capture.DrainPumpsAsync().ConfigureAwait(false);

            stopwatch.Stop();

            (string output, string error) = capture.ReadBuffers();

            int exitCode;

            try
            {
                exitCode = process.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }

            return UnRarResultClassifier.AnalyzeResult(
                exitCode,
                output,
                error,
                usedPassword,
                stopwatch.Elapsed,
                archivePath,
                operation,
                arguments);
        }
        catch (Exception ex)
        {
            await UnRarProcessTerminator.KillTreeAsync(process).ConfigureAwait(false);

            // 先把两路输出收干净再读缓冲区（与 7-Zip 侧同一理由：异常路径上别丢最后几行）。
            await capture.DrainPumpsAsync().ConfigureAwait(false);

            stopwatch.Stop();

            (string output, string errorText) = capture.ReadBuffers();

            string exceptionMessage = PasswordMasker.Sanitize(ex.Message);

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                errorText += Environment.NewLine;
            }

            errorText += exceptionMessage;

            return UnRarResultClassifier.CreateExceptionResult(
                -1,
                output,
                errorText,
                usedPassword,
                stopwatch.Elapsed,
                operation);
        }
        finally
        {
            /*
             * 收口顺序不能换（这是"不得再有迟到回调"那条红线的落点）：
             * ① 停看门狗 → ② 收完两路输出泵 → ③ 关掉进度与卡住提示的出口 → ④ 才处置进程。
             * 走到这里说明这一次运行已经彻底结束，之后任何回调都只会打到已经收尾的任务上，
             * 所以第 ③ 步之后所有上报都会被丢弃。
             */
            try
            {
                watchdogCts.Cancel();
                timeoutCts.Cancel();
            }
            catch
            {
            }

            await capture.DrainPumpsAsync().ConfigureAwait(false);

            capture.Complete();

            try
            {
                await timeoutWatch.ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch
            {
            }

            UnRarProcessTerminator.Release(process);
        }
    }
}
