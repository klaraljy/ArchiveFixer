using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 单任务解压的辅助阶段（从 `ExtractionCoordinator.ExtractTaskMainStep.cs` 搬出的真实现，2026-11-13）：
/// 开工前准备（快照/复用重置/APK/跟班卷/源变更闸门）、非压缩内容跳过、引擎入口路径、入仓、
/// 最终目录占位、失败事实登记与加密头结论恢复。
///
/// <para>⛔ 判据/文案/终态不在本类新增：全部沿用搬出前的同一份实现与协调器既有出口（<c>_host.</c>）。</para>
/// </summary>
internal sealed class ExtractionTaskAuxiliary
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionTaskAuxiliary(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>开工前准备；false = 这一单到此为止（原因已写进任务/日志，或按既有口径静默停下）。</summary>
    internal bool Prepare(ArchiveTask? task)
    {
        if (task == null)
        {
            return false;
        }

        DiagnosticLog.WriteTaskSnapshot(_host.Vm.Logger, "task.begin", task);

        // 每个任务开始：清掉"这一个"的冲突记账（"全部 X"是本批级的，不清）。
        _host.ResetTaskConflictState();

        // 上一轮的"提示类"结论先清掉（任务对象会被复用，见原注释）。
        task.DangerousEntriesWarning = string.Empty;
        task.PathLengthWarning = string.Empty;

        // 真正的 APK 不碰（内容证据、唯一出口）。
        if (TrySkipAndroidPackage(task))
        {
            return false;
        }

        // 同一组的后续卷：入口已经定下来 ⇒ 不再检查源文件、不再试密码、不再解压。
        if (task.IsVolumeGroupFollower)
        {
            return false;
        }

        _host.EnsureSourceSnapshot(task);

        if (_host.StopIfSourceChanged(task))
        {
            return false;
        }

        return true;
    }

    internal bool TrySkipKnownContent(
        ArchiveTask task,
        bool oneClickRun,
        ArchiveFixer.Detection.FileNatureCard card)
    {
        if (!oneClickRun
            || string.IsNullOrWhiteSpace(task.CurrentPath)
            || !File.Exists(task.CurrentPath))
        {
            return false;
        }

        /*
         * 媒体前缀的完整归档 / 已证明的归档：头部像媒体也不能按内容物跳过。
         * 这里用 entryCard 已经取到的底层事实，避免对媒体前缀再猜一次。
         */
        if (card.ContentClass == ArchiveFixer.Detection.FileContentClass.Archive
            || card.EmbeddedOffset > 0
            || card.BodyKind is ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                or ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece)
        {
            return false;
        }

        /*
         * 用户明确导入的内容容器（docx/epub/jar/apk…）保留既有“源包手动只解压”语义：
         * 它是用户点名要处理的文件，不按“自动扫到的内容物”跳过。
         * 自动递归/产物里的同类文件仍由递归闸门与 OneClick 产物扫描挡住。
         */
        if (!task.IsContinuationTask
            && ArchiveFixer.Security.ContentContainerProtection.ShouldProtect(task.CurrentPath))
        {
            return false;
        }

        ArchiveFixer.Detection.FileContentProbeResult content =
            ArchiveFixer.Detection.ContentAdmission.Probe(
                task.CurrentPath,
                ArchiveFixer.Detection.ContentAdmissionScope.UserSource,
                deep: true);

        if (content.Class != ArchiveFixer.Detection.FileContentClass.KnownContent)
        {
            return false;
        }

        /*
         * APK 走已有的专用闸门（它有自己的文案/终态/守门用例），
         * 这里不要用“普通非压缩文件”把它抢先收掉。
         */
        if (ArchiveFixer.Detection.AndroidPackageDetector.IsAndroidPackage(task.CurrentPath))
        {
            return false;
        }

        task.IsArchive = false;
        task.Status = StatusText.Skipped;
        task.Operation = StatusText.OpSkip;
        task.ProgressText = StatusText.ProgressCompleted;
        task.ErrorMessage = $"这是非压缩文件（{content.Format}）—— 按设置不解压、不递归、不改名，源文件一个字节都不动。";
        task.Outcome = TaskOutcome.Skipped;
        task.EndTime ??= ArchiveFixer.Helpers.Clock.Now;

        _host.AppendLog(
            "INFO",
            $"{task.LogName}：识别为非压缩文件（{content.Format}；{content.Evidence}）"
            + " —— 自动处理不碰，源文件原地不动。要拆请手动选中后用「只解压」。");

        return true;
    }

    internal bool TrySkipAndroidPackage(ArchiveTask task)
    {
        if (!AndroidPackageDetector.IsAndroidPackage(task.CurrentPath))
        {
            return false;
        }

        task.IsArchive = false;
        task.Status = StatusText.Skipped;
        task.Operation = StatusText.OpSkip;
        task.ProgressText = StatusText.ProgressCompleted;
        task.ErrorMessage = StatusText.AndroidPackageSkipped;

        // 机器终态显式落（与"未识别 ⇒ 跳过"那条同一个口径：只补 EndTime 会让终态收口按失败接手）。
        task.Outcome = TaskOutcome.Skipped;
        task.EndTime ??= ArchiveFixer.Helpers.Clock.Now;

        _host.AppendLog(
            "WARN",
            $"{task.LogName}：这是 Android 应用安装包（ZIP 里同时有 {AndroidPackageDetector.ManifestEntryName} 与 "
            + $"{AndroidPackageDetector.DexEntryName}）—— 按设置不解压、不递归、不改名，源文件一个字节都不动。"
            + "要处理它请用专门的 APK 工具。");

        return true;
    }

    /// <summary>
    /// 交给引擎的那一份归档路径：先修"坏名字的第一卷"（只改我们自己产出的那一卷），
    /// 再优先用盘上同一份的规范名（判据全在读事实，唯一出口 <c>RecursiveExtractor.PreferCanonicalSiblingName</c>）。
    /// ⛔ 只影响交给引擎的那一份：task.CurrentPath 与源包那一档一个字都不动。
    /// </summary>
    internal string ResolveEngineArchivePath(ArchiveTask task, string engineArchivePath)
    {
        /*
         * 第二大步给了规范名入口（`X.7z.001` 这类硬链接别名）⇒ 引擎就用它。
         * ⛔ 任务账/源包/删除判据仍读 task.CurrentPath；这里只换“交给引擎的那一份”。
         */
        if (!string.IsNullOrWhiteSpace(task.EngineEntryPathOverride)
            && File.Exists(task.EngineEntryPathOverride))
        {
            return task.EngineEntryPathOverride;
        }

        string resolved = _host.TryRepairBrokenVolumeChainName(task, engineArchivePath);

        return RecursiveExtractor.PreferCanonicalSiblingName(resolved);
    }

    /// <summary>
    /// 把预检的"文件名已加密"结论恢复回来：条件收得最紧 —— 预检说过、而且这一层最后得到的**只是**"密码没通过"。
    /// </summary>
    internal void RestoreEncryptedHeadersConclusion(
        ArchiveTask task,
        bool preflightSaidEncryptedHeaders,
        string? encryptedHeadersMessage)
    {
        if (!preflightSaidEncryptedHeaders ||
            (task.Status != StatusText.WrongPassword && task.Status != StatusText.ExtractFailed))
        {
            return;
        }

        task.Status = StatusText.EncryptedHeaders;
        task.PasswordStatus = StatusText.PasswordNeed;

        if (!string.IsNullOrWhiteSpace(encryptedHeadersMessage))
        {
            // 连原因也一起恢复：泛泛的"密码错误"会让用户去翻密码本，
            // 而这里的结论是"先给它一个密码，它才肯把内容清单给你看"。
            task.ErrorMessage = encryptedHeadersMessage;
        }

        _host.AppendLog(
            "WARN",
            $"{task.LogName}：这次拿到的是「{StatusText.EncryptedHeaders}」的结论" +
            "（不是泛泛的密码错误）—— 连内容清单都读不出来，需要正确密码才能列出内容。");
    }

    /// <summary>
    /// 阶段一：入仓（契约 §2.1）。从这一刻起本任务所有中间动作都在私有暂存目录里完成，
    /// 源目录与最终目录一个字节都不会被写。返回 false = 这一单停下（原因已经写进任务与日志）。
    /// </summary>
    internal async Task<(bool Ok, string StageDirectory, string EngineOutputPath)> TryPrepareStagingAsync(
        ArchiveTask task,
        string outputPath,
        CancellationToken cancellationToken)
    {
        string stageDirectory = string.Empty;
        string engineOutputPath = string.Empty;
        stageDirectory = string.Empty;
        engineOutputPath = string.Empty;

        /*
         * ===== 阶段一：入仓（stage，契约 §2.1）=====
         *
         * 从这一刻起，本任务**所有**中间动作（抠内嵌归档之后的解压、第一层、内层分卷、递归）
         * 都在私有暂存目录 <c>&lt;work&gt;\&lt;taskId&gt;\stage</c> 里完成，
         * 源目录与最终目录一个字节都不会被写。
         *
         * 这条就是用户那句"分卷文件你居然又解压到外面来了"的根治点：
         * 旧实现把引擎的输出目录直接设成 destDir，内层分卷于是和内容物一起躺在用户目录里；
         * 一旦失败，用户目录里还留着半成品。
         */
        stageDirectory = _host.PathService.BuildTaskStageDirectory(task);

        if (string.IsNullOrWhiteSpace(stageDirectory))
        {
            task.Status = StatusText.ExtractFailed;
            task.ProgressText = StatusText.ProgressFailed;
            task.ErrorMessage = "无法确定暂存目录（工作区不可用）";
            _host.AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
            return (false, string.Empty, string.Empty);
        }

        // 记下"这个任务真正用的工作区目录"：收尾时的空壳清理要按它来，
        // **不能事后重算**（taskId 含源路径哈希，而成功路径会回写 CurrentPath，见字段说明）。
        _host.TaskWorkspaceDirectories[task] = Path.GetDirectoryName(stageDirectory.TrimEnd('\\', '/')) ?? string.Empty;

        string? stageFailure = await _host.PrepareStageDirectoryAsync(task, stageDirectory, cancellationToken);

        if (!string.IsNullOrWhiteSpace(stageFailure))
        {
            task.Status = StatusText.ExtractFailed;
            task.ProgressText = StatusText.ProgressFailed;
            task.ErrorMessage = stageFailure;
            _host.AppendLog("ERROR", $"{task.LogName}：{stageFailure}");
            return (false, string.Empty, string.Empty);
        }

        // 引擎写盘的地方 = 暂存目录。**不是** outputPath —— 那是定稿的目标。
        engineOutputPath = stageDirectory;

        _host.AppendLog("INFO", $"{task.LogName}：入仓目录 {stageDirectory}（中间产物先落这里，定稿时才搬进 {outputPath}）");

        return (true, stageDirectory, engineOutputPath);
    }

    /// <summary>
    /// 并发下的"最终目录"占位：只对"目录还不存在、且是每包一个目录的落点"占位（只占不建），
    /// 占不到就按同一套 `名字(1)` 约定换一个；收尾时由 ProcessExtractTaskAsync 释放。
    /// 返回占位后的输出目录 + 可能产生的改名说明。
    /// </summary>
    internal (string OutputPath, string RedirectNote) ClaimFinalOutputDirectoryIfNeeded(
        ArchiveTask task,
        string outputPath,
        bool landsInSourceDirectory,
        bool sharedOutputRoot,
        string outputRedirectNote)
    {
        if (task.IsContinuationTask ||
            landsInSourceDirectory ||
            sharedOutputRoot ||
            string.IsNullOrWhiteSpace(outputPath) ||
            Directory.Exists(outputPath))
        {
            return (outputPath, outputRedirectNote);
        }

        return (_host.ClaimOutputDirectory(outputPath, task, ref outputRedirectNote), outputRedirectNote);
    }

    /// <summary>
    /// 登记"失败时盘上还剩什么"（部分完成发布的输入）：先按唯一出口 `ProducedContentGate` 数一次
    /// 真解出来的非空文件（终态分类的判据输入），再交候选表，最后把引擎原话落一次日志。
    /// </summary>
    internal void RecordPartialPublishFacts(
        ArchiveTask task,
        string engineOutputPath,
        string engineArchivePath,
        string selectedPassword,
        IReadOnlyList<(string Path, long Size)>? lastLayerManifestEntries,
        ArchiveOperationResult? lastResult)
    {
        /*
         * ===== 登记"失败时盘上还剩什么"（部分完成发布的输入，用户 2026-10-02）=====
         *
         * 位置刻意选在**候选循环已经跑完、结论已经定下来之后**：
         * · 产物目录里此刻留着的是**最后一个候选**留下的东西（每个候选开工前会清一次），
         *   所以尺子与现场必须是同一次候选的 —— `lastLayerManifestEntries` 与
         *   `lastResult` 都跟着循环滚动，天然对齐；
         * · 密码取 `selectedPassword`（最后一个候选的）：密码不对时收尾那次懒取清单
         *   照样列不出来 ⇒ 判"没有清单" ⇒ 一个字节都不发布。
         *
         * ⛔ 这里不判断③页开关（收尾时才读）—— 详见 RecordPartialPublishCandidate 的说明。
         */
        /*
         * "这一单整体有没有内容物解出"（终态分类那一档的判据输入，用户 2026-10-16 口径）：
         * 用**唯一出口** `ProducedContentGate`（与"密码已被证实"同一把尺子，⛔ 不另定阈值）
         * 数一次产物目录里真解出来的非空文件。
         */
        bool producedContent =
            ProducedContentGate.TryMeasure(
                engineOutputPath,
                lastResult?.ReportedSubItemErrors ?? 0,
                lastResult?.FailedEntryNames?.Count ?? 0,
                out int producedFiles,
                out _)
            && producedFiles >= ProducedContentGate.MinimumFiles;

        _host.RecordPartialPublishCandidate(
            task,
            engineOutputPath,
            engineArchivePath,
            selectedPassword,
            lastLayerManifestEntries,
            lastResult?.FailedEntryNames,
            lastResult?.ReportedSubItemErrors ?? 0,
            engineReportedFailure: lastResult != null && !lastResult.Success,
            detectedErrorType: lastResult?.DetectedErrorType,
            chainProducedContent: producedContent);

        /*
         * ===== 引擎原话落日志（用户 2026-09-27：「引擎原话从不落日志」）=====
         *
         * 位置刻意选在**结论已经定下来之后**（`lastResult` / `task.Status` 都写好了），
         * 而且整层只写一次：
         * · 候选循环里每个错密码都记一条 ERROR 是噪声（十个候选十条），
         *   而真正要看的恰恰是**最后那次**（走得最远的那一次，与失败原因同一份）；
         * · 详细档在循环里已经逐候选写过命令行与原话了，这里不重复。
         *
         * ⛔ 出口只有 <see cref="EngineOutputLog"/> 一个（递归路径同一个）。
         */
        EngineOutputLog.LogFailure(_host.AppendLogPair, task.FileName, lastResult);
    }
}

