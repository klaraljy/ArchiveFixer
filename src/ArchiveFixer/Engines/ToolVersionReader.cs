using System;
using System.IO;

namespace ArchiveFixer.Engines;

/// <summary>
/// 外部工具版本号的**唯一读法**：读文件版本资源，拿不到一律 <c>"unknown"</c>。
///
/// <para>为什么不解析工具自己输出的 banner：那要起一个进程，而设置界面每次刷新都要显示版本。
/// 文件版本资源的写法（<c>7.23.0</c>）与实际 banner（<c>UNRAR 7.23</c>）对得上，
/// 报告里写 <c>7.23.0</c> 足够追到"当时用的是哪个构建"（不变量 14）。</para>
///
/// <para>四条解析链（7-Zip / UnRAR / Rar.exe / WinRAR.exe）共用这一处，⛔ 不许各写一份 try/catch ——
/// "取不到就 unknown"是同一个口径，分散写迟早会出现某一条链编造版本号。</para>
/// </summary>
internal static class ToolVersionReader
{
    /// <summary>取不到版本时统一用这个词（⛔ 不编造版本号）。</summary>
    internal const string UnknownVersion = "unknown";

    /// <summary>读文件版本；路径为空、文件不存在或读失败都返回 <see cref="UnknownVersion"/>。</summary>
    internal static string ReadFileVersion(string? executablePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return UnknownVersion;
            }

            string? version = System.Diagnostics.FileVersionInfo.GetVersionInfo(executablePath).FileVersion;

            return string.IsNullOrWhiteSpace(version) ? UnknownVersion : version;
        }
        catch
        {
            return UnknownVersion;
        }
    }
}
