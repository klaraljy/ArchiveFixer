using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing
{
    public static class PackingVolumeRule
    {
        /// <summary>1 GiB（分档线）。</summary>
        public const long OneGiB = 1024L * 1024 * 1024;

        /// <summary>小于 1 GiB 的目标卷数。</summary>
        public const int SmallTargetCount = 2;

        /// <summary>大于等于 1 GiB 的目标卷数。</summary>
        public const int LargeTargetCount = 3;

        /// <summary>每卷上限的下限（7z 的 <c>-v</c> 以 MiB 为单位，再小没有意义）。</summary>
        public const long MinVolumeBytes = 1L * 1024 * 1024;

        /// <summary>
        /// 自动算出来的每卷上限的**天花板**（16 GiB）。
        ///
        /// <para>为什么留着天花板：某些文件系统（FAT32）单文件上限是 4 GiB，卷比它更大就写不下去；
        /// 而"内容极大"时（例如 100 GiB）不夹一下会切出一个几十 GiB 的卷。夹到 16 GiB 之后
        /// **卷数会多于目标卷数** —— 这一点由 <see cref="Describe"/> 如实说出来，⛔ 不许假装还是 3 卷。</para>
        /// </summary>
        public const long AutoMaxVolumeBytes = 16L * 1024 * 1024 * 1024;

        /// <summary>目标卷数：&lt;1 GiB → 2；≥1 GiB → 3。</summary>
        public static int TargetCount(long contentBytes) =>
            contentBytes < OneGiB ? SmallTargetCount : LargeTargetCount;

        /// <summary>
        /// 按目标卷数算出每卷上限（字节，整 MiB，已夹在 [1 MiB, 16 GiB] 内）。
        /// 内容为 0（空文件夹）时给下限 1 MiB —— 那种请求在上游就会被拒。
        /// </summary>
        public static long ComputeVolumeSize(long contentBytes, int targetCount)
        {
            if (contentBytes <= 0)
            {
                return MinVolumeBytes;
            }

            if (targetCount < 1)
            {
                targetCount = 1;
            }

            const long MiB = 1024 * 1024;

            // 先除再补，避免 content + count - 1 在极端值上溢出（与 ComputeVolumeCount 同一手法）。
            long quotient = contentBytes / targetCount;
            long remainder = contentBytes % targetCount;
            long raw = remainder == 0 ? quotient : TaskSpaceEstimate.SaturatingSum(quotient, 1);

            // 向上取整到整 MiB。
            long mib = raw / MiB + (raw % MiB == 0 ? 0 : 1);

            if (mib < 1)
            {
                mib = 1;
            }

            long bytes = mib * MiB;

            if (bytes > AutoMaxVolumeBytes)
            {
                return AutoMaxVolumeBytes;
            }

            return bytes < MinVolumeBytes ? MinVolumeBytes : bytes;
        }

        /// <summary>按算出来的每卷上限，**预计**切出几卷（内容为 0 时 0）。</summary>
        public static long PlannedCount(long contentBytes, long volumeSizeBytes) =>
            PackingPlan.ComputeVolumeCount(contentBytes, volumeSizeBytes);

        /// <summary>一句话说清"目标几卷、每卷上限多少、预计切出几卷"（日志 / 摘要用，⛔ 不撒谎）。</summary>
        public static string Describe(long contentBytes, long volumeSizeBytes)
        {
            int target = TargetCount(contentBytes);
            long planned = PlannedCount(contentBytes, volumeSizeBytes);

            return $"按 {target} 卷切（内容 {TaskSpaceEstimate.FormatSize(contentBytes)}，"
                 + $"每卷上限 {PackingPlan.FormatVolumeSize(volumeSizeBytes)}）—— 预计 {planned} 卷；"
                 + "实际按压缩后的体积算，前面每卷装满、最后一卷是零头。";
        }
    }

    /// <summary>选中的东西 → 真正要打包的那个文件夹（单文件会先在旁边生成同名文件夹）。</summary>
}
