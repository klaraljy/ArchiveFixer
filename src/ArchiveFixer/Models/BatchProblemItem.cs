namespace ArchiveFixer.Models;

/// <summary>诊断清单里被**点到名**的一个包。</summary>
public sealed class BatchProblemItem
{
    /// <summary>只写**文件名**（隐私红线 §8：名字里不许带用户的目录）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>括号里那句补充（分卷缺哪几个 / 空间差多少）；空 = 没有补充。</summary>
    public string Detail { get; init; } = string.Empty;
}
