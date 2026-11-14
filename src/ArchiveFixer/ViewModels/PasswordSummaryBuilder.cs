using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 密码本摘要/提示文案；MainViewModel 只负责把结果写进绑定属性。
/// </summary>
internal static class PasswordSummaryBuilder
{
    public static (string Summary, string Tooltip) Build(PasswordService passwordService)
    {
        string summary, tooltip;
        int count = passwordService.Passwords.Count;
        int enabled = passwordService.Passwords.Count(p => p.IsEnabled);
        string book = passwordService.LastImportedBookPath;

        IReadOnlyList<string> books = passwordService.RememberedBookPaths;

        if (passwordService.UseTextStore)
        {
            string textPath = passwordService.TextStore.FilePath;

            if (count == 0)
            {
                summary = "密码本：passwords.txt 暂无条目 ——点「密码列表管理」添加或导入";
                tooltip = "内置明文密码清单：" + textPath
                    + Environment.NewLine + "空密码仍会按设置尝试；日志不记明文。";
                AppendRememberedListWarningToSummary(passwordService, ref summary, ref tooltip);
                return (summary, tooltip);
            }

            summary = $"密码本：passwords.txt — {count} 条（启用 {enabled} 条）";
            tooltip = "内置明文密码清单：" + textPath
                + Environment.NewLine + "行顺序就是尝试顺序；可上下移动。外部密码本只导入一次，不再链接。";
            AppendRememberedListWarningToSummary(passwordService, ref summary, ref tooltip);
            return (summary, tooltip);
        }

        if (count == 0)
        {
            summary = "密码本：未加载（0 条）——点「密码列表管理」或在④「密码」页点「导入密码本…」";
            tooltip = "空密码仍会按设置尝试；密码列表按本机加密保存（可在设置里关掉），日志不记明文。";

            AppendRememberedListWarningToSummary(passwordService, ref summary, ref tooltip);
            return (summary, tooltip);
        }

        string name;

        if (books.Count > 1)
        {
            // 多本密码本（用户 2026-09-24 要求）：主界面这一行只报本数与总条数，明细在窗口里看。
            name = $"{books.Count} 本密码本";
        }
        else if (books.Count == 1)
        {
            name = System.IO.Path.GetFileName(books[0]);
        }
        else
        {
            name = string.IsNullOrWhiteSpace(book) ? "（手动添加）" : System.IO.Path.GetFileName(book);
        }

        summary = $"密码本：{name} — {count} 条（启用 {enabled} 条）";
        tooltip = BuildPasswordBookTooltip(book, books);

        AppendRememberedListWarningToSummary(passwordService, ref summary, ref tooltip);
        return (summary, tooltip);
    }

    private static void AppendRememberedListWarningToSummary(PasswordService passwordService, ref string summary, ref string tooltip)
    {
        if (passwordService.LastListWarning.Length == 0)
        {
            return;
        }

        summary += "　⚠ " + StatusText.PasswordListMemoryUnavailableShort;

        tooltip = tooltip.Length == 0
            ? passwordService.LastListWarning
            : tooltip + Environment.NewLine + Environment.NewLine + passwordService.LastListWarning;
    }

    private static string BuildPasswordBookTooltip(string lastImported, IReadOnlyList<string> books)
    {
        if (books.Count == 0)
        {
            return string.IsNullOrWhiteSpace(lastImported)
                ? "列表按本机加密保存（可在设置里关掉）；本文件未记录来源路径。"
                : lastImported;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("已记住的密码本（启动时按这个顺序逐本合并）：");

        foreach (string path in books)
        {
            builder.AppendLine();
            builder.Append(path);
        }

        return builder.ToString();
    }
}

