using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ArchiveFixer.Services;

/// <summary>
/// **生命周期**：日志根目录的初始化、一次运行一个 <c>run_*</c> 文件夹、一次操作一个
/// <c>operations\NNN-名称-时间</c> 文件夹（四份文件 + manifest + summary 全落在里面）、
/// 旧平铺日志迁移、旧运行目录回收、环境头。
///
/// <para>为什么单独一个类：这些事只在**开工/收工**发生，跑起来之后一行日志都不经过它们。
/// 以前它们和"写一行日志"挤在同一个类型上，于是每读一次写入路径都要先穿过目录命名、
/// 保留策略、环境头这一大段。抽出来以后，目录与保留口径只在这一处。</para>
///
/// <para>⛔ 一个字面量都不许改：目录名（<c>run_yyyyMMdd_HHmmss_fff</c>、<c>NNN-名称-HHmmss</c>）、
/// 四份文件名（<c>operation.log</c> / <c>operation.user.log</c> / <c>operation.jsonl</c> /
/// <c>operation.trace.jsonl</c>）、manifest 的 schema 与字段、summary 的占位 JSON、
/// 保留天数/总量/最少份数 —— 用户与守门用例（<c>LogV3Tests</c> / <c>LogVolumePolicyTests</c>）
/// 都是按这些名字认的。</para>
///
/// <para>⛔ 失败模型也与拆分前一致：初始化失败 ⇒ 关文件日志并清空当前路径，**绝不让程序起不来**；
/// 清理/迁移失败 ⇒ 静默记住（下次启动再收），不抛。</para>
/// </summary>
internal sealed class LogLifecycle
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    /// <summary>日志保留天数（第 44 条追加；固定策略，界面上没有开关 —— 要改告诉我）。</summary>
    internal const int RetentionDays = 30;

    /// <summary>日志目录总量上限（超过就从最旧的开始删）。</summary>
    internal const long RetentionTotalBytes = 100L * 1024 * 1024;

    /// <summary>无论如何至少留最近这么多份（用户随时可能要看"昨天那一次"）。</summary>
    internal const int RetentionMinFiles = 10;

    private readonly PathService _pathService;
    private readonly LogOperationContext _context;
    private readonly LogFileWriter _files;
    private readonly LogDiagnosticWriter _diagnostics;

    internal LogLifecycle(
        PathService pathService,
        LogOperationContext context,
        LogFileWriter files,
        LogDiagnosticWriter diagnostics)
    {
        _pathService = pathService;
        _context = context;
        _files = files;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// 初始化日志系统。
    /// </summary>
    internal void Initialize(bool enableFileLog = true)
    {
        _context.EnableFileLog = enableFileLog;

        try
        {
            _pathService.EnsureBaseDirectories();
            MigrateLegacyFlatLogs();

            DateTime started = ArchiveFixer.Helpers.Clock.Now;
            _context.RunDirectory = Path.Combine(
                _pathService.LogsDirectory,
                "run_" + started.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(_context.RunDirectory);

            BeginOperationFolder("启动");

            if (_context.EnableFileLog)
            {
                string header = "日志初始化";

                File.WriteAllText(
                    _context.MainLogPath,
                    LogFileWriter.FormatLogLine(started, "INFO", header) + Environment.NewLine,
                    Encoding.UTF8);

                File.WriteAllText(
                    _context.JsonlPath,
                    LogJsonRecord.Build(new OperationLogItem("INFO", header, string.Empty), _context.RunContext, null, null, 0, _context.RunId, Environment.ProcessId, Environment.CurrentManagedThreadId, _context.OperationId)
                    + Environment.NewLine,
                    Encoding.UTF8);

                File.WriteAllText(_context.TracePath, string.Empty, Encoding.UTF8);

                WriteEnvironmentHeader();
                CleanupOldRunDirectories();
            }
        }
        catch
        {
            _context.EnableFileLog = false;
            _context.MainLogPath = string.Empty;
        }
    }

    /// <summary>
    /// 开启一次操作日志：在本次运行目录下新建 operations\NNN-名称-时间，
    /// 四份日志与 manifest/summary 全部落在同一个操作文件夹里。
    /// </summary>
    internal void BeginOperationFolder(string operationName)
    {
        if (string.IsNullOrWhiteSpace(_context.RunDirectory))
        {
            return;
        }

        DateTime started = ArchiveFixer.Helpers.Clock.Now;
        _context.OperationIndex++;
        _context.OperationName = string.IsNullOrWhiteSpace(operationName) ? "操作" : operationName.Trim();
        _context.OperationId = $"{_context.RunId}-{_context.OperationIndex:D3}";
        string safeName = SanitizeFolderName(_context.OperationName);
        string operationFolder = Path.Combine(
            _context.RunDirectory,
            "operations",
            $"{_context.OperationIndex:D3}-{safeName}-{started.ToString("HHmmss", CultureInfo.InvariantCulture)}");

        int suffix = 1;
        string candidate = operationFolder;

        while (Directory.Exists(candidate))
        {
            candidate = operationFolder + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        _context.OperationDirectory = candidate;
        Directory.CreateDirectory(_context.OperationDirectory);
        DiagnosticSpan.StartOperation(_context.OperationId, _context.OperationName);
        _context.ResetSequences();

        _context.MainLogPath = Path.Combine(_context.OperationDirectory, "operation.log");
        _context.UserLogPath = Path.Combine(_context.OperationDirectory, "operation.user.log");
        _context.JsonlPath = Path.Combine(_context.OperationDirectory, "operation.jsonl");
        _context.TracePath = Path.Combine(_context.OperationDirectory, "operation.trace.jsonl");

        File.WriteAllText(_context.MainLogPath, string.Empty, Encoding.UTF8);
        File.WriteAllText(_context.UserLogPath, string.Empty, Encoding.UTF8);
        File.WriteAllText(_context.JsonlPath, string.Empty, Encoding.UTF8);
        File.WriteAllText(_context.TracePath, string.Empty, Encoding.UTF8);
        WriteOperationManifest(started);
        File.WriteAllText(
            Path.Combine(_context.OperationDirectory, "summary.json"),
            "{\"status\":\"running\",\"note\":\"导出诊断包时更新\"}" + Environment.NewLine,
            Encoding.UTF8);
    }

    private void WriteOperationManifest(DateTime started)
    {
        try
        {
            var manifest = new
            {
                schema = "archivefixer.operation.v1",
                run_id = _context.RunId,
                operation_id = _context.OperationId,
                operation_name = _context.OperationName,
                started_at = LogService.FormatTimestamp(started),
                app_version = typeof(LogService).Assembly.GetName().Version?.ToString() ?? string.Empty,
                file_version = ResolveFileVersion(),
                build_commit = BuildInfo.Commit,
                build_configuration = BuildInfo.Configuration,
                build_time = BuildInfo.BuildTime,
                os = RuntimeInformation.OSDescription,
                framework = RuntimeInformation.FrameworkDescription,
                process_id = Environment.ProcessId,
                operation_directory = _context.OperationDirectory,
                log_files = new[]
                {
                    "operation.user.log",
                    "operation.log",
                    "operation.jsonl",
                    "operation.trace.jsonl",
                    "manifest.json",
                    "summary.json"
                }
            };

            File.WriteAllText(
                Path.Combine(_context.OperationDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, ManifestJsonOptions),
                Encoding.UTF8);
        }
        catch
        {
            // 清单写失败不影响日志主流程。
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();

        foreach (char ch in name)
        {
            builder.Append(invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch);
        }

        string result = builder.ToString().Trim('-', '.');

        if (result.Length == 0)
        {
            result = "操作";
        }

        return result.Length > 40 ? result[..40] : result;
    }

    /// <summary>
    /// 旧版 logs 根下的平铺文件移到 legacy\；只移动，不删除。
    /// </summary>
    private void MigrateLegacyFlatLogs()
    {
        try
        {
            string legacy = Path.Combine(_pathService.LogsDirectory, "legacy");
            Directory.CreateDirectory(legacy);
            string[] patterns =
            {
                "ArchiveFixer_*.log",
                "ArchiveFixer_*.user.log",
                "ArchiveFixer_*.jsonl",
                "ArchiveFixer_*.trace.jsonl",
                "fatal_*.log"
            };

            foreach (string pattern in patterns)
            {
                foreach (string source in Directory.GetFiles(_pathService.LogsDirectory, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string destination = Path.Combine(legacy, Path.GetFileName(source));
                        int suffix = 1;
                        string candidate = destination;

                        while (File.Exists(candidate))
                        {
                            candidate = Path.Combine(
                                legacy,
                                Path.GetFileNameWithoutExtension(destination)
                                + "-" + suffix.ToString(CultureInfo.InvariantCulture)
                                + Path.GetExtension(destination));
                            suffix++;
                        }

                        File.Move(source, candidate);
                    }
                    catch
                    {
                        // 被占用/权限不足：留给下一次启动。
                    }
                }
            }
        }
        catch
        {
            // 迁移失败不能阻止程序启动。
        }
    }

    /// <summary>按运行目录清理，旧目录整体删除；当前运行和 legacy 永不删。</summary>
    private void CleanupOldRunDirectories()
    {
        try
        {
            var directories = new DirectoryInfo(_pathService.LogsDirectory)
                .GetDirectories("run_*", SearchOption.TopDirectoryOnly)
                .Where(dir => !string.Equals(
                    dir.FullName,
                    _context.RunDirectory,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(dir => dir.LastWriteTimeUtc)
                .ToList();

            if (directories.Count <= RetentionMinFiles)
            {
                return;
            }

            int deleted = 0;
            long freedBytes = 0;

            foreach (DirectoryInfo directory in directories.Skip(RetentionMinFiles))
            {
                try
                {
                    long size = directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    directory.Delete(recursive: true);
                    deleted++;
                    freedBytes += size;
                }
                catch
                {
                    // 被占用：下次再收。
                }
            }

            if (deleted > 0)
            {
                _diagnostics.Write(
                    "INFO",
                    $"已清理 {deleted} 个旧运行日志文件夹（至少保留最近 {RetentionMinFiles} 个），释放 {freedBytes / 1024.0 / 1024.0:0.0} MB");
            }
        }
        catch
        {
            // 清理失败不影响启动。
        }
    }

    /// <summary>
    /// 日志环境头：OS / .NET / 进程 / 架构 / 启动时间 / 工作目录 / 程序版本。
    /// 以后用户只发日志时，这一条让读日志的人不必再问“什么系统、什么版本、什么时候跑的”。
    /// </summary>
    private void WriteEnvironmentHeader()
    {
        try
        {
            Process process = Process.GetCurrentProcess();
            string detail = JsonSerializer.Serialize(new
            {
                schema = "archivefixer.log.v3",
                app_version = typeof(LogService).Assembly.GetName().Version?.ToString() ?? string.Empty,
                /*
                 * 单文件发布里 `Assembly.Location` 恒为空（IL3000，会被当成错误）⇒ 以**进程路径**为准；
                 * 开发期 `dotnet run` 时进程是 dotnet.exe，读不到本程序版本，再退回程序目录下的 dll。
                 */
                file_version = ResolveFileVersion(),
                os = RuntimeInformation.OSDescription,
                os_arch = RuntimeInformation.OSArchitecture.ToString(),
                process_arch = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                dotnet = Environment.Version.ToString(),
                process_id = process.Id,
                process_name = process.ProcessName,
                process_started = LogService.FormatTimestamp(process.StartTime),
                machine = Environment.MachineName,
                user = Environment.UserName,
                working_directory = Environment.CurrentDirectory,
                command_line = Environment.CommandLine,
                processor_count = Environment.ProcessorCount,
                is_64bit_process = Environment.Is64BitProcess,
                log_directory = _pathService.LogsDirectory,
                culture = CultureInfo.CurrentCulture.Name,
                ui_culture = CultureInfo.CurrentUICulture.Name
            });

            _diagnostics.Event(
                "session.environment",
                $"运行环境：{RuntimeInformation.OSDescription}；{RuntimeInformation.FrameworkDescription}；进程 {process.ProcessName}（{process.Id}）",
                detail: detail);
        }
        catch (Exception exception)
        {
            _diagnostics.Exception("写运行环境头失败", exception);
        }
    }

    /// <summary>
    /// 日志环境头里的「文件版本」：单文件发布只能从**进程路径**读，开发期退回程序目录里的 DLL。
    /// ⛔ 不用 `Assembly.Location`（单文件里恒为空，且会被 IL3000 判错）。
    /// </summary>
    private static string ResolveFileVersion()
    {
        foreach (string candidate in new[]
                 {
                     Environment.ProcessPath ?? string.Empty,
                     Path.Combine(AppContext.BaseDirectory, "ArchiveFixer.dll")
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            try
            {
                string? version = FileVersionInfo.GetVersionInfo(candidate).FileVersion;

                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version!;
                }
            }
            catch
            {
                // 读不到就试下一个来源。
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 日志目录的**保留策略**（用户 2026-09-25 第 44 条追加：他导出的那两份日志一份 721 KB、一份 1.4 MB，
    /// 目录里已经攒了 56 份、3.4 MB —— 而在这之前**没有任何清理**，只增不减）。
    ///
    /// <para>规则（按时间从旧到新处理，⛔ 绝不动当前这一份、绝不动不匹配 <c>ArchiveFixer_*.log</c> 的文件）：</para>
    /// <list type="number">
    /// <item><description>删掉**超过 <see cref="RetentionDays"/> 天**的；</description></item>
    /// <item><description>剩下的若总量超过 <see cref="RetentionTotalBytes"/>，从最旧的开始删到限额以内；</description></item>
    /// <item><description>**至少留最近 <see cref="RetentionMinFiles"/> 份**（用户随时可能要看昨天那一次）。</description></item>
    /// </list>
    ///
    /// <para>删了就在新日志首行之后写一条 INFO 说清删了几份 / 释放多少 —— 静默删文件是这个项目的红线。</para>
    /// </summary>
    private void CleanupOldLogs()
    {
        try
        {
            var files = new DirectoryInfo(_pathService.LogsDirectory)
                .GetFiles("ArchiveFixer_*.log", SearchOption.TopDirectoryOnly)
                .Where(file => !file.Name.EndsWith(".user.log", StringComparison.OrdinalIgnoreCase))
                .Where(file => !string.Equals(file.FullName, _context.MainLogPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();

            if (files.Count <= RetentionMinFiles)
            {
                return;
            }

            DateTime cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(RetentionDays);
            long totalBytes = files.Sum(file => file.Length
                + TwinLength(file.FullName, ".user.log")
                + TwinLength(file.FullName, ".jsonl")
                + TwinLength(file.FullName, ".trace.jsonl"));
            int deleted = 0;
            long freedBytes = 0;

            foreach (FileInfo file in files)
            {
                // 最近 N 份永远留着（下面是按时间升序，最后 N 份在队尾）。
                if (files.Count - files.IndexOf(file) <= RetentionMinFiles)
                {
                    break;
                }

                bool tooOld = file.LastWriteTimeUtc < cutoffUtc;
                bool overQuota = totalBytes > RetentionTotalBytes;

                if (!tooOld && !overQuota)
                {
                    break;
                }

                long length = file.Length;

                try
                {
                    file.Delete();
                    deleted++;
                    freedBytes += length;
                    totalBytes -= length;

                    /*
                     * 孪生文件跟着人读日志一起收：否则 `.jsonl` / `.trace.jsonl` 只增不减，
                     * 而用户看到的是“日志已经清过了”。当前这一份三件套永远不在这里的 files 里。
                     */
                    freedBytes += TryDeleteLogTwin(file.FullName, ".user.log");
                    freedBytes += TryDeleteLogTwin(file.FullName, ".jsonl");
                    freedBytes += TryDeleteLogTwin(file.FullName, ".trace.jsonl");
                }
                catch
                {
                    // 正被别的进程占用（例如上一份还开着）：跳过，下次启动再收。
                }
            }

            if (deleted > 0)
            {
                _files.AppendBothFiles(
                    ArchiveFixer.Helpers.Clock.Now,
                    "INFO",
                    $"已清理 {deleted} 份老日志（保留最近 {RetentionDays} 天 / "
                    + $"上限 {RetentionTotalBytes / 1024 / 1024} MB / 至少 {RetentionMinFiles} 份），释放 "
                    + $"{freedBytes / 1024.0 / 1024.0:0.0} MB");
            }
        }
        catch
        {
            // 清理失败绝不能让程序起不来：日志只是辅助。
        }
    }

    /// <summary>孪生文件的长度（用于 100 MB 总量口径；没有就是 0）。</summary>
    private static long TwinLength(string logPath, string extension)
    {
        try
        {
            string twin = Path.ChangeExtension(logPath, extension);
            return File.Exists(twin) ? new FileInfo(twin).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 删掉人读日志的孪生文件（`.user.log` / `.jsonl` / `.trace.jsonl`），返回释放字节数。
    /// 只由保留策略调用；当前这一份永远不会走到这里。
    /// </summary>
    private static long TryDeleteLogTwin(string logPath, string extension)
    {
        try
        {
            string twin = Path.ChangeExtension(logPath, extension);

            if (!File.Exists(twin))
            {
                return 0;
            }

            long length = new FileInfo(twin).Length;
            File.Delete(twin);
            return length;
        }
        catch
        {
            return 0;
        }
    }
}
