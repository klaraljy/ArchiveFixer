using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-26 第 1 条（原话）：
    /// "你这个保存设置不是所有东西都保存的，而且我刚刚测试当点击并发操作的时候，这个开关就没有保存，
    /// 我现在是想他们一个要自动保存……而且要有记忆性，下次重启也要有，这点要非常重视"。
    ///
    /// <para>这一组钉的就是那句话：<b>改任何一项都不需要点任何按钮，而且下次启动还在</b> ——
    /// 包括他点名的「全速」开关，包括「取消 / 关闭窗口」那一下没等到定时器的改动。</para>
    ///
    /// <para>这一组要构造真的 <see cref="MainViewModel"/>（构造会写两个进程级静态），
    /// 所以声明成"不与其他集合并行"——与其它管线测试同一套理由。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SettingsAutoSaveTests : IDisposable
    {
        private readonly string _root = CreateIsolatedRoot();

        public SettingsAutoSaveTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响结论。
            }
        }

        // ================================================================
        // 用户 2026-09-26："我现在是想他们一个要自动保存……而且要有记忆性，下次重启也要有"
        // ================================================================

        [Fact]
        public void 改了设置_不点任何按钮也会落盘并跨重启恢复()
        {
            Harness harness = CreateHarness("one");

            harness.Vm.Settings.UseSpecialExtraction = true;

            Assert.True(harness.Vm.AutoSaveSettingsIfChanged(), "改过之后这一跳必须落盘");

            // 跨重启：重新读一遍设置文件（另一份 SettingsService 实例，与真实启动同一条路）。
            var reloaded = new SettingsService(harness.PathService).Load();
            Assert.True(reloaded.UseSpecialExtraction, "重启后必须还在");

            Harness second = CreateHarness("one", reuseRoot: true);
            Assert.True(second.Vm.Settings.UseSpecialExtraction, "新建的主视图模型读到的也必须还是它");
        }

        [Fact]
        public void 全速开关也记得住_重启后还是勾着的()
        {
            // 他点名的那一个："我刚刚测试当点击并发操作的时候，这个开关就没有保存"。
            Harness harness = CreateHarness("fullspeed");

            harness.Vm.RunAtFullSpeed = true;

            Assert.True(harness.Vm.AutoSaveSettingsIfChanged());

            Assert.True(new SettingsService(harness.PathService).Load().RunAtFullSpeed, "appsettings.json 里必须是 true");

            Harness second = CreateHarness("fullspeed", reuseRoot: true);
            Assert.True(second.Vm.RunAtFullSpeed, "重启后「全速」必须还是勾着的");
        }

        [Fact]
        public void 没有改动时_一跳也不写盘()
        {
            Harness harness = CreateHarness("idle");
            DateTime afterStartup = File.GetLastWriteTimeUtc(harness.SettingsFilePath);

            /*
             * 自动保存不许变成"每 800ms 写一次文件"：启动本身（编辑器和打包页在构造时补齐默认档）
             * 也不许写 —— 指纹基线是在**构造末尾**取的（见 MainViewModel 构造函数最后那一段）。
             */
            for (int i = 0; i < 10; i++)
            {
                Assert.False(harness.Vm.AutoSaveSettingsIfChanged(), "没改任何东西时不该写");
            }

            Assert.Equal(afterStartup, File.GetLastWriteTimeUtc(harness.SettingsFilePath));

            // 改一项 → 立刻存；之后十跳 → 又安静下来。
            harness.Vm.Settings.MaxParallelExtractCount = 6;
            Assert.True(harness.Vm.AutoSaveSettingsIfChanged());

            DateTime afterChange = File.GetLastWriteTimeUtc(harness.SettingsFilePath);

            for (int i = 0; i < 10; i++)
            {
                Assert.False(harness.Vm.AutoSaveSettingsIfChanged());
            }

            Assert.Equal(afterChange, File.GetLastWriteTimeUtc(harness.SettingsFilePath));
        }

        /// <summary>
        /// 自动保存这条路：**非法的工具路径绝不会落到盘上**。
        ///
        /// <para>判据的唯一出口是 <see cref="AppSettings.Normalize"/> —— 它把"文件不存在"的工具路径**清空**
        /// （`SettingsService.Serialize` 是唯一序列化出口，Normalize 就在它里面）。所以自动保存这里
        /// 写下去的永远是清空后的值：用户不会在 `appsettings.json` 里留一个失效路径、下次启动拿着它去找引擎。</para>
        ///
        /// <para>⚠ 2026-09-30：这条用例原来断言的是"被 <c>DescribeAutoSaveBlock</c> 拦下 + 日志 WARN"，
        /// 那个非法值是**「缓存根目录填 C 盘」** —— 设置项删除之后，自动保存这条路上已经没有
        /// "Normalize 清不掉、只能靠拦下"的值了（工具路径都会被 Normalize 清空）。
        /// 所以这里改成钉**真正生效的那条保证**：非法值不落盘、同一份里的其它改动照常存下来。
        /// 「显式保存时被拦下并给出原因」那一条由 <c>WiringClosureTests</c> 用工具路径钉着。</para>
        /// </summary>
        [Fact]
        public void 非法工具路径不落盘_同一份里的其它改动照常存下来()
        {
            Harness harness = CreateHarness("badtoolpath");

            harness.Vm.Settings.MaxParallelExtractCount = 7;
            harness.Vm.Settings.CustomRarExePath = @"C:\af-should-not-be-saved\Rar.exe";

            Assert.True(harness.Vm.AutoSaveSettingsIfChanged(), "同一份里还有合法改动，这一跳必须落盘");

            AppSettings onDisk = new SettingsService(harness.PathService).Load();

            Assert.Equal(7, onDisk.MaxParallelExtractCount);
            Assert.Equal(string.Empty, onDisk.CustomRarExePath ?? string.Empty);

            // 内存里那一份也被 Normalize 清空了 —— 界面读的是同一个对象，不会显示一个已经不生效的路径。
            Assert.Equal(string.Empty, harness.Vm.Settings.CustomRarExePath ?? string.Empty);
        }

        [Fact]
        public void 关窗前的最后一次落盘_把没跳到的改动补上()
        {
            Harness harness = CreateHarness("flush");

            // 模拟"改完立刻关窗"：只调 FlushSettingsAutoSave（真机上由 MainWindow.OnClosed 调）。
            harness.Vm.Settings.VerboseLog = true;
            harness.Vm.FlushSettingsAutoSave();

            Assert.True(new SettingsService(harness.PathService).Load().VerboseLog);
        }

        /// <summary>
        /// 用户 2026-09-25 第 36 条要的"不许静默改掉你填的数字"：Normalize 会把超范围的值夹回合法区间，
        /// 夹过就必须**说清**。
        ///
        /// <para>⚠ 2026-09-26 的同步审计逮到：保存改成自动、那个框删掉之后，这条承诺**没人执行了** ——
        /// 用户填 8192、实际生效 4096，界面上一个字都没有（人工测试清单 C19 正是照这条承诺写的）。
        /// 补回到底栏那一行；⚠ 同日他又要求"底栏那一行彻底删除"（"这个鬼东西太突兀了"）→
        /// 现在**唯一出口是日志**（一条 WARN，含被夹回后的真实值）。</para>
        /// </summary>
        [Fact]
        public void 填超范围的数字被夹回_日志必须说清()
        {
            Harness harness = CreateHarness("clamp");

            harness.Vm.Settings.MaxSingleExtractedFileGiB = 8192;

            Assert.True(harness.Vm.AutoSaveSettingsIfChanged(), "夹回之后这一跳仍然要落盘（存的是合法值）");

            // ① 文件里是夹回后的值（不是用户填的 8192）。
            Assert.Equal(
                AppSettings.MaxExtractionCapGiB,
                new SettingsService(harness.PathService).Load().MaxSingleExtractedFileGiB);

            // ② 日志里必须说清"有一项被夹回了"，而且要说到是哪个上限、夹成了多少。
            Assert.Contains(
                harness.Logs,
                line => line.Contains("超出允许范围", StringComparison.Ordinal)
                        && line.Contains("安全上限", StringComparison.Ordinal)
                        && line.Contains(
                            AppSettings.MaxExtractionCapGiB.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            StringComparison.Ordinal));

            // 反向：没超范围时不许乱说"被夹回"。
            Harness normal = CreateHarness("clamp-ok");

            normal.Vm.Settings.MaxSingleExtractedFileGiB = 128;
            Assert.True(normal.Vm.AutoSaveSettingsIfChanged());
            Assert.DoesNotContain(normal.Logs, line => line.Contains("超出允许范围", StringComparison.Ordinal));
        }

        /// <summary>
        /// ⑥页「恢复默认设置」换的是**一份新的 AppSettings 对象** —— ⑤打包页那只必须跟着换过去，
        /// 否则它之后写的是"已经没人读的死对象"：日志说"已写进设置"，重启却全回到默认。
        ///
        /// <para>（2026-09-26 同步审计逮到的真缺陷：`Settings` 的 setter 只重挂了 SettingsEditor，
        /// 漏了 PackingEditor。）</para>
        /// </summary>
        [Fact]
        public void 恢复默认设置之后_打包页也要换成新的那一份设置()
        {
            Harness harness = CreateHarness("reset");

            Assert.Same(harness.Vm.Settings, harness.Vm.PackingEditor.Settings);

            AppSettings before = harness.Vm.Settings;

            harness.Vm.Settings = new SettingsService(harness.PathService).ResetToDefault();

            Assert.NotSame(before, harness.Vm.Settings);
            Assert.Same(harness.Vm.Settings, harness.Vm.PackingEditor.Settings);

            // ⑤页改一档 → 写进的是**新的**那一份（不是被换掉的那只死对象）。
            harness.Vm.PackingEditor.PlacementIsCustom = true;

            Assert.Equal("Custom", harness.Vm.Settings.PackTargetMode);
            Assert.Equal("Local", before.PackTargetMode);
        }

        /// <summary>
        /// 换设置对象之后，"指定位置"那一格显示的文字也要跟着换新对象（真机逮到）：
        /// `LoadPlacementFromSettings` 只改了字段、没通知属性 —— 于是⑥页恢复默认之后
        /// ⑤页那个灰着的框里**还挂着上一次的路径**，与"现在没有指定位置"正好相反。
        /// </summary>
        [Fact]
        public void 换设置对象之后_打包页那一格目录也要跟着换()
        {
            Harness harness = CreateHarness("pack-dir");

            harness.Vm.Settings.PackTargetMode = "Custom";
            harness.Vm.Settings.PackCustomOutputDirectory = @"D:\旧的出包";
            harness.Vm.PackingEditor.AttachSharedSettings(harness.Vm.Settings);

            Assert.Equal(@"D:\旧的出包", harness.Vm.PackingEditor.CustomPlacementDirectory);

            var raised = new List<string>();
            harness.Vm.PackingEditor.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

            AppSettings fresh = AppSettings.CreateDefault();
            fresh.PackTargetMode = "Local";

            harness.Vm.PackingEditor.AttachSharedSettings(fresh);

            Assert.Equal(string.Empty, harness.Vm.PackingEditor.CustomPlacementDirectory);
            Assert.Contains(nameof(harness.Vm.PackingEditor.CustomPlacementDirectory), raised);
            Assert.False(harness.Vm.PackingEditor.PlacementIsCustom);
        }

        /// <summary>
        /// 弹窗里勾了「以后不再询问」之后，②页那个同义开关必须跟着刷新
        /// （值写对了、界面显示旧值 = 用户读成"没生效"）。判据是**通知**（SettingsViewModel 里那张清单），不是值。
        ///
        /// <para>⚠ 2026-10-04：原来这里还有半条「③页的导入后提醒同理」，随那一格一起删掉了 ——
        /// 那格已按用户口径从界面上撤掉（那是底层自动做的事），写盘入口
        /// <c>MainViewModel.SaveRemindJunkAfterImport</c> 也一并删除，设置属性本身留着（旧键安静忽略）。</para>
        /// </summary>
        [Fact]
        public void 弹窗里勾了以后不再询问_解压方式页那个开关要跟着刷新()
        {
            Harness harness = CreateHarness("notify");
            var raised = new List<string>();

            harness.Vm.SettingsEditor.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

            harness.Vm.SaveSkipOneClickConfirm(true);

            Assert.Contains(nameof(harness.Vm.SettingsEditor.Settings), raised);
        }

        /*
         * ⛔ 2026-09-30：这里原来有一条「数据根跟着缓存根目录走时_启动读的是缓存根那一份」，
         * 随 `AppSettings.CacheRootDirectory` 设置项一起删除 ——
         * 数据根固定 = 程序目录下的 data（唯一出口 PathService.DefaultDataRootDirectory），
         * 设置文件只可能有一处，"启动读旧的、保存写新的"这个缺陷从结构上就不存在了。
         */

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(MainViewModel vm, PathService pathService, string dataRoot, LogService log)
            {
                Vm = vm;
                PathService = pathService;
                DataRoot = dataRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public PathService PathService { get; }

            public string DataRoot { get; }

            public LogService Log { get; }

            /// <summary>屏幕日志的正文（判"界面上不再提示、日志里必须说清"要用它）。</summary>
            public IEnumerable<string> Logs => Log.Logs.Select(item => item.DisplayText);

            public string SettingsFilePath => PathService.SettingsFilePath;
        }

        private Harness CreateHarness(string name, bool reuseRoot = false)
        {
            string runRoot = Path.Combine(_root, name);
            string dataRoot = Path.Combine(runRoot, "data");
            string outputRoot = Path.Combine(runRoot, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            if (!reuseRoot)
            {
                AppSettings settings = AppSettings.CreateDefault();

                /*
                 * 数据根 = 这个用例自己的数据目录（与其它管线测试同一套写法：显式塞进 PathService）。
                 * ⛔ 2026-09-30 起 MainViewModel **不再**按任何设置项去改数据根
                 * （原来那句 `settings.CacheRootDirectory = dataRoot` 就是为它写的，已随设置项删除），
                 * 所以这份 settings 里不再需要"指路"的字段 —— PathService 上那一句就是唯一出口。
                 */
                settings.CustomOutputDirectory = outputRoot;
                settings.AutoScanAfterDrop = false;
                settingsService.Save(settings);
            }

            return new Harness(
                BuildViewModel(settingsService, pathService, dataRoot, out LogService log),
                pathService,
                dataRoot,
                log);
        }

        /// <summary>
        /// 构造真的 <see cref="MainViewModel"/>。构造会顺手写两个进程级静态：先存后还原
        /// （与其它管线测试同一套）。
        /// </summary>
        private static MainViewModel BuildViewModel(
            SettingsService settingsService,
            PathService pathService,
            string dataRoot,
            out LogService log)
        {
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            log = new LogService(pathService);

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new SevenZipEngine(),
                new PasswordService { DataRootDirectory = dataRoot },
                log,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            return vm;
        }

        /// <summary>
        /// 隔离根：优先用**测试程序集所在那块盘**，其次任意一块能写的固定盘，都没有才退回 %TEMP%。
        ///
        /// <para>⚠ 2026-09-30：这条偏好原来是被"缓存根不许放 C 盘"那条设置校验逼出来的（用 %TEMP%
        /// 会让每个自动保存用例被拦下）；那个设置项与那条校验都已删除。这里**保留**这套偏好 ——
        /// 它现在只是"别把大批临时文件堆到系统盘"的工程习惯，与任何校验无关。</para>
        /// </summary>
        private static string CreateIsolatedRoot()
        {
            foreach (string baseDirectory in CandidateBaseDirectories())
            {
                try
                {
                    string parent = Path.Combine(baseDirectory, "ArchiveFixer-tests", "settings-autosave");
                    Directory.CreateDirectory(parent);
                    return Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
                }
                catch
                {
                    // 这块盘不能写就换下一块。
                }
            }

            return Path.Combine(Path.GetTempPath(), "af-settings-autosave-" + Guid.NewGuid().ToString("N"));
        }

        private static IEnumerable<string> CandidateBaseDirectories()
        {
            string assemblyDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            string? assemblyRoot = Path.GetPathRoot(assemblyDirectory);

            if (!string.IsNullOrEmpty(assemblyRoot) && !IsSystemDrive(assemblyRoot))
            {
                // 首选测试程序集自己所在的那一层（构建产物、不进版本库，而且与程序集同一块盘）。
                yield return assemblyDirectory;
                yield return assemblyRoot;
            }

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                bool usable;

                try
                {
                    usable = drive.IsReady && drive.DriveType == DriveType.Fixed && !IsSystemDrive(drive.Name);
                }
                catch
                {
                    usable = false;
                }

                if (usable && !string.Equals(drive.Name, assemblyRoot, StringComparison.OrdinalIgnoreCase))
                {
                    yield return drive.Name;
                }
            }
        }

        private static bool IsSystemDrive(string root)
        {
            return root.Length > 0 && char.ToUpperInvariant(root[0]) == 'C';
        }
    }
}
