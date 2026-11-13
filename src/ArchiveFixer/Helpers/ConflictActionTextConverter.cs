using System;
using System.Globalization;
using System.Windows.Data;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 改名冲突策略 → 中文显示。
    ///
    /// <c>RenamePreviewItem.ConflictAction</c> 里存的是 <c>AutoRename</c> / <c>Skip</c> 这类
    /// 枚举名（默认 AutoRename）：本程序是中文单语，界面上直接出现英文枚举名不合适，
    /// 而模型不归本次改动管，所以只在显示层翻译一次。未知取值原样显示（不猜）。
    /// </summary>
    public sealed class ConflictActionTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string raw = value?.ToString() ?? string.Empty;

            return raw switch
            {
                "AutoRename" => "自动重命名",
                "Skip" => "跳过",
                "Overwrite" => "覆盖",
                "Ask" => "询问",
                _ => raw
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("冲突处理列是只读的。");
        }
    }
}
