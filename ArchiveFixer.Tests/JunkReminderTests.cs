using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    /// 「解压前的提醒（无用物 / 无可用密码）」的专项测试（用户 2026-09-22 需求第 8 条）。
    ///
    /// <para>这一组要钉住的是四件事：</para>
    /// <list type="number">
    /// <item><description><b>判据窄</b>：伪装成 <c>.jpg</c> 的真压缩包（含"前面是视频、尾部藏着 ZIP"的双面文件）、
    /// 本批自己的源包与分卷**都不算无用物**；只有打包者常放的那几类文件名才算；</description></item>
    /// <item><description><b>上限与"还有 N 个"</b>：最多列 10 条，多出来的只报个数；</description></item>
    /// <item><description><b>B 段判据</b>：<c>IsEncrypted</c> 且**一个非空候选都没有**才命中 ——
    /// 密码本里有、或者设了统一密码，都不该被叫成"没有可用密码"；</description></item>
    /// <item><description><b>绝不能拦住无界面宿主</b>：无 UI 宿主下这条提醒按"继续处理"放行，
    /// 真的跑一遍 <c>StartExtractAsync</c> 必须整批照常完成。</description></item>
    /// </list>
    ///
    /// <para>MainViewModel 的构造会写两个进程级静态（7z 路径 / 递归工作区根目录），
    /// 所以与其它管线测试同一组串行，并在装配后立刻还原。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class JunkReminderTests : IDisposable
    {
        private readonly string _root;

        public JunkReminderTests()
        {
            // ⚠ 目录名里**不要**出现 "Reminder"：有一条断言在看设置文件里有没有同名键，
            // 而这个路径会被写进 appsettings.json（缓存根 / 输出目录），会造成假阳性。
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerJunkScan", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 伪装包不算无用物

        [Fact]
        public async Task 伪装成图片的真压缩包_不会被当成无用物()
        {
            /*
             * 这正是本程序最拿手的场景：后缀写着 .jpg，里面其实是压缩包。
             * 把它叫成"无用物"等于劝用户把真正要处理的包删掉 —— 判据必须靠魔数把它排除掉。
             *
             * 两个形态都要覆盖：
             * · 文件头就是 ZIP（改名的真包）；
             * · 文件头是 MP4、尾部藏着 ZIP（双面文件，只读文件头永远看不出来）。
             */
            Harness harness = CreateHarness("run");

            string junk = CreateTextFile(harness, "说明.txt");
            string disguised = CreateFile(harness, "伪装.jpg", ZipHeaderBytes());
            string doubleFace = CreateFile(harness, "双面.jpg", Mp4WithEmbeddedZipBytes());

            ArchiveTask task = AddTask(harness, CreateSourceFile(harness, "包.7z"));

            // 三个名字都"看起来像"无用物 —— 否则这个用例什么都没证明。
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("说明.txt"));
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("伪装.jpg"));
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("双面.jpg"));

            // 两个伪装文件都能被现有识别器认出来（魔数 / 尾部内嵌归档）。
            var prober = new MagicArchiveProber();
            Assert.True(await prober.IsArchiveAsync(disguised), "文件头是 ZIP 的伪装包必须被认出来");
            Assert.True(await prober.IsArchiveAsync(doubleFace), "尾部藏 ZIP 的双面文件必须被认出来");

            SourceJunkScanResult result = await SourceJunkScanner.ScanAsync(
                new[] { task },
                prober,
                CancellationToken.None);

            string only = Assert.Single(result.Items).FileName;

            Assert.Equal(Path.GetFileName(junk), only);
            Assert.Equal(1, result.TotalCount);
            Assert.DoesNotContain(
                result.SampleNames,
                name => name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        }

        // ================================================================ ② 源包与分卷不算无用物

        [Fact]
        public async Task 本批的源包与分卷组成员_不会被当成无用物()
        {
            /*
             * 用户拖进来的包未必叫 .7z：伪装包（"说明.txt"其实是个 ZIP）、后缀被改坏的分卷
             * （"卷组.jpg" + "卷组.jpg.002"）都很常见。
             * 只要它属于本批任何任务（CurrentPath 或 VolumePaths），就绝不能出现在"无用物"清单里 ——
             * 那是用户马上就要解压的东西。
             *
             * 这三个文件的内容都是普通文本（魔数认不出是压缩包），所以这里唯一能救它们的就是
             * "属于本批任务"这条判据：把判据摘掉，这个用例必然红。
             */
            Harness harness = CreateHarness("run");

            string sourceA = CreateTextFile(harness, "包A.txt");
            string volumeFirst = CreateTextFile(harness, "卷组.jpg");
            string volumeSecond = CreateTextFile(harness, "卷组.jpg.002");
            string realJunk = CreateTextFile(harness, "真说明.txt");

            ArchiveTask taskA = AddTask(harness, sourceA);

            ArchiveTask taskB = AddTask(harness, volumeFirst);
            taskB.IsVolumeGroup = true;
            taskB.VolumePaths.Add(volumeFirst);
            taskB.VolumePaths.Add(volumeSecond);

            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("包A.txt"));
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("卷组.jpg"));

            SourceJunkScanResult result = await SourceJunkScanner.ScanAsync(
                new[] { taskA, taskB },
                new MagicArchiveProber(),
                CancellationToken.None);

            Assert.Equal(Path.GetFileName(realJunk), Assert.Single(result.Items).FileName);
            Assert.Equal(1, result.TotalCount);
        }

        // ================================================================ ③ 上限与"还有 N 个"

        [Fact]
        public async Task 说明与网址与工具算无用物_超过上限只报还有N个()
        {
            /*
             * ① 判据窄：只有 .txt / .url / .exe 这些"打包者常放的那几类"才算，
             *    .mp4 / .zip / .pdf 一个都不报（它们既可能是诱饵也可能是用户真正要的东西）。
             * ② 上限：最多列 10 条，多出来的写"还有 N 个"，而且那句话必须真的出现在弹窗正文里。
             * ③ 「先不处理」= 这一批不开始（任务、源包、输出目录一个字节都没动）。
             */
            Harness harness = CreateHarness("run");

            CreateTextFile(harness, "a说明.txt");
            CreateTextFile(harness, "b网址.url");
            CreateTextFile(harness, "c工具.exe");

            for (int i = 1; i <= 12; i++)
            {
                CreateTextFile(harness, $"d{i:00}.txt");
            }

            // 不在判据名单里的：一个都不许报。
            CreateTextFile(harness, "影片.mp4");
            CreateTextFile(harness, "资源.zip");
            CreateTextFile(harness, "手册.pdf");

            ArchiveTask task = AddTask(harness, CreateSourceFile(harness, "包.7z"));

            SourceJunkScanResult scanned = await SourceJunkScanner.ScanAsync(
                new[] { task },
                new MagicArchiveProber(),
                CancellationToken.None);

            Assert.Equal(SourceJunkScanner.MaxReportedItems, scanned.Items.Count);
            Assert.Equal(5, scanned.ExtraCount);
            Assert.Equal(15, scanned.TotalCount);
            Assert.False(scanned.Truncated);
            Assert.DoesNotContain(
                scanned.Items,
                item => item.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                        item.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        item.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

            string? message = null;
            harness.Coordinator.ReminderAnswerOverride = text =>
            {
                message = text;
                return new ExtractionCoordinator.ReminderAnswer { Confirmed = false };
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.NotNull(message);

            // 数量 + 前几条名字 + "还有 N 个"。
            Assert.Contains("这次一共认出 15 个", message!, StringComparison.Ordinal);
            Assert.Contains(".url", message!, StringComparison.Ordinal);
            Assert.Contains(".exe", message!, StringComparison.Ordinal);
            Assert.Contains("还有 5 个", message!, StringComparison.Ordinal);

            // 用户要求写清的三件事：这些是什么 / 本程序不动它们 / 解压完自己判断要不要删。
            Assert.Contains("打包者附带", message!, StringComparison.Ordinal);
            Assert.Contains("一个都不会动", message!, StringComparison.Ordinal);
            Assert.Contains("解压完成后你可以自己看", message!, StringComparison.Ordinal);

            // 日志里也要有结论（数量 + 前几条名字）。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("解压前提醒：① 无用物 15 个（例如", StringComparison.Ordinal));

            // 「先不处理」= 这一批没有开始。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains(StatusText.JunkReminderDeclinedLog, StringComparison.Ordinal));

            Assert.DoesNotContain(
                harness.Log.Logs,
                item => item.Message.Contains("开始批量解压", StringComparison.Ordinal));

            Assert.Equal(StatusText.Recognized, task.Status);
        }

        // ================================================================ ④ B 段判据

        [Fact]
        public async Task 密码段_加密包没有可用候选才算命中()
        {
            /*
             * 判据 = IsEncrypted 且"算出来的候选里**一个非空的都没有**"（那一条空密码是兜底，不算）。
             *
             * 三个现场：
             * ① 甲：加密、密码本为空、没统一密码、没单任务密码 → **命中**；丙不加密、丁有单任务密码 → 不命中；
             * ② 密码本（映射式）里有这个包的密码 → 不命中，连提醒都不弹；
             * ③ 设了统一密码 → 不命中，同样不弹。
             *
             * 为什么不把"有候选的包"和"没候选的包"放进同一批：密码本是**整批共用**的，
             * 一条条目对全批都算候选 —— 同批里放一个"有密码的包"会把甲也一起覆盖掉，
             * 那就测不出"没有候选才命中"了。所以按"批"分开造现场。
             */
            Harness harness = CreateHarness("run1");

            harness.Vm.Settings.UseGlobalPasswordForAllTasks = false;

            ArchiveTask noCandidate = AddEncryptedTask(harness, "需要密码_甲.7z");
            ArchiveTask plain = AddTask(harness, CreateSourceFile(harness, "不加密_丙.7z"));
            ArchiveTask coveredByTaskPassword = AddEncryptedTask(harness, "需要密码_丁.7z");

            coveredByTaskPassword.Password = TaskPasswordSample;

            Assert.False(plain.IsEncrypted);

            // 纯判据（不经过管线）：空密码那一条是 GetPasswordCandidates 的兜底，不算"可用候选"。
            Assert.False(SourceJunkScanner.HasUsablePasswordCandidate(
                new[] { new PasswordItem { Value = string.Empty } }));
            Assert.True(SourceJunkScanner.HasUsablePasswordCandidate(
                new[] { new PasswordItem { Value = PasswordBookSample } }));
            Assert.False(SourceJunkScanner.HasUsablePasswordCandidate(null));
            Assert.False(SourceJunkScanner.HasUsablePasswordCandidate(Array.Empty<PasswordItem>()));

            string? message = null;
            harness.Coordinator.ReminderAnswerOverride = text =>
            {
                message = text;

                return new ExtractionCoordinator.ReminderAnswer { Confirmed = false };
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.NotNull(message);

            // 命中的那一个被点名，另外两个一个字都不许出现。
            Assert.Contains(noCandidate.FileName, message!, StringComparison.Ordinal);
            Assert.DoesNotContain(plain.FileName, message!, StringComparison.Ordinal);
            Assert.DoesNotContain(coveredByTaskPassword.FileName, message!, StringComparison.Ordinal);

            // 说清继续会发生什么 + 给出两条出路。
            Assert.Contains("密码错误", message!, StringComparison.Ordinal);
            Assert.Contains(StatusText.PasswordAttemptLimitReached, message!, StringComparison.Ordinal);
            Assert.Contains("导入密码本", message!, StringComparison.Ordinal);

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("② 无可用密码的包 1 个（例如 " + noCandidate.FileName, StringComparison.Ordinal));

            // ② 密码本里有（映射式命中）→ 这一段整个不成立，连提醒都不该弹（不弹空对话框）。
            Harness byBook = CreateHarness("run2");

            string bookPath = Path.Combine(byBook.DataRoot, "password-book.txt");

            File.WriteAllText(
                bookPath,
                "需要密码_乙:" + PasswordBookSample,
                new UTF8Encoding(false));

            byBook.Passwords.ImportPasswordList(bookPath);

            AddEncryptedTask(byBook, "需要密码_乙.7z");
            WireSuccessfulExtraction(byBook, "payload.mp4", "内容物");

            string? bookMessage = null;
            byBook.Coordinator.ReminderAnswerOverride = text =>
            {
                bookMessage = text;

                return new ExtractionCoordinator.ReminderAnswer { Confirmed = false };
            };

            await byBook.Coordinator.StartExtractAsync();

            Assert.Null(bookMessage);
            Assert.Contains(
                byBook.Log.Logs,
                item => item.Message.Contains("开始批量解压", StringComparison.Ordinal));

            // ③ 统一密码 → 同样不命中。
            Harness byGlobal = CreateHarness("run3");

            byGlobal.Vm.Settings.UseGlobalPasswordForAllTasks = true;
            byGlobal.Vm.GlobalPassword = GlobalPasswordSample;

            AddEncryptedTask(byGlobal, "需要密码_戊.7z");
            WireSuccessfulExtraction(byGlobal, "payload.mp4", "内容物");

            string? globalMessage = null;
            byGlobal.Coordinator.ReminderAnswerOverride = text =>
            {
                globalMessage = text;

                return new ExtractionCoordinator.ReminderAnswer { Confirmed = false };
            };

            await byGlobal.Coordinator.StartExtractAsync();

            Assert.Null(globalMessage);
            Assert.Contains(
                byGlobal.Log.Logs,
                item => item.Message.Contains("开始批量解压", StringComparison.Ordinal));
        }

        // ================================================================ ⑤ 无界面宿主必须照常跑完

        [Fact]
        public async Task 无界面宿主下_提醒按继续放行_整批照常跑完()
        {
            /*
             * 最关键的一条：这套提醒绝不能把无界面宿主（单元测试 / 控制台宿主）里的整批解压拦死。
             * 现场刻意造成"两段都非空"（有 .txt 诱饵 + 有一个没密码的加密包），
             * 然后真的跑一遍 StartExtractAsync —— 任务必须成功，而且不许尝试弹窗。
             */
            Harness harness = CreateHarness("run");

            CreateTextFile(harness, "说明.txt");
            CreateTextFile(harness, "网址.url");

            ArchiveTask encrypted = AddEncryptedTask(harness, "需要密码.7z");
            ArchiveTask plain = AddTask(harness, CreateSourceFile(harness, "普通包.7z"));

            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, encrypted.Status);
            Assert.Equal(StatusText.ExtractSuccess, plain.Status);

            // 两段结论都进了日志（数量 + 前几条名字）。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("解压前提醒：① 无用物 2 个（例如", StringComparison.Ordinal) &&
                        item.Message.Contains("② 无可用密码的包 1 个", StringComparison.Ordinal));

            // 无界面宿主的降级说明也要留痕（"为什么没人点过却照常跑了"）。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains(StatusText.JunkReminderNoHostLog, StringComparison.Ordinal));

            // 而且**连弹窗都没尝试**：DialogService 里不该留下这条提醒的降级记录。
            Assert.DoesNotContain(
                DialogService.FallbackLog,
                entry => entry.Contains("ShowReminderConfirm", StringComparison.Ordinal));

            // 整批真的跑完了（有产物落地）。
            Assert.True(File.Exists(Path.Combine(plain.OutputPath, "payload.mp4")));
        }

        // ================================================================ ⑥ "本次运行不再提示"

        [Fact]
        public async Task 本次运行不再提示_只影响本次运行()
        {
            /*
             * 勾"本次运行不再提示这类提醒"之后：
             * ① 同一次运行里的**后续批次**不再弹（这里用一个调用计数证明弹窗那段根本没走到）；
             * ② 设置文件**一个字节都没变**（它只是一个内存字段，不是设置项）；
             * ③ **新的一次运行**（新的 MainViewModel + 协调器）照常再提醒一次。
             */
            Harness first = CreateHarness("run1");

            CreateTextFile(first, "说明.txt");

            ArchiveTask task = AddTask(first, CreateSourceFile(first, "包.7z"));
            WireSuccessfulExtraction(first, "payload.mp4", "内容物");

            int calls = 0;
            first.Coordinator.ReminderAnswerOverride = _ =>
            {
                calls++;

                return new ExtractionCoordinator.ReminderAnswer { Confirmed = true, OptionChecked = true };
            };

            string settingsPath = Path.Combine(first.DataRoot, "appsettings.json");
            string before = File.ReadAllText(settingsPath);

            await first.Coordinator.StartExtractAsync();

            Assert.Equal(1, calls);
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Contains(
                first.Log.Logs,
                item => item.Message.Contains("本次运行内不再弹这个提醒", StringComparison.Ordinal));

            // 同一次运行的下一批：不再弹（计数不变），但整批照跑。
            await first.Coordinator.StartExtractAsync();

            Assert.Equal(1, calls);

            // "只在本次运行内记忆"：设置文件一个字节都没变，也没有任何相关键。
            Assert.Equal(before, File.ReadAllText(settingsPath));
            Assert.DoesNotContain("不再提示", before, StringComparison.Ordinal);
            Assert.DoesNotContain("Reminder", before, StringComparison.Ordinal);

            // 新的一次运行：照常提醒。
            Harness second = CreateHarness("run2");

            CreateTextFile(second, "说明.txt");
            AddTask(second, CreateSourceFile(second, "包.7z"));
            WireSuccessfulExtraction(second, "payload.mp4", "内容物");

            int secondCalls = 0;
            second.Coordinator.ReminderAnswerOverride = _ =>
            {
                secondCalls++;

                return new ExtractionCoordinator.ReminderAnswer { Confirmed = true };
            };

            await second.Coordinator.StartExtractAsync();

            Assert.Equal(1, secondCalls);
        }

        // ================================================================ 装配

        /// <summary>测试用的密码本条目（占位符，见 AGENTS.md §8：仓库里不出现真实密码）。</summary>
        private const string PasswordBookSample = "<示例密码>";

        private const string TaskPasswordSample = "<示例单任务密码>";

        private const string GlobalPasswordSample = "<示例统一密码>";

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                LogService log,
                PasswordService passwords,
                string dataRoot,
                string sourceDirectory)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
                Passwords = passwords;
                DataRoot = dataRoot;
                SourceDirectory = sourceDirectory;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public PasswordService Passwords { get; }

            public string DataRoot { get; }

            public string SourceDirectory { get; }
        }

        private Harness CreateHarness(string runName)
        {
            string runRoot = Path.Combine(_root, runName);
            string dataRoot = Path.Combine(runRoot, "data");
            string outputRoot = Path.Combine(runRoot, "out");
            string sourceDirectory = Path.Combine(runRoot, "src");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(sourceDirectory);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原（与其它管线测试同一套）。
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

            return new Harness(vm, engine, coordinator, logService, passwordService, dataRoot, sourceDirectory);
        }

        private static ArchiveTask AddTask(Harness harness, string sourcePath)
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

        private static ArchiveTask AddEncryptedTask(Harness harness, string fileName)
        {
            ArchiveTask task = AddTask(harness, CreateSourceFile(harness, fileName));

            task.IsEncrypted = true;
            task.PasswordStatus = StatusText.PasswordNeed;

            return task;
        }

        private static string CreateSourceFile(Harness harness, string fileName)
        {
            string path = Path.Combine(harness.SourceDirectory, fileName);

            // 引擎是假的，内容是什么都无所谓；关键是它"魔数认不出是压缩包"。
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");

            return path;
        }

        private static string CreateTextFile(Harness harness, string fileName) =>
            CreateFile(harness, fileName, Encoding.UTF8.GetBytes("打包者附带的说明 / 网址（测试用）"));

        private static string CreateFile(Harness harness, string fileName, byte[] content)
        {
            string path = Path.Combine(harness.SourceDirectory, fileName);
            File.WriteAllBytes(path, content);

            return path;
        }

        /// <summary>一个"文件头就是 ZIP"的伪装包（改名的真包）。</summary>
        private static byte[] ZipHeaderBytes()
        {
            var bytes = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };

            while (bytes.Count < 64)
            {
                bytes.Add(0x00);
            }

            return bytes.ToArray();
        }

        /// <summary>
        /// 一个"前面是 MP4、尾部藏着完整 ZIP"的双面文件（只读文件头永远看不出来）。
        ///
        /// 字节布局照着 <c>EmbeddedArchiveDetector</c> 的自洽性校验搭：
        /// 尾部 EOCD 里声明的中央目录偏移 = 真实位置 − ZIP 起点，于是它算出的 delta 正好等于前置数据长度。
        /// </summary>
        private static byte[] Mp4WithEmbeddedZipBytes()
        {
            var bytes = new List<byte>();

            bytes.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x20 });
            bytes.AddRange(Encoding.ASCII.GetBytes("ftypisom"));

            while (bytes.Count < 64)
            {
                bytes.Add(0x00);
            }

            int zipStart = bytes.Count;

            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x03, 0x04 });

            while (bytes.Count < zipStart + 30)
            {
                bytes.Add(0x00);
            }

            int centralDirectory = bytes.Count;

            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x01, 0x02 });

            while (bytes.Count < centralDirectory + 14)
            {
                bytes.Add(0x00);
            }

            const ushort entryCount = 1;
            const uint centralDirectorySize = 14;
            uint declaredOffset = (uint)(centralDirectory - zipStart);

            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            bytes.AddRange(BitConverter.GetBytes((ushort)0));
            bytes.AddRange(BitConverter.GetBytes((ushort)0));
            bytes.AddRange(BitConverter.GetBytes(entryCount));
            bytes.AddRange(BitConverter.GetBytes(entryCount));
            bytes.AddRange(BitConverter.GetBytes(centralDirectorySize));
            bytes.AddRange(BitConverter.GetBytes(declaredOffset));
            bytes.AddRange(BitConverter.GetBytes((ushort)0));

            return bytes.ToArray();
        }

        private static void WireSuccessfulExtraction(Harness harness, string contentFileName, string content)
        {
            int size = Encoding.UTF8.GetByteCount(content);

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(contentFileName, size));

            harness.Engine.OnExtractAsync = request =>
            {
                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllText(Path.Combine(request.OutputPath!, contentFileName), content);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.ExtractSuccess,
                    Message = "解压成功",
                    DetectedErrorType = "None"
                });
            };
        }

        private static ArchiveListResult ListResult(string fileName, int size)
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = size,
                Entries = new List<ArchiveEntry> { new() { Path = fileName, Size = size } },
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }

        /// <summary>可控的假引擎（与其它管线测试同形）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

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
                    : Task.FromResult(ListResult("payload.bin", 1));
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = StatusText.TestPassed,
                    DetectedErrorType = "None"
                });
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                return OnExtractAsync != null
                    ? OnExtractAsync(request)
                    : Task.FromResult(new ArchiveOperationResult
                    {
                        Success = true,
                        Status = StatusText.ExtractSuccess,
                        DetectedErrorType = "None"
                    });
            }
        }
    }
}
