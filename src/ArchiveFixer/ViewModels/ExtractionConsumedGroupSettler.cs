using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>成功组的生产者/父任务/跟班卷收场与"谁消费了这片"解析（从 ExtractionCoordinator.ConsumedSettlementStep.cs 搬出）。</summary>
internal sealed class ExtractionConsumedGroupSettler
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionConsumedGroupSettler(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>遍历"谁在解"的账：消费方**已经成功**的那几组，给它们的生产者收场（幂等；见 <see cref="_host.SettleGroupPieceProducers"/>）。</summary>
    internal void SettleSucceededGroupPieceProducers()
    {
        if (_host._groupConsumerByName.Count == 0 || _host.GroupPieceProducerCount == 0)
        {
            return;
        }

        var logEntries = new List<(string Level, string Message)>();

        foreach (string consumerName in _host._groupConsumerByName.Values.ToList())
        {
            ArchiveTask? consumer = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks).FirstOrDefault(
                candidate => candidate != null && string.Equals(candidate.FileName, consumerName, StringComparison.Ordinal));

            if (consumer == null || consumer.Outcome != TaskOutcome.Succeeded)
            {
                continue;
            }

            _host.SettleGroupPieceProducers(consumer, moveSources: true, logEntries);
        }

        foreach ((string level, string message) in logEntries)
        {
            _host.AppendLog(level, message);
        }
    }

    /// <summary>
    /// 内层分卷组由子任务解开后，**产出该组的父任务**也要收场。
    ///
    /// <para>真机 23.5：`导数的几何意义-polyflix.mp4` 先把 `.001/.002/.003` 解出来，
    /// 因为 `.003` 被误当独立包，父任务停在 `VolumeMissing/PartiallyCompleted`；
    /// 后来 `.001` 那一单成功，父任务却没人回写。判据只读两条既有事实：
    /// ① 子任务可证完整且成功；② 父任务的 `ProducedFiles` 里确实有这一组的片。
    /// ⛔ 不按名字猜、不碰源包、不改变其他父任务。</para>
    /// </summary>
    internal void SettleParentOfSucceededVolumeGroupForTests() =>
        SettleParentOfSucceededVolumeGroup();

    internal void SettleParentOfSucceededVolumeGroup()
    {
        foreach (ArchiveTask consumer in ExtractionCoordinator.SnapshotTaskTable(_host.Tasks))
        {
            if (consumer == null
                || consumer.Outcome != TaskOutcome.Succeeded
                || !consumer.IsVolumeGroup
                || !consumer.IsVolumeComplete)
            {
                continue;
            }

            string groupBase = FileNameHelper.GetArchiveBaseName(consumer.CurrentPath ?? string.Empty);

            if (groupBase.Length == 0)
            {
                continue;
            }

            ArchiveTask[] tasks = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks);

            ArchiveTask? parent = tasks.FirstOrDefault(
                candidate => candidate != null
                    && !ReferenceEquals(candidate, consumer)
                    && (!string.IsNullOrWhiteSpace(consumer.ParentTaskName)
                        && (string.Equals(candidate.FileName, consumer.ParentTaskName, StringComparison.Ordinal)
                            || string.Equals(candidate.DisplayFileName, consumer.ParentTaskName, StringComparison.Ordinal))));

            /*
             * 第二大步补建的入口任务，ParentTaskName 可能是**组基名**而不是父任务文件名
             * （真机 23.5：入口任务是组边界补建的，父任务名不在任务表里）。
             * ⇒ 再按既有事实找：谁的 ProducedFiles 里确实有这一组的片，谁就是产出方父任务。
             */
            parent ??= tasks.FirstOrDefault(candidate =>
                candidate != null
                && !ReferenceEquals(candidate, consumer)
                && candidate.ProducedFiles.Any(path =>
                    string.Equals(
                        FileNameHelper.GetArchiveBaseName(path),
                        groupBase,
                        StringComparison.OrdinalIgnoreCase)));

            if (parent == null
                || parent.Outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped or TaskOutcome.Cancelled
                || (parent.Outcome == TaskOutcome.Failed && parent.Reason != TaskReason.VolumeMissing))
            {
                continue;
            }

            if (!parent.ProducedFiles.Any(path =>
                    string.Equals(
                        FileNameHelper.GetArchiveBaseName(path),
                        groupBase,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            /*
             * 消费者成功只解除父任务的 deferred 标记；父任务自己的终态/状态不由它回写。
             * 父任务有没有产物证据，由它自己的收尾阶段决定。
             */
            parent.IsVolumeDeficitDeferred = false;
            _host._volumeDeficitDeferred.Remove(parent);
            _host._volumeDeficitFinalPass.Remove(parent);

            _host.AppendLog(
                "INFO",
                $"{parent.LogName}：产出分卷组「{groupBase}」的子任务「{consumer.LogName}」已成功，"
                + "父任务不再按缺卷/部分完成收场。");
        }
    }

    /// <summary>
    /// **已验证组里的跟班片**：源片按「彻底删除」收掉（2026-10-18 用户 `&&` 口径，`22222222` 现场）。
    ///
    /// <para>跟班片（<see cref="ArchiveTask.IsVolumeGroupFollower"/> + 机器终态成功）的源片有时
    /// 不在“谁吐过片”那本账里（它没吐过东西、只是被借去拼组）⇒ `_host.SettleGroupPieceProducers` 扫不到它，
    /// 源片一直留着。判据只要两条事实位：① 跟班 + 成功；② 同一组里有一个**可证完整 + 内容校验通过**
    /// 的消费方（`ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval`）——
    /// 这就是 `&&` 的第二步。⛔ 组不完整 / 校验没过 / 判不出消费方 ⇒ 什么都不做；幂等（Done 跳过）。</para>
    /// </summary>
    internal void SettleVerifiedGroupFollowers()
    {
        SourceHandlingMode handling = _host._spaceTightPolicy.Enabled
            ? SourceHandlingMode.DeleteInPlace
            : _host.RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(_host.Settings?.SourceHandling);

        if (handling != SourceHandlingMode.DeleteInPlace)
        {
            return;
        }

        var logEntries = new List<(string Level, string Message)>();
        ArchiveTask[] tasks = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks);

        foreach (ArchiveTask task in tasks)
        {
            if (task != null && task.IsVolumeGroupFollower)
            {
                logEntries.Add((
                    "INFO",
                    $"{task.LogName}：跟班源片结算判据｜成功={task.Outcome == TaskOutcome.Succeeded}"
                    + $"｜已处理={task.SourcePackageMove}｜组名=「{task.SettledWithGroupUnitName}」"
                    + $"｜当前=「{task.CurrentPath}」｜原始=「{task.OriginalPath}」"));
            }

            /*
             * ⛔ **已标 Done、但改名前的原始那一份还在盘上**（2026-10-18 `22222222` 现场）：
             * `part1869删除2` 的 `CurrentPath` 被改写成组里的 `part1.rar`，早先那次清理删的是
             * `part1.rar` 并把它标成 Done；可用户导入的那个**原始名字**（`OriginalPath`）还在
             * —— 两个名字是同一份字节的硬链接，删一个不等于删干净。⇒ 只对“跟班里已证完整 +
             * 原始那份确实还在”这一种，允许再走一次删源（`ignoreDoneState`），别的 Done 一律不动。
             */
            bool recoveryOfOriginal = task != null
                && task.SourcePackageMove == SourcePackageMoveState.Done
                && !string.IsNullOrWhiteSpace(task.OriginalPath)
                && File.Exists(task.OriginalPath);

            if (task == null
                || task.IsContinuationTask
                || !task.IsVolumeGroupFollower
                || (task.SourcePackageMove == SourcePackageMoveState.Done && !recoveryOfOriginal)
                || task.Outcome != TaskOutcome.Succeeded)
            {
                continue;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

            /*
             * 消费方先从**跟班片自己记下的那个事实**找（`SettledWithGroupUnitName`，由
             * `SettleOneConsumedPieceTask` 在“这一组由 X 解开”那一刻写死）—— 脏名字底下
             * `GetArchiveBaseName` 可能给出两个不同的基名，按基名会漏（`22222222` 现场：
             * `part1869删除2` vs `part391392885删除1`）。判不出再退回基名那一档。
             */
            ArchiveTask? consumer = null;
            string settledWith = task.SettledWithGroupUnitName ?? string.Empty;

            if (settledWith.Length > 0)
            {
                consumer = tasks.FirstOrDefault(candidate =>
                    candidate != null
                    && candidate.Outcome == TaskOutcome.Succeeded
                    && candidate.IsVolumeGroup
                    && candidate.IsVolumeComplete
                    && (string.Equals(candidate.DisplayFileName, settledWith, StringComparison.Ordinal)
                        || string.Equals(candidate.FileName, settledWith, StringComparison.Ordinal)));
            }

            if (consumer == null && baseName.Length > 0)
            {
                consumer = tasks.FirstOrDefault(candidate =>
                    candidate != null
                    && candidate.Outcome == TaskOutcome.Succeeded
                    && candidate.IsVolumeGroup
                    && candidate.IsVolumeComplete
                    && string.Equals(
                        FileNameHelper.GetArchiveBaseName(candidate.CurrentPath ?? string.Empty),
                        baseName,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (consumer == null || !consumer.IsVolumeComplete)
            {
                continue;
            }

            ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(consumer);

            if (!verdict.AllowsSourceRemoval)
            {
                continue;
            }

            /*
             * 组任务的删除清单只装 `VolumePaths`；改名前的**原始那一份**不在里面，而这一单自己的
             * 校验又被跟班收场清过 ⇒ `BuildTargetList` 不会把 `OriginalPath` 放进去（`22222222` 现场：
             * `part1869删除2` 的当前路径是组里的 `part1.rar`，原始名字还在盘上）。
             * ⇒ 用一个只代表“这一份原始文件”的临时任务走**同一个删除出口**（⛔ 不另写删除代码），
             * 并沿用跟班 + 消费方 verdict 那两道事实。
             */
            ArchiveTask purgeTask = task;
            string originalPath = task.OriginalPath ?? string.Empty;
            bool purgeOriginalOnly = task.IsVolumeGroup
                && !string.IsNullOrWhiteSpace(originalPath)
                && File.Exists(originalPath);

            if (purgeOriginalOnly)
            {
                purgeTask = new ArchiveTask(originalPath)
                {
                    IsVolumeGroupFollower = true
                };
            }

            _host.PurgeLayerSourcePackage(
                purgeTask,
                verification: null,
                commit: null,
                logEntries,
                LayerPurgeTrigger.SourceHandlingDelete,
                reclaimVerdict: verdict,
                donePolicy: recoveryOfOriginal || purgeOriginalOnly
                    ? SourcePackageDonePolicy.AllowRecovery
                    : SourcePackageDonePolicy.Respect);

            if (purgeOriginalOnly && !File.Exists(originalPath))
            {
                // 原始那一份已经删掉 ⇒ 把真任务也记成“处理过”，避免后续每轮再扫一遍。
                task.SourcePackageMove = SourcePackageMoveState.Done;
            }
        }

        foreach ((string level, string message) in logEntries)
        {
            _host.AppendLog(level, message);
        }
    }

    /// <summary>
    /// 这一单所在的那一组，**是不是正在被本批另一单解**（而它还没收尾）⇒ 这一单先不落结论。
    /// 判据只有两条事实位：① 组基名在那份"谁在解"的账上；② 那位消费方**还没有机器终态**。
    /// </summary>
    internal bool TryGetGroupConsumerPending(ArchiveTask task, out string consumer)
    {
        consumer = string.Empty;

        string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

        if (baseName.Length == 0
            || !_host._groupConsumerByName.TryGetValue(baseName, out string? name)
            || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (string.Equals(name, task.FileName, StringComparison.Ordinal))
        {
            return false;   // 自己就是那个消费方
        }

        ArchiveTask? owner = ExtractionCoordinator.SnapshotTaskTable(_host.Tasks).FirstOrDefault(
            candidate => candidate != null && string.Equals(candidate.FileName, name, StringComparison.Ordinal));

        bool unfinished = owner == null
            || owner.Outcome is not (TaskOutcome.Succeeded
                or TaskOutcome.Failed
                or TaskOutcome.PartiallyCompleted
                or TaskOutcome.Cancelled
                or TaskOutcome.Skipped);

        if (!unfinished)
        {
            return false;
        }

        consumer = name;
        return true;
    }

    /// <summary>这一单的分卷，**是不是已经被本批另一个任务解出来了**（2026-10-05 真机第八批）。
    ///
    /// <para><b>现场</b>：一组跨盘 ZIP 的末片压在两层层层加密的 RAR 里、其余三片散在三个源目录里。
    /// 递归层用硬链接把那三片接过来、整组解开、结果发布成功 —— 可那三片自己那一单在批末仍然
    /// 如实报「分卷缺失」⇒ 汇总写成「成功 1 / 失败 3」，批末还指路"把缺的那几卷放到同一个目录里"
    /// （用户按这条去搬，只会白忙一场：内容已经在出来了）。</para>
    ///
    /// <para>判据只有两件事，都是**事实位**：① 这一单自己（或账上那几卷）出现在
    /// <see cref="_host._consumedVolumeSources"/> 里（写入点唯一 = 递归结果带回来的那份名单）；
    /// ② 那个消费方这一批**真的成功了**（机器终态）。两条都成立 ⇒ 按跟班卷处理。</para>
    /// </summary>
    internal bool TryResolveConsumedByAnotherTask(ArchiveTask task, out string consumerName)
    {
        consumerName = string.Empty;

        if (task == null || _host._consumedVolumeSources.Count == 0)
        {
            return false;
        }

        /*
         * ===== 判据 = **这一单自己那一片**有没有进过那份"借片账" =====
         *
         * ⛔ 比的是**同一份文件**，⛔ 不是路径字符串（真机 CCCC 2026-10-06 20:55，用户原话：
         * 「为什么文件真正的名字不看」）：接片那一档在"这一组该在的那一层"另起了一个**零字节硬链接**
         * （`…\111\111\111.z03`），而用户那一份还在他自己那个目录里（`…\111(4)\111.z03`）
         * —— 两条路径指着**同一份字节**。账里记的可能只是其中一条 ⇒ 按路径比永远对不上
         * ⇒ 那一单落到「判不出 ⇒ 什么都不做」，源片一直留着。
         */
        var ownPaths = new List<string>(ExtractionCoordinator.EnumerateTaskPaths(task));
        string ownOnDisk = SourcePackageMover.ResolveOwnFileOnDisk(task);

        if (ownOnDisk.Length > 0)
        {
            ownPaths.Add(ownOnDisk);
        }

        foreach ((string piecePath, string ownerName) in _host._consumedVolumeSources.ToList())
        {
            bool mine = false;

            foreach (string path in ownPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (SafePathHelper.PathEquals(path, piecePath) || FileIdentity.IsSamePhysicalFile(path, piecePath))
                {
                    mine = true;
                    break;
                }
            }

            if (!mine || string.IsNullOrWhiteSpace(ownerName))
            {
                continue;
            }

            /*
             * ⚠ **判据输入**（2026-10-11，真机 EEE 定案用）：这一档一旦命中，调用方就**清落点 / 清校验 /
             * 落「跟班卷」**，可"到底是哪一条账目匹配上的"原来一个字都不写 —— 于是真机 EEE 只看到
             * `111.part1.rar` 被算成跟班卷，看不到它被**哪一份文件**（物理同一 / 同一条路径）匹配上的。
             * 按"判不出就把判据输入写进日志"那条规矩补一行 WARN。⛔ 只写事实，不改判据。
             */
            _host.AppendLog(
                "WARN",
                $"借片账命中：「{FileNameHelper.GetFileName(task.CurrentPath)}」这一单自己的文件与账上那一片"
                + $"（{piecePath}）是同一份物理文件（或同一条路径）⇒ 按已由「{ownerName}」接手收场"
                + $"｜判据输入：本单路径={task.CurrentPath}，匹配上的账目=「{piecePath}」→ 接手方「{ownerName}」");

            ArchiveTask? owner = null;

            foreach (ArchiveTask candidate in ExtractionCoordinator.SnapshotTaskTable(_host.Tasks))
            {
                if (candidate == null || !string.Equals(candidate.FileName, ownerName, StringComparison.Ordinal))
                {
                    continue;
                }

                /*
                 * ⛔ **借片的那一单不是消费方**（真机 CCCC 2026-10-06 实测）：批末补判会把补齐了那一单的
                 * `CurrentPath` 改到入口包上 ⇒ 它与真正的消费方**撞名**（两个都叫 `111.zip`）。
                 */
                if (IsBorrowedPieceTask(candidate))
                {
                    continue;
                }

                owner = candidate;
                break;
            }

            if (owner == null || owner.Outcome != TaskOutcome.Succeeded)
            {
                continue;
            }

            consumerName = ownerName;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 这一单**自己那一片借给过别的单**吗（判据只有一条：它自己的某条路径是那份"借片账"的**键**）。
    ///
    /// <p>为什么要它：那份账记的消费方是文件名，而"补齐之后起点被改到入口包上"的那一单会与真正的
    /// 消费方**撞名** ⇒ 按名字回查必须先认出"这是借片的那一单，不是消费方"（真机 CCCC 2026-10-06）。</p>
    /// </summary>
    internal bool IsBorrowedPieceTask(ArchiveTask task)
    {
        if (task == null || _host._consumedVolumeSources.Count == 0)
        {
            return false;
        }

        /*
         * ⚠ **只问"它自己那一份"两条路径**（`OriginalPath` / `CurrentPath`），⛔ **不许连 `VolumePaths` 一起问**：
         * `VolumePaths` 是"这一组那几卷"的账，里面有**别的单**的片（真机：消费方 `111.zip` 那一单的
         * `VolumePaths` 里就有 `111.z0删除2/3`）—— 一起问会把**真正的消费方**误判成"借片的那一单"，
         * 于是谁也当不成消费方（实测踩到）。
         */
        foreach (string path in ExtractionCoordinator.EnumerateOwnPaths(task))
        {
            if (!string.IsNullOrWhiteSpace(path) && _host._consumedVolumeSources.ContainsKey(path))
            {
                return true;
            }
        }

        return false;
    }
}

