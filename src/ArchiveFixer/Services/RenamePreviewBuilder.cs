using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Services;

/// <summary>
/// 改名预览的构造：把勾选的任务按 <see cref="RenameOptions"/> 算成一行行 <see cref="RenamePreviewItem"/>。
///
/// <para>从 <see cref="RenameService"/> 抽出的协作类（「预览」那一块）：只做**预览**，
/// ⛔ 不执行改名、不写任务状态；落点由 <see cref="RenameTargetPlanner"/> 给，
/// 撞名结论由 <see cref="RenameConflictResolver"/> 给，本类只按既有顺序逐条落结论。</para>
///
/// <para>⛔ 不变量 3：预览里显示的那个 NewPath **就是**执行时会落的那个路径
/// （「询问」档的最终落点由 <c>RenameCoordinator</c> 调
/// <see cref="RenameService.ApplyConflictChoice"/> 写死）；预览期冲突结论与执行期结论
/// 共用同一套判定（<see cref="RenameConflictResolver"/>）。</para>
/// </summary>
internal static class RenamePreviewBuilder
{
    internal static List<RenamePreviewItem> Build(
        IEnumerable<ArchiveTask> tasks,
        RenameOptions options)
    {
        List<RenamePreviewItem> result = new();

        if (tasks == null)
        {
            return result;
        }

        options ??= new RenameOptions();
        options.Normalize();

        foreach (ArchiveTask task in tasks)
        {
            try
            {
                if (task == null)
                {
                    continue;
                }

                if (!task.IsSelected)
                {
                    continue;
                }

                string oldPath = task.CurrentPath;

                if (string.IsNullOrWhiteSpace(oldPath))
                {
                    oldPath = task.OriginalPath;
                }

                if (string.IsNullOrWhiteSpace(oldPath))
                {
                    continue;
                }

                string newPath = RenameTargetPlanner.BuildNewPath(task, options);
                string operationName = BuildOperationLabel(options, task.DetectedFormat);

                RenamePreviewItem item = new RenamePreviewItem(
                    oldPath,
                    task.DetectedFormat,
                    operationName,
                    newPath,
                    options.ConflictAction);

                if (!File.Exists(oldPath))
                {
                    item.MarkInvalid("源文件不存在");
                    result.Add(item);
                    continue;
                }

                /*
                 * 分卷文件**不许改名** —— 只有一种例外：名字被改坏的第 1 卷能被修回标准名
                 * （那一条在 <see cref="RenameTargetPlanner.BuildNewPath"/> 的 FixByDetectedFormat
                 * 分支里算，判据是 VolumeNameRepair.Plan）。这里判"算出来的落点与原名相同"=
                 * 没有可修的东西，于是如实标「将跳过」并说清原因（2026-09-26 审计：以前这条闸门
                 * 只长在智能修正里，「替换后缀」能把 set.7z.001 改成 set.7z.7z，整组就废了）。
                 */
                if (LegacyVolumeNames.IsVolumePart(item.OriginalFileName) &&
                    SafePathHelper.PathEquals(oldPath, newPath))
                {
                    item.NewPath = oldPath;
                    item.NewFileName = item.OriginalFileName;
                    item.MarkSkip(StatusText.RenameVolumeSkippedReason);
                    result.Add(item);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(newPath))
                {
                    item.MarkInvalid("无法生成新路径");
                    result.Add(item);
                    continue;
                }

                // 必须用规范化路径比较，不能比字符串：
                // 只要路径写法有差异（正斜杠 / 冗余分隔符 / 大小写），
                // 冲突判定里那句 File.Exists(newPath) 就会把"自己"当成"已存在的目标"，
                // 于是把 data.tar.gz 这种本来不用改的文件改成 data.tar(1).gz。
                if (SafePathHelper.PathEquals(oldPath, newPath))
                {
                    item.MarkSkip("新路径与原路径相同，无需改名");
                    result.Add(item);
                    continue;
                }

                string? directory = Path.GetDirectoryName(newPath);

                if (string.IsNullOrWhiteSpace(directory))
                {
                    item.MarkInvalid("无法确定目标目录");
                    result.Add(item);
                    continue;
                }

                if (!Directory.Exists(directory))
                {
                    item.MarkInvalid("目标目录不存在");
                    result.Add(item);
                    continue;
                }

                RenameConflictResolver.ApplyPreviewConflict(item, options, newPath);

                result.Add(item);
            }
            catch (Exception ex)
            {
                string path = task?.CurrentPath ?? task?.OriginalPath ?? string.Empty;

                RenamePreviewItem item = new RenamePreviewItem
                {
                    OriginalPath = path,
                    OriginalFileName = Path.GetFileName(path),
                    DetectedFormat = task?.DetectedFormat ?? "Unknown",
                    Operation = GetOperationDisplayName(options),
                    NewPath = string.Empty,
                    NewFileName = string.Empty,
                    ConflictAction = options.ConflictAction
                };

                item.MarkInvalid("生成预览失败：" + ex.Message);
                result.Add(item);
            }
        }

        RenameConflictResolver.ResolveBatchDuplicateTargets(result);

        return result;
    }

    /// <summary>
    /// 预览表「操作」列的文字。
    ///
    /// <para>「智能修正」这一类把**检测格式**并进来（<c>按真实格式修正（ZIP）</c>）——
    /// 它以前是单独一列「检测格式」，可 5 种操作里只有这一种用得上它，
    /// 白占 78px 而名字列被挤到看不见后缀（2026-09-26 审计）。</para>
    /// </summary>
    private static string BuildOperationLabel(RenameOptions options, string? detectedFormat)
    {
        string name = GetOperationDisplayName(options);

        if (!string.Equals(options?.OperationType, "FixByDetectedFormat", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return string.IsNullOrWhiteSpace(detectedFormat) ||
               string.Equals(detectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}（{detectedFormat}）";
    }

    private static string GetOperationDisplayName(RenameOptions options)
    {
        if (options == null)
        {
            return "按真实格式修正";
        }

        return options.OperationType switch
        {
            "AddExtension" => "添加后缀",
            "ReplaceLastExtension" => "替换最后后缀",
            "DeleteLastExtension" => "删除最后后缀",
            "ReplaceFileNameText" => "替换文件名里的文字",
            "FixByDetectedFormat" => "按真实格式修正",
            _ => options.OperationType
        };
    }
}
