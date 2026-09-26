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
            /*
             * ⚠ 这里必须是 string?（可空）：parameter?.ToString() 真的可能是 null。
             * 以前写成不可空的 string，编译期就会报 CS8600（"将 null 文本转换到不可为 null 类型"），
             * 而这条警告一直挂在 README 的「已知限制」里 —— 行为虽然没错（下面立刻兜底），
             * 但它让"0 警告"这个更硬的判据永远达不到。
             * 下面的 IsNullOrEmpty 判定带 [NotNullWhen(false)]，所以过了那个 if 之后这里就是非空的。
             */
            string? emptyText = parameter?.ToString();

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
            // 同上：parameter?.ToString() 可能为 null，先按可空接住再兜底（否则是 CS8600）。
            string? emptyText = parameter?.ToString();

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
