using System;
using System.Collections.Generic;

namespace ArchiveFixer.Packing;

/// <summary>产物校验的结论。</summary>
public sealed class PackingVerification
{
    public bool Ok { get; init; }

    /// <summary>给人看的一句话（对上了写"数出几个、分别是哪一层"，不对写缺什么 / 多什么）。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>逐条问题（为空 = 通过）。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}
