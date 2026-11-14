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
/// <summary>试开请求。</summary>
public sealed class VolumeTrialRequest
{
    /// <summary>第 1 卷（按推定的顺序）。</summary>
    public string FirstVolumePath { get; init; } = string.Empty;

    /// <summary>后续卷的几种候选排列（每一组都不含第 1 卷）。</summary>
    public IReadOnlyList<IReadOnlyList<VolumeCandidate>> Orderings { get; init; } =
        Array.Empty<IReadOnlyList<VolumeCandidate>>();

    /// <summary>临时物优先落点（空 = 由试开器自己定）。</summary>
    public string PreferredWorkRootDirectory { get; init; } = string.Empty;
}

/// <summary>试开端口（Detection 不引用 Extraction，所以这里只定义端口，实现由调用方注入）。</summary>
public delegate Task<VolumeTrialOutcome> VolumeTrialOpen(
    VolumeTrialRequest request,
    CancellationToken cancellationToken);
