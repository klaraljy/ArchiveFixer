using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **链尾「其余物」按设置处理：判据只问"这一单自己的根源包那一层"**（用户 2026-10-04 拍板，
    /// 推翻 2026-09-25 第 37 条那道"整条链都得成功"的闸门）。
    ///
    /// <para><b>用户原话</b>：「你不会读设置吗，我勾选了保留吗，没勾选你留着干什么」——
    /// 他选了「其余物 = 彻底删除」，只因为链上某个**内层包**失败（根源包自己那一层是成功的、
    /// 输出校验也过了）就整份留着（真机日志 271/272 就是这个形状）。</para>
    ///
    /// <para><b>现在的口径</b>：根源包成功 + 输出校验通过 + 未取消 + 完整性可证 ⇒
    /// 按③页「删除操作」处理其余物（Delete / RecycleBin）；⛔ 不再要求链上每个内层包都成功。
    /// 红线一个字没动：失败 / 部分完成 / 取消的**那一单** ⇒ 源包原地不动、其余物不生成；
    /// 删除只有 <c>RestItemPurger</c> 一个执行体，六道门槛（含「半套分卷」）照旧全过。</para>
    ///
    /// <para><b>四个用例</b>（真 7z + 真 <see cref="MainViewModel"/> + 真解压管线）：
    /// ① 根源包成功 + 内层包失败 + 彻底删除 ⇒ **其余物真被删**（源包也随之按设置消失）；
    /// ② 同场景 + 「不动其余物」 ⇒ 其余物留着（照设置）；
    /// ③ 根源包自己失败 / 部分完成 / 取消 ⇒ **一个字节都不动**（对照，⛔ 不许放宽）；
    /// ④ 「半套分卷」那道闸门照旧拦得住（回归）。</para>
    ///
    /// <para><b>红检</b>：把"不要求整链成功"这一条改回旧口径（把 `ChainCompletionGate` 那道整链闸门
    /// 加回 <c>ApplyRestHandlingAfterChainAsync</c>）⇒ 用例 ①② 一起变红
    /// （①：`Assert.Empty(FindFiles("outer.7z"))` 失败，源包与其余物都还在；②：`Assert.NotEmpty` 之外
    /// 还会多出"链尾的其余物不处理"那行日志的对照断言）。</para>
    ///
    /// <para><b>⛔ 自我约束</b>：绝不往用户的系统回收站里塞东西 —— 回收站那一档全部走注入的假执行器
    /// （<c>ExtractionCoordinator.RestDeleteExecutor</c> 这个测试缝）；只在临时目录里动真实文件系统。</para>
    /// </summary>
    public class ChainRestHandlingSettingTests : IDisposable
    {
        /// <summary>拿它给内层包加密码、密码本里没有 ⇒ 那一层必然失败（用例 ①②）。</summary>
        private const string UnknownLayerPassword = "测试用未知密码";

        private readonly string _root;
        private readonly string _sevenZip;
        private readonly ITestOutputHelper _output;

        public ChainRestHandlingSettingTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerChainRest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
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

        // ================================================================ 用例 ① / ②

        /// <summary>
        /// <b>用例 ①</b>：两层真链（<c>outer.7z</c> → <c>level2.7z</c>），内层包用密码包、密码本里没有
        /// ⇒ **内层那一单失败**，而根源包那一层成功（它自己的内容文件 <c>layer1.txt</c> 定稿了）。
        ///
        /// <para>③页「删除操作」= 彻底删除 ⇒ 链尾**必须**按设置把其余物删掉：
        /// 盘上不再有 <c>outer.7z</c>、其余物目录也没了，而内容物 <c>layer1.txt</c> 在。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例1_根源包成功而内层包失败_彻底删除档下其余物真被删()
        {
            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.MoveToRest);
            string outer = BuildTwoLayerChain(failInner: true);

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            _output.WriteLine(
                $"【用例①】删源包名单 {harness.SourceDeletes.DeletedPaths.Count} 个 ｜ 日志 {harness.Log.Logs.Count} 行 ｜ "
                + $"盘上还剩的包：{string.Join("、", FindFiles("*.7z").Select(Path.GetFileName))}");

            // 内层那一单确实失败了（不然这个用例什么都没验到）。
            Assert.Contains(harness.Vm.Tasks, task => task.Outcome == TaskOutcome.Failed);

            // ① 内容物出来了（根源包那一层是成功的）。
            Assert.NotEmpty(FindFiles("layer1.txt"));

            // ② 其余物真被删：源包（它按"源包操作 = 放入其余物"进了其余物）与其余物目录都不在盘上。
            Assert.Empty(FindFiles("outer.7z"));
            Assert.Empty(FindDirectories(ProcessArtifactLayout.ArtifactDirectoryName));

            // ③ 其余物**真的走了删除执行体**（事实而不是措辞），而且日志按档名如实说。
            Assert.NotEmpty(harness.RecycleExecutor.PermanentCalls);
            Assert.Empty(harness.RecycleExecutor.RecycleCalls);

            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains(StatusText.RestActionDelete + "：", StringComparison.Ordinal));

            // ④ ⛔ 不再有"链尾的其余物不处理"那句（判据换了，那句话就该消失）。
            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains(StatusText.ChainRestBlockedPrefix, StringComparison.Ordinal));
        }

        /// <summary>
        /// <b>用例 ②</b>：同一个现场，只把③页「删除操作」换成「不动其余物」⇒ **其余物留着**（照设置）。
        ///
        /// <para>这一条与用例 ① 是一对：说明"按设置"是**双向**的 —— 判据换了，但档位照样说话。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例2_同场景而不动其余物档下其余物留着()
        {
            Harness harness = CreateHarness(RestHandlingModes.Keep, SourceHandlingMode.MoveToRest);
            string outer = BuildTwoLayerChain(failInner: true);

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            // ① 源包进其余物之后**一直在盘上**（其余物没被处理）。
            string[] packages = FindFiles("outer.7z");

            Assert.NotEmpty(packages);

            Assert.Contains(
                packages,
                path => path.Contains(ProcessArtifactLayout.ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase));

            // ② 一个字都没删（假执行器一次都没被调用）。
            Assert.Empty(harness.RecycleExecutor.RecycleCalls);
            Assert.Empty(harness.RecycleExecutor.PermanentCalls);

            // ③ 任何"删了 / 搬进回收站"的结论都不许出现。
            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains(StatusText.RestActionDelete + "：", StringComparison.Ordinal)
                         || entry.Message.Contains(StatusText.RestActionRecycleBin + "：", StringComparison.Ordinal));

            /*
             * ④「不动其余物」这一档**不是"被拦下"**：没有任何"其余物没处理 + 为什么"记到任务上
             * （那句话只在真被某条判据挡下时才写）—— 这一档是照设置不动，两件事别混。
             */
            Assert.All(harness.Vm.Tasks, task => Assert.True(string.IsNullOrWhiteSpace(task.RestKeptReason)));
        }

        // ================================================================ 用例 ③

        /// <summary>
        /// <b>用例 ③（对照，⛔ 不许放宽）</b>：**根源包自己那一层就失败**（外层包是密码包、密码本里没有）
        /// ⇒ 一个字节都不动：源包**原地不动**、其余物不生成、删除执行器零调用，
        /// 而且日志与①页都要**如实写清原因**（逐链点名机制）。
        /// </summary>
        [SevenZipFact]
        public async Task 用例3_根源包自己失败_一个字节都不动并写清原因()
        {
            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.MoveToRest);
            string outer = BuildTwoLayerChain(failInner: false, lockOuter: true);

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            // ① 源包原地不动（路径都没变）。
            Assert.True(File.Exists(outer), "失败的那一单 ⇒ 源包必须原地不动（红线 1）");

            // ② 其余物一个都不生成、一个字节都没删。
            Assert.Empty(FindDirectories(ProcessArtifactLayout.ArtifactDirectoryName));
            Assert.Empty(harness.RecycleExecutor.RecycleCalls);
            Assert.Empty(harness.RecycleExecutor.PermanentCalls);
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

            // ③ 根任务落失败（不变量 6）。
            Assert.Contains(harness.Vm.Tasks, task => task.Outcome == TaskOutcome.Failed);

            // ④ 逐链点名：①页「错误信息」列 / 批末诊断 / 失败清单读的是同一句（用户 2026-10-04 真机要求）。
            ArchiveTask root = harness.Vm.Tasks.First(task => !task.IsContinuationTask);

            Assert.False(string.IsNullOrWhiteSpace(root.RestKeptReason), "其余物没处理时必须如实写清原因");

            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains(StatusText.ChainRestBlockedPrefix, StringComparison.Ordinal));
        }

        /// <summary>
        /// <b>用例 ③ 的另两档（部分完成 / 取消）</b>：这两档在**唯一执行体**里判
        /// （<see cref="RestItemPurger.Purge"/> 的第 1 / 3 道门槛）—— 到不了执行体时协调器还会先拦一道。
        /// 这里直接钉执行体：现场是一个"该动手的其余物"，只把终态 / 取消位改成那两档 ⇒ 一律不删。
        /// </summary>
        [Fact]
        public void 用例3_部分完成与取消_其余物一个字节都不删()
        {
            foreach (TaskOutcome outcome in new[] { TaskOutcome.PartiallyCompleted, TaskOutcome.Failed, TaskOutcome.Pending })
            {
                (ArchiveTask task, string sourcePath, string restDirectory) = CreateRemovableRest();
                task.Outcome = outcome;

                RestPurgeOutcome result = new RestItemPurger().Purge(task, cancelled: false, DeleteMode.Permanent);

                Assert.False(result.Attempted, $"终态 {outcome} 时不许动其余物");
                Assert.True(Directory.Exists(restDirectory), $"终态 {outcome} 时其余物必须原封不动");
                Assert.True(File.Exists(sourcePath), $"终态 {outcome} 时源包必须原封不动");
            }

            (ArchiveTask cancelledTask, string cancelledSource, string cancelledRest) = CreateRemovableRest();

            cancelledTask.Outcome = TaskOutcome.Succeeded;
            cancelledTask.OutputVerification = OutputVerificationOutcome.Passed;
            cancelledTask.OutputManifestCrossChecked = true;

            RestPurgeOutcome cancelled = new RestItemPurger().Purge(cancelledTask, cancelled: true, DeleteMode.Permanent);

            Assert.False(cancelled.Attempted, "已取消时不许动其余物");
            Assert.True(Directory.Exists(cancelledRest), "已取消时其余物必须原封不动");
            Assert.True(File.Exists(cancelledSource), "已取消时源包必须原封不动");
        }

        // ================================================================ 用例 ④（回归）

        /// <summary>
        /// <b>用例 ④（回归）</b>：「其余物里的分卷是**半套**」那道闸门照旧拦得住，而且排在
        /// 2026-10-04 新增的「内容物保留关键词」判据**之前**（两件事互不干扰，谁也不许绕过谁）。
        /// </summary>
        [Fact]
        public void 用例4_半套分卷那道闸门照旧拦得住()
        {
            (ArchiveTask task, _, string restDirectory) = CreateRemovableRest();

            task.Outcome = TaskOutcome.Succeeded;
            task.OutputVerification = OutputVerificationOutcome.Passed;
            task.OutputManifestCrossChecked = true;

            // 成品目录里那一片（同基名的另一卷）—— 其余物那一份就是"半套"。
            string output = Directory.GetParent(restDirectory)!.FullName;

            File.WriteAllBytes(Path.Combine(restDirectory, "一只顶美.z01"), new byte[64]);
            File.WriteAllBytes(Path.Combine(output, "一只顶美.z删除ip"), new byte[64]);

            // 就算这一次带着"内容物保留关键词"判据，半套分卷也必须先把它拦下。
            RestPurgeOutcome outcome = new RestItemPurger(
                    null,
                    null,
                    Security.ContentKeepRules.FromKeywords(new[] { "一只顶美" }))
                .Purge(task, cancelled: false, DeleteMode.Permanent);

            Assert.False(outcome.Attempted);
            Assert.True(Directory.Exists(restDirectory), "半套分卷时其余物必须原封不动");
        }

        /// <summary>造一个"该动手的其余物现场"：成功终态 + 其余物里有源包 + 成品目录里有内容物。</summary>
        private (ArchiveTask Task, string SourcePath, string RestDirectory) CreateRemovableRest()
        {
            string outputPath = Path.Combine(_root, "rest-case-" + Guid.NewGuid().ToString("N"));
            string restDirectory = Path.Combine(outputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Directory.CreateDirectory(restDirectory);

            string sourcePath = Path.Combine(restDirectory, "pack.7z");
            File.WriteAllText(sourcePath, "源包");
            File.WriteAllText(Path.Combine(outputPath, "content.bin"), "内容物");

            var task = new ArchiveTask(sourcePath, 1)
            {
                FileName = "pack.7z",
                CurrentPath = sourcePath,
                OutputPath = outputPath,
                RestDirectoryPath = restDirectory,
                Outcome = TaskOutcome.Succeeded,
                IsOutputVerified = true,
                OutputManifestCrossChecked = true
            };

            return (task, sourcePath, restDirectory);
        }

        // ================================================================ 夹具

        /// <summary>
        /// 造一条两层真链：<c>outer.7z → level2.7z → final.txt</c>，两层各带一个自己的内容文件。
        ///
        /// <para><paramref name="failInner"/> = true ⇒ <c>level2.7z</c> 用密码包（<c>-mhe=on</c>）、
        /// 密码不进密码本 ⇒ 内层那一单必然失败，而根源包那一层成功（<c>layer1.txt</c> 会定稿）。
        /// <paramref name="lockOuter"/> = true ⇒ 外层包自己就是密码包 ⇒ 根源包那一层就失败。</para>
        /// </summary>
        private string BuildTwoLayerChain(bool failInner, bool lockOuter = false)
        {
            string build = Path.Combine(_root, "chain-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(build, "layer2.txt"), "第 2 层自己的内容\n", new UTF8Encoding(false));

            if (failInner)
            {
                Run7z(build, "a", "-t7z", "level2.7z", "-p" + UnknownLayerPassword, "-mhe=on", "final.txt", "layer2.txt");
            }
            else
            {
                Run7z(build, "a", "-t7z", "level2.7z", "final.txt", "layer2.txt");
            }

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");

            if (lockOuter)
            {
                Run7z(build, "a", "-t7z", outer, "-p" + UnknownLayerPassword, "-mhe=on", "level2.7z", "layer1.txt");
            }
            else
            {
                Run7z(build, "a", "-t7z", outer, "level2.7z", "layer1.txt");
            }

            // 造样本的脚手架不是管线产物：留着会让"盘上还剩什么"这类断言自欺欺人。
            Directory.Delete(build, recursive: true);

            return outer;
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                psi.ArgumentList.Add(a.ToString() ?? string.Empty);
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        private string[] FindFiles(string pattern) =>
            Directory.Exists(_root)
                ? Directory.GetFiles(_root, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();

        private string[] FindDirectories(string name) =>
            Directory.Exists(_root)
                ? Directory.GetDirectories(_root, name, SearchOption.AllDirectories)
                : Array.Empty<string>();

        private Harness CreateHarness(string restMode, SourceHandlingMode sourceMode)
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
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.TryEmptyPasswordFirst = false;
            settings.SourceHandling = sourceMode.ToString();
            settings.RestHandlingAfterVerify = restMode;
            settings.CustomSevenZipExePath = string.Empty;

            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

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

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            var recycle = new FakeDeleteExecutor();
            var sourceDeletes = new RecordingDeleteFileSystem();

            extraction.RestDeleteExecutor = recycle;
            extraction.SourcePackageDeleteFileSystem = sourceDeletes;
            extraction.KeepTaskDetailInLog = true;

            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, recycle, sourceDeletes, logService);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                OneClickCoordinator oneClick,
                FakeDeleteExecutor recycle,
                RecordingDeleteFileSystem sourceDeletes,
                LogService log)
            {
                Vm = vm;
                _oneClick = oneClick;
                RecycleExecutor = recycle;
                SourceDeletes = sourceDeletes;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public FakeDeleteExecutor RecycleExecutor { get; }

            public RecordingDeleteFileSystem SourceDeletes { get; }

            public LogService Log { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 记账式的"删源包"执行体（产品代码默认走真实文件系统，这里只替换执行体、判据一个字不改）。
        /// </summary>
        private sealed class RecordingDeleteFileSystem : ISourceDeleteFileSystem
        {
            public List<string> DeletedPaths { get; } = new();

            public bool FileExists(string path) => File.Exists(path);

            public void DeleteFile(string path)
            {
                DeletedPaths.Add(path);
                File.Delete(path);
            }
        }

        /// <summary>
        /// 假的其余物删除执行器（**绝不碰用户的系统回收站**）：记调用、按档真删临时目录。
        /// </summary>
        private sealed class FakeDeleteExecutor : IDeleteExecutor
        {
            public string RecycleMessage { get; set; } = "已移入回收站（假执行器）";

            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = RecycleMessage;

                Delete(path, isDirectory);

                return RecycleAttemptResult.Recycled;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);
                Delete(path, isDirectory);
            }

            private static void Delete(string path, bool isDirectory)
            {
                if (isDirectory && Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
