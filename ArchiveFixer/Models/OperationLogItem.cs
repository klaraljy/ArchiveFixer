using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 屏幕日志项。
    /// LogService 写入文件日志时，也可以同步生成此对象用于界面显示。
    /// </summary>
    public class OperationLogItem : INotifyPropertyChanged
    {
        private DateTime _time = DateTime.Now;
        private string _level = "INFO";
        private string _message = string.Empty;
        private string _taskPath = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 日志时间。
        /// </summary>
        public DateTime Time
        {
            get => _time;
            set
            {
                if (SetProperty(ref _time, value))
                {
                    OnPropertyChanged(nameof(TimeText));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>
        /// 日志级别。
        /// INFO / WARN / ERROR。
        /// </summary>
        public string Level
        {
            get => _level;
            set
            {
                if (SetProperty(ref _level, string.IsNullOrWhiteSpace(value) ? "INFO" : value))
                {
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>
        /// 日志消息。
        /// 不能包含明文密码。
        /// </summary>
        public string Message
        {
            get => _message;
            set
            {
                if (SetProperty(ref _message, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        /// <summary>
        /// 关联任务路径。
        /// 可为空。
        /// </summary>
        public string TaskPath
        {
            get => _taskPath;
            set => SetProperty(ref _taskPath, value ?? string.Empty);
        }

        /// <summary>
        /// 时间文本。
        /// </summary>
        public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>
        /// 显示文本。
        /// </summary>
        public string DisplayText => $"[{TimeText}] [{Level}] {Message}";

        public OperationLogItem()
        {
        }

        public OperationLogItem(string level, string message, string taskPath = "")
        {
            Time = DateTime.Now;
            Level = string.IsNullOrWhiteSpace(level) ? "INFO" : level;
            Message = message ?? string.Empty;
            TaskPath = taskPath ?? string.Empty;
        }

        public static OperationLogItem Info(string message, string taskPath = "")
        {
            return new OperationLogItem("INFO", message, taskPath);
        }

        public static OperationLogItem Warning(string message, string taskPath = "")
        {
            return new OperationLogItem("WARN", message, taskPath);
        }

        public static OperationLogItem Error(string message, string taskPath = "")
        {
            return new OperationLogItem("ERROR", message, taskPath);
        }

        public override string ToString()
        {
            return DisplayText;
        }

        protected bool SetProperty<T>(
            ref T storage,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
