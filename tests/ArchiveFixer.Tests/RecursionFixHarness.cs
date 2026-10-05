using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-10-05 只读审计那一批修复**共用的测试夹具**：假引擎 / 假探测器 /
    /// 能跑起协调器的宿主 / 造真样本的小工具。
    ///
    /// <para>为什么抽出来：这批用例按主题分成两个文件（密码侧 / 闸门与提示侧），
    /// 两边都要"一个结论完全确定的假引擎"和"一个能跑整条管线的宿主"。
    /// 夹具抄两遍的下场与产品代码一样 —— 迟早只剩一份是对的（§9.5）。</para>
    ///
    /// <para>临时目录由调用方给（各测试类自己的 <c>_root</c>），本类不持有、也不删。</para>
    /// </summary>
    internal sealed class RecursionFixHarness
    {
        private RecursionFixHarness(
            string root,
            MainViewModel vm,
            ExtractionCoordinator coordinator,
            LogService log,
            IArchiveEngine engine)
        {
            Root = root;
            Vm = vm;
            Coordinator = coordinator;
            Log = log;
            Engine = engine;
        }

        public string Root { get; }

        public MainViewModel Vm { get; }

        public ExtractionCoordinator Coordinator { get; }

        public LogService Log { get; }

        /// <summary>协调器手上的那个引擎（真 7z 或假引擎）。</summary>
        public IArchiveEngine Engine { get; }

        public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

        // ================================================================ 装配

        /// <summary>
        /// 一套最小可用宿主：真 MainViewModel + 真 ExtractionCoordinator，
        /// 数据目录 / 输出目录 / 密码本全部落在 <paramref name="root"/> 里（绝不碰用户目录）。
        /// </summary>
        /// <param name="engine">不传 = 真 7-Zip（只有"要验整条管线"的用例才需要它）。</param>
        /// <param name="configure">建完之后、写盘之前改设置（例：打开旁路说明文件、保留失败现场）。</param>
        public static RecursionFixHarness Create(
            string root,
            IReadOnlyList<string> candidates,
            int attemptLimit,
            string recursionMode = "SingleLayer",
            IArchiveEngine? engine = null,
            Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(root, "data-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = recursionMode;
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.MaxPasswordAttemptsPerLayer = attemptLimit;
            settings.TryEmptyPasswordFirst = true;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            configure?.Invoke(settings);

            settingsService.Save(settings);

            IArchiveEngine actualEngine = engine ?? new SevenZipEngine();

            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            int index = 0;

            foreach (string password in candidates)
            {
                index++;

                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = $"只读审计修复用例候选 {index}"
                });
            }

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                actualEngine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            var coordinator = new ExtractionCoordinator(
                vm,
                actualEngine,
                passwordService,
                pathService,
                new DialogService());

            return new RecursionFixHarness(root, vm, coordinator, logService, actualEngine);
        }

        /// <summary>把一份文件按"导入之后自动识别"同一条路加进列表（识别 + 拍快照 + 勾上）。</summary>
        public async Task<ArchiveTask> AddTaskAsync(string path)
        {
            var task = new ArchiveTask(path, Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            task.CaptureSourceSnapshot();
            Vm.Tasks.Add(task);

            return task;
        }

        // ================================================================ 递归核心的小工具

        /// <summary>建一个只用假引擎的递归核心（日志收进返回的那个列表）。</summary>
        public static RecursiveExtractor CreateExtractor(
            IArchiveEngine engine,
            Func<string, IReadOnlyList<string>> passwordProvider,
            RecursionLimits? limits,
            out List<string> logs)
        {
            var collected = new List<string>();

            var extractor = new RecursiveExtractor(
                engine,
                new NeverArchiveProber(),
                passwordProvider,
                limits,
                (level, message) => collected.Add($"[{level}] {message}"));

            logs = collected;

            return extractor;
        }

        /// <summary>在 <paramref name="root"/> 下造一个"存在的源文件"再跑一次递归（内容由引擎决定）。</summary>
        public static async Task<RecursionResult> RunRecursionAsync(
            RecursiveExtractor extractor,
            string root,
            string archiveName)
        {
            string archivePath = Path.Combine(root, archiveName);

            // 源文件真的存在（递归核心要量它的大小算展开比；内容由假引擎决定）。
            File.WriteAllText(archivePath, "not a real archive; the engine is fake");

            string output = Path.Combine(root, "out", archiveName + "-" + Guid.NewGuid().ToString("N"));

            return await extractor.ExtractAsync(
                new ArchiveTask(archivePath),
                output,
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);
        }

        // ================================================================ 真 7z（只有端到端那两条需要）

        public static string SevenZipPath => SevenZipFactAttribute.LocateSevenZipPath();

        public static void RequireSevenZip()
        {
            Assert.True(
                !string.IsNullOrEmpty(SevenZipPath),
                "这一组要内置 7-Zip（src\\ArchiveFixer\\tools\\7zip\\7z.exe）才能跑。");
        }

        public static void Run7z(string workDir, params string[] args)
        {
            var info = new ProcessStartInfo(SevenZipPath)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using Process? process = Process.Start(info);

            Assert.NotNull(process);

            process!.WaitForExit(60_000);

            Assert.Equal(0, process.ExitCode);
        }

        // ================================================================ 假件

        /// <summary>一份"成功但有条目加密"的清单（"整包已加密 ⇒ 跳过空密码"那一档正要用它）。</summary>
        public static ArchiveListResult Listing(params (string Path, long Size)[] entries)
        {
            return new ArchiveListResult
            {
                Success = true,
                Entries = entries
                    .Select(entry => new ArchiveEntry { Path = entry.Path, Size = entry.Size })
                    .ToList(),
                FileCount = entries.Length,
                TotalUncompressedSize = entries.Sum(entry => entry.Size),
                IsEncrypted = true,
                EngineId = EngineIds.SevenZip,
                EngineVersion = "0.0"
            };
        }

        /// <summary>一份"成功、没加密"的清单（闸门类用例用它，免得被"跳过空密码"那一档搅进来）。</summary>
        public static ArchiveListResult PlainListing(params (string Path, long Size)[] entries)
        {
            return new ArchiveListResult
            {
                Success = true,
                Entries = entries
                    .Select(entry => new ArchiveEntry { Path = entry.Path, Size = entry.Size })
                    .ToList(),
                FileCount = entries.Length,
                TotalUncompressedSize = entries.Sum(entry => entry.Size),
                EngineId = EngineIds.SevenZip,
                EngineVersion = "0.0"
            };
        }

        public static ArchiveListResult ListingFailure(string errorType)
        {
            return ArchiveListResult.Failure(errorType, "假引擎：列目录失败", EngineIds.SevenZip, "0.0");
        }

        public static ArchiveOperationResult WrongPassword()
        {
            return new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.WrongPassword,
                Message = "Cannot open encrypted archive. Wrong password?",
                DetectedErrorType = EngineErrorTypes.WrongPassword,
                EngineId = EngineIds.SevenZip
            };
        }

        /// <summary>解压成功，并在输出目录里留下一个真实文件（让"量产物"那条路真的跑到）。</summary>
        public static ArchiveOperationResult ExtractSucceeded(string? outputPath)
        {
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                Directory.CreateDirectory(outputPath);
                File.WriteAllText(Path.Combine(outputPath, "payload.bin"), "假引擎产物");
            }

            return ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero);
        }

        /// <summary>假引擎：列目录 / 解压的行为由用例给定（不碰真 7z，结论完全确定）。</summary>
        internal sealed class ScriptedEngine : IArchiveEngine
        {
            public Func<ArchiveRequest, ArchiveListResult>? OnList { get; set; }

            public Func<ArchiveRequest, ArchiveOperationResult>? OnExtract { get; set; }

            /// <summary>
            /// 需要看这一次解压的**参数**时用它（优先于 <see cref="OnExtract"/>）。
            /// 用途：把"探针那一次"（<c>ExtractOptions.IncludeEntries</c> 非空）与"解整包那一次"分开 ——
            /// 探针落点正是拿这两次请求的 <c>OutputPath</c> 对照出来的。
            /// </summary>
            public Func<ArchiveRequest, ExtractOptions, ArchiveOperationResult>? OnExtractWithOptions { get; set; }

            /// <summary>每次解压用的密码（按调用顺序）—— 用例靠它数"到底试了几个候选"。</summary>
            public List<string> Extracted { get; } = new();

            public string Id => EngineIds.SevenZip;

            public string DisplayName => "假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    OnList?.Invoke(request) ?? ListingFailure(EngineErrorTypes.UnknownError));
            }

            public Task<ArchiveOperationResult> TestAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                Extracted.Add(request.Password ?? string.Empty);

                if (OnExtractWithOptions != null)
                {
                    return Task.FromResult(OnExtractWithOptions(request, options));
                }

                return Task.FromResult(
                    OnExtract?.Invoke(request)
                    ?? ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }
        }

        /// <summary>本批用例不验"内层还有没有包"，所以探测器一律答"不是归档"（结论完全确定）。</summary>
        internal sealed class NeverArchiveProber : IArchiveProber
        {
            public Task<bool> IsArchiveAsync(string filePath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(false);
            }
        }
    }
}
