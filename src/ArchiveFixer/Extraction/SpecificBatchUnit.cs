using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 特定解压的排队单位：一个普通包，或一组**标准名**分卷（只按卷名归组，不走进第一/第二大步）。
/// 独立归档即使名字相似也不合并；它们没有卷标记，各自成为一单。
/// </summary>
public sealed class SpecificBatchUnit
{
    public ArchiveTask Entry { get; init; } = null!;

    public IReadOnlyList<ArchiveTask> Followers { get; init; } = Array.Empty<ArchiveTask>();

    public long SourceBytes { get; init; }
}
