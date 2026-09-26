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
            var runner = new CountingRunner();

            var confirmed = new PackingRunOptions
            {
                TargetMode = PackingTargetMode.Custom,
                CustomOutputDirectory = Path.Combine(_root, "出包"),
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Keep
            };

            Harness harness = BuildHarness(source, runner, confirmed);

            string extractSourceHandling = harness.Settings.SourceHandling;
            string extractRestHandling = harness.Settings.RestHandlingAfterVerify;

            await harness.ViewModel.StartAsync();

            // ① 弹窗那一套**真的传下去**了（分卷这一步跑起来了，而且是拿"弹出确认"之后才跑的）。
            Assert.True(runner.Calls >= 1, "确认之后至少要把分卷那一步跑起来");
            Assert.Contains(runner.Args, line => line.Contains("-v", StringComparison.Ordinal));

            // ② 写回的是**打包那几档**；解压侧一个字都没动。
            Assert.Equal("Custom", harness.Settings.PackTargetMode);
            Assert.Equal("MoveToRest", harness.Settings.PackSourceHandling);
            Assert.Equal("Keep", harness.Settings.PackRestHandling);
            Assert.Equal(Path.Combine(_root, "出包"), harness.Settings.PackCustomOutputDirectory);

            Assert.Equal(extractSourceHandling, harness.Settings.SourceHandling);
            Assert.Equal(extractRestHandling, harness.Settings.RestHandlingAfterVerify);

            // ③ 确认框也真的被问过一次（不是绕过去直接跑）。
            Assert.Equal(1, harness.Dialog.ConfirmCalls);
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

            return new Harness(viewModel, dialog, settings, () => saves);
        }

        private sealed class Harness
        {
            public Harness(PackingViewModel viewModel, FakeDialogService dialog, AppSettings settings, Func<int> saves)
            {
                ViewModel = viewModel;
                Dialog = dialog;
                Settings = settings;
                Saves = saves;
            }

            public PackingViewModel ViewModel { get; }

            public FakeDialogService Dialog { get; }

            public AppSettings Settings { get; }

            public Func<int> Saves { get; }
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
                System.Threading.CancellationToken cancellationToken)
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
