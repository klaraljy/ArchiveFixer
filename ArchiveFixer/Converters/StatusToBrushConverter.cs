using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ArchiveFixer.Models;

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
                StatusText.Recognized or
                StatusText.ExtensionNormal or
                StatusText.RenameSuccess or
                StatusText.TestPassed or
                StatusText.ExtractSuccess or
                StatusText.PasswordCorrect or
                StatusText.Success or
                StatusText.ProgressCompleted or
                "INFO";
        }

        private static bool IsErrorStatus(string status)
        {
            return status is
                StatusText.RenameFailed or
                StatusText.TestFailed or
                StatusText.ExtractFailed or
                StatusText.WrongPassword or
                StatusText.Corrupted or
                StatusText.AccessDenied or
                StatusText.OutputConflict or
                StatusText.VolumeMissing or
                StatusText.PathTooLong or
                StatusText.UnknownError or
                StatusText.SevenZipMissing or
                StatusText.RenameCannot or
                "ERROR";
        }

        private static bool IsWarningStatus(string status)
        {
            return status is
                StatusText.UnknownFormat or
                StatusText.ExtensionMissing or
                StatusText.ExtensionMismatch or
                StatusText.ExtensionMultiFake or
                StatusText.TargetExists or
                StatusText.WillAutoRename or
                StatusText.PasswordNeed or
                "WARN";
        }

        private static bool IsInfoStatus(string status)
        {
            return status is
                StatusText.WaitingScan or
                StatusText.Scanning or
                StatusText.WaitingRename or
                StatusText.WaitingTest or
                StatusText.Testing or
                StatusText.WaitingExtract or
                StatusText.Extracting or
                StatusText.ProgressProcessing or
                StatusText.OpScan or
                StatusText.OpRename or
                StatusText.OpTest or
                StatusText.OpExtract or
                // 分卷后缀是"正常的一类"，不是伪装、也不是漏写后缀：给中性色，别引导用户去改它。
                StatusText.ExtensionVolume;
        }

        private static bool IsDisabledStatus(string status)
        {
            return status is
                StatusText.Skipped or
                StatusText.Cancelled or
                StatusText.RenameWillSkip or
                StatusText.NotArchive or
                StatusText.NotChecked or
                StatusText.OpWaiting;
        }
    }
}
