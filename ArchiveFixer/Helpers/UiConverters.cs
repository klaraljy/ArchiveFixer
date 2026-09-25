using System;
using System.Globalization;
using System.Windows.Data;

namespace ArchiveFixer.Helpers
{
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

    /// <summary>
    /// 输出位置摘要：两个布尔（解压到原目录 / 保留同名文件夹）+ 自定义路径 → 一句人话。
    ///
    /// 为什么需要它：主窗口只有 <c>Settings</c> 与 <c>SelectedOutputDirectory</c> 可以绑定
    /// （主 ViewModel 不归本改动管），而"当前解压到哪"以前要用户自己把两个开关在脑子里
    /// 组合出来 —— 这正是用户抱怨"根本看不懂输出的哪"的来源。这里把组合结果直接写出来。
    /// </summary>
    public sealed class OutputPlacementSummaryConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool extractToSourceDirectory = ToBool(values, 0);
            bool keepArchiveNameFolder = ToBool(values, 1);
            string customRoot = values.Length > 2 ? values[2]?.ToString() ?? string.Empty : string.Empty;

            return Describe(extractToSourceDirectory, keepArchiveNameFolder, customRoot);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("输出位置摘要是只读的。");
        }

        /// <summary>
        /// 两档各写一句：先给"落在哪"，再给一个具体例子。
        ///
        /// <para>
        /// 用户 2026-09-24 第 13 条之后只剩这两档，"摊平"那两句已经删掉
        /// （<paramref name="keepArchiveNameFolder"/> 只是为了不改旧绑定而留着，不再参与判断）。
        /// </para>
        /// </summary>
        public static string Describe(bool extractToSourceDirectory, bool keepArchiveNameFolder, string customRoot)
        {
            _ = keepArchiveNameFolder;

            if (extractToSourceDirectory)
            {
                return "压缩包同目录 · 以压缩包名命名的子文件夹（111\\222.rar → 111\\222\\内容物）";
            }

            if (string.IsNullOrWhiteSpace(customRoot))
            {
                return "指定位置（尚未选择）· 暂按压缩包同目录处理";
            }

            string root = customRoot.TrimEnd('\\', '/');

            // 占位符写「包名」不写数字（用户 2026-09-25 第 36 条）：这一句会进日志与失败清单，
            // 里面出现一个真实路径拼一个假目录名（旧的 `\222\`）会被当成"程序要往那儿写"。
            return $"{root} · 以压缩包名命名的子文件夹（{root}\\包名\\内容物；选中文件夹时用该文件夹的名字）";
        }

        private static bool ToBool(object[] values, int index)
        {
            if (values == null || index >= values.Length || values[index] == null)
            {
                return false;
            }

            if (values[index] is bool boolValue)
            {
                return boolValue;
            }

            return bool.TryParse(values[index].ToString(), out bool parsed) && parsed;
        }
    }
}
