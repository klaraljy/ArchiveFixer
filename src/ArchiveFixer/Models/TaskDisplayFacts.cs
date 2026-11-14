using System;
using System.IO;

namespace ArchiveFixer.Models;

/// <summary>
/// 任务行显示要用的**纯路径 / 纯格式化**事实：文件在不在、目录是哪一层、体积怎么念。
///
/// <para>从 <see cref="ArchiveTask"/> 抽出的协作类（「身份显示」那一块）：这里只回答
/// "给定这条路径，给用户看的那个字符串是什么"，⛔ 不持有任务状态、不发变更通知、
/// 不参与任何解压 / 删除 / 统计判据。</para>
///
/// <para><b>失败模型</b>：所有 IO 意外一律吞掉，读不到就如实写"读不到"（<c>-</c> / 空串），
/// ⛔ 不抛、不编数字 —— 这是显示层，任何异常都不允许影响解压。</para>
///
/// <para>生命周期：纯静态、无状态；输入路径与输出文字一一对应。</para>
/// </summary>
internal static class TaskDisplayFacts
{
    /// <summary>文件此刻在盘上吗（任何 IO 意外都算"不在"）。</summary>
    internal static bool ExistsOnDisk(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>路径的目录（形状不合法 / 取不到 ⇒ 空串）。</summary>
    internal static string SafeDirectoryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>两条路径是否在同一个目录（目录取不到 ⇒ <c>false</c>）。</summary>
    internal static bool IsSameDirectory(string? left, string? right)
    {
        string leftDirectory = SafeDirectoryOf(left);

        return leftDirectory.Length > 0
            && string.Equals(leftDirectory, SafeDirectoryOf(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读一个文件的体积文字（读不到 ⇒ 短横，⛔ 不显示 0 字节）。</summary>
    internal static string DescribeSize(string path)
    {
        try
        {
            return File.Exists(path) ? FormatSize(new FileInfo(path).Length) : "-";
        }
        catch
        {
            return "-";
        }
    }

    /// <summary>
    /// 人读的字节数（列表列宽有限，所以最多两位小数、单位取最合适的那一档）。
    /// 单位与 <c>Storage/TaskSpaceEstimate.FormatSize</c> **完全一致**（KiB / MiB / GiB / 字节）——
    /// 界面上两处对同一块盘报出两种单位，用户会以为程序在算两个不同的数（历史上被投诉过）。
    /// 这里自带一份实现是为了**不让 Models 层反向依赖 Storage 层**（分层铁律）。
    /// </summary>
    internal static string FormatSize(long bytes)
    {
        string[] units = { "字节", "KiB", "MiB", "GiB", "TiB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
