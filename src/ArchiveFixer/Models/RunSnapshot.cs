using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 一次一键处理/批量运行的不可变事实快照。
    ///
    /// <para>批次开头拍一次；中途用户改设置、改勾选、改空间不足开关，都不许影响这一批。
    /// 新发现的续解任务只加入本次运行，仍读这一份快照。</para>
    /// </summary>
    public sealed class RunSnapshot
    {
        public Guid RunId { get; init; } = Guid.NewGuid();

        public DateTime StartedAt { get; init; } = DateTime.Now;

        /// <summary>开跑时的目标清单（副本；之后任务列表变化不影响这里）。</summary>
        public IReadOnlyList<ArchiveTask> Targets { get; init; } = Array.Empty<ArchiveTask>();

        /// <summary>本次选项（落点/源包/其余物等），不再回读设置。</summary>
        public OneClickRunOptions Options { get; init; } = new();

        public bool SpaceTightMode { get; init; }

        public bool SpaceTightKeepSource { get; init; }

        public bool UseSpecificExtraction { get; init; }

        public string SelectedOutputDirectory { get; init; } = string.Empty;

        /// <summary>开跑时可见的密码候选（只读副本；无密码为空串，不是 null）。</summary>
        public IReadOnlyList<string?> PasswordCandidates { get; init; } = Array.Empty<string?>();

        public static RunSnapshot Capture(
            IEnumerable<ArchiveTask>? targets,
            OneClickRunOptions options,
            bool spaceTightMode,
            bool spaceTightKeepSource,
            bool useSpecificExtraction,
            string? selectedOutputDirectory,
            IEnumerable<string?>? passwordCandidates = null)
        {
            return new RunSnapshot
            {
                Targets = (targets ?? Array.Empty<ArchiveTask>())
                    .Where(task => task != null)
                    .ToArray(),
                Options = options ?? new OneClickRunOptions(),
                SpaceTightMode = spaceTightMode,
                SpaceTightKeepSource = spaceTightKeepSource,
                UseSpecificExtraction = useSpecificExtraction,
                SelectedOutputDirectory = selectedOutputDirectory ?? string.Empty,
                PasswordCandidates = (passwordCandidates ?? Array.Empty<string?>())
                    .Select(value => value ?? string.Empty)
                    .ToArray()
            };
        }
    }
}
