using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 一次改名批次的落点计划：谁要动、各自动到哪个路径、谁不用动。
/// 只回答"落到哪"，除读一次"目标是否存在"外不碰文件系统。
///
/// <para>从 <see cref="RenameService"/> 的嵌套类原样提升为顶层协作类（一类型一文件）：
/// 计划里的"源路径 → 最终落点"是执行与结果回填**共用的唯一一份**，⛔ 不允许两处各判一次。</para>
///
/// <para>顺序有意为之：先把"谁要占哪个名字"整批登记完，再逐个看冲突 ——
/// <see cref="File.Exists(string)"/> 问的是磁盘现状，而本批次会先把名字腾出来，
/// 只有计划表才知道"这个名字其实马上就是空的"（名称交换就靠它）。</para>
/// </summary>
internal sealed class RenamePlan
{
    private readonly List<RenamePreviewItem> _items;
    private readonly Dictionary<string, string> _finalPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _skipReasons = new(StringComparer.OrdinalIgnoreCase);

    private RenamePlan(List<RenamePreviewItem> items)
    {
        _items = items;
    }

    /// <summary>源路径 → 最终落点。执行阶段与结果回填都查它。</summary>
    internal IReadOnlyDictionary<string, string> FinalPaths => _finalPaths;

    internal static RenamePlan Build(List<RenamePreviewItem> items)
    {
        var plan = new RenamePlan(items);

        plan.PlanCore();

        return plan;
    }

    internal bool ShouldExecute(RenamePreviewItem? item)
    {
        return item != null
            && item.IsSelected
            && !string.IsNullOrWhiteSpace(item.OriginalPath)
            && !string.IsNullOrWhiteSpace(item.NewPath)
            && !string.Equals(item.Status, StatusText.RenameCannot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.Status, StatusText.RenameWillSkip, StringComparison.OrdinalIgnoreCase);
    }

    internal string GetFinalPath(string sourcePath) =>
        _finalPaths.TryGetValue(sourcePath, out string? value) ? value : string.Empty;

    internal bool IsSkipped(string sourcePath) => _skipReasons.ContainsKey(sourcePath);

    internal string GetSkipReason(string sourcePath) =>
        _skipReasons.TryGetValue(sourcePath, out string? value) ? value : string.Empty;

    /// <summary>
    /// 定落点。顺序有意为之：先把"谁要占哪个名字"整批登记完，再逐个看冲突 ——
    /// 因为 <see cref="File.Exists(string)"/> 问的是磁盘现状，而本批次会先把名字腾出来，
    /// 只有计划表才知道"这个名字其实马上就是空的"（名称交换就靠它）。
    /// </summary>
    private void PlanCore()
    {
        foreach (RenamePreviewItem item in _items)
        {
            if (!ShouldExecute(item))
            {
                continue;
            }

            _finalPaths[item.OriginalPath] = item.NewPath;
        }

        foreach (KeyValuePair<string, string> pair in _finalPaths.ToList())
        {
            string source = pair.Key;
            string destination = pair.Value;

            // 这个名字上是本批次另一个"要腾出去"的源 → 不是冲突，交给两阶段。
            if (_finalPaths.TryGetValue(destination, out string? occupant) &&
                !string.Equals(occupant, source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(destination))
            {
                continue;
            }

            /*
             * 预览期已经判定"目标是别人的文件、且要覆盖"的项：最终落点就是预览给的那个路径，
             * 执行时不再问一次文件系统。
             *
             * 为什么：本批次前面的项可能已经把这个名字腾出来了（名称交换），
             * 这时 File.Exists 会说"不存在"，这一项就会退化成普通移动 ——
             * 而它本该顶掉的那个文件正躺在临时名下，落点判断与计划对不上。
             */
            RenamePreviewItem? owner = Find(source);

            if (string.Equals(owner?.Status, StatusText.TargetExists, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string conflictAction = string.IsNullOrWhiteSpace(owner?.ConflictAction)
                ? "AutoRename"
                : owner!.ConflictAction;

            if (string.Equals(conflictAction, "Skip", StringComparison.OrdinalIgnoreCase))
            {
                _finalPaths.Remove(source);
                _skipReasons[source] = "目标文件已存在";
                continue;
            }

            if (string.Equals(conflictAction, "Overwrite", StringComparison.OrdinalIgnoreCase))
            {
                // 明确要覆盖：旧文件会在"腾名字"阶段被挪走，落位成功后才删。
                continue;
            }

            // AutoRename / Ask：执行前按当前文件系统重算一次落地名。
            _finalPaths[source] = SafePathHelper.AutoRenameFilePath(destination);
        }
    }

    private RenamePreviewItem? Find(string source)
    {
        return _items.FirstOrDefault(x =>
            x != null && string.Equals(x.OriginalPath, source, StringComparison.OrdinalIgnoreCase));
    }
}
