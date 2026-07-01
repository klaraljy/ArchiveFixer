using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    public class ExtractService
    {
        private static readonly TimeSpan DefaultSevenZipTimeout = TimeSpan.FromMinutes(30);

        public string GetSevenZipPath()
        {
            string localPath = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(localPath))
            {
                return localPath;
            }

            string testPath = @"D:\7-Zip\7z.exe";

            if (File.Exists(testPath))
            {
                return testPath;
            }

            return localPath;
        }

        public string GetSevenZipDllPath()
        {
            string localPath = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.dll");

            if (File.Exists(localPath))
            {
                return localPath;
            }

            string testPath = @"D:\7-Zip\7z.dll";

            if (File.Exists(testPath))
            {
                return testPath;
            }

            return localPath;
        }

        public bool CheckSevenZipExists()
        {
            return File.Exists(GetSevenZipPath());
        }

        public bool CheckSevenZipDllExists()
        {
            return File.Exists(GetSevenZipDllPath());
        }

        public async Task<SevenZipResult> TestArchiveAsync(
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
                return SevenZipResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    "测试失败",
                    "压缩包文件不存在",
                    "UnknownError",
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            List<string> arguments = BuildTestArguments(archivePath, password);

            SevenZipResult result = await RunSevenZipAsync(arguments, password, cancellationToken);

            if (result.Success)
            {
                result.Status = "测试通过";
                result.Message = "测试通过";
                result.DetectedErrorType = "None";
            }
            else
            {
                string mappedStatus = ProcessOutputHelper.ErrorTypeToTaskStatus(result.DetectedErrorType);

                result.Status = mappedStatus switch
                {
                    "解压成功" => "测试通过",
                    "解压失败" => "测试失败",
                    _ => mappedStatus
                };

                if (string.IsNullOrWhiteSpace(result.Message) ||
                    result.Message == "操作失败" ||
                    result.Message == "未知错误，请查看日志")
                {
                    result.Message = ProcessOutputHelper.ErrorTypeToMessage(
                        result.DetectedErrorType,
                        result.CombinedOutput);
                }
            }

            return result;
        }

        public async Task<SevenZipResult> ExtractArchiveAsync(
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
                return SevenZipResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    "解压失败",
                    "压缩包文件不存在",
                    "UnknownError",
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return SevenZipResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    "解压失败",
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
                string safeMessage = ProcessOutputHelper.SanitizePasswordText(ex.Message);

                return SevenZipResult.CreateFailure(
                    -1,
                    string.Empty,
                    safeMessage,
                    "权限不足",
                    "无法创建输出目录：" + safeMessage,
                    "AccessDenied",
                    TimeSpan.Zero,
                    MaskPassword(password));
            }

            List<string> arguments = BuildExtractArguments(archivePath, outputPath, password, options);

            SevenZipResult result = await RunSevenZipAsync(arguments, password, cancellationToken);

            if (result.Success)
            {
                result.Status = "解压成功";
                result.Message = "解压成功";
                result.DetectedErrorType = "None";
            }
            else
            {
                result.Status = ProcessOutputHelper.ErrorTypeToTaskStatus(result.DetectedErrorType);

                if (string.IsNullOrWhiteSpace(result.Message) ||
                    result.Message == "操作失败" ||
                    result.Message == "未知错误，请查看日志")
                {
                    result.Message = ProcessOutputHelper.ErrorTypeToMessage(
                        result.DetectedErrorType,
                        result.CombinedOutput);
                }
            }

            return result;
        }

        public async Task<SevenZipResult> RunSevenZipAsync(
            IEnumerable<string> arguments,
            CancellationToken cancellationToken = default)
        {
            return await RunSevenZipAsync(arguments, string.Empty, cancellationToken);
        }

        public async Task<SevenZipResult> RunSevenZipAsync(
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

                    return SevenZipResult.CreateFailure(
                        -1,
                        string.Empty,
                        string.Empty,
                        "解压失败",
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

                    outputOnCancel = ProcessOutputHelper.SanitizePasswordText(outputOnCancel);
                    errorOnCancel = ProcessOutputHelper.SanitizePasswordText(errorOnCancel);

                    return new SevenZipResult
                    {
                        Success = false,
                        ExitCode = isTimeout ? -3 : -2,
                        StandardOutput = outputOnCancel,
                        StandardError = errorOnCancel,
                        Status = isTimeout ? "未知错误" : "已取消",
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

                output = ProcessOutputHelper.SanitizePasswordText(output);
                error = ProcessOutputHelper.SanitizePasswordText(error);

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

                string exceptionMessage = ProcessOutputHelper.SanitizePasswordText(ex.Message);

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    errorText += Environment.NewLine;
                }

                errorText += exceptionMessage;

                string combined = ProcessOutputHelper.CombineOutput(output, errorText);
                string detectedErrorType = ProcessOutputHelper.DetectSevenZipErrorType(-1, output, errorText);

                return new SevenZipResult
                {
                    Success = false,
                    ExitCode = -1,
                    StandardOutput = ProcessOutputHelper.SanitizePasswordText(output),
                    StandardError = ProcessOutputHelper.SanitizePasswordText(errorText),
                    Status = ProcessOutputHelper.ErrorTypeToTaskStatus(detectedErrorType),
                    Message = ProcessOutputHelper.ErrorTypeToMessage(detectedErrorType, combined),
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

        public SevenZipResult AnalyzeResult(int exitCode, string output, string error)
        {
            return AnalyzeResult(exitCode, output, error, string.Empty, TimeSpan.Zero);
        }

        public SevenZipResult AnalyzeResult(
            int exitCode,
            string output,
            string error,
            string usedPassword,
            TimeSpan elapsed)
        {
            output = ProcessOutputHelper.SanitizePasswordText(output);
            error = ProcessOutputHelper.SanitizePasswordText(error);

            string combined = ProcessOutputHelper.CombineOutput(output, error);
            bool success = ProcessOutputHelper.LooksLikeSuccess(exitCode, output, error);

            string errorType = success
                ? "None"
                : ProcessOutputHelper.DetectSevenZipErrorType(exitCode, output, error);

            string status = ProcessOutputHelper.ErrorTypeToTaskStatus(errorType);
            string message = success
                ? "操作成功"
                : ProcessOutputHelper.ErrorTypeToMessage(errorType, combined);

            return new SevenZipResult
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

        private SevenZipResult CreateSevenZipMissingResult()
        {
            string expectedPath = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return new SevenZipResult
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = "7z不存在",
                Message = $"未找到 7-Zip 程序。已检查：{expectedPath} 和 D:\\7-Zip\\7z.exe",
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

            try
            {
                if (process != null)
                {
                    bool exited = process.WaitForExit(10000);

                    if (!exited && pid.HasValue)
                    {
                        RunTaskKillByPid(pid.Value);
                    }
                }
            }
            catch
            {
                if (pid.HasValue)
                {
                    RunTaskKillByPid(pid.Value);
                }
            }

            if (pid.HasValue)
            {
                RunTaskKillByPid(pid.Value);
            }

            KillResidualSevenZipProcesses();

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

        private void KillResidualSevenZipProcesses()
        {
            string expectedSevenZipPath;

            try
            {
                expectedSevenZipPath = Path.GetFullPath(GetSevenZipPath());
            }
            catch
            {
                expectedSevenZipPath = string.Empty;
            }

            KillProcessesByNameAndPath("7z", expectedSevenZipPath);
            KillProcessesByNameAndPath("7za", expectedSevenZipPath);
            KillProcessesByNameAndPath("7zr", expectedSevenZipPath);
            KillProcessesByNameAndPath("7zG", expectedSevenZipPath);
        }

        private static void KillProcessesByNameAndPath(string processName, string expectedPath)
        {
            Process[] processes;

            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                return;
            }

            foreach (Process p in processes)
            {
                try
                {
                    bool shouldKill = false;

                    if (string.IsNullOrWhiteSpace(expectedPath))
                    {
                        shouldKill = true;
                    }
                    else
                    {
                        string actualPath = string.Empty;

                        try
                        {
                            actualPath = p.MainModule?.FileName ?? string.Empty;
                        }
                        catch
                        {
                            shouldKill = true;
                        }

                        if (!string.IsNullOrWhiteSpace(actualPath))
                        {
                            shouldKill = string.Equals(
                                Path.GetFullPath(actualPath),
                                expectedPath,
                                StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    if (shouldKill && !p.HasExited)
                    {
                        try
                        {
                            p.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            try
                            {
                                p.Kill();
                            }
                            catch
                            {
                            }
                        }

                        try
                        {
                            p.WaitForExit(5000);
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        p.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string MaskPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "空密码";
            }

            return "******";
        }
    }
}
