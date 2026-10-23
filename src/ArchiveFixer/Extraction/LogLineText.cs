using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 日志里"路径怎么说才短"的纯格式化（第 23 轮前置：从协调器 LogCaptureStep 分片提到 Extraction，
    /// 好让其余物那一步的静态方法也能用同一份口径）。
    /// </summary>
    internal static class LogLineText
    {
        /// <summary>摘要里一条路径最多留几个字符（超了就从中间省略）。</summary>
        private const int MaxSummaryPathLength = 72;


        /// <summary>
        /// 落点写成"短地址"（用户 2026-09-25 第 44 条：满屏几百字符的绝对路径最占地方）。
        /// 规则：从**输出根**往下保留；拿不到输出根就保留末两段，前面用 `…\` 省掉。
        /// 完整路径仍然在任务详情 / 失败清单 / ①页「输出位置」里（那里要求看到完整地址）。
        /// </summary>
        internal static string DescribeShortDestination(string? collectedPath, string? outputPath)
        {
            string path = !string.IsNullOrWhiteSpace(collectedPath) ? collectedPath! : outputPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (trimmed.Length <= MaxSummaryPathLength)
            {
                return trimmed;
            }

            string[] segments = trimmed.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length <= 3)
            {
                return trimmed;
            }

            string tail = string.Join(Path.DirectorySeparatorChar, segments[^2..]);

            return "…" + Path.DirectorySeparatorChar + tail;
        }

        /// <summary>
        /// 只留路径的**末 N 段**（<c>H:\…\共用根\其余物\025</c> → <c>其余物\025</c>）。
        ///
        /// <para>其余物那一行用它（用户 2026-09-26 第 45 条：真机上逐任务一条 60 字符的完整路径 × 118 条，
        /// 而他要认的只是"哪一个包的其余物"）。完整路径在 ③页「工作区残留」与输出目录里都看得到，
        /// **失败**时也照旧全量打印。</para>
        /// </summary>
        internal static string DescribeShortTail(string? path, int segments)
        {
            if (string.IsNullOrWhiteSpace(path) || segments <= 0)
            {
                return string.Empty;
            }

            string trimmed = path!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string[] parts = trimmed.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            return parts.Length <= segments
                ? trimmed
                : string.Join(Path.DirectorySeparatorChar, parts[^segments..]);
        }

        /// <summary>其余物目录里有几个顶层条目（只数，不递归 —— 只为一行日志说清"有多少东西"）。</summary>
        internal static int CountRestEntries(string? directory)
        {
            try
            {
                return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                    ? 0
                    : Directory.GetFileSystemEntries(directory).Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}