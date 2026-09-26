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

            /*
             * 日志三级**先判**（用户 2026-09-26："用不同颜色的字体，就比如说用红色、黄色、黑色字体，
             * 表示危险、警告、正常的操作"）：
             * · ERROR → 红（危险，要处理）；
             * · WARN  → 黄（警告，看一眼）；
             * · INFO  → 黑（正常操作，别抢注意力）。
             *
             * ⚠ 为什么 INFO 要从 IsSuccessStatus 里拿出来：那一组同时服务于**任务状态列**
             * （"已识别 / 解压成功"用绿色是对的），而日志里的 INFO 只是"进行到哪了" ——
             * 一片绿会让"哪一行真出事了"看不出来。所以按**日志级别**这一档单独返回默认色（黑）。
             */
            if (string.Equals(status, "INFO", StringComparison.Ordinal))
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
                StatusText.ProgressCompleted;
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
                // "一个引擎都没找到"与"7z 不存在"分开：文案不同，但都是**这一单拿不到产物**，
                // 所以同用错误色（与 TaskSummaryService 的"解压失败"桶一致，§7 三处同改）。
                StatusText.NoEngineAvailable or
                // 达到密码尝试上限也是"这一单没拿到产物"，和它的统计分桶（解压失败）保持一致用错误色；
                // 与"密码错误"的区别写在状态文字里（上限 = 还没试完，别让用户以为密码本错了）。
                StatusText.PasswordAttemptLimitReached or
                // 磁盘空间不足：连开始都没开始（空间门在解压前就拦下了），但它同样是**这一单拿不到产物**，
                // 用错误色与"解压失败"同桶（§7 三处同改）；具体该怎么办写在 ErrorMessage 的数字与建议里。
                StatusText.DiskSpaceInsufficient or
                // 源文件已变化（不变量 11）：引擎一次都没被调用，但用户手上这份识别结果已经作废、
                // 这一单同样没拿到产物 —— 与"分卷缺失"同一档：**必须先修好源文件才能继续**，
                // 所以给错误色（与它的统计分桶"解压失败"一致，§7 三处同改）。
                StatusText.SourceChanged or
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
                // 部分完成是"要人看一眼"的状态：不是失败（东西解出来了一些），也绝不是成功。
                StatusText.PartiallyCompleted or
                // 文件名已加密同理：包本身可能没问题，只是内容无法判定、需要正确密码 ——
                // 给警告色（与"部分完成"同分桶同色），不要用"文件损坏"的错误色把人引去重下。
                StatusText.EncryptedHeaders or
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
                StatusText.ExtensionVolume or
                // 内嵌归档同理：它是"已识别、且不该改名"的一类（改成 .zip 后 7z 照样打不开），
                // 给中性色，不要用"后缀异常"的警告色把人引去改名。
                StatusText.ExtensionEmbedded;
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
