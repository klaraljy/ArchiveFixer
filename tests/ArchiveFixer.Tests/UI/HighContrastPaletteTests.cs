using System.Windows;
using System.Windows.Media;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    [Collection("ArchiveFixerGlobalState")]
    public sealed class HighContrastPaletteTests
    {
        [Fact]
        public void 高对比度替换为系统窗口和文字色()
        {
            var resources = new ResourceDictionary();
            resources["WindowBackgroundBrush"] = Brushes.White;
            resources["TextPrimaryBrush"] = Brushes.Black;
            resources["PanelBorderBrush"] = Brushes.Gray;

            HighContrastPalette.Apply(resources);

            Assert.Equal(SystemColors.WindowBrush.Color, ((SolidColorBrush)resources["WindowBackgroundBrush"]).Color);
            Assert.Equal(SystemColors.WindowTextBrush.Color, ((SolidColorBrush)resources["TextPrimaryBrush"]).Color);
            Assert.Equal(SystemColors.WindowFrameBrush.Color, ((SolidColorBrush)resources["PanelBorderBrush"]).Color);
        }
    }
}
