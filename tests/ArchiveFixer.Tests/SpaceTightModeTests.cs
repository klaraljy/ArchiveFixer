using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// ①页「空间不足」模式（用户 2026-09-27 拍板）的回归测试。
    ///
    /// <para><b>这个模式是什么</b>：盘快满时的一个**运行期**开关（⛔ 不写设置、不记忆）。开着时四件事一起变：
    /// ① 并发档由空间自己定（忽略设置里的档与「全速」）；② 按**净占用**从小到大排序；
    /// ③ 每个包定稿 + 校验通过后**立刻永久删除它的源包**（最快回收空间，这正是它存在的理由）；
    /// ④ 其余物（过程物）任务成功后彻底删除。</para>
    ///
    /// <para><b>这个文件钉住的四条红线</b>：</para>
    /// <list type="number">
    /// <item><description><b>不写设置</b>：开关一动，`appsettings.json` 一个字节都不许变
    /// （用户原话："覆盖是运行期的，别动我的设置"）；</description></item>
    /// <item><description><b>删的是源包、而且只在"定稿 + 校验通过"之后</b>：
    /// 失败 / 校验不过 / 取消三种情形一个字节都不删（与"删源包"那条既有红线同一口径）；</description></item>
    /// <item><description><b>覆盖确实生效</b>：设置里明明是「源包留在原地 + 其余物不动」，开了模式之后源包照删
    /// —— 而**设置里的值仍然没变**；</description></item>
    /// <item><description><b>覆盖要说出来</b>：日志、②③页的提示、一键处理确认框里的红字都按同一个词表说
    /// （"我以为它按默认跑的"是用户最恨的一件事）。</description></item>
    /// </list>
    /// </summary>
    public class SpaceTightModeTests : IDisposable
    {
        private readonly string _root;

        public SpaceTightModeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSpaceTight", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 开关本身：不写设置

        /// <summary>
        /// 开关一动，设置文件**一个字节都不许变**，而且 `appsettings.json` 里**根本没有这个键**。
        ///
        /// <para>判据用真实的设置文件文本（不是内存对象）："不记忆"这件事只有落到文件上才算数 ——
        /// 用户要的正是"下次启动它是关着的"。</para>
        /// </summary>
        [Fact]
        public void 空间不足开关_不写设置_设置文件一个字节都不变()
        {
            Harness harness = CreateHarness();

            string before = File.ReadAllText(harness.SettingsFilePath, Encoding.UTF8);

            harness.Vm.SpaceTightMode = true;

            Assert.True(harness.Vm.SpaceTightMode);

            string after = File.ReadAllText(harness.SettingsFilePath, Encoding.UTF8);

            Assert.Equal(before, after);

            /*
             * 判据刻意**按 JSON 的键**看，不是拿整份文件文本找子串：
             * 设置里存着缓存根 / 输出目录这些**路径**，而路径里完全可能带上测试自己的目录名
             * （真机上就是用户名或盘符），拿文本找子串会得到一个与产品行为无关的假阳性。
             */
            using var document = System.Text.Json.JsonDocument.Parse(after);

            foreach (System.Text.Json.JsonProperty property in document.RootElement.EnumerateObject())
            {
                Assert.DoesNotContain("tight", property.Name, StringComparison.OrdinalIgnoreCase);
            }

            // 覆盖提示跟着开关走（②③页那条线；关掉就整行收起）。
            Assert.True(harness.Vm.HasSpaceTightOverride);
            Assert.Contains("空间不足", harness.Vm.SpaceTightOverrideText, StringComparison.Ordinal);

            harness.Vm.SpaceTightMode = false;

            Assert.False(harness.Vm.HasSpaceTightOverride);
            Assert.Equal(string.Empty, harness.Vm.SpaceTightOverrideText);

            Assert.Equal(before, File.ReadAllText(harness.SettingsFilePath, Encoding.UTF8));
        }

        // ================================================================ ② 成功：当场删源包

        /// <summary>
        /// <b>这个模式的核心行为</b>：设置里明明是「源包留在原地 + 其余物不动」，
        /// 开了模式之后解压成功 → 源包**当场被删**，而设置里的两个值**一个字都没改**。
        ///
        /// <para>"当场"的判据是这批日志里的那条删除行；设置没被改的判据是内存对象 + 磁盘文件两处。</para>
        /// </summary>
        [Fact]
        public async Task 空间不足模式_定稿校验通过后立刻删源包_而设置一个字都没改()
        {
            Harness harness = CreateHarness(settings =>
            {
                // 刻意选"最不删东西"的两档：任何删除都只可能来自这个模式本身。
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string settingsBefore = File.ReadAllText(harness.SettingsFilePath, Encoding.UTF8);

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.SetProducts(("payload-00000.bin", 8));

            harness.Vm.SpaceTightMode = true;

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified);
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

            Assert.False(File.Exists(source), "空间不足模式下，定稿 + 校验通过之后源包必须被删掉（这正是它省空间的方式）");
            Assert.Equal(SourcePackageMoveState.Done, task.SourcePackageMove);

            // 删了必须**说出来**（用户要能事后回答"我的包哪去了"）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("空间不足模式", StringComparison.Ordinal) &&
                     x.Message.Contains("已立刻永久删除源包", StringComparison.Ordinal));

            // 批首也要说清这一批按什么跑（覆盖了什么、没写设置）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("「空间不足」模式（本次运行，不写设置）", StringComparison.Ordinal));

            // 设置：内存与磁盘两处都不许被改写。
            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), harness.Vm.Settings.SourceHandling);
            Assert.Equal(RestHandlingModes.Keep, harness.Vm.Settings.RestHandlingAfterVerify);
            Assert.Equal(settingsBefore, File.ReadAllText(harness.SettingsFilePath, Encoding.UTF8));
        }

        /// <summary>
        /// **对照组（红检的那一半）**：同一套设置、同一个包，只是**没开**那个模式 →
        /// 源包原地不动、设置里那两档照旧生效。两条测试合起来才证明"删源包"是这个模式干的。
        /// </summary>
        [Fact]
        public async Task 对照组_模式关着_源包照旧原地不动()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.SetProducts(("payload-00000.bin", 8));

            Assert.False(harness.Vm.SpaceTightMode);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source), "模式关着时源包必须原地不动");
            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains("空间不足模式", StringComparison.Ordinal));
        }

        // ================================================================ ③ 失败 / 校验不过 / 取消：一个字节都不删

        [Fact]
        public async Task 空间不足模式_解压失败_一个字节都不删()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.ExtractFailure = new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.Corrupted,
                Message = "文件损坏",
                DetectedErrorType = "Corrupted"
            };

            harness.Vm.SpaceTightMode = true;

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source), "解压失败时源包必须原地不动（红线不随模式放松）");
            Assert.True(harness.Engine.Extracted == false);
        }

        [Fact]
        public async Task 空间不足模式_校验不过_一个字节都不删()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.SetProducts(("payload-00000.bin", 8));
            harness.Engine.ExpectedTotalSize = 9999;   // 清单说 9999 字节、实际只有 8 → 校验不过

            harness.Vm.SpaceTightMode = true;

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source), "校验不过时源包必须原地不动");
        }

        [Fact]
        public async Task 空间不足模式_取消_一个字节都不删()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            // 解压已经做完、进入收尾（收尾第一件事就是列目录）时按下「取消当前」。
            harness.Engine.OnList = _ =>
            {
                if (harness.Engine.Extracted)
                {
                    harness.Coordinator.CancelCurrentTask();
                }
            };

            harness.Vm.SpaceTightMode = true;

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.True(File.Exists(source), "取消时源包必须原地不动");
        }

        // ================================================================ ④ 分卷组：整组一起删

        [Fact]
        public async Task 空间不足模式_分卷组成功_整组一起删()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string folder = Path.Combine(_root, "src", "volumes");
            Directory.CreateDirectory(folder);

            string first = Path.Combine(folder, "222.7z.001");
            string second = Path.Combine(folder, "222.7z.002");
            File.WriteAllText(first, new string('a', 64));
            File.WriteAllText(second, new string('b', 64));

            ArchiveTask task = await harness.ScanFolderAndAddTask(folder);

            Assert.True(task.IsVolumeGroup, "前提：扫描应当把这两卷归成一组");

            harness.Engine.SetProducts(("payload-00000.bin", 8));

            harness.Vm.SpaceTightMode = true;

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(first), "第一卷没有被删");
            Assert.False(File.Exists(second), "第二卷被落下了（整组必须一起删）");
        }

        // ================================================================ ⑤ 一键处理那条路也认这个模式

        [Fact]
        public async Task 空间不足模式_一键处理也删源包()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
                settings.SkipOneClickConfirm = true;   // 只跑流程，不弹确认框（测试里没人在那儿点）
            });

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.SetProducts(("payload-00000.bin", 8));

            harness.Vm.SpaceTightMode = true;

            await harness.OneClick.RunPipelineAsync(harness.Vm.Tasks.ToList());

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(File.Exists(source), "一键处理那条路也必须按空间不足模式删源包");
            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), harness.Vm.Settings.SourceHandling);
        }

        // ================================================================ ⑥ 动手前必须看得见（确认框红字）

        /// <summary>
        /// 一键处理确认框里那条红字：模式开着才出现，而且正文那两行必须换成**覆盖后**的值 ——
        /// 只加一条红字、却让"源包：留在原地"那一行照旧显示设置值，等于让用户在同一个框里读到两句矛盾的话。
        /// </summary>
        [Fact]
        public async Task 确认框事实_模式开着才有红字_而且源包与其余物两行按覆盖后的值说()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            OneClickRunOptions seed = OneClickRunOptions.FromSettings(harness.Vm.Settings);

            // ① 模式关着：没有红字，正文说的是设置里的值（留在原地 / 不动其余物）。
            OneClickConfirmFacts normal = await harness.Coordinator.BuildConfirmFactsAsync(
                new[] { task }, seed, reminders: null);

            Assert.Equal(string.Empty, normal.SpaceTightEcho);
            Assert.Contains(OneClickRunOptions.DescribeSourceHandling(SourceHandlingMode.KeepInPlace), normal.SourceEcho, StringComparison.Ordinal);
            Assert.Contains(StatusText.OneClickConfirmRestKeep, normal.RestEcho, StringComparison.Ordinal);

            // ② 模式开着：红字出现，正文两行**都换成覆盖后的值**（用户不会被同一框里的两句话绕进去）。
            harness.Vm.SpaceTightMode = true;

            OneClickConfirmFacts tight = await harness.Coordinator.BuildConfirmFactsAsync(
                new[] { task }, seed, reminders: null);

            Assert.Equal(StatusText.SpaceTightConfirmText, tight.SpaceTightEcho);
            Assert.Contains("永久删除", tight.SpaceTightEcho, StringComparison.Ordinal);
            Assert.Contains("不写进设置", tight.SpaceTightEcho, StringComparison.Ordinal);

            Assert.Contains(OneClickRunOptions.DescribeSourceHandling(SourceHandlingMode.MoveToRest), tight.SourceEcho, StringComparison.Ordinal);
            Assert.Contains(StatusText.OneClickConfirmRestAutoDelete, tight.RestEcho, StringComparison.Ordinal);

            // 而且它只是"这一次"的说法：设置本身还是留在原地 / 不动其余物。
            Assert.Equal(nameof(SourceHandlingMode.KeepInPlace), harness.Vm.Settings.SourceHandling);
            Assert.Equal(RestHandlingModes.Keep, harness.Vm.Settings.RestHandlingAfterVerify);
        }

        // ================================================================ ⑦ 界面：开关在①页主操作栏，「只解压」已挪进手动操作

        /// <summary>
        /// ①页那颗黄色「空间不足」勾选框（用户 2026-09-27："放在一键处理左边"）：
        /// 绑的是**运行期**属性 <c>SpaceTightMode</c>（⛔ 不是设置项），而且 ToolTip 必须把
        /// "会永久删除源包""只对本次运行有效"两件事说出来。
        ///
        /// <para>同一条测试还钉住另一次挪位：「只解压」从主操作栏搬进了「手动操作」折叠区 ——
        /// 判据是**它在文件里的位置**（折叠区那一行之后），不是"文件里有没有这个词"。</para>
        /// </summary>
        [Fact]
        public void 界面_空间不足开关在任务页主操作栏_只解压已挪进手动操作()
        {
            string taskTab = File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Views", "Tabs", "TaskTab.xaml"),
                Encoding.UTF8);

            Assert.Contains("IsChecked=\"{Binding SpaceTightMode, Mode=TwoWay}\"", taskTab, StringComparison.Ordinal);
            Assert.Contains("StatusText.SpaceTightToggleLabel", taskTab, StringComparison.Ordinal);
            Assert.Contains("StatusText.SpaceTightToggleToolTip", taskTab, StringComparison.Ordinal);

            // 「不删原包」= 那个模式的安全档（用户 2026-09-27 追加），就在它旁边。
            Assert.Contains("IsChecked=\"{Binding SpaceTightKeepSource, Mode=TwoWay}\"", taskTab, StringComparison.Ordinal);
            Assert.Contains("StatusText.SpaceTightKeepSourceLabel", taskTab, StringComparison.Ordinal);
            Assert.Contains("StatusText.SpaceTightKeepSourceToolTip", taskTab, StringComparison.Ordinal);

            Assert.True(
                taskTab.IndexOf("SpaceTightKeepSource", StringComparison.Ordinal) >
                taskTab.IndexOf("SpaceTightMode", StringComparison.Ordinal),
                "「不删原包」要排在「空间不足」右边（用户原话：「在空间不足旁边弄一个」）");

            int switchIndex = taskTab.IndexOf("SpaceTightMode", StringComparison.Ordinal);
            int oneClickIndex = taskTab.IndexOf("OneClickProcessCommand", StringComparison.Ordinal);
            int manualIndex = taskTab.IndexOf("手动操作（单独改后缀 / 单独解压）", StringComparison.Ordinal);
            int extractIndex = taskTab.IndexOf("StartExtractCommand", StringComparison.Ordinal);

            Assert.True(switchIndex > 0 && oneClickIndex > switchIndex, "「空间不足」必须排在「一键处理」左边");
            Assert.True(manualIndex > 0 && extractIndex > manualIndex, "「只解压」必须已经挪进「手动操作」折叠区");

            // 「空间不足」是运行期开关：界面这一页不许出现任何"写设置"的绑定（SettingsEditor / Settings.）。
            int switchLineEnd = taskTab.IndexOf("/>", switchIndex, StringComparison.Ordinal);
            string switchBlock = taskTab[switchIndex..switchLineEnd];

            Assert.DoesNotContain("Settings", switchBlock, StringComparison.Ordinal);
        }

        // ================================================================ ⑧ 导入后的空间体检（提示 + 弹窗）

        /// <summary>
        /// 导入结束就自动体检一次（用户 2026-09-27 第 2 条）：源包总量 + 目标盘可用空间写进日志；
        /// 有整盘都放不下的包时，**黄色 WARN + 红色 ERROR 两条都写**，并点名那两个包。
        ///
        /// <para>这一条用假盘探测（<c>SpaceProbeOverride</c>）把可用空间钉成 1 GiB，
        /// 于是"放不下"这件事是**算出来的**而不是碰运气碰上的。</para>
        /// </summary>
        [Fact]
        public async Task 空间体检_放不下时_日志两条级别都写_并且点名()
        {
            Harness harness = CreateHarness();

            /*
             * 假盘：可用 4 KiB、要求保留 0 余量 → 预算是 4 KiB。
             *
             * ⚠ 必须用**假探测**而不是造一个大文件：估算器读的是**真实文件大小**
             * （`SpaceEstimator.FromSourceFiles` 直接 stat），手改 `ArchiveTask.SourceSizeBytes`
             * 对账面没有任何影响 —— 而造一个真的 4 GiB 样本既慢又会因为盘满而失败
             * （那样测的是这台机器的磁盘，不是体检逻辑）。
             *
             * ⚠ 探测口子在**视图模型自己那一个**协调器上（`ExtractionPipeline`）：体检是
             * `Vm.CheckSpaceForTasksAsync` 发起的，用测试另建的那个实例注入是打不中的。
             */
            harness.Vm.ExtractionPipeline.SpaceProbeOverride = _ => 4096;
            harness.Vm.ExtractionPipeline.SpaceReserveOverride = 0;

            string folder = Path.Combine(_root, "src", "check");
            Directory.CreateDirectory(folder);

            // 3 KiB 的包峰值 6 KiB > 预算 4 KiB → 整盘都放不下，必须被点名。
            File.WriteAllBytes(Path.Combine(folder, "big.7z"), new byte[3 * 1024]);

            ArchiveTask bigTask = await harness.ScanFolderAndAddTask(folder);

            Assert.Equal("big.7z", bigTask.FileName);

            await harness.Vm.CheckSpaceForTasksAsync(harness.Vm.Tasks.ToList(), "导入完成");

            Assert.True(
                harness.Log.Logs.Any(x => x.Level == "WARN" && x.Message.Contains("空间体检（导入完成）", StringComparison.Ordinal)),
                "日志里没有那条空间体检 WARN。实际日志：" + DescribeLogs(harness));

            Assert.True(
                harness.Log.Logs.Any(x => x.Level == "ERROR" &&
                                          x.Message.Contains("空间不足，这一批会跳过", StringComparison.Ordinal) &&
                                          x.Message.Contains("big.7z", StringComparison.Ordinal)),
                "日志里没有那条点名 ERROR。实际日志：" + DescribeLogs(harness));

            // 弹窗也要弹（用户原话"因为这个比较危险"）——无 UI 宿主时它降级成一条记录，不弹、不死等。
            Assert.Contains(
                DialogService.FallbackLog,
                line => line.Contains("ShowSpaceShortageWarning", StringComparison.Ordinal));
        }

        /// <summary>
        /// **自动**这两个字要真的落地：走一次真正的导入（<c>ScanCoordinator.AddPathsAsync</c>，
        /// 与①页「添加文件 / 添加文件夹」同一条路），不用用户点任何东西，空间体检就已经跑过一遍。
        ///
        /// <para>续解往列表里补内层包那一条（<c>suppressAutoScan = true</c>）**不做**体检：
        /// 那些包是程序刚产出的过程物，正在跑的批次里统计它们没有意义。</para>
        /// </summary>
        [Fact]
        public async Task 导入完成_自动做一次空间体检()
        {
            Harness harness = CreateHarness();

            harness.Vm.ExtractionPipeline.SpaceProbeOverride = _ => 4096;
            harness.Vm.ExtractionPipeline.SpaceReserveOverride = 0;

            string folder = Path.Combine(_root, "src", "auto-check");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "big.7z"), new byte[3 * 1024]);

            var scan = new ScanCoordinator(
                harness.Vm, new FileScanService(), new ArchiveDetectService(), new DialogService());

            await scan.AddPathsAsync(new[] { folder }, ImportMode.Replace, suppressAutoScan: false);

            Assert.NotEmpty(harness.Vm.Tasks);

            Assert.True(
                harness.Log.Logs.Any(x => x.Message.Contains("空间体检（导入完成）", StringComparison.Ordinal)),
                "导入完成后必须自动体检一次。实际日志：" + DescribeLogs(harness));
        }

        /// <summary>
        /// **②页那个「选择…」也必须是触发点**（用户 2026-09-27 第 2 条："用户选择/切换指定位置时也要判"）。
        ///
        /// <para>①页与②页的「选择…」是**两个入口、同一件事**（换了一块输出盘）。
        /// 这条分两半钉：①②页那颗按钮选完目录要**通知**一声（本类造一个假的文件夹选择器，
        /// 因为无界面宿主里真对话框一律返回空串）；②主视图模型接住之后**真的做体检**
        /// （直接调它那个回调，断言日志里出现了体检结论）。</para>
        /// </summary>
        [Fact]
        public async Task 换输出位置_二页那颗选择按钮也会触发空间体检()
        {
            Harness harness = CreateHarness();

            harness.Vm.ExtractionPipeline.SpaceProbeOverride = _ => 4096;
            harness.Vm.ExtractionPipeline.SpaceReserveOverride = 0;

            // ① ②页那颗按钮：选完目录要说一声（值也真的写进设置）。
            string picked = Path.Combine(_root, "picked-output");
            Directory.CreateDirectory(picked);

            var picker = new FolderPickerDialogService { FolderToReturn = picked };
            var editor = new SettingsViewModel(harness.Vm.Settings, new SettingsService(harness.PathService), picker);

            string? notified = null;
            editor.OutputDirectoryPicked = folder => notified = folder;

            editor.SelectOutputDirectoryCommand.Execute(null);

            Assert.Equal(picked, notified);
            Assert.Equal(picked, editor.Settings.CustomOutputDirectory);

            // ② 主视图模型的回调 = 真做一次体检（把回调接到那个假盘上，看日志）。
            string folder = Path.Combine(_root, "src", "second-entry");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "big.7z"), new byte[3 * 1024]);

            ArchiveTask task = await harness.ScanFolderAndAddTask(folder);

            Assert.Equal("big.7z", task.FileName);

            Assert.NotNull(harness.Vm.SettingsEditor.OutputDirectoryPicked);

            harness.Vm.SettingsEditor.OutputDirectoryPicked!(picked);

            // 回调是"投递即返回"的（不 await）：等那次后台体检跑完（有界等待）。
            await WaitForLogAsync(harness, "空间体检（换了输出位置）");
        }

        // ================================================================ ⑦ 安全档：不删原包（2026-09-27 追加）

        /// <summary>
        /// 「空间不足 + 不删原包」= **安全档**（用户 2026-09-27："源包讲实话，这个功能才刚刚弄
        /// 我怕会出现意外，导致没成功而且原包也没有了，这样的话就太亏了"）：
        /// 并发与排序照旧由空间决定，但**源包一个字节都不动**（既不搬进其余物、也不删除）。
        /// </summary>
        [Fact]
        public async Task 不删原包_源包一个字节都不动_而设置也没被改()
        {
            Harness harness = CreateHarness(settings =>
            {
                // 设置里刻意选"会删"的那一套组合：安全档必须**压过**它。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string settingsBefore = File.ReadAllText(harness.SettingsFilePath, Encoding.UTF8);

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.SetProducts(("payload-00000.bin", 8));

            harness.Vm.SpaceTightKeepSource = true;

            // 勾上安全档 → 自动把「空间不足」也勾上（它只是那个模式的安全档）。
            Assert.True(harness.Vm.SpaceTightMode);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified);

            Assert.True(File.Exists(source), "安全档下源包必须原地不动（这就是它存在的理由）");
            Assert.Equal(SourcePackageMoveState.NotAttempted, task.SourcePackageMove);

            // 源包没进其余物：其余物目录里不许出现那个包（"不搬"也要能被验）。
            string restDirectory = Path.Combine(task.OutputPath, "其余物");

            if (Directory.Exists(restDirectory))
            {
                Assert.DoesNotContain(
                    Directory.EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories),
                    path => string.Equals(Path.GetFileName(path), "pack.7z", StringComparison.OrdinalIgnoreCase));
            }

            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("不删原包", StringComparison.Ordinal) &&
                     x.Message.Contains("一个字节都不动", StringComparison.Ordinal));

            // 设置同样一个字节都没改。
            Assert.Equal(nameof(SourceHandlingMode.MoveToRest), harness.Vm.Settings.SourceHandling);
            Assert.Equal(RestHandlingModes.Delete, harness.Vm.Settings.RestHandlingAfterVerify);
            Assert.Equal(settingsBefore, File.ReadAllText(harness.SettingsFilePath, Encoding.UTF8));
        }

        /// <summary>两个勾的耦合：勾安全档 → 自动勾上「空间不足」；关「空间不足」→ 安全档一起关。</summary>
        [Fact]
        public void 两个勾的耦合_勾安全档顺带勾模式_关模式连安全档一起关()
        {
            Harness harness = CreateHarness();

            Assert.False(harness.Vm.SpaceTightMode);
            Assert.False(harness.Vm.SpaceTightKeepSource);

            harness.Vm.SpaceTightKeepSource = true;

            Assert.True(harness.Vm.SpaceTightMode, "勾上安全档必须顺带把「空间不足」也勾上");
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("顺带把「空间不足」也勾上", StringComparison.Ordinal));

            // 覆盖提示要说"源包一个字节都不动"，⛔ 不许拿"会删源包"那条（两档行为相反）。
            Assert.Contains("一个字节都不动", harness.Vm.SpaceTightOverrideText, StringComparison.Ordinal);

            harness.Vm.SpaceTightMode = false;

            Assert.False(harness.Vm.SpaceTightKeepSource, "关掉「空间不足」之后安全档不能孤零零留着");
            Assert.False(harness.Vm.HasSpaceTightOverride);
        }

        /// <summary>
        /// **空间曲线**：一批跑完，日志里必须有"起 → 最低 → 收 + 最多同时占用"那一条，
        /// 而且批首要留下"每 5 秒侦察一次"那句话（用户 2026-09-27："你要时刻弄空间检测……
        /// 同样也要弄空间检查技术"）。
        ///
        /// <para>⚠ 这里只钉"这条线存在、字段齐、针数够"；**曲线的算术**（阈值、最低点、占用）由
        /// <c>SpaceTrendMonitorTests</c> 用注时钟的单测精确钉住 —— 在这一层追具体数字会很脆：
        /// 批首之前还有两次探测（排计划 + 建账本），把假盘按"第几次探测"编号等于在测别的东西。</para>
        /// </summary>
        [Fact]
        public async Task 空间曲线_批末必须给出起最低收与最多占用()
        {
            Harness harness = CreateHarness();

            long free = 10L * 1024 * 1024 * 1024;
            harness.Coordinator.SpaceProbeOverride = _ => free;
            harness.Coordinator.SpaceReserveOverride = 0;

            string source = harness.CreateSource("pack.7z");
            ArchiveTask task = await harness.ScanFolderAndAddTask(Path.GetDirectoryName(source)!);

            harness.Engine.SetProducts(("payload-00000.bin", 8));

            // 解压那一刻盘上掉下去（曲线的最低点就出现在这一带）。
            harness.Engine.OnExtract = _ => free = 4L * 1024 * 1024 * 1024;

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string curve = Assert.Single(
                harness.Log.Logs,
                x => x.Message.Contains("空间曲线", StringComparison.Ordinal)).Message;

            Assert.Contains("起 ", curve, StringComparison.Ordinal);
            Assert.Contains("最低 ", curve, StringComparison.Ordinal);
            Assert.Contains("收 ", curve, StringComparison.Ordinal);
            Assert.Contains("最多同时占用约", curve, StringComparison.Ordinal);

            // 针数够（批首 / 开工 / 收尾 / 批末 至少四针），而且批首那句"时刻侦察"在。
            Assert.Contains("本批 4 针", curve, StringComparison.Ordinal);

            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("每 5 秒侦察一次", StringComparison.Ordinal));
        }

        // ================================================================ 装配

        /// <summary>有界等待某条日志出现（回调是 fire-and-forget，不能同步断言）。</summary>
        private static async Task WaitForLogAsync(Harness harness, string fragment)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (harness.Log.Logs.Any(x => x.Message.Contains(fragment, StringComparison.Ordinal)))
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail($"等了 5 秒也没等到那条日志：{fragment}。实际日志：" + DescribeLogs(harness));
        }

        /// <summary>假的文件夹选择器（只覆盖"用户挑好了哪个目录"这一步，其余行为与真实的一致）。</summary>
        private sealed class FolderPickerDialogService : DialogService
        {
            public string FolderToReturn { get; set; } = string.Empty;

            public override string ShowFolderBrowserDialog(string title, string initialDirectory)
                => FolderToReturn;

            public override string ShowFolderBrowserDialog() => FolderToReturn;
        }

        /// <summary>把这一批的日志拼成一行（断言失败时看得见"实际写了什么"，省一次返工）。</summary>
        private static string DescribeLogs(Harness harness)
            => string.Join(
                " ｜ ",
                harness.Log.Logs.Select(entry => entry.Level + ":" + entry.Message));

        private Harness CreateHarness(Action<AppSettings>? configure = null)
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
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.TryEmptyPasswordFirst = false;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var fake = new FakeEngine();

            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                fake,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;

            var coordinator = new ExtractionCoordinator(
                vm, fake, passwordService, pathService, new DialogService());

            coordinator.KeepTaskDetailInLog = true;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());

            return new Harness(vm, fake, coordinator, oneClick, logService, pathService, outputRoot, dataRoot);
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                LogService log,
                PathService pathService,
                string outputRoot,
                string dataRoot)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                OneClick = oneClick;
                Log = log;
                PathService = pathService;
                OutputRoot = outputRoot;
                DataRoot = dataRoot;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public OneClickCoordinator OneClick { get; }

            public LogService Log { get; }

            public PathService PathService { get; }

            public string SettingsFilePath => PathService.SettingsFilePath;

            public string OutputRoot { get; }

            public string DataRoot { get; }

            public string CreateSource(string fileName)
            {
                string directory = Path.Combine(DataRoot, "..", "src", "folder");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, fileName);
                File.WriteAllText(path, "不是真的压缩包（这一组用的是假引擎）");
                return path;
            }

            /// <summary>走**真正的"添加文件夹"路径**造任务（与用户在真机上那条路一致）。</summary>
            public async Task<ArchiveTask> ScanFolderAndAddTask(string folder)
            {
                var scanService = new FileScanService();

                List<ArchiveTask> scanned = await scanService.ScanPathsAsync(
                    new[] { folder },
                    new ScanOptions { RecursiveScan = true, ScanMode = "ScanAllFiles" });

                Assert.NotEmpty(scanned);

                ArchiveTask task = scanned[0];
                task.IsSelected = true;
                task.Status = StatusText.Recognized;
                task.Index = Vm.Tasks.Count + 1;

                Vm.Tasks.Add(task);
                return task;
            }
        }

        private sealed class FakeEngine : IArchiveEngine
        {
            private List<(string Name, int Size)> _products = new() { ("payload-00000.bin", 8) };

            public int ExpectedFileCount { get; set; } = 1;

            public long ExpectedTotalSize { get; set; } = 8;

            /// <summary>非 null = 解压直接返回这个失败结论。</summary>
            public ArchiveOperationResult? ExtractFailure { get; set; }

            /// <summary>列目录前回调（收尾第一件事就是列目录：用来在那一刻按取消 / 改假盘数字）。</summary>
            public Action<ArchiveRequest>? OnList { get; set; }

            /// <summary>真正解压那一刻的回调（用来在"内容物落盘之后"改假盘数字）。</summary>
            public Action<ArchiveRequest>? OnExtract { get; set; }

            public bool Extracted { get; private set; }

            public void SetProducts(params (string Name, int Size)[] products)
            {
                _products = products.ToList();
                ExpectedFileCount = products.Length;
                ExpectedTotalSize = products.Sum(p => (long)p.Size);
            }

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
                => Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                OnList?.Invoke(request);

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = ExpectedFileCount,
                    TotalUncompressedSize = ExpectedTotalSize,
                    Entries = Enumerable.Range(0, ExpectedFileCount)
                        .Select(i => new ArchiveEntry { Path = $"entry-{i:D5}.bin", Size = 1 })
                        .ToList(),
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(Succeeded());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                if (ExtractFailure != null)
                {
                    return Task.FromResult(ExtractFailure);
                }

                OnExtract?.Invoke(request);

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    foreach ((string name, int size) in _products)
                    {
                        File.WriteAllBytes(Path.Combine(output, name), new byte[size]);
                    }
                }

                Extracted = true;

                return Task.FromResult(Succeeded());
            }

            private static ArchiveOperationResult Succeeded() => new()
            {
                Success = true,
                Status = StatusText.ExtractSuccess,
                Message = "解压成功",
                DetectedErrorType = "None"
            };
        }
    }
}
