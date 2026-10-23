using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「递归结果 → 任务上的结构化事实」的唯一搬运出口（从 `ExtractionCoordinator` 搬出的第 5 个功能族，2026-10-23）。
    ///
    /// <para>现在只搬两件：这一层**真正试过几个候选**（<see cref="RecursionLayerReport.TriedCandidateCount"/>）
    /// 与**候选间结果是否完全相同**（<see cref="RecursionLayerReport.CandidatesProducedIdenticalResults"/>）。
    /// ⛔ 这两个都是**事实搬运**，不做任何判据；消费方（收尾文案）自己读任务上的字段。</para>
    /// </summary>
    public static class RecursionTaskFacts
    {
        public static void Apply(ArchiveTask task, RecursionResult? result)
        {
            if (task == null)
            {
                return;
            }

            if (result?.Layers is { Count: > 0 } layers)
            {
                task.RecursionTriedCandidateCount = layers[^1].TriedCandidateCount;
            }
            else
            {
                task.RecursionTriedCandidateCount = 0;
            }

            task.RecursionCandidatesIdentical = RecursionFailureNotes.HasIdenticalResults(result);
        }
    }
}
