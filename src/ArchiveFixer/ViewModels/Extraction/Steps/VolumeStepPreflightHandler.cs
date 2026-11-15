using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 第二大步开工前检查 + 组收片账（M5：原 <c>ExtractionCoordinator.VolumeStepPreflightStep.cs</c>
/// 已整份删除，成员全部并进本类）：缺卷不开工 / 缺卷不在批首判死 / 最后一遍仍缺才如实报缺卷；
/// 组收片账（基名 + 入口层 + 片清单）的登记与清空、入口层临时标准名目录与遗留壳的删除、
/// 成功收片失败留片；缺卷补救子系统 <see cref="VolumeRepairHandler"/> 的唯一持有者。
///
/// <para>⛔ 判据、文案、日志顺序、删除路径一个字没改；本类只持有宿主
/// <see cref="ExtractionCoordinator"/>，两本账与缺卷补救子系统都在本类里（宿主不再留同名薄转发）。</para>
/// </summary>
internal sealed class VolumeStepPreflightHandler
{
    private readonly ExtractionCoordinator _host;

    internal VolumeStepPreflightHandler(ExtractionCoordinator host)
    {
        _host = host;
    }

    /*
     * ⚠ 本类由宿主**构造时建一次**（`ExtractionCoordinator` 的 `_volumeStepPreflight`），⛔ 不懒建：
     * 下面两本账是无锁字典，批内任务线程会同时读写；无锁懒建一旦造出第二个实例，账就会分成两半。
     */

    /// <summary>第二大步入口任务 ⇒ 它这一组的基名/入口层/片清单（成功后收片与回写用；批首清空）。</summary>
    private readonly Dictionary<ArchiveTask, VolumeStepGroupWork> _groupWork = new();

    /// <summary>第二大步每组一句话结论（批末回写读它；⛔ 不从日志反推）。</summary>
    private readonly Dictionary<string, string> _groupVerdicts = new(StringComparer.OrdinalIgnoreCase);

    private VolumeRepairHandler? _volumeRepairHandler;

    /// <summary>缺卷补救与批前收卷的唯一实例（本类是它唯一的调用方；实现见 <see cref="VolumeRepairHandler"/>）。</summary>
    private VolumeRepairHandler VolumeRepair => _volumeRepairHandler ??= new VolumeRepairHandler(_host);

    /// <summary>组收片账（别的协调类读）：入口任务 ⇒ 基名/入口层/片清单。</summary>
    internal IReadOnlyDictionary<ArchiveTask, VolumeStepGroupWork> GroupWork => _groupWork;

    /// <summary>第二大步每组一句话结论（别的协调类读；⛔ 不从日志反推）。</summary>
    internal IReadOnlyDictionary<string, string> GroupVerdicts => _groupVerdicts;

    /// <summary>组收片账的一条：基名 + 入口层 + 片清单。</summary>
    internal sealed record VolumeStepGroupWork(string BaseName, string EntryLayer, IReadOnlyList<string> Pieces);

    /// <summary>
    /// 第二大步开工前的唯一出口：返回 <c>true</c> = 这一单**这一次不开工**（调用方直接 return）。
    /// 判据、文案、账本全部沿用原实现，⛔ 一个字没改。
    /// </summary>
    private async Task<bool> TryStopVolumeStepBeforeStartAsync(
        ArchiveTask task,
        bool oneClickRun,
        CancellationToken cancellationToken)
    {
        if (!task.IsVolumeGroup || task.IsVolumeComplete)
        {
            return false;
        }

        /*
         * 一键处理期间**不问**"缺失卷在哪个目录"（用户 2026-09-27：一键解压不许弹窗）。
         * 手动档问一次（用户就在旁边），一键档直接按"不问"往下走 ——
         * ⚠ 但结论**不再当场落死**了，见下面那两段（用户 2026-10-05 口径 2）。
         */
        bool repaired = !oneClickRun
            && await VolumeRepair.TryRepairMissingVolumesAsync(task, cancellationToken);

        if (!repaired)
        {
            /*
             * ⚠ 按用户 2026-10-24 口径：原来这一段里"现读一次盘上事实、齐了就照常跑"
             * （`ResolveCompleteGroupFromDisk` + `ApplyGroupInfo` + 换起点 + 重拍快照）**已删** ——
             * 那个能力在第二大步侧本来就有（`DeferredVolumeDeficitStep` 用的就是同一对出口），
             * 这里不再重复一遍。
             */

            /*
             * ===== 缺卷**不许在批首一次判死**（用户 2026-10-05 口径 2）=====
             *
             * 旧写法在这里当场写一句"分卷不完整 …… 本次不开始"并把这一单落成失败 —— 真机上
             * `.002` 就是这么在 16:29:33（批首那一刻）被判死的，而它的 `.001` 正躺在同一棵树里、
             * 由同一批的另一个包解出来。
             *
             * 现在的口径：这一单**现在确实没有开始解压**（一个字节都没动，不变量 7 一个字节没放松），
             * 但"还缺哪几卷"这句话留到最后再说 —— 先记进名单，等这一批的解压都跑完之后由
             * `RecheckDeferredVolumeDeficits` 再判一次（那时缺的那几卷可能已经被解出来 / 收拢到位）。
             *
             * ⚠ 兜底：批末补判**必须**给名单里每一条一个结论（补齐 ⇒ 跑；仍然缺 ⇒ 如实报缺卷）。
             * 唯一"没有结论"的出口是"整条链跑到一半被用户停下 / 撞上轮数上限" —— 那时它留在
             * 「未处理」那一档，如实反映"这一批没轮到它"。
             */
            if (!_host._volumeLedgerState.VolumeDeficitFinalPass.Contains(task))
            {
                _host.VolumeLedger.RecordDeferredVolumeDeficit(task, StalledGroupHandler.DescribeMissingVolumeNames(task));
                return true;
            }

            /*
             * ===== ① 解前预检：**缺卷就别开解，而且要直接说清缺哪一卷 / 为什么这样判** =====
             *
             * 走到这里 = 批末补判已经跑过它一遍、盘上仍然凑不齐 ⇒ **这才如实报缺卷**。
             *
             * 判据走唯一出口 <see cref="VolumeGroupResolver"/>（分卷组装判定器）：
             * 它把六条证据（基名 / 卷号 / 体积 / 位置 / 物理同一性 / 试开）收在一处，
             * 只吐一个结论。试开那一档在**这里**做（有引擎、且是只读的硬链接试开）——
             * 于是"末卷被改名成 一只顶美.z删除ip"这种情形能被认出来是**齐的**，
             * 而不是像老判据那样一律报「分卷缺失」把用户指去满盘找卷。
             *
             * ⛔ 不管判出什么，**都不改变"这一单开不开"**：名字不标准 ⇒ 7-Zip 按原名打不开 ⇒
             * 照旧不开始（不变量 7）。这里换掉的只是**诊断**：说得对不对、依据在不在。
             */
            VolumeGroupResolution volumeVerdict = await _host.ResolveVolumeGroupForVolumeStepPreflightAsync(task, cancellationToken)
                .ConfigureAwait(true);

            string diagnosis = ExtractionCoordinator.BuildVolumeVerdictMessageForVolumeStepPreflight(task, volumeVerdict);

            /*
             * 判据走**开工前拦下的统一收口**（与"源文件已变化"同一个出口）：
             * 状态照旧是「分卷缺失」（中文一个字不改），但"这一单已经结束"这件事必须一起落 ——
             * 少了结束时间，机器终态的唯一收口补不了 Failed，它就一直停在 Pending：
             * 批末汇总说「未处理 1」、批末诊断说「分卷缺失」，两处口径当场打架
             * （2026-09-30 真机 `222.z01`）。
             */
            _host.MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, diagnosis, TaskReason.VolumeMissing);

            _host.AppendLog("ERROR", $"分卷缺失，未开始解压：{task.FileName}，{diagnosis}");
            _host.LogVolumeGroupEvidenceForVolumeStepPreflight(task, volumeVerdict);
            return true;
        }

        /*
         * 用户刚把缺的卷找回来了（或者刚由"现读一次盘上事实"确认它已经齐了）：
         * 基准必须**跟着重拍**（不变量 11 与这条补救出口的和解）。
         *
         * 不重拍的话，新找回来的那一卷不在快照里，比对时它会被算成"新出现的文件" →
         * 用户按提示把卷补齐了，反而被"源文件已变化"拦下，而且再补多少次都一样 ——
         * 那句提示就成了死循环。补齐分卷是**改变源文件组**的正当操作，
         * 所以基准从这里重新开始（与"重新扫描"同一条道理）。
         */
        task.CaptureSourceSnapshot();

        _host.AppendLog("INFO", $"{task.LogName}：分卷已补齐，源文件快照重新记录（{task.VolumeCount} 卷）。");

        return false;
    }

    /// <summary>B/C 段点名入口任务时登记这一组的收片账（基名 + 入口层 + 片清单）。</summary>
    internal void RememberVolumeStepGroupWork(
        ArchiveTask task,
        string baseName,
        string entryLayer,
        IReadOnlyList<string> pieces)
    {
        if (task == null || string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(entryLayer))
        {
            return;
        }

        _groupWork[task] = new VolumeStepGroupWork(baseName, entryLayer, pieces);
    }

    /// <summary>
    /// **P3b 第二步：成功收片 / 失败留片**（用户 2026-10-21 口径）。
    ///
    /// <para>成功（校验通过）⇒ 入口层里这一组的片按过程物删掉，**用户导入的原件不删**（唯一判据
    /// <see cref="PieceAdoptionHandler.IsUserImportedSourcePath"/>）；失败 / 密码错 / 缺卷 ⇒ 调用方**不调**本方法，
    /// 片全部留在入口层等补密码（这里也留一道保险：没成功只写一行、一个字节不动）。</para>
    /// </summary>
    /// <summary>
    /// 任务收尾时清理这一单临时标准名入口目录；源片不动，只删
    /// <c>.ArchiveFixer.work\group-entry</c> 下的硬链接壳。
    /// </summary>
    internal void CleanupVolumeStepGroupEntry(ArchiveTask task)
    {
        if (task == null || !_groupWork.TryGetValue(task, out VolumeStepGroupWork? work))
        {
            return;
        }

        ArchiveFixer.Extraction.VolumeStep.VolumeGroupEntryDirectory.TryDelete(work.EntryLayer);
        _groupWork.Remove(task);
    }

    /// <summary>批末清理所有临时标准名入口目录；只删 group-entry 壳，源片不动。</summary>
    internal void CleanupAllVolumeStepEntryDirectories()
    {
        foreach (VolumeStepGroupWork work in _groupWork.Values)
        {
            ArchiveFixer.Extraction.VolumeStep.VolumeGroupEntryDirectory.TryDelete(work.EntryLayer);
        }

        _groupWork.Clear();
    }

    /// <summary>
    /// 批末兜底：从每个任务落点里删掉遗留的 .ArchiveFixer.work\group-entry 壳。
    /// 只删这一层临时硬链接目录，不碰源片、不碰内容物。
    /// </summary>
    internal void CleanupGroupEntryShellsFor(IEnumerable<ArchiveTask>? tasks)
    {
        if (tasks == null)
        {
            return;
        }

        foreach (ArchiveTask task in tasks)
        {
            if (task == null || string.IsNullOrWhiteSpace(task.OutputPath))
            {
                continue;
            }

            string work = Path.Combine(task.OutputPath, ".ArchiveFixer.work");
            string group = Path.Combine(work, "group-entry");

            try
            {
                if (Directory.Exists(group))
                {
                    Directory.Delete(group, recursive: true);
                }

                if (Directory.Exists(work)
                    && !Directory.EnumerateFileSystemEntries(work).Any())
                {
                    Directory.Delete(work);
                }
            }
            catch
            {
                // 清不掉不影响内容物；下一轮再由工作区清理兜底。
            }
        }
    }

    internal void TryPurgeSolvedVolumeStepPieces(ArchiveTask task)
    {
        if (task == null || !_groupWork.TryGetValue(task, out VolumeStepGroupWork? work))
        {
            return;
        }

        if (task.Outcome != TaskOutcome.Succeeded)
        {
            /*
             * ⚠ 这里不能写 `task.Outcome`：收片紧跟在解压之后，失败那一档的机器终态要到
             * `RunScheduledTaskAsync` 的收尾才落（此刻常是 `Pending`）⇒ 写出来会像"没结论"。
             * 按事实说"本次没成功"，具体原因由任务自己的失败行给。
             */
            _host.AppendLog(
                "INFO",
                $"[第二大步·收片] {task.LogName}：本次没成功 ⇒ 入口层里的片全部留着"
                + "（失败 / 密码错 / 缺卷不删，等补密码）。");

            _host.VolumeHandoff.Ledger.MarkVolumeHandoffVerdict(
                work.Pieces,
                "没成功（密码错 / 缺卷 / 失败），片全部留在入口层");

            _groupVerdicts[work.BaseName] = "没成功（密码错 / 缺卷 / 失败），片全部留在入口层";

            return;
        }

        int deleted = ArchiveFixer.Extraction.VolumeGroupEntryLayer.PurgeSolvedGroupPiecesInEntryLayer(
            work.Pieces,
            work.EntryLayer,
            path => _host.PieceAdoption.IsUserImportedSourcePath(path),
            (level, message) => _host.AppendLog(level, $"[第二大步·收片] {message}"));

        /*
         * prepared 组的标准名硬链接在源卷临时入口目录里：收完片把这一组自己的空壳也删掉。
         * TryDelete 只认 `.ArchiveFixer.work\group-entry` 下的目录，普通源层目录一律不碰。
         */
        bool removedEntryDirectory = ArchiveFixer.Extraction.VolumeStep.VolumeGroupEntryDirectory.TryDelete(work.EntryLayer);

        /*
         * 入口层链接不是物理片的全部名字：同一组的过程物可能散在多个包装输出目录。
         * 组成功后按 work.Pieces 的原始路径逐个收：用户原包不动，其余过程物全删。
         * ⛔ 不写死片数，也不只删入口层链接。
         */
        int removedProcessPieces = 0;
        foreach (string piecePath in work.Pieces)
        {
            if (string.IsNullOrWhiteSpace(piecePath)
                || !File.Exists(piecePath)
                || _host.PieceAdoption.IsUserImportedSourcePath(piecePath))
            {
                continue;
            }

            try
            {
                FileOperations.Delete(piecePath);
                removedProcessPieces++;
            }
            catch (Exception ex)
            {
                _host.AppendLog(
                    "WARN",
                    $"[第二大步·收片] {task.LogName}：过程物删不掉：{piecePath}（{ex.Message}）");
            }
        }

        _host.AppendLog(
            "INFO",
            $"[第二大步·收片] {task.LogName}：成功 ⇒ 入口层里删掉 {deleted} 片；"
            + $"按组删掉过程物 {removedProcessPieces} 片（用户原件不删）"
            + (removedEntryDirectory ? "；临时标准名目录已清理" : string.Empty)
            + "。");

        _host.MarkVolumeGroupPiecesConsumed(work.Pieces);

        _host.VolumeHandoff.Ledger.MarkVolumeHandoffVerdict(
            work.Pieces,
            $"解压成功，入口层收片 {deleted} 片（用户原件不删）");

        _groupVerdicts[work.BaseName] = $"解压成功，入口层收片 {deleted} 片（用户原件不删）";

        _groupWork.Remove(task);
    }
}
