using ArchiveFixer.Models;
using System.Collections.Generic;

namespace ArchiveFixer.Services;

/// <summary>定稿这一步要问宿主的事（设置、路径服务、组落点层记忆、源包内解压档、组片账本、自带层占位判据）。</summary>
internal interface IPlacementStepHost
{
    AppSettings Settings { get; }

    PathService Paths { get; }

    /// <summary>本次是不是"解压到源包所在文件夹"（它等于"不套包名那一层"）。</summary>
    bool ExtractIntoSourceFolderThisRun { get; }

    /// <summary>本次是不是手动单层「只解压」：引擎这一层写出来的都算内容物，不算其余物。</summary>
    bool ManualSingleLayerThisRun { get; }

    /// <summary>接片那一档算出来的"这一组拼到哪一层"（没记过 ⇒ false）。</summary>
    bool TryResolveGroupLayer(string baseName, out string layer);

    /// <summary>本批已经认出来的组基名（定稿时据此放行那些组的片）。</summary>
    IReadOnlyList<string> SnapshotGroupPieceProducerBaseNames();

    /// <summary>是否把抠出来的内嵌归档搬进其余物（历史开关，当前恒为关）。</summary>
    bool MoveCarvedArtifactIntoProcessDirectory { get; }

    /// <summary>双面文件抠出来的那一份的路径（唯一实现：协调器的嵌入式直读那一档）。</summary>
    string BuildEmbeddedArchivePath(ArchiveTask task);

    /// <summary>自带层目录是不是已经被包文件自己占着（唯一实现：OneClickCoordinator）。</summary>
    bool IsOwnLayerOccupiedByPackageFile(string directory, string? ownLayerName, string? innerPackagePath);
}
