using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 密码"成功一次就复用" + "按使用频率排序"的回归（用户 2026-09-24 第 14 条）。
    ///
    /// <para>用户原话："如果测密码，<b>只要测到一个成功了就不要去再尝试了</b>，你现在每次都尝试，
    /// 如果是密码本里面的第 5 个，如果是 200 个压缩包，那你岂不是要尝试 1000 遍……
    /// 而且密码本里面的优先级差不多就是<b>使用次数的频繁程度</b>"。</para>
    ///
    /// <para>五组判据：① 第 1 个包在第 5 个候选成功 → 第 2 个包**除空密码外只被问一次**（就是那条密码），
    /// 且日志写明"复用本批已成功的密码"；② 20 个同源包的总尝试次数从 O(N×5) 降到 O(N)；
    /// ③ 两个包密码不同时，第二个包仍然能试到它自己那条（复用只是提前，不是替换）；
    /// ④ 同层级按成功次数从多到少排（次数相同保持原顺序）；
    /// ⑤ 成功次数落盘 / 读回往返正确，且**旧版本文件（v1）读得进来**。</para>
    /// </summary>
    public sealed class PasswordReuseTests : IDisposable
    {
        private readonly string _root;

        public PasswordReuseTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPasswordReuse", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 假引擎计数：第二个包只被问一次

        /// <summary>
        /// 第 1 个包在第 5 个候选上才成功；第 2 个包的**第一次尝试**就必须是那条密码（连空密码都不再有）。
        ///
        /// <para>⚠ 2026-09-26 第 45 条改了口径（原来钉的是"除空密码外只被问一次"）：本批已经有成功的密码时，
        /// 空密码**根本不再进候选表** —— 加密包的空密码永远不可能对，先试它只是白跑一轮。
        /// 用户真机上每个包都是"先空密码、再复用"，他原话："不就是在放屁吗，每次都是重新一轮密码检测"。</para>
        /// </summary>
        [Fact]
        public async Task 第一包第五候选成功_第二包第一次尝试就是复用来的密码()
        {
            string password = "<示例密码5>";

            Harness harness = CreateHarness("reuse-log", settings =>
            {
                settings.MaxPasswordAttemptsPerLayer = 10;
                settings.TryEmptyPasswordFirst = true;
            });

            harness.Book.AddPassword("<示例密码1>");
            harness.Book.AddPassword("<示例密码2>");
            harness.Book.AddPassword("<示例密码3>");
            harness.Book.AddPassword("<示例密码4>");
            harness.Book.AddPassword(password);

            ArchiveTask first = harness.AddTask("first.7z", password);
            ArchiveTask second = harness.AddTask("second.7z", password);

            await harness.RunAsync();

            Assert.Equal(StatusText.ExtractSuccess, first.Status);
            Assert.Equal(StatusText.ExtractSuccess, second.Status);

            // 第 1 个包：空密码 + 4 条错密码 + 第 5 条 = 6 次（证明它确实在第 5 个候选上成功）。
            Assert.Equal(6, harness.Engine.AttemptsFor("first.7z").Count);

            // 第 2 个包：**一次**，而且就是那条密码（空密码不再进来）。
            List<string> secondAttempts = harness.Engine.AttemptsFor("second.7z");

            Assert.Single(secondAttempts);
            Assert.Equal(password, secondAttempts[0]);

            /*
             * ⚠ 第 45 条起，"复用本批已成功的密码"这一行**在默认档里看不到**了 —— 它是任务细节，
             * 而这个包**成功**了（"成功就丢"）。顺序本身由上面那个假引擎的尝试记录钉着（只有一次、就是那条密码）；
             * "日志里看得见复用"这件事由**详细档**那条用例钉（Item45LogAndPasswordTests.真7z_密码复用…）。
             * 这里只保留"绝不出现密码明文"这一条。
             */
            string[] secondLines = harness.LogTexts
                .Where(line => line.Contains("second.7z", StringComparison.Ordinal))
                .ToArray();

            Assert.DoesNotContain(secondLines, line => line.Contains(password, StringComparison.Ordinal));
        }

        // ================================================================ ② 20 个同源包：O(N×5) → O(N)

        /// <summary>
        /// 20 个同源包（都只认密码本里第 5 条密码）：**没有复用**时是 20×5 = 100 次候选尝试，
        /// 复用之后是 20 次（每个包一次；空密码在 <c>TryEmptyPasswordFirst=false</c> 下不出现）。
        ///
        /// <para>这里数的是"候选尝试次数"（= 引擎被问到第几个候选），用真管线跑 20 个包太慢，
        /// 所以用同一个候选组装实现 + 一个"只认那条密码"的判定器来数 —— 判据是**比例**，
        /// 与用户那句"200 个包岂不是要尝试 1000 遍"是同一件事。</para>
        /// </summary>
        [Fact]
        public void 二十个同源包_总尝试次数从乘k降到线性()
        {
            const int packageCount = 20;
            const int winningIndex = 4;      // 密码本里第 5 条（0 基的 4）

            string password = "<示例密码5>";
            string[] book = { "<示例密码1>", "<示例密码2>", "<示例密码3>", "<示例密码4>", password };

            static PasswordService NewService(string[] passwords)
            {
                var service = new PasswordService();

                foreach (string value in passwords)
                {
                    service.AddPassword(value);
                }

                return service;
            }

            static int CountAttempts(PasswordService passwordService, string archiveName, string required)
            {
                var task = new ArchiveTask(Path.Combine(Path.GetTempPath(), archiveName));

                List<PasswordItem> candidates = passwordService.GetPasswordCandidates(
                    task,
                    globalPassword: string.Empty,
                    passwordList: passwordService.Passwords,
                    tryEmptyFirst: false);

                int attempts = 0;

                foreach (PasswordItem candidate in candidates)
                {
                    attempts++;

                    if (string.Equals(candidate.Value, required, StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                return attempts;
            }

            // 没有复用（一条都没成功过）：每个包都要从头数到第 5 条 → N × k。
            var withoutReuseService = NewService(book);
            int withoutReuse = 0;

            for (int i = 0; i < packageCount; i++)
            {
                withoutReuse += CountAttempts(withoutReuseService, $"p{i}.7z", password);
            }

            /*
             * 有复用：第 1 个包照旧要数到第 5 条，从第 2 个包开始第一次就命中
             * （真管线里"成功即记账"发生在每个包成功的那一刻，这里用同样的顺序模拟）。
             */
            var withReuseService = NewService(book);
            int withReuse = 0;

            for (int i = 0; i < packageCount; i++)
            {
                withReuse += CountAttempts(withReuseService, $"p{i}.7z", password);

                withReuseService.RecordPasswordSuccess(
                    Path.Combine(Path.GetTempPath(), $"p{i}.7z"),
                    password);
            }

            Assert.Equal(packageCount * (winningIndex + 1), withoutReuse);     // 20 × 5 = 100
            Assert.Equal(winningIndex + 1 + (packageCount - 1), withReuse);    // 5 + 19 = 24

            // 判据是**比例**：从 O(N×k) 降到 O(N+k-1)，也就是 k=5 时不足原来的四分之一。
            Assert.True(
                withReuse * (winningIndex + 1) < withoutReuse * 2,
                $"复用后总次数应当是线性的：实测 {withReuse} 次 vs 未复用 {withoutReuse} 次");
        }

        // ================================================================ ③ 不同密码的包仍然能试到自己的那条

        /// <summary>
        /// 第 1 个包用 A 成功、第 2 个包需要 B：复用只是把 A 排到前面，
        /// ⛔ 绝不允许"因为复用过了就只试 A" —— B 必须还在候选链里，而且真的能试到。
        /// </summary>
        [Fact]
        public void 不同密码的包_复用之后仍然能试到它自己那条()
        {
            var service = new PasswordService();
            service.AddPassword("<示例密码A>");
            service.AddPassword("<示例密码B>");

            var winning = new ArchiveTask(Path.Combine(Path.GetTempPath(), "first.7z"));
            service.RecordPasswordSuccess(winning.CurrentPath, "<示例密码A>");

            var other = new ArchiveTask(Path.Combine(Path.GetTempPath(), "second.7z"));

            List<PasswordItem> candidates = service.GetPasswordCandidates(
                other,
                globalPassword: string.Empty,
                passwordList: service.Passwords,
                tryEmptyFirst: false);

            // 复用项在最前（A），但 B 一条都没少。
            Assert.Equal("<示例密码A>", candidates[0].Value);
            Assert.Contains(candidates, candidate => candidate.Value == "<示例密码B>");

            // 模拟"第二个包要 B"：按顺序试，必须在候选链里找到 B。
            int index = candidates.FindIndex(candidate => candidate.Value == "<示例密码B>");

            Assert.True(index >= 0, "复用的密码链里丢了 B：那就变成'只试 A'了");
        }

        [Fact]
        public async Task 端到端_两个包密码不同_第二个包仍然解开()
        {
            Harness harness = CreateHarness("reuse-different");

            harness.Book.AddPassword("<示例密码A>");
            harness.Book.AddPassword("<示例密码B>");

            ArchiveTask first = harness.AddTask("first.7z", "<示例密码A>");
            ArchiveTask second = harness.AddTask("second.7z", "<示例密码B>");

            await harness.RunAsync();

            Assert.Equal(StatusText.ExtractSuccess, first.Status);
            Assert.Equal(StatusText.ExtractSuccess, second.Status);

            // 第二个包先被问 A（复用），失败之后照旧试到 B —— 一次都不能少。
            List<string> secondAttempts = harness.Engine.AttemptsFor("second.7z");

            Assert.Contains("<示例密码A>", secondAttempts);
            Assert.Contains("<示例密码B>", secondAttempts);
        }

        // ================================================================ ④ 按使用次数排序

        /// <summary>
        /// 同层级按成功次数从多到少（次数相同保持原顺序）。
        ///
        /// <para>这里刻意**从落盘的记忆里恢复次数**（新服务 + <c>LoadRememberedList</c>）：
        /// 于是"本批已成功"那一条提前项为空，验的就是纯粹的**频率排序**本身 ——
        /// 也正是用户跨重启时期望的那个行为。</para>
        /// </summary>
        [Fact]
        public void 候选排序_成功次数多的在前_次数相同保持原顺序()
        {
            string dataRoot = Path.Combine(_root, "ordering");
            Directory.CreateDirectory(dataRoot);

            var snapshot = new PasswordListSnapshot();

            snapshot.Entries.Add(new PasswordListEntry { Value = "<示例密码1>", Source = "ManualList" });
            snapshot.Entries.Add(new PasswordListEntry { Value = "<示例密码2>", Source = "ManualList" });
            snapshot.Entries.Add(new PasswordListEntry { Value = "<示例密码3>", Source = "ManualList" });
            snapshot.Entries.Add(new PasswordListEntry { Value = "<示例密码4>", Source = "ManualList" });

            snapshot.SuccessCounts["<示例密码3>"] = 3;
            snapshot.SuccessCounts["<示例密码2>"] = 1;
            snapshot.SuccessCounts["<示例密码1>"] = 7;
            // <示例密码4> 一次都没成功过 → 0。

            Assert.True(new PasswordListStore { DataRootDirectory = dataRoot }.Save(snapshot).Success);

            var service = new PasswordService { DataRootDirectory = dataRoot, RememberPasswordList = true };

            Assert.Equal(PasswordListLoadStatus.Loaded, service.LoadRememberedList());
            Assert.Empty(service.BatchVerifiedPasswords);

            List<PasswordItem> candidates = service.GetPasswordCandidates(
                new ArchiveTask(@"C:\whatever.7z"),
                globalPassword: string.Empty,
                passwordList: service.Passwords,
                tryEmptyFirst: true);

            // 空密码仍在最前（这一档没被动过）。
            Assert.Equal(string.Empty, candidates[0].Value);

            // 列表那一层：7 次 → 3 次 → 1 次 → 0 次。
            List<string> listLevel = candidates
                .Where(candidate => candidate.Source == "ImportedList")
                .Select(candidate => candidate.Value ?? string.Empty)
                .ToList();

            Assert.Equal(
                new[] { "<示例密码1>", "<示例密码3>", "<示例密码2>", "<示例密码4>" },
                listLevel);

            // 备注里的序号仍是用户在**列表里**看到的位置（排序不改变"第几项"，否则回列表里找不到）。
            Assert.Equal(
                "密码列表第 3 项",
                candidates.Single(candidate => candidate.Value == "<示例密码3>").Remark);

            // 次数相同 → 保持原顺序（稳定排序）。
            var ties = new PasswordService();
            ties.AddPassword("<示例密码甲>");
            ties.AddPassword("<示例密码乙>");

            List<string> tieOrder = ties
                .GetPasswordCandidates(
                    new ArchiveTask(@"C:\z.7z"),
                    globalPassword: string.Empty,
                    passwordList: ties.Passwords,
                    tryEmptyFirst: false)
                .Select(candidate => candidate.Value ?? string.Empty)
                .ToList();

            Assert.Equal(new[] { "<示例密码甲>", "<示例密码乙>" }, tieOrder);
        }

        /// <summary>
        /// 复用项的位置：**候选表第一条**，而且那时**空密码根本不在表里**。
        ///
        /// <para>⚠ 2026-09-26 第 45 条改了位置（原来钉的是"紧跟空密码之后"，那是在**还没有已知密码**的
        /// 前提下定的）：用户原话"我这个在用特定解压，是多相同文件，密码都是一样的，你这个复用本批次
        /// 已成功的密码，不就是在放屁吗，每次都是重新一轮密码检测" —— 有已知密码时，加密包的空密码
        /// 永远不可能对，先试它纯粹白跑一轮。</para>
        /// </summary>
        [Fact]
        public void 复用项位置_排在候选表第一条_且空密码不再进表()
        {
            var service = new PasswordService();
            service.AddPassword("<示例密码X>");
            service.AddPassword("<示例密码复用>");

            service.RecordPasswordSuccess(@"C:\done.7z", "<示例密码复用>");

            List<PasswordItem> candidates = service.GetPasswordCandidates(
                new ArchiveTask(@"C:\next.7z"),
                globalPassword: "<示例密码统一>",
                passwordList: service.Passwords,
                tryEmptyFirst: true);

            Assert.Equal("BatchSuccess", candidates[0].Source);
            Assert.Equal("<示例密码复用>", candidates[0].Value);

            // ⛔ 空密码不在表里（这一条正是第 45 条要的）。
            Assert.DoesNotContain(candidates, candidate => candidate.Source == "Empty");

            // ⛔ 其余候选一条都不许少（复用只是提前）。
            Assert.Contains(candidates, candidate => candidate.Value == "<示例密码X>");
            Assert.Contains(candidates, candidate => candidate.Value == "<示例密码统一>");
        }

        // ================================================================ ⑤ 落盘 / 读回 / 旧版本兼容

        [Fact]
        public void 成功次数随列表加密落盘_读回往返一致()
        {
            string dataRoot = Path.Combine(_root, "roundtrip");
            Directory.CreateDirectory(dataRoot);

            var first = new PasswordService { DataRootDirectory = dataRoot, RememberPasswordList = true };
            first.AddPassword("<示例密码1>");
            first.AddPassword("<示例密码2>");

            first.RecordPasswordSuccess(@"C:\a.7z", "<示例密码1>");
            first.RecordPasswordSuccess(@"C:\b.7z", "<示例密码1>");
            first.RecordPasswordSuccess(@"C:\c.7z", "<示例密码2>");

            Assert.True(File.Exists(first.ListStore.FilePath), "记忆文件应当已经写出来");

            var reloaded = new PasswordService { DataRootDirectory = dataRoot, RememberPasswordList = true };

            Assert.Equal(PasswordListLoadStatus.Loaded, reloaded.LoadRememberedList());

            Assert.Equal(2, reloaded.GetSuccessCount("<示例密码1>"));
            Assert.Equal(1, reloaded.GetSuccessCount("<示例密码2>"));
            Assert.Equal(0, reloaded.GetSuccessCount("<示例密码3>"));

            // 重启后"按使用频率排序"照样成立。
            List<string> order = reloaded
                .GetPasswordCandidates(
                    new ArchiveTask(@"C:\new.7z"),
                    globalPassword: string.Empty,
                    passwordList: reloaded.Passwords,
                    tryEmptyFirst: false)
                .Select(candidate => candidate.Value ?? string.Empty)
                .ToList();

            Assert.Equal(new[] { "<示例密码1>", "<示例密码2>" }, order);
        }

        /// <summary>
        /// 旧版本写下的记忆文件（版本 1，**没有**成功次数这一项）必须照样读得进来：
        /// 不报"格式不认识"、不丢条目，成功次数一律当 0（不猜）。
        /// </summary>
        [Fact]
        public void 旧版本记忆文件_读得进来且次数为空()
        {
            string dataRoot = Path.Combine(_root, "legacy");
            Directory.CreateDirectory(dataRoot);

            var store = new PasswordListStore { DataRootDirectory = dataRoot };

            // 先写一份"当前版本"的文件，证明这条路径本身是通的（否则下面的断言可能只是碰巧）。
            var current = new PasswordListSnapshot();
            current.Entries.Add(new PasswordListEntry { Value = "<示例密码1>", Source = "ManualList" });
            current.SuccessCounts["<示例密码1>"] = 7;

            Assert.True(store.Save(current).Success);

            PasswordListLoadResult loaded = store.Load();

            Assert.Equal(PasswordListLoadStatus.Loaded, loaded.Status);
            Assert.Equal(7, loaded.Snapshot.SuccessCounts["<示例密码1>"]);

            // 版本号认的范围：1（旧）到 2（当前）；再老/再新一律"不是我们的格式"。
            Assert.Equal(1, PasswordListStore.OldestSupportedVersion);
            Assert.Equal(2, PasswordListStore.CurrentVersion);

            /*
             * 真的造一份 **v1 载荷**（旧版本写下的形状：没有 SuccessCounts 这一项）走一遍解析：
             * 条目一个都不能丢，次数当 0（不猜）。载荷本身是加密落盘的，所以只能在这一层验。
             */
            byte[] versionOne = Encoding.UTF8.GetBytes(
                "{\"Version\":1,\"Entries\":[{\"Value\":\"<示例密码2>\",\"Source\":\"ManualList\"," +
                "\"Enabled\":true,\"Remark\":\"\"}],\"BookPaths\":[\"C:\\\\books\\\\a.txt\"],\"Tombstones\":[]}");

            PasswordListSnapshot? legacy = PasswordListStore.TryParsePayload(versionOne, out string reason);

            Assert.NotNull(legacy);
            Assert.Equal(string.Empty, reason);
            Assert.Single(legacy!.Entries);
            Assert.Equal("<示例密码2>", legacy.Entries[0].Value);
            Assert.Single(legacy.BookPaths);
            Assert.Empty(legacy.SuccessCounts);

            // 空表也要能往返（没有成功记录 = 一切按原顺序）。
            var empty = new PasswordListSnapshot();
            empty.Entries.Add(new PasswordListEntry { Value = "<示例密码2>", Source = "ManualList" });

            Assert.True(store.Save(empty).Success);

            PasswordListLoadResult reloaded = store.Load();

            Assert.Equal(PasswordListLoadStatus.Loaded, reloaded.Status);
            Assert.Single(reloaded.Snapshot.Entries);
            Assert.Empty(reloaded.Snapshot.SuccessCounts);
        }

        [Fact]
        public async Task 关掉记住密码列表_成功次数只留内存_一个字节都不写()
        {
            string dataRoot = Path.Combine(_root, "disabled");
            Directory.CreateDirectory(dataRoot);

            var service = new PasswordService { DataRootDirectory = dataRoot, RememberPasswordList = false };

            service.AddPassword("<示例密码1>");
            service.RecordPasswordSuccess(@"C:\a.7z", "<示例密码1>");

            Assert.Equal(1, service.GetSuccessCount("<示例密码1>"));
            Assert.False(File.Exists(service.ListStore.FilePath), "关掉开关时记忆文件一个字节都不该有");

            // 本次运行内复用照样生效（内存计数与开关无关）—— 第 45 条起它是**第一条**（空密码不再进表）。
            List<PasswordItem> candidates = service.GetPasswordCandidates(
                new ArchiveTask(@"C:\b.7z"),
                globalPassword: string.Empty,
                passwordList: service.Passwords,
                tryEmptyFirst: true);

            Assert.Equal("<示例密码1>", candidates[0].Value);
            Assert.Equal("BatchSuccess", candidates[0].Source);

            await Task.CompletedTask;
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                ReuseFakeEngine engine,
                ExtractionCoordinator coordinator,
                PasswordService book,
                LogService log,
                string sourceRoot)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Book = book;
                Log = log;
                SourceRoot = sourceRoot;
            }

            public MainViewModel Vm { get; }

            public ReuseFakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public PasswordService Book { get; }

            public LogService Log { get; }

            public string SourceRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public ArchiveTask AddTask(string fileName, string requiredPassword)
            {
                Directory.CreateDirectory(SourceRoot);

                string path = Path.Combine(SourceRoot, fileName);

                File.WriteAllText(path, "not a real archive - the engine is faked in these tests", new UTF8Encoding(false));

                Engine.RequiredPasswords[fileName] = requiredPassword;

                var task = new ArchiveTask(path, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    IsSelected = true
                };

                Vm.Tasks.Add(task);
                return task;
            }

            public async Task RunAsync()
            {
                await Coordinator.StartExtractAsync();

                await WaitUntilAsync(() => !Vm.IsBusy, TimeSpan.FromSeconds(120));
            }
        }

        private Harness CreateHarness(string runName, Action<AppSettings>? configure = null)
        {
            string runRoot = Path.Combine(_root, runName);
            string dataRoot = Path.Combine(runRoot, "data");
            string outputRoot = Path.Combine(runRoot, "out");
            string sourceRoot = Path.Combine(runRoot, "src");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(sourceRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.MaxParallelExtractCount = 1;
            settings.MaxPasswordAttemptsPerLayer = 10;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new ReuseFakeEngine();
            var passwordService = new PasswordService { DataRootDirectory = dataRoot, RememberPasswordList = false };
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

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new DialogService());

            return new Harness(vm, engine, coordinator, passwordService, logService, sourceRoot);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail($"等待条件超时（{timeout.TotalSeconds:F0} 秒）");
        }

        /// <summary>
        /// 假引擎：每个包只认自己那条密码，密码不对就报 <c>WrongPassword</c>（驱动候选循环往下试），
        /// 对了一次就把内容写进引擎输出目录。每一次尝试都记下来（"被问了几次"的判据）。
        /// </summary>
        private sealed class ReuseFakeEngine : IArchiveEngine
        {
            private readonly List<(string Archive, string Password)> _attempts = new();

            /// <summary>包名 → 它能解开的密码。</summary>
            public Dictionary<string, string> RequiredPasswords { get; } = new(StringComparer.OrdinalIgnoreCase);

            public string Id => "reuse-fake";

            public string DisplayName => "密码复用用例假引擎";

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

            /// <summary>某个包被问过的密码序列（按先后）。</summary>
            public List<string> AttemptsFor(string fileName)
            {
                lock (_attempts)
                {
                    return _attempts
                        .Where(item => string.Equals(item.Archive, fileName, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.Password)
                        .ToList();
                }
            }

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                string fileName = Path.GetFileName(request.ArchivePath ?? string.Empty);

                // 列目录也认密码（真 7z 对 -mhe 的包就是这样）：密码不对时列出不来，
                // 预检那一步会照旧失败，不影响候选循环。
                if (!IsCorrect(fileName, request.Password))
                {
                    return Task.FromResult(new ArchiveListResult
                    {
                        Success = false,
                        Message = "密码错误",
                        ErrorType = "WrongPassword"
                    });
                }

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 1,
                    Entries = new List<ArchiveEntry> { new() { Path = fileName + ".txt", Size = 1 } },
                    EngineId = Id,
                    EngineVersion = Version
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Succeeded());
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string fileName = Path.GetFileName(request.ArchivePath ?? string.Empty);
                string password = request.Password ?? string.Empty;

                lock (_attempts)
                {
                    _attempts.Add((fileName, password));
                }

                if (!IsCorrect(fileName, password))
                {
                    return Task.FromResult(new ArchiveOperationResult
                    {
                        Success = false,
                        ExitCode = 2,
                        Status = StatusText.WrongPassword,
                        Message = "密码错误",
                        DetectedErrorType = "WrongPassword"
                    });
                }

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, fileName + ".txt"), "x");
                }

                return Task.FromResult(Succeeded());
            }

            private bool IsCorrect(string fileName, string? password)
            {
                return RequiredPasswords.TryGetValue(fileName, out string? required)
                       && string.Equals(required, password ?? string.Empty, StringComparison.Ordinal);
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
        }
    }
}
