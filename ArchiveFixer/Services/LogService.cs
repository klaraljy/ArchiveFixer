using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 日志服务。
    /// 负责屏幕日志、文件日志、密码脱敏、导出日志、打开日志目录。
    /// </summary>
    public class LogService
    {
        private readonly PathService _pathService;
        private readonly object _lockObj = new();

        private string _currentLogFilePath = string.Empty;
        private bool _enableFileLog = true;

        public LogService()
            : this(new PathService())
        {
        }

        public LogService(PathService pathService)
        {
            _pathService = pathService;
        }

        /// <summary>
        /// 屏幕日志集合。
        /// MainViewModel 可以直接绑定该集合，或者订阅 LogAdded 事件后自己维护集合。
        /// </summary>
        public ObservableCollection<OperationLogItem> Logs { get; } = new();

        /// <summary>
        /// 日志新增事件。
        /// </summary>
        public event EventHandler<OperationLogItem>? LogAdded;

        /// <summary>
        /// 当前日志文件路径。
        /// </summary>
        public string CurrentLogFilePath => _currentLogFilePath;

        /// <summary>
        /// 是否往文件里写日志（对应设置项「启用日志文件」，默认开）。
        ///
        /// 关掉只影响**写盘**：屏幕日志照旧（用户还得看见程序在干什么），
        /// 而且 <see cref="Initialize"/> 仍然把当前日志文件路径算好，重新打开就能接着写同一个文件。
        /// </summary>
        public bool EnableFileLog
        {
            get => _enableFileLog;
            set => _enableFileLog = value;
        }

        /// <summary>
        /// 日志目录。
        /// </summary>
        public string LogDirectory => _pathService.LogsDirectory;

        /// <summary>
        /// 初始化日志系统。
        /// </summary>
        public void Initialize(bool enableFileLog = true)
        {
            _enableFileLog = enableFileLog;

            try
            {
                _pathService.EnsureBaseDirectories();

                string fileName = $"ArchiveFixer_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                _currentLogFilePath = Path.Combine(LogDirectory, fileName);

                if (_enableFileLog)
                {
                    File.WriteAllText(
                        _currentLogFilePath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] 日志初始化{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch
            {
                _enableFileLog = false;
                _currentLogFilePath = string.Empty;
            }
        }

        /// <summary>
        /// 写入 INFO 日志。
        /// </summary>
        public void WriteInfo(string message)
        {
            Write("INFO", message);
        }

        /// <summary>
        /// 写入 WARN 日志。
        /// </summary>
        public void WriteWarning(string message)
        {
            Write("WARN", message);
        }

        /// <summary>
        /// 写入 ERROR 日志。
        /// </summary>
        public void WriteError(string message)
        {
            Write("ERROR", message);
        }

        /// <summary>
        /// 写入任务日志。
        /// </summary>
        public void WriteTaskLog(ArchiveTask task, string message)
        {
            if (task == null)
            {
                WriteInfo(message);
                return;
            }

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            string text = $"任务[{fileName}] {message}";
            Write("INFO", text, task.CurrentPath);
        }

        /// <summary>
        /// 写入任务警告日志。
        /// </summary>
        public void WriteTaskWarning(ArchiveTask task, string message)
        {
            if (task == null)
            {
                WriteWarning(message);
                return;
            }

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            string text = $"任务[{fileName}] {message}";
            Write("WARN", text, task.CurrentPath);
        }

        /// <summary>
        /// 写入任务错误日志。
        /// </summary>
        public void WriteTaskError(ArchiveTask task, string message)
        {
            if (task == null)
            {
                WriteError(message);
                return;
            }

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            string text = $"任务[{fileName}] {message}";
            Write("ERROR", text, task.CurrentPath);
        }

        /// <summary>
        /// 写入日志。
        /// </summary>
        public void Write(string level, string message, string taskPath = "")
        {
            level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant();
            message = Sanitize(message);

            var item = new OperationLogItem(level, message, taskPath);

            lock (_lockObj)
            {
                AddScreenLog(item);
                WriteFileLog(item);
            }

            LogAdded?.Invoke(this, item);
        }

        /// <summary>
        /// 添加屏幕日志。
        /// </summary>
        private void AddScreenLog(OperationLogItem item)
        {
            try
            {
                Logs.Add(item);

                // 防止屏幕日志无限增长。
                while (Logs.Count > 3000)
                {
                    Logs.RemoveAt(0);
                }
            }
            catch
            {
                // 如果跨线程直接访问 ObservableCollection 出错，
                // ViewModel 可选择订阅 LogAdded 后在 UI 线程添加。
            }
        }

        /// <summary>
        /// 写入文件日志。
        /// </summary>
        private void WriteFileLog(OperationLogItem item)
        {
            if (!_enableFileLog)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_currentLogFilePath))
            {
                return;
            }

            try
            {
                File.AppendAllText(
                    _currentLogFilePath,
                    item.DisplayText + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                _enableFileLog = false;
            }
        }

        /// <summary>
        /// 日志脱敏。
        /// 不允许记录明文密码。
        /// </summary>
        public string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            string result = message;

            result = PasswordMasker.Sanitize(result);

            // -p明文密码
            result = Regex.Replace(
                result,
                @"(?i)(^|\s)-p[^\s]+",
                m =>
                {
                    string prefix = m.Value.StartsWith(" ") ? " " : string.Empty;
                    return prefix + "-p******";
                });

            // --password=明文
            result = Regex.Replace(
                result,
                @"(?i)(--password\s*=\s*)([^\s;]+)",
                "$1******");

            // password=明文
            result = Regex.Replace(
                result,
                @"(?i)(password\s*=\s*)([^\s;]+)",
                "$1******");

            // Password: 明文
            result = Regex.Replace(
                result,
                @"(?i)(password\s*:\s*)([^\r\n]+)",
                "$1******");

            // 使用密码 明文
            result = Regex.Replace(
                result,
                @"使用密码\s*[^\r\n]+",
                "使用密码 ******");

            // 尝试密码 明文
            result = Regex.Replace(
                result,
                @"尝试密码\s*[^\r\n]+",
                "尝试密码 ******");

            // 统一密码：明文
            result = Regex.Replace(
                result,
                @"统一密码\s*[:：]\s*[^\r\n]+",
                "统一密码：******");

            // 单任务密码：明文
            result = Regex.Replace(
                result,
                @"单任务密码\s*[:：]\s*[^\r\n]+",
                "单任务密码：******");

            // 密码：明文
            result = Regex.Replace(
                result,
                @"密码\s*[:：]\s*[^\r\n]+",
                "密码：******");

            return result;
        }

        /// <summary>
        /// 导出当前日志。
        /// </summary>
        public bool ExportLog(string targetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    return false;
                }

                string? directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!string.IsNullOrWhiteSpace(_currentLogFilePath) &&
                    File.Exists(_currentLogFilePath))
                {
                    File.Copy(_currentLogFilePath, targetPath, overwrite: true);
                    return true;
                }

                var builder = new StringBuilder();

                foreach (OperationLogItem item in Logs)
                {
                    builder.AppendLine(item.DisplayText);
                }

                File.WriteAllText(targetPath, builder.ToString(), Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 打开日志目录。
        /// </summary>
        public bool OpenLogDirectory()
        {
            try
            {
                _pathService.EnsureDirectoryExists(LogDirectory);
                return _pathService.OpenDirectory(LogDirectory);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 清空屏幕日志。
        /// 不删除文件日志。
        /// </summary>
        public void ClearScreenLogs()
        {
            try
            {
                Logs.Clear();
            }
            catch
            {
                // 忽略 UI 线程问题。
            }
        }

        /// <summary>
        /// 获取屏幕日志文本。
        /// </summary>
        public string GetScreenLogText()
        {
            var builder = new StringBuilder();

            foreach (OperationLogItem item in Logs)
            {
                builder.AppendLine(item.DisplayText);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 记录软件启动日志。
        /// </summary>
        public void WriteStartupInfo()
        {
            WriteInfo("软件启动");
            WriteInfo($"程序目录：{AppContext.BaseDirectory}");
            WriteInfo($"日志目录：{LogDirectory}");
        }

        /// <summary>
        /// 记录 7-Zip 检查结果。
        /// </summary>
        public void WriteSevenZipCheck(PathService pathService)
        {
            pathService ??= _pathService;

            if (pathService.SevenZipExeExists())
            {
                WriteInfo($"检测到 7-Zip：{pathService.SevenZipExePath}");
            }
            else
            {
                WriteWarning($"未找到 7-Zip：{pathService.SevenZipExePath}");
            }

            if (pathService.SevenZipDllExists())
            {
                WriteInfo($"检测到 7z.dll：{pathService.SevenZipDllPath}");
            }
            else
            {
                WriteWarning($"未找到 7z.dll：{pathService.SevenZipDllPath}");
            }
        }
    }
}
