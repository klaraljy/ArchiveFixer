using System;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 普通挡位/特定解压共用的不可拆调度单位。
    /// 分卷组和特定 unit 都只占一个单位，不允许把内部卷拆到不同槽位。
    /// </summary>
    public sealed class ScheduledWorkUnit
    {
        private ScheduledWorkUnit(
            object payload,
            long sourceBytes,
            int volumeCount,
            bool isVolumeGroup,
            double estimatedSeconds)
        {
            Payload = payload;
            SourceBytes = sourceBytes;
            VolumeCount = Math.Max(1, volumeCount);
            IsVolumeGroup = isVolumeGroup;
            EstimatedSeconds = Math.Max(1, estimatedSeconds);
        }

        public object Payload { get; }
        public long SourceBytes { get; }
        public int VolumeCount { get; }
        public bool IsVolumeGroup { get; }
        public double EstimatedSeconds { get; }

        /// <summary>按最新历史吞吐重新估算；只修改 pending，不碰正在跑的任务。</summary>
        public double RefreshEstimatedSeconds()
        {
            if (Payload is ScheduledExtractionItem item)
            {
                return TaskDurationEstimator.EstimateSeconds(
                    SourceBytes,
                    item.Task,
                    VolumeCount,
                    IsVolumeGroup);
            }

            if (Payload is SpecificBatchUnit unit)
            {
                return TaskDurationEstimator.EstimateSeconds(
                    SourceBytes,
                    unit.Entry,
                    VolumeCount,
                    IsVolumeGroup);
            }

            return EstimatedSeconds;
        }

        public T PayloadAs<T>() where T : class
        {
            return Payload as T ?? throw new InvalidOperationException("调度单位载荷类型不匹配。");
        }

        public static ScheduledWorkUnit FromPlanItem(ScheduledExtractionItem item)
        {
            return new ScheduledWorkUnit(
                item,
                item.Estimate.SourceBytes,
                item.Task.VolumeCount,
                item.Task.IsVolumeGroup || item.Task.VolumeCount > 1,
                TaskDurationEstimator.EstimateSeconds(
                    item.Estimate.SourceBytes,
                    item.Task,
                    item.Task.VolumeCount,
                    item.Task.IsVolumeGroup || item.Task.VolumeCount > 1));
        }

        public static ScheduledWorkUnit FromSpecificUnit(SpecificBatchUnit unit)
        {
            int volumeCount = unit.Followers.Count + 1;
            bool isGroup = unit.Followers.Count > 0;
            return new ScheduledWorkUnit(
                unit,
                unit.SourceBytes,
                volumeCount,
                isGroup,
                TaskDurationEstimator.EstimateSeconds(
                    unit.SourceBytes,
                    unit.Entry,
                    volumeCount,
                    isGroup));
        }
    }
}
