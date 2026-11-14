using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Services
{
    /// <summary>清理**执行**的结论。</summary>
    public sealed class CleanupOutcome
    {
        public CleanupScope Scope { get; init; }

        public DeleteMode Mode { get; init; }

        /// <summary>是否真的执行过删除（未确认 / 没有候选时为 false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        public int SuccessCount { get; init; }

        public int FailureCount { get; init; }

        /// <summary>彻底删除真正释放的字节数。</summary>
        public long FreedBytes { get; init; }

        /// <summary>移入回收站的字节数（未真正释放空间）。</summary>
        public long RecycledBytes { get; init; }

        /// <summary>失败原因清单（"路径：说明"）。</summary>
        public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

        /// <summary>每一步的删除日志（路径 + 理由 + 条目数 + 总大小），调用方原样写进界面/文件日志。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();

        public string Message { get; init; } = string.Empty;

        /// <summary>是不是"一项都没删掉"（含"根本没执行"）—— 界面据此决定用提示还是警告。</summary>
        public bool SucceededNothing => SuccessCount <= 0;
    }
}
