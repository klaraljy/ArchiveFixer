using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 改名服务（公开 API 门面）。
///
/// 职责：
/// 1. 根据 RenameOptions 生成改名预览。
/// 2. 执行用户确认后的改名。
/// 3. 处理目标文件冲突。
/// 4. 更新 ArchiveTask 的 CurrentPath / FileName / CurrentExtension / 状态。
///
/// <para><b>本类只保留公开 API 的薄转发</b>（职责拆分；判据与两阶段安全顺序都在协作类里）：</para>
/// <list type="bullet">
/// <item><description>预览构造 = <see cref="RenamePreviewBuilder"/>；</description></item>
/// <item><description>命名规则 = <see cref="RenameTargetPlanner"/> / <see cref="RenameNameRules"/> /
/// <see cref="RenameDetectedFormatFixer"/>；</description></item>
/// <item><description>冲突解析 = <see cref="RenameConflictResolver"/>（判定表仍只有
/// <see cref="PathService.ResolveConflict"/> 一处）；</description></item>
/// <item><description>改名执行 = <see cref="RenamePlan"/> + <see cref="RenameExecution"/>；</description></item>
/// <item><description>回滚 = <see cref="RenameRollback"/>；</description></item>
/// <item><description>任务回填 / 变换链记录 = <see cref="RenameTaskJournal"/>。</description></item>
/// </list>
///
/// 注意：
/// 这里不直接操作 WPF 控件。
/// 这里不弹窗。
/// 所有后缀修改都必须先 BuildPreview，再由用户确认 ExecuteRenameAsync。
/// </summary>
public class RenameService
{
    /// <summary>生成改名预览（实现见 <see cref="RenamePreviewBuilder.Build"/>）。</summary>
    public List<RenamePreviewItem> BuildPreview(
        IEnumerable<ArchiveTask> tasks,
        RenameOptions options)
    {
        return RenamePreviewBuilder.Build(tasks, options);
    }

    /// <summary>只算这一条任务的新路径（实现见 <see cref="RenameTargetPlanner.BuildNewPath"/>）。</summary>
    public string BuildNewPath(ArchiveTask task, RenameOptions options)
    {
        return RenameTargetPlanner.BuildNewPath(task, options);
    }

    /// <summary>
    /// 把用户在预览里为某一条选定的冲突处理方式落到 <see cref="RenamePreviewItem.NewPath"/> 上。
    ///
    /// <para>
    /// 「询问」档（<see cref="AppSettings.ConflictAction"/> = <c>Ask</c>）在改名路径上的落地就是它：
    /// 预览把撞名的条目标成 <see cref="RenamePreviewItem.NeedsConflictChoice"/>，
    /// 用户在预览表的「冲突处理」列里逐条选；预览窗口关掉之后由
    /// <c>RenameCoordinator</c> 对每一条调用本方法，把结论写死到 NewPath 上 ——
    /// 于是预览里显示的那个路径**就是**执行时会落的那个路径（两边不会各判一次）。
    /// </para>
    /// <para>
    /// ⛔ 不变量 3：**没选 / 选不出来一律落到"自动重命名"，绝不覆盖**。
    /// 用户直接关掉预览窗口时走的也是这条 —— 内容不丢、已有文件一个字节不动。
    /// </para>
    /// </summary>
    /// <returns>真的改写了落点返回 true（用于日志里说清"这一条按你选的办了"）。</returns>
    public bool ApplyConflictChoice(RenamePreviewItem? item)
    {
        if (item == null || !item.NeedsConflictChoice)
        {
            return false;
        }

        string choice = string.IsNullOrWhiteSpace(item.ConflictChoice)
            ? "AutoRename"
            : item.ConflictChoice.Trim();

        // 判定表只有一处实现（PathService.ResolveConflict），这里不另写一套 switch。
        var pathService = new PathService();
        ConflictResolution resolution = pathService.ResolveConflict(
            item.NewPath,
            ConflictTargetKind.File,
            ConflictActions.Ask,
            ConflictDecision.Once(MapConflictChoice(choice)));

        return RenameConflictResolver.ApplyResolvedChoice(item, resolution);
    }

    /// <summary>执行一批改名（实现见 <see cref="RenameExecution.RunBatchAsync"/>）。</summary>
    public async Task ExecuteRenameAsync(
        IEnumerable<RenamePreviewItem> previewItems,
        IEnumerable<ArchiveTask> tasks)
    {
        await Task.Yield();

        if (previewItems == null)
        {
            return;
        }

        await RenameExecution.RunBatchAsync(previewItems, tasks);
    }

    /// <summary>目标被占时取下一个可用名字（实现见 <see cref="RenameTargetPlanner.AutoRenamePath"/>）。</summary>
    public string AutoRenamePath(string targetPath)
    {
        return RenameTargetPlanner.AutoRenamePath(targetPath);
    }

    /// <summary>
    /// 按路径在任务表里找任务（改名执行与"只重扫被改过名的那些"共用这一份匹配口径）。
    ///
    /// <para>三个字段都要比：执行期会把 <c>CurrentPath</c> 换到新名字上，而
    /// <c>OriginalPath</c> / <c>RenamePreviewPath</c> 还留着旧路径 —— 少比一个就会漏掉任务，
    /// 于是"改名成功但列表里那一行还指着旧文件"。</para>
    /// </summary>
    public static ArchiveTask? FindTaskByPath(IEnumerable<ArchiveTask> tasks, string path)
    {
        return RenameTaskJournal.FindTaskByPath(tasks, path);
    }

    /// <summary>用户选定的冲突档位 → <see cref="ConflictChoice"/>（只做词表映射，判定不在这里）。</summary>
    private static ConflictChoice MapConflictChoice(string choice)
    {
        return choice switch
        {
            "Overwrite" => ConflictChoice.Overwrite,
            "Skip" => ConflictChoice.Skip,
            _ => ConflictChoice.AutoRename
        };
    }
}
