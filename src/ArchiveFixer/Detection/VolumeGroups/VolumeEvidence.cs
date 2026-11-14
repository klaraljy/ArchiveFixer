using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Detection;
/// <summary>一条证据（证据表里的一行）。</summary>
public sealed class VolumeEvidence
{
    /// <summary>哪一条证据。</summary>
    public VolumeEvidenceKind Kind { get; init; }

    /// <summary>权重。</summary>
    public VolumeEvidenceWeight Weight { get; init; }

    /// <summary>这次的结论。</summary>
    public VolumeEvidenceOutcome Outcome { get; init; }

    /// <summary>证据名（一行，给用户看的）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>怎么取的 + 取到了什么（尽量带数字，AGENTS.md §9.5「排错先量化」）。</summary>
    public string Detail { get; init; } = string.Empty;
}
