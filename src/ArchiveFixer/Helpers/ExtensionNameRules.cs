using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Helpers;

/// <summary>
/// 后缀**词表与解析**的唯一一份实现：三张后缀表（归档 / 伪装 / ZIP 容器）、
/// 后缀的取/加/删/替换、建议后缀，以及"这个文件算不算可疑"。
///
/// <para><b>为什么单独立一类</b>：这里全是**纯字符串**操作 —— 不打开文件、不看内容、不碰盘；
/// 而"卷标记 / 骨架 / 卷名重建"那三层是**分发与还原判据**，两类原先同处一个文件却互不依赖。
/// 拆开后本类是最底层：上面三层都转调它。<see cref="KnownArchiveExtensions"/> 是**唯一词表**
/// （⛔ 别在别处再抄一张；<see cref="ZipContainerExtensions"/> 那张"本身就是 ZIP 容器"的表同理）。</para>
///
/// <para>⚠ 本类只回答"这个后缀是什么 / 这算不算可疑"，⛔ **不回答"该不该改名"**。</para>
/// </summary>
internal static class ExtensionNameRules
{
    /// <summary>
    /// 常见压缩包后缀。
    /// </summary>
    public static readonly HashSet<string> KnownArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip",
            ".rar",
            ".7z",
            ".gz",
            ".gzip",
            ".bz2",
            ".xz",
            ".tar",
            ".tgz",
            ".tbz2",
            ".txz",
            ".001",
            ".z",
            ".cab",
            ".iso",
            ".arj",
            ".lzh",
            ".zst",
            ".lz4",
            ".rpm"
        };

    /// <summary>
    /// 常见伪装后缀。
    /// </summary>
    public static readonly HashSet<string> SuspiciousFakeExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".gif",
            ".bmp",
            ".webp",
            ".pdf",
            ".mp4",
            ".mkv",
            ".avi",
            ".mp3",
            ".wav",
            ".txt",
            ".doc",
            // .docx / .xlsx / .pptx 已从伪装表移除：它们本身就是 ZIP 容器（Office Open XML），
            // 把"文件是 ZIP"当成伪装属于误报 —— 一份正常 Word 文档会被判成"疑似伪装"。
            ".xls",
            ".ppt",
            ".html",
            ".htm"
        };

    /// <summary>
    /// "本身就是 ZIP 容器"的合法扩展名。
    ///
    /// 为什么必须单独列一张表：
    /// 这些格式（APK / JAR / Office Open XML / EPUB / VSIX / NuGet 包…）的内容就是 ZIP，
    /// 但它们的后缀**本来就是对的**。如果按"检测到 ZIP 但后缀不是 .zip"判成"后缀不匹配"，
    /// 「智能修正后缀」就会把 `xxx.apk` 改成 `xxx.zip` —— 把安装包变成一个打不开的压缩包。
    /// 所以它们一律视为"后缀正常"。
    ///
    /// 真实踩到：`rar-android-722.132.apk`（Android 版 RAR）被判"后缀不匹配"、建议改成 .zip。
    /// </summary>
    public static readonly HashSet<string> ZipContainerExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".apk",     // Android 应用包
            ".aab",     // Android App Bundle
            ".jar",     // Java 归档
            ".war",
            ".ear",
            ".docx",    // Office Open XML
            ".docm",
            ".xlsx",
            ".xlsm",
            ".pptx",
            ".pptm",
            ".odt",     // OpenDocument
            ".ods",
            ".odp",
            ".epub",    // 电子书
            ".vsix",    // VS 扩展
            ".nupkg",   // NuGet 包
            ".ipa",     // iOS 应用包
            ".xpi",     // Firefox 扩展
            ".kmz",     // Google Earth
            ".cbz"      // 漫画包
        };

    /// <summary>是不是"本身就是 ZIP 容器"的合法扩展名（后缀不该被改）。</summary>
    public static bool IsZipContainerExtension(string? extension)
    {
        extension = NormalizeExtension(extension);

        return !string.IsNullOrWhiteSpace(extension)
               && ZipContainerExtensions.Contains(extension);
    }

    /// <summary>
    /// 判断是否是常见压缩包后缀。
    /// </summary>
    public static bool IsKnownArchiveExtension(string? extension)
    {
        extension = NormalizeExtension(extension);
        return !string.IsNullOrWhiteSpace(extension)
               && KnownArchiveExtensions.Contains(extension);
    }

    /// <summary>
    /// 判断是否是常见伪装后缀。
    /// </summary>
    public static bool IsSuspiciousFakeExtension(string? extension)
    {
        extension = NormalizeExtension(extension);
        return !string.IsNullOrWhiteSpace(extension)
               && SuspiciousFakeExtensions.Contains(extension);
    }

    /// <summary>
    /// 判断文件名是否无后缀。
    /// </summary>
    public static bool HasNoExtension(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(Path.GetExtension(fileNameOrPath));
    }

    /// <summary>
    /// 获取最后一个后缀。
    /// 没有后缀返回空字符串。
    /// </summary>
    public static string GetLastExtension(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return string.Empty;
        }

        return Path.GetExtension(fileNameOrPath) ?? string.Empty;
    }

    /// <summary>
    /// 获取最后一个后缀。
    /// 没有后缀返回“无”。
    /// </summary>
    public static string GetLastExtensionDisplay(string? fileNameOrPath)
    {
        string ext = GetLastExtension(fileNameOrPath);
        return string.IsNullOrWhiteSpace(ext) ? "无" : ext;
    }

    /// <summary>
    /// 获取文件名中所有后缀。
    /// 例如：
    /// test.rar.pdf.jpg -> .rar .pdf .jpg
    /// </summary>
    public static List<string> GetAllExtensions(string? fileNameOrPath)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return result;
        }

        string fileName = Path.GetFileName(fileNameOrPath);
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
            if (!string.IsNullOrEmpty(parts[i]))
            {
                result.Add("." + parts[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// 判断是否有多个后缀。
    /// </summary>
    public static bool HasMultipleExtensions(string? fileNameOrPath)
    {
        return GetAllExtensions(fileNameOrPath).Count >= 2;
    }

    /// <summary>
    /// 判断文件名中是否包含指定后缀。
    /// </summary>
    public static bool ContainsExtension(string? fileNameOrPath, string? extension)
    {
        extension = NormalizeExtension(extension);

        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return GetAllExtensions(fileNameOrPath)
            .Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 标准化后缀。
    /// 输入 7z 返回 .7z。
    /// 输入 .7z 返回 .7z。
    /// </summary>
    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        string value = extension.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!value.StartsWith(".", StringComparison.Ordinal))
        {
            value = "." + value;
        }

        return value;
    }

    /// <summary>
    /// 替换最后一个后缀。
    /// 无后缀时直接添加。
    /// </summary>
    public static string ReplaceLastExtension(string filePath, string targetExtension)
    {
        targetExtension = NormalizeExtension(targetExtension);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        if (string.IsNullOrWhiteSpace(targetExtension))
        {
            return filePath;
        }

        string? dir = Path.GetDirectoryName(filePath);
        string fileName = Path.GetFileName(filePath);

        string newFileName;

        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            newFileName = fileName + targetExtension;
        }
        else
        {
            newFileName = Path.GetFileNameWithoutExtension(fileName) + targetExtension;
        }

        return string.IsNullOrWhiteSpace(dir)
            ? newFileName
            : Path.Combine(dir, newFileName);
    }

    /// <summary>
    /// 添加后缀。
    /// </summary>
    public static string AddExtension(string filePath, string targetExtension)
    {
        targetExtension = NormalizeExtension(targetExtension);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        if (string.IsNullOrWhiteSpace(targetExtension))
        {
            return filePath;
        }

        return filePath + targetExtension;
    }

    /// <summary>
    /// 删除最后一个后缀。
    /// </summary>
    public static string DeleteLastExtension(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        string? dir = Path.GetDirectoryName(filePath);
        string fileName = Path.GetFileName(filePath);

        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            return filePath;
        }

        string newFileName = Path.GetFileNameWithoutExtension(fileName);

        return string.IsNullOrWhiteSpace(dir)
            ? newFileName
            : Path.Combine(dir, newFileName);
    }

    /// <summary>
    /// 删除多个后缀。
    /// 例如：
    /// test.rar.pdf.jpg 删除 2 个 -> test.rar
    /// </summary>
    public static string DeleteMultipleExtensions(string filePath, int count)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        if (count <= 0)
        {
            return filePath;
        }

        string result = filePath;

        for (int i = 0; i < count; i++)
        {
            string next = DeleteLastExtension(result);

            if (string.Equals(next, result, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            result = next;
        }

        return result;
    }

    /// <summary>
    /// 根据真实格式获取建议后缀。
    /// </summary>
    public static string GetSuggestedExtensionByFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return string.Empty;
        }

        return format.ToUpperInvariant() switch
        {
            "ZIP" => ".zip",
            "ZIP_EMPTY" => ".zip",
            "ZIP_SPANNED" => ".zip",
            "7Z" => ".7z",
            "RAR4" => ".rar",
            "RAR5" => ".rar",
            "RAR" => ".rar",
            "GZIP" => ".gz",
            "BZIP2" => ".bz2",
            "XZ" => ".xz",
            "TAR" => ".tar",
            "CAB" => ".cab",
            "ARJ" => ".arj",
            "LZH" => ".lzh",
            "Z" => ".z",
            "ZSTD" => ".zst",
            "LZ4" => ".lz4",
            "RPM" => ".rpm",
            "ISO" => ".iso",
            _ => string.Empty
        };
    }

    /// <summary>
    /// 判断某个文件是否是扫描模式中的疑似文件。
    /// </summary>
    public static bool IsSuspiciousFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string ext = GetLastExtension(filePath);

        if (string.IsNullOrWhiteSpace(ext))
        {
            return true;
        }

        if (HasMultipleExtensions(filePath))
        {
            return true;
        }

        if (IsKnownArchiveExtension(ext))
        {
            return true;
        }

        if (IsSuspiciousFakeExtension(ext))
        {
            return true;
        }

        return false;
    }
}
