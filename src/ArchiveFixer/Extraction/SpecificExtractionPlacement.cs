using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 特定解压的容器根（成品内容名再落到这个根下面）。
///
/// <para>它只回答“根在哪”，不猜“最后一层内容叫什么”——后者由
/// <see cref="SpecificOutputNaming.Resolve"/> 在真正解完一层后决定。</para>
///
/// <para>规则与普通落点共用同一套事实：未指定位置 = 源包父目录；指定位置 + 选文件夹
/// = 自定义根 \ 选中文件夹名 \ 相对子路径；指定位置 + 选文件 = 自定义根。
/// 相对子路径和文件夹名都走 <see cref="OutputPlacement"/> 的既有出口，⛔ 不在特定解压里另拼一套。</para>
/// </summary>
internal static class SpecificExtractionPlacement
{
    internal static OutputPlacementResult ResolveContainerRoot(
        string? sourceArchivePath,
        OutputPlacementMode mode,
        string? customRoot,
        SourceSelectionKind selectionKind,
        string? selectionRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceArchivePath))
        {
            return Failure(OutputPlacementError.EmptySourcePath, "源包路径为空，无法判断特定解压容器根");
        }

        if (!OutputPlacement.UsesCustomRoot(mode))
        {
            string sourceRoot = SpecificOutputNaming.ResolveOutputRoot(
                sourceArchivePath,
                selectionKind,
                selectionRoot);

            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                return Failure(OutputPlacementError.SourceDirectoryUnavailable, "源包路径里没有目录部分，无法判断特定解压容器根");
            }

            return new OutputPlacementResult
            {
                Success = true,
                DestinationRoot = sourceRoot,
                DestinationDirectory = sourceRoot,
                Message = "特定解压容器根：" + sourceRoot
            };
        }

        OutputPlacementResult root = OutputPlacement.ResolveDestinationRoot(sourceArchivePath, mode, customRoot);

        if (!root.Success)
        {
            return root;
        }

        string container = root.DestinationRoot;
        string relativeSubPath = string.Empty;
        string selectedFolderName = OutputPlacement.ResolveSelectedFolderName(selectionKind, selectionRoot);
        bool usesSelectedFolderName = false;

        if (selectedFolderName.Length > 0
            && selectionKind == SourceSelectionKind.Folder
            && !string.IsNullOrWhiteSpace(selectionRoot))
        {
            relativeSubPath = OutputPlacement.ResolveRelativeSubPath(selectionRoot!, sourceArchivePath);
            container = SafePathHelper.Combine(container, selectedFolderName);

            if (!string.IsNullOrWhiteSpace(relativeSubPath))
            {
                container = SafePathHelper.Combine(container, relativeSubPath);
            }

            usesSelectedFolderName = true;
        }

        if (string.IsNullOrWhiteSpace(container))
        {
            return Failure(OutputPlacementError.InvalidDestinationPath, "特定解压容器根拼不出来：" + root.DestinationRoot);
        }

        return new OutputPlacementResult
        {
            Success = true,
            DestinationRoot = container,
            DestinationDirectory = container,
            RelativeSubPath = relativeSubPath,
            UsesSelectedFolderName = usesSelectedFolderName,
            Message = "特定解压容器根：" + container
        };
    }

    private static OutputPlacementResult Failure(OutputPlacementError error, string message) => new()
    {
        Success = false,
        Error = error,
        Message = message
    };
}
