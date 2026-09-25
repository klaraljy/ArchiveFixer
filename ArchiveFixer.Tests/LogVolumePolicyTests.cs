using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **日志瘦身**（用户 2026-09-25 第 44 条："你看看一次导出 713KB……这个多吓人"）。
    ///
    /// <para>他那一次的 4025 行 / 713 KB 是这么构成的：进度 1181 行（16%）、删除 412 行（16%）、
    /// 其余 2432 行里绝大多数是**每个任务十几条一样形状的样板**（落点 / 入仓 / 空间门 / 资源预算 /
    /// 特定解压 / 结果校验 / 定稿完成 / 工作区清理 / 其余物…），68 个包就是 68 份。</para>
    ///
    /// <para>他给的三条要求，在这一组里逐条钉住：</para>
    /// <list type="number">
    /// <item><description><b>进度别这么详细</b> —— 成功的任务日志里**一条进度行都不许有**；</description></item>
    /// <item><description><b>不用汇报那么详细</b> —— 成功 = 两行（开始 + 收尾摘要），样板行全丢；</description></item>
    /// <item><description><b>删文件别写那么多</b> —— 逐任务一行 + 批末一条汇总；</description></item>
    /// </list>
    /// <para>⚠ 反过来同样要钉住：**失败 / 取消时细节一个都不许少**（他原话："如果失败的话，
    /// 你就可以多一点"）—— 排查要的就是那些数字与路径。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class LogVolumePolicyTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public LogVolumePolicyTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerLogVolume", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = LocateSevenZip();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }

        /// <summary>① 成功的任务：日志里没有进度行、没有样板行，只有一行收尾摘要。</summary>
        [Fact]
        public async Task 成功的任务_日志只剩一行摘要_没有进度与样板()
        {
            RequireSevenZip();

            string package = BuildPackage("plain.7z", 64 * 1024);
            Harness harness = CreateHarness();

            await harness.AddTaskAsync(package);
            await harness.Coordinator.StartExtractAsync();

            List<string> mine = harness.LogTexts
                .Where(line => line.Contains("] plain.7z：", StringComparison.Ordinal))
                .ToList();

            // 进度：一条都不许有（用户第 1 条要求）。
            Assert.DoesNotContain(mine, line => line.Contains("：进度 ", StringComparison.Ordinal));

            // 样板行：成功时全丢（用户第 2 条要求）。
            foreach (string boilerplate in new[] { "空间门通过", "入仓目录", "资源预算提示", "定稿完成", "结果校验", "工作区已清理", "中间工作区已清理", "本次内容物" })
            {
                Assert.DoesNotContain(mine, line => line.Contains(boilerplate, StringComparison.Ordinal));
            }

            // 收尾摘要：一行说清结果（状态 + 落点）。加上开头那一行"开始解压"，正常一个任务就 2–3 行
            //（其余物那一档再补一行短句），⛔ 绝不是以前那种十几行。
            Assert.True(
                mine.Count <= 3,
                $"成功的任务日志最多三行（开始 / 摘要 / 其余物），实际 {mine.Count} 行：" + string.Join(" | ", mine));

            Assert.Contains(mine, line => line.Contains("开始解压", StringComparison.Ordinal));
            Assert.Single(mine, line => line.Contains(StatusText.ExtractSuccess, StringComparison.Ordinal));
        }

        /// <summary>② 失败的任务：细节照旧全留（⛔ 不许"为了瘦身把失败现场也吃了"）。</summary>
        [Fact]
        public async Task 失败的任务_细节照旧全留()
        {
            // 7z 魔数 + 垃圾内容：识别得出来、解压必失败（不需要真引擎就能走到失败收尾）。
            string broken = Path.Combine(_root, "broken.7z");
            byte[] bytes = new byte[4096];
            new Random(20260925).NextBytes(bytes);
            bytes[0] = 0x37;
            bytes[1] = 0x7A;
            bytes[2] = 0xBC;
            bytes[3] = 0xAF;
            bytes[4] = 0x27;
            bytes[5] = 0x1C;
            File.WriteAllBytes(broken, bytes);

            Harness harness = CreateHarness();
            await harness.AddTaskAsync(broken);
            await harness.Coordinator.StartExtractAsync();

            List<string> mine = harness.LogTexts.Where(line => line.Contains("broken.7z", StringComparison.Ordinal)).ToList();

            Assert.True(mine.Count > 3, "失败任务的细节必须留下来，实际只有：" + string.Join(" | ", mine));
            Assert.Contains(mine, line => line.Contains("空间门通过", StringComparison.Ordinal)
                || line.Contains("资源预算", StringComparison.Ordinal)
                || line.Contains("入仓目录", StringComparison.Ordinal));
        }

        /// <summary>③ 其余物：逐任务一行（短），批末一条汇总；⛔ 不再逐条念理由与"不可逆"。</summary>
        [Fact]
        public async Task 其余物删除_逐任务一行加批末汇总()
        {
            RequireSevenZip();

            string package = BuildPackage("rest.7z", 32 * 1024);
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            await harness.AddTaskAsync(package);
            await harness.Coordinator.StartExtractAsync();

            List<string> purgeLines = harness.LogTexts
                .Where(line => line.Contains(StatusText.RestActionDelete, StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(purgeLines);

            // 逐任务那一行必须短（旧的是 200+ 字符的长文案，里头还塞着理由与"不可逆"）。
            Assert.All(purgeLines, line => Assert.True(line.Length < 160, "删除行还是太长了：" + line));
            Assert.DoesNotContain(purgeLines, line => line.Contains("理由=", StringComparison.Ordinal));
            Assert.DoesNotContain(purgeLines, line => line.Contains("这一步不可逆", StringComparison.Ordinal));
        }

        /// <summary>④ 批末汇总：一眼看到"这批成不成"（第 44 条追加）。</summary>
        [Fact]
        public async Task 批末汇总_一眼看到成功失败与逐条失败()
        {
            RequireSevenZip();

            string good = BuildPackage("good.7z", 32 * 1024);

            string broken = Path.Combine(_root, "packages", "broken.7z");
            byte[] bytes = new byte[4096];
            new Random(7).NextBytes(bytes);
            bytes[0] = 0x37;
            bytes[1] = 0x7A;
            bytes[2] = 0xBC;
            bytes[3] = 0xAF;
            bytes[4] = 0x27;
            bytes[5] = 0x1C;
            File.WriteAllBytes(broken, bytes);

            Harness harness = CreateHarness();

            await harness.AddTaskAsync(good);
            await harness.AddTaskAsync(broken);
            await harness.Coordinator.StartExtractAsync();

            string? summary = harness.LogTexts.FirstOrDefault(line => line.Contains("本批汇总：", StringComparison.Ordinal));

            Assert.NotNull(summary);
            Assert.True(
                summary!.Contains("2 个任务", StringComparison.Ordinal) && summary.Contains("失败 1", StringComparison.Ordinal),
                $"批末汇总不对：{summary}\nbroken 相关日志：\n{string.Join("\n", harness.LogTexts.Where(l => l.Contains("broken", StringComparison.Ordinal)))}");

            // 失败要**逐条点名**（不用他去数行）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("  失败：", StringComparison.Ordinal)
                    && line.Contains("broken.7z", StringComparison.Ordinal));
        }

        /// <summary>⑤ 老日志保留策略：超期 / 超量的收掉，最近若干份与"当前这一份"永远留着。</summary>
        [Fact]
        public void 日志保留_清理老日志但不碰当前这一份与最近几份()
        {
            string dataRoot = Path.Combine(_root, "retention-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var logService = new LogService(pathService);

            logService.Initialize(enableFileLog: true);

            string logsDirectory = logService.LogDirectory;
            string current = logService.CurrentLogFilePath;

            Assert.True(File.Exists(current), "当前日志文件应该已经建好");

            // 造 40 份"很旧"的日志（每份 2 MB → 共 80 MB，超过 50 MB 上限），再各留一份的修改时间。
            DateTime old = DateTime.UtcNow.AddDays(-90);

            for (int i = 0; i < 40; i++)
            {
                string fake = Path.Combine(logsDirectory, $"ArchiveFixer_20250101_{i:D6}.log");
                File.WriteAllBytes(fake, new byte[2 * 1024 * 1024]);
                File.SetLastWriteTimeUtc(fake, old.AddMinutes(i));
            }

            // 不匹配命名规则的文件：⛔ 一个都不许动。
            string stranger = Path.Combine(logsDirectory, "别人的日志.txt");
            File.WriteAllText(stranger, "不要动我");
            File.SetLastWriteTimeUtc(stranger, old);

            var second = new LogService(pathService);
            second.Initialize(enableFileLog: true);

            int remaining = Directory.GetFiles(logsDirectory, "ArchiveFixer_*.log").Length;

            // 至少留 LogRetentionMinFiles 份，且总量落回上限以内（当前这一份 + 最近几份）。
            Assert.True(remaining >= LogService.LogRetentionMinFiles, $"老日志被清得太狠：只剩 {remaining} 份");

            long total = Directory.GetFiles(logsDirectory, "ArchiveFixer_*.log").Sum(file => new FileInfo(file).Length);
            Assert.True(total <= LogService.LogRetentionTotalBytes, $"日志总量仍超上限：{total / 1024 / 1024} MB");

            Assert.True(File.Exists(current), "当前这一份永远不许被删");
            Assert.True(File.Exists(stranger), "不匹配命名规则的文件一个字节都不许动");
        }

        // ================================================================ 工具

        private string BuildPackage(string fileName, int payloadBytes)
        {
            string stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);

            var payload = new byte[payloadBytes];
            new Random(4242).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(stage, "payload.bin"), payload);

            string packages = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packages);

            Run7z(stage, "a", "-t7z", "-mx0", Path.Combine(packages, fileName), "payload.bin");

            return Path.Combine(packages, fileName);
        }

        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new DialogService());

            return new Harness(vm, coordinator, logService, outputRoot);
        }

        private void Run7z(string workingDirectory, params string[] args)
        {
            RequireSevenZip();

            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败：{stdout}{stderr}");
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("测试机上没有 7z.exe（ArchiveFixer/tools/7zip/7z.exe）");
            }
        }

        private static string? LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");
                    return File.Exists(candidate) ? candidate : null;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private sealed class Harness
        {
            public Harness(MainViewModel vm, ExtractionCoordinator coordinator, LogService log, string outputRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public async Task AddTaskAsync(string path)
            {
                var task = new ArchiveTask(path, Vm.Tasks.Count + 1) { IsSelected = true };

                await new ArchiveDetectService().ApplyDetectResultAsync(task);
                task.CaptureSourceSnapshot();
                Vm.Tasks.Add(task);
            }
        }
    }
}
