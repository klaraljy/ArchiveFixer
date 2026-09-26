using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ArchiveFixer.Converters
{
    /// <summary>
    /// bool 转 Visibility。
    /// 
    /// 可通过 ConverterParameter 控制：
    /// Invert：取反。
    /// Hidden：false 时返回 Hidden，否则默认 Collapsed。
    /// InvertHidden：取反且 false 时返回 Hidden。
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; } = false;

        public bool UseHidden { get; set; } = false;

        public object Convert(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            bool boolValue = ToBool(value);

            string param = parameter?.ToString() ?? string.Empty;

            bool invert = Invert ||
                          param.Equals("Invert", StringComparison.OrdinalIgnoreCase) ||
                          param.Equals("InvertHidden", StringComparison.OrdinalIgnoreCase);

            bool useHidden = UseHidden ||
                             param.Equals("Hidden", StringComparison.OrdinalIgnoreCase) ||
                             param.Equals("InvertHidden", StringComparison.OrdinalIgnoreCase);

            if (invert)
            {
                boolValue = !boolValue;
            }

            if (boolValue)
            {
                return Visibility.Visible;
            }

            return useHidden ? Visibility.Hidden : Visibility.Collapsed;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            if (value is not Visibility visibility)
            {
                return false;
            }

            bool result = visibility == Visibility.Visible;

            string param = parameter?.ToString() ?? string.Empty;

            bool invert = Invert ||
                          param.Equals("Invert", StringComparison.OrdinalIgnoreCase) ||
                          param.Equals("InvertHidden", StringComparison.OrdinalIgnoreCase);

            return invert ? !result : result;
        }

        private static bool ToBool(object value)
        {
            if (value == null)
            {
                return false;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is bool?)
            {
                return ((bool?)value).GetValueOrDefault();
            }

            if (value is int intValue)
            {
                return intValue != 0;
            }

            if (bool.TryParse(value.ToString(), out bool parsed))
            {
                return parsed;
            }

            return false;
        }
    }
}
