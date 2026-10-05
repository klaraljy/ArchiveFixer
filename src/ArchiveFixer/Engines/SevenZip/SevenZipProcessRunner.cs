using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.SevenZip
{
    /// <summary>
    /// 7-Zip 命令行进程的调用器：只负责"怎么起进程、怎么收输出、怎么收尸"。
    ///
    /// 边界（AGENTS.md §3.1 的四条禁止项）：
    /// 参数拼接、7-Zip 文本输出解析、退出码到错误的映射，**全部只能存在于本命名空间内**。
    /// 核心模块（ViewModel / 调度 / 递归）一律通过 <see cref="IArchiveEngine"/> 调用，不得直接碰 7z.exe。
    /// </summary>
    public sealed class SevenZipProcessRunner
    {
        private static readonly TimeSpan DefaultSevenZipTimeout = TimeSpan.FromMinutes(30);

        /// <summary>等两路输出泵收完的上限（进程已经退出，正常情况下是毫秒级）。</summary>
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(3);

        private readonly ToolLocator _tools;

        public SevenZipProcessRunner()
            : this(ToolLocator.Default)
        {
        }

        public SevenZipProcessRunner(ToolLocator tools)
        {
            _tools = tools ?? ToolLocator.Default;
        }

        /// <summary>
        /// 外部工具路径统一由 <see cref="ToolLocator"/> 解析。
        /// 这里不再自己拼路径 —— 以前 App.xaml.cs / PathService / ExtractService 各自拼一遍，
        /// 还带一个写死的 D:\7-Zip 回退，属于必须还的债（AGENTS.md §3.1）。
        /// </summary>
        public ToolLocator Tools => _tools;

        public bool CheckSevenZipExists()
        {
            return _tools.SevenZipExists;
        }

        public bool CheckSevenZipDllExists()
        {
            return _tools.SevenZipDllExists;
        }

        public string GetSevenZipPath()
        {
            return _tools.SevenZipExePath;
        }

        public string GetSevenZipDllPath()
        {
            return _tools.SevenZipDllPath;
        }


        public async Task<ArchiveOperationResult> TestArchiveAsync(
            string archivePath,
            string password,
            CancellationToken cancellationToken = default)
        {
            return await TestArchiveAsync(archivePath, password, cancellationToken, null).ConfigureAwait(false);
        }

        /// <summary>带进度 / 卡住提示的测试（<paramref name="progressContext"/> 可空）。</summary>
        public async Task<ArchiveOperationResult> TestArchiveAsync(
            string archivePath,
            string password,
            CancellationToken cancellationToken,
            EngineProgressContext? progressContext)
        {
            if (!CheckSevenZipExists())
            {
                return CreateSevenZipMissingResult();
            }

            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return ArchiveOperationResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    StatusText.TestFailed,
                    "压缩包文件不存在",
                    "UnknownError",
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            List<string> arguments = BuildTestArguments(archivePath, password);

            ArchiveOperationResult result = await RunSevenZipAsync(
                    arguments,
                    password,
                    cancellationToken,
                    progressContext)
                .ConfigureAwait(false);

            if (result.Success)
            {
                result.Status = StatusText.TestPassed;
                result.Message = StatusText.TestPassed;
                result.DetectedErrorType = "None";
            }
            else
            {
                result.DetectedErrorType = ResolveVolumeMissingErrorType(
                    result.DetectedErrorType,
                    archivePath,
                    arguments);

                string mappedStatus = SevenZipOutputParser.ErrorTypeToTaskStatus(result.DetectedErrorType);

                result.Status = mappedStatus switch
                {
                    StatusText.ExtractSuccess => StatusText.TestPassed,
                    StatusText.ExtractFailed => StatusText.TestFailed,
                    _ => mappedStatus
                };

                if (result.DetectedErrorType == "VolumeMissing")
                {
                    /*
                     * 这个结果不是"AnalyzeResult 直接产出"的（上面刚改过分类），
                     * 所以"缺哪几卷"要在这里补上，否则用户看到的还是那句笼统的"缺少必要分卷"。
                     */
                    result.Message = BuildVolumeMissingMessage(archivePath);
                }
                else if (string.IsNullOrWhiteSpace(result.Message) ||
                    result.Message == "操作失败" ||
                    result.Message == "未知错误，请查看日志")
                {
                    result.Message = SevenZipOutputParser.ErrorTypeToMessage(
                        result.DetectedErrorType,
                        result.CombinedOutput);
                }
            }

            return result;
        }

        public async Task<ArchiveOperationResult> ExtractArchiveAsync(
            string archivePath,
            string outputPath,
            string password,
            ExtractOptions options,
            CancellationToken cancellationToken = default)
        {
            return await ExtractArchiveAsync(
                    archivePath,
                    outputPath,
                    password,
                    options,
                    cancellationToken,
                    null)
                .ConfigureAwait(false);
        }

        /// <summary>带进度 / 卡住提示的解压（<paramref name="progressContext"/> 可空）。</summary>
        public async Task<ArchiveOperationResult> ExtractArchiveAsync(
            string archivePath,
            string outputPath,
            string password,
            ExtractOptions options,
            CancellationToken cancellationToken,
            EngineProgressContext? progressContext)
        {
            options ??= new ExtractOptions();
            options.Normalize();

            if (!CheckSevenZipExists())
            {
                return CreateSevenZipMissingResult();
            }

            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return ArchiveOperationResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    StatusText.ExtractFailed,
                    "压缩包文件不存在",
                    "UnknownError",
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return ArchiveOperationResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    StatusText.ExtractFailed,
                    "输出目录为空",
                    "UnknownError",
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            try
            {
                Directory.CreateDirectory(outputPath);
            }
            catch (Exception ex)
            {
                string safeMessage = PasswordMasker.Sanitize(ex.Message);

                return ArchiveOperationResult.CreateFailure(
                    -1,
                    string.Empty,
                    safeMessage,
                    StatusText.AccessDenied,
                    "无法创建输出目录：" + safeMessage,
                    "AccessDenied",
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            List<string> arguments = BuildExtractArguments(archivePath, outputPath, password, options);

            ArchiveOperationResult result = await RunSevenZipAsync(
                    arguments,
                    password,
                    cancellationToken,
                    progressContext)
                .ConfigureAwait(false);

            if (result.Success)
            {
                result.Status = StatusText.ExtractSuccess;
                result.Message = StatusText.ExtractSuccess;
                result.DetectedErrorType = "None";
            }
            else
            {
                result.Status = SevenZipOutputParser.ErrorTypeToTaskStatus(result.DetectedErrorType);

                if (string.IsNullOrWhiteSpace(result.Message) ||
                    result.Message == "操作失败" ||
                    result.Message == "未知错误，请查看日志")
                {
                    result.Message = SevenZipOutputParser.ErrorTypeToMessage(
                        result.DetectedErrorType,
                        result.CombinedOutput);
                }
            }

            return result;
        }

        public async Task<ArchiveOperationResult> RunSevenZipAsync(
            IEnumerable<string> arguments,
            CancellationToken cancellationToken = default)
        {
            return await RunSevenZipAsync(arguments, string.Empty, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ArchiveOperationResult> RunSevenZipAsync(
            IEnumerable<string> arguments,
            string usedPassword,
            CancellationToken cancellationToken = default)
        {
            return await RunSevenZipAsync(arguments, usedPassword, cancellationToken, null).ConfigureAwait(false);
        }

        /// <summary>
        /// 起进程、收输出、按退出码与输出分类。
        ///
        /// <para>
        /// <b>进度与卡住提示的接线（2026-09-22 新增）</b>：
        /// <paramref name="progressContext"/> 非空时，本方法会把 7-Zip 的进度行解析成统一进度
        /// （<see cref="SevenZipProgressParser"/>，只在本命名空间内）经**节流后**投递给上层，
        /// 并在"很久没有任何输出"时回调 <see cref="EngineProgressContext.Stalled"/>。
        /// 两条通道的生命周期都绑在本方法上：返回之前必定 <c>Complete</c>，
        /// 所以进程退出 / 取消 / 超时之后**不可能**再有迟到回调。
        /// </para>
        /// </summary>
        public async Task<ArchiveOperationResult> RunSevenZipAsync(
            IEnumerable<string> arguments,
            string usedPassword,
            CancellationToken cancellationToken,
            EngineProgressContext? progressContext)
        {
            if (!CheckSevenZipExists())
            {
                return CreateSevenZipMissingResult();
            }

            var stopwatch = Stopwatch.StartNew();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            object outputLock = new();
            object errorLock = new();

            Process? process = null;

            /*
             * 进度解析只在"这次真的打开了进度开关"时才启用。
             *
             * 为什么要这个条件：RunSevenZipAsync 是通用的（列目录 / 测试 / 解压都走它），
             * 而 l -slt 的输出是要被逐行解析的。只有带 -bsp1 的调用才可能产出进度行，
             * 这样列目录那条路径上"7-Zip 文本"的解析面一点都没变宽。
             */
            EngineProgressParser? progressParser = HasProgressSwitch(arguments)
                ? SevenZipProgressParser.TryParse
                : null;

            ArchiveProgressReporter reporter = new(progressContext?.Progress);

            /*
             * 卡住监视器：只有上层要求提示时才建（没有接收端时它什么都不做，没必要每 5 秒醒一次）。
             * ⛔ 它**没有杀进程的能力**，只把一个事实报出去（见 EngineOutputActivityMonitor）。
             */
            EngineOutputActivityMonitor? monitor = progressContext?.Stalled != null
                ? new EngineOutputActivityMonitor(progressContext.StallThreshold, progressContext.Stalled)
                : null;

            var outputCollector = new EngineOutputCollector(outputBuilder, outputLock, progressParser, reporter, monitor);
            var errorCollector = new EngineOutputCollector(errorBuilder, errorLock, progressParser, reporter, monitor);

            using var timeoutCts = new CancellationTokenSource(DefaultSevenZipTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

            using var watchdogCts = new CancellationTokenSource();

            Task outputPump = Task.CompletedTask;
            Task errorPump = Task.CompletedTask;
            Task watchdog = Task.CompletedTask;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = GetSevenZipPath(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = AppContext.BaseDirectory
                };

                foreach (string arg in arguments)
                {
                    if (arg != null)
                    {
                        startInfo.ArgumentList.Add(arg);
                    }
                }

                process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                if (!process.Start())
                {
                    stopwatch.Stop();

                    return ArchiveOperationResult.CreateFailure(
                        -1,
                        string.Empty,
                        string.Empty,
                        StatusText.ExtractFailed,
                        "无法启动 7-Zip",
                        "UnknownError",
                        stopwatch.Elapsed,
                        MaskPassword(usedPassword));
                }

                /*
                 * 旧写法是 BeginOutputReadLine / OutputDataReceived。换成自己按 \r / \n / \b 切流
                 * 的理由见 ProcessOutputPump 的类注释：7-Zip 的进度是 \r 分隔的，UnRAR 的百分比
                 * 是同一行内用退格回写的 —— 按行读会把"实时进度"退化成"解完才有"。
                 */
                outputPump = ProcessOutputPump.PumpAsync(
                    process.StandardOutput,
                    outputCollector.Handle,
                    linkedCts.Token);

                errorPump = ProcessOutputPump.PumpAsync(
                    process.StandardError,
                    errorCollector.Handle,
                    linkedCts.Token);

                watchdog = EngineOutputActivityMonitor.WatchAsync(
                    monitor!,
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

                    await KillProcessTreeSafeAsync(process).ConfigureAwait(false);

                    stopwatch.Stop();

                    (string outputOnCancel, string errorOnCancel) = ReadBuffers(
                        outputBuilder,
                        errorBuilder,
                        outputLock,
                        errorLock);

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
                        UsedPasswordMasked = MaskPassword(usedPassword),
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
                await DrainPumpsAsync(outputPump, errorPump).ConfigureAwait(false);

                stopwatch.Stop();

                (string output, string error) = ReadBuffers(outputBuilder, errorBuilder, outputLock, errorLock);

                int exitCode;

                try
                {
                    exitCode = process.ExitCode;
                }
                catch
                {
                    exitCode = -1;
                }

                return AnalyzeResult(
                    exitCode,
                    output,
                    error,
                    usedPassword,
                    stopwatch.Elapsed,
                    FindArchiveArgument(arguments),
                    ResolveOperation(arguments),
                    arguments);
            }
            catch (Exception ex)
            {
                await KillProcessTreeSafeAsync(process).ConfigureAwait(false);

                // 先把两路输出收干净再读缓冲区，否则异常路径上会丢掉最后几行
                //（那几行往往正是"为什么失败"的答案）。
                await DrainPumpsAsync(outputPump, errorPump).ConfigureAwait(false);

                stopwatch.Stop();

                (string output, string errorText) = ReadBuffers(outputBuilder, errorBuilder, outputLock, errorLock);

                string exceptionMessage = PasswordMasker.Sanitize(ex.Message);

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    errorText += Environment.NewLine;
                }

                errorText += exceptionMessage;

                string combined = SevenZipOutputParser.CombineOutput(output, errorText);
                string detectedErrorType = SevenZipOutputParser.DetectSevenZipErrorType(-1, output, errorText);

                return new ArchiveOperationResult
                {
                    Success = false,
                    ExitCode = -1,
                    StandardOutput = PasswordMasker.Sanitize(output),
                    StandardError = PasswordMasker.Sanitize(errorText),
                    Status = SevenZipOutputParser.ErrorTypeToTaskStatus(detectedErrorType),
                    Message = SevenZipOutputParser.ErrorTypeToMessage(detectedErrorType, combined),
                    DetectedErrorType = detectedErrorType,
                    UsedPasswordMasked = MaskPassword(usedPassword),
                    Elapsed = stopwatch.Elapsed
                };
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
                }
                catch
                {
                }

                await DrainPumpsAsync(outputPump, errorPump).ConfigureAwait(false);

                reporter.Complete();
                monitor?.Complete();

                try
                {
                    await watchdog.ConfigureAwait(false);
                }
                catch
                {
                }

                if (process != null)
                {
                    try
                    {
                        process.CancelOutputRead();
                    }
                    catch
                    {
                    }

                    try
                    {
                        process.CancelErrorRead();
                    }
                    catch
                    {
                    }

                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>这次调用是不是打开了进度开关（<c>-bsp1</c> / <c>-bsp2</c>）。</summary>
        private static bool HasProgressSwitch(IEnumerable<string>? arguments)
        {
            if (arguments == null)
            {
                return false;
            }

            foreach (string argument in arguments)
            {
                if (string.Equals(argument, "-bsp1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-bsp2", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 等两路输出泵收完，**有上限**。
        ///
        /// 为什么必须有上限：泵读的是子进程的管道；万一有别的进程继承了写端，EOF 可能永远不来。
        /// 那时宁可丢一点尾巴输出，也不能把整个任务挂在这里（AGENTS.md §9：禁止无界等待）。
        /// </summary>
        private static async Task DrainPumpsAsync(Task outputPump, Task errorPump)
        {
            try
            {
                Task all = Task.WhenAll(outputPump, errorPump);

                await Task.WhenAny(all, Task.Delay(PumpDrainTimeout)).ConfigureAwait(false);
            }
            catch
            {
                // 泵内部已经把取消 / 管道关闭都吞掉了，这里只是兜底。
            }
        }

        private static (string Output, string Error) ReadBuffers(
            StringBuilder outputBuilder,
            StringBuilder errorBuilder,
            object outputLock,
            object errorLock)
        {
            string output;
            string error;

            lock (outputLock)
            {
                output = outputBuilder.ToString();
            }

            lock (errorLock)
            {
                error = errorBuilder.ToString();
            }

            return (PasswordMasker.Sanitize(output), PasswordMasker.Sanitize(error));
        }

        public ArchiveOperationResult AnalyzeResult(int exitCode, string output, string error)
        {
            return AnalyzeResult(exitCode, output, error, string.Empty, TimeSpan.Zero);
        }

        public ArchiveOperationResult AnalyzeResult(
            int exitCode,
            string output,
            string error,
            string usedPassword,
            TimeSpan elapsed)
        {
            return AnalyzeResult(exitCode, output, error, usedPassword, elapsed, archivePath: null);
        }

        /// <summary>
        /// 分析一次 7z 调用的结果。
        ///
        /// <paramref name="archivePath"/> 非空时，分卷缺失会被判得更准（见
        /// <see cref="ResolveVolumeMissingErrorType"/>）：只给 <c>xxx.7z.002</c> 时 7-Zip 报的是
        /// "Cannot open the file as archive"，字面与"这不是归档"一样，只能靠文件名与目录内容区分。
        ///
        /// <paramref name="operation"/> 说明这次跑的是哪个命令（l / t / x）：<b>只有列目录</b>才允许
        /// 下"加密了文件名"的结论（见 <see cref="SevenZipOutputParser.LooksLikeEncryptedHeaders"/>）。
        /// </summary>
        private ArchiveOperationResult AnalyzeResult(
            int exitCode,
            string output,
            string error,
            string usedPassword,
            TimeSpan elapsed,
            string? archivePath,
            EngineOperation? operation = null,
            IEnumerable<string>? arguments = null)
        {
            output = PasswordMasker.Sanitize(output);
            error = PasswordMasker.Sanitize(error);

            string combined = SevenZipOutputParser.CombineOutput(output, error);
            bool success = SevenZipOutputParser.LooksLikeSuccess(exitCode, output, error);

            string errorType = success
                ? "None"
                : SevenZipOutputParser.DetectSevenZipErrorType(exitCode, output, error, archivePath, operation);

            errorType = ResolveVolumeMissingErrorType(errorType, archivePath);

            string status = SevenZipOutputParser.ErrorTypeToTaskStatus(errorType);

            string message = success
                ? "操作成功"
                : errorType switch
                {
                    SevenZipOutputParser.MissingFirstVolumeErrorType => SevenZipOutputParser.ErrorTypeToMessage(errorType),
                    "VolumeMissing" => BuildVolumeMissingMessage(archivePath),
                    _ => SevenZipOutputParser.ErrorTypeToMessage(errorType, combined)
                };

            return new ArchiveOperationResult
            {
                Success = success,
                ExitCode = exitCode,
                StandardOutput = output,
                StandardError = error,
                Status = status,
                Message = message,
                DetectedErrorType = errorType,
                UsedPasswordMasked = MaskPassword(usedPassword),
                Elapsed = elapsed,
                CommandSummary = BuildCommandSummary(arguments),

                /*
                 * 「哪些条目坏了」与"引擎自报坏了几个" —— 部分完成发布那块功能的地基（见
                 * ArchiveOperationResult.FailedEntryNames 的说明）。原文已经脱敏，解析在引擎目录内完成。
                 */
                FailedEntryNames = SevenZipOutputParser.ExtractFailedEntryNames(combined),
                ReportedSubItemErrors = SevenZipOutputParser.ExtractSubItemErrorCount(combined)
            };
        }

        /// <summary>
        /// "这次到底拿什么参数调的 7-Zip"（**已脱敏**，只给详细日志用）。
        ///
        /// <para>形状：命令 + 开关 + 归档**文件名**。刻意不带完整路径 ——
        /// 详细日志也不该把用户的个人目录写进去（§8 隐私红线），而定位问题用文件名就够。</para>
        ///
        /// <para>⛔ 参数里绝不允许出现明文密码：这里再过一道 <see cref="PasswordMasker.Sanitize"/>，
        /// 由它把 <c>-p&lt;明文&gt;</c> 改成 <c>-p******</c>（不变量 5）。</para>
        /// </summary>
        internal static string BuildCommandSummary(IEnumerable<string>? arguments)
        {
            if (arguments == null)
            {
                return string.Empty;
            }

            var parts = new List<string> { "7z" };
            bool isFirst = true;
            bool sawArchive = false;

            foreach (string argument in arguments)
            {
                if (string.IsNullOrWhiteSpace(argument))
                {
                    continue;
                }

                if (isFirst)
                {
                    // 命令字（l / t / x / e）直接跟在 7z 后面。
                    isFirst = false;
                    parts.Add(argument.Trim());
                    continue;
                }

                if (argument.StartsWith("-", StringComparison.Ordinal))
                {
                    // 输出目录不写进摘要（它可能很长，而且对排查"调了什么"没有信息量）。
                    if (argument.StartsWith("-o", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    parts.Add(argument.Trim());
                    continue;
                }

                if (!sawArchive)
                {
                    // 第一个非开关参数就是归档（参数拼装顺序固定，见 BuildExtractArguments）：
                    // 只留**文件名**，完整路径不进日志（§8 隐私红线）。
                    sawArchive = true;
                    parts.Add(Path.GetFileName(argument));
                    continue;
                }

                // 归档之后的位置是"要处理的条目名"（密码预检只解那一条）：保留，它能说明这次解的是什么。
                parts.Add(argument.Trim());
            }

            return PasswordMasker.Sanitize(string.Join(" ", parts));
        }

        /// <summary>
        /// 把"字段上像'非归档'"的结果修正成缺卷/缺首卷（不变量 7）。
        ///
        /// 为什么需要二次判定：7-Zip 对"只给非首卷"和"给了一个纯文本文件"报的是同一句
        /// <c>Cannot open the file as archive</c>（26.01 实测，退出码 2），
        /// 纯关键字分类一定会把缺卷误报成"不支持该格式"，用户就会以为文件坏了。
        /// 唯一可靠的判据是**文件名 + 同目录里有没有这一组的其它卷**：
        /// 目录里数出缺号 → 缺卷；数不出缺号但文件本身就叫 .001/.002/… → 缺首卷。
        ///
        /// <para>⚠ 2026-10-05：入口那一档已经**在纯文本分类里就分好**了
        /// （<see cref="SevenZipOutputParser.LooksLikeMissingVolumePart"/> 只认卷号 ≥ 2；
        /// 卷号 == 1 由分类器直接返 <c>"VolumeMissing"</c>，真机 `HK.7z.001` 报"缺少首卷"就是说反了）。
        /// 本方法因此**照旧自洽**：<c>"VolumeMissing"</c> 在第一行原样返回，
        /// <c>MissingFirstVolume</c> 仍走下面的"数缺号"二次判定；两个码对上层是同一类
        /// （<c>ExtractionCoordinator.IsVolumeMissingErrorType</c> 两个都收）。</para>
        /// </summary>
        private static string ResolveVolumeMissingErrorType(
            string errorType,
            string? archivePath,
            IEnumerable<string>? arguments = null)
        {
            if (string.Equals(errorType, "VolumeMissing", StringComparison.Ordinal))
            {
                return errorType;
            }

            /*
             * 纯文本分类（没有文件系统可用）只会说"这是分卷，少了首卷"。
             * 走到这里说明我们在运行器里、磁盘就在手边 —— 那就去数一遍缺哪几卷，
             * 把结论落到上层认识的 VolumeMissing 上（不变量 7 要的是"缺哪几个"）。
             */
            if (string.Equals(errorType, SevenZipOutputParser.MissingFirstVolumeErrorType, StringComparison.Ordinal))
            {
                string? volumePath = ResolveArchivePath(archivePath, arguments);

                return string.IsNullOrWhiteSpace(volumePath) || FindMissingVolumeParts(volumePath).Count == 0
                    ? errorType
                    : "VolumeMissing";
            }

            /*
             * 只在"看起来像打不开这个文件"的分类上做二次判定。
             * 密码错误、权限不足、磁盘满这些各有明确原因，不能被这条吞掉。
             */
            if (!string.Equals(errorType, "UnsupportedFormat", StringComparison.Ordinal) &&
                !string.Equals(errorType, "CorruptedArchive", StringComparison.Ordinal) &&
                !string.Equals(errorType, "UnknownError", StringComparison.Ordinal))
            {
                return errorType;
            }

            string? path = ResolveArchivePath(archivePath, arguments);

            if (string.IsNullOrWhiteSpace(path))
            {
                return errorType;
            }

            string fileName = Path.GetFileName(path);

            if (VolumeGroupDetector.TryGetVolumeIndex(fileName) == null)
            {
                // 文件名不是分卷 → 原来的分类是对的（纯文本改名成 .7z 走的就是这一支）。
                return errorType;
            }

            /*
             * 这个文件本身就是分卷名：
             * 同目录里能数出缺号 → 缺卷（消息里会列出缺哪几个）；
             * 数不出缺号 → 首卷不在同目录（用户的真实现场：两个 mp4 各藏一段分卷，解到两个目录里去了），
             * 消息里点名首卷名 —— 分卷总数在命名里没有答案，不编造。
             *
             * 对外统一报 VolumeMissing：它已经映射到"分卷缺失"状态与正确的用户动作，
             * 不新造一个上层代码不认识的状态码。MissingFirstVolume 只留给
             * 纯文本分类（DetectSevenZipErrorType）那一条没有文件系统可查的路径。
             */
            return "VolumeMissing";
        }

        /// <summary>
        /// "缺哪几个卷"是用户在缺卷时唯一能行动的信息（不变量 7），所以这句提示要去磁盘上数一遍：
        /// 已经找到哪几卷、按命名推出来的缺号是哪些。数不出来时退回通用文案，绝不编造名字。
        /// </summary>
        private static string BuildVolumeMissingMessage(string? archivePath)
        {
            List<string> missing = FindMissingVolumeParts(archivePath);

            if (missing.Count == 0)
            {
                return SevenZipOutputParser.ErrorTypeToMessage("VolumeMissing");
            }

            return $"分卷压缩包缺少必要分卷，缺少：{string.Join('、', missing)}" +
                   "。请把同一组分卷放在同一目录后重试。";
        }

        /// <summary>
        /// 拿这次调用真正用的归档路径：优先用调用方直接给的，没有才去参数表里翻。
        /// </summary>
        private static string? ResolveArchivePath(string? archivePath, IEnumerable<string>? arguments)
        {
            return string.IsNullOrWhiteSpace(archivePath)
                ? FindArchiveArgument(arguments)
                : archivePath;
        }

        /// <summary>
        /// 从参数表里找出归档路径（用于"缺卷 / 缺首卷"的二次判定）。
        ///
        /// ⚠ 第一个参数是**命令**（<c>x</c> / <c>t</c> / <c>l</c>），它不以 <c>-</c> 开头、
        /// 也不是路径 —— 必须显式跳过。曾经的写法是"跳过以 - 开头的，返回第一个别的"，
        /// 结果返回的是命令字 <c>x</c>，于是二次判定永远在判一个叫 "x" 的文件，
        /// 缺卷被静默判成"不支持该格式"（实测：诊断里 path=x）。
        /// </summary>
        private static string? FindArchiveArgument(IEnumerable<string>? arguments)
        {
            if (arguments == null)
            {
                return null;
            }

            bool isFirst = true;

            foreach (string argument in arguments)
            {
                if (isFirst)
                {
                    // 命令本身不是路径。
                    isFirst = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(argument) ||
                    argument.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                return argument;
            }

            return null;
        }

        /// <summary>
        /// 从参数表里认出这次跑的是哪个命令（<c>l</c> / <c>t</c> / <c>x</c> / <c>e</c>）。
        ///
        /// 为什么要它：**同一段输出在不同命令下的含义不同**。加密头（-mhe）的包用 `l` 列目录失败
        /// 说明"名字读不出来"，而在 `x` 上失败只说明"这个候选密码不对"（还要接着试下一个）。
        /// 认不出来时返回 null = "这层不下结论"，与旧行为一致。
        /// </summary>
        internal static EngineOperation? ResolveOperation(IEnumerable<string>? arguments)
        {
            if (arguments == null)
            {
                return null;
            }

            foreach (string argument in arguments)
            {
                if (argument == null)
                {
                    continue;
                }

                string command = argument.Trim().ToLowerInvariant();

                return command switch
                {
                    "l" => EngineOperation.List,
                    "t" => EngineOperation.Test,
                    "x" => EngineOperation.Extract,
                    "e" => EngineOperation.Extract,
                    _ => null
                };
            }

            return null;
        }
        /// <summary>
        /// 在归档所在目录里找分卷缺号。
        ///
        /// 这些命名（.001 / .z01 / .r00 / .partN）**都没有"共几卷"这个信息**，
        /// 所以只能报 [第 1 卷, 命中过的最大卷号] 之间的缺号；最大卷号之后再有没有卷，
        /// 命名里没有答案，不猜（与 VolumeGroupDetector 的口径一致）。
        /// </summary>
        private static List<string> FindMissingVolumeParts(string? archivePath)
        {
            var missing = new List<string>();

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return missing;
            }

            try
            {
                string directory = Path.GetDirectoryName(archivePath) ?? string.Empty;

                if (directory.Length == 0 || !Directory.Exists(directory))
                {
                    return missing;
                }

                // 只扫同目录，不递归、不跟链接：与"一组分卷 = 一个任务"的口径一致。
                var candidates = new List<VolumeCandidate>();
                var foundInline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string archiveFileName = Path.GetFileName(archivePath);

                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    candidates.Add(new VolumeCandidate { Path = file });
                }

                foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
                {
                    if (!group.Volumes.Any(volume => SafePathHelper.PathEquals(volume.Path, archivePath)))
                    {
                        continue;
                    }

                    /*
                     * 首卷不在时**必须单独点名**，而且名字要跟用户手上的文件同风格：
                     * 用 VolumeGroupDetector 按"基名 + 卷号"补出来的名字是 volume.001，
                     * 而用户磁盘上那一组叫 volume.7z.001 —— 报一个用户找不到的名字等于没报。
                     * 所以缺第 1 卷时，名字取"当前这个分卷的文件名 + 推出来的首卷名"。
                     */
                    if (!string.Equals(
                            SafePathHelper.GetFullPathSafe(group.FirstVolumePath),
                            SafePathHelper.GetFullPathSafe(archivePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string? firstVolumeName = VolumeGroupDetector.TryGetFirstVolumeName(archiveFileName);

                        if (!string.IsNullOrWhiteSpace(firstVolumeName))
                        {
                            int suffixIndex = archiveFileName.Length - Path.GetExtension(archiveFileName).Length;
                            string styledName = string.Equals(
                                    Path.GetExtension(firstVolumeName),
                                    Path.GetExtension(archiveFileName),
                                    StringComparison.OrdinalIgnoreCase)
                                ? firstVolumeName
                                : (suffixIndex > 0 ? archiveFileName[..suffixIndex] : archiveFileName) + firstVolumeName;

                            if (foundInline.Add(styledName))
                            {
                                missing.Add(styledName);
                            }
                        }
                    }

                    foreach (string name in group.MissingVolumeNames)
                    {
                        if (foundInline.Add(name))
                        {
                            missing.Add(name);
                        }
                    }

                    break;
                }
            }
            catch
            {
                // 数不出来就退回通用文案：报"缺卷"这件事本身已经做对了，清单是加分项。
            }

            return missing;
        }

        /// <summary>
        /// 解压参数。
        ///
        /// <para>
        /// <b>进度开关（2026-09-22 改）</b>：原来是 <c>-bsp0 -bd</c>，等于**主动把 7-Zip 的进度关掉** ——
        /// 界面因此只剩"处理中/完成"两态，长时间零输出看起来就是卡死（用户反复抱怨的那件事）。
        /// 现在改成 <c>-bsp1</c>（进度打到 stdout），并**必须去掉 <c>-bd</c>**：
        /// 本机 26.03 实测 <c>-bsp1 -bd</c> 一条进度都不出，<c>-bd</c> 会压掉进度指示器；
        /// 去掉之后同一命令能稳定刷出 <c> 67% 8 - payload 08 数据.bin</c> 这样的行。
        /// 解析在 <see cref="SevenZipProgressParser"/>（只允许待在本命名空间内），
        /// 进度行本身会被 <see cref="EngineOutputCollector"/> 从日志缓冲区里剔掉。
        /// </para>
        ///
        /// <para>
        /// ⚠ <b>这里刻意**不**加 <c>-kb</c></b>（"保留受损的文件"）。这一点与
        /// <c>docs/WinRAR功能参考.md</c> §2 C 组的建议**相反**，依据是本机实测：
        /// <list type="bullet">
        /// <item><description><c>7z x -kb …</c> → <c>Command Line Error: Unknown switch: -kb</c>，退出码 <b>7</b>
        /// （26.01 实测，开关放前放后都一样）—— <c>-kb</c> 是 RAR / UnRAR 的开关，7-Zip **没有**它；</description></item>
        /// <item><description>7-Zip 本来**就保留**校验失败的半成品（实测：截断包里第二个文件被截到 29888 字节，
        /// 仍然留在输出目录里），也就是说"保留受损文件"这一档在 7-Zip 上**本来就是开着的**，没有对应开关可切。</description></item>
        /// </list>
        /// 所以那一档只作用于 RAR 引擎（<c>UnRarEngine</c> 的 <c>-kb</c>）；
        /// 真按建议给 7-Zip 加上，用户一开这个设置，**所有解压都会以命令行错误失败**。
        /// </para>
        /// </summary>
        public List<string> BuildExtractArguments(
            string archivePath,
            string outputPath,
            string password,
            ExtractOptions options)
        {
            options ??= new ExtractOptions();
            options.Normalize();

            var args = new List<string>
            {
                "x",
                "-bsp1",
                "-sccUTF-8",
                archivePath,
                "-o" + outputPath,
                "-y"
            };

            string overwriteArg = GetOverwriteArgument(options.OverwriteMode);

            if (!string.IsNullOrWhiteSpace(overwriteArg))
            {
                args.Add(overwriteArg);
            }

            args.Add("-p" + (password ?? string.Empty));

            /*
             * 只解指定的条目（密码预检：先解"最小的那一个"验密码，再决定要不要跑整包 —— 用户 2026-09-25 第 37 条）。
             * 7-Zip 把归档之后的位置当"要处理的条目名"，名字要原样给（`-o`/`-p` 这些开关都已经加完）。
             * ⚠ 调用方必须先把带通配符（* ? [）的名字挑掉：7-Zip 会把它们当模式匹配（见 Extraction/PasswordProbe）。
             */
            foreach (string entry in options.IncludeEntries ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    args.Add(entry);
                }
            }

            return args;
        }

        /// <summary>
        /// 测试参数。进度开关与解压同一口径（见 <see cref="BuildExtractArguments"/>）：
        /// <c>t</c> 在大包上同样会跑几十秒，没有进度就还是"看起来死了"。
        /// </summary>
        public List<string> BuildTestArguments(string archivePath, string password)
        {
            var args = new List<string>
            {
                "t",
                "-bsp1",
                "-sccUTF-8",
                archivePath,
                "-y",
                "-p" + (password ?? string.Empty)
            };

            return args;
        }

        public string GetOverwriteArgument(string overwriteMode)
        {
            return overwriteMode switch
            {
                "SkipExisting" => "-aos",
                "OverwriteAll" => "-aoa",
                "AutoRenameExtracted" => "-aou",
                "AutoRenameExisting" => "-aot",
                _ => "-aos"
            };
        }

        private ArchiveOperationResult CreateSevenZipMissingResult()
        {
            // 路径由 ToolLocator 统一解析，不再在这里拼、也不再猜 D:\7-Zip（AGENTS.md §3.1）。
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.SevenZipMissing,
                Message = $"未找到 7-Zip 程序：{_tools.SevenZipExePath}",
                DetectedErrorType = "SevenZipMissing",
                UsedPasswordMasked = string.Empty,
                Elapsed = TimeSpan.Zero
            };
        }

        /// <summary>
        /// **只读出"某个条目解密后的开头 N 字节"**（用户 2026-09-25 第 38 条）。
        ///
        /// <para>为什么需要它：7z 的 AES **没有密码校验位**（不像 WinZip AES 有 2 字节校验值），
        /// 所以"只验密码不读数据"在协议上做不到；但可以**只读开头几十字节**：密码对，解出来就是文件真正的开头
        /// （z7/zip/rar/mp4… 的魔数），密码错，解出来是随机字节 —— 一眼分得开。
        /// 代价是 7-Zip 的密钥派生（约 0.1–0.3 秒）+ 读几十字节，**与包多大无关**
        /// （这正是"包里没有小文件"时唯一便宜的验密码办法）。</para>
        ///
        /// <para>做法：<c>7z x -so 归档 条目 -p密码</c> 把内容流到标准输出，我们读够 <paramref name="maxBytes"/>
        /// 就**杀掉自己启动的这个进程树**（不变量 10：只杀自己启动的 PID 及其子进程），
        /// 于是一个 5 GB 的条目也只花零点几秒、一个字节都不落盘。</para>
        ///
        /// <para>⛔ 判据不在这里：本方法只负责"把开头那些字节拿回来"，怎么判由
        /// <c>Extraction/PasswordProbe</c> 决定（它必须保守 —— 看不出文件开头**不等于**密码错）。</para>
        /// </summary>
        /// <returns>读到的字节（可能少于 <paramref name="maxBytes"/>）；拿不到返回 null。</returns>
        public async Task<byte[]?> TryReadDecryptedPrefixAsync(
            string archivePath,
            string entryPath,
            string password,
            int maxBytes,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(archivePath) ||
                string.IsNullOrWhiteSpace(entryPath) ||
                maxBytes <= 0 ||
                !File.Exists(GetSevenZipPath()))
            {
                return null;
            }

            Process? process = null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = GetSevenZipPath(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    WorkingDirectory = AppContext.BaseDirectory

                    /*
                     * ⚠ 刻意**不设** StandardOutputEncoding：`-so` 吐的是**原始字节**，
                     * 用文本读取器会把非 UTF-8 的内容改坏（我们要拿它比对文件魔数）。
                     */
                };

                // 顺序与 BuildExtractArguments 同一口径：x -so 归档 -p密码 条目
                startInfo.ArgumentList.Add("x");
                startInfo.ArgumentList.Add("-so");
                startInfo.ArgumentList.Add("-sccUTF-8");
                startInfo.ArgumentList.Add(archivePath);
                startInfo.ArgumentList.Add("-p" + (password ?? string.Empty));
                startInfo.ArgumentList.Add(entryPath);

                process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

                if (!process.Start())
                {
                    return null;
                }

                var buffer = new byte[maxBytes];
                int total = 0;

                while (total < maxBytes)
                {
                    int read = await process.StandardOutput.BaseStream
                        .ReadAsync(buffer.AsMemory(total, maxBytes - total), timeoutCts.Token)
                        .ConfigureAwait(false);

                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                return total > 0 ? buffer[..total] : null;
            }
            catch
            {
                // 读不到（超时 / 进程起不来 / 输出被截断）：调用方按"说不准"处理（退回整包试解）。
                return null;
            }
            finally
            {
                /*
                 * 不管读没读够都要把进程收干净：`-so` 会把整个条目往下倒，
                 * 不杀它就会一直读那个 5 GB 的文件（磁盘白转，还可能占住归档）。
                 */
                await KillProcessTreeSafeAsync(process).ConfigureAwait(false);
                process?.Dispose();
            }
        }

        private async Task KillProcessTreeSafeAsync(Process? process)
        {
            int? pid = null;

            try
            {
                if (process != null)
                {
                    pid = process.Id;
                }
            }
            catch
            {
                pid = null;
            }

            try
            {
                if (process != null)
                {
                    try
                    {
                        process.CancelOutputRead();
                    }
                    catch
                    {
                    }

                    try
                    {
                        process.CancelErrorRead();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            bool exited = false;

            try
            {
                if (process != null)
                {
                    exited = process.WaitForExit(10000);
                }
            }
            catch
            {
                exited = false;
            }

            if (!exited && pid.HasValue)
            {
                RunTaskKillByPid(pid.Value);
            }

            await Task.Delay(800).ConfigureAwait(false);
        }

        private static void RunTaskKillByPid(int pid)
        {
            if (pid <= 0)
            {
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                psi.ArgumentList.Add("/PID");
                psi.ArgumentList.Add(pid.ToString());
                psi.ArgumentList.Add("/T");
                psi.ArgumentList.Add("/F");

                using Process taskkill = Process.Start(psi)!;
                taskkill.WaitForExit(5000);
            }
            catch
            {
            }
        }

        private static string MaskPassword(string password)
        {
            return PasswordMasker.Mask(password);
        }
    }
}
