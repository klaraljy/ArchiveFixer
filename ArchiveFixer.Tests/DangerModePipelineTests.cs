using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// 「空间门 + 危险模式」的**端到端**测试：真 MainViewModel + 真解压管线，只把归档引擎换成假引擎。
    ///
    /// <para>这一组要证明的是"跑起来真的会那样"（纯模型那组在 <c>SpaceModeTests</c>）：</para>
    /// <list type="number">
    /// <item><description><b>危险模式真的边解边删</b>：内容物定稿 + 校验通过之后，其余物（含源包）
    /// 被彻底删除，而失败的任务一个字节都不动；</description></item>
    /// <item><description><b>空间门真的拦得住</b>：假盘（可注入的探测）装不下的任务**不启动**，
    /// 状态落「磁盘空间不足」，日志如实报告"跳过谁、需要多少、差多少"；</description></item>
    /// <item><description><b>自测协议真的会跑</b>：拿"并发 × 2"个文件真跑一遍，通过才写出凭证；
    /// 样本不够 / 前置条件不满足时拒绝，并且**不动设置文件**。</description></item>
    /// </list>
    ///
    /// <para>MainViewModel 的构造会写两个进程级静态（7z 路径 / 递归工作区根目录），
    /// 所以与其它管线测试同一组串行，并在装配后立刻还原。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class DangerModePipelineTests : IDisposable
    {
        private readonly string _root;

        public DangerModePipelineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDangerMode", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 危险模式：边解边删

        [Fact]
        public async Task 危险模式_成功后立刻彻底删掉其余物与源包()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.DangerousSpaceModeEnabled = true;

                // 凭证必须**盖得住当前并发档**（默认 4 → 要 8 个样本），
                // 否则本批不生效（那条规则的正例见下面的"凭证盖不住"三个测试）。
                settings.DangerModeSelfTestStamp = ValidStamp(parallel: 4, sample: 8);
            });

            string source = CreateSourceFile("222.7z");

            ArchiveTask task = AddTask(harness, source);
            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified, "输出校验应当通过（假引擎的产物与清单一致）");

            // 内容物还在。
            Assert.True(File.Exists(Path.Combine(task.OutputPath, "payload.mp4")));

            // 其余物（含源包）已经没了 —— 这正是"净占用基本不变"的来源。
            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.False(Directory.Exists(restDirectory), $"其余物应当被彻底删除：{restDirectory}");
            Assert.False(File.Exists(source), "源包应当已经被永久删除");

            // 日志里必须留下"删了什么、为什么"的痕迹（不可逆操作要可追溯）。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("危险模式已彻底删除其余物", StringComparison.Ordinal));

            // 风险四条也要在日志里（用户事后回看日志时能想起自己在什么档位上跑过）。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("源包会被永久删除", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 危险模式关着时_源包只是搬进其余物_不会被删()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.DangerousSpaceModeEnabled = false;
            });

            string source = CreateSourceFile("333.7z");

            ArchiveTask task = AddTask(harness, source);
            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.True(Directory.Exists(restDirectory), "默认档下其余物必须留着（用户可确认后清掉）");

            // 源包在其余物里（可还原：它只是被移走，没有被删）。
            Assert.False(File.Exists(source));
            Assert.True(
                Directory.GetFiles(restDirectory, "333.7z", SearchOption.AllDirectories).Length == 1,
                "源包应当在其余物里，等着用户确认删除");
        }

        [Fact]
        public async Task 危险模式_任务失败时一个字节都不删()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.DangerousSpaceModeEnabled = true;
                settings.DangerModeSelfTestStamp = ValidStamp(parallel: 4, sample: 8);
            });

            string source = CreateSourceFile("444.7z");

            ArchiveTask task = AddTask(harness, source);

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("payload.mp4", 8));

            harness.Engine.OnExtractAsync = _ => Task.FromResult(new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.Corrupted,
                Message = "文件损坏（测试用）",
                DetectedErrorType = "Corrupted"
            });

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);

            // 源包原地不动、其余物不生成（不变量 1 的红线）。
            Assert.True(File.Exists(source), "失败时源包必须原地不动");
            Assert.False(Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)));

            Assert.DoesNotContain(
                harness.Log.Logs,
                item => item.Message.Contains("危险模式已彻底删除其余物", StringComparison.Ordinal));
        }

        // ================================================================ ①b 自测凭证只覆盖它跑过的那一档

        [Fact]
        public async Task 凭证盖不住当前并发档时_本批一个字节都不删_并且明说是为什么()
        {
            /*
             * 用户那条协议是"拿并发数 × 2 个文件先跑一遍，通过了才能用"。
             * 所以凭证只对它跑过的那一档成立：并发 4 的凭证不能替并发 8 背书 ——
             * 那等于**没测过就用了**，而这条路上源包是永久删除。
             *
             * 这里造的正是那个现场：凭证是并发 1（2 个样本），现在这一档是并发 4。
             * 期望：**走普通档**（其余物还在、源包只是被搬进去），并且日志里说清原因与出路。
             */
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.DangerousSpaceModeEnabled = true;
                settings.MaxParallelExtractCount = 4;
                settings.DangerModeSelfTestStamp = ValidStamp(parallel: 1, sample: 2);
            });

            string source = CreateSourceFile("555.7z");

            ArchiveTask task = AddTask(harness, source);
            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 一个字节都没删：其余物还在，源包在里面等着用户自己确认。
            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.True(Directory.Exists(restDirectory), "凭证盖不住时不许永久删除任何东西");
            Assert.Single(Directory.GetFiles(restDirectory, "555.7z", SearchOption.AllDirectories));

            Assert.DoesNotContain(
                harness.Log.Logs,
                item => item.Message.Contains("危险模式已彻底删除其余物", StringComparison.Ordinal));

            // 静默降级是不能接受的：日志必须说清"没生效、为什么、怎么办"。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("盖不住", StringComparison.Ordinal));

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("普通档", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 把并发档调回凭证覆盖的档位_危险模式立刻恢复生效()
        {
            // 上一条的反面：同一份凭证（并发 4），并发档调回 4 → 照常边解边删。
            // 这一条钉住"单调方向"：不是"凭证必须等于当前档"，而是"凭证必须盖得住当前档"。
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.DangerousSpaceModeEnabled = true;
                settings.MaxParallelExtractCount = 4;
                settings.DangerModeSelfTestStamp = ValidStamp(parallel: 8, sample: 16);
            });

            string source = CreateSourceFile("666.7z");

            ArchiveTask task = AddTask(harness, source);
            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "并发 8 的凭证盖得住并发 4（调小只会更安全），所以应当照常删除");

            Assert.False(File.Exists(source), "源包应当已经被永久删除");
        }

        [Fact]
        public async Task 凭证里的样本数不够两倍并发时_也不算盖得住()
        {
            /*
             * 凭证字段是**文本**，手改成"并发=8|样本=2"就能骗过"只看并发数"的实现。
             * 协议要的是 2× 并发个样本，所以样本数也必须单独判 —— 这里钉住它。
             */
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.DangerousSpaceModeEnabled = true;
                settings.MaxParallelExtractCount = 4;

                // 并发写着 8，样本却只有 2 个（并发 8 协议要 16 个）。
                settings.DangerModeSelfTestStamp = ValidStamp(parallel: 8, sample: 2);
            });

            string source = CreateSourceFile("777.7z");

            ArchiveTask task = AddTask(harness, source);
            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            Assert.True(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "样本数不足 2×并发 → 凭证不算数 → 不许删任何东西");

            Assert.True(File.Exists(Path.Combine(
                Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName),
                "777.7z")));
        }

        // ================================================================ ② 空间门：装不下就不启动

        [Fact]
        public async Task 空间门_假盘装不下第二个任务时_它不启动并且如实报告()
        {
            /*
             * 造一块"只有 40 KiB"的假盘（可注入的探测），每个任务峰值 = 2 × 源包大小 = 32 KiB：
             * 第一个放行并记账（预留 32 KiB），第二个 32 + 32 = 64 > 40 → **必须被拦下**。
             * 并发档取 2，让第二个任务的判断发生在第一个还在跑的时候（这正是并发撑爆盘的那条路径）。
             */
            const long fakeAvailable = 40 * 1024;

            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.MaxParallelExtractCount = 2;
            });

            harness.Coordinator.SpaceProbeOverride = _ => fakeAvailable;
            harness.Coordinator.SpaceReserveOverride = 0;

            ArchiveTask first = AddTask(harness, CreateSizedSourceFile("first.7z", 16 * 1024));
            ArchiveTask second = AddTask(harness, CreateSizedSourceFile("second.7z", 16 * 1024));

            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, first.Status);
            Assert.Equal(StatusText.DiskSpaceInsufficient, second.Status);

            // 文案里必须有具体数字与建议动作。
            Assert.Contains("磁盘空间不足", second.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("需要", second.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("差", second.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("删除其余物", second.ErrorMessage, StringComparison.Ordinal);

            // 被跳过的任务**没有产物**（一个字节都没写）。
            Assert.False(Directory.Exists(second.OutputPath) &&
                         Directory.GetFiles(second.OutputPath, "*", SearchOption.AllDirectories).Length > 0);

            // 批末必须**点名**报告，绝不静默跳过。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("空间不足，本批跳过 1 个任务", StringComparison.Ordinal));

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("跳过：second.7z", StringComparison.Ordinal));

            // 调度依据也要在日志里（用户要能复核这个判断）。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("空间调度", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 空间门_盘够时照常全跑_并且日志里给出建议并行数()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.MaxParallelExtractCount = 2;
            });

            harness.Coordinator.SpaceProbeOverride = _ => 10L * 1024 * 1024 * 1024;
            harness.Coordinator.SpaceReserveOverride = 0;

            ArchiveTask first = AddTask(harness, CreateSizedSourceFile("first.7z", 4096));
            ArchiveTask second = AddTask(harness, CreateSizedSourceFile("second.7z", 4096));

            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, first.Status);
            Assert.Equal(StatusText.ExtractSuccess, second.Status);

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("建议并行 2 个", StringComparison.Ordinal));
        }

        // ================================================================ ③ 自测协议

        [Fact]
        public async Task 自测_并发1就跑2个文件_通过后写出凭证()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.MaxParallelExtractCount = 1;
            });

            harness.Coordinator.SpaceProbeOverride = _ => 10L * 1024 * 1024 * 1024;
            harness.Coordinator.SpaceReserveOverride = 0;

            ArchiveTask first = AddTask(harness, CreateSourceFile("s1.7z"));
            ArchiveTask second = AddTask(harness, CreateSourceFile("s2.7z"));

            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            DangerModeSelfTestVerdict verdict =
                await harness.Coordinator.RunDangerModeSelfTestAsync(harness.Vm.Tasks.ToList());

            Assert.True(verdict.Passed, verdict.Summary + " | " + string.Join("；", verdict.FailureReasons));
            Assert.Equal(2, verdict.SampleSize);
            Assert.Contains("可以一试，但风险还是有的", verdict.Summary, StringComparison.Ordinal);

            // 两个样本的其余物都必须被真的删掉（这正是要验证的动作）。
            foreach (ArchiveTask task in new[] { first, second })
            {
                Assert.Equal(StatusText.ExtractSuccess, task.Status);
                Assert.True(task.IsOutputVerified);

                Assert.False(
                    Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                    $"{task.FileName} 的其余物应当被彻底删除");
            }

            // 进度与结果都要写进日志（用户明确要求"自测要有进度与结果"）。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("自测进度 1/2", StringComparison.Ordinal));

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("自测进度 2/2", StringComparison.Ordinal));

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("自测结果 ——", StringComparison.Ordinal));

            // 自测本身**不许**改设置（危险模式是临时打开的）。
            Assert.False(harness.Vm.Settings.DangerousSpaceModeEnabled);
        }

        [Fact]
        public async Task 自测_勾选的任务不够时不测_并说清要几个()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.MaxParallelExtractCount = 2;
            });

            AddTask(harness, CreateSourceFile("only-one.7z"));

            DangerModeSelfTestVerdict verdict =
                await harness.Coordinator.RunDangerModeSelfTestAsync(harness.Vm.Tasks.ToList());

            Assert.False(verdict.Passed);
            Assert.Contains("自测需要 4 个", verdict.Summary, StringComparison.Ordinal);
            Assert.Empty(verdict.StepLines);
        }

        [Fact]
        public async Task 自测_源包留在原地时直接拒绝()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.MaxParallelExtractCount = 1;
            });

            AddTask(harness, CreateSourceFile("a.7z"));
            AddTask(harness, CreateSourceFile("b.7z"));

            DangerModeSelfTestVerdict verdict =
                await harness.Coordinator.RunDangerModeSelfTestAsync(harness.Vm.Tasks.ToList());

            Assert.False(verdict.Passed);
            Assert.Contains(
                verdict.FailureReasons,
                reason => reason.Contains("留在原地", StringComparison.Ordinal));
        }

        // ================================================================ ④ 开启流程

        [Fact]
        public async Task 开启_没有通过红色确认时什么都不做()
        {
            Harness harness = CreateHarness();
            AddTask(harness, CreateSourceFile("a.7z"));

            bool enabled = await harness.Vm.EnableDangerModeAsync(confirmed: true, acknowledged: false);

            Assert.False(enabled);
            Assert.False(harness.Vm.Settings.DangerousSpaceModeEnabled);
            Assert.False(DangerModeSelfTestStamp.IsValid(harness.Vm.Settings.DangerModeSelfTestStamp));
            Assert.False(harness.Vm.DangerModeEnabled);
        }

        [Fact]
        public async Task 开启_自测通过后写凭证并落盘()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.MaxParallelExtractCount = 1;
            });

            harness.Coordinator.SpaceProbeOverride = _ => 10L * 1024 * 1024 * 1024;
            harness.Coordinator.SpaceReserveOverride = 0;

            AddTask(harness, CreateSourceFile("s1.7z"));
            AddTask(harness, CreateSourceFile("s2.7z"));

            WireSuccessfulExtraction(harness, "payload.mp4", "内容物");

            bool enabled = await harness.Vm.EnableDangerModeAsync(
                confirmed: true,
                acknowledged: true,
                skipConfirmToEnable: true);

            Assert.True(enabled);
            Assert.True(harness.Vm.Settings.DangerousSpaceModeEnabled);
            Assert.True(DangerModeSelfTestStamp.IsValid(harness.Vm.Settings.DangerModeSelfTestStamp));
            Assert.Equal(1, DangerModeSelfTestStamp.ParseParallelCount(harness.Vm.Settings.DangerModeSelfTestStamp));

            // 凭证必须真的落盘（重启后不能又变成"没自测过"）。
            AppSettings reloaded = new SettingsService(new PathService { DataRootDirectory = harness.DataRoot }).Load();

            Assert.True(reloaded.DangerousSpaceModeEnabled);
            Assert.True(DangerModeSelfTestStamp.IsValid(reloaded.DangerModeSelfTestStamp));
        }

        [Fact]
        public async Task 开启_自测不通过时拒绝开启且设置一个字节都不改()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.MaxParallelExtractCount = 1;
            });

            harness.Coordinator.SpaceProbeOverride = _ => 10L * 1024 * 1024 * 1024;
            harness.Coordinator.SpaceReserveOverride = 0;

            AddTask(harness, CreateSourceFile("s1.7z"));
            AddTask(harness, CreateSourceFile("s2.7z"));

            /*
             * 让引擎解压"成功"但**什么都不写** → 输出校验必然不通过 → 自测必须拒绝。
             * 这正是真机上最难复现、却最该拦住的那类现场。
             */
            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("payload.mp4", 8));
            harness.Engine.OnExtractAsync = _ => Task.FromResult(new ArchiveOperationResult
            {
                Success = true,
                Status = StatusText.ExtractSuccess,
                Message = "解压成功",
                DetectedErrorType = "None"
            });

            bool enabled = await harness.Vm.EnableDangerModeAsync(
                confirmed: true,
                acknowledged: true,
                skipConfirmToEnable: true);

            Assert.False(enabled);
            Assert.False(harness.Vm.Settings.DangerousSpaceModeEnabled);
            Assert.False(DangerModeSelfTestStamp.IsValid(harness.Vm.Settings.DangerModeSelfTestStamp));

            AppSettings reloaded = new SettingsService(new PathService { DataRootDirectory = harness.DataRoot }).Load();

            Assert.False(reloaded.DangerousSpaceModeEnabled);
            Assert.False(DangerModeSelfTestStamp.IsValid(reloaded.DangerModeSelfTestStamp));
        }

        [Fact]
        public async Task 开启_无界面宿主下红色确认恒为否_所以开不起来()
        {
            /*
             * 无 UI 宿主（单元测试 / 控制台宿主）里 DialogService 的降级口径是**拒绝**（fallback: false），
             * 这条就是它的落地验证：不可逆操作的入口在没有人点的情况下必须什么都不做。
             */
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.MaxParallelExtractCount = 1;
            });

            AddTask(harness, CreateSourceFile("a.7z"));
            AddTask(harness, CreateSourceFile("b.7z"));

            harness.Vm.ToggleDangerModeCommand.Execute(null);

            // 命令是异步的（AsyncRelayCommand）：给它一点时间跑完"确认 → 拒绝"这条最短路径。
            await WaitAsync(() => harness.Log.Logs.Any(
                item => item.Message.Contains("没有通过红色的二次确认", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10),
                "没有等到『没有通过红色确认』这条日志");

            Assert.False(harness.Vm.Settings.DangerousSpaceModeEnabled);
            Assert.False(harness.Vm.DangerModeEnabled);
        }

        // ================================================================ ⑤ 同名包并发

        [Fact]
        public async Task 同名包并发_两个都要成功_第二个自动改成_名字1()
        {
            /*
             * 同一目录里 `pipe.rar` 与 `pipe.zip` 的**包基名都是 `pipe`**，默认落点都是 `<out>\pipe`。
             * 串行时第二个会看到目录已存在、自动落成 `pipe(1)`；并发时两个任务会在同一瞬间
             * 认定"这个目录还不存在"，于是撞进同一个目录 —— 第二个的定稿可能整体失败。
             *
             * 并发默认值已经提到 4（2026-09-22 实测），所以这条路是**默认配置**下的常态，
             * 必须钉住"两个都成功、第二个落成 pipe(1)"，而不是"第二个解压失败"。
             */
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.MaxParallelExtractCount = 2;
            });

            harness.Coordinator.SpaceProbeOverride = _ => 10L * 1024 * 1024 * 1024;
            harness.Coordinator.SpaceReserveOverride = 0;

            ArchiveTask rarTask = AddTask(harness, CreateSourceFile("pipe.rar"));
            ArchiveTask zipTask = AddTask(harness, CreateSourceFile("pipe.zip"));

            WireSuccessfulExtraction(harness, "content.txt", "x");

            await harness.Coordinator.StartExtractAsync();

            Assert.True(
                rarTask.Status == StatusText.ExtractSuccess && zipTask.Status == StatusText.ExtractSuccess,
                $"两个同名包都必须成功：pipe.rar={rarTask.Status}（{rarTask.ErrorMessage}）；" +
                $"pipe.zip={zipTask.Status}（{zipTask.ErrorMessage}）");

            // 两个产物目录都在，第二个是自动改名的 pipe(1)。
            Assert.True(Directory.Exists(rarTask.OutputPath), rarTask.OutputPath);
            Assert.True(Directory.Exists(zipTask.OutputPath), zipTask.OutputPath);
            Assert.NotEqual(rarTask.OutputPath, zipTask.OutputPath, StringComparer.OrdinalIgnoreCase);

            // 命名规则与既有的同名冲突档一致：`名字(1)`（用户可见的约定，不能自创一套）。
            Assert.EndsWith("pipe(1)", zipTask.OutputPath, StringComparison.OrdinalIgnoreCase);

            // 两个目录各有自己的一份内容物（绝不合并、绝不覆盖）。
            Assert.True(File.Exists(Path.Combine(rarTask.OutputPath, "content.txt")));
            Assert.True(File.Exists(Path.Combine(zipTask.OutputPath, "content.txt")));

            // 落点被改这件事不许静默：日志里要有一句。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("同一个名字的包正在并行解压", StringComparison.Ordinal));
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                LogService log,
                string dataRoot)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
                DataRoot = dataRoot;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string DataRoot { get; }
        }

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

            // 默认固定"留在原地"（与其它管线测试一致）：这一组要断言"源包到底动没动"，
            // 由每条用例自己改成它需要的那一档，免得默认值把断言搅浑。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            configure?.Invoke(settings);
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

            return new Harness(vm, engine, coordinator, logService, dataRoot);
        }

        private ArchiveTask AddTask(Harness harness, string sourcePath)
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

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");

            return path;
        }

        private string CreateSizedSourceFile(string fileName, int size)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);

            using FileStream stream = File.Create(path);
            stream.SetLength(size);

            return path;
        }

        private static string ValidStamp(int parallel, int sample)
        {
            return DangerModeSelfTestStamp.Create(
                new DangerModeSelfTestVerdict { Passed = true, ParallelCount = parallel, SampleSize = sample },
                DateTime.Now);
        }

        private static void WireSuccessfulExtraction(Harness harness, string contentFileName, string content)
        {
            int size = System.Text.Encoding.UTF8.GetByteCount(content);

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

        private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.Fail(failureMessage);
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
