using System;

namespace ArchiveFixer.Models;

/// <summary>
/// 落点模式的纯函数规则（从 Extraction/OutputPlacement 搬出的真实现）：旧值迁移、是否指定位置、
/// 旧布尔迁移。⛔ 不碰文件系统、不读配置，只做机器可判的换算；唯一来源仍是这里。
/// </summary>
internal static class OutputPlacementRules
{
    /// <summary>是不是"以自定义根为目标"的模式（先过一遍旧值迁移）。</summary>
    internal static bool UsesCustomRoot(OutputPlacementMode mode)
    {
        return NormalizeLegacyMode(mode) == OutputPlacementMode.CustomRootPerArchive;
    }

    /// <summary>旧枚举值 / 旧枚举名 → 现在这两档（旧值 1/3 的迁移唯一实现处）。</summary>
    internal static OutputPlacementMode NormalizeLegacyMode(OutputPlacementMode mode)
    {
        return mode switch
        {
            OutputPlacementMode.PerArchiveSubfolder => OutputPlacementMode.PerArchiveSubfolder,
            OutputPlacementMode.CustomRootPerArchive => OutputPlacementMode.CustomRootPerArchive,

            // 旧值 1 = SourceDirectoryFlat（已删除）→ 回到"建同名子文件夹"。
            (OutputPlacementMode)1 => OutputPlacementMode.PerArchiveSubfolder,

            // 旧值 3 = CustomRootFlat（已删除）→ 保留"指定位置"，但补上同名子文件夹。
            (OutputPlacementMode)3 => OutputPlacementMode.CustomRootPerArchive,

            _ => OutputPlacementMode.PerArchiveSubfolder
        };
    }

    /// <summary>旧枚举名（字符串）→ 现在这两档；空/非法/认不出一律回落默认档，不抛异常。</summary>
    internal static OutputPlacementMode ParsePlacementMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OutputPlacementMode.PerArchiveSubfolder;
        }

        string trimmed = value.Trim();

        if (string.Equals(trimmed, "SourceDirectoryFlat", StringComparison.OrdinalIgnoreCase))
        {
            return OutputPlacementMode.PerArchiveSubfolder;
        }

        if (string.Equals(trimmed, "CustomRootFlat", StringComparison.OrdinalIgnoreCase))
        {
            return OutputPlacementMode.CustomRootPerArchive;
        }

        if (Enum.TryParse(trimmed, ignoreCase: true, out OutputPlacementMode parsed) && Enum.IsDefined(parsed))
        {
            return NormalizeLegacyMode(parsed);
        }

        if (int.TryParse(trimmed, out int numeric))
        {
            return NormalizeLegacyMode((OutputPlacementMode)numeric);
        }

        return OutputPlacementMode.PerArchiveSubfolder;
    }

    /// <summary>旧的两个布尔 + 自定义根 → 新枚举（旧配置迁移唯一实现处；空根一律源目录家族）。</summary>
    internal static OutputPlacementMode FromLegacyFlags(
        bool extractToOriginalDirectory,
        bool keepArchiveNameFolder,
        string? customRoot = null)
    {
        _ = keepArchiveNameFolder;

        bool hasCustomRoot = !string.IsNullOrWhiteSpace(customRoot);

        return !extractToOriginalDirectory && hasCustomRoot
            ? OutputPlacementMode.CustomRootPerArchive
            : OutputPlacementMode.PerArchiveSubfolder;
    }
}

