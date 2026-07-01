using System;
using System.Globalization;
using System.Windows.Data;

namespace ArchiveFixer.Converters
{
    /// <summary>
    /// 空字符串转默认文本。
    /// 
    /// 例如：
    /// "" -> "-"
    /// null -> "-"
    /// </summary>
    public class EmptyStringToTextConverter : IValueConverter
    {
        /// <summary>
        /// 默认显示文本。
        /// </summary>
        public string EmptyText { get; set; } = "-";

        public object Convert(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            string emptyText = parameter?.ToString();

            if (string.IsNullOrEmpty(emptyText))
            {
                emptyText = EmptyText;
            }

            if (value == null)
            {
                return emptyText;
            }

            string text = value.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                return emptyText;
            }

            return text;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            string emptyText = parameter?.ToString();

            if (string.IsNullOrEmpty(emptyText))
            {
                emptyText = EmptyText;
            }

            string text = value?.ToString() ?? string.Empty;

            if (text == emptyText)
            {
                return string.Empty;
            }

            return text;
        }
    }
}
