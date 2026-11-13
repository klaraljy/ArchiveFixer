using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>拼装的结果（失败也要能说清为什么 —— 失败**不改变**任务的结论）。</summary>
    public sealed class SplitVolumeAssemblyResult
    {
        public bool Success { get; init; }

        public string Reason { get; init; } = string.Empty;

        /// <summary>拼好之后交给引擎的那个文件（首卷）。</summary>
        public string FirstVolumePath { get; init; } = string.Empty;

        /// <summary>用硬链接接了几个（零字节复制）。</summary>
        public int HardLinkCount { get; init; }

        /// <summary>复制了几个。</summary>
        public int CopyCount { get; init; }

        /// <summary>复制了多少字节（硬链接的不算）。</summary>
        public long CopiedBytes { get; init; }

        /// <summary>日志里那几行（调用方原样写出去，口径只有这一处）。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();
    }
}

