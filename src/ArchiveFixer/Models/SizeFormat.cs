using System.Globalization;

namespace ArchiveFixer.Models;

/// <summary>
/// 字节数格式化契约（从 Security/TaskSpaceEstimate 抽出的纯格式化实现）：
/// 口径必须与 <c>SpaceChecker</c> / <c>ResourceBudget</c> 一致；⛔ 不碰磁盘、不读配置。
/// </summary>
internal static class SizeFormat
{
    /// <summary>把字节数说成人话（GiB / MiB / KiB / 字节，固定 InvariantCulture）。</summary>
    internal static string Format(long bytes)
    {
        long value = bytes > 0 ? bytes : 0L;
        double gib = value / 1024d / 1024d / 1024d;

        if (gib >= 1d)
        {
            return gib.ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
        }

        double mib = value / 1024d / 1024d;

        if (mib >= 1d)
        {
            return mib.ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
        }

        double kib = value / 1024d;

        if (kib >= 1d)
        {
            return kib.ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
        }

        return value.ToString(CultureInfo.InvariantCulture) + " 字节";
    }
}

