using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction.VolumeStep
{
    /// <summary>
    /// **族专属边界 resolver**（共用骨架的唯一接口）。
    ///
    /// <para>每个族只实现自己的"怎么定序/怎么校验/怎么出标准名"；共享管线只按族调一次，
    /// ⛔ 不在这里写 `if (族 == ...)`。</para>
    /// </summary>
    public interface IVolumeBoundaryResolver
    {
        VolumeNamingFamily Family { get; }

        /// <summary>
        /// 解析同基名的一族候选；返回 null = 这一族没有可处理的候选。
        /// ⛔ 判不出/结构矛盾必须返回一个带状态的对象，不能返回 null 把问题藏掉。
        /// </summary>
        Task<PreparedVolumeGroup?> ResolveAsync(
            VolumeBoundaryContext context,
            CancellationToken cancellationToken = default);
    }
}
