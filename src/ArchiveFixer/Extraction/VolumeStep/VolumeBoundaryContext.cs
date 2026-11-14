using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction.VolumeStep
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

        /// <summary>试开进度（引擎 -bsp1 百分比）；为空则上层看不到进度（真机 2026-11-10 的"零日志长窗"）。</summary>
        public IProgress<ArchiveProgress>? Progress { get; init; }

        /// <summary>"很久没有引擎输出"的提示；为空则不提示。</summary>
        public Action<ArchiveStallNotice>? Stalled { get; init; }
    }
}
