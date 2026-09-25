using System;
using System.Collections.ObjectModel;
using System.Globalization;
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
        /// <summary>
        /// 日志 / 报告里时间戳的**固定格式**（不随系统区域设置、也不随系统日历变）。
        ///
        /// 为什么必须显式带上 <see cref="CultureInfo.InvariantCulture"/>：
        /// <c>DateTime.ToString("yyyy-MM-dd HH:mm:ss")</c> 里的 <c>yyyy</c> 用的是**当前区域的日历** ——
        /// 在泰历（th-TH）这类区域里同一个时刻会写成 2569 年，日志立刻变成对不上号的文本。
        /// 日志和报告是要被解析、被比对、被贴进问题反馈的文本，格式必须与用户机器设置无关
        /// （WinRAR 的"生成报告"同样固定成 <c>YYYY-MM-DD hh:mm</c>，理由一样）。
        ///
        /// ⚠ 与 <c>Models/OperationLogItem.TimeText</c> 用的是**同一个格式**：屏幕日志那一行也是
        /// <c>yyyy-MM-dd HH:mm:ss</c>。既有日志解析按这个格式写的，改这里必须连着看那里（口径只能有一份）。
        /// </summary>
        public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>日志文件名里的紧凑时间戳格式（同样固定文化，避免区域日历换出别的年份）。</summary>
        public const string FileNameTimestampFormat = "yyyyMMdd_HHmmss";

        /// <summary>
        /// 把时间按 <see cref="TimestampFormat"/> 格式化。全仓的日志/报告时间戳都走这里，
        /// **不要**再写 <c>DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")</c>（那样会各自跟随区域日历）。
        /// </summary>
        public static string FormatTimestamp(DateTime value)
        {
            return value.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>把时间按 <see cref="FileNameTimestampFormat"/> 格式化（日志文件名用）。</summary>
        public static string FormatFileTimestamp(DateTime value)
        {
            return value.ToString(FileNameTimestampFormat, CultureInfo.InvariantCulture);
        }

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

                string fileName = $"ArchiveFixer_{FormatFileTimestamp(DateTime.Now)}.log";
                _currentLogFilePath = Path.Combine(LogDirectory, fileName);

                if (_enableFileLog)
                {
                    // 首行时间戳同样走固定格式：这一行是既有日志解析的锚点，别让它随区域设置漂。
                    File.WriteAllText(
                        _currentLogFilePath,
                        $"[{FormatTimestamp(DateTime.Now)}] [INFO] 日志初始化{Environment.NewLine}",
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
        /// <summary>
        /// 日志文件里一行的完整文本：<c>[时间] [级别] 消息</c>。
        ///
        /// 形态与屏幕日志（<c>OperationLogItem.DisplayText</c>）**一模一样**，差别只在时间戳走
        /// <see cref="FormatTimestamp"/>（固定文化）。为什么不直接用 <c>item.DisplayText</c>：
        /// 它内部是 <c>Time.ToString("yyyy-MM-dd HH:mm:ss")</c>，用的是**当前区域的日历** ——
        /// 在泰历这类区域里写进文件的就是 2569 年，而日志文件是要被解析、被比对的那一份。
        /// 屏幕那一行由视图直接绑定，改不到（在 Models 里），所以文件这一份先钉死。
        /// </summary>
        public static string FormatLogLine(DateTime time, string? level, string? message)
        {
            string normalizedLevel = string.IsNullOrWhiteSpace(level) ? "INFO" : level;

            return $"[{FormatTimestamp(time)}] [{normalizedLevel}] {message}";
        }

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
                    FormatLogLine(item.Time, item.Level, item.Message) + Environment.NewLine,
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
        /// **导出全部日志**（用户 2026-09-25 第 37 条：不是只导这一次的，是**所有**日志）。
        ///
        /// <para>把日志目录里所有 <c>ArchiveFixer_*.log</c> 按文件名排序（= 时间顺序，最旧在前）
        /// 拼成一个文件，每份前面写一行分隔头（文件名 + 大小 + 行数），末尾写一行合计。
        /// 内容一律过 <see cref="PasswordMasker"/>（与"导出日志 / 导出失败清单"同一口径）。</para>
        ///
        /// <para>读不出来的那一份**跳过但记一行**（被别的程序占用 / 权限）：绝不因为一份坏了就什么都不导。</para>
        /// </summary>
        /// <returns>导出的日志份数与行数（写成 <c>(0, 0)</c> 表示日志目录里一份都没有）。</returns>
        public (int FileCount, int LineCount) ExportAllLogs(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return (0, 0);
            }

            string? directory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();

            builder.AppendLine("ArchiveFixer 日志 —— 全部日志（本程序写过的每一份都在这里）");
            builder.AppendLine($"导出时间：{FormatTimestamp(DateTime.Now)}");
            builder.AppendLine($"日志目录：{LogDirectory}");
            builder.AppendLine();

            int fileCount = 0;
            int lineCount = 0;
            var skipped = new List<string>();

            foreach (string file in EnumerateLogFilesOldestFirst())
            {
                string fileName = Path.GetFileName(file);

                string[] lines;

                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{fileName}（{PasswordMasker.Sanitize(ex.Message)}）");
                    continue;
                }

                builder.AppendLine("================================================================================");
                builder.AppendLine($"# {fileName}（{lines.Length} 行）");
                builder.AppendLine("================================================================================");

                foreach (string line in lines)
                {
                    builder.AppendLine(line);
                }

                builder.AppendLine();

                fileCount++;
                lineCount += lines.Length;
            }

            if (skipped.Count > 0)
            {
                builder.AppendLine("--------------------------------------------------------------------------------");
                builder.AppendLine("以下日志这次没能读出来（被占用或没有权限）：" + string.Join("、", skipped));
            }

            builder.AppendLine("--------------------------------------------------------------------------------");
            builder.AppendLine($"合计：{fileCount} 份日志 / {lineCount} 行。");

            File.WriteAllText(targetPath, PasswordMasker.Sanitize(builder.ToString()), new UTF8Encoding(false));

            return (fileCount, lineCount);
        }

        /// <summary>日志目录里的日志文件，按文件名排序（= 时间顺序，最旧在前）。</summary>
        private IEnumerable<string> EnumerateLogFilesOldestFirst()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(LogDirectory, "ArchiveFixer_*.log", SearchOption.TopDirectoryOnly)
                    .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
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
