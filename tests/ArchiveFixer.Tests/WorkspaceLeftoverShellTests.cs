using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **上一次跑到一半被中断 / 被掐掉留下的空壳，下一次跑的时候自动收掉**（用户 2026-09-30 亲口点了"推荐"）。
    ///
    /// <para><b>现场</b>：正常跑完的收尾本来就会连壳一起删，只有中断这一档会残留 ——
    /// 真机上那个目标目录里就只剩一个空的 <c>.ArchiveFixer.work</c>，一个成品都没有，
    /// 用户翻自己的目录时会看见这个凭空多出来的文件夹。</para>
    ///
    /// <para>这一组钉五件事（每一条都有对应用例）：</para>
    /// <list type="number">
    /// <item><description>**只有那个空的工作区、别的什么都没有** ⇒ 下一次跑时连壳一起收掉（走真解压管线）；</description></item>
    /// <item><description>里面还有**一个 0 字节文件** ⇒ 一个字节都不动，写一条 WARN 说清路径（判据 + 管线两条都钉）；</description></item>
    /// <item><description>里面还有**一个子目录** ⇒ 一个字节都不动（判据 + 管线两条都钉）；</description></item>
    /// <item><description>**正常工作流不受影响**：本次任务自己的目标目录与工作区照旧（批尾清理口径一个字没改）；</description></item>
    /// <item><description>⛔ **越界红线**：目标目录之外一个字节都不动 —— 隔壁那个长得一模一样的
    /// 「目录 + 空 <c>.ArchiveFixer.work</c>」不是这一批的目标目录，就不许碰（周围目录树快照逐条一致）。</description></item>
    /// </list>
    ///
    /// <para><b>判据只有一处</b>：<see cref="WorkspaceLeftoverShellCleaner.Reclaim"/> /
    /// <see cref="WorkspaceLeftoverShellCleaner.ReclaimAll"/>（调用点在批首、`WorkspaceRootResolver.Resolve` 之前）。
    /// 用例里凡是"判据本身"的断言都直接调它 —— 调用点那几句只做转发，不重写判据。</para>
    ///
    /// <para><b>纪律</b>：临时目录全在 <see cref="Path.GetTempPath"/> 下的独立目录里（⛔ 不碰真机目录、不碰 `H:`）；
    /// 构造 <c>MainViewModel</c> 会写进程级静态（7z 路径、递归工作区根），所以整类进
    /// <c>ArchiveFixerGlobalState</c> 集合、与其它同类用例串行，并在装配后立刻还原。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class WorkspaceLeftoverShellTests : IDisposable
    {
        private readonly string _root;

        public WorkspaceLeftoverShellTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerShellLeftover", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

        // ================================================================ 一、判据本身（唯一出口）

        /// <summary>
        /// 用例 1 的判据面：目录里**除了那个空的工作区之外什么都没有** ⇒ 两个都收掉（先收工作区、再收壳）。
        /// </summary>
        [Fact]
        public void 只有空的ArchiveFixer_work_判据成立_两个都收掉()
        {
            string target = NewDirectory("only-shell");
            string workspace = CreateEmptyWorkspace(target);

            WorkspaceLeftoverShellOutcome outcome = WorkspaceLeftoverShellCleaner.Reclaim(target);

            Assert.Equal(WorkspaceLeftoverShellAction.Reclaimed, outcome.Action);
            Assert.True(outcome.ShouldReport, "收掉了就该写一条日志");
            Assert.Equal("INFO", outcome.LogLevel);
            Assert.False(Directory.Exists(workspace), "空的工作区目录要收掉");
            Assert.False(Directory.Exists(target), "连那个已经空掉的外壳一起收掉（用户看得见的残留一条都不许多）");
        }

        /// <summary>
        /// 用例 2 的判据面：**多一个 0 字节文件** ⇒ 一个字节都不动（连那个空工作区也不删），
        /// 只写一条 WARN 说清"发现了、没清理、路径是…"。
        /// </summary>
        [Fact]
        public void 多一个0字节文件_一个字节都不动_并写清路径()
        {
            string target = NewDirectory("plus-empty-file");
            string workspace = CreateEmptyWorkspace(target);
            string keep = Path.Combine(target, "keep-0.txt");

            File.WriteAllBytes(keep, Array.Empty<byte>());

            WorkspaceLeftoverShellOutcome outcome = WorkspaceLeftoverShellCleaner.Reclaim(target);

            Assert.Equal(WorkspaceLeftoverShellAction.LeftIntact, outcome.Action);
            Assert.Equal("WARN", outcome.LogLevel);
            Assert.Contains("没有清理", outcome.Message, StringComparison.Ordinal);
            Assert.Contains(target, outcome.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("还有 1 个", outcome.Message, StringComparison.Ordinal);

            Assert.True(File.Exists(keep), "0 字节也是用户的文件，一个字节都不许动");
            Assert.True(Directory.Exists(workspace), "判据不成立时连那个空工作区也不删（宁可留壳，也不误删）");
            Assert.Equal(2, Directory.GetFileSystemEntries(target).Length);
        }

        /// <summary>用例 3 的判据面：**多一个子目录** ⇒ 一个字节都不动。</summary>
        [Fact]
        public void 多一个子目录_一个字节都不动()
        {
            string target = NewDirectory("plus-subdirectory");
            string workspace = CreateEmptyWorkspace(target);
            string sub = Path.Combine(target, "成品");

            Directory.CreateDirectory(sub);

            WorkspaceLeftoverShellOutcome outcome = WorkspaceLeftoverShellCleaner.Reclaim(target);

            Assert.Equal(WorkspaceLeftoverShellAction.LeftIntact, outcome.Action);
            Assert.Contains("没有清理", outcome.Message, StringComparison.Ordinal);
            Assert.Contains(target, outcome.Message, StringComparison.OrdinalIgnoreCase);

            Assert.True(Directory.Exists(sub), "子目录一个字节都不许动");
            Assert.True(Directory.Exists(workspace));
            Assert.True(Directory.Exists(target));
        }

        /// <summary>
        /// **工作区里还有东西** ⇒ 不是空壳：连工作区都不删（里面可能是用户唯一的一份产物线索，
        /// 与 §6 不变量 12 的"失败保留现场"同一口径）。
        /// </summary>
        [Fact]
        public void 工作区里还有东西_连工作区都不删()
        {
            string target = NewDirectory("workspace-not-empty");
            string workspace = CreateEmptyWorkspace(target);

            File.WriteAllText(Path.Combine(workspace, "half.bin"), "还没搬出去的中间产物");

            WorkspaceLeftoverShellOutcome outcome = WorkspaceLeftoverShellCleaner.Reclaim(target);

            Assert.Equal(WorkspaceLeftoverShellAction.LeftIntact, outcome.Action);
            Assert.Contains("没有清理", outcome.Message, StringComparison.Ordinal);
            Assert.Contains(workspace, outcome.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(workspace));
            Assert.True(File.Exists(Path.Combine(workspace, "half.bin")));
            Assert.True(Directory.Exists(target));
        }

        /// <summary>
        /// ⛔ **空目录本身不是证据**：「名字像包名」「看起来是我们建的」都不构成删它的理由 ——
        /// 只有那个约定名的工作区目录才是"我们确实在这儿干过活"的事实。没有它 ⇒ 什么都判不出来、什么都不做、也不写日志。
        /// </summary>
        [Fact]
        public void 没有工作区目录_不是我们的脚印_一个字节都不动()
        {
            string bare = NewDirectory("bare-empty");          // 用户自己建的空文件夹
            string withFile = NewDirectory("user-data");
            string userFile = Path.Combine(withFile, "我的东西.txt");

            File.WriteAllText(userFile, "用户的文件");

            WorkspaceLeftoverShellOutcome bareOutcome = WorkspaceLeftoverShellCleaner.Reclaim(bare);
            WorkspaceLeftoverShellOutcome fileOutcome = WorkspaceLeftoverShellCleaner.Reclaim(withFile);

            Assert.Equal(WorkspaceLeftoverShellAction.NoFootprint, bareOutcome.Action);
            Assert.Equal(WorkspaceLeftoverShellAction.NoFootprint, fileOutcome.Action);
            Assert.False(bareOutcome.ShouldReport, "没有事实可说就不写日志（写了只是噪音）");
            Assert.False(fileOutcome.ShouldReport);

            Assert.True(Directory.Exists(bare), "空目录不是「我们建的」的理由");
            Assert.True(File.Exists(userFile));
            Assert.True(Directory.Exists(withFile));

            // 目录根本不在时同样什么都不做。
            Assert.Equal(
                WorkspaceLeftoverShellAction.NoFootprint,
                WorkspaceLeftoverShellCleaner.Reclaim(Path.Combine(_root, "never-existed")).Action);
        }

        /// <summary>同名的是个**文件**（不是我们建的工作区目录）⇒ 那不是工作区，⛔ 不许删。</summary>
        [Fact]
        public void 同名的是个文件_不许当工作区删()
        {
            string target = NewDirectory("same-name-file");
            string sameName = Path.Combine(target, WorkspaceRootResolver.DefaultWorkspaceDirectoryName);

            File.WriteAllText(sameName, "恰好叫这个名字的文件");

            WorkspaceLeftoverShellOutcome outcome = WorkspaceLeftoverShellCleaner.Reclaim(target);

            Assert.Equal(WorkspaceLeftoverShellAction.LeftIntact, outcome.Action);
            Assert.Contains("没有清理", outcome.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(sameName), "同名文件一个字节都不许动");
            Assert.True(Directory.Exists(target));
        }

        // ================================================================ 二、下一次跑的时候真的收掉了（走真管线）

        /// <summary>
        /// 用例 1（端到端）：上一次中断留下的"壳 + 空工作区"，**下一次跑**（同一目标目录）时自动收掉 —— 壳也没了。
        ///
        /// <para>为什么"目标目录最后不在了"这条断言能证明是**这一次跑**收掉的：批首收掉之后那个目录才由本批重建，
        /// 于是批尾的既有收尾（<c>RemoveEmptyTargetShellIfWeCreatedIt</c>）认它、把它一起收掉；
        /// 若批首没做这件事，目标目录解析时就已经在，本批不会认它是自己建的，跑完只会剩一个空目录。</para>
        /// </summary>
        [Fact]
        public async Task 下一次跑_残留空壳被自动收掉_壳也没了()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, "leftover-gone.7z");
            string target = ResolveTarget(harness, task);
            string workspace = CreateEmptyWorkspace(target);

            Assert.True(Directory.Exists(workspace), "前提：上一次被中断，留下一个空的工作区");

            await harness.Coordinator.StartExtractAsync();

            Assert.False(
                Directory.Exists(target),
                $"上一次没跑完留下的空壳没被收掉（用户翻目录时会看见这个凭空多出来的文件夹）：{target}");

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("收掉了上次没跑完留下的空壳", StringComparison.Ordinal) &&
                        item.Message.Contains(target, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 用例 2（端到端）：里面除了那个空工作区还有**一个 0 字节文件** ⇒ 什么都不删，只写一条 WARN 说清路径。
        ///
        /// <para>⚠ 这里刻意**不**断言「那个空工作区还在」：批尾「清掉空的工作区壳」是既有口径
        /// （不变量 12：零文件空壳无论如何都删），那一条一个字都没改 —— 见下一条用例。
        /// 要钉"判据不成立时一个字节都不动"的是判据面那几条用例。</para>
        /// </summary>
        [Fact]
        public async Task 下一次跑_里面还有别的文件_什么都不删并写WARN()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, "leftover-file.7z");
            string target = ResolveTarget(harness, task);

            CreateEmptyWorkspace(target);

            string keep = Path.Combine(target, "keep-0.txt");

            File.WriteAllBytes(keep, Array.Empty<byte>());

            await harness.Coordinator.StartExtractAsync();

            Assert.True(File.Exists(keep), "里面有别的东西时一个字节都不许动");
            Assert.True(Directory.Exists(target), "兜底落在「什么都不做」那一档：那个目录壳也得留着");

            Assert.Contains(
                harness.Log.Logs,
                item => item.Level == "WARN" &&
                        item.Message.Contains("没有清理", StringComparison.Ordinal) &&
                        item.Message.Contains(target, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>用例 3（端到端）：里面除了那个空工作区还有**一个子目录** ⇒ 什么都不删。</summary>
        [Fact]
        public async Task 下一次跑_里面还有一个子目录_什么都不删()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, "leftover-subdir.7z");
            string target = ResolveTarget(harness, task);
            string sub = Path.Combine(target, "上一次解到一半的目录");

            CreateEmptyWorkspace(target);
            Directory.CreateDirectory(sub);

            await harness.Coordinator.StartExtractAsync();

            Assert.True(Directory.Exists(sub), "子目录一个字节都不许动");
            Assert.True(Directory.Exists(target));

            Assert.Contains(
                harness.Log.Logs,
                item => item.Level == "WARN" &&
                        item.Message.Contains("没有清理", StringComparison.Ordinal) &&
                        item.Message.Contains(target, StringComparison.OrdinalIgnoreCase));
        }

        // ================================================================ 三、正常工作流不受影响

        /// <summary>
        /// 用例 4：**正常工作流一个字都没改** —— 本批自己的目标目录与工作区照旧
        /// （工作区仍然落在 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>、批尾空壳照旧清掉、
        /// 用户原本就在目录里的东西一个字节都不动、失败时源包原地不动），而且**不会**多出任何"残留"日志。
        /// </summary>
        [Fact]
        public async Task 正常工作流_目标目录与工作区照旧()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, "normal-flow.7z");
            string target = ResolveTarget(harness, task);
            string userFile = Path.Combine(target, "我原来就在这里的东西.txt");

            Directory.CreateDirectory(target);
            File.WriteAllText(userFile, "解压之前就放在目标目录里");

            await harness.Coordinator.StartExtractAsync();

            // ① 用户自己的东西一个字节都不动，那个目录也照旧在。
            Assert.True(File.Exists(userFile));
            Assert.Equal("解压之前就放在目标目录里", File.ReadAllText(userFile));
            Assert.True(Directory.Exists(target));

            // ② 这一批的工作区仍然由目标目录派生（日志里那一句照旧）。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains(
                    Path.Combine(target, WorkspaceRootResolver.DefaultWorkspaceDirectoryName),
                    StringComparison.OrdinalIgnoreCase));

            // ③ 批尾照旧清掉空的工作区壳（口径一个字没改）。
            Assert.False(
                Directory.Exists(Path.Combine(target, WorkspaceRootResolver.DefaultWorkspaceDirectoryName)),
                "零文件空壳无论如何都删 —— 这条既有口径不许被这次改动带偏");

            // ④ 这一批里根本没有"上次的残留"这回事，就不该有那条日志。
            Assert.DoesNotContain(
                harness.Log.Logs,
                item => item.Message.Contains("上次没跑完的残留", StringComparison.Ordinal));

            // ⑤ 失败的任务：源包原地不动（不变量 1）。
            Assert.True(File.Exists(Path.Combine(harness.SourceRoot, "normal-flow.7z")));
        }

        // ================================================================ 四、越界红线

        /// <summary>
        /// 用例 5：⛔ **目标目录之外一个字节都不动**。
        ///
        /// <para>摆一个隔壁目录，形状和"残留空壳"**一模一样**（目录里只有一个空的 <c>.ArchiveFixer.work</c>）——
        /// 它不是这一批的目标目录，就不许碰；周围整棵目录树（文件、目录、字节数）跑前跑后逐条一致。
        /// 这一条同时钉住"⛔ 不许扫描整盘找残留"：判据只看调用方交给它的那几个目标目录。</para>
        /// </summary>
        [Fact]
        public async Task 越界红线_目标目录之外一个字节都不动()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, "neighbour-touched.7z");
            string target = ResolveTarget(harness, task);

            // 本批的目标目录：一个真的残留空壳（该被收掉）。
            CreateEmptyWorkspace(target);

            // 隔壁那些**一个都不许动**的东西（包括一个长得一模一样的"残留"）。
            string siblingShell = NewDirectory(Path.Combine(harness.OutputRoot, "sibling-shell"));
            string siblingWorkspace = CreateEmptyWorkspace(siblingShell);
            string otherTree = Path.Combine(harness.OutputRoot, "other-tree", "sub");

            Directory.CreateDirectory(otherTree);
            File.WriteAllBytes(Path.Combine(otherTree, "payload.bin"), new byte[] { 1, 2, 3, 4, 5 });
            File.WriteAllText(Path.Combine(harness.OutputRoot, "loose.txt"), "散在输出根下的一个文件");
            Directory.CreateDirectory(Path.Combine(harness.OutputRoot, "bare-empty-dir"));

            List<string> before = Snapshot(harness.OutputRoot, excludeSubtree: target);

            await harness.Coordinator.StartExtractAsync();

            Assert.False(Directory.Exists(target), "本批自己的那个残留空壳该收掉");

            Assert.True(
                Directory.Exists(siblingWorkspace),
                "隔壁那个「目录 + 空 .ArchiveFixer.work」不是这一批的目标目录 —— 一个字节都不许动");
            Assert.True(Directory.Exists(siblingShell));
            Assert.True(Directory.Exists(Path.Combine(harness.OutputRoot, "bare-empty-dir")));
            Assert.True(File.Exists(Path.Combine(harness.OutputRoot, "loose.txt")));

            Assert.Equal(before, Snapshot(harness.OutputRoot, excludeSubtree: target));
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public MainViewModel Vm { get; init; } = null!;

            public FakeEngine Engine { get; init; } = null!;

            public ExtractionCoordinator Coordinator { get; init; } = null!;

            public LogService Log { get; init; } = null!;

            public PathService PathService { get; init; } = null!;

            public string SourceRoot { get; init; } = string.Empty;

            public string OutputRoot { get; init; } = string.Empty;
        }

        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string sourceRoot = Path.Combine(_root, "src");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.AutoScanAfterDrop = false;
            settings.RecursionMode = "SingleLayer";
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.ExtractToOriginalDirectory = false;
            settings.CustomOutputDirectory = outputRoot;
            settings.KeepArchiveNameFolder = true;
            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

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

            // 这一组拿「细节日志」当行为证据。
            coordinator.KeepTaskDetailInLog = true;

            return new Harness
            {
                Vm = vm,
                Engine = engine,
                Coordinator = coordinator,
                Log = logService,
                PathService = pathService,
                SourceRoot = sourceRoot,
                OutputRoot = outputRoot
            };
        }

        /// <summary>
        /// 造一个任务：假引擎解压一律报"密码错误" —— 这一组只关心**批首收残留**这件事，
        /// 失败恰好是最省事又最贴近现场的那一档（真机上那个 <c>1111</c> 就是跑到一半被打断的）。
        /// </summary>
        private ArchiveTask AddTask(Harness harness, string fileName)
        {
            string sourcePath = Path.Combine(harness.SourceRoot, fileName);

            File.WriteAllText(sourcePath, "not a real archive - the engine is faked in these tests");

            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(task);

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(
                ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            return task;
        }

        /// <summary>
        /// 本任务的落点（目标目录）——走**唯一实现** <see cref="PathService.ResolveOutputPlacement"/>，
        /// 测试不自己拼"输出根 + 包名"那套规则（拼一份迟早与实现分叉）。
        /// </summary>
        private static string ResolveTarget(Harness harness, ArchiveTask task)
        {
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = harness.Vm.Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = harness.Vm.Settings.CustomOutputDirectory,
                KeepArchiveNameFolder = harness.Vm.Settings.KeepArchiveNameFolder
            };

            options.Normalize();

            OutputPlacementResult placement = harness.PathService.ResolveOutputPlacement(task, options);

            Assert.True(placement.Success, "前提：这个任务的落点算得出来");
            Assert.False(string.IsNullOrWhiteSpace(placement.DestinationDirectory));

            return placement.DestinationDirectory;
        }

        private string NewDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>造出"上一次被中断留下的空工作区"（名字走唯一常量，⛔ 不在测试里另写一遍字面量）。</summary>
        private static string CreateEmptyWorkspace(string targetDirectory)
        {
            string workspace = Path.Combine(targetDirectory, WorkspaceRootResolver.DefaultWorkspaceDirectoryName);

            Directory.CreateDirectory(workspace);
            return workspace;
        }

        /// <summary>
        /// 周围目录树的快照（相对路径 + 是不是目录 + 字节数），**排除本批自己的那个目标目录**
        /// （它本来就会随这一批的收尾而消失）。跑前跑后逐条比对，证明"目标目录之外一个字节都没动"。
        /// </summary>
        private static List<string> Snapshot(string root, string excludeSubtree)
        {
            var lines = new List<string>();
            var pending = new Stack<string>();

            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(current);
                }
                catch
                {
                    lines.Add(current[(root.Length)..] + "|unreadable");
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (string.Equals(entry, excludeSubtree, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;

                    lines.Add(
                        entry[(root.Length)..]
                        + (isDirectory ? "|dir" : "|file|" + new FileInfo(entry).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));

                    if (isDirectory)
                    {
                        pending.Push(entry);
                    }
                }
            }

            lines.Sort(StringComparer.OrdinalIgnoreCase);
            return lines;
        }

        private static ArchiveOperationResult WrongPassword() => new()
        {
            Success = false,
            Status = StatusText.WrongPassword,
            Message = "密码错误",
            DetectedErrorType = "WrongPassword"
        };

        /// <summary>可控的假引擎（与 <c>ExtractionPipelineFixTests</c> / <c>WorkspaceRootTests</c> 同一套做法）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

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

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                OnListAsync != null
                    ? OnListAsync(request)
                    : Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(WrongPassword());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default) =>
                OnExtractAsync != null
                    ? OnExtractAsync(request)
                    : Task.FromResult(WrongPassword());
        }
    }
}
