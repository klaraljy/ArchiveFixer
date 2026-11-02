using System;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 解压前的唯一落点事实：源包/选择对象/目标根/包名/最终目录。
    ///
    /// <para>阶段 3 起，路径服务只负责把“这次按什么选项解压”喂成这份计划；
    /// 调用方要落点就读这里，⛔ 不再自己拼源包目录、包名和相对子路径。</para>
    ///
    /// <para>旧布尔/旧落点枚举只允许在本类构造时读入并翻译一次；新写入不再回写旧布尔。</para>
    /// </summary>
    public sealed class OutputPlan
    {
        public bool Success => Placement.Success;

        public OutputPlacementError Error => Placement.Error;

        public string Message => Placement.Message;

        public string SourcePath { get; init; } = string.Empty;

        public string SourceRoot { get; init; } = string.Empty;

        public string DestinationRoot => Placement.DestinationRoot;

        public string DestinationDirectory => Placement.DestinationDirectory;

        public string PackageBaseName => Placement.ArchiveBaseName;

        public string PackageFolderName => Placement.PackageFolderName;

        public string RelativeSubPath => Placement.RelativeSubPath;

        public bool SharesDestinationWithOtherPackages => Placement.SharesDestinationWithOtherPackages;

        public OutputPlacementMode PlacementMode { get; init; }

        public SourceSelectionKind SelectionKind { get; init; }

        public string SelectionRoot { get; init; } = string.Empty;

        public bool FlattenIntoSourceFolder { get; init; }

        public string EntryArchivePath { get; init; } = string.Empty;

        public OutputPlacementResult Placement { get; init; } = new();

        /// <summary>
        /// 由落点公式产出一份计划。⛔ 这里不再做第二套路径拼接；公式仍只有
        /// <see cref="OutputPlacement.ResolveDestinationDirectory"/> 一处。
        /// </summary>
        public static OutputPlan Create(
            string? sourcePath,
            OutputPlacementMode mode,
            string? customRoot,
            string? volumeGroupBaseName,
            SourceSelectionKind selectionKind,
            string? selectionRoot,
            bool flattenIntoSourceFolder,
            string? entryArchivePath,
            string? sourceRoot = null)
        {
            OutputPlacementResult placement = OutputPlacement.ResolveDestinationDirectory(
                sourcePath,
                mode,
                customRoot,
                volumeGroupBaseName,
                driveExists: null,
                selectionKind: selectionKind,
                selectionRoot: selectionRoot,
                flattenIntoSourceFolder: flattenIntoSourceFolder,
                entryArchivePath: entryArchivePath);

            return new OutputPlan
            {
                SourcePath = sourcePath ?? string.Empty,
                SourceRoot = sourceRoot ?? string.Empty,
                PlacementMode = mode,
                SelectionKind = selectionKind,
                SelectionRoot = selectionRoot ?? string.Empty,
                FlattenIntoSourceFolder = flattenIntoSourceFolder,
                EntryArchivePath = entryArchivePath ?? string.Empty,
                Placement = placement
            };
        }
    }
}
