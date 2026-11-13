using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{    /// <summary>源包搬运的结论。</summary>
    public sealed class SourcePackageMoveResult
    {
        /// <summary>是否真的动过手（没有可搬的、被跳过时为 false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        public int MovedCount { get; init; }

        /// <summary>搬失败的个数（**源包原地不动**）。</summary>
        public int FailedCount { get; init; }

        public int SkippedCount { get; init; }

        /// <summary>其中几个走了"跨盘：复制成功后再删原件"这条路。</summary>
        public int CrossVolumeCount { get; init; }

        /// <summary>真的搬走的字节数。</summary>
        public long MovedBytes { get; init; }

        /// <summary>成功的搬运（旧 → 新）。任务对象要靠它更新 CurrentPath 与分卷清单。</summary>
        public IReadOnlyList<SourcePackageMove> Moved { get; init; } = Array.Empty<SourcePackageMove>();

        public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

        /// <summary>逐条搬运日志（含从哪到哪 + 总大小），调用方原样写进界面/文件日志。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();

        public string Message { get; init; } = string.Empty;
    }

}
