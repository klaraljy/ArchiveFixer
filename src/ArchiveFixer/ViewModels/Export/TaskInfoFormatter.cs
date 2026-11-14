using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System.Text;

namespace ArchiveFixer.ViewModels.Export
{
    /// <summary>
    /// 任务信息文本的纯格式化出口。
    ///
    /// <para>字段清单必须与 <see cref="ClipboardService.CopyTaskInfo"/> 保持一致；
    /// 这里的文本统一交给 ClipboardService 的脱敏入口，不直接写剪贴板。</para>
    /// </summary>
    internal static class TaskInfoFormatter
    {
        internal static string Format(ArchiveTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            builder.AppendLine("序号：" + task.Index);

            /*
             * ⛔ 与屏幕上一致（用户 2026-10-07：「这个显示的问题，根本就没有同步」）：
             * 名称 / 体积 / 路径 / 错误信息取**显示口径**那一份；裸值在下面三行里一个字不少。
             */
            builder.AppendLine("文件名：" + (task.DisplayFileName ?? string.Empty));
            builder.AppendLine("大小：" + (task.DisplaySizeText ?? string.Empty));
            builder.AppendLine("界面显示路径：" + (task.DisplayPath ?? string.Empty));
            builder.AppendLine("原始路径：" + (task.OriginalPath ?? string.Empty));
            builder.AppendLine("当前路径：" + (task.CurrentPath ?? string.Empty));
            builder.AppendLine("当前后缀：" + (task.CurrentExtension ?? string.Empty));
            builder.AppendLine("检测格式：" + (task.DetectedFormat ?? string.Empty));
            builder.AppendLine("建议后缀：" + (task.SuggestedExtension ?? string.Empty));
            builder.AppendLine("后缀状态：" + (task.ExtensionStatus ?? string.Empty));
            builder.AppendLine("密码状态：" + (task.PasswordStatus ?? string.Empty));
            builder.AppendLine("输出目录：" + (task.OutputPath ?? string.Empty));

            ArtifactCleanupScope artifactScope = MaintenanceCleanupService.ResolveArtifactScope(task);
            builder.AppendLine("其余物目录：" + (artifactScope.ArtifactDirectory ?? string.Empty));

            builder.AppendLine("操作：" + (task.Operation ?? string.Empty));
            builder.AppendLine("状态：" + (task.Status ?? string.Empty));
            builder.AppendLine("进度：" + (task.ProgressText ?? string.Empty));
            builder.AppendLine("错误信息：" + (task.DisplayErrorMessage ?? string.Empty));

            if (task.DisplayTransformationText.Length > 0)
            {
                builder.AppendLine("过程：" + task.DisplayTransformationText);
            }

            builder.AppendLine("耗时：" + (task.ElapsedText ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(task.DangerousEntriesWarning))
            {
                builder.AppendLine("可疑条目：" + task.DangerousEntriesWarning);
            }

            if (!string.IsNullOrWhiteSpace(task.PathLengthWarning))
            {
                builder.AppendLine("路径预警：" + task.PathLengthWarning);
            }

            if (!string.IsNullOrWhiteSpace(task.RunOptionsNote))
            {
                builder.AppendLine("本次选项：" + task.RunOptionsNote);
            }

            return builder.ToString().TrimEnd();
        }
    }
}
