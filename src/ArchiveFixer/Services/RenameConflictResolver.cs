using ArchiveFixer.BindingModels;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Services;

/// <summary>
/// 改名的同名冲突：预览期按档位落结论、本批次内重名错开、用户选定的结论写回落点。
///
/// <para>从 <see cref="RenameService"/> 抽出的协作类（「冲突解析」）：判定表**只有一处实现**
/// ——<c>PathService.ResolveConflict</c>（本类不另写 switch 判档位）；⛔ 不变量 3：
/// 没选 / 选不出来一律自动重命名，绝不覆盖。判据与拆分前逐字相同。</para>
/// </summary>
internal static class RenameConflictResolver
{
    /// <summary>
    /// 预览期撞名（落点上已有文件）时按冲突档位写结论。
    ///
    /// <para>预览期的冲突结论必须和真正执行时的结论一致，否则用户看到的 NewPath
    /// 与落盘结果会对不上。所以两边共用同一套判定：</para>
    /// <list type="bullet">
    /// <item><description>Skip      → 跳过；</description></item>
    /// <item><description>Overwrite → 目标保持 NewPath，状态 TargetExists（执行阶段走"先移开再落位"）；</description></item>
    /// <item><description>Ask       → **在预览里逐条让用户选**（本方法只标"待选择"，落点由
    ///               RenameCoordinator 在窗口关掉之后按用户的选择写回）；</description></item>
    /// <item><description>其余       → 换成自动改名后的路径。</description></item>
    /// </list>
    ///
    /// <para>Ask 以前被并进"其余"分支，于是预览表里出现「询问」+「将自动重命名」并列
    /// —— 界面在自相矛盾（真实缺陷，见 fix-ask.md §6 第 1 条）。现在它真的会问，
    /// 而且问在预览里（改名本来就必须先预览，不变量 3），不额外弹第二个对话框。</para>
    /// </summary>
    internal static void ApplyPreviewConflict(RenamePreviewItem item, RenameOptions options, string newPath)
    {
        if (!File.Exists(newPath))
        {
            return;
        }

        if (string.Equals(options.ConflictAction, "Skip", StringComparison.OrdinalIgnoreCase))
        {
            item.MarkSkip("目标文件已存在，已跳过");
        }
        else if (string.Equals(options.ConflictAction, "Overwrite", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = StatusText.TargetExists;
            item.ErrorMessage = "确认后将覆盖目标文件（先移开旧文件再落位，中途失败不丢文件）";
        }
        else if (ConflictActions.IsAsk(options.ConflictAction))
        {
            item.MarkNeedsConflictChoice();
        }
        else
        {
            // 默认档（AutoRename）：**绝不覆盖**，只改目标侧的名字。
            item.NewPath = RenameTargetPlanner.AutoRenamePath(newPath);
            item.MarkAutoRename("目标文件已存在，将自动重命名");
        }
    }

    /// <summary>
    /// 把 <c>PathService.ResolveConflict</c> 的结论写回预览项：覆盖 / 跳过 / 自动重命名三档的
    /// 状态与落点（判定本身不在这里，主类只把用户选的档位映射成 <see cref="ConflictChoice"/>）。
    /// </summary>
    internal static bool ApplyResolvedChoice(RenamePreviewItem item, ConflictResolution resolution)
    {
        if (resolution.Choice == ConflictChoice.Overwrite)
        {
            // 覆盖：落点不变，执行阶段走"先移到临时名 → 落位 → 再删"两阶段（不变量 3）。
            item.Status = StatusText.TargetExists;
            item.ErrorMessage = "按你的选择：覆盖目标文件（先移开旧文件再落位，中途失败不丢文件）";
            item.NeedsConflictChoice = false;
            return true;
        }

        if (resolution.Choice == ConflictChoice.Skip)
        {
            item.MarkSkip("按你的选择：跳过（目标文件已存在，一个字节都不动）");
            item.NeedsConflictChoice = false;
            return true;
        }

        // 兜底（含"没选"）：自动重命名 —— 绝不覆盖。
        item.NewPath = string.IsNullOrWhiteSpace(resolution.TargetPath)
            ? RenameTargetPlanner.AutoRenamePath(item.NewPath)
            : resolution.TargetPath;

        item.MarkAutoRename(
            string.IsNullOrWhiteSpace(item.ConflictChoice)
                ? "你没选，按保守档自动重命名（绝不覆盖）"
                : "按你的选择：自动重命名");

        item.NeedsConflictChoice = false;
        return true;
    }

    /// <summary>
    /// **本批次内**两行改成同一个落点时，把后来者错开（2026-09-26 审计修的真缺陷）。
    ///
    /// <para><b>为什么必须在预览期做</b>：预览构造循环只看得见**磁盘上**已有的文件，
    /// 看不见"这一批里另一行马上就要占这个名字"。<c>A.jpg</c> 与 <c>A.png</c> 同目录、
    /// 一起用「替换后缀」改成 <c>.rar</c> 时两行的落点都是 <c>A.rar</c> ——
    /// 预览里两行都是「可改名」、统计还写着"确认改名 2 个文件"，
    /// 可执行阶段第 3 步 <c>File.Move</c> 撞名 → **整批回滚** + 用户拿到一句
    /// "落位 A.rar 时出错：文件已存在"（数据不丢，但结论与预览不符、报错也看不懂）。</para>
    ///
    /// <para><b>为什么只处理"批内重名"</b>：磁盘上已存在的冲突在生成预览时就已经按
    /// 用户的冲突档位处理过了（跳过 / 覆盖 / 询问 / 自动重命名）。这里**不重新判磁盘**，
    /// 否则会把"名称交换"（A↔B，两个落点都真实存在）误判成冲突 —— 那是执行期刻意支持的一档。
    /// 错开用的名字仍然绕开磁盘上已有的文件（<see cref="ResolveBatchUniqueTargetPath"/>）。</para>
    /// </summary>
    internal static void ResolveBatchDuplicateTargets(List<RenamePreviewItem> items)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RenamePreviewItem item in items)
        {
            if (!WillExecute(item))
            {
                continue;
            }

            if (claimed.Add(item.NewPath))
            {
                continue;
            }

            string resolved = ResolveBatchUniqueTargetPath(item.NewPath, claimed);

            item.NewPath = resolved;
            item.MarkAutoRename(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RenameBatchDuplicateAutoRenameFormat,
                Path.GetFileName(resolved)));
        }
    }

    /// <summary>这一条会不会真的执行（与执行期 <c>RenamePlan.ShouldExecute</c> 同一口径）。</summary>
    private static bool WillExecute(RenamePreviewItem? item)
    {
        return item != null
            && item.IsSelected
            && !string.IsNullOrWhiteSpace(item.OriginalPath)
            && !string.IsNullOrWhiteSpace(item.NewPath)
            && !string.Equals(item.Status, StatusText.RenameCannot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.Status, StatusText.RenameWillSkip, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取一个"本批次还没被占、磁盘上也没有"的名字：<c>名字(1).ext</c>、<c>名字(2).ext</c>…
    /// 编号风格与 <see cref="SafePathHelper.AutoRenameFilePath"/> 一致（用户看到的还是同一套）。
    /// </summary>
    private static string ResolveBatchUniqueTargetPath(string desiredPath, ISet<string> claimed)
    {
        if (string.IsNullOrWhiteSpace(desiredPath))
        {
            return desiredPath;
        }

        string? directory = Path.GetDirectoryName(desiredPath);
        string fileName = Path.GetFileNameWithoutExtension(desiredPath);
        string extension = Path.GetExtension(desiredPath);

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return SafePathHelper.AutoRenameFilePath(desiredPath);
        }

        for (int i = 1; i < 10000; i++)
        {
            string candidate = Path.Combine(directory, $"{fileName}({i}){extension}");

            if (claimed.Contains(candidate))
            {
                continue;
            }

            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                continue;
            }

            return candidate;
        }

        return SafePathHelper.AutoRenameFilePath(desiredPath);
    }
}
