using System;
using System.Collections.Generic;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **引擎原话落日志的唯一出口**（用户 2026-09-27："引擎原话从不落日志"）。
    ///
    /// <para>为什么必须有出口：7-Zip / UnRAR 的输出里存着真正说明原因的句子
    /// （<c>Cannot open encrypted archive. Wrong password?</c>、<c>CRC Failed in …</c>），
    /// 而全仓只有"解析它"的地方，**没有任何一处把它写进日志** —— 真机那次 13 分钟白跑，
    /// 日志里连一句引擎原话都没有。</para>
    ///
    /// <para>两条路径（单层 <c>ExtractionCoordinator</c>、递归 <c>RecursiveExtractor</c>）
    /// 都只调这里的两个方法，⛔ 谁都不许自己再挑一遍行、自己再拼一遍前缀
    /// （§9.5：同一件事的真值只允许有一个出口）：</para>
    /// <list type="bullet">
    /// <item><description>失败 / 部分完成 → <see cref="LogFailureAsync"/>（按 <c>MaxLines</c> 挑行，ERROR / WARN）；</description></item>
    /// <item><description>详细日志档 → <see cref="LogVerboseAsync"/>（参数摘要 + 原话，成功也写）。</description></item>
    /// </list>
    ///
    /// <para>⚠ 挑行规则与结论里那几行**同一份**（<see cref="EngineOutputKeywords"/>），
    /// 而且这里只输出 <see cref="ArchiveOperationResult.KeyOutputLines"/> 已经整理好的行 ——
    /// 整段 stdout 永远不进日志（噪声 + 可能带用户名路径）。</para>
    /// </summary>
    internal static class EngineOutputLog
    {
        /// <summary>非详细档：失败时把引擎原话写进日志（` ｜ ` 连接，最多 3 行）。</summary>
        public static void LogFailure(
            Action<string, string>? log,
            string taskLabel,
            ArchiveOperationResult? result)
        {
            if (log == null || result == null)
            {
                return;
            }

            IReadOnlyList<string> lines = result.KeyOutputLines();

            if (lines.Count == 0)
            {
                return;
            }

            string joined = string.Join(ArchiveOperationResult.KeyOutputLineSeparator, lines);

            string text = $"{taskLabel}：{result.EngineOutputLabel}：{joined}";

            /*
             * 级别按**机器终态**判（⛔ 不比中文）：部分完成 = 有东西解出来了 = WARN；
             * 其余失败 = ERROR。两次调用各自取一次，级别与文字来自同一份结果对象。
             */
            log(result.IsPartiallyCompleted ? "WARN" : "ERROR", text);
        }

        /// <summary>
        /// 详细日志档：**参数摘要 + 引擎原话**（无论成败都写）。
        ///
        /// <para>为什么要"无论成败"：用户开了详细日志就是要把这一次调用看全 ——
        /// 成功那一次的参数与原话恰恰是"上一次失败到底差在哪"的对照物。</para>
        /// </summary>
        public static void LogVerbose(
            Action<string, string>? log,
            string taskLabel,
            ArchiveOperationResult? result)
        {
            if (log == null || result == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.CommandSummary))
            {
                log("INFO", $"{taskLabel}：{StatusText.EngineCommandSummaryPrefix}{result.CommandSummary}");
            }

            IReadOnlyList<string> lines = result.KeyOutputLines();

            if (lines.Count == 0)
            {
                return;
            }

            string joined = string.Join(ArchiveOperationResult.KeyOutputLineSeparator, lines);

            log("INFO", $"{taskLabel}：{result.EngineOutputLabel}：{joined}");
        }
    }
}
