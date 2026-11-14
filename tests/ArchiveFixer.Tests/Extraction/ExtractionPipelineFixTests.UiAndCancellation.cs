using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests;

/// <summary>
/// UI 线程、取消与停止后续的回归用例：
/// 收尾不占 UI 线程、收尾中按取消、并发位上的停止后续。
/// </summary>
public partial class ExtractionPipelineFixTests
{


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

        /*
         * 收尾窗口的起点 = **产品自己的收尾钩子**（`PostProcessStartedForTests`）。
         *
         * ⚠ 2026-10-21 迁移：原来这一档拿"收尾那一次列目录"当起点（`OnListAsync` + `Extracted`），
         * 而迁移成"普通任务全递归"之后，递归路用的是**解压前**拿到的那份层清单
         * （`RecursionLayerReport.Manifest`）当 L3 的预期，**收尾不再重新列一遍目录** ⇒
         * 那个观察点整条消失（实测逐字：`没有观察到收尾窗口的起点`）。
         * 要证明的事实一个字没改（收尾期间 UI 线程照常跳心跳），只把起点换成产品给出的那一刻。
         */
        harness.Coordinator.PostProcessStartedForTests = () =>
            Interlocked.CompareExchange(ref postProcessStartTicks, Stopwatch.GetTimestamp(), 0);

        // 列目录仍要给真结果：解压前的预检/清单要拿它当预期（与收尾窗口无关了）。
        harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(fileCount));

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
    /// **收尾已经开始之后按取消** ⇒ 不定稿、不归集、源包不删、状态「已取消」。
    ///
    /// <para>用户「残余缺口：收尾内部按取消无用例钉住」的守门。它靠测试缝
    /// `ExtractionCoordinator.PostProcessStartedForTests` 把取消按在**收尾第一道闸门之后** ——
    /// 此前那两个钩子（`Engine.OnList/OnExtract`）都在收尾之前，更早的闸门先看见了取消 ⇒
    /// 收尾内部那几处 `ThrowIfCancellationRequested()` 从来没被任何用例看见过。</para>
    ///
    /// <para>⚠ **红检未成立（2026-10-17 实测，如实记）**：把收尾里那几处 `ThrowIfCancellationRequested()`
    /// （以及另外 10 处）临时全撤掉 ⇒ 本条**照旧绿** ⇒ 真正看见这次取消的是**别的闸门**（`IsStopping` 那一族），
    /// **不是收尾内部那几处检查**。⇒ 本用例钉的是**行为**（收尾开始之后按取消 ⇒ 不定稿/不归集/源包不删），
    /// ⛔ **别把它读成"收尾内部那几处检查已验过"**；那个缺口仍然开着，`docs/大改动-2026-10-14-口径与冲突.md:840` 那条记账不变。</para>
    /// </summary>
    [Fact]
    public async Task 收尾已经开始之后按取消_不定稿_不归集_源包不删()
    {
        string collectRoot = Path.Combine(_root, "collect-after-postprocess");

        Harness harness = CreateHarness(configure: settings =>
        {
            settings.CollectResultsToDirectory = true;
            settings.CollectTargetDirectory = collectRoot;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
        });

        string source = CreateSourceFile("cancel-late.7z");
        ArchiveTask task = AddTask(harness, source);

        harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(5));
        harness.Engine.OnExtractAsync = request =>
        {
            harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
            WriteFiles(request.OutputPath!, 5);
            harness.Engine.Extracted = true;

            return Task.FromResult(Succeeded());
        };

        // 收尾已经开始（第一道闸门之后）才按取消 —— 只有收尾内部那几处检查能看见它。
        harness.Coordinator.PostProcessStartedForTests = () => harness.Coordinator.CancelCurrentTask();

        await harness.Coordinator.StartExtractAsync();

        Assert.Equal(StatusText.Cancelled, task.Status);
        Assert.True(File.Exists(source), "取消之后源包被删了");
        Assert.False(Directory.Exists(collectRoot), "取消之后产物被归集走了");
        Assert.False(Directory.Exists(task.OutputPath), $"取消之后最终目录被建出来了：{task.OutputPath}");
    }
    /// <summary>
    /// 收尾阶段以前从不检查取消令牌：用户按了「取消当前」，产物照样被移动、源包照样被删。
    /// 现在每一步之前都查令牌，且状态必须落成"已取消"（不得显示成功）。
    /// </summary>
    [Fact]
    public async Task 产物刚写完就取消_不移动产物_不删源包_状态是已取消()
    {
        string collectRoot = Path.Combine(_root, "collect");

        Harness harness = CreateHarness(configure: settings =>
        {
            settings.CollectResultsToDirectory = true;
            settings.CollectTargetDirectory = collectRoot;

            // 开着"会动源包"的那两档才有意义：取消之后一个源文件都不许少。
            // （第 32 条之后就是这两档组合：源包放进其余物 + 其余物彻底删除。）
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
        });

        string source = CreateSourceFile("cancel.7z");
        ArchiveTask task = AddTask(harness, source);

        /*
         * 取消的时刻 = **产物已经写完**（"任务真在跑"的那一刻）。
         *
         * ⚠ 2026-10-16 重摆 + 红检（两条都是实测取的运行期事实，⛔ 不是读代码推的）：
         * ① 原来挂在 `OnListAsync` 里"解压已完成才按取消"，而新流程把列目录挪到了**解压之前**
         *    （识别 / 空间门 / 层计划各一次）—— 探针实测 `listCalls=3`、日志里**一条取消记录都没有**
         *    （`CancelCurrentTask` 从未被调用）⇒ 取消根本没发生，跑完落「解压成功」；
         * ② ⛔ **本用例钉的是行为、不是收尾内部那两处检查**：把 `PostProcessSuccessAsync` 入口
         *    与中段那两句 `ThrowIfCancellationRequested()` 临时撤掉，用例**照旧绿** ⇒ 看见这次取消的
         *    是更早的那一道闸门。判据意图一个字没改（**取消 ⇒ 状态「已取消」＋ 源包原地不动
         *    ＋ 产物一个都不许移动**），但⛔ 别把它读成"收尾内部的取消检查已验过"——
         *    那个缺口已如实记进 `docs/大改动-2026-10-14-口径与冲突.md`。
         */
        harness.Engine.OnExtractAsync = request => Task.Run(() =>
        {
            harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
            WriteFiles(request.OutputPath!, 5);
            harness.Engine.Extracted = true;

            // 产物已经在盘上了：此刻按取消，收尾必须看得见它。
            harness.Coordinator.CancelCurrentTask();
            return Succeeded();
        });

        harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(5));

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
        Harness harness = CreateHarness(configure: settings => settings.ConcurrencyOverride = 2);

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
        // "已按「暂停所有操作」停下：…"
        await WaitAsync(
            () => harness.Engine.ExtractCalls.Any(p => string.Equals(p, third.CurrentPath, StringComparison.OrdinalIgnoreCase)) ||
                  harness.Log.Logs.Any(x => x.Message.Contains("已按「暂停所有操作」停下", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10),
            "停止后续没有被处理");

        Assert.DoesNotContain(
            harness.Engine.ExtractCalls,
            p => string.Equals(p, third.CurrentPath, StringComparison.OrdinalIgnoreCase));

        Assert.NotEqual(StatusText.ExtractSuccess, third.Status);
        Assert.NotEqual(StatusText.Extracting, third.Status);

        _ = pipeline;   // 第 2 个任务故意不结束，管线会一直挂着；测试不 await 它。
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
        Assert.Equal(1, logs.Count(text => text.Contains("重新按「一键处理」", StringComparison.Ordinal)));
        Assert.Contains(logs, text => text.Contains("已按「暂停所有操作」中断", StringComparison.Ordinal)
                                      && text.Contains("不再尝试", StringComparison.Ordinal));
    }
}
