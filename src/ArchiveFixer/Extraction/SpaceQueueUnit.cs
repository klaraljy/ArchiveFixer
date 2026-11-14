using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 空间不足排队单位：一个单一完整包，或一整组分卷。
    /// 排序键 = 这个单位的**源包总体积**（分卷按整组相加），不是解压后净占用。
    /// </summary>
    public sealed class SpaceQueueUnit
    {
        public string Key { get; init; } = string.Empty;

        public ArchiveTask Entry { get; init; } = null!;

        public IReadOnlyList<ArchiveTask> Tasks { get; init; } = Array.Empty<ArchiveTask>();

        public long SourceBytes { get; init; }
    }
}
