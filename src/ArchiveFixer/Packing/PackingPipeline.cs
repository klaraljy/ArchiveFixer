using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Packing;

/// <summary>
/// 一次打包的**流程编排**（<see cref="PackingService"/> 的协作类，职责：把计划 / 执行 / 校验 / 清理 / 日志
/// 串成一条线）。
///
/// <para>它只做**顺序与结论**：先计划（判据全在动字节之前判完），再 7z 加密分卷，再外层容器，
/// 再按容器核对产物，最后收尾。每一步"做不做、做到哪"都由各协作类给结论，这里只按结论决定
/// 继续还是返回 —— ⛔ 不在这里另写一套判据。</para>
///
/// <para><b>几条不肯让步的规矩</b>（判据实现见各协作类，这里是它们串起来之后的整体效果）：</para>
/// <list type="bullet">
/// <item><description>失败 / 取消**不留半个外层容器**；分卷保留（重试前要用户自己清）；</description></item>
/// <item><description>完成之后**按容器**核对产物，对不上**不显示成功**（不变量 6）；</description></item>
/// <item><description>收尾（原包 / 其余物）只在"外层生成 + 校验通过"之后走，之前一个字节都不动；</description></item>
/// <item><description>取消只杀**自己启动的那个 PID 及其子进程**（在 <see cref="PackProcessRunner"/> 里）。</description></item>
/// </list>
///
/// <para>⚠ 返回的 <see cref="PackingResult"/> 是**唯一**结论出口：这个类不抛异常来表示失败，
/// 失败与取消都落成结果（<see cref="PackingState.Failed"/> / <see cref="PackingState.Cancelled"/>）。</para>
/// </summary>
internal sealed class PackingPipeline
{
    private readonly ToolLocator _tools;
    private readonly PackingPreparation _preparation;
    private readonly PackingEngineSteps _engine;

    internal PackingPipeline(
        ToolLocator tools,
        IPackProcessRunner runner,
        Func<string, long?> availableSpaceProbe)
    {
        _tools = tools;
        _preparation = new PackingPreparation(tools, availableSpaceProbe);
        _engine = new PackingEngineSteps(runner);
    }

    /// <summary>跑一次打包。**不抛异常**：失败与取消都落成 <see cref="PackingResult"/>。</summary>
    /// <param name="request">请求（含两个密码；只存内存）。</param>
    /// <param name="log">每一条步骤日志的出口（可空）；内容与 <see cref="PackingResult.LogLines"/> 一致。</param>
    /// <param name="progress">进度接收端（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal async Task<PackingResult> RunAsync(
        PackingRequest request,
        Action<string>? log,
        IProgress<PackingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var runLog = new PackingRunLog(log, progress);

        PackingResult Fail(string reason, string message, IReadOnlyList<PackingVolume>? volumes = null)
        {
            stopwatch.Stop();
            runLog.Write("失败：" + reason);

            return new PackingResult
            {
                State = PackingState.Failed,
                Message = message,
                FailureReason = reason,
                OuterContainer = request?.OuterContainer ?? PackOuterContainer.Rar,
                Volumes = volumes ?? Array.Empty<PackingVolume>(),
                LogLines = runLog.Lines,
                Elapsed = stopwatch.Elapsed
            };
        }

        PackingResult Cancelled(string reason, string? outerPath, IReadOnlyList<PackingVolume>? volumes = null)
        {
            stopwatch.Stop();
            runLog.Write(StatusText.PackCancelled + "：" + reason);

            return new PackingResult
            {
                State = PackingState.Cancelled,
                Message = StatusText.PackCancelled,
                FailureReason = reason,
                OuterContainer = request?.OuterContainer ?? PackOuterContainer.Rar,
                OuterPath = outerPath,
                Volumes = volumes ?? Array.Empty<PackingVolume>(),
                LogLines = runLog.Lines,
                Elapsed = stopwatch.Elapsed
            };
        }

        runLog.Report(PackingStep.Preparing, StatusText.PackStepPreparing);

        PackingPreparedRun prepared = _preparation.TryPrepare(request, runLog);

        if (prepared.Plan is not PackingPlan plan)
        {
            return Fail(prepared.FailureReason, prepared.FailureMessage);
        }

        PackOuterContainer effectiveContainer = prepared.EffectiveContainer;

        // ───────── ④ 第一步：7z 加密分卷 ─────────
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled("开始之前就被取消了", null);
        }

        runLog.Report(PackingStep.Volumes, StatusText.PackStepVolumes);

        PackStepResult volumeStep;
        List<PackingVolume> volumes;

        try
        {
            volumeStep = await _engine.RunVolumeStepAsync(
                    PackingCommandBuilder.BuildVolumeArguments(plan, request.Password),
                    request.Password,
                    StatusText.PackStepVolumes,
                    runLog,
                    cancellationToken)
                .ConfigureAwait(false);

            // 数分卷必须在进程结束之后：跑到一半去数，数到的是半成品。
            volumes = PackingProductCheck.CollectVolumes(plan);
        }
        catch (OperationCanceledException)
        {
            return Cancelled("生成 7z 分卷时被取消", null, PackingProductCheck.CollectVolumes(plan));
        }

        if (volumeStep.Cancelled)
        {
            runLog.Write(PackingProductCheck.DescribePartialVolumes(volumes.Count, plan));

            return Cancelled("生成 7z 分卷时被取消", null, volumes);
        }

        if (!volumeStep.Success)
        {
            runLog.Write(PackingProductCheck.DescribePartialVolumes(volumes.Count, plan));

            return Fail(
                $"7-Zip 没能建出加密分卷（退出码 {volumeStep.ExitCode}）。"
                + PackingEngineSteps.DescribeOutputTail(volumeStep)
                + "分卷没有做成，外层容器也就没开始 —— 结果文件一个都没有生成。",
                StatusText.PackFailed,
                volumes);
        }

        string volumeProblem = PackingProductCheck.ValidateVolumes(volumes, plan);

        if (volumeProblem.Length > 0)
        {
            return Fail(volumeProblem, StatusText.PackFailed, volumes);
        }

        runLog.Write($"7z 分卷完成：{volumes.Count} 个（共 {TaskSpaceEstimate.FormatSize(volumes.Sum(v => v.Bytes))}）"
                     + $"；预计 {plan.PlannedVolumeCount} 卷");

        // ───────── 不做外层容器这条路（本机没装 WinRAR 时最省的出路） ─────────
        if (!effectiveContainer.HasOuterArtifact())
        {
            runLog.Report(PackingStep.Finished, StatusText.PackSuccess, 100);

            stopwatch.Stop();

            var volumesOnly = new PackingResult
            {
                State = PackingState.Succeeded,
                Message = StatusText.PackPartialVolumesOnly,
                OuterContainer = PackOuterContainer.None,
                VolumesFolder = plan.OutputFolder,
                Volumes = volumes,
                VerificationDetail = "只做了 7z 分卷（按要求不做外层容器）",
                LogLines = runLog.Lines,
                Elapsed = stopwatch.Elapsed
            };

            runLog.Write(volumesOnly.Describe());

            return volumesOnly;
        }

        // ───────── ⑤ 第二步：外层容器（rar 或 7z） ─────────
        // ⚠ 用**实际生效的**容器（第 46 条：没装 WinRAR 时 rar 已在上面的检查里降级成 7z）。
        bool outerIsRar = effectiveContainer == PackOuterContainer.Rar;
        string outerPath = outerIsRar ? plan.RarPath : plan.SevenZipOuterPath;

        // 没有降级时，执行路径必须与校验时那一份逐字一致；降级（rar→7z）才换到实际容器。
        if (effectiveContainer == plan.OuterContainer
            && !string.IsNullOrWhiteSpace(plan.PlannedOuterPath))
        {
            outerPath = plan.PlannedOuterPath;
        }

        string outerName = outerIsRar ? "外层 rar" : "外层 7z";
        string outerTool = outerIsRar ? "Rar.exe" : "7-Zip";
        PackToolKind outerToolKind = outerIsRar ? PackToolKind.Rar : PackToolKind.SevenZip;
        string outerStepText = outerIsRar ? StatusText.PackStepRar : StatusText.PackStepSevenZipOuter;

        runLog.Report(PackingStep.OuterContainer, outerStepText);

        PackStepResult outerStep;

        /*
         * ⚠ 追加改口径：外层容器现在装的是**逐个点名的分卷文件**（顶层，没有文件夹层），
         * 所以这一步的进程要在**落点目录**里执行、参数里只给文件名（见两个 Build*Arguments）。
         */
        List<string> outerArguments = outerIsRar
            ? PackingCommandBuilder.BuildRarArguments(_tools, plan, request.EffectiveOuterPassword, volumes)
            : PackingCommandBuilder.BuildSevenZipOuterArguments(
                plan,
                request.EffectiveOuterPassword,
                PackingCommandBuilder.VolumePaths(volumes));

        try
        {
            outerStep = await _engine.RunOuterStepAsync(
                    outerToolKind,
                    outerArguments,
                    request.EffectiveOuterPassword,
                    plan.OutputFolder,
                    outerStepText,
                    runLog,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PackingIncompleteOuterCleanup.Delete(outerPath, outerName, runLog);

            return Cancelled($"生成{outerName}时被取消", null, volumes);
        }

        if (outerStep.Cancelled)
        {
            PackingIncompleteOuterCleanup.Delete(outerPath, outerName, runLog);

            return Cancelled($"生成{outerName}时被取消（分卷还在落点目录里）", null, volumes);
        }

        if (!outerStep.Success)
        {
            PackingIncompleteOuterCleanup.Delete(outerPath, outerName, runLog);

            return Fail(
                $"{outerName}没做成（{outerTool} 退出码 {outerStep.ExitCode}）。" + PackingEngineSteps.DescribeOutputTail(outerStep)
                + $"7z 分卷还在落点目录里：{plan.OutputFolder}（{volumes.Count} 个）—— 修好原因后可以重试，"
                + "重试前请先删掉那些分卷文件，让程序从干净状态重建。",
                StatusText.PackFailed,
                volumes);
        }

        // ───────── ⑥ 核对产物（**按容器**读一遍；对不上就不显示成功） ─────────
        string verifyStepText = outerIsRar ? StatusText.PackStepVerify : StatusText.PackStepVerifySevenZip;

        runLog.Report(PackingStep.Verifying, verifyStepText);

        PackingVerification verification;

        try
        {
            PackStepResult listing = await _engine.RunListingStepAsync(
                    outerToolKind,
                    outerIsRar
                        ? PackingCommandBuilder.BuildRarListArguments(_tools, plan, request.EffectiveOuterPassword)
                        : PackingCommandBuilder.BuildSevenZipListArguments(plan, request.EffectiveOuterPassword),
                    request.EffectiveOuterPassword,
                    cancellationToken)
                .ConfigureAwait(false);

            if (listing.Cancelled)
            {
                return Cancelled($"校验产物时被取消（{outerName}已经生成，但没核对过）", outerPath, volumes);
            }

            verification = PackingProductCheck.FromListingStep(listing, outerName, outerTool, outerIsRar, volumes);
        }
        catch (OperationCanceledException)
        {
            return Cancelled($"校验产物时被取消（{outerName}已经生成，但没核对过）", outerPath, volumes);
        }

        runLog.Write("产物校验：" + verification.Detail);

        if (!verification.Ok)
        {
            /*
             * 不变量 6：**产物对不上就不是成功**。
             * 这里刻意不删外层容器：它确实存在、也可能对用户有用（比如条目名与预期不同），
             * 删掉是不可逆的；用户看到这句话之后自己决定。
             */
            return Fail(
                verification.Detail + "。结果文件没有被当成成功：请先自己打开看一眼再决定留不留。",
                StatusText.PackVerifyFailed,
                volumes);
        }

        long outerBytes = 0;

        try
        {
            outerBytes = new FileInfo(outerPath).Length;
        }
        catch
        {
            outerBytes = 0;
        }

        /*
         * ───────── ⑦ 收尾：原包 / 其余物（用户 2026-09-26 第 46 条）─────────
         *
         * ⛔ 位置是刻意的：**只有外层容器生成 + 校验通过**之后才走这一步；
         * 失败 / 取消 / 校验没过都在前面 return 掉了，一个字节都不会动（红线）。
         * 收尾出问题也不改结论（结果文件已经在那儿了），只把话写进日志。
         */
        PackingCleanupOutcome cleanup = PackingCleanup.Run(plan, succeeded: true, outerPath);

        foreach (string line in cleanup.LogLines)
        {
            runLog.Write(line);
        }

        runLog.Report(PackingStep.Finished, StatusText.PackSuccess, 100);

        stopwatch.Stop();

        var result = new PackingResult
        {
            State = PackingState.Succeeded,
            Message = StatusText.PackSuccess,
            OuterContainer = effectiveContainer,
            OuterPath = outerPath,
            OuterBytes = outerBytes,
            VolumesFolder = plan.OutputFolder,
            Volumes = volumes,
            VerificationDetail = verification.Detail,
            CleanupNote = cleanup.Ran
                ? $"原包：{(cleanup.SourceMoved ? "已移入其余物" : "不动")}；其余物：{cleanup.RestSummary}"
                : string.Empty,
            LogLines = runLog.Lines,
            Elapsed = stopwatch.Elapsed
        };

        runLog.Write(result.Describe());

        return result;
    }
}
