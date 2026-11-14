using System;
using System.Globalization;
using System.Windows.Data;

namespace ArchiveFixer.Helpers;

/// <summary>
/// 布尔 → 固定文案（文案由 <c>ConverterParameter</c> 给，为 true 时返回它，否则返回空串）。
///
/// <para><b>为什么不是"在 ViewModel 里再存一份中文字面量"</b>：AGENTS.md §7 要求界面文案只能有
/// 一个来源（<c>StatusText</c>），而这个标记的文案写在 XAML 里最自然（它就是个列上的小标签）。
/// 于是让 XAML 从常量取词、由转换器判断显示 —— 词的来源仍然只有一处。</para>
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool boolValue && boolValue;

        return flag ? parameter?.ToString() ?? string.Empty : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 只用于显示（DataGrid 里那一格旁边还有真正可编辑的备注框），不往回写。
        throw new NotSupportedException("这个标记是只读的。");
    }
}
