using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 清理决策/汇总的纯逻辑（无 UI、无命令）。
/// </summary>
internal static class CleanupDecisionPlanner
{
    /// <summary>整批清理的计数器（成功/失败任务数、条目数、释放字节数、失败原因）。</summary>
    internal sealed class BatchCleanupTally
    {
        private readonly List<string> _failureReasons = new();

        public int SucceededTaskCount { get; private set; }

        public int FailedTaskCount { get; private set; }

        public int SucceededItemCount { get; private set; }

        public int FailedItemCount { get; private set; }

        public long FreedBytes { get; private set; }

        public long RecycledBytes { get; private set; }

        /// <summary>整批有没有任何一项没删掉（含"任务直接抛异常"）。</summary>
        public bool HasAnyFailure => FailedTaskCount > 0 || FailedItemCount > 0;

        public IReadOnlyList<string> FailureReasons => _failureReasons;

        public void Record(ArchiveTask task, CleanupOutcome outcome)
        {
            if (outcome.FailureCount > 0 || outcome.SucceededNothing)
            {
                FailedTaskCount++;
                FailedItemCount += outcome.FailureCount;
                _failureReasons.Add($"{task.FileName}：{outcome.Message}");
            }
            else
            {
                SucceededTaskCount++;
            }

            SucceededItemCount += outcome.SuccessCount;
            FreedBytes += outcome.FreedBytes;
            RecycledBytes += outcome.RecycledBytes;

            foreach (string reason in outcome.FailureReasons)
            {
                if (_failureReasons.Count < CleanupPreview.MaxListedItems * 2)
                {
                    _failureReasons.Add($"{task.FileName}：{reason}");
                }
            }
        }

        public void RecordException(ArchiveTask task, string message)
        {
            FailedTaskCount++;
            _failureReasons.Add($"{task.FileName}：{message}");
        }
    }

    /// <summary>
    /// 整批清理的收尾报告：**成功 N / 失败 M（含各自原因）/ 释放字节数**。
    ///
    /// 只要有任何一项没删掉就不说"全部成功"（<see cref="BatchCleanupTally.HasAnyFailure"/> 的调用方据此选提示框）。
    /// </summary>
    internal static string BuildBatchCleanupSummary(
        string title,
        DeleteMode mode,
        int targetCount,
        BatchCleanupTally tally)
    {
        string modeText = mode == DeleteMode.Permanent ? "彻底删除" : "移入回收站";

        var builder = new StringBuilder();

        builder.Append(
            $"{title}：{targetCount} 个任务，{modeText}成功 {tally.SucceededTaskCount} 个 / 失败 {tally.FailedTaskCount} 个；"
            + $"条目成功 {tally.SucceededItemCount} 项、失败 {tally.FailedItemCount} 项。");

        builder.Append(
            mode == DeleteMode.Permanent
                ? $"释放 {tally.FreedBytes} 字节。"
                : $"移入回收站 {tally.RecycledBytes} 字节（回收站里的内容未真正释放空间）。");

        if (tally.HasAnyFailure && tally.FailureReasons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("没删掉的原因：");

            foreach (string reason in tally.FailureReasons.Take(CleanupPreview.MaxListedItems))
            {
                builder.AppendLine("· " + reason);
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 一次清理确认框的"档位设计"：主按钮文案、勾选框文案、勾选框的默认状态，以及
    /// "用户最终勾没勾 → 到底用哪一档"的唯一推导。
    ///
    /// <para>
    /// 为什么需要它（真实的自相矛盾风险）：设置项「其余物清理默认档」可以是<em>彻底删除</em>，
    /// 而确认框里那个勾选框原来的文案固定是"改为彻底删除，不进回收站"、默认不勾 ——
    /// 于是默认档是彻底删除时，界面会同时说"默认彻底删除"和"勾上才彻底删除"，
    /// 用户不勾反而得到了更危险的那一档。把"档位 ↔ 文案 ↔ 勾选语义"三件事绑在一个对象上，
    /// 就不可能再各写各的。
    /// </para>
    /// </summary>
    internal sealed class CleanupDecision
    {
        public required DeleteMode DefaultMode { get; init; }

        /// <summary>确认框正文（已带上默认档的说明）。</summary>
        public required string ConfirmText { get; init; }

        /// <summary>主按钮文案：就是默认档本身。</summary>
        public required string ConfirmButtonText { get; init; }

        /// <summary>勾选框文案：**永远是"切到另一档"**，所以默认档不同时文案也不同。</summary>
        public required string OptionText { get; init; }

        /// <summary>勾选框初始状态：永远不勾（默认档就是"不勾"的那一档）。</summary>
        public bool OptionCheckedByDefault { get; init; }

        /// <summary>勾选框勾上时的档位（= 另一档）。</summary>
        public required DeleteMode OptionMode { get; init; }

        /// <summary>用户最终勾没勾 → 实际执行哪一档。</summary>
        public DeleteMode Resolve(bool optionChecked) => optionChecked ? OptionMode : DefaultMode;
    }

    /// <summary>
    /// 由设置项「其余物清理默认档」推出确认框的档位设计（唯一判定处）。
    ///
    /// <para>
    /// 判定只有一处实现：<see cref="RestRemovalModes.IsPermanent"/> —— 别在别处再写
    /// <c>== "Permanent"</c>，那种字符串比较散落两处迟早分叉（一个认大小写、一个不认）。
    /// </para>
    /// <para>
    /// ⛔ 两条红线在这里**不受默认档影响**：
    /// ① 二次确认保留 —— 只要最终要彻底删除，调用方仍会再弹一次"我知道不可恢复"，见 RunCleanupAsync；
    /// ② 回收站不可用绝不降级为永久删除 —— 那条在 RecycleBinService 里，跟这里的档位无关。
    /// </para>
    /// </summary>
    internal static CleanupDecision DeriveCleanupDecision(
        CleanupPreview preview,
        string title,
        string? scopeNote)
    {
        return DeriveCleanupDecision(preview, title, scopeNote, plans: null);
    }

    /// <summary>
    /// 同上，但确认框正文按**整批**渲染（多任务合并成一次确认时用）。
    /// 档位判定一模一样 —— 变的只有正文里多出来的"各任务作用范围"那一段。
    /// </summary>
    internal static CleanupDecision DeriveCleanupDecision(
        CleanupPreview preview,
        string title,
        string? scopeNote,
        IReadOnlyList<CleanupTaskPlan>? plans)
    {
        /*
         * ⛔ 2026-10-15：用户口径「我们这里现在只有留下和彻底删除操作，已经不存在放入回收站了」
         * ⇒ 手工清理（点按钮才发生的那一种）**只剩一个动作 = 彻底删除**，
         * 于是这里不再有"默认档 / 另一档"之分，也不再读那个已经删掉的设置项。
         *
         * 没有"另一档"可切 ⇒ `OptionText` 留空 ⇒ 确认框里那个勾选框整格不出现
         * （`AppDialogWindow` 只在文案非空时才显示它，见那里 `!string.IsNullOrWhiteSpace`）。
         */
        const DeleteMode defaultMode = DeleteMode.Permanent;

        return new CleanupDecision
        {
            DefaultMode = defaultMode,
            OptionMode = defaultMode,
            ConfirmText = plans == null
                ? BuildCleanupConfirmText(title, preview, defaultMode, scopeNote)
                : BuildCleanupConfirmText(title, plans, preview, defaultMode, scopeNote),
            ConfirmButtonText = "彻底删除",
            OptionText = string.Empty,
            OptionCheckedByDefault = false
        };
    }

    /// <summary>
    /// 确认框正文：写清"删什么、多少、能不能撤销"，并把作用域路径原文列出来
    /// （作用域越大越要让用户看见，例如共享输出目录下 其余物 会被多个包共用；
    /// 含源包时还要说清"删掉后需要重新下载"）。
    /// </summary>
    internal static string BuildCleanupConfirmText(
        string title,
        CleanupPreview preview,
        DeleteMode mode,
        string? scopeNote = null)
    {
        var builder = new StringBuilder();

        builder.AppendLine(title);
        builder.AppendLine();
        builder.Append(BuildCleanupBody(preview, mode, scopeNote));

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// **多任务合并确认**的正文（用户 2026-09-22 拍板："要合并成 1 次确认，不要有冗余操作"）。
    ///
    /// <para>
    /// 与单任务版本共用同一段正文（<see cref="BuildCleanupBody"/>：合计条目数/字节数、源包清单、
    /// 作用域提醒、档位说明），只在前面多一段"各任务的作用范围"清单 ——
    /// 于是"确认框里到底说了什么"仍然只有一处实现，不会两套措辞各说各的。
    /// </para>
    /// <para>
    /// 批里只有一个目标时**逐字节等同**旧格式（不该凭空多出一段清单）。
    /// </para>
    /// </summary>
    internal static string BuildCleanupConfirmText(
        string title,
        IReadOnlyList<CleanupTaskPlan> plans,
        CleanupPreview merged,
        DeleteMode mode,
        string? scopeNote = null)
    {
        if (plans.Count <= 1)
        {
            return BuildCleanupConfirmText(
                title,
                plans.Count == 1 ? plans[0].Preview ?? merged : merged,
                mode,
                scopeNote);
        }

        var builder = new StringBuilder();

        builder.AppendLine(title);
        builder.AppendLine();
        builder.AppendLine($"本次只确认这一次：将清理以下 {plans.Count} 个任务（确认后逐个执行，任何一个失败都不会中断整批）。");

        foreach (CleanupTaskPlan plan in plans)
        {
            builder.AppendLine(plan.DescribeTarget());
        }

        builder.AppendLine();

        // 含源包的任务要点名（删了要重新下载）—— 逐个任务一条，比一个总数更能让人停一下。
        List<CleanupTaskPlan> withSources = plans
            .Where(plan => plan.Preview is { SourcePackageCount: > 0 })
            .ToList();

        if (withSources.Count > 0)
        {
            builder.AppendLine($"⚠ 其中 {withSources.Count} 个任务含源包文件（压缩包本身），删掉后需要重新下载：");

            foreach (CleanupTaskPlan plan in withSources)
            {
                builder.AppendLine(
                    $"· {plan.Task?.FileName}：{plan.Preview!.SourcePackageCount} 个"
                    + (plan.Preview.SourcePackageNames.Count > 0
                        ? "（" + string.Join("、", plan.Preview.SourcePackageNames) + "）"
                        : string.Empty));
            }

            builder.AppendLine();
        }

        builder.Append(BuildCleanupBody(merged, mode, scopeNote));

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 确认框正文的公共部分（单任务 / 多任务两版共用，避免"同一个确认框两套措辞"）。
    /// </summary>
    private static string BuildCleanupBody(CleanupPreview preview, DeleteMode mode, string? scopeNote)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"作用范围：{preview.ScopePath}");
        builder.AppendLine($"顶层 {preview.ItemCount} 项，共 {preview.EntryCount} 个条目 / {preview.TotalBytes} 字节"
                           + (preview.Determined ? string.Empty : "（部分内容读不到，数字可能不全）"));

        /*
         * 源包提示必须**显著**：其余物里现在也有源包本身（用户 2026-09-22 的新布局），
         * 删掉它意味着要重新下载 —— 这是用户最容易忽略的一个后果。
         */
        if (preview.SourcePackageCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"⚠ 含 {preview.SourcePackageCount} 个源包文件（压缩包本身），删掉后需要重新下载：");

            foreach (string name in preview.SourcePackageNames)
            {
                builder.AppendLine("· " + name);
            }

            if (preview.SourcePackageCount > preview.SourcePackageNames.Count)
            {
                builder.AppendLine($"…等共 {preview.SourcePackageCount} 个源包");
            }
        }

        if (!string.IsNullOrWhiteSpace(scopeNote))
        {
            builder.AppendLine();
            builder.AppendLine("⚠ " + scopeNote);
        }

        if (preview.Items.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("将删除（最多列 " + CleanupPreview.MaxListedItems + " 项）：");

            foreach (string item in preview.Items)
            {
                builder.AppendLine("· " + item);
            }

            if (preview.ItemCount > preview.Items.Count)
            {
                builder.AppendLine($"…等共 {preview.ItemCount} 项");
            }
        }

        builder.AppendLine();

        builder.AppendLine(mode == DeleteMode.Permanent
            ? "⚠ 彻底删除：内容不会进回收站，无法恢复。"
            : "默认档：移入回收站，之后可以从回收站还原。回收站不可用时程序不会改删，会直接报错并放弃。");

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 作用域提醒：这次删除覆盖的范围是"只有本任务那一份"还是"整个共享目录"。
    ///
    /// <para>
    /// 其余物（<see cref="CleanupScope.Artifacts"/>）：共享输出目录（模式 B / 指定位置直接放）下
    /// **只删本任务包基名那一份**，所以这里说清"不会碰到别的包的其余物"；
    /// 走「删除本目录全部其余物…」这个显式入口时反过来强调"会影响同目录的所有包"。
    /// </para>
    /// <para>
    /// 空文件夹（<see cref="CleanupScope.EmptyFolders"/>）：按规格 §5 作用域就是"扫这个根"，
    /// 共享模式下会覆盖到同目录里其它包留下的空壳 —— 这必须让用户看见
    /// （它删的只是"任意层级都没有文件"的目录，不含任何文件）。
    /// </para>
    /// </summary>
    internal static string BuildSharedScopeNote(
        CleanupScope scope,
        ArchiveTask? task,
        CleanupPreview? preview = null)
    {
        if (task == null || !IsOutputDirectorySharedWithSource(task.OutputPath, task.CurrentPath))
        {
            return string.Empty;
        }

        if (scope == CleanupScope.EmptyFolders)
        {
            return "当前任务的输出目录就是源包所在目录（包名与目录同名时合并了重复的一层），"
                   + "本次会清掉这个目录下所有“任意层级都没有文件”的子目录（不只是本任务产出的；不含任何文件）。";
        }

        if (preview?.ResolvedScope?.DeletesEverythingInDirectory == true)
        {
            /*
             * 「删除本目录全部其余物」是三个入口里作用域最大的一个（整目录、含别的包的源包），
             * 所以**必须点名会影响几个包**：只说"会影响同目录的所有包"是一句形容词，
             * 用户没法判断这次动的是 2 个还是 20 个（预览里的包名清单来自
             * CleanupPreview.AffectedPackageNames，数不出来时保持 0，绝不编数字）。
             */
            string affected = preview.AffectedPackageCount > 0
                ? "受影响：" + preview.AffectedPackageCount + " 个包的其余物会一起被删"
                  + (preview.AffectedPackageNames.Count > 0
                      ? "（" + string.Join("、", preview.AffectedPackageNames)
                        + (preview.AffectedPackageCount > preview.AffectedPackageNames.Count ? " …" : string.Empty)
                        + "）。"
                      : "。")
                : "受影响：这个目录下的其余物（分不出包名，整目录一起算）。";

            return "这是「删除本目录全部其余物」：输出目录与同目录的其它包共用，"
                   + "本次会一并删掉它们的其余物（含各自的源包文件），删掉后都需要重新下载。"
                   + affected;
        }

        return "当前任务的输出目录与源包所在目录是同一个（手动档「解压到当前文件夹」之后就是这样），"
               + "本次只删本任务那一份其余物，不会碰别的包。";
    }

    /// <summary>任务的输出目录是不是就是源包所在目录（**手动档「解压到当前文件夹」之后的形态**）。</summary>
    internal static bool IsOutputDirectorySharedWithSource(string? outputDirectory, string? sourceArchivePath)
    {
        // 判据只有一处实现（OutputPlacement.LandsInSourceDirectory），这里只是把参数顺序转过来。
        return OutputPlacement.LandsInSourceDirectory(sourceArchivePath, outputDirectory);
    }
}

