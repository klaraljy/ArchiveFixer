using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 四族"简单伪装手法"的覆盖验证（用户 2026-09-22 口述的那四种）。
    ///
    /// <para>四族与它们的判据：</para>
    /// <list type="number">
    /// <item><description>族 1 <b>图片尾部挂包</b>：<c>.jpg</c>/<c>.png</c> = 真图片头 + 追加完整 ZIP；
    ///     必须是「内嵌归档」、**不许改名**、解压走按偏移抠出。</description></item>
    /// <item><description>族 2 <b>视频尾部挂包</b>：<c>.mp4</c> 同法。</description></item>
    /// <item><description>族 3 <b>分卷 + 乱后缀</b>：<c>资料.zip.001</c> 纯改名成 <c>资料.mp4.001</c>；
    ///     必须被认成**一组分卷**（基名取到分卷标记之前）、**不许改名**、整组一起处理。</description></item>
    /// <item><description>族 4 <b>大前缀双面文件</b>：前缀 ≥ 10 MB（超过 7-Zip 的 8 MiB 容忍上限），
    ///     原文件 7z 直接拒绝，只有「按偏移抠出」才解得开。</description></item>
    /// </list>
    ///
    /// <para>
    /// 样本**全部自己造**（临时目录 + 项目内置 7z.exe；RAR 分卷那一档用本机已装的 <c>Rar.exe</c> 现造，
    /// 绝不入库、绝不复制，见 AGENTS.md §8 / docs/引擎与外部工具.md §4）。
    /// 若环境变量 <c>ARCHIVEFIXER_DISGUISE_DIR</c> 指向一个已经造好的样本目录（<c>_tmp\ArchiveFixer\disguise</c>），
    /// 就直接吃那一份真机样本 —— 命令行造出来的文件与测试造出来的逐字节一致（同一套造法）。
    /// </para>
    ///
    /// <para>
    /// ⚠ 全程**不启动 GUI、不显示任何窗口、不动鼠标键盘**（AGENTS.md §13）。
    /// 改名预览那一段用生产代码同一套服务 + 窗口自己的 ViewModel（<see cref="RenamePreviewViewModel"/>）驱动，
    /// 只有"窗口被 Show 出来"这一步没法在无 UI 宿主里做（见 <c>一键处理_需要改名时会先出预览</c> 的说明）。
    /// </para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class DisguiseCoverageTests : IClassFixture<DisguiseSampleFixture>, IDisposable
    {
        private readonly DisguiseSampleFixture _samples;
        private readonly ITestOutputHelper _output;
        private readonly string _root;

        public DisguiseCoverageTests(DisguiseSampleFixture samples, ITestOutputHelper output)
        {
            _samples = samples;
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDisguise", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ 族 1：图片尾部挂包


        // ================================================================ 族 2：视频尾部挂包


        // ================================================================ 族 4：大前缀双面文件


        // ================================================================ 族 3：分卷 + 乱后缀





        // ================================================================ 一键处理：产物




        // ================================================================ 改名预览的三条路径





        // ================================================================ 不变量 3 的接线


        // ================================================================ 共用断言 / 装配

        /// <summary>双面文件的四条结论：识别 / 内嵌偏移 / 后缀状态 / 不改名（+ 统计口径）。</summary>
        private async Task AssertDoubleSidedAsync(string path, long expectedOffset, string family)
        {
            var detect = new ArchiveDetectService();

            DetectResult result = await detect.DetectAsync(path);

            Assert.Equal("ZIP", result.Format);
            Assert.True(result.IsArchive);
            Assert.True(result.IsKnownFormat);
            Assert.Equal(expectedOffset, result.EmbeddedArchiveOffset);
            Assert.Equal(80, result.Confidence);   // 80 = "尾部推断"，不是"文件头直接证据"

            var task = new ArchiveTask(path) { IsSelected = true };
            await detect.ApplyDetectResultAsync(task);

            Assert.Equal(StatusText.ExtensionEmbedded, task.ExtensionStatus);
            Assert.True(task.EmbeddedArchiveOffset > 0);

            // 统计口径：算"已识别"，不是"未知格式"。
            TaskSummary summary = new TaskSummaryService().BuildSummary(new[] { task });
            Assert.Equal(1, summary.RecognizedCount);
            Assert.Equal(0, summary.UnknownCount);

            // 改名：一律"将跳过"，且 NewPath 就是原路径。
            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { task },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            RenamePreviewItem item = Assert.Single(preview);
            Assert.False(item.CanRename, $"{family}：双面文件不该被改名（改成 .zip 后 7z 照样打不开）");
            Assert.Equal(path, item.NewPath);
        }

        /// <summary>产物核对：<c>说明.txt</c> 与 <c>素材\data.bin</c> 都在输出根下，内容与源逐字节一致。</summary>
        private void AssertProductsExtracted(string outputRoot)
        {
            string? extractedText = Directory
                .GetFiles(outputRoot, "说明.txt", SearchOption.AllDirectories)
                .FirstOrDefault();

            string? extractedBinary = Directory
                .GetFiles(outputRoot, "data.bin", SearchOption.AllDirectories)
                .FirstOrDefault();

            Assert.True(extractedText != null, $"没解出 说明.txt（输出根：{outputRoot}）");
            Assert.True(extractedBinary != null, $"没解出 素材\\data.bin（输出根：{outputRoot}）");

            Assert.Equal(
                HashFile(Path.Combine(_samples.PayloadDirectory, "说明.txt")),
                HashFile(extractedText!));

            Assert.Equal(
                HashFile(Path.Combine(_samples.PayloadDirectory, "素材", "data.bin")),
                HashFile(extractedBinary!));
        }

        private SevenZipEngine CreateEngine()
        {
            var engine = new SevenZipEngine();
            Assert.True(engine.IsAvailable, "测试需要项目内置的 7z.exe");
            return engine;
        }

        private string CopyToWorkDir(string source, string fileName)
        {
            string directory = Path.Combine(_root, "work", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            string target = Path.Combine(directory, fileName);
            File.Copy(source, target);

            return target;
        }

        private static string HashFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;

            for (int index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
                 index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <summary>仓库根下的文件路径（从测试输出目录往上找 ArchiveFixer.slnx）。</summary>
        private static string RepoPath(params string[] parts)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("找不到仓库根（ArchiveFixer.slnx）。");
        }

        // ---------------------------------------------------------------- 改名预览的流程层宿主

        private enum RenamePreviewDecision
        {
            /// <summary>用户点了「确认改名」。</summary>
            Confirm,

            /// <summary>用户点了「取消」/ 直接关掉预览窗口。</summary>
            Cancel
        }

        private sealed class RenamePreviewRun
        {
            public bool? DialogResult { get; init; }

            public List<RenamePreviewItem> PreviewItems { get; init; } = new();

            /// <summary>冲突结论定下来、但**还没落盘**时每一条的状态（执行会把状态改成"改名成功"）。</summary>
            public List<string> DecidedStatuses { get; init; } = new();

            public List<RenamePreviewItem> ExecutedItems { get; init; } = new();

            public List<ArchiveTask> Tasks { get; init; } = new();
        }

        /// <summary>
        /// 改名预览分支的**流程层宿主**：逐句照抄 <c>RenameCoordinator.RenameByOptionsAsync</c>，
        /// 只有一处不同 —— 它把 <c>RenamePreviewWindow</c> Show 出来等人的点击，
        /// 这里换成"直接以用户的选择驱动窗口自己的 ViewModel"（AGENTS.md §13：不显示窗口、不模拟鼠标）。
        ///
        /// 生产代码里 <c>PreviewBeforeRename</c> 硬编码 true（不变量 3），所以这里也用 true。
        /// </summary>
        private static async Task<RenamePreviewRun> RunRenamePreviewFlowAsync(
            string sourcePath,
            string conflictAction,
            RenamePreviewDecision decision,
            Action<RenamePreviewItem>? actOnPreview = null)
        {
            var task = new ArchiveTask(sourcePath) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            AppSettings settings = AppSettings.CreateDefault();
            settings.ConflictAction = conflictAction;

            RenameOptions options = RenameOptions.CreateFixByDetectedFormat(settings);

            Assert.True(options.PreviewBeforeRename, "不变量 3：PreviewBeforeRename 不许被旁路");

            var service = new RenameService();
            var tasks = new List<ArchiveTask> { task };

            List<RenamePreviewItem> previewItems = service.BuildPreview(
                tasks.Where(x => x.IsSelected).ToList(),
                options);

            Assert.NotEmpty(previewItems);

            // 预览窗口的 DataContext（生产代码里就是 new RenamePreviewWindow(previewItems) 的那一个）。
            var viewModel = new RenamePreviewViewModel(previewItems);

            actOnPreview?.Invoke(viewModel.Items[0]);

            if (decision == RenamePreviewDecision.Confirm)
            {
                viewModel.Confirm();
            }
            else
            {
                viewModel.Cancel();
            }

            // ↓↓↓ 从这里往下与 RenameCoordinator.RenameByOptionsAsync 逐句一致 ↓↓↓
            bool? dialogResult = viewModel.DialogResult;

            if (dialogResult != true)
            {
                return new RenamePreviewRun
                {
                    DialogResult = dialogResult,
                    PreviewItems = viewModel.Items.ToList(),
                    Tasks = tasks
                };
            }

            foreach (RenamePreviewItem item in viewModel.Items)
            {
                service.ApplyConflictChoice(item);
            }

            // 冲突结论刚定下、还没落盘：这里的状态才是"预览里给用户看到的那个结论"。
            List<string> decidedStatuses = viewModel.Items.Select(x => x.Status).ToList();

            List<RenamePreviewItem> selectedItems = viewModel.GetSelectedItems();

            if (selectedItems.Count == 0)
            {
                return new RenamePreviewRun
                {
                    DialogResult = dialogResult,
                    PreviewItems = viewModel.Items.ToList(),
                    DecidedStatuses = decidedStatuses,
                    Tasks = tasks
                };
            }

            await service.ExecuteRenameAsync(selectedItems, tasks);

            return new RenamePreviewRun
            {
                DialogResult = dialogResult,
                PreviewItems = viewModel.Items.ToList(),
                DecidedStatuses = decidedStatuses,
                ExecutedItems = selectedItems,
                Tasks = tasks
            };
        }

        // ---------------------------------------------------------------- 一键处理的流程层宿主

        private sealed class Harness
        {
            public required MainViewModel Vm { get; init; }

            public required OneClickCoordinator OneClick { get; init; }

            public required LogService Log { get; init; }

            public required string OutputRoot { get; init; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        /// <summary>
        /// 真引擎 + 真协调器的流程层宿主（与其它端到端用例同一套形状）：
        /// 添加 → 一键处理，全程无窗口、无对话框（<see cref="DialogService"/> 在无 UI 宿主下降级为写日志）。
        /// </summary>
        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data", Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.AutoScanAfterDrop = true;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            Assert.True(engine.IsAvailable, "一键处理端到端要用真 7z");

            var passwordService = new PasswordService { DataRootDirectory = dataRoot };
            var logService = new LogService(pathService);

            // MainViewModel 的构造会写两个进程级静态（7z 路径 / 递归工作区根目录）：先存后还原。
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

            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness
            {
                Vm = vm,
                OneClick = oneClick,
                Log = logService,
                OutputRoot = outputRoot
            };
        }
    }

    /// <summary>
    /// 四族伪装样本的构造与自检（只给测试用）。
    ///
    /// <para>造法刻意与命令行脚本 <c>_tmp\ArchiveFixer\disguise\make-samples.ps1</c> 保持一致：
    /// 先把 ZIP 整个建好、**再**在前面拼数据 —— 这样 ZIP 的内部偏移保持相对它自己的起点，
    /// 这才是"双面文件"的全部关键（用"重新打包"造不出来）。</para>
    ///
    /// <para>环境变量 <c>ARCHIVEFIXER_DISGUISE_DIR</c> 指向已造好的目录（真机样本）时直接用它，
    /// 否则在临时目录里现造 —— 两条来源的样本逐字节一致。</para>
    /// </summary>
    public sealed class DisguiseSampleFixture : IDisposable
    {
        internal const string SamplesEnvironmentVariable = "ARCHIVEFIXER_DISGUISE_DIR";

        private readonly bool _ownsRoot;

        public DisguiseSampleFixture()
        {
            SevenZipPath = LocateSevenZip();

            string? configured = Environment.GetEnvironmentVariable(SamplesEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(configured)
                && File.Exists(Path.Combine(configured, "fam1", "照片.jpg")))
            {
                Root = configured;
                _ownsRoot = false;
            }
            else
            {
                Root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDisguiseSamples", Guid.NewGuid().ToString("N"));
                _ownsRoot = true;
                Directory.CreateDirectory(Root);
            }

            PayloadDirectory = Path.Combine(Root, "_payload");
            PlainZipPath = Path.Combine(Root, "_build", "包.zip");
            Source = _ownsRoot ? "临时目录现造" : "环境变量指向的真机样本目录";

            if (_ownsRoot)
            {
                Build();
            }

            PlainZipSha256 = HashFile(PlainZipPath);

            BigPrefixLength = new FileInfo(Fam4BigPrefix).Length
                              - new FileInfo(PlainZipPath).Length;
        }

        /// <summary>样本根目录。</summary>
        public string Root { get; }

        /// <summary>样本来源（写进测试输出，便于报告引用）。</summary>
        public string Source { get; }

        public string PayloadDirectory { get; }

        public string PlainZipPath { get; }

        public string PlainZipSha256 { get; }

        /// <summary>族 4 的前缀长度（12 MiB）。</summary>
        public long BigPrefixLength { get; }

        /// <summary>本机有没有 Rar.exe（族 3b 的真 RAR 分卷要用它现造；没装就跳过那一档）。</summary>
        public bool HasRarVolumeSamples { get; private set; }

        public string Fam1(string fileName) => Path.Combine(Root, "fam1", fileName);

        public string Fam2(string fileName) => Path.Combine(Root, "fam2", fileName);

        public string Fam4BigPrefix => Path.Combine(Root, "fam4", "大前缀.mp4");

        public string Fam3bWholeArchiveMp4 => Path.Combine(Root, "fam3b", "整包.mp4");

        /// <summary>族 3a 的整组分卷（纯改名之后的 <c>资料.mp4.001…</c>，已按卷序排好）。</summary>
        public string[] Fam3aVolumes() =>
            SortedVolumes(Path.Combine(Root, "fam3a"), "资料.mp4.*");

        /// <summary>族 3b 的整组 RAR 分卷（纯改名之后的 <c>资料2.mp4.partN</c>）。</summary>
        public string[] Fam3bVolumes() =>
            SortedVolumes(Path.Combine(Root, "fam3b"), "资料2.mp4.part*");

        public string PathOf(string family, string fileName) => family switch
        {
            "族1" => Fam1(fileName),
            "族2" => Fam2(fileName),
            "族4" => Fam4BigPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "未知族")
        };

        public void Dispose()
        {
            if (!_ownsRoot)
            {
                return;   // 真机样本目录是用户/命令行脚本造出来的，测试不许删它。
            }

            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

        // ---------------------------------------------------------------- 构造

        private string SevenZipPath { get; }

        private void Build()
        {
            string[] families = { "fam1", "fam2", "fam3a", "fam3b", "fam4", "_build", "_payload" };

            foreach (string family in families)
            {
                Directory.CreateDirectory(Path.Combine(Root, family));
            }

            // 公共内容物：一个文本 + 一个二进制（带一层子目录，产物结构才验得动）。
            Directory.CreateDirectory(Path.Combine(PayloadDirectory, "素材"));

            File.WriteAllText(
                Path.Combine(PayloadDirectory, "说明.txt"),
                string.Concat(Enumerable.Repeat("这是一份用于验证伪装样本的内容物说明。" + Environment.NewLine, 40)),
                new UTF8Encoding(false));

            File.WriteAllBytes(
                Path.Combine(PayloadDirectory, "素材", "data.bin"),
                DeterministicBytes(204800, 44));

            // 基准 ZIP。
            Run7z(PayloadDirectory, "a", "-tzip", "-mx1", PlainZipPath, "说明.txt", "素材");

            byte[] zipBytes = File.ReadAllBytes(PlainZipPath);

            // 族 1：图片尾部挂包（JPEG / PNG 头 + 追加完整 ZIP）。
            WriteDisguised(Fam1("照片.jpg"), BuildJpegHeader(4096), zipBytes);
            WriteDisguised(Fam1("图片.png"), BuildPngHeader(4096), zipBytes);

            // 族 2：视频尾部挂包（MP4 头 + 追加完整 ZIP）；另加一个"前缀在 7z 容忍范围内"的边界样本。
            WriteDisguised(Fam2("视频.mp4"), BuildMp4Header(32768), zipBytes);
            WriteDisguised(Fam2("小前缀.mp4"), BuildMp4Header(1024), zipBytes);

            // 族 3a：真 ZIP 分卷组 → 纯改名成 资料.mp4.001/.002…
            BuildFam3aVolumes();

            // 族 3b：真 RAR5 分卷组 → 纯改名成 资料2.mp4.partN；另加"整包改后缀"的 整包.mp4。
            BuildFam3bVolumes();
            File.Copy(PlainZipPath, Fam3bWholeArchiveMp4, overwrite: true);

            // 族 4：12 MiB 前缀（含 MP4 ftyp 头，其余是填充）+ 追加完整 ZIP。
            byte[] bigPrefix = DeterministicBytes(12 * 1024 * 1024, 55);
            byte[] ftyp = BuildMp4Header(64);
            Array.Copy(ftyp, 0, bigPrefix, 0, ftyp.Length);

            WriteDisguised(Fam4BigPrefix, bigPrefix, zipBytes);
        }

        private void BuildFam3aVolumes()
        {
            string stage = Path.Combine(Root, "_build", "fam3a");
            Directory.CreateDirectory(stage);

            Run7z(
                PayloadDirectory,
                "a", "-tzip", "-mx1", "-v64k",
                Path.Combine(stage, "资料.zip"),
                "说明.txt", "素材");

            foreach (string volume in Directory.GetFiles(stage, "资料.zip.*").OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(volume);

                // 纯改名：".zip.NNN" 里的 ".zip" 换成 ".mp4"，字节一个都不动。
                string newName = name.Replace(".zip.", ".mp4.", StringComparison.Ordinal);

                File.Move(volume, Path.Combine(Root, "fam3a", newName));
            }
        }

        private void BuildFam3bVolumes()
        {
            string? rarExe = LocateRarExe();

            if (rarExe == null)
            {
                HasRarVolumeSamples = false;
                return;
            }

            string stage = Path.Combine(Root, "_build", "fam3b");
            Directory.CreateDirectory(stage);

            RunProcess(
                rarExe,
                PayloadDirectory,
                "a", "-m1", "-r", "-v64k", "-idq",
                Path.Combine(stage, "资料2.rar"),
                "说明.txt", "素材");

            string[] volumes = Directory.GetFiles(stage, "资料2.part*.rar").OrderBy(x => x, StringComparer.Ordinal).ToArray();

            if (volumes.Length < 2)
            {
                HasRarVolumeSamples = false;
                return;
            }

            foreach (string volume in volumes)
            {
                // 纯改名：资料2.part1.rar → 资料2.mp4.part1（去掉 .rar、插入 .mp4）。
                string name = Path.GetFileName(volume);
                string newName = name
                    .Replace("资料2.", "资料2.mp4.", StringComparison.Ordinal)
                    .Replace(".rar", string.Empty, StringComparison.Ordinal);

                File.Move(volume, Path.Combine(Root, "fam3b", newName));
            }

            HasRarVolumeSamples = true;
        }

        private static string[] SortedVolumes(string directory, string pattern)
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(directory, pattern)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void WriteDisguised(string target, byte[] prefix, byte[] zipBytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

            output.Write(prefix);
            output.Write(zipBytes);
        }

        private static byte[] DeterministicBytes(int length, int seed)
        {
            byte[] buffer = new byte[length];
            new Random(seed).NextBytes(buffer);

            return buffer;
        }

        /// <summary>JPEG：SOI + APP0/JFIF + 填充 + EOI（形态真实，不追求可解码）。</summary>
        private static byte[] BuildJpegHeader(int length)
        {
            byte[] head =
            {
                0xFF, 0xD8,
                0xFF, 0xE0, 0x00, 0x10,
                0x4A, 0x46, 0x49, 0x46, 0x00,
                0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00
            };

            byte[] body = DeterministicBytes(length - head.Length - 2, 11);
            byte[] tail = { 0xFF, 0xD9 };

            return Concat(head, body, tail);
        }

        /// <summary>PNG：8 字节签名 + IHDR（CRC 正确）+ 填充。</summary>
        private static byte[] BuildPngHeader(int length)
        {
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

            byte[] ihdrData = { 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x02, 0x00, 0x00, 0x00 };
            byte[] ihdrLength = { 0x00, 0x00, 0x00, 0x0D };
            byte[] ihdrType = Encoding.ASCII.GetBytes("IHDR");

            byte[] crcInput = Concat(ihdrType, ihdrData);
            byte[] crc = BitConverter.GetBytes(Crc32(crcInput));

            byte[] ihdr = Concat(ihdrLength, crcInput, crc);
            byte[] filler = DeterministicBytes(length - signature.Length - ihdr.Length, 22);

            return Concat(signature, ihdr, filler);
        }

        /// <summary>MP4：ftyp box（isom/iso2/avc1/mp41）+ free 头 + 填充。</summary>
        private static byte[] BuildMp4Header(int length)
        {
            byte[] ftyp =
            {
                0x00, 0x00, 0x00, 0x20,
                0x66, 0x74, 0x79, 0x70,
                0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x02, 0x00,
                0x69, 0x73, 0x6F, 0x6D, 0x69, 0x73, 0x6F, 0x32,
                0x61, 0x76, 0x63, 0x31, 0x6D, 0x70, 0x34, 0x31
            };

            int fillerLength = length - ftyp.Length;
            byte[] free = Concat(
                BitConverter.GetBytes((uint)(fillerLength - 8)),
                Encoding.ASCII.GetBytes("free"));

            byte[] filler = DeterministicBytes(fillerLength - free.Length, 33);

            return Concat(ftyp, free, filler);
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte value in data)
            {
                crc ^= value;

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFF;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            byte[] result = new byte[parts.Sum(p => p.Length)];
            int offset = 0;

            foreach (byte[] part in parts)
            {
                Array.Copy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        private static string LocateSevenZip()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        /// <summary>
        /// 找本机已装的 <c>Rar.exe</c>（与 <see cref="RarSampleSet"/> 同一套探测目录）。
        /// **只读取、只在本机临时目录里造样本**：共享软件的建包器绝不复制、绝不入库。
        /// </summary>
        private static string? LocateRarExe()
        {
            foreach (string directory in new ToolLocator().WinRarInstallationCandidates())
            {
                string candidate = Path.Combine(directory, "Rar.exe");

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private void Run7z(string workingDirectory, params string[] args)
        {
            RunProcess(SevenZipPath, workingDirectory, args);
        }

        private static void RunProcess(string exe, string workingDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
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

            using Process process = Process.Start(psi)!;

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                process.Kill(true);
                throw new TimeoutException($"{Path.GetFileName(exe)} 造样本超时。");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(exe)} 失败（exit {process.ExitCode}）：{string.Join(' ', args)}\n{stdout}\n{stderr}");
            }
        }

        private static string HashFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
