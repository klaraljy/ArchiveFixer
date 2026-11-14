using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;
/// <summary>一次归置规划请求。</summary>
public sealed class ArtifactPlacementRequest
{
    /// <summary>目标目录 <c>D</c>（内容物目录）；其余物目录 = <c>parent(D)\其余物</c>（与内容物文件夹同级）。</summary>
    public string TargetDirectory { get; init; } = string.Empty;

    /// <summary>相对路径的基准目录（例：本层产物目录）。留空时按文件名归置。</summary>
    public string BaseDirectory { get; init; } = string.Empty;

    /// <summary>
    /// 归档基名（决策 D-10）：**多个包共用同一个目标目录**时，其余物下按它再分一层。
    /// 留空 = 不分（退回"集中一处"，与旧行为一致）。
    /// </summary>
    public string ArchiveBaseName { get; init; } = string.Empty;

    /// <summary>
    /// 目标目录是不是"多个包共用的根"（`OutputPlacementResult.SharesDestinationWithOtherPackages`：
    /// 默认 false：包本来就有自己目录的模式**不再多套一层**（决策 D-10）。
    /// </summary>
    public bool SharedRoot { get; init; }

    public IReadOnlyList<ArtifactPlacementItem> Items { get; init; } = Array.Empty<ArtifactPlacementItem>();

    /// <summary>目标占用探测；不传时按真实文件系统判断（"目标已存在同名就不覆盖"靠它）。</summary>
    public IArtifactTargetProbe? TargetProbe { get; init; }
}
