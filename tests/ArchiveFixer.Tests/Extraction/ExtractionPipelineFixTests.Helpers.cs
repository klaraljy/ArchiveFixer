using ArchiveFixer.BindingModels;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests;

/// <summary>
/// 装配与替身：Harness、假引擎、造文件/计数工具，供各测试域共用。
/// </summary>
public partial class ExtractionPipelineFixTests
{


    // ================================================================ 装配

    private sealed class Harness
    {
        public Harness(
            MainViewModel vm,
            FakeEngine engine,
            ExtractionCoordinator coordinator,
            LogService log,
            PathService pathService)
        {
            Vm = vm;
            Engine = engine;
            Coordinator = coordinator;
            Log = log;
            PathService = pathService;
        }

        public MainViewModel Vm { get; }

        public FakeEngine Engine { get; }

        public ExtractionCoordinator Coordinator { get; }

        public LogService Log { get; }

        public PathService PathService { get; }
    }

    private Harness CreateHarness(IEnumerable<string>? passwords = null, Action<AppSettings>? configure = null)
    {
        string dataRoot = Path.Combine(_root, "data");
        string outputRoot = Path.Combine(_root, "out");

        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(outputRoot);

        var pathService = new PathService { DataRootDirectory = dataRoot };
        var settingsService = new SettingsService(pathService);

        AppSettings settings = AppSettings.CreateDefault();
        settings.CustomOutputDirectory = outputRoot;
        settings.ExtractToOriginalDirectory = false;
        settings.KeepArchiveNameFolder = true;
        settings.AutoScanAfterDrop = false;

        /*
         * 源包档位固定成"留在原地"。
         *
         * 理由：这一组测的是**解压管线本身**（入仓/定稿、校验、落点、归集、取消、UI 线程不被占住），
         * 断言里到处是"输出目录里有几个文件""源文件还在不在"。用户 2026-09-22 的新规则是
         * "成功就把源包也移进其余物"，照默认档跑会让源包混进这些计数，把管线测试变成
         * 一半在测源包搬运 —— 那种失败看不出管线坏没坏。
         * 源包搬运（两条路径 + 幂等 + 失败/取消）另有专测：SourcePackageRestMoveTests
         * 与 InnerLayerContinuationTests。
         */
        settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

        configure?.Invoke(settings);
        settingsService.Save(settings);

        var engine = new FakeEngine();
        var passwordService = new PasswordService();
        var logService = new LogService(pathService);

        if (passwords != null)
        {
            foreach (string password in passwords)
            {
                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = "测试候选"
                });
            }
        }

        // MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）：
        // 先存后还原，免得别的测试拿到我这边马上要删的临时目录。
        string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
        string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

        var vm = new MainViewModel(
            new FileScanService(),
            new ArchiveDetectService(),
            new RenameService(),
            engine,
            passwordService,
            logService,
            settingsService,
            pathService,
            new TaskSummaryService(),
            new ClipboardService(),
            new DialogService());

        RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
        ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

        var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

        // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
        coordinator.KeepTaskDetailInLog = true;

        return new Harness(vm, engine, coordinator, logService, pathService);
    }

    private ArchiveTask AddTask(Harness harness, string sourcePath)
    {
        var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
        {
            IsArchive = true,
            DetectedFormat = "7Z",
            ExtensionStatus = StatusText.ExtensionNormal,
            Status = StatusText.Recognized,
            IsSelected = true
        };

        harness.Vm.Tasks.Add(task);
        return task;
    }

    private string CreateSourceFile(string fileName)
    {
        string directory = Path.Combine(_root, "src");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
        return path;
    }

    /// <summary>造双面文件时前面垫多少字节（"内嵌归档"就是它后面的那一段）。</summary>
    private const int EmbeddedPaddingBytes = 4096;

    /// <summary>
    /// 造一个"双面文件"：前面垫一段数据，偏移之后才是"真正的归档"。
    ///
    /// 抠包（<see cref="EmbeddedArchiveCarver"/>）是**真实的字节拷贝**，不需要那一段真的能被解开
    /// —— 解压由假引擎负责。要的只是"任务确实往工作区里写了过程物"这个事实。
    /// </summary>
    private string CreateEmbeddedSourceFile(string fileName)
    {
        string path = Path.Combine(_root, "src", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] bytes = new byte[EmbeddedPaddingBytes + 512];

        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// 本任务的过程物落点目录（布局由 <see cref="PathService.BuildTaskWorkDirectory"/> 给出，
    /// 测试**不自己拼规则** —— 拼一份迟早与实现分叉）。
    /// </summary>
    private static string TaskWorkDirectory(Harness harness, ArchiveTask task) =>
        harness.PathService.BuildTaskWorkDirectory(task);

    /// <summary>
    /// 本任务的**暂存目录**（入仓阶段的产物在这里，定稿之后才进最终目录）。
    /// </summary>
    private static string TaskStageDirectory(Harness harness, ArchiveTask task) =>
        harness.PathService.BuildTaskStageDirectory(task);

    private static ExtractOptions DefaultOptions(Harness harness)
    {
        var options = new ExtractOptions
        {
            ExtractToOriginalDirectory = harness.Vm.Settings.ExtractToOriginalDirectory,
            CustomOutputDirectory = harness.Vm.Settings.CustomOutputDirectory,
            KeepArchiveNameFolder = harness.Vm.Settings.KeepArchiveNameFolder
        };

        options.Normalize();
        return options;
    }

    private static void WriteFiles(string directory, int count)
    {
        Directory.CreateDirectory(directory);

        for (int i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(directory, $"payload-{i:D5}.bin"), "x");
        }
    }

    private static int CountFiles(string? directory)
    {
        try
        {
            return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                ? 0
                : Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>目录下第一层子目录的个数；目录不存在算 0，读不了算 -1（断言会因此红掉，不静默通过）。</summary>
    private static int CountSubdirectories(string? directory)
    {
        try
        {
            return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                ? 0
                : Directory.GetDirectories(directory).Length;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 写几个"看起来像 ZIP"的产物：递归探测内层归档靠的是**魔数**（PK 03 04），
    /// 内容真假无所谓 —— 这里要的是"探测出两个内层归档"这个确定的局面。
    /// </summary>
    private static void WriteFakeZipFiles(string directory, params string[] fileNames)
    {
        Directory.CreateDirectory(directory);

        foreach (string fileName in fileNames)
        {
            byte[] bytes = new byte[512];
            bytes[0] = 0x50;
            bytes[1] = 0x4B;
            bytes[2] = 0x03;
            bytes[3] = 0x04;

            File.WriteAllBytes(Path.Combine(directory, fileName), bytes);
        }
    }

    /// <summary>
    /// 用 <c>mklink /J</c> 造目录联接点：不需要管理员权限，也不改注册表
    /// （符号链接要 SeCreateSymbolicLinkPrivilege，普通开发机上跑不了）。
    ///
    /// 造完再确认一次：mklink 失败的返回码在这里没法直接拿到，而上层断言依赖"联接点真的存在"——
    /// 静默失败会让测试变成"什么都没测到却是绿的"。
    /// </summary>
    private static void CreateJunction(string linkPath, string targetPath)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(linkPath);
        psi.ArgumentList.Add(targetPath);

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 cmd.exe 建联接点");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(30_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已经退了就无所谓。
            }

            throw new InvalidOperationException("mklink 建联接点超时");
        }

        Assert.True(
            Directory.Exists(linkPath) &&
            (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0,
            $"没能造出目录联接点（mklink 输出：{stdout}{stderr}）");
    }

    private static ArchiveOperationResult Succeeded()
    {
        return new ArchiveOperationResult
        {
            Success = true,
            Status = StatusText.ExtractSuccess,
            Message = "解压成功",
            DetectedErrorType = "None"
        };
    }

    private static ArchiveOperationResult WrongPassword()
    {
        return new ArchiveOperationResult
        {
            Success = false,
            Status = StatusText.WrongPassword,
            Message = "密码错误",
            DetectedErrorType = "WrongPassword"
        };
    }

    /// <summary>
    /// 引擎给出的**一句两义**（真机上由 `UnRarOutputParser` 落成它）：
    /// 「在加密文件 X 里校验和错误。文件已损坏或密码错误。」—— 判据是退出码 3 + 加密判读，
    /// ⛔ 不比中文（§11.4）。
    /// </summary>
    private static ArchiveOperationResult PasswordOrCorrupted()
    {
        return new ArchiveOperationResult
        {
            Success = false,
            Status = StatusText.PasswordOrCorrupted,
            Message = "在加密文件「示例条目」里校验和错误。文件已损坏或密码错误。",
            DetectedErrorType = EngineErrorTypes.PasswordOrCorrupted
        };
    }

    private static ArchiveListResult ListResult(int fileCount)
    {
        return new ArchiveListResult
        {
            Success = true,
            FileCount = fileCount,
            TotalUncompressedSize = 0,
            Entries = Enumerable.Range(0, fileCount)
                .Select(i => new ArchiveEntry { Path = $"payload-{i:D5}.bin", Size = 1 })
                .ToList(),
            EngineId = "fake",
            EngineVersion = "1.0"
        };
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"{failureMessage}（等了 {timeout.TotalSeconds:F0} 秒）");
    }

    /// <summary>
    /// 模拟 WPF 的 UI 线程：一条专用线程 + 一个消息队列，回调只在这条线程上执行。
    /// </summary>
    private sealed class MessagePumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public void RunUntil(Func<bool> stop)
        {
            while (!stop())
            {
                if (_queue.TryTake(out (SendOrPostCallback Callback, object? State) item, 20))
                {
                    item.Callback(item.State);
                }
            }
        }
    }

    /// <summary>
    /// 可控的假引擎：调用记录 + 每个方法都可以被测试替换实现。
    /// 它是"外部进程"的替身，所以测试里可以放心让它返回密码错误、慢、或者永不结束。
    /// </summary>
    private sealed class FakeEngine : IArchiveEngine
    {
        public List<string> ExtractCalls { get; } = new();

        public string LastExtractOutputPath { get; set; } = string.Empty;

        /// <summary>解压是否真的成功过一次（用来只对"收尾那次列目录"下手）。</summary>
        public bool Extracted { get; set; }

        public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

        /// <summary>带 <see cref="ExtractOptions"/> 的解压钩子（密码预检要按"只解哪些条目"区分）。</summary>
        public Func<ArchiveRequest, ExtractOptions, Task<ArchiveOperationResult>>? OnExtractWithOptionsAsync { get; set; }

        public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

        /// <summary>
        /// 「先测试再解压」（<c>settings.TestBeforeExtract = true</c>）那条路要可控地给出结论 ——
        /// 不配它时行为与以前逐字相同（一律"测试通过"）。
        /// </summary>
        public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnTestAsync { get; set; }

        public string Id => "fake";

        public string DisplayName => "假引擎";

        public string Version => "1.0";

        public bool IsAvailable => true;

        public EngineCapabilities Capabilities { get; } = new()
        {
            CanProbe = true,
            CanList = true,
            CanTest = true,
            CanExtract = true,
            SupportsPassword = true
        };

        public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
        }

        public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            return OnListAsync != null
                ? OnListAsync(request)
                : Task.FromResult(ListResult(0));
        }

        public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            return OnTestAsync != null
                ? OnTestAsync(request)
                : Task.FromResult(Succeeded());
        }

        public Task<ArchiveOperationResult> ExtractAsync(
            ArchiveRequest request,
            ExtractOptions options,
            CancellationToken cancellationToken = default)
        {
            ExtractCalls.Add(request.ArchivePath);

            if (OnExtractWithOptionsAsync != null)
            {
                return OnExtractWithOptionsAsync(request, options);
            }

            if (OnExtractAsync != null)
            {
                return OnExtractAsync(request);
            }

            LastExtractOutputPath = request.OutputPath ?? string.Empty;
            return Task.FromResult(Succeeded());
        }
    }
}
