using ArchiveFixer.Engines;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Packing
{
    /// <summary>打包用到的两个外部工具。</summary>
    public enum PackToolKind
    {
        /// <summary>
        /// 内置的 <c>tools\7zip\7z.exe</c>：建加密分卷、建**7z 外层容器**、以及列出 7z 外层容器的条目。
        /// </summary>
        SevenZip = 0,

        /// <summary>本机已装的 <c>Rar.exe</c>（或退一档的 <c>WinRAR.exe</c>）—— **绝不分发**。</summary>
        Rar = 1
    }

    /// <summary>一个打包步骤的进度（只带"到哪了"）。</summary>
    public sealed class PackStepProgress
    {
        /// <summary>百分比未知（还在扫描 / 这一行没给百分比）。</summary>
        public const int UnknownPercent = -1;

        /// <summary>0–100；未知时是 <see cref="UnknownPercent"/>。</summary>
        public int Percent { get; init; } = UnknownPercent;

        /// <summary>当前条目 / 状态行（引擎给什么就是什么）。</summary>
        public string Detail { get; init; } = string.Empty;
    }

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

    /// <summary>
    /// "起一次打包用的外部进程"这件事的抽象。
    ///
    /// <para>为什么要有接口：<see cref="PackingService"/> 里那些**判据**（没有 Rar.exe 时不许产生 rar、
    /// 取消时不许留半个 rar、第二步失败时第一步的分卷必须还在）都可以在不碰真 WinRAR 的前提下验证 ——
    /// 验收明确要求"用假的 runner 断言它没被调用"（docs/打包功能.md §9）。</para>
    /// </summary>
    public interface IPackProcessRunner
    {
        Task<PackStepResult> RunAsync(
            PackToolKind tool,
            IReadOnlyList<string> arguments,
            string usedPassword,
            IProgress<PackStepProgress>? progress,
            CancellationToken cancellationToken,
            string? workingDirectory = null);
    }

    /// <summary>
    /// 打包用的进程运行器：起进程、收输出、报进度、取消时收尸。
    ///
    /// <para><b>与 <c>Engines/SevenZip/SevenZipProcessRunner</c> 的关系</b>：做法**照抄**那一份，
    /// 不另造一套 —— <c>ArgumentList</c> 传参（禁止拼 cmd 字符串）、两路输出重定向、
    /// <c>ProcessOutputPump</c> 按 <c>\r</c>/<c>\b</c> 切片（不然 7z / RAR 的进度在解完之前一个都看不见）、
    /// 有界超时、取消只杀**自己启动的那个 PID 及其子进程**（<c>Kill(entireProcessTree: true)</c> 兜底
    /// <c>taskkill /PID &lt;pid&gt; /T /F</c>，**绝不按进程名批量杀</b>）。
    /// 差别只有一处：这里要按工具选**输出编码**（见 <see cref="ResolveOutputEncoding"/>）。</para>
    /// </summary>
    public sealed class PackProcessRunner : IPackProcessRunner
    {
        /// <summary>默认超时（与七压那条路同一档：30 分钟）。</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

        /// <summary>等两路输出泵收完的上限（进程已经退出，正常情况下是毫秒级）。</summary>
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(3);

        /// <summary>两次进度上报之间的最小间隔（进度行又密又长，不节流会把界面刷爆）。</summary>
        private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(250);

        private readonly ToolLocator _tools;
        private readonly TimeSpan _timeout;

        public PackProcessRunner()
            : this(ToolLocator.Default, DefaultTimeout)
        {
        }

        public PackProcessRunner(ToolLocator tools, TimeSpan? timeout = null)
        {
            _tools = tools ?? ToolLocator.Default;
            _timeout = timeout is { TotalMilliseconds: > 0 } ? timeout.Value : DefaultTimeout;
        }

        public ToolLocator Tools => _tools;

        /// <summary>这个工具在这台机器上有没有。</summary>
        public bool IsToolAvailable(PackToolKind tool)
        {
            return tool switch
            {
                PackToolKind.Rar => _tools.RarExists,
                _ => _tools.SevenZipExists
            };
        }

        /// <summary>这次会用的可执行文件路径。</summary>
        public string GetToolPath(PackToolKind tool)
        {
            return tool == PackToolKind.Rar ? _tools.RarExePath : _tools.SevenZipExePath;
        }

        public async Task<PackStepResult> RunAsync(
            PackToolKind tool,
            IReadOnlyList<string> arguments,
            string usedPassword,
            IProgress<PackStepProgress>? progress,
            CancellationToken cancellationToken,
            string? workingDirectory = null)
        {
            if (!IsToolAvailable(tool))
            {
                return new PackStepResult
                {
                    Success = false,
                    ExitCode = -1,
                    Message = tool == PackToolKind.Rar
                        ? _tools.DescribeNoRarAvailable()
                        : $"未找到 7-Zip 程序：{_tools.SevenZipExePath}"
                };
            }

            var stopwatch = Stopwatch.StartNew();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            object outputLock = new();
            object errorLock = new();

            Process? process = null;

            Task outputPump = Task.CompletedTask;
            Task errorPump = Task.CompletedTask;

            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            // 节流：只在百分比变了、且过了最小间隔之后才往上报。
            //
            // ⚠ **第一条进度必须放行**（`lastReportedPercent == int.MinValue`）：不这样做的话，
            // 一个几十毫秒就跑完的打包会一条进度都不报 —— 用户看到的是"进度条一直 0%，
            // 突然就完成了"，而"能看见进度"正是这条流水线的硬要求。
            int lastReportedPercent = int.MinValue;
            var lastReportAt = Stopwatch.StartNew();

            void HandleSegment(ProcessOutputSegment segment, StringBuilder buffer, object bufferLock)
            {
                int? percent = PackProgressParser.TryParse(segment.Text);

                if (percent.HasValue)
                {
                    bool isFirstReport = lastReportedPercent == int.MinValue;

                    if (percent.Value != lastReportedPercent && (isFirstReport || lastReportAt.Elapsed >= ProgressThrottle))
                    {
                        lastReportedPercent = percent.Value;
                        lastReportAt.Restart();

                        progress?.Report(new PackStepProgress
                        {
                            Percent = percent.Value,
                            Detail = PackProgressParser.ExtractDetail(segment.Text)
                        });
                    }

                    // 进度行不进日志缓冲区：又密又长，会把真正的结论淹掉。
                    return;
                }

                if (segment.Kind == ProcessOutputSegmentKind.Fragment && string.IsNullOrWhiteSpace(segment.Text))
                {
                    return;
                }

                lock (bufferLock)
                {
                    buffer.AppendLine(segment.Text);
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = GetToolPath(tool),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = ResolveOutputEncoding(tool),
                    StandardErrorEncoding = ResolveOutputEncoding(tool),

                    /*
                     * 进程在哪个目录里跑。
                     *
                     * ⚠ 2026-09-26 追加改口径：外层容器那一步装的是**逐个点名的分卷文件**
                     * （顶层、没有文件夹层），所以那一步要让进程在**落点目录**里执行、参数里只给文件名 ——
                     * 给绝对路径的话 7z / RAR 会把路径也存进归档（多出一层目录）。
                     * 其余步骤（切分卷、列条目）传 null = 照旧用程序目录。
                     */
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
                        ? AppContext.BaseDirectory
                        : workingDirectory
                };

                foreach (string argument in arguments)
                {
                    if (argument != null)
                    {
                        startInfo.ArgumentList.Add(argument);
                    }
                }

                process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

                if (!process.Start())
                {
                    stopwatch.Stop();

                    return new PackStepResult
                    {
                        Success = false,
                        ExitCode = -1,
                        Message = "无法启动：" + startInfo.FileName,
                        Elapsed = stopwatch.Elapsed
                    };
                }

                outputPump = ProcessOutputPump.PumpAsync(
                    process.StandardOutput,
                    segment => HandleSegment(segment, outputBuilder, outputLock),
                    linkedCts.Token);

                errorPump = ProcessOutputPump.PumpAsync(
                    process.StandardError,
                    segment => HandleSegment(segment, errorBuilder, errorLock),
                    linkedCts.Token);

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
                    bool isTimeout = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

                    await KillProcessTreeSafeAsync(process).ConfigureAwait(false);

                    stopwatch.Stop();

                    (string outputOnCancel, string errorOnCancel) = ReadBuffers(
                        outputBuilder,
                        errorBuilder,
                        outputLock,
                        errorLock,
                        usedPassword);

                    return new PackStepResult
                    {
                        Success = false,
                        Cancelled = !isTimeout,
                        TimedOut = isTimeout,
                        ExitCode = isTimeout ? -3 : -2,
                        StandardOutput = outputOnCancel,
                        StandardError = errorOnCancel,
                        Message = isTimeout
                            ? $"{DescribeTool(tool)} 执行超时，已强制结束进程"
                            : $"操作已取消，已强制结束 {DescribeTool(tool)} 进程",
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

                // 进程退出后管道里可能还有尾巴：必须等泵读完再取缓冲区，
                // 否则最后几行（常常正是结论那几行）会丢。
                await DrainPumpsAsync(outputPump, errorPump).ConfigureAwait(false);

                stopwatch.Stop();

                (string output, string error) = ReadBuffers(
                    outputBuilder,
                    errorBuilder,
                    outputLock,
                    errorLock,
                    usedPassword);

                int exitCode;

                try
                {
                    exitCode = process.ExitCode;
                }
                catch
                {
                    exitCode = -1;
                }

                return new PackStepResult
                {
                    Success = exitCode == 0,
                    ExitCode = exitCode,
                    StandardOutput = output,
                    StandardError = error,
                    Message = exitCode == 0
                        ? string.Empty
                        : $"{DescribeTool(tool)} 退出码 {exitCode}",
                    Elapsed = stopwatch.Elapsed
                };
            }
            catch (Exception ex)
            {
                await KillProcessTreeSafeAsync(process).ConfigureAwait(false);
                await DrainPumpsAsync(outputPump, errorPump).ConfigureAwait(false);

                stopwatch.Stop();

                (string output, string errorText) = ReadBuffers(
                    outputBuilder,
                    errorBuilder,
                    outputLock,
                    errorLock,
                    usedPassword);

                string exceptionMessage = PackPasswordGuard.Sanitize(ex.Message, usedPassword);

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    errorText += Environment.NewLine;
                }

                errorText += exceptionMessage;

                return new PackStepResult
                {
                    Success = false,
                    ExitCode = -1,
                    StandardOutput = output,
                    StandardError = errorText,
                    Message = exceptionMessage,
                    Elapsed = stopwatch.Elapsed
                };
            }
            finally
            {
                await DrainPumpsAsync(outputPump, errorPump).ConfigureAwait(false);

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
        /// 按工具选输出编码。
        ///
        /// <para>7-Zip 走 <c>-sccUTF-8</c>（命令行的参数模板在本目录内拼），所以按 UTF-8 解；
        /// <b>Rar.exe 的输出是控制台代码页</b>（本机实测中文 Windows 上是 936/GBK，
        /// 按 UTF-8 解会全是乱码 —— 分卷名带中文时校验就认不出条目了），
        /// 所以按当前区域的 OEM 代码页解。取不到就退回 UTF-8（宁可乱码也不抛）。</para>
        /// </summary>
        internal static Encoding ResolveOutputEncoding(PackToolKind tool)
        {
            if (tool != PackToolKind.Rar)
            {
                return Encoding.UTF8;
            }

            try
            {
                /*
                 * .NET Core 默认只带 Unicode 系编码；GBK 这类代码页编码要显式注册（程序集随运行时分发，不加包）。
                 *
                 * ⚠ 注册走 Helpers.CodePageEncodingBootstrap（幂等的一次性初始化），**不在这里就地注册**：
                 * 注册是进程级全局动作，散在某个 runner 里会让"谁先跑到"决定别处能不能用 GB18030 ——
                 * 测试里真的因此出现过顺序相关的偶发失败（见 PasswordBookTests 那条的注释）。
                 */
                Helpers.CodePageEncodingBootstrap.EnsureRegistered();

                int codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

                return codePage > 0 ? Encoding.GetEncoding(codePage) : Encoding.UTF8;
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        /// <summary>工具的人读名字（日志与提示用）。</summary>
        public static string DescribeTool(PackToolKind tool)
        {
            return tool == PackToolKind.Rar ? "Rar.exe" : "7-Zip";
        }

        private static async Task DrainPumpsAsync(Task outputPump, Task errorPump)
        {
            try
            {
                Task all = Task.WhenAll(outputPump, errorPump);

                await Task.WhenAny(all, Task.Delay(PumpDrainTimeout)).ConfigureAwait(false);
            }
            catch
            {
                // 泵内部已经把取消 / 管道关闭吞掉了，这里只是兜底。
            }
        }

        private static (string Output, string Error) ReadBuffers(
            StringBuilder outputBuilder,
            StringBuilder errorBuilder,
            object outputLock,
            object errorLock,
            string usedPassword)
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

            return (PackPasswordGuard.Sanitize(output, usedPassword), PackPasswordGuard.Sanitize(error, usedPassword));
        }

        /// <summary>
        /// 收尸：只杀**自己启动的这个 PID 及其子进程**（不变量 10）。
        ///
        /// <para>做法与 <c>SevenZipProcessRunner.KillProcessTreeSafeAsync</c> 一致：
        /// 先 <c>Kill(entireProcessTree: true)</c>，10 秒内没退再按 PID 兜底 <c>taskkill /T /F</c>。
        /// <b>绝不按进程名批量杀</c>（那会杀掉用户自己在跑的 7z / WinRAR）。</para>
        /// </summary>
        private static async Task KillProcessTreeSafeAsync(Process? process)
        {
            int? pid = null;

            try
            {
                pid = process?.Id;
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

            await Task.Delay(500).ConfigureAwait(false);
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
                psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("/T");
                psi.ArgumentList.Add("/F");

                using Process taskkill = Process.Start(psi)!;
                taskkill.WaitForExit(5000);
            }
            catch
            {
            }
        }
    }
}
