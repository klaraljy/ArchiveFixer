using System;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「这一单收尾时间」的唯一出口（从 `ExtractionCoordinator` 搬出的第 10 个功能族，2026-10-23）。
    ///
    /// <para>为什么必须落：终态收口读的是"`EndTime` 有值 + `Outcome == Pending`"，
    /// 少了它任务会一直停在「还在跑」（耗时列跟着现在涨）、批末"用时"也取不到这一单。</para>
    /// </summary>
    public static class TaskTimingFinalizer
    {
        public static void Finalize(ArchiveTask? task)
        {
            if (task == null)
            {
                return;
            }

            task.EndTime ??= DateTime.Now;

            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : task.ElapsedText;

            task.LastUpdatedTime = DateTime.Now;
        }
    }
}
