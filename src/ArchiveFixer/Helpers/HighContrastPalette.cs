using System.Windows;
using System.Windows.Media;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// Windows 高对比度主题下用系统色替换固定色板。
    /// 只覆盖“背景/文字/边框/强调/状态”这些关键资源；不影响布局。
    /// </summary>
    internal static class HighContrastPalette
    {
        internal static void Apply(ResourceDictionary resources)
        {
            if (resources == null)
            {
                return;
            }

            Brush window = SystemColors.WindowBrush;
            Brush text = SystemColors.WindowTextBrush;
            Brush frame = SystemColors.WindowFrameBrush;
            Brush highlight = SystemColors.HighlightBrush;
            Brush highlightText = SystemColors.HighlightTextBrush;
            Brush grayText = SystemColors.GrayTextBrush;

            Set(resources, "WindowBackgroundBrush", window);
            Set(resources, "PanelBrush", window);
            Set(resources, "PanelAltBrush", window);
            Set(resources, "PanelSunkenBrush", window);
            Set(resources, "GroupHeaderBackgroundBrush", window);

            Set(resources, "PanelBorderBrush", frame);
            Set(resources, "PanelBorderStrongBrush", frame);
            Set(resources, "DividerBrush", frame);

            Set(resources, "TextPrimaryBrush", text);
            Set(resources, "TextSecondaryBrush", text);
            Set(resources, "TextMutedBrush", text);
            Set(resources, "TextHintBrush", grayText);
            Set(resources, "TextOnAccentBrush", highlightText);

            Set(resources, "PrimaryBrush", highlight);
            Set(resources, "PrimaryDarkBrush", highlight);
            Set(resources, "PrimarySoftBrush", window);
            Set(resources, "PrimarySoftHoverBrush", window);
            Set(resources, "PrimarySoftPressedBrush", window);

            Set(resources, "DangerBrush", text);
            Set(resources, "DangerDarkBrush", text);
            Set(resources, "DangerSoftBrush", window);
            Set(resources, "DangerBorderBrush", frame);
            Set(resources, "DangerTextBrush", text);

            Set(resources, "WarningBrush", text);
            Set(resources, "WarningSoftBrush", window);
            Set(resources, "WarningBorderBrush", frame);
            Set(resources, "WarningTextBrush", text);

            Set(resources, "SuccessBrush", text);
            Set(resources, "SuccessSoftBrush", window);
            Set(resources, "SuccessBorderBrush", frame);
            Set(resources, "SuccessTextBrush", text);

            Set(resources, "InfoSoftBrush", window);
            Set(resources, "InfoBorderBrush", frame);
            Set(resources, "InfoTextBrush", text);

            Set(resources, "SummarySuccessBrush", highlight);
            Set(resources, "SummaryPartialBrush", highlight);
            Set(resources, "SummaryFailedBrush", highlight);
            Set(resources, "SummaryBorderBrush", frame);

            Set(resources, "RowHoverBrush", highlight);
            Set(resources, "RowSelectedBrush", highlight);
            Set(resources, "DisabledBackgroundBrush", window);
            Set(resources, "DisabledTextBrush", grayText);
            Set(resources, "OverlayHoverBrush", Brushes.Transparent);
            Set(resources, "OverlayPressedBrush", Brushes.Transparent);
        }

        private static void Set(ResourceDictionary resources, string key, Brush brush)
        {
            if (brush.CanFreeze && !brush.IsFrozen)
            {
                brush.Freeze();
            }

            resources[key] = brush;
        }
    }
}
