using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>一组的定序结论（成立 / 不成立都要说清是哪一档）。</summary>
public sealed class VolumeGroupOrder
{
    public bool Confirmed { get; init; }

    /// <summary>成立时按卷号升序的整组（第 1 卷在第一位）。</summary>
    public IReadOnlyList<VolumeGroupSlot> Slots { get; init; } = Array.Empty<VolumeGroupSlot>();

    public VolumeNumberFail Fail { get; init; }

    /// <summary>给文案用的数字（片数等）。</summary>
    public int Detail { get; init; }

    public int Count => Slots.Count;
}

