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

            ArchiveOperationResult result = await RunSevenZipAsync(arguments, password, cancellationToken);

            if (result.Success)
            {
                result.Status = StatusText.TestPassed;
                result.Message = StatusText.TestPassed;
                result.DetectedErrorType = "None";
            }
            else
            {
                string mappedStatus = SevenZipOutputParser.ErrorTypeToTaskStatus(result.DetectedErrorType);

                result.Status = mappedStatus switch
                {
                    StatusText.ExtractSuccess => StatusText.TestPassed,
                    StatusText.ExtractFailed => StatusText.TestFailed,
                    _ => mappedStatus
                };

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

            ArchiveOperationResult result = await RunSevenZipAsync(arguments, password, cancellationToken);

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
            return await RunSevenZipAsync(arguments, string.Empty, cancellationToken);
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
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    bool isTimeout = timeoutCts.IsCancellationRequested &&
                                     !cancellationToken.IsCancellationRequested;

                    await KillProcessTreeSafeAsync(process);

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

                return AnalyzeResult(exitCode, output, error, usedPassword, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                await KillProcessTreeSafeAsync(process);

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
            output = PasswordMasker.Sanitize(output);
            error = PasswordMasker.Sanitize(error);

            string combined = SevenZipOutputParser.CombineOutput(output, error);
            bool success = SevenZipOutputParser.LooksLikeSuccess(exitCode, output, error);

            string errorType = success
                ? "None"
                : SevenZipOutputParser.DetectSevenZipErrorType(exitCode, output, error);

            string status = SevenZipOutputParser.ErrorTypeToTaskStatus(errorType);
            string message = success
                ? "操作成功"
                : SevenZipOutputParser.ErrorTypeToMessage(errorType, combined);

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

            await Task.Delay(800);
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
