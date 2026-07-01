using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ArchiveFixer.Converters
{
    /// <summary>
    /// 根据任务状态返回颜色。
    /// 用于 DataGrid 状态列、日志级别等显示。
    /// </summary>
    public class StatusToBrushConverter : IValueConverter
    {
        public Brush DefaultBrush { get; set; } = Brushes.Black;

        public Brush SuccessBrush { get; set; } = new SolidColorBrush(Color.FromRgb(46, 125, 50));

        public Brush WarningBrush { get; set; } = new SolidColorBrush(Color.FromRgb(245, 124, 0));

        public Brush ErrorBrush { get; set; } = new SolidColorBrush(Color.FromRgb(198, 40, 40));

        public Brush InfoBrush { get; set; } = new SolidColorBrush(Color.FromRgb(25, 118, 210));

        public Brush DisabledBrush { get; set; } = Brushes.Gray;

        public object Convert(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            string status = value?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(status))
            {
                return DefaultBrush;
            }

            if (IsSuccessStatus(status))
            {
                return SuccessBrush;
            }

            if (IsErrorStatus(status))
            {
                return ErrorBrush;
            }

            if (IsWarningStatus(status))
            {
                return WarningBrush;
            }

            if (IsInfoStatus(status))
            {
                return InfoBrush;
            }

            if (IsDisabledStatus(status))
            {
                return DisabledBrush;
            }

            return DefaultBrush;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            return Binding.DoNothing;
        }

        private static bool IsSuccessStatus(string status)
        {
            return status is
                "已识别" or
                "后缀正常" or
                "改名成功" or
                "测试通过" or
                "解压成功" or
                "密码正确" or
                "成功" or
                "完成" or
                "INFO";
        }

        private static bool IsErrorStatus(string status)
        {
            return status is
                "改名失败" or
                "测试失败" or
                "解压失败" or
                "密码错误" or
                "文件损坏" or
                "权限不足" or
                "输出路径冲突" or
                "分卷缺失" or
                "路径过长" or
                "未知错误" or
                "7z不存在" or
                "无法改名" or
                "ERROR";
        }

        private static bool IsWarningStatus(string status)
        {
            return status is
                "格式未知" or
                "后缀缺失" or
                "后缀不匹配" or
                "多重后缀疑似伪装" or
                "目标已存在" or
                "将自动重命名" or
                "需要密码" or
                "WARN";
        }

        private static bool IsInfoStatus(string status)
        {
            return status is
                "等待扫描" or
                "扫描中" or
                "等待改名" or
                "等待测试" or
                "测试中" or
                "等待解压" or
                "解压中" or
                "处理中" or
                "扫描" or
                "改名" or
                "测试" or
                "解压";
        }

        private static bool IsDisabledStatus(string status)
        {
            return status is
                "已跳过" or
                "已取消" or
                "将跳过" or
                "非压缩包" or
                "未检测" or
                "等待";
        }
    }
}
