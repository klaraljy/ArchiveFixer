using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 空间不足模式的动态并发闸：按队列顺序（已从大到小）算当前还能再启动几个。
    /// 只做纯计算，不碰磁盘；调用方把真实可用空间和正在跑的任务预留量传进来。
    /// </summary>
    public static class SpaceConcurrencyPlanner
    {
        /// <summary>
        /// 当前还能从队首开始启动几个任务。
        /// </summary>
        /// <param name="availableBytes">可用于本次启动的空间（真实剩余 − 保留余量）。</param>
        /// <param name="reservedBytes">已经在跑的任务预计新增的总字节数。</param>
        /// <param name="pendingSizes">按队列顺序排列的待跑任务预计新增字节数（从大到小）。</param>
        /// <param name="maxParallel">用户设置的最大并发数（空间不足下作为上限，不允许超过）。</param>
        public static int CountLaunchable(
            long availableBytes,
            long reservedBytes,
            IReadOnlyList<long>? pendingSizes,
            int maxParallel)
        {
            if (availableBytes <= 0 || pendingSizes == null || pendingSizes.Count == 0 || maxParallel <= 0)
            {
                return 0;
            }

            long used = Math.Max(0, reservedBytes);
            int count = 0;

            foreach (long size in pendingSizes)
            {
                if (count >= maxParallel)
                {
                    break;
                }

                if (size < 0 || used + size > availableBytes)
                {
                    break;
                }

                used += size;
                count++;
            }

            return count;
        }
    }
}
