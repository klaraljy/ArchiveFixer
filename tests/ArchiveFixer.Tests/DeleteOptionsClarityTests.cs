using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「删除操作」三档必须**当场看得懂、说得准**（用户 2026-09-25 第 32 条）。
    ///
    /// <para><b>用户原话（他亲自定的形态）</b>：「首先分 1.源包操作，1_原来的位置不动，2_放入其余物当中，
    /// 2.删除操作，1_不动其余物，2_动，但是只删除在回收站的位置，3_动，而且是彻底删除，直接节约空间
    /// （这个就可以字体变红了，就不用再弄其他的没用的注释了）」＋「当用户点击了彻底删除那个选项时
    /// 就会在下面出现一个提示的选项，而且不能消掉，用户选择了就一直放在那」。</para>
    ///
    /// <para>这一组钉四件事：①设置层的默认值与三档归一化；②ViewModel 那三档与"红字提示可见性"；
    /// ③删除裁决的五道门槛里最关键的两条（失败 / 取消 → 一个字节都不删）；
    /// ④界面形态（③页两组单选 + 常驻提示；②页危险模式红区整块消失）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class DeleteOptionsClarityTests : IDisposable
    {
        private readonly string _root;

        public DeleteOptionsClarityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDeleteClarity", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 设置层

        /// <summary>默认档：源包 = 原来的位置不动；删除操作 = 不动其余物（用户原话里的那两个默认）。</summary>
        [Fact]
        public void 两个默认档_都不动()
        {
            AppSettings settings = AppSettings.CreateDefault();

            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), settings.SourceHandling);
            Assert.Equal(RestHandlingModes.Keep, settings.RestHandlingAfterVerify);
        }

        /// <summary>三档都能落盘读回；认不出的值一律回落 Keep（保守方向：宁可不动，绝不误删）。</summary>
        [Fact]
        public void 删除操作三档_落盘读回与非法值归一化()
        {
            foreach (string mode in new[] { RestHandlingModes.Keep, RestHandlingModes.RecycleBin, RestHandlingModes.Delete })
            {
                string dataRoot = Path.Combine(_root, "data-" + mode);
                Directory.CreateDirectory(dataRoot);

                var pathService = new PathService { DataRootDirectory = dataRoot };
                new SettingsService(pathService).Save(new AppSettings { RestHandlingAfterVerify = mode });

                AppSettings reloaded = new SettingsService(new PathService { DataRootDirectory = dataRoot }).Load();

                Assert.Equal(mode, reloaded.RestHandlingAfterVerify);
            }

            Assert.Equal(RestHandlingModes.Keep, RestHandlingModes.Normalize(null));
            Assert.Equal(RestHandlingModes.Keep, RestHandlingModes.Normalize("Nonsense"));
            Assert.Equal(RestHandlingModes.Delete, RestHandlingModes.Normalize("delete"));
        }

        /// <summary>
        /// <b>2026-09-25 第 34 条的真机故障：在③页点单选框，设置一个字节都没写。</b>
        ///
        /// <para>机制：③页「2 删除操作」绑的是 <c>SettingsEditor.RestHandling</c>（**字符串**），
        /// 而 <c>EnumOptionConverter.ConvertBack</c> 老实现只认枚举 —— 目标类型是 string 时直接返回
        /// <c>Binding.DoNothing</c>，于是"有黑点、没写值"。用户看到的是一整条链：
        /// 选「彻底删除」→ 点保存 → 一键处理的弹窗里还是旧档位（"我之前的选项完全没有用"）；
        /// 任何一次绑定重新求值又把黑点弹回旧值。</para>
        ///
        /// <para>这条测试走**与绑定完全同一条路**：转换器 ConvertBack → 把结果写进属性
        /// （等价于 TwoWay 绑定写回源）。⛔ 撤掉 ConvertBack 里那段"字符串档位"的分支，这条立刻变红。</para>
        /// </summary>
        [Fact]
        public void 单选框写回_字符串档位也必须落进设置()
        {
            MainViewModel vm = CreateViewModel();
            var converter = new ArchiveFixer.Helpers.EnumOptionConverter();

            // 用户点③页的「彻底删除」：RadioButton 的 IsChecked 变 true → 转换器往回写。
            foreach (string mode in new[] { RestHandlingModes.Delete, RestHandlingModes.RecycleBin, RestHandlingModes.Keep })
            {
                object? written = converter.ConvertBack(true, typeof(string), mode, System.Globalization.CultureInfo.InvariantCulture);

                Assert.False(
                    ReferenceEquals(written, System.Windows.Data.Binding.DoNothing),
                    $"「{mode}」这一档没有被写回（ConvertBack 返回了 DoNothing）—— 点了单选框等于没点");
                Assert.Equal(mode, written);

                vm.SettingsEditor.RestHandling = (string)written!;

                Assert.Equal(mode, vm.Settings.RestHandlingAfterVerify);
            }

            // 取消勾选（被同组别的项顶掉）时**不许**写回，否则会把刚选中的那一项又改掉。
            Assert.True(
                ReferenceEquals(
                    converter.ConvertBack(false, typeof(string), RestHandlingModes.Delete, System.Globalization.CultureInfo.InvariantCulture),
                    System.Windows.Data.Binding.DoNothing));

            // 枚举档位（源包操作那一组）照旧按枚举写回 —— 两种目标类型都要能用。
            object? enumWritten = converter.ConvertBack(
                true,
                typeof(SourceHandlingMode),
                nameof(SourceHandlingMode.MoveToRest),
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal(SourceHandlingMode.MoveToRest, enumWritten);
        }

        /// <summary>
        /// 通知口径：**源包操作与删除操作都必须在那张"包在 Settings 外面"的通知清单里**。
        ///
        /// <para>真机现场：关掉一键处理的弹窗回到③页，「删除操作」那一组三个单选一个黑点都没有；
        /// 切一次选项卡之后「源包操作」的黑点回来了、删除操作的还是不在 ——
        /// 差别正是老实现的通知清单里**只有 SourceHandling、漏了 RestHandling**
        /// （从 <c>RaiseOutputPlacementChanged</c> 漏掉的直接后果）。</para>
        /// </summary>
        [Fact]
        public void 重新求值时_源包操作与删除操作都要发通知()
        {
            MainViewModel vm = CreateViewModel();

            var raised = new List<string>();

            vm.SettingsEditor.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

            vm.SettingsEditor.NotifyProcessingOptionsChanged();

            Assert.Contains(nameof(vm.SettingsEditor.SourceHandling), raised);
            Assert.Contains(nameof(vm.SettingsEditor.RestHandling), raised);
            Assert.Contains(nameof(vm.SettingsEditor.IsRestDeleteSelected), raised);
            Assert.Contains(nameof(vm.SettingsEditor.RestHandlingSummary), raised);

            // 落点那条路（①页「选择…」/ ③页切页都走它）同样要把这两档带上。
            raised.Clear();
            vm.SettingsEditor.NotifyOutputPlacementChanged();

            Assert.Contains(nameof(vm.SettingsEditor.SourceHandling), raised);
            Assert.Contains(nameof(vm.SettingsEditor.RestHandling), raised);
        }

        // ================================================================ ② ViewModel 那一层

        /// <summary>选了「彻底删除」→ 那条约提示必须可见；换回别的档 → 收起来（没有"关掉"这条出口）。</summary>
        [Fact]
        public void 选彻底删除时_红字提示可见_换成别的档就收起()
        {
            MainViewModel vm = CreateViewModel();

            Assert.False(vm.SettingsEditor.IsRestDeleteSelected);

            vm.SettingsEditor.RestHandling = RestHandlingModes.Delete;

            Assert.True(vm.SettingsEditor.IsRestDeleteSelected);
            Assert.Equal(RestHandlingModes.Delete, vm.Settings.RestHandlingAfterVerify);
            Assert.Equal(StatusText.OneClickConfirmRestAutoDelete, vm.SettingsEditor.RestHandlingSummary);

            vm.SettingsEditor.RestHandling = RestHandlingModes.RecycleBin;

            Assert.False(vm.SettingsEditor.IsRestDeleteSelected);
            Assert.Equal(RestHandlingModes.RecycleBin, vm.Settings.RestHandlingAfterVerify);
            Assert.Equal(StatusText.OneClickConfirmRestRecycle, vm.SettingsEditor.RestHandlingSummary);

            vm.SettingsEditor.RestHandling = RestHandlingModes.Keep;

            Assert.Equal(RestHandlingModes.Keep, vm.Settings.RestHandlingAfterVerify);
            Assert.Equal(StatusText.OneClickConfirmRestKeep, vm.SettingsEditor.RestHandlingSummary);
        }

        /// <summary>「当前档位」那句话必须把源包与其余物两件事都说出来（界面与日志读同一份实现）。</summary>
        [Fact]
        public void 当前档位那句话_把两件事都说清()
        {
            MainViewModel vm = CreateViewModel();

            vm.SettingsEditor.SourceHandling = SourceHandlingMode.MoveToRest;
            vm.SettingsEditor.RestHandling = RestHandlingModes.Delete;
            vm.RefreshSpaceModeText();

            Assert.Contains("源包：移入其余物", vm.SpaceModeText, StringComparison.Ordinal);
            Assert.Contains("彻底删除", vm.SpaceModeText, StringComparison.Ordinal);

            vm.SettingsEditor.SourceHandling = SourceHandlingMode.KeepInPlace;
            vm.SettingsEditor.RestHandling = RestHandlingModes.Keep;
            vm.RefreshSpaceModeText();

            Assert.Contains("源包：留在原地", vm.SpaceModeText, StringComparison.Ordinal);
            Assert.Contains("保留", vm.SpaceModeText, StringComparison.Ordinal);
        }

        // ================================================================ ③ 删除裁决的红线（五道门槛的两条）

        /// <summary>任务不是"完成"（失败 / 取消 / 跳过 / 部分完成）→ 其余物一个字节都不删。</summary>
        [Theory]
        [InlineData(TaskOutcome.Failed)]
        [InlineData(TaskOutcome.Cancelled)]
        [InlineData(TaskOutcome.Skipped)]
        [InlineData(TaskOutcome.PartiallyCompleted)]
        public void 不是完成态_其余物一个字节都不删(TaskOutcome outcome)
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();
            task.Outcome = outcome;

            RestPurgeOutcome result = new RestItemPurger().Purge(task, cancelled: false, DeleteMode.Permanent);

            Assert.False(result.Attempted);
            Assert.True(Directory.Exists(restDirectory), "失败 / 取消 / 跳过 / 部分完成 → 其余物必须原样留着");
            Assert.True(File.Exists(Path.Combine(restDirectory, "222.7z")));
        }

        /// <summary>被取消（用户按了停止）→ 同样一个字节都不删，哪怕机器终态是"完成"。</summary>
        [Fact]
        public void 已取消_其余物一个字节都不删()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            RestPurgeOutcome result = new RestItemPurger().Purge(task, cancelled: true, DeleteMode.Permanent);

            Assert.False(result.Attempted);
            Assert.True(Directory.Exists(restDirectory));
        }

        /// <summary>成功 + 校验通过 → 「彻底删除」真的把其余物删掉，而且不进回收站。</summary>
        [Fact]
        public void 完成态加校验通过_彻底删除真的删掉()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            RestPurgeOutcome result = new RestItemPurger().Purge(task, cancelled: false, DeleteMode.Permanent);

            Assert.True(result.Succeeded, result.Message);
            Assert.False(Directory.Exists(restDirectory));
            Assert.Contains("彻底删除", result.Message, StringComparison.Ordinal);
        }

        /// <summary>同一现场走「移入回收站」那一档：消息必须说"回收站"（而不是"彻底删除"）。</summary>
        [Fact]
        public void 回收站档_消息说清是回收站()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            RestPurgeOutcome result = new RestItemPurger().Purge(task, cancelled: false, DeleteMode.RecycleBin);

            Assert.True(result.Succeeded, result.Message);
            Assert.False(Directory.Exists(restDirectory));
            Assert.Contains("回收站", result.Message, StringComparison.Ordinal);
            Assert.Contains(RestItemPurger.AutoRecycleReason, string.Join(" | ", result.LogLines), StringComparison.Ordinal);
        }

        // ================================================================ 装配

        private MainViewModel CreateViewModel()
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.AutoScanAfterDrop = false;

            settingsService.Save(settings);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            try
            {
                return new MainViewModel(
                    new FileScanService(),
                    new ArchiveDetectService(),
                    new RenameService(),
                    new Engines.SevenZip.SevenZipEngine(),
                    new PasswordService(),
                    new LogService(pathService),
                    settingsService,
                    pathService,
                    new TaskSummaryService(),
                    new ClipboardService(),
                    new DialogService());
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;
            }
        }

        /// <summary>造一个"删除操作该动手"的现场：完成 + 校验通过 + 其余物里躺着源包与中间件。</summary>
        private (ArchiveTask Task, string RestDirectory) CreatePurgeScenario()
        {
            string outputPath = Path.Combine(_root, "out", "222");
            string restDirectory = Path.Combine(outputPath, "其余物");

            Directory.CreateDirectory(restDirectory);

            File.WriteAllText(Path.Combine(restDirectory, "222.7z"), "源包（会被删）");
            File.WriteAllText(Path.Combine(restDirectory, "inner-222.7z.001"), "内层分卷（中间件）");
            File.WriteAllText(Path.Combine(outputPath, "payload.mp4"), "内容物");

            var task = new ArchiveTask(Path.Combine(_root, "src", "222.7z"), 1)
            {
                FileName = "222.7z",
                OutputPath = outputPath,
                Status = StatusText.ExtractSuccess,
                IsOutputVerified = true,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = restDirectory,
                VolumeGroupKey = "222"
            };

            return (task, restDirectory);
        }
    }
}
