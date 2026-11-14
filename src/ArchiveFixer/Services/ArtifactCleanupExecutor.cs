using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 其余物清理的**执行**：把预览阶段算好的删除计划交给 <see cref="RecycleBinService"/>，
/// 再把结果翻成界面结论。
///
/// <para>
/// 这一层只做三件事，一件也不多做：
/// ① **复用**预览给的计划（拿不出"与预览一致"的证据就不动手）；
/// ② 动手前再过一遍容器内校验（目标必须在允许根之内）；
/// ③ 交给删除服务，由它做安全前置、度量、删除与写日志 —— 本类不绕过它，也不自己降级档位。
/// </para>
/// <para>
/// ⚠ 「预览与执行必须是同一个作用域」是本类存在的理由：作用域由预览算好、
/// 放进 <see cref="CleanupPreview"/> 带过来；本类若发现这一刻算出来的计划与预览里的
/// 不是同一份（允许根变了、目标集合变了），一律拒绝并让用户重新预览。
/// </para>
/// </summary>
public sealed class ArtifactCleanupExecutor
{
    /// <summary>清理理由（写进删除日志；两档共用同一句话）。</summary>
    internal const string ProcessArtifactReason = "删除其余物（内层归档 / 分卷 / 过程物 / 源包）";

    /// <summary>「删除本目录全部其余物」的理由：说清这次不是只删本任务那一份。</summary>
    internal const string AllArtifactsReason = "删除本目录全部其余物（影响该目录下所有包）";

    private readonly CleanupDeleteServices _deleteServices;

    /// <param name="deleteServices">注入项装配（与预览用的是同一份，见 <see cref="CleanupDeleteServices"/>）。</param>
    public ArtifactCleanupExecutor(CleanupDeleteServices deleteServices)
    {
        _deleteServices = deleteServices;
    }

    /// <summary>
    /// 执行其余物删除。<paramref name="mode"/> 默认档 = 回收站；
    /// <see cref="DeleteMode.Permanent"/> 只能由界面在"红色 + 二次确认"之后传进来。
    ///
    /// <para>
    /// ⚠ <paramref name="preview"/> 是**必填的语义**：作用域必须在预览阶段算好、在这里原样复用。
    /// 传 null 时本方法会自己算一遍（仍然只有一处实现），但调用方不该走那条路 ——
    /// "预览给用户看的是 A、执行删的是 B"正是上一个版本的缺陷形态。
    /// </para>
    /// </summary>
    public CleanupOutcome Execute(
        ArchiveTask? task,
        DeleteMode mode,
        CleanupPreview? preview = null,
        ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
    {
        ArtifactCleanupScope scope = preview?.ResolvedScope
                                     ?? ArtifactCleanupScopeResolver.Resolve(task, deleteScope);

        if (!scope.IsResolved || scope.RefusedUnsafe)
        {
            return CleanupOutcomeMapper.NotAttempted(
                CleanupScope.Artifacts,
                mode,
                "已拒绝删除其余物：" + scope.BlockReason);
        }

        DeletionPlan plan = preview?.Plan ?? scope.ToDeletionPlan("手工清理其余物");

        // 作用域必须与预览时那一刻一致（拿不出证据一致就不动手）：
        // 预览给用户看的是 A、执行删的是 B，正是上一版的缺陷形态。
        if (preview != null
            && (!SafePathHelper.PathEquals(preview.Plan?.AllowedRoot ?? string.Empty, plan.AllowedRoot)
                || !ArtifactCleanupScopeResolver.SameDirectorySet(preview.Plan?.Targets, plan.Targets)))
        {
            return CleanupOutcomeMapper.NotAttempted(
                CleanupScope.Artifacts,
                mode,
                "作用域与预览不一致（预览之后目录有变动），已拒绝删除；请重新预览一次");
        }

        string reason = plan.DeletesEverything ? AllArtifactsReason : ProcessArtifactReason;
        var requests = new List<DeleteRequest>();

        foreach (string directory in plan.Targets)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (!ArchivePathGuard.IsInsideRoot(plan.AllowedRoot, directory, out string outsideReason))
            {
                return CleanupOutcomeMapper.NotAttempted(
                    CleanupScope.Artifacts,
                    mode,
                    $"其余物目录不在允许的范围内，已拒绝删除 —— {outsideReason}");
            }

            requests.Add(new DeleteRequest(directory, reason));
        }

        if (requests.Count == 0)
        {
            return CleanupOutcomeMapper.NotAttempted(
                CleanupScope.Artifacts,
                mode,
                $"没有可删除的其余物：{plan.Targets.FirstOrDefault() ?? string.Empty} 不存在");
        }

        DeleteResult result;

        try
        {
            result = _deleteServices.CreateDeleteService().Delete(
                requests,
                new DeleteOptions
                {
                    // 允许根：包的输出目录，或共享输出目录。其余物目录必须落在它之内，且不得等于它。
                    AllowedRoot = SafePathHelper.GetFullPathSafe(plan.AllowedRoot),
                    UserConfirmed = true,
                    Mode = mode,
                    Reason = reason
                });
        }
        catch (Exception ex)
        {
            return CleanupOutcomeMapper.NotAttempted(
                CleanupScope.Artifacts,
                mode,
                "删除其余物时出现意外错误：" + ex.Message);
        }

        return CleanupOutcomeMapper.FromDeleteResult(CleanupScope.Artifacts, mode, result);
    }
}
