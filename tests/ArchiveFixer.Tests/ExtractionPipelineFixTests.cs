using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 解压管线（<see cref="ExtractionCoordinator"/>）的五个修复点的回归测试。
    ///
    /// 全部走**真 MainViewModel + 真解压管线**，只把归档引擎换成可控的假引擎：
    /// 这样能精确摆出"大产物目录"、"密码候选几百条"、"收尾时按下取消"这些现场，
    /// 而不用去读用户机器上的任何真实文件（AGENTS.md §8 隐私红线）。
    ///
    /// 和 InnerLayerContinuationTests 同一组：MainViewModel 的构造会写进程级静态
    /// （7z 路径 / 递归工作区根目录），必须和其他测试集串行，并在装配后立刻还原。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ExtractionPipelineFixTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root;

        public ExtractionPipelineFixTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerExtractFix", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

        // ================================================================ P0-1：收尾不许占住 UI 线程

        /// <summary>
        /// 解压收尾（全目录枚举 / 二次遍历 / 归集移动 / 删除源包）以前整段同步跑在 UI 线程上，
        /// 780MB 的包解出几千个文件时窗口完全无响应。
        ///
        /// 这里用"专用单线程 + 消息泵"模拟 WPF 的 UI 线程：管线跑在这条线程上，
        /// 心跳回调也只能在这条线程空闲时被处理。
        ///
        /// 判据刻意用**收尾窗口**（假引擎的收尾列目录 → 管线结束）内的最长心跳间隔，与窗口本身比较：
        /// · 收尾在后台线程（现在的实现） → 窗口内 UI 线程照常跳心跳，最长间隔远小于窗口；
        /// · 收尾在 UI 线程（旧实现）     → 整个收尾窗口内一次心跳都跑不了，最长间隔≈窗口，断言必红
        ///   （实测：把 Task.Run 去掉后最长间隔 ≈ 窗口的 9 成）。
        ///
        /// 不用"整条管线耗时"作分母：那一大截是**测试自己造文件**的时间，
        /// 与被测代码无关，用它会把阈值放得太松（第一版就是这样，负向对照没抓住旧实现）。
        /// </summary>
        [Fact]
        public async Task 大目录收尾时_UI线程没有被长活占住()
        {
            const int fileCount = 4000;

            string collectRoot = Path.Combine(_root, "collect");

            Harness harness = CreateHarness(configure: settings =>
            {
                settings.CollectResultsToDirectory = true;
                settings.CollectTargetDirectory = collectRoot;
            });

            ArchiveTask task = AddTask(harness, CreateSourceFile("big.7z"));

            // 假引擎 = 真实引擎的替身：在**后台线程**里写出几千个产物文件（真实 7z 也是这样，不在 UI 线程写盘）。
            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, fileCount);
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            var pump = new MessagePumpContext();
            long postProcessStartTicks = 0;
            long maxGapDuringPostProcessTicks = 0;
            long lastPulseTicks = Stopwatch.GetTimestamp();
            long pipelineEndTicks = 0;
            int pulsesDuringPostProcess = 0;
            Task? pipeline = null;

            // 收尾的第一次列目录 = 收尾窗口的起点（这一刻起，后面就是校验/归集/清理）。
            harness.Engine.OnListAsync = _ =>
            {
                if (harness.Engine.Extracted)
                {
                    Interlocked.CompareExchange(ref postProcessStartTicks, Stopwatch.GetTimestamp(), 0);
                }

                return Task.FromResult(ListResult(fileCount));
            };

            var uiThread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(pump);

                void Pulse(object? _)
                {
                    long now = Stopwatch.GetTimestamp();

                    if (postProcessStartTicks != 0)
                    {
                        long gap = now - lastPulseTicks;

                        if (gap > maxGapDuringPostProcessTicks)
                        {
                            maxGapDuringPostProcessTicks = gap;
                        }

                        pulsesDuringPostProcess++;
                    }

                    lastPulseTicks = now;

                    // 处理完就重新排队：只要 UI 线程有空，心跳就一直在跳。
                    pump.Post(Pulse, null);
                }

                pump.Post(Pulse, null);

                pipeline = harness.Coordinator.StartExtractAsync();

                pump.RunUntil(() => pipeline!.IsCompleted);

                pipelineEndTicks = Stopwatch.GetTimestamp();
            });

            uiThread.Start();

            Assert.True(uiThread.Join(TimeSpan.FromMinutes(3)), "解压管线没有在 3 分钟内结束");

            Assert.NotNull(pipeline);
            await pipeline!;

            double toMs = 1000.0 / Stopwatch.Frequency;
            double postProcessMs = (pipelineEndTicks - postProcessStartTicks) * toMs;
            double maxGapMs = maxGapDuringPostProcessTicks * toMs;

            _output.WriteLine(
                $"收尾窗口 {postProcessMs:F0} ms，窗口内心跳 {pulsesDuringPostProcess} 次，最长间隔 {maxGapMs:F0} ms（任务 {task.Status}）");

            // 先证明"重活真的干了"，否则这条测试证明不了任何事：
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(fileCount, CountFiles(collectRoot));   // 归集把 4000 个文件搬走了
            Assert.Equal(0, CountFiles(task.OutputPath));       // 产物目录已经搬空

            /*
             * 阶段化（入仓 → 定稿）之后，落点的口径变了，这里改成三条一起断言：
             * ① 引擎写盘的地方是**暂存目录**（工作区里），不是最终目录；
             * ② 任务的 OutputPath 仍然是**最终目录**（界面"输出目录"列、续解都读它）；
             * ③ 归集把最终目录整个搬到了归集目标下（所以最终目录现在是空的，不是没产物）。
             */
            Assert.StartsWith(
                harness.PathService.WorkDirectory,
                harness.Engine.LastExtractOutputPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(_root, "out", "big"), task.OutputPath);
            Assert.StartsWith(collectRoot, task.CollectedPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(task.IsOutputVerified, "产物数量与清单一致，校验应该通过");

            Assert.True(postProcessStartTicks != 0, "没有观察到收尾窗口的起点");
            Assert.True(postProcessMs > 50, $"收尾阶段只花了 {postProcessMs:F0} ms，这条测试证明不了什么");
            Assert.True(pulsesDuringPostProcess > 10, $"收尾期间心跳只跳了 {pulsesDuringPostProcess} 次");

            Assert.True(
                maxGapMs < postProcessMs / 3.0,
                $"UI 线程在收尾期间被占住了：最长无响应 {maxGapMs:F0} ms，收尾窗口共 {postProcessMs:F0} ms");
        }

        // ================================================================ P1-3：取消之后不移动、不清理

        /// <summary>
        /// 收尾阶段以前从不检查取消令牌：用户按了「取消当前」，产物照样被移动、源包照样被删。
        /// 现在每一步之前都查令牌，且状态必须落成"已取消"（不得显示成功）。
        /// </summary>
        [Fact]
        public async Task 收尾时按下取消_不移动产物_不删源包_状态是已取消()
        {
            string collectRoot = Path.Combine(_root, "collect");

            Harness harness = CreateHarness(configure: settings =>
            {
                settings.CollectResultsToDirectory = true;
                settings.CollectTargetDirectory = collectRoot;

                // 开着"会动源包"的那两档才有意义：取消之后一个源文件都不许少。
                // （第 32 条之后就是这两档组合：源包放进其余物 + 其余物彻底删除。）
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = CreateSourceFile("cancel.7z");
            ArchiveTask task = AddTask(harness, source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 5);
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            // 收尾第一件事就是列目录；在这里按下「取消当前」——正是旧实现漏检的那个窗口。
            harness.Engine.OnListAsync = _ =>
            {
                if (harness.Engine.Extracted)
                {
                    harness.Coordinator.CancelCurrentTask();
                }

                return Task.FromResult(ListResult(5));
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);

            // 源包一个都不许删（AGENTS.md §9.5：取消时一律不删）。
            Assert.True(File.Exists(source), "取消之后源包被删了");

            /*
             * 产物一个都不许移动。
             * 入仓之后产物在**暂存目录**里，而默认档（用户 2026-09-25 第 25 条追加："失败了就失败了"）
             * 会把这一单没成功的工作区**整份清掉** —— 所以取消之后暂存目录连同它的产物都不在了。
             * 要害仍然钉死两条：① 最终目录**连建都不该建**（定稿没跑，契约 §6 第 2 条）；
             * ② 归集目录一个字节都没有（产物绝没有被搬走）。
             */
            Assert.False(Directory.Exists(collectRoot), "取消之后产物被归集走了");
            Assert.False(
                Directory.Exists(TaskStageDirectory(harness, task)),
                "默认档下取消之后暂存目录该被整份清掉");
            Assert.False(Directory.Exists(task.OutputPath), $"取消之后最终目录被建出来了：{task.OutputPath}");
            Assert.Equal(string.Empty, task.CollectedPath);
        }

        // ================================================================ P0-2：密码错误只提示一次

        /// <summary>
        /// 旧实现：每个任务内部各弹一次模态框（50 个错包 = 50 次阻塞点击），而且用的是同步 Dispatcher.Invoke。
        /// 新实现：循环里只登记，批次结束后合并成**一次**提示；日志与弹窗同源。
        ///
        /// 说明：单元测试里没有 WPF Application（下面显式断言这一点），
        /// 所以"弹窗"这一步退化成只写日志 —— 而日志正是可断言的证据：
        /// 只允许出现**一条**"本批 N 个包没能解开密码"，不允许出现按任务重复的提示。
        /// </summary>
        [Fact]
        public async Task 一批密码错误_只合并提示一次()
        {
            Assert.Null(System.Windows.Application.Current);   // 前提：测试进程里没有 WPF 应用

            Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2" });

            for (int i = 1; i <= 3; i++)
            {
                AddTask(harness, CreateSourceFile($"wrong-{i}.7z"));
            }

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.All(harness.Vm.Tasks, t => Assert.Equal(StatusText.WrongPassword, t.Status));

            string[] aggregated = harness.Log.Logs
                .Where(x => x.Message.Contains("个包没能解开", StringComparison.Ordinal))
                .Select(x => x.Message)
                .ToArray();

            Assert.Single(aggregated);
            Assert.Contains("本批 3 个包没能解开", aggregated[0], StringComparison.Ordinal);
            Assert.Contains("原因：密码错误或缺少正确密码", aggregated[0], StringComparison.Ordinal);

            // 脱敏兜底不许把提示吃掉（"密码"后面直接跟冒号会被 PasswordMasker 整行打码）。
            Assert.DoesNotContain("******", aggregated[0], StringComparison.Ordinal);

            foreach (ArchiveTask task in harness.Vm.Tasks)
            {
                Assert.Contains(task.FileName, aggregated[0], StringComparison.Ordinal);
            }
        }

        // ================================================================ P1-4：密码尝试上限

        /// <summary>
        /// 密码候选没有上限时，几百条密码本的包会被逐个候选整包重解一遍（每个候选一次完整解压）。
        /// 现在每层最多试 <c>Settings.MaxPasswordAttemptsPerLayer</c> 个（缺省 10）。
        /// </summary>
        [Fact]
        public async Task 密码候选超过上限时_只试到上限且状态是达到上限而不是密码错误()
        {
            var passwords = Enumerable.Range(1, 40).Select(i => $"候选密码{i}").ToArray();

            Harness harness = CreateHarness(passwords: passwords);
            ArchiveTask task = AddTask(harness, CreateSourceFile("many-passwords.7z"));

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            // 空密码 + 40 条候选 = 41 个，必须被截到上限（缺省 10）。
            Assert.Equal(harness.Vm.Settings.MaxPasswordAttemptsPerLayer, harness.Engine.ExtractCalls.Count);
            Assert.True(harness.Engine.ExtractCalls.Count < passwords.Length + 1, "候选没有被截断");

            Assert.Equal(StatusText.PasswordAttemptLimitReached, task.Status);
            Assert.NotEqual(StatusText.WrongPassword, task.Status);
            Assert.Contains("上限", task.ErrorMessage, StringComparison.Ordinal);
        }

        /// <summary>
        /// 上限是**设置项**，不是写死的常量：用户在设置界面改成 3，就必须只试 3 个候选。
        /// （旧实现里它是 <c>ExtractionCoordinator</c> 的常量，界面改了不生效 —— 等于设置项是摆设。）
        /// </summary>
        [Fact]
        public async Task 密码尝试上限改成设置值后_真的只试那么多个()
        {
            var passwords = Enumerable.Range(1, 40).Select(i => $"候选密码{i}").ToArray();

            Harness harness = CreateHarness(
                passwords: passwords,
                configure: settings => settings.MaxPasswordAttemptsPerLayer = 3);

            ArchiveTask task = AddTask(harness, CreateSourceFile("limit-3.7z"));

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(3, harness.Engine.ExtractCalls.Count);

            // 到上限 ≠ 密码错误（AGENTS.md §9.2）：候选还剩 38 个，不能说"密码本里没有正确密码"。
            Assert.Equal(StatusText.PasswordAttemptLimitReached, task.Status);
            Assert.NotEqual(StatusText.WrongPassword, task.Status);
            Assert.Contains("3 个候选", task.ErrorMessage, StringComparison.Ordinal);
        }

        /// <summary>候选本来就不超过上限时，试完全部仍失败 —— 这时才该报"密码错误"。</summary>
        [Fact]
        public async Task 候选没到上限且全部错误_仍然报密码错误()
        {
            Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2" });
            ArchiveTask task = AddTask(harness, CreateSourceFile("few-passwords.7z"));

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(3, harness.Engine.ExtractCalls.Count);   // 空密码 + 2 条候选，全试过
            Assert.Equal(StatusText.WrongPassword, task.Status);
        }

        /// <summary>
        /// 上限这个设置项也要管到**递归内层**，而且必须是同一个值。
        ///
        /// 真实的坑：候选表按设置截断，而递归核心（<c>RecursionLimits</c>）另有自己的默认值 8。
        /// 用户把上限改成 20 时，如果只有候选表跟着改，递归里仍然在第 8 个候选上停手 ——
        /// 设置项看着生效、实际半生效（而且状态还报"达到密码尝试上限"，让人以为已经试到底了）。
        /// 这里用 20 做钉子：旧接线只会有 8 次解压调用。
        /// </summary>
        [Fact]
        public async Task 递归内层也按设置里的密码尝试上限停手()
        {
            var passwords = Enumerable.Range(1, 40).Select(i => $"候选密码{i}").ToArray();

            Harness harness = CreateHarness(
                passwords: passwords,
                configure: settings =>
                {
                    settings.RecursionMode = "SingleChain";
                    settings.MaxPasswordAttemptsPerLayer = 20;
                });

            ArchiveTask task = AddTask(harness, CreateSourceFile("recursive-limit.7z"));

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            // 递归的工作区根目录是进程级静态（见 CreateHarness 的说明），用完必须还原。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = Path.Combine(_root, "work");

                await harness.Coordinator.StartExtractAsync();
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            }

            // 空密码 + 40 条候选，上限 20 → 恰好 20 次解压尝试（旧接线是 8 次）。
            Assert.Equal(20, harness.Engine.ExtractCalls.Count);

            // 一个候选都没成功：绝不能显示成功（不变量 6），最多"部分完成"。
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
        }

        /// <summary>
        /// 安全上限也是**设置项**，而且必须真的管到解压管线（用户 2026-09-25 第 36 条）。
        ///
        /// <para>真机现场：他那个 12.22 GiB 的包里有 4.88 GiB 的单条目，而单文件上限当时是**硬编码**的
        /// 4 GiB —— 界面上一个字都没有，他看到的只有失败清单上那句像"这个包坏了"的判决。</para>
        ///
        /// <para>这条钉子把上限调到 1 GiB：同一个清单必须**当场**被拒、引擎一次都不许调，
        /// 而且任务上的原因要点名"安全上限"并带上调大的入口（撤掉接线立刻变红：
        /// 预检退回 <c>new ResourceBudget()</c> 的 4 GiB 默认值时，1.0001 GiB 的条目会被放行、引擎被调用）。</para>
        /// </summary>
        [Fact]
        public async Task 安全上限来自设置_调小之后当场拒绝且不调引擎()
        {
            Harness harness = CreateHarness(configure: settings => settings.MaxSingleExtractedFileGiB = 1);

            ArchiveTask task = AddTask(harness, CreateSourceFile("big-parts.7z"));

            long entrySize = (1L * 1024 * 1024 * 1024) + (1L * 1024 * 1024);

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = entrySize,
                Entries = new List<ArchiveEntry>
                {
                    // 名字刻意用他真机那个"括号没配对上"的卷名：文案里必须原样带出来（外面加「」）。
                    new() { Path = "Code Complete-BZ.7z(删掉.001", Size = entrySize }
                },
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            await harness.Coordinator.StartExtractAsync();

            Assert.Empty(harness.Engine.ExtractCalls);
            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.Contains("安全上限", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("条目「Code Complete-BZ.7z(删掉.001」", task.ErrorMessage, StringComparison.Ordinal);
        }

        /// <summary>
        /// 「一组分卷的第一卷名字被改坏」时必须判「分卷缺失」，**不是**解出一个等大的垃圾文件还显示成功
        /// （用户 2026-09-25 第 36 条追加；真 7z 的这条行为由 <c>RawSplitStreamTests</c> 固化）。
        ///
        /// <para>真机链条：7-Zip 顺着名字找不到同组的后续卷 → 退化成通用分片 → 把这一卷**照解不误**（退出码 0）
        /// → 解出来一个与它等大的垃圾文件 → 结果校验拿"清单那一条"比"盘上那一个文件"会判**通过**
        /// → 用户拿到垃圾却看到「成功」。所以管线要在**写盘之前**拦下：引擎一次都不许调。</para>
        /// </summary>
        [Fact]
        public async Task 第一卷名字被改坏时_判分卷缺失而不是解出一个垃圾文件()
        {
            Harness harness = CreateHarness();

            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string firstVolume = Path.Combine(directory, "Code Complete-BZ.7z(删掉.001");

            File.WriteAllText(firstVolume, "not a real archive - the engine is faked in these tests");

            // 同目录里像后续卷的那两个（真机上它们就躺在旁边，只是名字对不上）
            File.WriteAllText(Path.Combine(directory, "Code Complete-BZ.7z.002"), "x");
            File.WriteAllText(Path.Combine(directory, "Code Complete-BZ.7z.003"), "x");

            ArchiveTask task = AddTask(harness, firstVolume);

            long size = new FileInfo(firstVolume).Length;

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = size,
                Entries = new List<ArchiveEntry> { new() { Path = "Code Complete-BZ.7z(删掉", Size = size } },
                IsRawSplitStream = true,
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            await harness.Coordinator.StartExtractAsync();

            Assert.True(File.Exists(Path.Combine(directory, "Code Complete-BZ.7z.001")), "应该自动改回标准名");
            Assert.False(File.Exists(firstVolume), "旧名字该没了");
            Assert.NotEqual(StatusText.VolumeMissing, task.Status);
        }

        /// <summary>
        /// **先试密码，再解整包**（用户 2026-09-25 第 37 条，他原话："你应该先试密码再进行解压"）。
        ///
        /// <para>真机代价：那个 12.22 GiB 的包用**空密码**跑了 10 分 18 秒才报密码错误（还把 12 GiB 垃圾写进工作区）。
        /// 这条钉子把一个"名字可见 + 条目加密"的大包喂给管线，断言：</para>
        /// <list type="bullet">
        /// <item><description>错密码**只解探针那一个条目**（`IncludeEntries` 非空），**一次整包都不解**；</description></item>
        /// <item><description>对密码才走到整包解压；</description></item>
        /// <item><description>「空密码」这一档**根本不试**（加密包不可能用空密码加密）。</description></item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task 加密大包_错密码只解一个小条目_绝不去解整包()
        {
            Harness harness = CreateHarness(passwords: new[] { "错误密码", "正确密码" });

            // 源文件要有一点体积：预检里的"展开比"是 解压后 ÷ 压缩包体积，
            // 55 字节的假源文件配 100 MiB 的声明显然会被判成压缩炸弹（那是另一条闸门，与本用例无关）。
            string sourcePath = Path.Combine(_root, "src", "big-encrypted.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, new byte[1024 * 1024]);

            ArchiveTask task = AddTask(harness, sourcePath);

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 100L * 1024 * 1024,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "big.bin", Size = 100L * 1024 * 1024 },
                    new() { Path = "readme.txt", Size = 1024 }
                },
                IsEncrypted = true,
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            int probeCalls = 0;
            int fullExtractCalls = 0;
            var triedCandidates = new List<string>();

            harness.Engine.OnExtractWithOptionsAsync = (request, options) =>
            {
                triedCandidates.Add(request.Password ?? string.Empty);

                if (options.IncludeEntries.Count > 0)
                {
                    probeCalls++;

                    Assert.Equal("readme.txt", options.IncludeEntries[0]);

                    return Task.FromResult(
                        request.Password == "正确密码"
                            ? Succeeded()
                            : new ArchiveOperationResult
                            {
                                Success = false,
                                Status = StatusText.WrongPassword,
                                Message = "密码错误",
                                DetectedErrorType = "WrongPassword"
                            });
                }

                fullExtractCalls++;
                WriteFiles(request.OutputPath!, 2);
                harness.Engine.Extracted = true;
                return Task.FromResult(Succeeded());
            };

            await harness.Coordinator.StartExtractAsync();

            _output.WriteLine($"诊断：状态={task.Status} 原因={task.ErrorMessage} 候选={string.Join("|", triedCandidates)} ExtractCalls={harness.Engine.ExtractCalls.Count}");

            // 空密码那一档根本没被试过（加密包）。
            Assert.DoesNotContain(string.Empty, triedCandidates);

            // 两个非空候选各探一次；整包只解了一次（对的那个）。
            Assert.Equal(2, probeCalls);
            Assert.Equal(1, fullExtractCalls);
        }

        // ================================================================ P1-6：实际输出目录写回任务


        /// <summary>
        /// 输出目录已存在且非空时会自动改用 <c>xxx(1)</c>。
        /// 旧行为只把这件事写进日志，任务对象上的 <see cref="ArchiveTask.OutputPath"/> 是"打算输出到哪"；
        /// 一键处理的续解就按解压前的目录快照找内层包，于是第二层静默不跑，汇总却还写"完成"。
        ///
        /// 现在：实际落点写回 <see cref="ArchiveTask.OutputPath"/>（界面"输出目录"列直接看得见），
        /// 校验结论里带一句 <see cref="ArchiveTask.VerifyMessage"/>，日志里也明说一次。
        /// </summary>
        [Fact]
        public async Task 输出目录冲突自动改名_实际落点写回任务且看得见()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string requested = harness.PathService.BuildOutputPath(task, DefaultOptions(harness));

            // 现场：目标目录已存在且非空（重跑一次、上次失败留下的目录都会这样）。
            Directory.CreateDirectory(requested);
            File.WriteAllText(Path.Combine(requested, "上次留下的旧文件.txt"), "old");

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 2);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(2));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            /*
             * ① 写回的是**实际**落点（用户看得见的那个目录），不是解压前算出来的那个。
             * 入仓之后引擎写的是暂存目录，产物由定稿搬进最终目录 —— 所以这两件事要分开断言：
             * 引擎的落点在工作区里，任务的落点是改名后的 out\pack(1)。
             */
            Assert.NotEqual(requested, task.OutputPath);
            Assert.True(Directory.Exists(task.OutputPath));
            Assert.Equal(2, CountFiles(task.OutputPath));
            Assert.StartsWith(
                harness.PathService.WorkDirectory,
                harness.Engine.LastExtractOutputPath,
                StringComparison.OrdinalIgnoreCase);

            // ② 旧目录里的东西一个都没动（绝不覆盖/清空既有文件）。
            Assert.True(File.Exists(Path.Combine(requested, "上次留下的旧文件.txt")));

            // ③ "输出到别处"这件事在任务与日志里都看得见，不许静默。
            Assert.Contains("实际输出到", task.VerifyMessage, StringComparison.Ordinal);
            Assert.Contains(
                harness.Log.Logs.Select(x => x.Message),
                message => message.Contains("本次实际输出目录", StringComparison.Ordinal) &&
                           message.Contains(task.OutputPath, StringComparison.OrdinalIgnoreCase));
        }

        // ================================================================ 停止后续：不许再启动新任务

        /// <summary>
        /// 实测复现的缺陷：「停止后续」的 <c>break</c> 只跳出了"等并发位"的 while，
        /// 之后照样 <c>runningTasks.Add(...)</c> 把当前任务启动起来 ——
        /// 用户点完停止，另一个包还是跑成功了（违反不变量 9）。
        ///
        /// 现场：并发 2、3 个任务。前两个占满并发位，第 3 个在"等位"时用户按下停止后续，
        /// 然后让第 1 个结束 —— 第 3 个**一个字节都不许动**。
        /// </summary>
        [Fact]
        public async Task 等并发位时点了停止后续_不再启动新任务()
        {
            Harness harness = CreateHarness(configure: settings => settings.MaxParallelExtractCount = 2);

            ArchiveTask first = AddTask(harness, CreateSourceFile("stop-1.7z"));
            ArchiveTask second = AddTask(harness, CreateSourceFile("stop-2.7z"));
            ArchiveTask third = AddTask(harness, CreateSourceFile("stop-3.7z"));

            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            harness.Engine.OnExtractAsync = request =>
            {
                if (string.Equals(request.ArchivePath, first.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    firstStarted.TrySetResult(true);
                    return releaseFirst.Task.ContinueWith(_ => Succeeded(), TaskScheduler.Default);
                }

                if (string.Equals(request.ArchivePath, second.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    secondStarted.TrySetResult(true);

                    // 第 2 个任务一直不结束：让第 3 个卡在"等并发位"上。
                    return new TaskCompletionSource<ArchiveOperationResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously).Task;
                }

                // 任何其它解压调用（尤其是第 3 个任务）都记下来。
                return Task.FromResult(Succeeded());
            };

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(0));

            Task pipeline = harness.Coordinator.StartExtractAsync();

            await WaitAsync(() => firstStarted.Task.IsCompleted && secondStarted.Task.IsCompleted, TimeSpan.FromSeconds(10), "前两个任务没有起来");

            // 用户按下「停止后续」，然后第 1 个任务结束 → 第 3 个从"等位"里出来。
            harness.Coordinator.StopAfterCurrent();
            releaseFirst.TrySetResult(true);

            // 等到"停止后续"这件事被循环处理掉（旧实现在这里会先去启动第 3 个任务）。
            //
            // ⚠ 2026-09-27 起这条日志的措辞统一成一句（StatusText.StopRequestedNotice）：
            // 原来是"已请求停止后续任务，不再启动新的解压任务。"，现在是
            // "已按「停止后续」停下：不再启动新任务；…在下一个安全点停下…"。
            await WaitAsync(
                () => harness.Engine.ExtractCalls.Any(p => string.Equals(p, third.CurrentPath, StringComparison.OrdinalIgnoreCase)) ||
                      harness.Log.Logs.Any(x => x.Message.Contains("已按「停止后续」停下", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10),
                "停止后续没有被处理");

            Assert.DoesNotContain(
                harness.Engine.ExtractCalls,
                p => string.Equals(p, third.CurrentPath, StringComparison.OrdinalIgnoreCase));

            Assert.NotEqual(StatusText.ExtractSuccess, third.Status);
            Assert.NotEqual(StatusText.Extracting, third.Status);

            _ = pipeline;   // 第 2 个任务故意不结束，管线会一直挂着；测试不 await 它。
        }

        // ================================================================ 不变量 4：单层落点越界 = 失败结论
        /// <summary>
        /// 单层解压的"解压后落点校验"以前只写一行 ERROR 日志：任务照样是**解压成功**，
        /// 产物照样被归集走、源包照样被删 —— 越界的产物已经落在目标根之外，程序却报一切正常，
        /// 等于把不变量 4 降级成一条没人看的提示。
        ///
        /// 现场：产物目录里有个**指向目录外的目录联接点**（名字干干净净，第一道"条目名预检"看不出问题，
        /// 真实落点却在外面）—— 这正是第二道落点校验存在的理由。用联接点而不是符号链接：
        /// 前者（mklink /J）不需要管理员权限。
        ///
        /// 判据（三条一起才说明口径改对了）：①不是成功状态 ②没有归集 ③没有清理源包。
        /// 这里刻意让结果校验**通过**（产物数量对得上）——否则"跳过归集/清理"是校验失败的副作用，
        /// 测不到本次修的那条路。
        /// </summary>
        [Fact]
        public async Task 单层产物越出目标根目录_不算成功且不归集不清理()
        {
            string collectRoot = Path.Combine(_root, "collect");

            Harness harness = CreateHarness(configure: settings =>
            {
                settings.CollectResultsToDirectory = true;
                settings.CollectTargetDirectory = collectRoot;

                // 开着"会动源包"的那两档才有意义：越界结论下源文件一个都不许少。
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = CreateSourceFile("escape.7z");
            ArchiveTask task = AddTask(harness, source);

            string outside = Path.Combine(_root, "outside");

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                string output = request.OutputPath ?? string.Empty;
                harness.Engine.LastExtractOutputPath = output;

                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "innocent.txt"), "正常产物");

                Directory.CreateDirectory(outside);

                string link = Path.Combine(output, "link-out");

                if (!Directory.Exists(link))
                {
                    CreateJunction(link, outside);
                }

                File.WriteAllText(Path.Combine(link, "escaped.txt"), "越界产物");

                harness.Engine.Extracted = true;
                return Succeeded();
            });

            // 清单说 1 个文件 → 结果校验会通过（innocent.txt 1 个，联接点里的那份按"不跟随链接"不计）。
            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            // ① 结论：不是成功，而是失败，且原因写在任务上（不是只躺在日志里）。
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.Contains("越出目标根目录", task.ErrorMessage, StringComparison.Ordinal);
            Assert.False(task.IsOutputVerified, "结论不成立时不许显示「输出校验通过」");

            // 日志里也要有证据（无界面宿主时日志是唯一线索），而且不许再出现"解压成功：xxx"。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("越出目标根目录", StringComparison.Ordinal));
            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains("解压成功：", StringComparison.Ordinal));

            // ② 不清理源包（AGENTS.md §9.5：校验失败/结论不成立一律不删）。
            Assert.True(File.Exists(source), "越界结论下源包被删了");

            // ③ 不归集：目标目录连建都不该建，产物留在原地。
            Assert.False(Directory.Exists(collectRoot), "越界结论下产物被归集走了");
            Assert.Equal(string.Empty, task.CollectedPath);

            /*
             * 默认档（用户 2026-09-25 第 25 条追加）：这一单没成功 → 工作区整份清掉。
             * ⚠ 这一条用例的工作区里有一个**目录联接点**（那正是越界的来源）：Windows 会拒绝递归删除它，
             * 于是走"删不掉"那一条路 —— 那时**必须**留一条 WARN 说清"没清掉 + 路径"，
             * 绝不许假装清干净了（这条同时钉住"一次清理失败绝不改任务结论"）。
             * 要害始终是"定稿一步都没跑"：最终目录连建都不该建、归集目录一个字节都没有、
             * 越界落点的那个目录也没被我们删。
             */
            string taskWorkDirectory = TaskWorkDirectory(harness, task);

            if (Directory.Exists(taskWorkDirectory))
            {
                Assert.Contains(
                    harness.Log.Logs,
                    x => x.Level == "WARN" &&
                         x.Message.Contains("工作区没清掉", StringComparison.Ordinal) &&
                         x.Message.Contains(taskWorkDirectory, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Assert.False(
                    Directory.Exists(TaskStageDirectory(harness, task)),
                    "工作区都清掉了，暂存目录不该还在");
            }

            Assert.False(Directory.Exists(task.OutputPath), $"越界结论下最终目录被建出来了：{task.OutputPath}");
            Assert.True(Directory.Exists(outside), "越界落点的目录本身不该被我们删掉");
        }

        // ================================================================ 中间工作区清理（成功后不留 1 GB 垃圾）

        /// <summary>
        /// 双面文件（内嵌归档）要先按偏移把尾部那段 ZIP 抠进 <c>work\&lt;任务名&gt;\</c> 再解压。
        /// 端到端验收实测：一次**成功**的一键处理在那里留下近 1 GB 过程物（733 MB + 167 MB）。
        /// 这些是从源文件可再生的派生数据 —— 成功且校验通过后必须清掉。
        /// </summary>
        [Fact]
        public async Task 成功解压且校验通过后_本任务的中间工作区被清理()
        {
            Harness harness = CreateHarness();
            string source = CreateEmbeddedSourceFile("embedded-ok.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 1);
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 抠包是真的发生了（否则这条测试证明不了任何事）：引擎拿到的是工作区里那个抠出来的文件。
            Assert.StartsWith(
                harness.PathService.WorkDirectory,
                harness.Engine.ExtractCalls.Single(),
                StringComparison.OrdinalIgnoreCase);

            Assert.False(Directory.Exists(taskWorkDirectory), $"成功之后中间工作区还在：{taskWorkDirectory}");

            // 源文件一个字节都不许动（AGENTS.md 不变量 1）。
            Assert.True(File.Exists(source), "清理中间工作区时把源文件删了");

            // 删什么、为什么，日志里必须留得下（不可逆操作要留证据）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("清理本任务的中间工作区", StringComparison.Ordinal) &&
                     x.Message.Contains("校验通过", StringComparison.Ordinal));
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("中间工作区已清理", StringComparison.Ordinal));

            /*
             * 成功这一支**不是**靠"失败不留残留"那条新逻辑清的（用户 2026-09-25 第 25 条追加）：
             * 那一条的日志是「已清理工作区…（这次没成功…）」，这里一条都不许出现 ——
             * 否则"成功路径的清理口径一个字不改"就成了空话，两套清理也会互相掩盖。
             */
            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains("已清理工作区：", StringComparison.Ordinal));
        }

        /// <summary>
        /// 取消：中间工作区默认**整份清掉**（用户 2026-09-25 第 25 条追加）。
        ///
        /// <para>旧口径是"取消时过程物是用户唯一还能看的东西，一律留着"（2026-09-24 第 23 条）。
        /// 用户后来说得很直接："失败了就失败了，成功了就成功了"+"我不希望有这么多的失败残留"，
        /// 而且失败清单已经能说清每个包为什么失败 —— 所以默认档改成不留现场；
        /// 要看现场的人在 ③ 页打开「失败时保留中间产物」（那一条有独立用例钉住）。</para>
        /// </summary>
        [Fact]
        public async Task 取消后_中间工作区默认被清掉()
        {
            Harness harness = CreateHarness();
            string source = CreateEmbeddedSourceFile("embedded-cancel.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 5);
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            // 收尾第一件事就是列目录：在这里按下「取消当前」。
            harness.Engine.OnListAsync = _ =>
            {
                if (harness.Engine.Extracted)
                {
                    harness.Coordinator.CancelCurrentTask();
                }

                return Task.FromResult(ListResult(5));
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"取消之后中间工作区还在：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "取消时源文件必须原样保留");

            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("已清理工作区：", StringComparison.Ordinal) &&
                     x.Message.Contains("这次没成功", StringComparison.Ordinal));
        }

        /// <summary>失败（密码不对）：默认同样整份清掉 —— 用户要的是"失败了就失败了"。</summary>
        [Fact]
        public async Task 解压失败后_中间工作区默认被清掉()
        {
            Harness harness = CreateHarness();
            string source = CreateEmbeddedSourceFile("embedded-fail.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"解压失败之后中间工作区还在：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "失败时源文件必须原样保留");

            // 删了什么要说清：几个文件 / 多大 / 怎么改主意（去 ③ 页打开保留开关）。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("已清理工作区：", StringComparison.Ordinal) &&
                     x.Message.Contains("失败时保留中间产物", StringComparison.Ordinal));
        }

        // ================================================================ 递归路径的工作区（data\work\recursive）

        /// <summary>
        /// 递归模式（<c>RecursionMode != SingleLayer</c>）的这一路同样不许漏垃圾，而且**有两处**：
        /// ① 递归核心自己的逐层工作区（<c>data\work\recursive\&lt;taskId&gt;\</c>，动辄几百 MB）；
        /// ② 双面文件抠出来的过程物所在的 <c>work\&lt;任务名&gt;\</c>。
        /// 递归成功后两处都必须清掉 —— 否则"解压成功"就是一次几百 MB 的泄漏。
        /// </summary>
        [Fact]
        public async Task 递归成功且产物发布后_抠出来的过程物与递归工作区一起被清理()
        {
            Harness harness = CreateHarness(configure: settings => settings.RecursionMode = "SingleChain");
            string source = CreateEmbeddedSourceFile("embedded-recursive.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);
            string recursiveRoot = Path.Combine(harness.PathService.WorkDirectory, "recursive");

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 1);
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            // 递归工作区根目录是**进程级静态**（见 CreateHarness 的说明），用完必须还原。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = harness.PathService.WorkDirectory;

                await harness.Coordinator.StartExtractAsync();
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            }

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 抠包真的发生了，而且这一单确实走的递归核心（引擎拿到的是工作区里抠出来的那个文件）。
            Assert.StartsWith(
                harness.PathService.WorkDirectory,
                harness.Engine.ExtractCalls.Single(),
                StringComparison.OrdinalIgnoreCase);

            // 产物已经被递归核心发布出去 —— 清理工作区不能连产物一起清掉。
            Assert.True(
                File.Exists(Path.Combine(task.OutputPath, "payload-00000.bin")),
                $"递归产物应当已经发布：{task.OutputPath}");

            Assert.False(Directory.Exists(taskWorkDirectory), $"递归成功之后中间工作区还在：{taskWorkDirectory}");

            // 递归核心自己的逐层工作区也必须清干净（这一条就是"几百 MB 泄漏"的钉子）。
            Assert.Equal(0, CountSubdirectories(recursiveRoot));

            // 源文件一个字节都不许动（AGENTS.md 不变量 1）。
            Assert.True(File.Exists(source), "清理中间工作区时把源文件删了");
        }

        /// <summary>
        /// 递归失败（第一层就没解开）：**两处**工作区默认都要清掉（用户 2026-09-25 第 25 条追加）。
        ///
        /// <para>用户原话：「如果解压 40G，两层，解压失败有 80G 的卸载残留，用户不得气死」——
        /// 双层包失败时留得最多的正是递归核心那份逐层工作区，所以这一条同时钉两处：
        /// ① 抠出来的过程物所在的 <c>work\&lt;任务名&gt;\</c>；② <c>work\recursive\&lt;taskId&gt;\</c>。
        /// 结论仍是"部分完成"（绝不显示成功，不变量 6），源包一个字节不动（不变量 1）。</para>
        /// </summary>
        [Fact]
        public async Task 递归失败后_两处工作区默认都被清掉()
        {
            Harness harness = CreateHarness(configure: settings => settings.RecursionMode = "SingleChain");
            string source = CreateEmbeddedSourceFile("embedded-recursive-fail.7z");

            ArchiveTask task = AddTask(harness, source);
            task.EmbeddedArchiveOffset = EmbeddedPaddingBytes;

            string taskWorkDirectory = TaskWorkDirectory(harness, task);
            string recursiveRoot = Path.Combine(harness.PathService.WorkDirectory, "recursive");

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = harness.PathService.WorkDirectory;

                await harness.Coordinator.StartExtractAsync();
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            }

            // 递归没走完 → 部分完成（绝不显示成功）。
            Assert.Equal(StatusText.PartiallyCompleted, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"递归失败之后中间工作区还在：{taskWorkDirectory}");

            // 递归核心的逐层工作区（几百 MB / 几十 GB 的那一份）也必须清干净。
            Assert.Equal(0, CountSubdirectories(recursiveRoot));

            Assert.True(File.Exists(source), "失败时源文件必须原样保留");
        }

        /// <summary>
        /// 多分支询问里用户选"只保留当前这一层"（无界面宿主下 ShowConfirm 返回 false，正好走这一支）。
        ///
        /// 这条路的结论是在协调器里被**改写成 Completed** 的：递归核心当时以 NeedsDecision 收尾、
        /// 按规则没清工作区；产物被取回暂存目录之后那份工作区就是纯垃圾，
        /// 不清的话每次"只保留当前一层"都会在 <c>data\work\recursive</c> 留一份。
        /// </summary>
        [Fact]
        public async Task 用户选择只保留当前一层_递归工作区随成功一起清理()
        {
            Harness harness = CreateHarness(configure: settings => settings.RecursionMode = "SingleChain");
            string source = CreateSourceFile("multi-branch.7z");

            ArchiveTask task = AddTask(harness, source);

            string recursiveRoot = Path.Combine(harness.PathService.WorkDirectory, "recursive");

            // 第 0 层解出两个"内层归档"（魔数就是 PK 03 04，递归探测只认魔数）→ 触发多分支询问。
            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFakeZipFiles(request.OutputPath!, "inner-a.zip", "inner-b.zip");
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(2));

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = harness.PathService.WorkDirectory;

                await harness.Coordinator.StartExtractAsync();
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            }

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 第 0 层产物被取回并定稿：清理没有连产物一起删。
            // （内层归档按契约是"过程物"，会在输出目录下的「其余物」里，所以按名递归找，
            //   不假设它躺在输出目录根上。）
            Assert.True(
                Directory.Exists(task.OutputPath) &&
                Directory.GetFiles(task.OutputPath, "inner-a.zip", SearchOption.AllDirectories).Length == 1,
                $"第 0 层产物应当被取回并定稿：{task.OutputPath}");

            Assert.Equal(0, CountSubdirectories(recursiveRoot));
        }

        // ================================================================ 2026-09-27 真机：候选循环的三条

        /// <summary>
        /// **包没加密 / 结果不变 → 不许再烧候选，也不许把结论写成「密码错误」**
        /// （用户 2026-09-27 真机：`rar-android-722.132.apk` 用空密码解出 1238 个文件，
        /// 被当成"候选密码不对"，10 个候选白试 5 分钟，最后还报「密码错误」）。
        ///
        /// <para>本用例造的形状 = 引擎说成功、产物**真的有一份**、但比清单少一个文件，
        /// 而且清单里**一个加密条目都没有** → 换密码根本不可能改变结果。</para>
        /// </summary>
        [Fact]
        public async Task 没加密的包_产物校验不过时_不再换密码也不写密码错误()
        {
            Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2", "候选密码3" });

            string sourcePath = CreateSourceFile("apk-like.apk");
            ArchiveTask task = AddTask(harness, sourcePath);

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 200,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "classes.dex", Size = 100 },
                    new() { Path = "AndroidManifest.xml", Size = 100 }
                },
                IsEncrypted = false,
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            // 引擎"成功"，但只写出一个文件 → 校验必然判否（真机少 33 个文件就是这种形状）
            harness.Engine.OnExtractAsync = request =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "classes.dex"), new byte[100]);
                return Task.FromResult(Succeeded());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Single(harness.Engine.ExtractCalls);
            Assert.NotEqual(StatusText.WrongPassword, task.Status);
            Assert.Equal(StatusText.PasswordNotNeeded, task.PasswordStatus);
            Assert.Contains("没有加密", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("产物校验未通过", task.ErrorMessage, StringComparison.Ordinal);
        }

        /// <summary>
        /// **连续两个候选的结果一模一样 → 立刻停**（换密码不可能改变结果；真机白试了 9 次）。
        ///
        /// <para>判据只用事实：两次校验的文件数与字节数相同（⛔ 不比文案、也不看引擎说了什么）。</para>
        /// </summary>
        [Fact]
        public async Task 连续两个候选结果完全相同_不再试第三个()
        {
            Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2", "候选密码3", "候选密码4" });

            string sourcePath = CreateSourceFile("same-result.7z");
            ArchiveTask task = AddTask(harness, sourcePath);

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 200,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "one.bin", Size = 100 },
                    new() { Path = "two.bin", Size = 100 }
                },

                // 加密包：这一条走的是"结果相同"那一路（不是"没加密"那一路）
                IsEncrypted = true,
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            harness.Engine.OnExtractAsync = request =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "one.bin"), new byte[100]);
                return Task.FromResult(Succeeded());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(2, harness.Engine.ExtractCalls.Count);
            Assert.Contains("完全相同", task.ErrorMessage, StringComparison.Ordinal);
        }

        /// <summary>
        /// **「停止后续」必须在候选之间生效**（用户 2026-09-27："我都暂停了，你还在尝试新的密码"）。
        ///
        /// <para>真机现场：08:25:32 点了停止，08:25:33 程序照旧开始"密码候选 10/10"，又跑 32 秒。
        /// 本用例在第 1 个候选返回密码错误时调用 <c>StopAfterCurrent()</c>（就是那颗按钮的动作），
        /// 断言**只试了 1 个候选**，而且那条日志只写一次、说清"在下一个安全点停下"。</para>
        /// </summary>
        [Fact]
        public async Task 停止后续_候选之间停下_不再试新密码()
        {
            Harness harness = CreateHarness(passwords: new[] { "候选密码1", "候选密码2", "候选密码3", "候选密码4" });

            string sourcePath = CreateSourceFile("stop-between-candidates.7z");
            AddTask(harness, sourcePath);

            harness.Engine.OnListAsync = _ => Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = 100,
                Entries = new List<ArchiveEntry> { new() { Path = "one.bin", Size = 100 } },
                IsEncrypted = true,
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            harness.Engine.OnExtractAsync = request =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;

                // 用户在第 1 个候选跑完之后点了「停止后续」（真机上就是那颗按钮）
                harness.Coordinator.StopAfterCurrent();

                return Task.FromResult(WrongPassword());
            };

            try
            {
                await harness.Coordinator.StartExtractAsync();
            }
            catch (OperationCanceledException)
            {
                // 批级取消把整批收掉也算"停下"，核心断言在下面（只试了一个候选）。
            }

            List<string> logs = harness.Log.Logs.Select(item => item.DisplayText).ToList();

            Assert.True(
                harness.Engine.ExtractCalls.Count == 1,
                $"点了「停止后续」之后又试了 {harness.Engine.ExtractCalls.Count} 个候选。日志：{string.Join(" ｜ ", logs)}");

            /*
             * 日志两条、各有各的用途（真机上原来是三句不同措辞、还重复刷）：
             * · 批级通知**只写一次**（"…在下一个安全点停下…"）—— 他连点两次也只出现一条；
             * · 任务级那条说清"在第几个候选上停下、还剩几个没试"。
             */
            Assert.Equal(1, logs.Count(text => text.Contains("下一个安全点停下", StringComparison.Ordinal)));
            Assert.Contains(logs, text => text.Contains("已按「停止后续」中断", StringComparison.Ordinal)
                                          && text.Contains("不再尝试", StringComparison.Ordinal));
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                LogService log,
                PathService pathService)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
                PathService = pathService;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public PathService PathService { get; }
        }

        private Harness CreateHarness(IEnumerable<string>? passwords = null, Action<AppSettings>? configure = null)
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

            /*
             * 源包档位固定成"留在原地"。
             *
             * 理由：这一组测的是**解压管线本身**（入仓/定稿、校验、落点、归集、取消、UI 线程不被占住），
             * 断言里到处是"输出目录里有几个文件""源文件还在不在"。用户 2026-09-22 的新规则是
             * "成功就把源包也移进其余物"，照默认档跑会让源包混进这些计数，把管线测试变成
             * 一半在测源包搬运 —— 那种失败看不出管线坏没坏。
             * 源包搬运（两条路径 + 幂等 + 失败/取消）另有专测：SourcePackageRestMoveTests
             * 与 InnerLayerContinuationTests。
             */
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            if (passwords != null)
            {
                foreach (string password in passwords)
                {
                    passwordService.Passwords.Add(new PasswordItem
                    {
                        Value = password,
                        Source = "ImportedList",
                        IsEnabled = true,
                        Remark = "测试候选"
                    });
                }
            }

            // MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）：
            // 先存后还原，免得别的测试拿到我这边马上要删的临时目录。
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

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, engine, coordinator, logService, pathService);
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

        /// <summary>造双面文件时前面垫多少字节（"内嵌归档"就是它后面的那一段）。</summary>
        private const int EmbeddedPaddingBytes = 4096;

        /// <summary>
        /// 造一个"双面文件"：前面垫一段数据，偏移之后才是"真正的归档"。
        ///
        /// 抠包（<see cref="EmbeddedArchiveCarver"/>）是**真实的字节拷贝**，不需要那一段真的能被解开
        /// —— 解压由假引擎负责。要的只是"任务确实往工作区里写了过程物"这个事实。
        /// </summary>
        private string CreateEmbeddedSourceFile(string fileName)
        {
            string path = Path.Combine(_root, "src", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            byte[] bytes = new byte[EmbeddedPaddingBytes + 512];

            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i % 251);
            }

            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>
        /// 本任务的过程物落点目录（布局由 <see cref="PathService.BuildTaskWorkDirectory"/> 给出，
        /// 测试**不自己拼规则** —— 拼一份迟早与实现分叉）。
        /// </summary>
        private static string TaskWorkDirectory(Harness harness, ArchiveTask task) =>
            harness.PathService.BuildTaskWorkDirectory(task);

        /// <summary>
        /// 本任务的**暂存目录**（入仓阶段的产物在这里，定稿之后才进最终目录）。
        /// </summary>
        private static string TaskStageDirectory(Harness harness, ArchiveTask task) =>
            harness.PathService.BuildTaskStageDirectory(task);

        private static ExtractOptions DefaultOptions(Harness harness)
        {
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = harness.Vm.Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = harness.Vm.Settings.CustomOutputDirectory,
                KeepArchiveNameFolder = harness.Vm.Settings.KeepArchiveNameFolder
            };

            options.Normalize();
            return options;
        }

        private static void WriteFiles(string directory, int count)
        {
            Directory.CreateDirectory(directory);

            for (int i = 0; i < count; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"payload-{i:D5}.bin"), "x");
            }
        }

        private static int CountFiles(string? directory)
        {
            try
            {
                return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                    ? 0
                    : Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>目录下第一层子目录的个数；目录不存在算 0，读不了算 -1（断言会因此红掉，不静默通过）。</summary>
        private static int CountSubdirectories(string? directory)
        {
            try
            {
                return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                    ? 0
                    : Directory.GetDirectories(directory).Length;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 写几个"看起来像 ZIP"的产物：递归探测内层归档靠的是**魔数**（PK 03 04），
        /// 内容真假无所谓 —— 这里要的是"探测出两个内层归档"这个确定的局面。
        /// </summary>
        private static void WriteFakeZipFiles(string directory, params string[] fileNames)
        {
            Directory.CreateDirectory(directory);

            foreach (string fileName in fileNames)
            {
                byte[] bytes = new byte[512];
                bytes[0] = 0x50;
                bytes[1] = 0x4B;
                bytes[2] = 0x03;
                bytes[3] = 0x04;

                File.WriteAllBytes(Path.Combine(directory, fileName), bytes);
            }
        }

        /// <summary>
        /// 用 <c>mklink /J</c> 造目录联接点：不需要管理员权限，也不改注册表
        /// （符号链接要 SeCreateSymbolicLinkPrivilege，普通开发机上跑不了）。
        ///
        /// 造完再确认一次：mklink 失败的返回码在这里没法直接拿到，而上层断言依赖"联接点真的存在"——
        /// 静默失败会让测试变成"什么都没测到却是绿的"。
        /// </summary>
        private static void CreateJunction(string linkPath, string targetPath)
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(linkPath);
            psi.ArgumentList.Add(targetPath);

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 cmd.exe 建联接点");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已经退了就无所谓。
                }

                throw new InvalidOperationException("mklink 建联接点超时");
            }

            Assert.True(
                Directory.Exists(linkPath) &&
                (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0,
                $"没能造出目录联接点（mklink 输出：{stdout}{stderr}）");
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

        private static ArchiveOperationResult WrongPassword()
        {
            return new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.WrongPassword,
                Message = "密码错误",
                DetectedErrorType = "WrongPassword"
            };
        }

        private static ArchiveListResult ListResult(int fileCount)
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = fileCount,
                TotalUncompressedSize = 0,
                Entries = Enumerable.Range(0, fileCount)
                    .Select(i => new ArchiveEntry { Path = $"payload-{i:D5}.bin", Size = 1 })
                    .ToList(),
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }

        private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
        {
            var watch = Stopwatch.StartNew();

            while (watch.Elapsed < timeout)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.Fail($"{failureMessage}（等了 {timeout.TotalSeconds:F0} 秒）");
        }

        /// <summary>
        /// 模拟 WPF 的 UI 线程：一条专用线程 + 一个消息队列，回调只在这条线程上执行。
        /// </summary>
        private sealed class MessagePumpContext : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

            public override void Post(SendOrPostCallback d, object? state)
            {
                _queue.Add((d, state));
            }

            public void RunUntil(Func<bool> stop)
            {
                while (!stop())
                {
                    if (_queue.TryTake(out (SendOrPostCallback Callback, object? State) item, 20))
                    {
                        item.Callback(item.State);
                    }
                }
            }
        }

        /// <summary>
        /// 可控的假引擎：调用记录 + 每个方法都可以被测试替换实现。
        /// 它是"外部进程"的替身，所以测试里可以放心让它返回密码错误、慢、或者永不结束。
        /// </summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public List<string> ExtractCalls { get; } = new();

            public string LastExtractOutputPath { get; set; } = string.Empty;

            /// <summary>解压是否真的成功过一次（用来只对"收尾那次列目录"下手）。</summary>
            public bool Extracted { get; set; }

            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            /// <summary>带 <see cref="ExtractOptions"/> 的解压钩子（密码预检要按"只解哪些条目"区分）。</summary>
            public Func<ArchiveRequest, ExtractOptions, Task<ArchiveOperationResult>>? OnExtractWithOptionsAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

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
                    : Task.FromResult(ListResult(0));
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
                ExtractCalls.Add(request.ArchivePath);

                if (OnExtractWithOptionsAsync != null)
                {
                    return OnExtractWithOptionsAsync(request, options);
                }

                if (OnExtractAsync != null)
                {
                    return OnExtractAsync(request);
                }

                LastExtractOutputPath = request.OutputPath ?? string.Empty;
                return Task.FromResult(Succeeded());
            }
        }
    }
}
