using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 批前提醒的文案与结论落日志（从 `ExtractionCoordinator.BatchReminderStep.cs` 搬出）：
/// 两段结论的 WARN、确认框正文；⛔ 文案与判据沿用原实现。
/// </summary>
internal sealed class ExtractionBatchReminderMessages
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionBatchReminderMessages(ExtractionCoordinator host)
    {
        _host = host;
    }


    /// <summary>批首写一条 WARN，把两段的结论都落进日志（数量 + 前几条名字）。</summary>
    internal void LogReminderFindings(SourceJunkScanResult junk, IReadOnlyList<ArchiveTask> noPassword)
    {
        string junkPart = junk.HasAnything
            ? string.Format(
                StatusText.JunkReminderLogFoundFormat,
                junk.TotalCount,
                string.Join("、", junk.SampleNames))
            : StatusText.JunkReminderLogNoneText;

        string passwordPart = noPassword.Count > 0
            ? string.Format(
                StatusText.JunkReminderLogFoundFormat,
                noPassword.Count,
                string.Join(
                    "、",
                    noPassword.Take(ExtractionCoordinator.MaxPasswordFailureNamesInDialog).Select(ExtractionCoordinator.DisplayNameOf)))
            : StatusText.JunkReminderLogNoneText;

        _host.AppendLog("WARN", string.Format(StatusText.JunkReminderLogFormat, junkPart, passwordPart));
    }

    /// <summary>
    /// 拼提醒正文。三段话的顺序见 <see cref="StatusText"/> 里那一组的说明（**不要重排**）：
    /// ① 这些文件是什么 → ② 本程序不会动它们 → ③ 解压完你自己判断要不要删。
    /// </summary>
    internal static string BuildReminderMessage(SourceJunkScanResult junk, IReadOnlyList<ArchiveTask> noPassword)
    {
        var builder = new StringBuilder();

        builder.AppendLine(StatusText.JunkReminderIntro);

        if (junk.HasAnything)
        {
            builder.AppendLine();
            builder.AppendLine(StatusText.JunkReminderJunkHeader);
            builder.AppendLine(string.Format(StatusText.JunkReminderJunkCountFormat, junk.TotalCount));

            foreach (IGrouping<string, SourceJunkItem> group in
                     junk.Items.GroupBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine("  " + group.Key);

                foreach (SourceJunkItem item in group)
                {
                    // 目录要标出来（第 32 条）：真实资源包把密码提示做成一个文件夹，
                    // 与"说明.txt"混在一起不标就分不清哪个是文件夹。
                    builder.AppendLine("    " + item.FileName + (item.IsDirectory ? "（文件夹）" : string.Empty));
                }
            }

            if (junk.ExtraCount > 0)
            {
                builder.AppendLine(string.Format(StatusText.JunkReminderJunkMoreFormat, junk.ExtraCount));
            }

            if (junk.Truncated)
            {
                builder.AppendLine(StatusText.JunkReminderTruncatedNote);
            }

            builder.AppendLine(StatusText.JunkReminderJunkFooter);
        }

        if (noPassword.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(string.Format(StatusText.JunkReminderPasswordHeaderFormat, noPassword.Count));

            foreach (ArchiveTask task in noPassword.Take(ExtractionCoordinator.MaxPasswordFailureNamesInDialog))
            {
                builder.AppendLine("    " + ExtractionCoordinator.DisplayNameOf(task));
            }

            if (noPassword.Count > ExtractionCoordinator.MaxPasswordFailureNamesInDialog)
            {
                builder.AppendLine(string.Format(
                    StatusText.JunkReminderPasswordMoreFormat,
                    noPassword.Count - ExtractionCoordinator.MaxPasswordFailureNamesInDialog));
            }

            builder.AppendLine(StatusText.JunkReminderPasswordOutcome);
            builder.AppendLine(StatusText.JunkReminderPasswordExit);
        }

        return builder.ToString().TrimEnd();
    }
}

