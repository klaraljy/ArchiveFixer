using ArchiveFixer.BindingModels;
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
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 「一组分卷的第一卷名字被改坏」时，把**我们自己产出的**那一卷改回标准命名
/// （生命周期：入口层解压之前；失败模型：只对续解任务、只摆正自己的产物，改不动 ⇒ 保持原样并 WARN）。
///
/// <para>它和其余物处理不是同一件事（一个改名字、一个搬/删过程物），所以单独一类；
/// 判据与行为逐字未改：用户自己添加的坏名字分卷走不了这一条（会由「通用分片」闸门如实报分卷缺失）。</para>
/// </summary>
internal sealed class BrokenVolumeChainNameRepairStep
{
    private readonly IRestHandlingStepHost _host;

    internal BrokenVolumeChainNameRepairStep(IRestHandlingStepHost host)
    {
        _host = host;
    }

    /// <summary>
    /// 「一组分卷的第一卷名字被改坏」时，把**我们自己产出的**那一卷改回标准命名（用户 2026-09-25 第 37 条）。
    ///
    /// <para>真机现场：他那个 12.22 GiB 的容器解出来的是三卷 7z，第一卷
    /// <c>Code Complete-BZ.7z(删掉.001</c>（打包者塞了字、吃掉右括号），后两卷名字正常。
    /// 7-Zip 按名字找不到后续卷 → 把它当"通用分片" → 解出来是一个等大的垃圾文件；
    /// 于是那 12 GiB 的内容永远出不来（用户看到的是"20 G 的素材只出来 8 G"）。</para>
    ///
    /// <para>⛔ 只对**续解任务**（文件是我们自己从上一层解出来的过程物）改名：
    /// 那是把我们的产物摆正，不是动用户的源文件（不变量 1）。
    /// 用户自己添加的坏名字分卷走不了这一条 —— 会由后面的「通用分片」闸门如实报「分卷缺失」并给出改名建议。</para>
    ///
    /// <para>改完之后**重拍源文件快照**（不变量 11）：名字换了、路径换了，旧快照对不上会让这一单
    /// 被"源文件已变化"拦下（与"分卷补齐"那条路同一处理）。</para>
    /// </summary>
    internal string TryRepair(ArchiveTask task, string engineArchivePath)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
        {
            return engineArchivePath;
        }

        // 只有"从别的包里解出来的"才允许改名（见方法注释）。
        if (!task.IsContinuationTask)
        {
            return engineArchivePath;
        }

        IReadOnlyList<string?> names = TryListFileNames(Path.GetDirectoryName(task.CurrentPath));

        if (names.Count == 0)
        {
            return engineArchivePath;
        }

        BrokenVolumeChainRepair.RenamePlan? plan = BrokenVolumeChainRepair.TryPlan(task.CurrentPath, names);

        // 名字本来就标准（零条改名）= 什么都不用做，也**不许**记那句"被改坏"的日志。
        if (plan == null || BrokenVolumeChainRepair.IsNoOp(plan))
        {
            return engineArchivePath;
        }

        if (!BrokenVolumeChainRepair.TryApply(plan, out string failure))
        {
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：这一卷的名字看着被改坏了（{plan.Describe()}），但改名没成功：{failure}"
                + " —— 保持原样继续，下面会如实报结论。");

            return engineArchivePath;
        }

        _host.AppendLog(
            "INFO",
            $"{task.LogName}：分卷名字被改坏，已在自己的产物里摆正 —— {plan.Describe()}"
            + $"（源包一个字节都没动；改的是上一层解出来的过程物）。原位置：{task.CurrentPath}");

        string previousPath = task.CurrentPath;
        task.CurrentPath = string.IsNullOrWhiteSpace(plan.FirstVolumePathAfterRename)
            ? previousPath
            : plan.FirstVolumePathAfterRename;

        // 路径换了就必须重拍快照（不变量 11：按"位"比对，旧路径会立刻判成"文件不见了"）。
        task.CaptureSourceSnapshot();

        return task.CurrentPath;
    }

    /// <summary>列一个目录里的**文件名**（读不了就返回空集合，调用方按"没有线索"处理）。</summary>
    internal static IReadOnlyList<string?> TryListFileNames(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string?>();
            }

            return Directory.EnumerateFiles(directory!, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string?>();
        }
    }
}
