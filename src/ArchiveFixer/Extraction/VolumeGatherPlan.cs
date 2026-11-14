using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>收拢计划（纯数据；⛔ 这里不动盘上任何东西）。</summary>
public sealed record VolumeGatherPlan(
    VolumeGatherAction Action,
    string Reason,
    IReadOnlyList<string> Pieces)
{
    public static VolumeGatherPlan Leave(string reason)
        => new(VolumeGatherAction.Leave, reason, Array.Empty<string>());
}
