using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 其余物清理的**预览**：动手之前给用户看的那份清单（规格 §3.2 要求"列出路径数量与总大小"）。
///
/// <para>
/// 预览**不删除任何东西**，也不会为了预览去造删除请求：它只做三件事 ——
/// 拿作用域（<see cref="ArtifactCleanupScopeResolver"/>）、把目录内容读成清单
/// （<see cref="ArtifactDirectoryInspector"/>）、用删除服务自身的度量算出条目数与总大小。
/// 度量特意走 <see cref="RecycleBinService.MeasureTarget"/>：与真正删除时是同一段代码，
/// 这样预览里报的数字和删除日志里的数字不会互相打架。
/// </para>
/// <para>
/// ⚠ 预览算出来的作用域与删除计划会**原样放进结果**（<see cref="CleanupPreview.ResolvedScope"/> /
/// <see cref="CleanupPreview.Plan"/>），执行阶段直接复用 —— "预览给用户看的是 A、执行删的是 B"
/// 正是上一个版本的缺陷形态。
/// </para>
/// </summary>
public sealed class ArtifactCleanupPreviewer
{
    private readonly CleanupDeleteServices _deleteServices;

    /// <param name="deleteServices">注入项装配（预览的度量与执行时的删除必须来自同一份）。</param>
    public ArtifactCleanupPreviewer(CleanupDeleteServices deleteServices)
    {
        _deleteServices = deleteServices;
    }

    /// <summary>
    /// 预览其余物删除：条目数 + 总大小 + 顶层条目清单 + 源包识别。**不删除任何东西**。
    /// </summary>
    public CleanupPreview Preview(
        ArchiveTask? task,
        ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
    {
        ArtifactCleanupScope scope = ArtifactCleanupScopeResolver.Resolve(task, deleteScope);

        if (!scope.IsResolved || scope.RefusedUnsafe)
        {
            /*
             * "确实没有"与"不敢删"必须分开说：
             * 前者是正常状态（用户点了一下，程序说这儿没东西），后者要让用户知道程序为什么不动手。
             *
             * ⚠ 拒绝的那一档必须走这里，**不能**掉到下面的"列清单"分支去：
             * 拒绝时 ArtifactDirectories 里放的是"本该属于本任务、但不存在"的候选路径
             * （为了让日志能说清它去哪儿找过），拿它去列目录只会得到一句
             * "其余物目录是空的"，把"我拒绝了"说成"这里没东西" —— 用户会以为作用域算错了。
             */
            return new CleanupPreview
            {
                Scope = CleanupScope.Artifacts,
                ScopePath = scope.ArtifactDirectory,
                AllowedRoot = scope.AllowedRoot,
                ResolvedScope = scope,
                HasTarget = false,
                Message = scope.RefusedUnsafe
                    ? "已拒绝删除其余物：" + scope.BlockReason
                    : scope.BlockReason
            };
        }

        var items = new List<string>();
        var sourcePackages = new List<string>();
        RecycleBinService deleteService = _deleteServices.CreateDeleteService();
        var entryCount = 0;
        long totalBytes = 0;
        bool determined = true;

        DeletionPlan plan = scope.ToDeletionPlan("手工清理其余物");

        foreach (string directory in plan.Targets)
        {
            // 容器内校验：目标必须落在允许根之内（不变量 4 的同一口径，不另写前缀比较）。
            if (!ArchivePathGuard.IsInsideRoot(plan.AllowedRoot, directory, out string reason))
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.Artifacts,
                    ScopePath = directory,
                    AllowedRoot = plan.AllowedRoot,
                    ResolvedScope = scope,
                    Plan = plan,
                    HasTarget = false,
                    Message = $"其余物目录不在允许的范围内，已拒绝删除 —— {reason}"
                };
            }

            if (!Directory.Exists(directory))
            {
                // 解析时判过存在性，但那是两次系统调用之间的时间差（TOCTOU 的温和版本）；
                // 这里再确认一次，让"预览说有、执行时没了"变成一句明确的话。
                continue;
            }

            IReadOnlyList<string> directoryItems;
            IReadOnlyList<string> directoryPackages;

            try
            {
                directoryItems = ArtifactDirectoryInspector.ListTopLevelEntries(directory);
                directoryPackages = ArtifactDirectoryInspector.FindSourcePackageNames(directory, directoryItems, task);
            }
            catch (Exception ex)
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.Artifacts,
                    ScopePath = directory,
                    AllowedRoot = scope.AllowedRoot,
                    ResolvedScope = scope,
                    HasTarget = false,
                    Message = $"读不了其余物目录（{ex.Message}），已放弃删除：{directory}"
                };
            }

            // 度量走 RecycleBinService.MeasureTarget：与真正删除时用的是同一段代码，
            // 预览里报的数字和删除日志里的数字不会互相打架。
            DeleteMeasurement measurement = deleteService.MeasureTarget(directory);

            items.AddRange(directoryItems);
            sourcePackages.AddRange(directoryPackages);
            entryCount += measurement.EntryCount;
            totalBytes += measurement.TotalBytes;
            determined &= measurement.Determined;
        }

        if (items.Count == 0)
        {
            return new CleanupPreview
            {
                Scope = CleanupScope.Artifacts,
                ScopePath = scope.ArtifactDirectory,
                AllowedRoot = scope.AllowedRoot,
                ResolvedScope = scope,
                HasTarget = false,
                Message = $"其余物目录是空的，没有可删除的内容：{scope.ArtifactDirectory}"
            };
        }

        string message =
            $"将删除其余物：{string.Join("、", plan.Targets)}"
            + $"（顶层 {items.Count} 项，共 {entryCount} 个条目 / {totalBytes} 字节）"
            + (determined ? string.Empty : "，其中部分内容读不到，数字可能不全");

        if (sourcePackages.Count > 0)
        {
            message += $"；⚠ 其中含 {sourcePackages.Count} 个源包文件（删掉后需要重新下载）";
        }

        /*
         * 「删除本目录全部其余物」要点名"影响几个包"：其余物根下的**子目录**就是各个包的那一份
         * （共享根布局是 <共享根>\其余物\<包基名>\）。数不出名字时保持 0 —— 调用方会改用
         * "整个目录"的说法，绝不编一个数字出来（预览里的数字宁可没有，也不能是猜的）。
         */
        (int affectedPackageCount, IReadOnlyList<string> affectedPackageNames) =
            scope.DeletesEverythingInDirectory
                ? CountAffectedPackages(plan.Targets)
                : (0, (IReadOnlyList<string>)Array.Empty<string>());

        if (affectedPackageCount > 0)
        {
            message += $"；⚠ 本次是「删除本目录全部其余物」，会影响该目录下 {affectedPackageCount} 个包";
        }

        return new CleanupPreview
        {
            Scope = CleanupScope.Artifacts,
            ScopePath = scope.ArtifactDirectory,
            AllowedRoot = plan.AllowedRoot,
            ResolvedScope = scope,
            Plan = plan,

            // 量不准也能删（删除时服务自己会再判一次并拒绝），但预览要如实说"数字可能不全"。
            HasTarget = true,
            ItemCount = items.Count,
            EntryCount = entryCount,
            TotalBytes = totalBytes,
            Determined = determined,
            Items = items.Take(CleanupPreview.MaxListedItems).ToList(),
            SourcePackageNames = sourcePackages.Take(CleanupPreview.MaxListedItems).ToList(),
            SourcePackageCount = sourcePackages.Count,
            AffectedPackageCount = affectedPackageCount,
            AffectedPackageNames = affectedPackageNames,
            Message = message
        };
    }

    /// <summary>
    /// 数一数这次整目录删除会波及几个包：其余物根下的每一个子目录算一个包。
    ///
    /// <para>
    /// 只在「删除本目录全部其余物」这一档调用。读不了目录时返回 0（= 数不出来），
    /// **不抛异常、不中断预览** —— 预览已经拿到了"有多少项、多少字节"这些硬数字，
    /// 包名清单只是补充说明，不该因为它读不到就让整次删除失败。
    /// </para>
    /// </summary>
    private static (int Count, IReadOnlyList<string> Names) CountAffectedPackages(
        IReadOnlyList<string> artifactDirectories)
    {
        var names = new List<string>();

        foreach (string directory in artifactDirectories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (string sub in Directory.GetDirectories(directory))
                {
                    string name = Path.GetFileName(sub);

                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
            }
            catch
            {
                return (0, Array.Empty<string>());
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);

        return (names.Count, names.Take(CleanupPreview.MaxListedItems).ToList());
    }
}
