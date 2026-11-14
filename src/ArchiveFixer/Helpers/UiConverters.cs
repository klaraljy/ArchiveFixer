using System;
using System.Globalization;
using System.Windows.Data;

namespace ArchiveFixer.Helpers;

/// <summary>
/// 单选按钮 ↔ 枚举属性。
///
/// 用途：一组"四选一"（例如输出位置），每个 RadioButton 绑同一个枚举属性、
/// 用 <c>ConverterParameter</c> 指明自己代表哪一项。
///
/// 为什么不用四个布尔：模式必须是**互斥**的，四个布尔能表达出 16 种组合，
/// 其中 12 种是非法的（"既是 A 又是 B"）。用一个枚举从数据上就不可能出错。
/// </summary>
public sealed class EnumOptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 取消勾选（被同组别的项顶掉）时不写回：写回会把刚选中的那一项又改掉。
        if (value is not bool isChecked || !isChecked || parameter == null)
        {
            return Binding.DoNothing;
        }

        /*
         * ⚠ 目标属性是**字符串**时必须原样返回参数（2026-09-25 第 34 条的真机故障）。
         *
         * 真机现场：③页「2 删除操作」三档绑的是 `SettingsEditor.RestHandling`（string），
         * 而老实现到这里只认枚举 —— `targetType` 是 string → `IsEnum` 为 false → 返回
         * `Binding.DoNothing` → **点单选框有黑点、但设置一个字节都没写**。
         * 用户看到的是：选「彻底删除」→ 点保存 → 一键处理的弹窗里还是旧档位
         * （"我之前的选项完全没有用"），而任何一次绑定重新求值又会把黑点弹回旧值。
         * 字符串档位（`RestHandling` 这类）由属性 setter 自己归一化，这里不需要认识它的词表。
         */
        Type enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (enumType == typeof(string))
        {
            return parameter.ToString()!;
        }

        if (!enumType.IsEnum)
        {
            return Binding.DoNothing;
        }

        try
        {
            return Enum.Parse(enumType, parameter.ToString()!, ignoreCase: false);
        }
        catch (ArgumentException)
        {
            return Binding.DoNothing;
        }
    }
}
