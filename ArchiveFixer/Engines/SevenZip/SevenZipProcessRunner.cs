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

            ArchiveOperationResult result = await RunSevenZipAsync(arguments, password, cancellationToken)
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

            ArchiveOperationResult result = await RunSevenZipAsync(arguments, password, cancellationToken)
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

            using var timeoutCts = new CancellationTokenSource(DefaultSevenZipTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

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

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                    {
                        return;
                    }

                    lock (outputLock)
                    {
                        outputBuilder.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                    {
                        return;
                    }

                    lock (errorLock)
                    {
                        errorBuilder.AppendLine(e.Data);
                    }
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

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

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

                    string outputOnCancel;
                    string errorOnCancel;

                    lock (outputLock)
                    {
                        outputOnCancel = outputBuilder.ToString();
                    }

                    lock (errorLock)
                    {
                        errorOnCancel = errorBuilder.ToString();
                    }

                    outputOnCancel = PasswordMasker.Sanitize(outputOnCancel);
                    errorOnCancel = PasswordMasker.Sanitize(errorOnCancel);

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

                stopwatch.Stop();

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

                output = PasswordMasker.Sanitize(output);
                error = PasswordMasker.Sanitize(error);

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
                    ResolveOperation(arguments));
            }
            catch (Exception ex)
            {
                await KillProcessTreeSafeAsync(process).ConfigureAwait(false);

                stopwatch.Stop();

                string output;
                string errorText;

                lock (outputLock)
                {
                    output = outputBuilder.ToString();
                }

                lock (errorLock)
                {
                    errorText = errorBuilder.ToString();
                }

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
            EngineOperation? operation = null)
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
                Elapsed = elapsed
            };
        }

        /// <summary>
        /// 把"字段上像'非归档'"的结果修正成缺卷/缺首卷（不变量 7）。
        ///
        /// 为什么需要二次判定：7-Zip 对"只给非首卷"和"给了一个纯文本文件"报的是同一句
        /// <c>Cannot open the file as archive</c>（26.01 实测，退出码 2），
        /// 纯关键字分类一定会把缺卷误报成"不支持该格式"，用户就会以为文件坏了。
        /// 唯一可靠的判据是**文件名 + 同目录里有没有这一组的其它卷**：
        /// 目录里数出缺号 → 缺卷；数不出缺号但文件本身就叫 .001/.002/… → 缺首卷。
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
                "-bsp0",
                "-bd",
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

            return args;
        }

        public List<string> BuildTestArguments(string archivePath, string password)
        {
            var args = new List<string>
            {
                "t",
                "-bsp0",
                "-bd",
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
