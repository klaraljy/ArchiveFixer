using System;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 递归失败结论里两段"事实补充"的唯一出口（从 `ExtractionCoordinator` 搬出的第 4 个功能族，2026-10-23）：
    ///
    /// <list type="number">
    /// <item><description><b>连续两个候选解出来的产物完全相同</b>（结构化事实
    /// <see cref="RecursionLayerReport.CandidatesProducedIdenticalResults"/>）⇒ 补一句"换密码不会改变结果"；</description></item>
    /// <item><description><b>这一层真正试过几个候选</b>（<see cref="RecursionLayerReport.TriedCandidateCount"/>）⇒
    /// 补「（已试 N 个候选）」。</description></item>
    /// </list>
    ///
    /// <para>⛔ 判据只读结构化字段，⛔ 不比中文文案；已经写过同一句的不重复追加（幂等）。</para>
    /// </summary>
    public static class RecursionFailureNotes
    {
        public const string IdenticalResultsNote = "（连续两个候选解出来的产物完全相同，换密码不会改变结果）";

        /// <summary>这一趟的递归里有没有哪一层报过"候选结果完全相同"。</summary>
        public static bool HasIdenticalResults(RecursionResult? result) =>
            result?.Layers != null
            && result.Layers.Any(layer => layer != null && layer.CandidatesProducedIdenticalResults);

        /// <summary>追加"候选结果完全相同"那一句（已经有了就不再追加）。</summary>
        public static string AppendIdenticalResultsNote(string? message)
        {
            string current = message ?? string.Empty;

            return current.Contains("完全相同", StringComparison.Ordinal)
                ? current
                : current + IdenticalResultsNote;
        }

        /// <summary>追加「（已试 N 个候选）」；<paramref name="triedCandidates"/> ≤ 0 ⇒ 原样返回（老口径不动）。</summary>
        public static string AppendTriedCandidatesNote(string? message, int triedCandidates)
        {
            string current = message ?? string.Empty;

            if (triedCandidates <= 0)
            {
                return current;
            }

            return current.Contains("已试 ", StringComparison.Ordinal)
                ? current
                : current + $"（已试 {triedCandidates} 个候选）";
        }
    }
}
