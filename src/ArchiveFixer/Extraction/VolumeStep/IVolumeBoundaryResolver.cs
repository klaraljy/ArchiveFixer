using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **边界 resolver 的输入**（共用骨架）：账本、基名、源层、临时入口根、密码候选、日志。
    ///
    /// <para>⛔ 这里不塞"7z 特有的字段"：族专属证据留在账本项里，由各族 resolver 自己解释。</para>
    /// </summary>
    public sealed class VolumeBoundaryContext
    {
        public FirstStepFileLedger Ledger { get; init; } = new();

        public string BaseName { get; init; } = string.Empty;

        public string OriginalSourceLayer { get; init; } = string.Empty;

        /// <summary>本单目标工作区根（可空；没有它就不做任何试开）。</summary>
        public string? WorkspaceRoot { get; init; }

        /// <summary>试开用的引擎（可空；没有它就不做任何试开）。</summary>
        public IArchiveEngine? Engine { get; init; }

        public IReadOnlyList<string> PasswordCandidates { get; init; } = Array.Empty<string>();

        /// <summary>日志回调（级别, 文案）；为空则不写。</summary>
        public Action<string, string>? Log { get; init; }
    }

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
