using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 特定解压的落点预告：只预告容器根，不伪造解压前还未知的最后一层内容名。
/// </summary>
internal static class SpecificDestinationPreview
{
    internal static string Describe(IReadOnlyList<ArchiveTask> targets, OneClickRunOptions options)
    {
        if (targets == null || targets.Count == 0)
        {
            return string.Empty;
        }

        ArchiveTask first = targets[0];

        OutputPlacementResult placement = SpecificExtractionPlacement.ResolveContainerRoot(
            first.CurrentPath,
            options.PlacementMode,
            options.CustomRoot,
            first.SourceSelectionKind,
            first.SourceSelectionRoot);

        if (!placement.Success || string.IsNullOrWhiteSpace(placement.DestinationDirectory))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                StatusText.OneClickConfirmDestinationUnknownFormat,
                placement.Message);
        }

        string head = placement.DestinationDirectory.TrimEnd('\\', '/');
        string hint = targets.Count > 1
            ? "\\（内容名按最后一层规则；其余任务按各自相对子路径）"
            : "\\（内容名按最后一层规则）";

        return head + hint;
    }
}
