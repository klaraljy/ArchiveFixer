using System;
using System.Globalization;
using System.Windows.Data;

namespace ArchiveFixer.Helpers
{
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
