using System;
using System.IO;

namespace ArchiveFixer.Engines;

/// <summary>
/// 外部工具路径的**只读验证**：设置里的原始值怎么归一化、一个路径算不算"可用的自选文件"、
/// 那个文件是不是 GUI 版 <c>WinRAR.exe</c>。
///
/// <para>⛔ 只做 <c>File.Exists</c> 与文件名这一类无副作用判断：不复制、不搬动、不改名
/// （<c>Rar.exe</c> / <c>WinRAR.exe</c> 是共享软件，只允许检测与调用，见 AGENTS.md §3.1）。</para>
///
/// <para>判据的用途见 <see cref="ToolDiscovery"/> 里每条解析链的注释：自选路径"填了但文件不存在"
/// 时**不许吃掉后面的档位**，这里返回 false 就是让解析链继续往下走。</para>
/// </summary>
internal static class ToolPathValidator
{
    /// <summary>设置里读到的路径归一化：去掉首尾空白，<c>null</c> 当空串。</summary>
    internal static string Normalize(string? path)
    {
        return path?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 自选路径"填了**而且**文件真的在"。
    ///
    /// <para>⚠ 别把它简化成只判 <c>File.Exists</c>：<c>File.Exists("")</c> 也是 false，
    /// 但语义上"没填"和"填了但不存在"要分开 —— 前者是"按默认顺序找"，后者是"回落并如实说明"。</para>
    /// </summary>
    internal static bool IsUsableCustomPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    /// <summary>
    /// 这个名字确实是 GUI 版（<c>WinRAR.exe</c>）—— 只有它支持 <c>-ibck</c> 后台模式，
    /// 命令行版 <c>Rar.exe</c> 见到 <c>-ibck</c> 会直接报命令行错误并退出。
    /// </summary>
    internal static bool IsWinRarGuiExecutable(string? path)
    {
        try
        {
            return File.Exists(path)
                && string.Equals(Path.GetFileName(path), "WinRAR.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
