using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **⑤打包页的新流程**（用户 2026-09-26 第 46 条）：
    /// 点「打包」→ 一个小确认弹窗（落点 / 原包操作 / 其余物操作）→ 确认之后才动任何字节。
    ///
    /// <para>这一组钉三件事：①弹窗抬头把"源 + 最终产物"说清（含单文件那一档）；
    /// ②**取消 = 什么都没发生**（服务一次都没调、盘上一个字节都没动）；
    /// ③确认之后**照着弹窗里的选择做**，并把打包那一套写回设置（与解压侧各存各的）。</para>
    /// </summary>
    public class PackingUiFlowTests : IDisposable
    {
        private const string SamplePassword = "Packing-Flow-2026";

        private readonly string _root;

        public PackingUiFlowTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPackUi", Guid.NewGuid().ToString("N"));
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
            }
        }

        // ================================================================ 弹窗抬头（纯逻辑）

        [Fact]
        public void 确认弹窗抬头_源与最终产物都说清()
        {
            string source = BuildSourceFolder("素材");

            Assert.True(PackingPlan.TryCreate(
                new PackingRequest { SourceFolder = source, Password = SamplePassword },
                out PackingPlan? plan,
                out string error), error);

            PackingConfirmRequest request = PackingConfirmRequest.FromPlan(plan!, new PackingRunOptions());
            IReadOnlyList<string> head = request.BuildHeadLines();

            Assert.Contains(head, line => line.Contains("源文件夹", StringComparison.Ordinal) && line.Contains(source));
            Assert.Contains(head, line => line.Contains("内容：", StringComparison.Ordinal));
            Assert.Contains(head, line => line.Contains("按 2 卷切", StringComparison.Ordinal));
            Assert.Contains(head, line => line.Contains("最终产物：", StringComparison.Ordinal) && line.Contains(".rar"));

            // 落点默认是源旁边；其余物解释与"原包是什么"都要能给他看。
            Assert.Equal(_root, request.LocalTargetText);
            Assert.Contains("其余物", request.RestExplanation);
            Assert.Contains("原包", request.SourceExplanation);
        }

        /// <summary>单文件：抬头要写明"会先建同名文件夹"，其余物解释要把那一层也算进去。</summary>
        [Fact]
        public void 确认弹窗抬头_单文件说清会先建同名文件夹()
        {
            string file = Path.Combine(_root, "111.mp4");
            File.WriteAllBytes(file, new byte[128]);

            Assert.True(PackingPlan.TryCreate(
                new PackingRequest { SourceFolder = file, Password = SamplePassword },
                out PackingPlan? plan,
                out string error), error);

            PackingConfirmRequest request = PackingConfirmRequest.FromPlan(plan!, new PackingRunOptions());
            IReadOnlyList<string> head = request.BuildHeadLines();

            Assert.Contains(head, line => line.Contains("源文件：", StringComparison.Ordinal));
            Assert.Contains(head, line => line.Contains("同名文件夹", StringComparison.Ordinal));

            Assert.Contains("临时建的同名文件夹", request.RestExplanation);
            Assert.Contains("你选的那个文件", request.SourceExplanation);

            // 命名：装分卷的文件夹与最终产物都以 **111** 为准。
            Assert.Equal("111", plan!.SourceFolderName);
            Assert.Equal(Path.Combine(_root, "111.rar"), plan.RarPath);
        }

        // ================================================================ 抬头不许撒谎（第 46 条收尾）

        /// <summary>
        /// ⛔ **没装 WinRAR 时，弹窗里那句"最终产物"必须写 `.7z`** —— 实际产出的就是它。
        ///
        /// <para>收尾自查时抓到的真缺陷：外层容器是"实际生效的那个"由 `PackingService` 内部算，
        /// 而弹窗按请求里那个（永远 rar）写，于是没装 WinRAR 的机器上会写着
        /// <c>1111.rar</c>、跑完却得到 <c>1111.7z</c>（第 36/45 条那类"界面撒谎"）。
        /// 现在服务与界面引同一个 `PackingOuterContainerResolver`。</para>
        /// </summary>
        [Fact]
        public void 确认弹窗抬头_没装Rar时写的是7z产物而不是rar()
        {
            string source = BuildSourceFolder("素材");

            Assert.True(PackingPlan.TryCreate(
                new PackingRequest { SourceFolder = source, Password = SamplePassword },
                out PackingPlan? plan,
                out string error), error);

            // 本机没有 Rar.exe、但有内置 7-Zip → 实际会用 7z。
            PackingOuterContainerResolution outer = PackingOuterContainerResolver.Resolve(
                PackOuterContainer.Rar,
                rarExists: false,
                sevenZipExists: true);

            Assert.True(outer.FellBackToSevenZip);

            PackingConfirmRequest request = PackingConfirmRequest.FromPlan(plan!, new PackingRunOptions(), outer.Effective);
            IReadOnlyList<string> head = request.BuildHeadLines();

            string artifactLine = Assert.Single(head, line => line.Contains("最终产物：", StringComparison.Ordinal));

            Assert.Contains(".7z", artifactLine, StringComparison.Ordinal);
            Assert.DoesNotContain(".rar", artifactLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("自动用 7z", artifactLine, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⛔ 用户在弹窗里把落点改成「指定位置」之后，那句"最终产物"必须**跟着换目录** ——
        /// 否则弹窗说的是 A 目录、跑出来落在 B 目录（同一个"界面撒谎"的毛病）。
        /// </summary>
        [Fact]
        public void 确认弹窗抬头_落点改成指定位置之后最终产物跟着换目录()
        {
            string source = BuildSourceFolder("素材");

            Assert.True(PackingPlan.TryCreate(
                new PackingRequest { SourceFolder = source, Password = SamplePassword },
                out PackingPlan? plan,
                out string error), error);

            PackingConfirmRequest request = PackingConfirmRequest.FromPlan(plan!, new PackingRunOptions());

            string localLine = Assert.Single(
                request.BuildHeadLines(new PackingRunOptions { TargetMode = PackingTargetMode.Local }),
                line => line.Contains("最终产物：", StringComparison.Ordinal));

            Assert.Contains(_root, localLine, StringComparison.Ordinal);

            string elsewhere = Path.Combine(_root, "别处");

            Directory.CreateDirectory(elsewhere);

            string customLine = Assert.Single(
                request.BuildHeadLines(new PackingRunOptions
                {
                    TargetMode = PackingTargetMode.Custom,
                    CustomOutputDirectory = elsewhere
                }),
                line => line.Contains("最终产物：", StringComparison.Ordinal));

            Assert.Contains(elsewhere, customLine, StringComparison.Ordinal);
            Assert.EndsWith("素材.rar", customLine);
        }

        /// <summary>
        /// 三层解析规则本身（纯逻辑）：要 rar 有 rar 就用 rar；没 rar 有 7z 就自动 7z；
        /// 两个都没有则**什么外层都做不出来**（调用方据此报错，⛔ 不许假装成功）。
        /// </summary>
        [Fact]
        public void 外层容器解析_有Rar用Rar_没Rar自动7z_都没有则做不出来()
        {
            PackingOuterContainerResolution withRar = PackingOuterContainerResolver.Resolve(
                PackOuterContainer.Rar,
                rarExists: true,
                sevenZipExists: true);

            Assert.Equal(PackOuterContainer.Rar, withRar.Effective);
            Assert.False(withRar.FellBackToSevenZip);
            Assert.False(withRar.Impossible);

            PackingOuterContainerResolution fallback = PackingOuterContainerResolver.Resolve(
                PackOuterContainer.Rar,
                rarExists: false,
                sevenZipExists: true);

            Assert.Equal(PackOuterContainer.SevenZip, fallback.Effective);
            Assert.True(fallback.FellBackToSevenZip);
            Assert.Contains("没有 Rar.exe", fallback.Note, StringComparison.Ordinal);
            Assert.Contains("7z 外层", fallback.Note, StringComparison.Ordinal);

            PackingOuterContainerResolution nothing = PackingOuterContainerResolver.Resolve(
                PackOuterContainer.Rar,
                rarExists: false,
                sevenZipExists: false);

            Assert.True(nothing.Impossible, "要外层却什么都没有 → 必须报错，不许假装做完");

            // "不做外层"永远成立（它不需要任何工具）。
            Assert.False(PackingOuterContainerResolver
                .Resolve(PackOuterContainer.None, rarExists: false, sevenZipExists: false)
                .Impossible);
        }

        /// <summary>
        /// ⛔ 结果区**不许撒谎**（第 46 条收尾自查抓到的第 6 处）：默认档下"其余物"（装分卷的文件夹）
        /// 是被**彻底删掉**的，所以结果区不能再写"分卷在 X、你可以自己删 X"，也不该再出现
        /// 「其余物留着了」那句提示 —— 原来这两句是**无条件**拼上去的。
        ///
        /// <para>这条走**真 7z 端到端**（假 runner 到不了收尾那一步），直接读 ViewModel 的 `ResultText`。</para>
        /// </summary>
        [Fact]
        public async Task 结果区_默认档其余物已删_不许再写分卷在X可以自己删()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                return;
            }

            string source = BuildSourceFolder("素材");

            var tools = new ToolLocator();

            // 真服务 + 真 runner；弹窗替身回一套**默认档**（本地 / 原包不动 / 其余物彻底删除）。
            var viewModel = new PackingViewModel(
                new PackingService(tools, null, _ => long.MaxValue),
                new FakeDialogService(new PackingRunOptions()),
                new PathService { DataRootDirectory = Path.Combine(_root, "data") },
                new ClipboardService(),
                tools);

            viewModel.Settings = AppSettings.CreateDefault();
            viewModel.SourceFolder = source;
            viewModel.Password = SamplePassword;

            await viewModel.StartAsync();

            Assert.Equal("Success", viewModel.ResultKind);
            Assert.Contains("打包成功", viewModel.ResultText, StringComparison.Ordinal);

            // 装分卷的文件夹会因为"源文件夹本身就叫这个名字"而让位成 `素材(1)`（第 46 条的命名规则）。
            string volumesFolder = Path.Combine(_root, "素材(1)");

            Assert.False(Directory.Exists(volumesFolder), "默认档下装分卷的文件夹应当已被删掉");

            // 落点目录里只剩源文件夹本身（其余的都被清掉了）。
            Assert.Equal(new[] { source }, Directory.GetDirectories(_root));

            // ① 不能再说"分卷在 <那个文件夹>"。
            Assert.DoesNotContain($"分卷在：{volumesFolder}", viewModel.ResultText, StringComparison.Ordinal);

            // ② 也不能再说"其余物留着了、你自己删"。
            Assert.DoesNotContain("留着了", viewModel.ResultText, StringComparison.Ordinal);

            // ③ 要如实说"已彻底删除"。
            Assert.Contains("彻底删除", viewModel.ResultText, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⛔ **落点的选择在⑤页**（用户 2026-09-26 追加："选择还是得放在页面"）：
        /// 页面上选「指定位置 + 某目录」→ 最终产物就落在那儿；而弹窗里那两档**不许**改落点
        /// （这条用一个"乱返回落点"的弹窗替身钉住）。
        /// </summary>
        [Fact]
        public async Task 落点在页面上选_弹窗不许改落点()
        {
            string source = BuildSourceFolder("素材");
            string elsewhere = Path.Combine(_root, "出包");

            Directory.CreateDirectory(elsewhere);

            var runner = new CountingRunner();

            // 弹窗替身故意返回"本地 + 另一个目录"——页面选的是"指定位置（出包）"，必须以此为准。
            var confirmed = new PackingRunOptions
            {
                TargetMode = PackingTargetMode.Local,
                CustomOutputDirectory = Path.Combine(_root, "弹窗瞎指的目录"),
                SourceHandling = PackingSourceHandling.KeepInPlace,
                RestHandling = PackingRestHandling.Delete
            };

            Harness harness = BuildHarness(source, runner, confirmed);

            harness.ViewModel.PlacementIsCustom = true;
            harness.ViewModel.CustomPlacementDirectory = elsewhere;

            await harness.ViewModel.StartAsync();

            Assert.True(runner.Calls >= 1, "确认之后至少要把分卷那一步跑起来");

            // 分卷落点必须是**页面上选的那个目录**（不是弹窗里那个，也不是源旁边）。
            Assert.Contains(
                runner.Args,
                line => line.Contains(Path.Combine(elsewhere, "素材"), StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain(
                runner.Args,
                line => line.Contains("弹窗瞎指的目录", StringComparison.Ordinal));

            // 设置里记住的也是页面那一套。
            Assert.Equal("Custom", harness.Settings.PackTargetMode);
            Assert.Equal(elsewhere, harness.Settings.PackCustomOutputDirectory);
        }

        // ================================================================ 取消 = 什么都不做

        [Fact]
        public async Task 取消确认_一个字节都不动_服务一次都没调()
        {
            string source = BuildSourceFolder("素材");
            var runner = new CountingRunner();
            Harness harness = BuildHarness(source, runner, confirmed: null);

            await harness.ViewModel.StartAsync();

            Assert.Equal(0, runner.Calls);
            Assert.True(Directory.Exists(source), "取消时源必须原样留着");
            Assert.True(File.Exists(Path.Combine(source, "a.bin")));

            // 盘上不该多出任何东西（连装分卷的文件夹都不许建）。
            Assert.Single(Directory.GetDirectories(_root));
            Assert.Empty(Directory.GetFiles(_root, "*.rar", SearchOption.AllDirectories));
            Assert.Contains("什么都没做", harness.ViewModel.NoticeText, StringComparison.Ordinal);
        }

        // ================================================================ 确认 = 照着做 + 写回设置

        [Fact]
        public async Task 确认之后_照着弹窗的选择做_而且只写打包那一套设置()
        {
            string source = BuildSourceFolder("素材");
            string output = Path.Combine(_root, "出包");

            Directory.CreateDirectory(output);

            var runner = new CountingRunner();

            // 弹窗只管**两档操作**（落点由页面选，见 `落点在页面上选_弹窗不许改落点`）。
            var confirmed = new PackingRunOptions
            {
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Keep
            };

            Harness harness = BuildHarness(source, runner, confirmed);

            harness.ViewModel.PlacementIsCustom = true;
            harness.ViewModel.CustomPlacementDirectory = output;

            string extractSourceHandling = harness.Settings.SourceHandling;
            string extractRestHandling = harness.Settings.RestHandlingAfterVerify;

            await harness.ViewModel.StartAsync();

            // ① 弹窗那一套**真的传下去**了（分卷这一步跑起来了，而且是拿"弹出确认"之后才跑的）。
            Assert.True(runner.Calls >= 1, "确认之后至少要把分卷那一步跑起来");
            Assert.Contains(runner.Args, line => line.Contains("-v", StringComparison.Ordinal));

            // ② 写回的是**打包那几档**（落点取自页面、两档取自弹窗）；解压侧一个字都没动。
            Assert.Equal("Custom", harness.Settings.PackTargetMode);
            Assert.Equal("MoveToRest", harness.Settings.PackSourceHandling);
            Assert.Equal("Keep", harness.Settings.PackRestHandling);
            Assert.Equal(output, harness.Settings.PackCustomOutputDirectory);

            Assert.Equal(extractSourceHandling, harness.Settings.SourceHandling);
            Assert.Equal(extractRestHandling, harness.Settings.RestHandlingAfterVerify);

            // ③ 确认框也真的被问过一次（不是绕过去直接跑）。
            Assert.Equal(1, harness.Dialog.ConfirmCalls);

            // ④ 操作日志的"本次操作开始：打包"这一条**打过**（「导出日志（本次操作）」靠它定位）。
            Assert.Equal(1, harness.Starts());
        }

        /// <summary>
        /// ⛔ **取消时"本次操作开始"一次都不许打** —— 否则用户点一次取消再点「导出日志（本次操作）」，
        /// 导出来的头一行会是一句"本次操作开始：打包"，可那次操作什么都没干。
        /// </summary>
        [Fact]
        public async Task 取消确认_操作日志里连本次操作开始都不许写()
        {
            string source = BuildSourceFolder("素材");
            Harness harness = BuildHarness(source, new CountingRunner(), confirmed: null);

            await harness.ViewModel.StartAsync();

            Assert.Equal(0, harness.Starts());
        }

        /// <summary>弹窗里的选择来自设置：上次选"回收站"，这次打开就该是"回收站"。</summary>
        [Fact]
        public void 确认弹窗初值_来自上次存下的选择()
        {
            var settings = AppSettings.CreateDefault();

            settings.PackTargetMode = "Custom";
            settings.PackCustomOutputDirectory = @"D:\出包";
            settings.PackSourceHandling = "MoveToRest";
            settings.PackRestHandling = "RecycleBin";

            PackingRunOptions options = PackingRunOptions.FromSettings(settings);

            Assert.Equal(PackingTargetMode.Custom, options.TargetMode);
            Assert.Equal(@"D:\出包", options.CustomOutputDirectory);
            Assert.Equal(PackingSourceHandling.MoveToRest, options.SourceHandling);
            Assert.Equal(PackingRestHandling.RecycleBin, options.RestHandling);
        }

        // ================================================================ 工具

        /// <summary>找内置 7z.exe（找不到就跳过真引擎那几条）。</summary>
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

            return File.Exists(local) ? local : string.Empty;
        }

        private string BuildSourceFolder(string name)
        {
            string folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[4096]);

            return folder;
        }

        private Harness BuildHarness(string source, CountingRunner runner, PackingRunOptions? confirmed)
        {
            var settings = AppSettings.CreateDefault();
            var dialog = new FakeDialogService(confirmed);

            var tools = new ToolLocator { UseWinRarInstallation = false };
            var service = new PackingService(tools, runner, _ => long.MaxValue);

            var viewModel = new PackingViewModel(
                service,
                dialog,
                new PathService { DataRootDirectory = Path.Combine(_root, "data") },
                new ClipboardService(),
                tools);

            viewModel.Settings = settings;
            viewModel.SourceFolder = source;
            viewModel.Password = SamplePassword;

            int saves = 0;
            viewModel.SettingsChanged = () => saves++;

            int starts = 0;
            viewModel.OperationStarted = () => starts++;

            return new Harness(viewModel, dialog, settings, () => saves, () => starts);
        }

        private sealed class Harness
        {
            public Harness(
                PackingViewModel viewModel,
                FakeDialogService dialog,
                AppSettings settings,
                Func<int> saves,
                Func<int> starts)
            {
                ViewModel = viewModel;
                Dialog = dialog;
                Settings = settings;
                Saves = saves;
                Starts = starts;
            }

            public PackingViewModel ViewModel { get; }

            public FakeDialogService Dialog { get; }

            public AppSettings Settings { get; }

            public Func<int> Saves { get; }

            /// <summary>操作日志里"本次操作开始"打过几次。</summary>
            public Func<int> Starts { get; }
        }

        /// <summary>确认弹窗的替身：返回预设的那一套（null = 用户取消）。</summary>
        private sealed class FakeDialogService : DialogService
        {
            private readonly PackingRunOptions? _confirmed;

            public FakeDialogService(PackingRunOptions? confirmed) => _confirmed = confirmed;

            public int ConfirmCalls { get; private set; }

            public override PackingRunOptions? ShowPackingConfirm(PackingConfirmRequest request)
            {
                ConfirmCalls++;

                // 抬头必须算得出来（算不出来说明规划没走通 —— 那种情况本来就该在弹窗之前被拦下）。
                Assert.NotEmpty(request.BuildHeadLines());

                return _confirmed;
            }
        }

        /// <summary>假的打包进程：只记"调了几次"，不真的起 7z（这一组测的是流程，不是打包本身）。</summary>
        private sealed class CountingRunner : IPackProcessRunner
        {
            public int Calls { get; private set; }

            public List<string> Args { get; } = new();

            public Task<PackStepResult> RunAsync(
                PackToolKind tool,
                IReadOnlyList<string> arguments,
                string usedPassword,
                IProgress<PackStepProgress>? progress,
                System.Threading.CancellationToken cancellationToken,
                string? workingDirectory = null)
            {
                Calls++;
                Args.Add(tool + " " + string.Join(" ", arguments));

                return Task.FromResult(new PackStepResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardOutput = string.Empty,
                    StandardError = "（假 runner：这一组不真的打包）"
                });
            }
        }
    }
}
