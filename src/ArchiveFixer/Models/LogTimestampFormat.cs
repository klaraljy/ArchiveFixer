using System;
using System.Globalization;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 日志/报告时间戳的唯一格式与格式化出口：固定 <see cref="CultureInfo.InvariantCulture"/>，
    /// 不随系统区域日历（泰历等）变；屏幕日志与文件日志共用这一份。
    /// </summary>
    internal static class LogTimestampFormat
    {
        /// <summary>时间戳格式（与既有日志解析口径一致）。</summary>
        internal const string Timestamp = "yyyy-MM-dd HH:mm:ss";

        /// <summary>按 <see cref="Timestamp"/> 格式化。</summary>
        internal static string Format(DateTime value) =>
            value.ToString(Timestamp, CultureInfo.InvariantCulture);
    }
}

