using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System.Text;

namespace ArchiveFixer.ViewModels.Export;

/// <summary>看内容 / 试密码结果的纯文本格式化。</summary>
internal static class InspectResultFormatter
{
    internal static string Describe(ArchiveInspectResult result, bool tryPasswords)
    {
        var text = new StringBuilder();

        text.AppendLine($"【{result.Task?.FileName ?? "（未知）"}】");

        if (!result.Success)
        {
            text.AppendLine($"  ✗ {result.Message}");
            return text.ToString().TrimEnd();
        }

        string size = TaskSpaceEstimate.FormatSize(result.TotalBytes);

        text.AppendLine(
            $"  格式 {result.Format} ｜ 文件 {result.FileCount} 个"
            + (result.DirectoryCount > 0 ? $" + 文件夹 {result.DirectoryCount} 个" : string.Empty)
            + $" ｜ 解压后约 {size}"
            + (result.IsMultiVolume ? " ｜ 分卷" : string.Empty));

        if (result.NeedsPassword)
        {
            text.AppendLine(result.PasswordFound
                ? $"  密码：需要 —— 能开的是「{result.PasswordLabel}」（试到第 {result.CandidatesTried} 个）"
                : $"  密码：需要 —— {result.CandidatesTried}/{result.CandidatesTotal} 个候选都没能开"
                  + (tryPasswords ? string.Empty : "（想要它替你逐个试：点「试密码」）"));
        }
        else
        {
            text.AppendLine("  密码：不需要");
        }

        if (result.TopEntries.Count > 0)
        {
            text.AppendLine("  里面有：");

            foreach (ArchiveInspectEntry entry in result.TopEntries)
            {
                string suffix = entry.IsDirectory ? "（文件夹）" : "  " + TaskSpaceEstimate.FormatSize(entry.Size);
                text.AppendLine($"    · {entry.Name}{suffix}");
            }

            int remaining = result.FileCount + result.DirectoryCount - result.TopEntries.Count;

            if (remaining > 0)
            {
                text.AppendLine($"    … 还有 {remaining} 项没列出来");
            }
        }

        text.AppendLine($"  （用时 {result.ElapsedText}）");

        return text.ToString().TrimEnd();
    }

    internal static string Summarize(ArchiveInspectResult result, bool tryPasswords)
    {
        if (!result.Success)
        {
            return result.Message;
        }

        string password = !result.NeedsPassword
            ? "不需要密码"
            : result.PasswordFound
                ? $"密码={result.PasswordLabel}"
                : $"没试出密码（{result.CandidatesTried}/{result.CandidatesTotal}）";

        return $"格式 {result.Format}，{result.FileCount} 个文件 / {TaskSpaceEstimate.FormatSize(result.TotalBytes)}，{password}";
    }
}
