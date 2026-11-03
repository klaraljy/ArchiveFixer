using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 一批任务的纯汇总结果：tally 与 severity 必须来自同一份快照/清单，UI 只读它。
    /// </summary>
    public sealed class BatchReport
    {
        public BatchOutcomeTally Tally { get; init; } = new();

        public BatchSummarySeverity Severity { get; init; } = BatchSummarySeverity.Success;

        public static BatchReport Create(IEnumerable<ArchiveTask>? tasks)
        {
            BatchOutcomeTally tally = BatchOutcomeTally.Count(tasks);
            return new BatchReport
            {
                Tally = tally,
                Severity = BatchSummarySeverityRules.FromTally(tally)
            };
        }
    }
}
