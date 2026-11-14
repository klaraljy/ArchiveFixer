using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 一次 7-Zip 运行的**编排**：拿整机名额 → 启动进程 → 接两路输出 → 空闲超时看门狗 →
/// 等退出 / 取消 → 收尸 → 把输出交给错误分类。
///
/// <para>职责都在协作类里：参数在 <see cref="SevenZipCommandLine"/>，启动在
/// <see cref="SevenZipProcessLauncher"/>，输出读取在 <see cref="SevenZipOutputCapture"/>，
/// 空闲超时在 <see cref="SevenZipIdleWatch"/>，收尸在 <see cref="SevenZipProcessTerminator"/>，
/// 分类在 <see cref="SevenZipResultClassifier"/>。本类只负责它们的**先后顺序与生命周期**，
/// 换顺序就可能重新引入"迟到回调"或"没等尾巴输出"这两类问题，注释里写了原因的地方不要动。</para>
/// </summary>
internal static class SevenZipProcessSession
{
    /// <summary>
    /// 起进程、收输出、按退出码与输出分类。
    ///
    /// <para><b>进度与卡住提示的接线</b>：<paramref name="progressContext"/> 非空时，
    /// 7-Zip 的进度行会被解析成统一进度（<see cref="SevenZipProgressParser"/>，只在本命名空间内）
    /// 经**节流后**投递给上层，并在"很久没有任何输出"时回调
    /// <see cref="EngineProgressContext.Stalled"/>。两条通道的生命周期都绑在本方法上：
    /// 返回之前必定 <c>Complete</c>，所以进程退出 / 取消 / 超时之后**不可能**再有迟到回调。</para>
    /// </summary>
    internal static async Task<ArchiveOperationResult> RunAsync(
        ToolLocator tools,
        IEnumerable<string> arguments,
        string usedPassword,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        if (!tools.SevenZipExists)
        {
            return SevenZipResultClassifier.CreateEngineMissingResult(tools.SevenZipExePath);
        }

        using IDisposable throttleLease = await GlobalExtractionThrottle
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var capture = new SevenZipOutputCapture(arguments, progressContext);

        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        Task timeoutWatch = SevenZipIdleWatch.WatchAsync(
            capture.Monitor,
            SevenZipIdleWatch.DefaultIdleTimeout,
            timeoutCts);

        using var watchdogCts = new CancellationTokenSource();

        Process? process = null;
        Task watchdog = Task.CompletedTask;

        try
        {
            process = SevenZipProcessLauncher.CreateTextProcess(tools.SevenZipExePath, arguments);

            if (!SevenZipProcessLauncher.TryStart(process))
            {
                stopwatch.Stop();

                return SevenZipResultClassifier.CreateStartFailureResult(
                    stopwatch.Elapsed,
                    usedPassword);
            }

            /*
             * 旧写法是 BeginOutputReadLine / OutputDataReceived。换成自己按 \r / \n / \b 切流
             * 的理由见 ProcessOutputPump 的类注释：7-Zip 的进度是 \r 分隔的，UnRAR 的百分比
             * 是同一行内用退格回写的 —— 按行读会把"实时进度"退化成"解完才有"。
             */
            capture.StartPumps(process, linkedCts.Token);

            watchdog = EngineOutputActivityMonitor.WatchAsync(
                capture.Monitor,
                EngineOutputActivityMonitor.ResolvePollInterval(
                    progressContext?.StallThreshold ?? EngineOutputActivityMonitor.DefaultStallThreshold),
                watchdogCts.Token);

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                bool isTimeout = timeoutCts.IsCancellationRequested &&
                                 !cancellationToken.IsCancellationRequested;

                await SevenZipProcessTerminator.KillTreeAsync(process).ConfigureAwait(false);

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
                        ? "7-Zip 执行超时，已强制结束进程"
                        : "操作已取消，已强制结束 7-Zip 进程",
                    DetectedErrorType = isTimeout ? "TimedOut" : "Cancelled",
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

            // 进程已经退出，但两路输出可能还有尾巴在管道里 —— 必须等泵读完再取缓冲区，
            // 否则最后几行（常常正是结论那几行）会丢。
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

            return SevenZipResultClassifier.AnalyzeResult(
                exitCode,
                output,
                error,
                usedPassword,
                stopwatch.Elapsed,
                SevenZipCommandLine.FindArchiveArgument(arguments),
                SevenZipCommandLine.ResolveOperation(arguments),
                arguments);
        }
        catch (Exception ex)
        {
            await SevenZipProcessTerminator.KillTreeAsync(process).ConfigureAwait(false);

            // 先把两路输出收干净再读缓冲区，否则异常路径上会丢掉最后几行
            //（那几行往往正是"为什么失败"的答案）。
            await capture.DrainPumpsAsync().ConfigureAwait(false);

            stopwatch.Stop();

            (string output, string errorText) = capture.ReadBuffers();

            string exceptionMessage = PasswordMasker.Sanitize(ex.Message);

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                errorText += Environment.NewLine;
            }

            errorText += exceptionMessage;

            return SevenZipResultClassifier.CreateExceptionResult(
                -1,
                output,
                errorText,
                usedPassword,
                stopwatch.Elapsed);
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
                await watchdog.ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await timeoutWatch.ConfigureAwait(false);
            }
            catch
            {
            }

            SevenZipProcessTerminator.Release(process);
        }
    }
}
