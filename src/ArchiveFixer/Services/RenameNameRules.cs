using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 纯文件名规则：加 / 换 / 删后缀、替换名字里的文字、收口非法尾部、统一后缀点号、拆后缀段。
///
/// <para>从 <see cref="RenameService"/> 抽出的协作类（「命名规则」的算子）：全部只做字符串运算，
/// ⛔ 不读文件系统、不判格式、不碰任务状态；判据与拆分前逐字相同。</para>
///
/// <para>「替换文件名里的文字」一条对分卷**豁免**改名闸门（理由见调用方
/// <see cref="RenameTargetPlanner"/>）；这里只负责换字，判不判分卷不归本类。</para>
/// </summary>
internal static class RenameNameRules
{
    internal static string BuildAddExtensionFileName(string fileName, string extension)
    {
        extension = NormalizeExtension(extension, ".7z");

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        return fileName + extension;
    }

    internal static string BuildReplaceLastExtensionFileName(string fileName, string extension)
    {
        extension = NormalizeExtension(extension, ".7z");

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        string currentExtension = Path.GetExtension(fileName);

        if (string.IsNullOrWhiteSpace(currentExtension))
        {
            return fileName + extension;
        }

        string baseName = fileName[..^currentExtension.Length];

        if (string.IsNullOrWhiteSpace(baseName))
        {
            return fileName;
        }

        return baseName + extension;
    }

    internal static string BuildDeleteExtensionFileName(string fileName, int deleteCount)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        if (deleteCount < 1)
        {
            deleteCount = 1;
        }

        string result = fileName;

        for (int i = 0; i < deleteCount; i++)
        {
            string extension = Path.GetExtension(result);

            if (string.IsNullOrWhiteSpace(extension))
            {
                break;
            }

            result = result[..^extension.Length];

            if (string.IsNullOrWhiteSpace(result))
            {
                return fileName;
            }
        }

        return result;
    }

    /// <summary>
    /// 「替换文件名里的文字」：把名字里出现的那一段换成另一段（留空 = 删掉）。
    ///
    /// <para>口径（写进预览、也写进弹窗提示）：</para>
    /// <list type="bullet">
    /// <item><description>在**整个文件名**（含后缀那一段）上替换 —— 他那种"后缀被塞字"（`222.ra删除r`）
    /// 与"名字中间被塞字"（`Code Complete-BZ.7z(删掉.001`）才能用同一颗按钮修；</description></item>
    /// <item><description>**全部出现**都换（不是只换第一处）：修名字时"每一处都得干净"；</description></item>
    /// <item><description>**区分大小写**（Ordinal）：`ABC` 与 `abc` 是两回事，宁可不换也不误换；</description></item>
    /// <item><description>找不到 / 找的是空串 → 原样返回（预览里会显示「将跳过 / 无需改名」）。</description></item>
    /// </list>
    /// </summary>
    internal static string BuildReplaceTextFileName(string fileName, string? findText, string? replaceText)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrEmpty(findText))
        {
            return fileName;
        }

        if (!fileName.Contains(findText, StringComparison.Ordinal))
        {
            return fileName;
        }

        string replaced = fileName.Replace(findText, replaceText ?? string.Empty, StringComparison.Ordinal);

        // 换完可能变成空名 / 只剩后缀（例如把主名整段删掉）：那种名字不合法或没意义，宁可不换。
        return string.IsNullOrWhiteSpace(replaced) ? fileName : replaced;
    }

    /// <summary>
    /// 文件名的**最后一段是纯数字**吗（<c>amb909.7.01</c> → <c>01</c>；<c>amb909.z.2</c> → <c>2</c>）？
    ///
    /// <para>纯数字尾巴在这一行里一律当**卷号**看，绝不当后缀去替换（2026-09-29 真机事故：
    /// 把 `.01` 改成 `.7z` 等于把卷号删了，7-Zip 再也拼不起整组）。</para>
    /// </summary>
    internal static bool LooksLikeNumericVolumeTail(string fileName)
    {
        string[] parts = fileName.Split('.');

        if (parts.Length < 2)
        {
            return false;
        }

        string tail = parts[^1];

        return tail.Length > 0 && tail.All(char.IsDigit);
    }

    /// <summary>
    /// 改名结果只允许是合法文件名的形状：去掉尾部点/空格，折叠连续点。
    /// 现场 `aaa-W2-2.7z...删除` 命中多后缀后被削成 `aaa-W2-2.7z..`，
    /// 引擎按这个名字找不到文件；这里在返回前统一收口。
    /// </summary>
    internal static string NormalizeResultFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        name = name.TrimEnd('.', ' ');

        while (name.Contains("..", StringComparison.Ordinal))
        {
            name = name.Replace("..", ".", StringComparison.Ordinal);
        }

        return name;
    }

    internal static List<string> GetAllExtensions(string fileName)
    {
        List<string> result = new();

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return result;
        }

        string[] parts = fileName.Split('.', StringSplitOptions.None);

        if (parts.Length <= 1)
        {
            return result;
        }

        for (int i = 1; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i]))
            {
                continue;
            }

            result.Add("." + parts[i]);
        }

        return result;
    }

    internal static string NormalizeExtension(string? extension, string fallback)
    {
        string value = string.IsNullOrWhiteSpace(extension)
            ? fallback
            : extension.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            value = ".7z";
        }

        if (!value.StartsWith(".", StringComparison.Ordinal))
        {
            value = "." + value;
        }

        return value;
    }
}
