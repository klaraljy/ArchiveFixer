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
    /// **换引擎兜底**的调用器：用**用户自己装的** <c>WinRAR.exe</c> 解一份主引擎吃不下的归档
    /// （用户 2026-10-05 拍板"补"；真机现场见 AGENTS.md §11.5 最后一条）。
    ///
    /// <para><b>它解决的是什么</b>：真机同一份 ZIP（WinZip AES / 压缩法 99）、同一条密码 ——
    /// WinRAR 6.11 退出码 0、两卷原样解出；内置 7-Zip 26.03 退出码 2、<c>Wrong password</c>、
    /// 一个字节都没出来。而 <c>WrongPassword</c> 在"不换引擎"那一档（AGENTS.md §3）⇒
    /// 程序把"这个引擎吃不下这个 ZIP"如实报成了「达到密码尝试上限」，是一条假结论。</para>
    ///
    /// <para><b>许可证边界</b>（AGENTS.md §2 / §3.1、docs/引擎与外部工具.md §4）：
    /// <c>WinRAR.exe</c> 是共享软件 —— ⛔ 绝不打包、绝不复制、绝不捆绑，本类只做
    /// "用户自装、我们检测与调用"（路径唯一来源仍是 <see cref="ToolLocator"/>）。
    /// ⛔ 它**不进引擎优先级表**：平时 Zip / 7z 照旧由 7-Zip 解，只有兜底那一档才轮到它。</para>
    ///
    /// <para><b>外部进程 8 条纪律</b>（不变量 10）逐条对应：<see cref="ProcessStartInfo.ArgumentList"/>
    /// 安全传参（⛔ 不拼 <c>cmd.exe</c> 字符串）、重定向 stdout/stderr、支持超时、支持取消、
    /// 正确关句柄、**只终止自己起的那个 PID 及其子进程**（⛔ 绝不按进程名批量杀 WinRAR.exe）。</para>
    ///
    /// <para><b>实测出来的三条硬事实</b>（2026-10-05，本机 <c>C:\Program Files\WinRAR\WinRAR.exe</c>
    /// 6.11 + 内置 7-Zip 26.03 造的 <c>-mem=AES256</c> ZIP，全部经 <c>ArgumentList</c> 调用）：</para>
    /// <list type="number">
    /// <item><description><b>⛔ 必须带 <c>-cfg-</c></b>：本机 <c>HKCU\SOFTWARE\WinRAR\Extraction\Profile</c>
    /// 里 <c>ExtrDelArc = 2</c>（界面上「解压后删除压缩包 = 总是」）—— 不加 <c>-cfg-</c> 时
    /// WinRAR **真的会把源包删掉**（实测：解压成功、退出码 0、源文件消失），那是不可逆的数据丢失，
    /// 与不变量 1（源包原地不动）直接冲突。带上 <c>-cfg-</c>（"忽略配置文件和 RAR 环境变量"）
    /// 之后 5 种开关组合全部实测：解压正常、退出码 0、**源文件原地不动**。</description></item>
    /// <item><description><b>⛔ 空密码必须写 <c>-p-</c></b>：裸的 <c>-p</c> 会让 WinRAR **弹窗问密码**
    /// 并永久等待（实测：进程挂住不退出，只能杀掉）。<c>-p-</c> = 不询问密码，实测退出码 10、
    /// 不挂起（与 <c>UnRarProcessRunner</c> 那条实测记录同源）。</description></item>
    /// <item><description><b>退出码与 7-Zip / 文档不完全一样</b>（实测，别照抄文档）：
    /// 正确密码 = <c>0</c>；**密码不对 = <c>10</c>**（不是文档里的 11）；不是归档的文件 = <c>1</c>；
    /// 文档里的 <c>11</c>（密码错误）与 <c>3</c>（CRC / 数据错）一并按"这个候选没成"处置。
    /// ⚠ GUI 版**不往 stdout/stderr 写任何东西**（实测两个管道全程 0 字节，<c>-ierr</c> 也一样），
    /// 所以日志里能说的是"退出码是多少"，而不是"引擎原话"。</description></item>
    /// </list>
    /// </summary>
    public sealed class WinRarProcessRunner
    {
        /// <summary>与 7-Zip / UnRAR 两侧同一个口径：30 分钟。超大包可能很慢，超时会被明确报出来。</summary>
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

        /// <summary>等两路输出读完的上限（GUI 版正常情况下一个字节都没有，这里只是不许无界等待）。</summary>
        private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(3);

        private readonly ToolLocator _tools;

        public WinRarProcessRunner()
            : this(ToolLocator.Default)
        {
        }

        public WinRarProcessRunner(ToolLocator tools)
        {
            _tools = tools ?? ToolLocator.Default;
        }

        /// <summary>外部工具路径统一由 <see cref="ToolLocator"/> 解析（本类不拼路径）。</summary>
        public ToolLocator Tools => _tools;

        /// <summary>本机有没有可用的 WinRAR.exe（没有 ⇒ 兜底什么都不做，并如实说清）。</summary>
        public bool IsAvailable => _tools.WinRarExeExists;

        /// <summary>实际用的 WinRAR.exe 路径（没找到时是空串）。</summary>
        public string ExePath => _tools.WinRarExePath ?? string.Empty;

        /// <summary>它的文件版本（取不到是 <c>unknown</c>；报告与日志都要带，不变量 14）。</summary>
        public string Version => _tools.WinRarVersion;

        /// <summary>报告/日志里用的显示名（措辞只做事实性说明，docs/引擎与外部工具.md §6）。</summary>
        public const string DisplayName = "WinRAR 命令行（本机自装，只检测与调用）";

        /// <summary>
        /// 解压参数：<c>x -cfg- -ibck -o+ -y -p&lt;密码&gt; &lt;归档&gt; &lt;目标目录&gt;\</c>。
        ///
        /// <list type="bullet">
        /// <item><description><c>x</c> = 保留包内路径（⛔ 不用 <c>e</c>：摊平必然撞名）；</description></item>
        /// <item><description><c>-cfg-</c> = 忽略本机配置（见类注释第 1 条实测：不加它会删源包）；</description></item>
        /// <item><description><c>-ibck</c> = 后台模式（不弹进度窗口、不抢焦点，实测照旧拿得到退出码）；</description></item>
        /// <item><description><c>-o+</c> = 覆盖已有文件（落点是我们自己的暂存目录，产物要完整）；</description></item>
        /// <item><description><c>-y</c> = 所有询问都回答"是"（无人值守；配合 <c>-o+</c> 才有确定落位）。</description></item>
        /// </list>
        ///
        /// <para>目标目录**必须带尾随分隔符**：WinRAR 的位置参数靠它区分"目录"与"新文件名"
        /// （不带时会被当成文件名，实测报 <c>无法打开 &lt;目录&gt;.rar</c>）。</para>
        /// </summary>
        public List<string> BuildExtractArguments(string archivePath, string outputDirectory, string? password)
        {
            return new List<string>
            {
                "x",
                "-cfg-",
                "-ibck",
                "-o+",
                "-y",
                BuildPasswordArgument(password),
                archivePath,
                EnsureTrailingSeparator(outputDirectory)
            };
        }

        /// <summary>
        /// 密码参数：<c>-p&lt;密码&gt;</c>；空 / 未给时 <c>-p-</c>。
        ///
        /// ⛔ 绝不返回裸的 <c>-p</c>：那会让 WinRAR 弹窗问密码并**永久等待**
        /// （实测进程挂住，只能杀掉）—— 与 <c>UnRarProcessRunner</c> 那条实测记录同源。
        /// </summary>
        internal static string BuildPasswordArgument(string? password)
        {
            return string.IsNullOrEmpty(password) ? "-p-" : "-p" + password;
        }

        /// <summary>目标目录末尾补一个分隔符（WinRAR 靠它认"这是目录"）。</summary>
        internal static string EnsureTrailingSeparator(string? directory)
        {
            string text = directory ?? string.Empty;

            if (text.Length == 0)
            {
                return text;
            }

            char last = text[^1];

            return last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar
                ? text
                : text + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// 起进程、收输出、按退出码分类。返回的 <see cref="ArchiveOperationResult"/> 已盖引擎戳
        /// （<see cref="EngineIds.WinRarFallback"/> + 版本），溯源见不变量 14。
        /// </summary>
        public async Task<ArchiveOperationResult> ExtractAsync(
            string archivePath,
            string outputDirectory,
            string? password,
            CancellationToken cancellationToken = default)
        {
            if (!IsAvailable)
            {
                return ArchiveOperationResult.CreateFailure(
                        -1,
                        string.Empty,
                        string.Empty,
                        StatusText.ExtractFailed,
                        $"未找到 WinRAR（{_tools.DescribeWinRarResolution()}）",
                        EngineErrorTypes.EngineUnavailable,
                        TimeSpan.Zero,
                        PasswordMasker.Mask(password))
                    .StampEngine(EngineIds.WinRarFallback, DisplayName, Version);
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
                        PasswordMasker.Mask(password))
                    .StampEngine(EngineIds.WinRarFallback, DisplayName, Version);
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return ArchiveOperationResult.CreateFailure(
                        -1,
                        string.Empty,
                        string.Empty,
                        StatusText.ExtractFailed,
                        "输出目录为空",
                        EngineErrorTypes.UnknownError,
                        TimeSpan.Zero,
                        PasswordMasker.Mask(password))
                    .StampEngine(EngineIds.WinRarFallback, DisplayName, Version);
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);
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
                        PasswordMasker.Mask(password))
                    .StampEngine(EngineIds.WinRarFallback, DisplayName, Version);
            }

            List<string> arguments = BuildExtractArguments(archivePath, outputDirectory, password);

            var stopwatch = Stopwatch.StartNew();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            Process? process = null;
            Task<string>? outputTask = null;
            Task<string>? errorTask = null;

            using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ExePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = AppContext.BaseDirectory
                };

                foreach (string argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
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
                            "无法启动 WinRAR",
                            EngineErrorTypes.UnknownError,
                            stopwatch.Elapsed,
                            PasswordMasker.Mask(password))
                        .StampEngine(EngineIds.WinRarFallback, DisplayName, Version);
                }

                /*
                 * GUI 版不写任何输出（实测），但两路管道照旧要读干净 ——
                 * 万一某个版本 / 某个开关下它真的写了，我们才能把那几行留在结果里；
                 * 读不完的管道会把子进程堵死，这正是 8 条纪律里"重定向 stdout/stderr"的意义。
                 */
                outputTask = process.StandardOutput.ReadToEndAsync();
                errorTask = process.StandardError.ReadToEndAsync();

                /*
                 * 立刻关掉标准输入：万一 WinRAR 想"问密码"（裸 -p）或弹别的询问，
                 * 它拿到的是 EOF、当场失败，而不是把任务挂在那里等一个永远不会来的输入。
                 * （实测：裸 -p 会挂住不退出；我们一律用 -p<密码> / -p- 避免它问。）
                 */
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

                    return ArchiveOperationResult.CreateFailure(
                            isTimeout ? -3 : -2,
                            string.Empty,
                            string.Empty,
                            isTimeout ? StatusText.UnknownError : StatusText.Cancelled,
                            isTimeout
                                ? "WinRAR 执行超时，已强制结束进程"
                                : "操作已取消，已强制结束 WinRAR 进程",
                            isTimeout ? EngineErrorTypes.TimedOut : EngineErrorTypes.Cancelled,
                            stopwatch.Elapsed,
                            PasswordMasker.Mask(password))
                        .StampEngine(EngineIds.WinRarFallback, DisplayName, Version);
                }

                try
                {
                    process.WaitForExit();
                }
                catch
                {
                }

                // 进程已退出，但两路输出的尾巴还在管道里：等泵读完再取。
                await DrainOutputAsync(outputTask, errorTask).ConfigureAwait(false);

                stopwatch.Stop();

                Collect(outputTask, outputBuilder);
                Collect(errorTask, errorBuilder);

                int exitCode;

                try
                {
                    exitCode = process.ExitCode;
                }
                catch
                {
                    exitCode = -1;
                }

                return BuildResult(exitCode, outputBuilder.ToString(), errorBuilder.ToString(), password, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                await KillProcessTreeSafeAsync(process).ConfigureAwait(false);
                await DrainOutputAsync(outputTask, errorTask).ConfigureAwait(false);

                stopwatch.Stop();

                Collect(outputTask, outputBuilder);
                Collect(errorTask, errorBuilder);

                string exceptionMessage = PasswordMasker.Sanitize(ex.Message);

                if (errorBuilder.Length > 0)
                {
                    errorBuilder.AppendLine();
                }

                errorBuilder.Append(exceptionMessage);

                return ArchiveOperationResult.CreateFailure(
                        -1,
                        PasswordMasker.Sanitize(outputBuilder.ToString()),
                        PasswordMasker.Sanitize(errorBuilder.ToString()),
                        StatusText.ExtractFailed,
                        exceptionMessage,
                        EngineErrorTypes.UnknownError,
                        stopwatch.Elapsed,
                        PasswordMasker.Mask(password))
                    .StampEngine(EngineIds.WinRarFallback, DisplayName, Version);
            }
            finally
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
        /// 退出码 → 结果（本类的唯一结论出口，便于单测直接喂数字）。
        ///
        /// <para>映射依据**实测**（见类注释第 3 条）：<c>0</c> = 成功；
        /// <c>10</c>（实测的密码不对）/ <c>11</c>（文档的密码错误）= <see cref="EngineErrorTypes.WrongPassword"/>；
        /// <c>3</c> = 数据 / 校验和错；<c>255</c> = 用户中断；其余（含 <c>1</c> 警告、<c>2</c> 致命）如实报失败，
        /// ⛔ 不硬塞进"密码错误"那一档（那正是"把引擎吃不下说成密码不对"的老毛病）。</para>
        /// </summary>
        internal ArchiveOperationResult BuildResult(
            int exitCode,
            string? output,
            string? error,
            string? password,
            TimeSpan elapsed)
        {
            string safeOutput = PasswordMasker.Sanitize(output);
            string safeError = PasswordMasker.Sanitize(error);

            ArchiveOperationResult result = exitCode switch
            {
                0 => ArchiveOperationResult.CreateSuccess(0, safeOutput, safeError, elapsed, PasswordMasker.Mask(password)),

                10 or 11 => ArchiveOperationResult.CreateFailure(
                    exitCode,
                    safeOutput,
                    safeError,
                    StatusText.WrongPassword,
                    "WinRAR 报密码不对（没有解出任何文件）",
                    EngineErrorTypes.WrongPassword,
                    elapsed,
                    PasswordMasker.Mask(password)),

                3 => ArchiveOperationResult.CreateFailure(
                    exitCode,
                    safeOutput,
                    safeError,
                    StatusText.Corrupted,
                    "WinRAR 报校验和 / 数据错误",
                    EngineErrorTypes.CorruptedArchive,
                    elapsed,
                    PasswordMasker.Mask(password)),

                255 => ArchiveOperationResult.CreateFailure(
                    exitCode,
                    safeOutput,
                    safeError,
                    StatusText.Cancelled,
                    "WinRAR 被中断（退出码 255）",
                    EngineErrorTypes.Cancelled,
                    elapsed,
                    PasswordMasker.Mask(password)),

                _ => ArchiveOperationResult.CreateFailure(
                    exitCode,
                    safeOutput,
                    safeError,
                    StatusText.ExtractFailed,
                    $"WinRAR 执行失败（退出码 {exitCode}）",
                    EngineErrorTypes.UnknownError,
                    elapsed,
                    PasswordMasker.Mask(password))
            };

            result.CommandSummary = BuildCommandSummary(exitCode);

            return result.StampEngine(EngineIds.WinRarFallback, DisplayName, Version);
        }

        /// <summary>
        /// "这次到底拿什么参数调的 WinRAR"（**已脱敏**，只给详细日志用）。
        /// ⛔ 完整路径不进日志（§8）：只留命令、开关与归档**文件名**；<c>-p</c> 由脱敏兜底（不变量 5）。
        /// </summary>
        internal static string BuildCommandSummary(int exitCode)
        {
            /*
             * ⛔ 这里刻意**不**把参数表整份拼出来：调用点的 DumpArguments 会把目标目录这种
             * 用户路径也写进日志。摘要只回答"用什么命令、结果如何"，参数由 BuildExtractArguments
             * 单独可测（参数表本身不是隐私，落进日志的路径才是）。
             */
            return $"winrar x -cfg- -ibck -o+ -y -p****** <归档> <目标目录>\\（退出码 {exitCode}）";
        }

        private static void Collect(Task<string>? pump, StringBuilder builder)
        {
            if (pump == null || !pump.IsCompletedSuccessfully)
            {
                return;
            }

            try
            {
                builder.Append(pump.Result);
            }
            catch
            {
            }
        }

        /// <summary>等两路输出读完，**有上限**（GUI 版正常情况下一个字节都没有，这里只是不许无界等待）。</summary>
        private static async Task DrainOutputAsync(Task<string>? outputTask, Task<string>? errorTask)
        {
            if (outputTask == null && errorTask == null)
            {
                return;
            }

            try
            {
                Task all = Task.WhenAll(
                    outputTask ?? Task.FromResult(string.Empty),
                    errorTask ?? Task.FromResult(string.Empty));

                await Task.WhenAny(all, Task.Delay(OutputDrainTimeout)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 收尸：⛔ **只按自己起的那个 PID**（<c>Kill(entireProcessTree: true)</c>，兜底 <c>taskkill /PID &lt;pid&gt; /T /F</c>）——
        /// 不变量 10 明令禁止"按进程名批量杀 WinRAR.exe"（用户可能正开着自己的 WinRAR 在用）。
        /// </summary>
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
    }
}
