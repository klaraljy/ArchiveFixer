using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.WinRar
{
    /// <summary>
    /// RARLAB <c>UnRAR.exe</c> 的调用器：只负责"怎么起进程、怎么收输出、怎么收尸"。
    ///
    /// <para>
    /// 边界（AGENTS.md §3.1 四条禁止项）：参数拼接、输出解析、退出码到错误的映射
    /// **全部只能存在于 <c>Engines/WinRar/</c> 内**。核心模块一律通过 <see cref="IArchiveEngine"/> 调用。
    /// 7-Zip 那一侧是 <c>SevenZipProcessRunner</c>，两者结构刻意对称 ——
    /// 第二个引擎的价值之一就是证明"换引擎不用改调度器"。
    /// </para>
    ///
    /// <para>
    /// 与 7-Zip 运行器的**实测差异**（写下来免得后来人以为是抄漏了）：
    /// <list type="number">
    /// <item><description><b>必须加 <c>-scf</c></b>：UnRAR 默认按系统 OEM 代码页输出（中文系统 = GBK），
    /// 而我们按 UTF-8 解码 → 不加这个开关中文条目名全是乱码（7.23 实测）。</description></item>
    /// <item><description><b>密码一律显式给</b>：<c>-p&lt;密码&gt;</c>，空密码/不传密码时给 <c>-p-</c>
    /// （= 不询问密码）。⛔ 绝不能传裸的 <c>-p</c>：那会让 UnRAR 去**问密码**，
    /// 而我们的标准输入是关着的 —— 实测结果是 "Enter password …" + "Read error in the file stdin" + 退出码 12，
    /// 于是"需要密码"会被误报成"读取错误"。</description></item>
    /// <item><description><b>不用 <c>-y</c></b>：<c>-y</c> 是"所有询问都回答是"，
    /// 它同时会把覆盖策略也变成"覆盖全部" —— 与我们"默认不覆盖"的落位约定（不变量 3）冲突。
    /// 覆盖怎么处理由显式的 <c>-o+ / -o- / -or</c> 决定，不用"全答是"这种粗开关。</description></item>
    /// <item><description><b>输出目录走 <c>-op&lt;路径&gt;</c></b>：它接受不带尾随分隔符的路径，
    /// 不像位置参数那样必须写成 <c>dest\</c>（RAR 6.10 起的行为，7.23 实测可用）。</description></item>
    /// <item><description><b>⛔ 不加 <c>-idq</c> / <c>-idp</c></b>：<c>-idq</c>（安静模式）实测**连进度一起关掉**
    /// （成功时输出 0 字节），<c>-idp</c> 关的正是百分比指示器。
    /// 这两个开关一加，界面就退回"处理中/完成"两态 —— 正是用户抱怨"卡死"的成因。
    /// 实测 UnRAR **默认**就带百分比进度，所以这里什么都不用加（见
    /// <see cref="UnRarProgressParser"/>）。</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class UnRarProcessRunner
    {
        /// <summary>与 7-Zip 侧同一个口径：30 分钟。RAR 的固实大包可能很慢，给足时间；超时会被明确报出来。</summary>
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

        /// <summary>等两路输出泵收完的上限（进程已经退出，正常情况下是毫秒级）。</summary>
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(3);

        private readonly ToolLocator _tools;

        public UnRarProcessRunner()
            : this(ToolLocator.Default)
        {
        }

        public UnRarProcessRunner(ToolLocator tools)
        {
            _tools = tools ?? ToolLocator.Default;
        }

        /// <summary>外部工具路径统一由 <see cref="ToolLocator"/> 解析（本类不拼路径）。</summary>
        public ToolLocator Tools => _tools;

        public bool CheckUnRarExists() => _tools.UnRarExists;

        public string GetUnRarPath() => _tools.UnRarExePath;

        /// <summary>
        /// 列表：<c>unrar lt -scf -c- -p&lt;密码&gt; &lt;包&gt;</c>。
        ///
        /// <c>lt</c> = 技术列表（稳定键值，见 <see cref="UnRarListParser"/>）；
        /// <c>-c-</c> = 不显示归档注释（注释是自由文本，混进列表只会污染解析）。
        /// </summary>
        public List<string> BuildListArguments(string archivePath, string? password)
        {
            return new List<string>
            {
                "lt",
                "-scf",
                "-c-",
                BuildPasswordArgument(password),
                archivePath
            };
        }

        /// <summary>测试：<c>unrar t -scf -c- -p&lt;密码&gt; &lt;包&gt;</c>。</summary>
        public List<string> BuildTestArguments(string archivePath, string? password)
        {
            return new List<string>
            {
                "t",
                "-scf",
                "-c-",
                BuildPasswordArgument(password),
                archivePath
            };
        }

        /// <summary>
        /// 解压：<c>unrar x -scf -c- [-kb] &lt;覆盖档&gt; -op&lt;输出目录&gt; -p&lt;密码&gt; &lt;包&gt;</c>。
        ///
        /// <c>x</c> = 保留包内路径（**永不**用 <c>e</c>：摊平必然撞名，
        /// 见 `docs/WinRAR功能参考.md` §1.13）。
        /// </summary>
        public List<string> BuildExtractArguments(
            string archivePath,
            string outputPath,
            string? password,
            ExtractOptions options,
            bool keepBrokenFiles)
        {
            options ??= new ExtractOptions();
            options.Normalize();

            var args = new List<string>
            {
                "x",
                "-scf",
                "-c-"
            };

            if (keepBrokenFiles)
            {
                /*
                 * 保留受损文件（WinRAR 的"保留受损的文件"，默认关）。
                 *
                 * ⚠ 它**只**决定"校验失败的半成品留不留在磁盘上"，
                 * **不改变**退出码与错误分类 —— 所以任务状态仍然是失败 / 部分完成，
                 * 绝不会因为这个开关显示成功（不变量 6）。
                 */
                args.Add("-kb");
            }

            args.Add(GetOverwriteArgument(options.OverwriteMode));
            args.Add("-op" + outputPath);
            args.Add(BuildPasswordArgument(password));
            args.Add(archivePath);

            /*
             * 只解指定的条目（密码预检：先解"最小的那一个"验密码，再决定要不要跑整卷 —— 用户 2026-09-25 第 37 条）。
             * UnRAR 的用法是 `unrar x 归档 条目…`，条目名要给在归档**之后**。
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
        /// 覆盖模式 → UnRAR 开关。
        ///
        /// ⚠ <c>AutoRenameExisting</c>（"把已存在的那个改名让位"）UnRAR **没有**对应开关，
        /// 这里落到 <c>-o-</c>（不覆盖、保留已存在的文件）—— 方向与不变量 3 一致：
        /// 宁可少落一个产物，也绝不默认覆盖用户已有文件。差异在
        /// <see cref="UnRarEngine"/> 的"已知问题"里如实登记。
        /// </summary>
        public string GetOverwriteArgument(string? overwriteMode)
        {
            return overwriteMode switch
            {
                "SkipExisting" => "-o-",
                "OverwriteAll" => "-o+",
                "AutoRenameExtracted" => "-or",
                "AutoRenameExisting" => "-o-",
                _ => "-o-"
            };
        }

        /// <summary>
        /// 密码参数：<c>-p&lt;密码&gt;</c>；空 / 未给时 <c>-p-</c>。
        ///
        /// ⛔ 绝不返回裸的 <c>-p</c>：那会让 UnRAR 交互式问密码（见类注释的实测记录）。
        /// </summary>
        internal static string BuildPasswordArgument(string? password)
        {
            return string.IsNullOrEmpty(password) ? "-p-" : "-p" + password;
        }

        public async Task<ArchiveOperationResult> TestArchiveAsync(
            string archivePath,
            string? password,
            CancellationToken cancellationToken = default)
        {
            return await TestArchiveAsync(archivePath, password, cancellationToken, null).ConfigureAwait(false);
        }

        /// <summary>带进度 / 卡住提示的测试（<paramref name="progressContext"/> 可空）。</summary>
        public async Task<ArchiveOperationResult> TestArchiveAsync(
            string archivePath,
            string? password,
            CancellationToken cancellationToken,
            EngineProgressContext? progressContext)
        {
            if (!CheckUnRarExists())
            {
                return CreateEngineMissingResult();
            }

            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return ArchiveOperationResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    StatusText.TestFailed,
                    "压缩包文件不存在",
                    EngineErrorTypes.UnknownError,
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            List<string> arguments = BuildTestArguments(archivePath, password);

            ArchiveOperationResult result = await RunAsync(arguments, password, cancellationToken, progressContext)
                .ConfigureAwait(false);

            if (result.Success)
            {
                result.Status = StatusText.TestPassed;
                result.Message = StatusText.TestPassed;
                result.DetectedErrorType = "None";
            }
            else
            {
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

            return result;
        }

        public async Task<ArchiveOperationResult> ExtractArchiveAsync(
            string archivePath,
            string outputPath,
            string? password,
            ExtractOptions options,
            bool keepBrokenFiles,
            CancellationToken cancellationToken = default)
        {
            return await ExtractArchiveAsync(
                    archivePath,
                    outputPath,
                    password,
                    options,
                    keepBrokenFiles,
                    cancellationToken,
                    null)
                .ConfigureAwait(false);
        }

        /// <summary>带进度 / 卡住提示的解压（<paramref name="progressContext"/> 可空）。</summary>
        public async Task<ArchiveOperationResult> ExtractArchiveAsync(
            string archivePath,
            string outputPath,
            string? password,
            ExtractOptions options,
            bool keepBrokenFiles,
            CancellationToken cancellationToken,
            EngineProgressContext? progressContext)
        {
            options ??= new ExtractOptions();
            options.Normalize();

            if (!CheckUnRarExists())
            {
                return CreateEngineMissingResult();
            }

            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return ArchiveOperationResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    StatusText.ExtractFailed,
                    "压缩包文件不存在",
                    EngineErrorTypes.UnknownError,
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
                    EngineErrorTypes.UnknownError,
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
                    EngineErrorTypes.AccessDenied,
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            List<string> arguments = BuildExtractArguments(archivePath, outputPath, password, options, keepBrokenFiles);

            ArchiveOperationResult result = await RunAsync(arguments, password, cancellationToken, progressContext)
                .ConfigureAwait(false);

            if (result.Success)
            {
                result.Status = StatusText.ExtractSuccess;
                result.Message = StatusText.ExtractSuccess;
                result.DetectedErrorType = "None";
            }
            else
            {
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

            return result;
        }

        /// <summary>
        /// 起进程、收输出、按退出码与输出分类。
        ///
        /// 外部进程的 8 条纪律（不变量 10）逐条对应：
        /// <see cref="ProcessStartInfo.ArgumentList"/> 安全传参（不拼 cmd 字符串）、重定向 stdout/stderr、
        /// 超时、取消、正确关句柄、只终止自己起的 PID 及其子进程。
        /// </summary>
        public async Task<ArchiveOperationResult> RunAsync(
            IEnumerable<string> arguments,
            string? usedPassword,
            CancellationToken cancellationToken = default)
        {
            return await RunAsync(arguments, usedPassword, cancellationToken, null).ConfigureAwait(false);
        }

        /// <summary>
        /// 起进程、收输出、按退出码与输出分类。
        ///
        /// 外部进程的 8 条纪律（不变量 10）逐条对应：
        /// <see cref="ProcessStartInfo.ArgumentList"/> 安全传参（不拼 cmd 字符串）、重定向 stdout/stderr、
        /// 超时、取消、正确关句柄、只终止自己起的 PID 及其子进程。
        ///
        /// <para>
        /// <b>进度与卡住提示的接线</b>与 7-Zip 那一侧完全对称（见
        /// <c>SevenZipProcessRunner.RunSevenZipAsync</c>）：解析器只在本命名空间内
        /// （<see cref="UnRarProgressParser"/>），经节流后投递；返回之前必定收口，不会有迟到回调。
        /// </para>
        /// </summary>
        public async Task<ArchiveOperationResult> RunAsync(
            IEnumerable<string> arguments,
            string? usedPassword,
            CancellationToken cancellationToken,
            EngineProgressContext? progressContext)
        {
            if (!CheckUnRarExists())
            {
                return CreateEngineMissingResult();
            }

            var stopwatch = Stopwatch.StartNew();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            object outputLock = new();
            object errorLock = new();

            Process? process = null;

            /*
             * UnRAR **默认就带百分比进度**（实测），所以没有"开关要不要打开"这一说；
             * 但列目录（lt）的输出是要被逐行解析的，那边绝不能把进度解析面引进来 ——
             * 只有"会长时间跑"的三件事才解析：解压 / 测试 / 更新。
             */
            EngineOperation? operation = ResolveOperation(arguments);
            string? archivePath = FindArchiveArgument(arguments);

            bool progressExpected = operation == EngineOperation.Extract || operation == EngineOperation.Test;
            EngineProgressParser? progressParser = progressExpected ? UnRarProgressParser.TryParse : null;

            ArchiveProgressReporter reporter = new(progressContext?.Progress);

            EngineOutputActivityMonitor? monitor = progressContext?.Stalled != null
                ? new EngineOutputActivityMonitor(progressContext.StallThreshold, progressContext.Stalled)
                : null;

            var outputCollector = new EngineOutputCollector(outputBuilder, outputLock, progressParser, reporter, monitor);
            var errorCollector = new EngineOutputCollector(errorBuilder, errorLock, progressParser, reporter, monitor);

            using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
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
                    FileName = GetUnRarPath(),
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
                        "无法启动 UnRAR",
                        EngineErrorTypes.UnknownError,
                        stopwatch.Elapsed,
                        MaskPassword(usedPassword));
                }

                /*
                 * 旧写法是 BeginOutputReadLine：UnRAR 的百分比是**同一个文件那一行内用退格回写**的，
                 * 按行读意味着"一个 5GB 的单文件在解完之前一条进度都看不到"（见 ProcessOutputPump）。
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

                // 立刻关掉标准输入：UnRAR 一旦想"问密码"就拿到 EOF 立刻失败，
                // 而不是把整个任务挂在那里等一个永远不会来的输入（我们一律用 -p/-p- 避免它问）。
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

                    (string outputOnCancel, string errorOnCancel) = ReadBuffers(outputBuilder, errorBuilder, outputLock, errorLock);

                    return new ArchiveOperationResult
                    {
                        Success = false,
                        ExitCode = isTimeout ? -3 : -2,
                        StandardOutput = outputOnCancel,
                        StandardError = errorOnCancel,
                        Status = isTimeout ? StatusText.UnknownError : StatusText.Cancelled,
                        Message = isTimeout
                            ? "UnRAR 执行超时，已强制结束进程"
                            : "操作已取消，已强制结束 UnRAR 进程",
                        DetectedErrorType = isTimeout ? EngineErrorTypes.TimedOut : EngineErrorTypes.Cancelled,
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

                // 进程已退出，但两路输出的尾巴还在管道里：等泵读完再取缓冲区。
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

                return AnalyzeResult(exitCode, output, error, usedPassword, stopwatch.Elapsed, archivePath, operation, arguments);
            }
            catch (Exception ex)
            {
                await KillProcessTreeSafeAsync(process).ConfigureAwait(false);

                // 先把两路输出收干净再读缓冲区（与 7-Zip 侧同一理由：异常路径上别丢最后几行）。
                await DrainPumpsAsync(outputPump, errorPump).ConfigureAwait(false);

                stopwatch.Stop();

                (string output, string errorText) = ReadBuffers(outputBuilder, errorBuilder, outputLock, errorLock);

                string exceptionMessage = PasswordMasker.Sanitize(ex.Message);

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    errorText += Environment.NewLine;
                }

                errorText += exceptionMessage;

                string combined = UnRarOutputParser.CombineOutput(output, errorText);
                string detectedErrorType = UnRarOutputParser.DetectErrorType(-1, output, errorText, operation);

                return new ArchiveOperationResult
                {
                    Success = false,
                    ExitCode = -1,
                    StandardOutput = output,
                    StandardError = errorText,
                    Status = UnRarOutputParser.ErrorTypeToTaskStatus(detectedErrorType),
                    Message = UnRarOutputParser.ErrorTypeToMessage(detectedErrorType, combined),
                    DetectedErrorType = detectedErrorType,
                    UsedPasswordMasked = MaskPassword(usedPassword),
                    Elapsed = stopwatch.Elapsed
                };
            }
            finally
            {
                // 收口顺序与 7-Zip 侧一致（见那边的说明）：停看门狗 → 收完输出泵 → 掐断回调出口 → 处置进程。
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

        /// <summary>
        /// 等两路输出泵收完，**有上限**（与 7-Zip 侧同一个理由：禁止无界等待，AGENTS.md §9）。
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
            }
        }

        /// <summary>
        /// 分析一次 UnRAR 调用的结果（运行器唯一的结果出口，便于单测直接喂文本）。
        /// </summary>
        public ArchiveOperationResult AnalyzeResult(
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
                : UnRarOutputParser.DetectErrorType(exitCode, output, error, operation);

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
                UsedPasswordMasked = MaskPassword(usedPassword),
                Elapsed = elapsed,
                CommandSummary = BuildCommandSummary(arguments)
            };
        }

        /// <summary>
        /// "这次到底拿什么参数调的 UnRAR"（**已脱敏**，只给详细日志用）。
        /// 形状与 7-Zip 侧同一个口径：命令 + 开关 + 归档**文件名** + 条目名；
        /// ⛔ 完整路径不进日志（§8），<c>-p</c> / <c>-hp</c> 由脱敏兜底（不变量 5）。
        /// </summary>
        internal static string BuildCommandSummary(IEnumerable<string>? arguments)
        {
            if (arguments == null)
            {
                return string.Empty;
            }

            var parts = new List<string> { "unrar" };
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
                    isFirst = false;
                    parts.Add(argument.Trim());
                    continue;
                }

                if (argument.StartsWith("-", StringComparison.Ordinal))
                {
                    parts.Add(argument.Trim());
                    continue;
                }

                if (!sawArchive)
                {
                    sawArchive = true;
                    parts.Add(Path.GetFileName(argument));
                    continue;
                }

                /*
                 * 归档之后的位置通常是两个东西：**输出目录**（`unrar x <包> <目录>\`）与**条目名**。
                 * 只留条目名 —— 目录进去既没有信息量、又会把用户的个人路径写进日志（§8 隐私红线）。
                 */
                if (argument.Contains('\\') || argument.Contains('/'))
                {
                    continue;
                }

                parts.Add(argument.Trim());
            }

            return PasswordMasker.Sanitize(string.Join(" ", parts));
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

        /// <summary>
        /// 从参数表里认出这次跑的是哪个命令（<c>lt</c> / <c>t</c> / <c>x</c>）。
        ///
        /// 为什么要它：**同一段输出在不同命令下的含义不同** —— 加密头的包在 <c>lt</c> 上失败
        /// 说明"名字读不出来"，在 <c>x</c> 上失败只说明"这个候选密码不对"（还要接着试下一个）。
        /// 第一条非 <c>-</c> 参数就是命令（UnRAR 的命令永远在最前）。
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

                if (command.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                return command switch
                {
                    "l" or "lt" or "lta" or "lb" or "v" or "vt" or "vb" => EngineOperation.List,
                    "t" => EngineOperation.Test,
                    "x" or "e" => EngineOperation.Extract,
                    _ => null
                };
            }

            return null;
        }

        /// <summary>
        /// 从参数表里找出归档路径（"缺卷"的二次判定与消息要用）。
        /// 命令字（<c>lt</c>）与开关都不算路径 —— 与 7-Zip 侧同一个坑：
        /// 曾经的写法返回的是命令字本身，于是二次判定永远在判一个叫 "x" 的文件。
        /// 这里用**跳过命令 + 跳过开关 + 跳过 <c>-op</c> 的值**的写法。
        /// </summary>
        internal static string? FindArchiveArgument(IEnumerable<string>? arguments)
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
                    isFirst = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                return argument;
            }

            return null;
        }

        private ArchiveOperationResult CreateEngineMissingResult()
        {
            /*
             * 引擎不可用时的落点。
             *
             * 刻意**不**复用 StatusText.SevenZipMissing（那是"7z不存在"的文案）：
             * 报一句"7z不存在"而其实是 UnRAR 没找到，只会把用户带偏。
             * 状态用既有的"解压失败"+"明确的消息"，不新造状态常量（StatusText 是别人在改的文件）。
             */
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.ExtractFailed,
                Message = $"未找到 RAR 解压引擎 UnRAR：{_tools.UnRarExePath}（{_tools.DescribeUnRarResolution()}）",
                DetectedErrorType = EngineErrorTypes.EngineUnavailable,
                UsedPasswordMasked = string.Empty,
                Elapsed = TimeSpan.Zero
            };
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

            // ⚠ 只按**自己起的那个 PID** 收尸（不变量 10：禁止按进程名批量杀 UnRAR.exe）。
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

        private static string MaskPassword(string? password)
        {
            return PasswordMasker.Mask(password);
        }
    }
}
