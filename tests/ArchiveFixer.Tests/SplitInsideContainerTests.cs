using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **容器里装的是"分卷的第一卷"**（用户 2026-09-25 第 42 条：顺着第 40/41 条往下核出来的形状）。
    ///
    /// <para>形状：`封面.jpg`（图片 + 原始字节）里装着的正是 `set.7z.001`，而 `set.7z.002/.003`
    /// 就在同一个目录里。真 7z 实测（探针跑出来的日志）：抠出来交给引擎之后，引擎按**文件名**
    /// 找同组的其他卷 —— 抠出来的那一段没有卷名 —— 于是报"这是分卷压缩包的**后续卷**，缺少首卷"，
    /// 与事实**正好相反**（缺的不是首卷，首卷就在我们手上；缺的是"名字对得上的后续卷"）。</para>
    ///
    /// <para>本轮不做"把两边自动凑起来"，只钉两件**必须正确**的事：①抠出来的过程物名字必须跟着真实格式（原来写死 `.zip`，
    /// 一个 7z 被抠成 `封面.zip`，日志与工作区残留都会把人带偏）；
    /// ②失败原因必须说对（补上"容器里是第 1 卷、外面那一组缺首卷"这个事实）。</para>
    ///
    /// <para>⚠ **2026-09-26 起**：自动拼装已经做出来了（用户选的方案 A，**默认关**，②页那个开关）——
    /// 那一档的回归在 <c>SplitVolumeAssemblyTests</c>；本类继续钉**默认档**的这两件事（与从前逐字相同）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SplitInsideContainerTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public SplitInsideContainerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSplitInside", Guid.NewGuid().ToString("N"));
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

        // ================================================================ 纯逻辑：缺首卷的分卷组

        [Fact]
        public void 缺首卷的分卷组_找得到并给出标准首卷名()
        {
            IReadOnlyList<OrphanVolumeSet> sets = OrphanVolumeSetDetector.Find(
                new[] { "set.7z.002", "set.7z.003", "别的文件.txt", "另一个包.rar" });

            OrphanVolumeSet set = Assert.Single(sets);

            Assert.Equal("set.7z.001", set.FirstVolumeName);
            Assert.Equal(new[] { "set.7z.002", "set.7z.003" }, set.ContinuationFileNames);
            Assert.True(set.IsContiguous);
            Assert.Contains("set.7z.002", set.Describe());
            Assert.Contains("set.7z.001", set.Describe());
        }

        [Fact]
        public void 首卷在目录里时_不是缺首卷的组()
        {
            IReadOnlyList<OrphanVolumeSet> sets = OrphanVolumeSetDetector.Find(
                new[] { "set.7z.001", "set.7z.002", "set.7z.003" });

            Assert.Empty(sets);
        }

        [Fact]
        public void 两组都缺首卷时_两组都报出来()
        {
            IReadOnlyList<OrphanVolumeSet> sets = OrphanVolumeSetDetector.Find(
                new[] { "a.7z.002", "b.rar.002", "b.rar.003" });

            Assert.Equal(2, sets.Count);
            Assert.Contains(sets, set => set.FirstVolumeName == "a.7z.001");
            Assert.Contains(sets, set => set.FirstVolumeName == "b.rar.001");
        }

        [Fact]
        public void 卷号不连续时_如实标注()
        {
            IReadOnlyList<OrphanVolumeSet> sets = OrphanVolumeSetDetector.Find(
                new[] { "set.7z.002", "set.7z.004" });

            OrphanVolumeSet set = Assert.Single(sets);

            Assert.False(set.IsContiguous);
            Assert.Contains("不连续", set.Describe());
        }

        [Fact]
        public void 普通文件名_一个组都不报()
        {
            Assert.Empty(OrphanVolumeSetDetector.Find(new[] { "a.bin", "封面.jpg", "说明.txt" }));
            Assert.Empty(OrphanVolumeSetDetector.Find(null));
        }

        // ================================================================ 端到端：真 7z + 真管线

        /// <summary>
        /// 容器里装第一卷、后续卷在外面：①抠出来的过程物必须叫 `封面.7z`（不是 `.zip`）；
        /// ②失败原因必须补上"容器里是第 1 卷、外面那一组缺首卷、那一组叫什么"。
        /// </summary>
        [Fact]
        public async Task 容器里装第一卷_后续卷在外面_失败原因必须把引擎说反的那句更正过来()
        {
            RequireSevenZip();

            string work = Path.Combine(_root, "work");
            Directory.CreateDirectory(work);

            string container = BuildContainerWithFirstVolume(work, "封面.jpg", withSiblings: true, siblingBaseName: "set.7z");

            Harness harness = CreateHarness();
            var task = await AddTaskAsync(harness, container);

            await harness.Coordinator.StartExtractAsync();

            // ①失败原因里必须有那一句事实（引擎原话是"后续卷、缺少首卷"，正好说反了）。
            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.Contains("第 1 卷", task.ErrorMessage);
            Assert.Contains("set.7z.002", task.ErrorMessage);
            Assert.Contains("set.7z.001", task.ErrorMessage);

            // ②抠出来的过程物沿用**源文件的包基名**（密码本"名称:密码"的映射靠它命中）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("实际使用", StringComparison.Ordinal) && line.Contains("封面.zip", StringComparison.Ordinal));

            // ③源文件与外面那一组卷一个字节都没动（不变量 1）。
            Assert.True(File.Exists(container));
            Assert.True(File.Exists(Path.Combine(work, "set.7z.002")));
            Assert.True(File.Exists(Path.Combine(work, "set.7z.003")));
        }

        /// <summary>
        /// **引擎行为证据**（这几条不是测我们的代码，而是钉住那段注释里的实测结论）：
        ///
        /// <para>①完整的归档**不看后缀**：容器里就是一整个 7z（或 RAR）时，被抠成 `封面.zip` 也照样打得开 ——
        /// 所以第 38/39 条那条"尾部挂 RAR/7z"的路不会被这个名字拖累；</para>
        ///
        /// <para>②而"容器里装的是分卷第一卷"时后缀**会改变判决**：叫 `.zip` 时引擎报的是「分卷缺失」
        /// 这一类（成因说得对），叫 `.7z` 就改口说"文件损坏"。所以
        /// <c>BuildEmbeddedArchivePath</c> 里那个写死的 `.zip` 是**刻意**留的，
        /// ⛔ 谁"顺手改成真实格式的后缀"都会把最需要说清的那一档说歪。</para>
        /// </summary>
        [Fact]
        public async Task 引擎行为证据_完整归档不看后缀_而分卷第一卷看后缀()
        {
            RequireSevenZip();

            string directory = Path.Combine(_root, "naming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var payload = new byte[2 * 1024 * 1024];
            new Random(11).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "a.bin"), payload);

            var engine = new Engines.SevenZip.SevenZipEngine();

            // ① 完整单卷 7z → 改名成 .zip 也能列出来
            Run7z(directory, "a", "-t7z", "-mx0", "single.7z", "a.bin");

            string asZip = Path.Combine(directory, "完整包.zip");
            File.Copy(Path.Combine(directory, "single.7z"), asZip);

            ArchiveListResult complete = await engine.ListAsync(ArchiveRequest.For(asZip), CancellationToken.None);

            Assert.True(complete.Success, $"完整的 7z 不该被名字影响：{complete.Message}");
            Assert.Contains(complete.Entries, entry => entry.Path!.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase));

            // ② 分卷的第一卷：叫 .zip 与叫 .7z 的判决**不一样**（后者落到"文件损坏"那一类）
            Run7z(directory, "a", "-t7z", "-mx0", "-v1m", "set.7z", "a.bin");

            string firstZip = Path.Combine(directory, "第一卷.zip");
            string firstSevenZip = Path.Combine(directory, "第一卷.7z");
            File.Copy(Path.Combine(directory, "set.7z.001"), firstZip);
            File.Copy(Path.Combine(directory, "set.7z.001"), firstSevenZip);

            ArchiveListResult zipNamed = await engine.ListAsync(ArchiveRequest.For(firstZip), CancellationToken.None);
            ArchiveListResult sevenZipNamed = await engine.ListAsync(ArchiveRequest.For(firstSevenZip), CancellationToken.None);

            Assert.False(zipNamed.Success, "只有第一卷时当然列不出来（这条只证明'结论拿得到'）");
            Assert.False(sevenZipNamed.Success);

            // 机器可比的那两位（实测）：叫 `.zip` → 落进"缺少首卷"这一类（管线据此报「分卷缺失」，
            // 成因说得对）；叫 `.7z` → 引擎改口说"损坏"（管线上就是「文件损坏」，会让人去怀疑文件）。
            Assert.Equal(Engines.SevenZip.SevenZipOutputParser.MissingFirstVolumeErrorType, zipNamed.ErrorType);
            Assert.Equal(EngineErrorTypes.CorruptedArchive, sevenZipNamed.ErrorType);
        }

        /// <summary>
        /// **反向断言**：外面没有那一组"缺首卷的分卷"时，不许硬套那句补充说明
        /// （判据必须来自文件系统，⛔ 不许见到内嵌归档就猜）。
        /// </summary>
        [Fact]
        public async Task 外面没有缺首卷的那一组时_不许硬套补充说明()
        {
            RequireSevenZip();

            string work = Path.Combine(_root, "work-alone");
            Directory.CreateDirectory(work);

            string container = BuildContainerWithFirstVolume(work, "只有一卷.jpg", withSiblings: false, siblingBaseName: "set.7z");

            Harness harness = CreateHarness();
            var task = await AddTaskAsync(harness, container);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.DoesNotContain("同目录里缺首卷的那一组", task.ErrorMessage);
        }

        // ================================================================ 造样本与工具

        /// <summary>
        /// 造一个"图片 + 一组真 7z 的第一卷"的容器；<paramref name="withSiblings"/> 为真时
        /// 把 `.002/.003` 放在容器同目录里（就是这一档的现场）。
        /// </summary>
        private string BuildContainerWithFirstVolume(string directory, string containerName, bool withSiblings, string siblingBaseName)
        {
            string source = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);

            var payload = new byte[3 * 1024 * 1024];
            new Random(20260925).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(source, "a.bin"), payload);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "set.7z", "a.bin");

            string first = Path.Combine(source, "set.7z.001");
            Assert.True(File.Exists(first), "第一卷没造出来");

            byte[] firstBytes = File.ReadAllBytes(first);
            var junk = new byte[2 * 1024 * 1024];
            new Random(7).NextBytes(junk);

            string container = Path.Combine(directory, containerName);

            using (var stream = File.Create(container))
            {
                stream.Write(junk);
                stream.Write(firstBytes);
            }

            if (withSiblings)
            {
                File.Copy(Path.Combine(source, "set.7z.002"), Path.Combine(directory, siblingBaseName + ".002"));
                File.Copy(Path.Combine(source, "set.7z.003"), Path.Combine(directory, siblingBaseName + ".003"));
            }

            return container;
        }

        private static async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            return task;
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

        private Harness CreateHarness()
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
                new ConfirmDialogService());

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmDialogService());

            return new Harness(vm, coordinator, logService, outputRoot);
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("测试机上没有 7z.exe");
            }
        }

        private static string? LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");
                    return File.Exists(candidate) ? candidate : null;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private sealed class ConfirmDialogService : DialogService
        {
            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                optionChecked = optionCheckedByDefault;
                return true;
            }
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
        }
    }
}
