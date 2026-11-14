using System;
using System.IO;

namespace ArchiveFixer.Services;

/// <summary>
/// 把编译期 CallerFilePath 收敛成诊断包里的相对源码路径。
/// 目标：用户把 zip 发回来，开发者能直接按 <c>src/...</c> 找到代码；
/// ⛔ 不把开发者机器上的绝对仓库路径带进日志。
/// </summary>
public static class DiagnosticSource
{
    public static string NormalizeFile(string? callerFile)
    {
        if (string.IsNullOrWhiteSpace(callerFile))
        {
            return string.Empty;
        }

        string normalized = callerFile.Replace('\\', '/');

        if (normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        int src = normalized.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);

        if (src >= 0)
        {
            return normalized[(src + 1)..];
        }

        int tests = normalized.IndexOf("/tests/", StringComparison.OrdinalIgnoreCase);

        if (tests >= 0)
        {
            return normalized[(tests + 1)..];
        }

        try
        {
            return Path.GetFileName(normalized);
        }
        catch
        {
            return normalized;
        }
    }
}
