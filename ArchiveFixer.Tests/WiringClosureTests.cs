using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 收口接线回归：把"前四个代理互相移交、但没人接上"的几处接起来之后的看门测试。
    ///
    /// <list type="number">
    /// <item>终端落法（<c>TerminalLayoutMode</c>）真的从设置传到 <c>Plan</c>，不再是写死的 KeepLastFolder；</item>
    /// <item>场景 B 塌缩（规格 §3.3）真的接线：设置 → 目录扫描 → 落点；</item>
    /// <item>清理服务（过程物 / 空文件夹）有可被界面调用的"预览 + 执行"两步。</item>
    /// </list>
    ///
    /// 全部只动 <see cref="Path.GetTempPath"/> 下的临时目录，<see cref="Dispose"/> 里删干净；
    /// **绝不碰系统回收站**：删除档位一律通过注入的假执行器走
    /// （真实回收站在单测里不可接受 —— 跑一次测试就往用户回收站塞垃圾）。
    /// </summary>
    public class WiringClosureTests : IDisposable
    {
        private readonly string _root;

        public WiringClosureTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerWiringTests", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        private string PathOf(string relativePath)
        {
            return Path.Combine(_root, relativePath);
        }

        private string WriteFile(string relativePath, string content)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private string MakeDirectory(string relativePath)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        // ================================================================ ① 终端落法

        [Fact]
        public void 设置默认值_终端落法保留最后一层_场景B塌缩默认开()
        {
            AppSettings defaults = AppSettings.CreateDefault();

            Assert.Equal("KeepLastFolder", defaults.TerminalLayoutMode);
            Assert.True(defaults.CollapseRepeatedFolderLayer, "规格 §3.3 明确要求默认开");
        }

        [Theory]
        [InlineData("KeepLastFolder", TerminalLayoutMode.KeepLastFolder)]
        [InlineData("UseArchiveName", TerminalLayoutMode.UseArchiveName)]
        [InlineData("usearchivename", TerminalLayoutMode.UseArchiveName)]
        [InlineData(" UseArchiveName ", TerminalLayoutMode.UseArchiveName)]
        [InlineData("", TerminalLayoutMode.KeepLastFolder)]
        [InlineData(null, TerminalLayoutMode.KeepLastFolder)]
        [InlineData("随便写的非法值", TerminalLayoutMode.KeepLastFolder)]
        [InlineData("999", TerminalLayoutMode.KeepLastFolder)]
        public void 终端落法解析_非法值一律回落默认(string? raw, TerminalLayoutMode expected)
        {
            Assert.Equal(expected, OutputPlacement.ParseTerminalLayoutMode(raw));
        }

        [Fact]
        public void 终端落法Normalize_非法值写回默认而不是原样留着()
        {
            var settings = new AppSettings { TerminalLayoutMode = "谁把这里改坏了" };

            settings.Normalize();

            Assert.Equal("KeepLastFolder", settings.TerminalLayoutMode);
        }

        [Fact]
        public void 终端落法_枚举与设置字符串互逆()
        {
            foreach (TerminalLayoutMode mode in Enum.GetValues<TerminalLayoutMode>())
            {
                Assert.Equal(mode, OutputPlacement.ParseTerminalLayoutMode(OutputPlacement.ToSettingValue(mode)));
            }
        }

        [Fact]
        public void 旧配置缺新字段_不报错且取到默认值()
        {
            // 旧版 appsettings.json：既没有 TerminalLayoutMode，也没有 CollapseRepeatedFolderLayer。
            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            Directory.CreateDirectory(_root);
            File.WriteAllText(
                pathService.SettingsFilePath,
                "{ \"RecursionMode\": \"SingleChain\", \"MaxRecursionDepth\": 4 }",
                new UTF8Encoding(false));

            AppSettings loaded = service.Load();

            Assert.Equal("SingleChain", loaded.RecursionMode);
            Assert.Equal(4, loaded.MaxRecursionDepth);
            Assert.Equal("KeepLastFolder", loaded.TerminalLayoutMode);
            Assert.True(loaded.CollapseRepeatedFolderLayer);
        }

        [Fact]
        public void 设置里的终端落法_真的传到定稿规划()
        {
            // 暂存树：out\666\a.mp4、out\666\b.mp4（多个文件 → 判定表 2 会套一层）
            string stage = PathOf("stage");
            WriteFile(@"stage\666\a.mp4", "a");
            WriteFile(@"stage\666\b.mp4", "b");

            string destination = PathOf(@"111\222");

            var settings = new AppSettings { TerminalLayoutMode = "KeepLastFolder" };

            ExtractionCoordinator.FinalLayoutPlan keep = ExtractionCoordinator.PlanFinalLayout(
                stage,
                destination,
                OutputPlacementMode.PerArchiveSubfolder,
                "555",
                OutputPlacement.ParseTerminalLayoutMode(settings.TerminalLayoutMode));

            settings.TerminalLayoutMode = "UseArchiveName";

            ExtractionCoordinator.FinalLayoutPlan useArchiveName = ExtractionCoordinator.PlanFinalLayout(
                stage,
                destination,
                OutputPlacementMode.PerArchiveSubfolder,
                "555",
                OutputPlacement.ParseTerminalLayoutMode(settings.TerminalLayoutMode));

            // 判定表 2 套出来的那一层：KeepLastFolder 用包内那层名字（666），UseArchiveName 用包基名（555）。
            // （内容物是"整棵 666 目录搬走"，所以断言的是那一层的落点。）
            Assert.Contains(keep.Moves, move => move.To == PathOf(@"111\222\666"));
            Assert.Contains(useArchiveName.Moves, move => move.To == PathOf(@"111\222\555"));

            // 两种取值必须真的不同（写死 KeepLastFolder 时这条会红）。
            Assert.NotEqual(
                keep.Moves.Select(move => move.To).OrderBy(path => path, StringComparer.Ordinal),
                useArchiveName.Moves.Select(move => move.To).OrderBy(path => path, StringComparer.Ordinal));
        }

        // ================================================================ ② 场景 B 塌缩

        [Fact]
        public void 场景B_目录里只有这一个包_判定为可塌缩()
        {
            string archive = WriteFile(@"111\222\名字\名字.rar", "rar");

            SourceFolderScanResult scan = SourceFolderScanService.Inspect(archive);

            Assert.True(scan.IsCandidate);
            Assert.True(scan.Scanned);
            Assert.True(scan.ContainsOnlyThisArchive);
        }

        [Fact]
        public void 场景B_包名与目录名不同_连目录都不扫()
        {
            string archive = WriteFile(@"111\222\333.rar", "rar");

            SourceFolderScanResult scan = SourceFolderScanService.Inspect(archive);

            Assert.False(scan.IsCandidate);
            Assert.False(scan.Scanned);
            Assert.False(scan.ContainsOnlyThisArchive);
        }

        [Fact]
        public void 场景B_自己的分卷和说明文件不算别的包()
        {
            string archive = WriteFile(@"111\222\名字\名字.rar", "rar");
            WriteFile(@"111\222\名字\名字.r00", "vol");
            WriteFile(@"111\222\名字\名字.r01", "vol");
            WriteFile(@"111\222\名字\说明.txt", "readme");

            Assert.True(SourceFolderScanService.Inspect(archive).ContainsOnlyThisArchive);
        }

        [Fact]
        public void 场景B_分卷组基名也算包基名()
        {
            string archive = WriteFile(@"111\222\名字\名字.7z.001", "v1");
            WriteFile(@"111\222\名字\名字.7z.002", "v2");

            SourceFolderScanResult scan = SourceFolderScanService.Inspect(archive);

            Assert.True(scan.IsCandidate);
            Assert.True(scan.ContainsOnlyThisArchive);
        }

        [Theory]
        [InlineData("另一个.rar")]
        [InlineData("另一个.7z")]
        [InlineData("名字.zip")]
        [InlineData("名字.7z.001")]
        [InlineData("伪装.jpg.rar")]
        public void 场景B_目录里还有别的包_不塌缩(string otherArchiveName)
        {
            string archive = WriteFile(@"111\222\名字\名字.rar", "rar");
            WriteFile($"111\\222\\名字\\{otherArchiveName}", "other");

            SourceFolderScanResult scan = SourceFolderScanService.Inspect(archive);

            Assert.True(scan.IsCandidate);
            Assert.False(scan.ContainsOnlyThisArchive);
        }

        [Fact]
        public void 场景B_目录读不到_保守不塌缩()
        {
            // 目录不存在 = 读不到：多一层是安全的默认，绝不因为"扫不到"就塌缩。
            SourceFolderScanResult scan = SourceFolderScanService.Inspect(PathOf(@"111\222\名字\名字.rar"));

            Assert.True(scan.IsCandidate);
            Assert.False(scan.Scanned);
            Assert.False(scan.ContainsOnlyThisArchive);
        }

        [Fact]
        public void 场景B_接线后落点少了重复的一层_有别的包时保留完整一层()
        {
            var pathService = new PathService();
            var settings = new AppSettings();
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = true
            };

            string onlyOne = WriteFile(@"111\222\名字\名字.rar", "rar");
            var task = new ArchiveTask(onlyOne);

            SourceFolderScanResult scan = SourceFolderScanService.Inspect(onlyOne);

            Assert.Equal(
                PathOf(@"111\222\名字"),
                pathService.BuildOutputPath(task, options, settings.CollapseRepeatedFolderLayer, scan.ContainsOnlyThisArchive));

            // 同一个目录里再放一个包 → 事实变了，落点跟着回到 111\222\名字\名字。
            WriteFile(@"111\222\名字\另一个.rar", "other");

            SourceFolderScanResult scanAgain = SourceFolderScanService.Inspect(onlyOne);

            Assert.Equal(
                PathOf(@"111\222\名字\名字"),
                pathService.BuildOutputPath(task, options, settings.CollapseRepeatedFolderLayer, scanAgain.ContainsOnlyThisArchive));
        }

        [Fact]
        public void 场景B_设置关掉塌缩时_即使只有这一个包也保留完整一层()
        {
            var pathService = new PathService();
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = true
            };

            string archive = WriteFile(@"111\222\名字\名字.rar", "rar");
            var task = new ArchiveTask(archive);

            var settings = new AppSettings { CollapseRepeatedFolderLayer = false };
            SourceFolderScanResult scan = SourceFolderScanService.Inspect(archive);

            Assert.Equal(
                PathOf(@"111\222\名字\名字"),
                pathService.BuildOutputPath(task, options, settings.CollapseRepeatedFolderLayer, scan.ContainsOnlyThisArchive));
        }

        [Fact]
        public void 场景B_塌缩后的落点就是源包所在目录_不套用目录已存在就改名()
        {
            /*
             * 塌缩之后落点必然等于源包所在目录，而那个目录**必然非空**（源包躺在里面）。
             * 所以"输出目录已存在且非空就改名 xxx(1)"这条规则必须让开 ——
             * 否则塌缩当场被抵消，产物落到旁边的 名字(1)\。
             * 这里把"落点 == 源包目录"这个判据钉死（管线里就是它决定让不让开）。
             */
            var pathService = new PathService();
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = true
            };

            string archive = WriteFile(@"111\222\名字\名字.rar", "rar");
            var task = new ArchiveTask(archive);

            SourceFolderScanResult scan = SourceFolderScanService.Inspect(archive);
            string output = pathService.BuildOutputPath(task, options, true, scan.ContainsOnlyThisArchive);

            Assert.True(OutputPlacement.LandsInSourceDirectory(archive, output));

            // 模式 B（解压到压缩包所在目录）同理：落点就是源目录。
            var flatOptions = new ExtractOptions
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = false
            };

            Assert.True(OutputPlacement.LandsInSourceDirectory(
                archive,
                pathService.BuildOutputPath(task, flatOptions)));

            // 普通的同名子文件夹落点（111\222\333）不适用这条例外，照旧走"已存在就改名"。
            string other = WriteFile(@"111\222\333.rar", "rar");

            Assert.False(OutputPlacement.LandsInSourceDirectory(
                other,
                pathService.BuildOutputPath(new ArchiveTask(other), options)));
        }

        // ================================================================ ③ 清理入口（过程物）
        [Fact]
        public void 过程物预览_报顶层项数与总大小且不删任何东西()
        {
            MakeDirectory(@"out\过程物\sub");
            WriteFile(@"out\过程物\inner.7z.001", "12345");        // 5 字节
            WriteFile(@"out\过程物\sub\middle.zip", "1234567890"); // 10 字节

            MaintenanceCleanupService service = CreateCleanupService(out _);
            CleanupPreview preview = service.PreviewProcessArtifacts(PathOf("out"));

            Assert.True(preview.HasTarget);
            Assert.Equal(PathOf(@"out\过程物"), preview.ScopePath);
            Assert.Equal(2, preview.ItemCount);
            Assert.Equal(15L, preview.TotalBytes);
            Assert.True(preview.Determined);
            Assert.Contains("inner.7z.001", preview.Items);

            // 预览不删除：目录还在，文件也还在。
            Assert.True(Directory.Exists(PathOf(@"out\过程物")));
            Assert.True(File.Exists(PathOf(@"out\过程物\inner.7z.001")));
        }

        [Fact]
        public void 过程物预览_没有过程物目录时不进确认流程()
        {
            MakeDirectory(@"out");

            CleanupPreview preview = CreateCleanupService(out _).PreviewProcessArtifacts(PathOf("out"));

            Assert.False(preview.HasTarget);
            Assert.Contains("不存在", preview.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 过程物执行_默认档走回收站并留下路径理由条目数总大小()
        {
            MakeDirectory(@"out\过程物\sub");
            WriteFile(@"out\过程物\inner.7z.001", "12345");
            WriteFile(@"out\过程物\sub\middle.zip", "1234567890");

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupOutcome outcome = service.CleanProcessArtifacts(PathOf("out"), DeleteMode.RecycleBin);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.Equal(0, outcome.FailureCount);
            Assert.Equal(15L, outcome.RecycledBytes);
            Assert.Equal(0L, outcome.FreedBytes);
            Assert.Empty(executor.PermanentCalls);
            Assert.Equal(PathOf(@"out\过程物"), Assert.Single(executor.RecycleCalls));
            Assert.False(Directory.Exists(PathOf(@"out\过程物")));

            // 日志四要素：路径 + 理由 + 条目数 + 总大小。
            string log = Assert.Single(outcome.LogLines);
            Assert.Contains(PathOf(@"out\过程物"), log, StringComparison.Ordinal);
            Assert.Contains(MaintenanceCleanupService.ProcessArtifactReason, log, StringComparison.Ordinal);
            Assert.Contains("条目数=3", log, StringComparison.Ordinal);
            Assert.Contains("总大小=15 字节", log, StringComparison.Ordinal);
        }

        [Fact]
        public void 过程物执行_激进档彻底删除并报释放字节数()
        {
            MakeDirectory(@"out\过程物");
            WriteFile(@"out\过程物\inner.7z.001", "12345");

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupOutcome outcome = service.CleanProcessArtifacts(PathOf("out"), DeleteMode.Permanent);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.Equal(5L, outcome.FreedBytes);
            Assert.Equal(0L, outcome.RecycledBytes);
            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(PathOf(@"out\过程物"), Assert.Single(executor.PermanentCalls));
        }

        [Fact]
        public void 过程物执行_回收站不可用时不降级为永久删除()
        {
            MakeDirectory(@"out\过程物");
            WriteFile(@"out\过程物\inner.7z.001", "12345");

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            executor.RecycleResult = RecycleAttemptResult.Unavailable;

            CleanupOutcome outcome = service.CleanProcessArtifacts(PathOf("out"), DeleteMode.RecycleBin);

            Assert.True(outcome.Attempted);
            Assert.Equal(0, outcome.SuccessCount);
            Assert.Equal(1, outcome.FailureCount);
            Assert.Empty(executor.PermanentCalls);
            Assert.True(Directory.Exists(PathOf(@"out\过程物")), "拒绝删除时目标必须原样留在原处");
            Assert.Contains(outcome.FailureReasons, reason => reason.Contains("回收站", StringComparison.Ordinal));
        }

        // ================================================================ ④ 清理入口（空文件夹）

        [Fact]
        public void 空文件夹预览_只列任意层级都没有文件的子目录()
        {
            MakeDirectory(@"out\empty-tree\deeper");
            WriteFile(@"out\has-file\x.mp4", "content");

            CleanupPreview preview = CreateCleanupService(out _).PreviewEmptyFolders(PathOf("out"));

            Assert.True(preview.HasTarget);
            Assert.Equal(1, preview.ItemCount);
            Assert.Equal(PathOf(@"out\empty-tree"), Assert.Single(preview.Items));
            Assert.True(Directory.Exists(PathOf(@"out\empty-tree")));
        }

        [Fact]
        public void 空文件夹执行_删掉空目录树且保留有文件的目录()
        {
            MakeDirectory(@"out\empty-tree\deeper");
            WriteFile(@"out\has-file\x.mp4", "content");

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupOutcome outcome = service.CleanEmptyFolders(PathOf("out"), DeleteMode.RecycleBin);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.Equal(PathOf(@"out\empty-tree"), Assert.Single(executor.RecycleCalls));
            Assert.False(Directory.Exists(PathOf(@"out\empty-tree")));
            Assert.True(File.Exists(PathOf(@"out\has-file\x.mp4")));
            Assert.Contains(outcome.LogLines, line => line.Contains(MaintenanceCleanupService.EmptyFolderReason, StringComparison.Ordinal));
        }

        [Fact]
        public void 清理作用域_过程物目录由ProcessArtifactLayout给出()
        {
            Assert.Equal(
                PathOf(@"out\过程物"),
                MaintenanceCleanupService.ResolveProcessArtifactScope(PathOf("out")));

            Assert.Equal(string.Empty, MaintenanceCleanupService.ResolveProcessArtifactScope(null));
        }

        [Fact]
        public void 作用域提醒_模式B下说清作用域与别的包共用()
        {
            // 模式 B：输出目录 = 源包所在目录。
            var task = new ArchiveTask(PathOf(@"111\222\333.rar")) { OutputPath = PathOf(@"111\222") };

            Assert.Contains("共用", MainViewModel.BuildSharedScopeNote(CleanupScope.ProcessArtifacts, task));
            Assert.Contains("源包所在目录", MainViewModel.BuildSharedScopeNote(CleanupScope.EmptyFolders, task));

            // 普通模式（输出目录是包子目录）：不提这句。
            task.OutputPath = PathOf(@"111\222\333");

            Assert.Equal(string.Empty, MainViewModel.BuildSharedScopeNote(CleanupScope.ProcessArtifacts, task));
            Assert.Equal(string.Empty, MainViewModel.BuildSharedScopeNote(CleanupScope.EmptyFolders, task));
        }

        [Fact]
        public void 确认框正文_含作用范围条目数总大小与档位说明()
        {
            var preview = new CleanupPreview
            {
                Scope = CleanupScope.ProcessArtifacts,
                ScopePath = @"D:\out\过程物",
                HasTarget = true,
                ItemCount = 3,
                EntryCount = 9,
                TotalBytes = 1024,
                Determined = true,
                Items = new[] { "a.7z", "b.7z" }
            };

            string recycleText = MainViewModel.BuildCleanupConfirmText("清理过程物", preview, DeleteMode.RecycleBin);

            Assert.Contains(@"D:\out\过程物", recycleText, StringComparison.Ordinal);
            Assert.Contains("顶层 3 项", recycleText, StringComparison.Ordinal);
            Assert.Contains("1024 字节", recycleText, StringComparison.Ordinal);
            Assert.Contains("回收站", recycleText, StringComparison.Ordinal);
            Assert.DoesNotContain("无法恢复", recycleText, StringComparison.Ordinal);

            string permanentText = MainViewModel.BuildCleanupConfirmText(
                "清理过程物", preview, DeleteMode.Permanent, "作用域共用提醒");

            Assert.Contains("无法恢复", permanentText, StringComparison.Ordinal);
            Assert.Contains("作用域共用提醒", permanentText, StringComparison.Ordinal);
        }

        // ================================================================ ⑤ 设置项真正生效

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData(@"D:\ArchiveFixer-cache", true)]
        [InlineData(@"D:\", true)]
        [InlineData(@"C:\ArchiveFixer-cache", false)]
        [InlineData(@"c:\cache", false)]
        [InlineData(@"cache", false)]
        public void 缓存根目录校验_C盘与相对路径一律拒绝(string? path, bool expected)
        {
            bool valid = SettingsViewModel.ValidateCacheRootDirectory(path, out string message);

            Assert.Equal(expected, valid);

            if (!expected)
            {
                Assert.False(string.IsNullOrWhiteSpace(message), "拒绝时必须说明原因与改法");
            }
        }

        [Fact]
        public void 缓存根目录填C盘_保存被拦下并给出原因()
        {
            var settings = new AppSettings { CacheRootDirectory = @"C:\ArchiveFixer-cache" };
            var viewModel = new SettingsViewModel(settings, new SettingsService());

            viewModel.SaveCommand.Execute(null);

            Assert.Null(viewModel.DialogResult);
            Assert.Contains("C 盘", viewModel.Message, StringComparison.Ordinal);
            Assert.Equal(@"C:\ArchiveFixer-cache", viewModel.Settings.CacheRootDirectory);
        }

        [Fact]
        public void 缓存根目录合法_保存通过()
        {
            var settings = new AppSettings { CacheRootDirectory = @"D:\ArchiveFixer-cache" };
            var viewModel = new SettingsViewModel(settings, new SettingsService());

            viewModel.SaveCommand.Execute(null);

            Assert.True(viewModel.DialogResult);
            Assert.Equal(@"D:\ArchiveFixer-cache", viewModel.Settings.CacheRootDirectory);
        }

        [Fact]
        public void 设置窗口的终端落法_读写设置字符串()
        {
            var viewModel = new SettingsViewModel(new AppSettings(), new SettingsService());

            Assert.Equal(TerminalLayoutMode.KeepLastFolder, viewModel.TerminalLayout);

            viewModel.TerminalLayout = TerminalLayoutMode.UseArchiveName;
            Assert.Equal("UseArchiveName", viewModel.Settings.TerminalLayoutMode);

            viewModel.TerminalLayout = TerminalLayoutMode.KeepLastFolder;
            Assert.Equal("KeepLastFolder", viewModel.Settings.TerminalLayoutMode);
        }

        [Fact]
        public void 设置窗口的塌缩开关_读写设置项()
        {
            var viewModel = new SettingsViewModel(new AppSettings(), new SettingsService());

            Assert.True(viewModel.CollapseRepeatedFolderLayer);

            viewModel.CollapseRepeatedFolderLayer = false;
            Assert.False(viewModel.Settings.CollapseRepeatedFolderLayer);
        }

        // ================================================================ ⑥ 异常文本脱敏（无 UI 宿主）

        [Fact]
        public void 异常提示文本脱敏_无UI宿主也不抛异常()
        {
            var service = new DialogService();

            // 异常消息里夹着 7z 命令行片段：这正是最可能带出明文密码的形态。
            service.ShowException(
                new InvalidOperationException("7z.exe x out.7z -pSecretPass123 password=SecretPass123"),
                "程序发生未处理的界面异常（已记录日志）");

            Assert.DoesNotContain(
                DialogService.FallbackLog,
                entry => entry.Contains("SecretPass123", StringComparison.Ordinal));
        }

        // ================================================================ 装配

        private static MaintenanceCleanupService CreateCleanupService(out FakeDeleteExecutor executor)
        {
            executor = new FakeDeleteExecutor();
            return new MaintenanceCleanupService(executor);
        }

        /// <summary>
        /// 记录调用、可脚本化结果的假执行器：全程不碰系统回收站。
        /// 与 RecycleBinServiceTests 里的同名假件同一口径（"报成功就必须真的把目标移走"）。
        /// </summary>
        private sealed class FakeDeleteExecutor : IDeleteExecutor
        {
            public RecycleAttemptResult RecycleResult { get; set; } = RecycleAttemptResult.Recycled;

            public string RecycleMessage { get; set; } = "已移入回收站（假执行器）";

            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = RecycleMessage;

                if (RecycleResult == RecycleAttemptResult.Recycled)
                {
                    if (isDirectory)
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }

                return RecycleResult;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);

                if (isDirectory)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
        }
    }
}
