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

        [Fact]
        public void 缓存根目录不合法时_先不落盘并在底栏说清()
        {
            Harness harness = CreateHarness("badcache");
            int before = new SettingsService(harness.PathService).Load().MaxParallelExtractCount;

            harness.Vm.Settings.MaxParallelExtractCount = 7;
            harness.Vm.Settings.CacheRootDirectory = @"C:\af-should-not-be-saved";

            // 校验没过 → 这一整份都先不存（缓存根目录决定数据根与工作区落点，写进去下次启动就照它走）。
            Assert.False(harness.Vm.AutoSaveSettingsIfChanged());

            AppSettings onDisk = new SettingsService(harness.PathService).Load();
            Assert.Equal(before, onDisk.MaxParallelExtractCount);
            Assert.DoesNotContain("af-should-not-be-saved", onDisk.CacheRootDirectory ?? string.Empty, StringComparison.Ordinal);

            // 底栏必须看得见"为什么先没存"（它不是失败，是"改好就会存"）。
            Assert.Contains("没有自动保存", harness.Vm.SettingsAutoSaveNote, StringComparison.Ordinal);

            // 改回合法值 → 立刻存下来（连同刚才那一项）。
            harness.Vm.Settings.CacheRootDirectory = harness.DataRoot;
            Assert.True(harness.Vm.AutoSaveSettingsIfChanged());
            Assert.Equal(7, new SettingsService(harness.PathService).Load().MaxParallelExtractCount);
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
        /// 设过「缓存根目录」的人：数据根会跟着缓存根走（见 <c>MainViewModel.ApplyEngineSettings</c>），
        /// 于是设置文件有两个可能的位置 —— 程序目录下的 <c>data</c>（启动时先读的那一份）与缓存根那一份。
        ///
        /// <para>用户 2026-09-26 第 1 条要的"下次重启也要有"在这里最容易漏：启动读旧的、保存写新的，
        /// 改什么都记不住。<b>以缓存根那一份为准</b>才对。</para>
        /// </summary>
        [Fact]
        public void 数据根跟着缓存根目录走时_启动读的是缓存根那一份()
        {
            string runRoot = Path.Combine(_root, "cacheroot");
            string programData = Path.Combine(runRoot, "programdata");
            string cacheRoot = Path.Combine(runRoot, "cacherepo");

            Directory.CreateDirectory(programData);
            Directory.CreateDirectory(cacheRoot);

            var pathService = new PathService { DataRootDirectory = programData };
            var settingsService = new SettingsService(pathService);

            // 程序目录那一份：只负责指出"数据根在哪儿"（缓存根目录 = cacherepo）。
            AppSettings inProgramData = AppSettings.CreateDefault();
            inProgramData.CacheRootDirectory = cacheRoot;
            inProgramData.MaxParallelExtractCount = 2;
            settingsService.Save(inProgramData);

            // 缓存根那一份：真正最新的那份（并发档 = 9）。
            AppSettings inCacheRoot = AppSettings.CreateDefault();
            inCacheRoot.CacheRootDirectory = cacheRoot;
            inCacheRoot.MaxParallelExtractCount = 7;
            new SettingsService(new PathService { DataRootDirectory = cacheRoot }).Save(inCacheRoot);

            MainViewModel vm = BuildViewModel(settingsService, pathService, programData);

            Assert.Equal(7, vm.Settings.MaxParallelExtractCount);
            Assert.Equal(
                Path.Combine(cacheRoot, "appsettings.json"),
                pathService.SettingsFilePath,
                ignoreCase: true);

            // 缓存根位置**还没有**设置文件时：沿用程序目录那一份，⛔ 绝不当场写一份默认值盖掉它。
            string emptyCacheRoot = Path.Combine(runRoot, "emptycache");
            Directory.CreateDirectory(emptyCacheRoot);

            AppSettings pointingToEmpty = AppSettings.CreateDefault();
            pointingToEmpty.CacheRootDirectory = emptyCacheRoot;
            pointingToEmpty.MaxParallelExtractCount = 6;
            new SettingsService(new PathService { DataRootDirectory = programData }).Save(pointingToEmpty);

            MainViewModel second = BuildViewModel(
                new SettingsService(new PathService { DataRootDirectory = programData }),
                new PathService { DataRootDirectory = programData },
                programData);

            Assert.Equal(6, second.Settings.MaxParallelExtractCount);
            Assert.False(
                File.Exists(Path.Combine(emptyCacheRoot, "appsettings.json")),
                "那个位置本来没有设置文件时，启动不许替用户写一份新的");
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(MainViewModel vm, PathService pathService, string dataRoot)
            {
                Vm = vm;
                PathService = pathService;
                DataRoot = dataRoot;
            }

            public MainViewModel Vm { get; }

            public PathService PathService { get; }

            public string DataRoot { get; }

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
                 * ⚠ 缓存根目录 = 这个用例自己的数据目录（与其它管线测试同一套写法）：MainViewModel
                 * 构造时会把**数据根**按它对齐，不这么写的话数据根会被改回程序目录下的 data，
                 * 所有用例就共用同一份 appsettings.json、互相踩（写盘也不是这个用例的那一份）。
                 */
                settings.CacheRootDirectory = dataRoot;
                settings.CustomOutputDirectory = outputRoot;
                settings.AutoScanAfterDrop = false;
                settingsService.Save(settings);
            }

            return new Harness(BuildViewModel(settingsService, pathService, dataRoot), pathService, dataRoot);
        }

        /// <summary>
        /// 构造真的 <see cref="MainViewModel"/>。构造会顺手写两个进程级静态：先存后还原
        /// （与其它管线测试同一套）。
        /// </summary>
        private static MainViewModel BuildViewModel(
            SettingsService settingsService,
            PathService pathService,
            string dataRoot)
        {
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new SevenZipEngine(),
                new PasswordService { DataRootDirectory = dataRoot },
                new LogService(pathService),
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
        /// 隔离根**必须不在 C 盘**：设置层有一条硬校验「缓存根不许放 C 盘」
        /// （<c>SettingsViewModel.ValidateCacheRootDirectory</c>），而 harness 必须把缓存根指到
        /// 用例自己的数据目录（理由见 <see cref="CreateHarness"/>）—— 用 %TEMP%（默认在 C 盘）的话，
        /// 每一个"自动保存"用例都会被那条校验拦下。
        ///
        /// <para>所以优先用**测试程序集所在那块盘**，其次任意一块能写的固定盘，都没有才退回 %TEMP%。</para>
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
