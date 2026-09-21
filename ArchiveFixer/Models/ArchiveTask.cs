using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 一个待处理压缩包任务。
    /// 
    /// 注意：
    /// 1. OriginalPath 永远保存最初导入路径。
    /// 2. CurrentPath 保存当前真实路径，改名后需要更新。
    /// 3. Password 允许保存在内存中，但不能写入日志。
    /// 4. UI 绑定此类，所以实现 INotifyPropertyChanged。
    /// </summary>
    public class ArchiveTask : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private int _index;
        private string _originalPath = string.Empty;
        private string _currentPath = string.Empty;
        private string _fileName = string.Empty;
        private string _directoryPath = string.Empty;
        private string _currentExtension = "无";
        private string _detectedFormat = "Unknown";
        private string _suggestedExtension = string.Empty;
        private string _extensionStatus = StatusText.NotChecked;
        private string _renamePreviewPath = string.Empty;
        private string _password = string.Empty;
        private string _passwordStatus = StatusText.NotChecked;
        private string _outputPath = string.Empty;
        private string _operation = StatusText.OpWaiting;
        private string _status = StatusText.WaitingScan;
        private string _progressText = "-";
        private string _errorMessage = string.Empty;
        private bool _isArchive;
        private bool _isEncrypted;
        private DateTime? _startTime;
        private DateTime? _endTime;
        private string _elapsedText = "-";
        private DateTime _lastUpdatedTime = DateTime.Now;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 是否选择此任务参与处理。
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// 任务序号。
        /// </summary>
        public int Index
        {
            get => _index;
            set => SetProperty(ref _index, value);
        }

        /// <summary>
        /// 最初导入时的路径。
        /// 即使改名后也不改变。
        /// </summary>
        public string OriginalPath
        {
            get => _originalPath;
            set => SetProperty(ref _originalPath, value ?? string.Empty);
        }

        /// <summary>
        /// 当前真实处理路径。
        /// 改名成功后必须更新。
        /// </summary>
        public string CurrentPath
        {
            get => _currentPath;
            set
            {
                if (SetProperty(ref _currentPath, value ?? string.Empty))
                {
                    RefreshPathRelatedProperties();
                }
            }
        }

        /// <summary>
        /// 当前文件名。
        /// 改名后应显示最新文件名。
        /// </summary>
        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value ?? string.Empty);
        }

        /// <summary>
        /// 当前文件所在目录。
        /// </summary>
        public string DirectoryPath
        {
            get => _directoryPath;
            set => SetProperty(ref _directoryPath, value ?? string.Empty);
        }

        /// <summary>
        /// 当前最后一个后缀。
        /// 没有后缀时为“无”。
        /// </summary>
        public string CurrentExtension
        {
            get => _currentExtension;
            set => SetProperty(ref _currentExtension, string.IsNullOrWhiteSpace(value) ? "无" : value);
        }

        /// <summary>
        /// 文件头检测格式。
        /// ZIP / 7Z / RAR4 / RAR5 / GZIP / BZIP2 / XZ / TAR / Unknown。
        /// </summary>
        public string DetectedFormat
        {
            get => _detectedFormat;
            set => SetProperty(ref _detectedFormat, string.IsNullOrWhiteSpace(value) ? "Unknown" : value);
        }

        /// <summary>
        /// 建议后缀。
        /// 例如 .zip / .7z / .rar / .gz / .bz2 / .xz / .tar。
        /// </summary>
        public string SuggestedExtension
        {
            get => _suggestedExtension;
            set => SetProperty(ref _suggestedExtension, value ?? string.Empty);
        }

        /// <summary>
        /// 后缀状态。
        /// 后缀正常 / 后缀缺失 / 后缀不匹配 / 多重后缀疑似伪装 / 格式未知 / 非压缩包。
        /// </summary>
        public string ExtensionStatus
        {
            get => _extensionStatus;
            set => SetProperty(ref _extensionStatus, string.IsNullOrWhiteSpace(value) ? StatusText.NotChecked : value);
        }

        /// <summary>
        /// 改名前预览路径。
        /// </summary>
        public string RenamePreviewPath
        {
            get => _renamePreviewPath;
            set => SetProperty(ref _renamePreviewPath, value ?? string.Empty);
        }

        /// <summary>
        /// 单任务密码。
        /// 只允许存在内存中。
        /// 严禁写入日志。
        /// </summary>
        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value ?? string.Empty);
        }

        /// <summary>
        /// 密码状态。
        /// 未检测 / 不需要密码 / 需要密码 / 已提供密码 / 密码错误 / 密码正确。
        /// </summary>
        public string PasswordStatus
        {
            get => _passwordStatus;
            set => SetProperty(ref _passwordStatus, string.IsNullOrWhiteSpace(value) ? StatusText.NotChecked : value);
        }

        /// <summary>
        /// 输出目录。
        /// </summary>
        public string OutputPath
        {
            get => _outputPath;
            set => SetProperty(ref _outputPath, value ?? string.Empty);
        }

        /// <summary>
        /// 当前操作。
        /// 等待 / 扫描 / 改名 / 测试 / 解压 / 跳过 / 完成。
        /// </summary>
        public string Operation
        {
            get => _operation;
            set => SetProperty(ref _operation, string.IsNullOrWhiteSpace(value) ? StatusText.OpWaiting : value);
        }

        /// <summary>
        /// 当前状态。
        /// 等待扫描 / 扫描中 / 已识别 / 格式未知 / 等待改名 / 改名成功 / 改名失败 /
        /// 等待测试 / 测试中 / 测试通过 / 测试失败 /
        /// 等待解压 / 解压中 / 解压成功 / 解压失败 /
        /// 密码错误 / 文件损坏 / 权限不足 / 输出路径冲突 / 分卷缺失 /
        /// 已跳过 / 已取消 / 未知错误。
        /// </summary>
        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, string.IsNullOrWhiteSpace(value) ? StatusText.WaitingScan : value);
        }

        /// <summary>
        /// 进度文本。
        /// 第一阶段可以为：
        /// - / 处理中 / 完成。
        /// </summary>
        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, string.IsNullOrWhiteSpace(value) ? "-" : value);
        }

        /// <summary>
        /// 错误信息。
        /// 不能包含明文密码。
        /// </summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value ?? string.Empty);
        }

        /// <summary>
        /// 是否识别为支持的压缩格式。
        /// </summary>
        public bool IsArchive
        {
            get => _isArchive;
            set => SetProperty(ref _isArchive, value);
        }

        /// <summary>
        /// 是否检测到可能需要密码。
        /// </summary>
        public bool IsEncrypted
        {
            get => _isEncrypted;
            set => SetProperty(ref _isEncrypted, value);
        }

        /// <summary>
        /// 任务开始时间。
        /// </summary>
        public DateTime? StartTime
        {
            get => _startTime;
            set
            {
                if (SetProperty(ref _startTime, value))
                {
                    UpdateElapsedText();
                }
            }
        }

        /// <summary>
        /// 任务结束时间。
        /// </summary>
        public DateTime? EndTime
        {
            get => _endTime;
            set
            {
                if (SetProperty(ref _endTime, value))
                {
                    UpdateElapsedText();
                }
            }
        }

        /// <summary>
        /// 耗时显示。
        /// 例如 00:00:03。
        /// </summary>
        public string ElapsedText
        {
            get => _elapsedText;
            set => SetProperty(ref _elapsedText, string.IsNullOrWhiteSpace(value) ? "-" : value);
        }

        /// <summary>
        /// 最后更新时间。
        /// </summary>
        public DateTime LastUpdatedTime
        {
            get => _lastUpdatedTime;
            set => SetProperty(ref _lastUpdatedTime, value);
        }

        /// <summary>
        /// 是否存在单任务密码。
        /// </summary>
        public bool HasTaskPassword => !string.IsNullOrEmpty(Password);

        /// <summary>
        /// 是否存在错误信息。
        /// </summary>
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        /// <summary>
        /// 本任务是否代表"一组分卷"（AGENTS.md §9.3）。
        /// 一组分卷 = 一个任务：只从第一卷启动解压，其余卷不作为独立任务出现。
        /// </summary>
        public bool IsVolumeGroup { get; set; }

        /// <summary>分卷组的归组键（目录 + 基名）。失败重试与清理源包时靠它找回同一组。</summary>
        public string VolumeGroupKey { get; set; } = string.Empty;

        /// <summary>这一组已找到的分卷文件（含第一卷），按卷序排列。</summary>
        public List<string> VolumePaths { get; } = new();

        /// <summary>已找到的卷数。</summary>
        public int VolumeCount => VolumePaths.Count;

        /// <summary>卷序是否从 1 连续、无缺口。</summary>
        public bool IsVolumeComplete { get; set; } = true;

        /// <summary>缺失的卷文件名（总数无法确定时为空）。</summary>
        public List<string> MissingVolumeNames { get; } = new();

        /// <summary>分卷情况的一句话说明，直接显示给用户。</summary>
        public string VolumeInfoText { get; set; } = string.Empty;

        /// <summary>
        /// 解压后的落盘结果是否通过校验（条目数 / 总大小）。
        /// **清理源包必须以此为前置条件**：没校验通过就不许删（AGENTS.md §9.5）。
        /// </summary>
        public bool IsOutputVerified { get; set; }

        /// <summary>校验结论的一句话说明。</summary>
        public string VerifyMessage { get; set; } = string.Empty;

        /// <summary>
        /// 引擎对"这到底是不是能解的容器"下的结论（例如"7-Zip 命令行 26.01 能打开：16 个文件"）。
        /// 魔数说不认识时，以引擎的结论为准 —— 这也是"结果可追溯"的一部分（不变量 14）。
        /// </summary>
        public string EngineVerdict { get; set; } = string.Empty;

        /// <summary>
        /// 内嵌归档在 <see cref="CurrentPath"/> 里的起始偏移，**0 表示没有内嵌归档**。
        ///
        /// 由识别阶段写进来，解压阶段据此把 <c>[偏移, EOF)</c> 抠出来再交给引擎：
        /// 这种文件的 ZIP 内部偏移相对它自己，前置数据超过 7-Zip 的容忍上限（实测 8 MiB）时就报
        /// "Cannot open the file as archive"，而真实文件前面垫了 17 MB 级的数据，必然落在拒绝区。
        /// **不要**用它去改 <see cref="CurrentPath"/> —— 源文件路径是改名、清理、统计的共同依据。
        /// </summary>
        public long EmbeddedArchiveOffset { get; set; }

        /// <summary>结果归集后的最终位置（没有归集时为空，表示还是 OutputPath）。</summary>
        public string CollectedPath { get; set; } = string.Empty;

        /// <summary>
        /// 创建任务。
        /// </summary>
        public ArchiveTask()
        {
        }

        /// <summary>
        /// 根据文件路径创建任务。
        /// </summary>
        public ArchiveTask(string filePath, int index = 0)
        {
            Index = index;
            OriginalPath = filePath ?? string.Empty;
            CurrentPath = filePath ?? string.Empty;
            Status = StatusText.WaitingScan;
            Operation = StatusText.OpWaiting;
            ProgressText = "-";
            PasswordStatus = StatusText.NotChecked;
            ExtensionStatus = StatusText.NotChecked;
            DetectedFormat = "Unknown";
            LastUpdatedTime = DateTime.Now;

            RefreshPathRelatedProperties();
        }

        /// <summary>
        /// 标记任务开始。
        /// </summary>
        public void MarkStarted(string operation, string status)
        {
            Operation = operation;
            Status = status;
            ProgressText = StatusText.ProgressProcessing;
            ErrorMessage = string.Empty;
            StartTime = DateTime.Now;
            EndTime = null;
            LastUpdatedTime = DateTime.Now;
        }

        /// <summary>
        /// 标记任务成功。
        /// </summary>
        public void MarkSuccess(string operation, string status)
        {
            Operation = operation;
            Status = status;
            ProgressText = StatusText.ProgressCompleted;
            ErrorMessage = string.Empty;
            EndTime = DateTime.Now;
            LastUpdatedTime = DateTime.Now;
            UpdateElapsedText();
        }

        /// <summary>
        /// 标记任务失败。
        /// </summary>
        public void MarkFailed(string operation, string status, string errorMessage)
        {
            Operation = operation;
            Status = status;
            ProgressText = StatusText.ProgressFailed;
            ErrorMessage = errorMessage ?? string.Empty;
            EndTime = DateTime.Now;
            LastUpdatedTime = DateTime.Now;
            UpdateElapsedText();
        }

        /// <summary>
        /// 标记任务跳过。
        /// </summary>
        public void MarkSkipped(string reason)
        {
            Operation = StatusText.OpSkip;
            Status = StatusText.Skipped;
            ProgressText = StatusText.OpSkip;
            ErrorMessage = reason ?? string.Empty;
            EndTime = DateTime.Now;
            LastUpdatedTime = DateTime.Now;
            UpdateElapsedText();
        }

        /// <summary>
        /// 标记任务取消。
        /// </summary>
        public void MarkCancelled(string reason = "用户取消")
        {
            Operation = StatusText.OpCancel;
            Status = StatusText.Cancelled;
            ProgressText = StatusText.Cancelled;
            ErrorMessage = reason;
            EndTime = DateTime.Now;
            LastUpdatedTime = DateTime.Now;
            UpdateElapsedText();
        }

        /// <summary>
        /// 更新路径相关属性。
        /// </summary>
        public void RefreshPathRelatedProperties()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(CurrentPath))
                {
                    FileName = string.Empty;
                    DirectoryPath = string.Empty;
                    CurrentExtension = "无";
                    return;
                }

                FileName = Path.GetFileName(CurrentPath);
                DirectoryPath = Path.GetDirectoryName(CurrentPath) ?? string.Empty;

                string ext = Path.GetExtension(CurrentPath);
                CurrentExtension = string.IsNullOrWhiteSpace(ext) ? "无" : ext;
            }
            catch
            {
                FileName = CurrentPath;
                DirectoryPath = string.Empty;
                CurrentExtension = "无";
            }
        }

        /// <summary>
        /// 应用识别结果到任务。
        /// </summary>
        public void ApplyDetectResult(DetectResult result, string extensionStatus)
        {
            if (result == null)
            {
                DetectedFormat = "Unknown";
                SuggestedExtension = string.Empty;
                IsArchive = false;
                IsEncrypted = false;
                EmbeddedArchiveOffset = 0;
                ExtensionStatus = StatusText.UnknownFormat;
                Status = StatusText.UnknownFormat;
                Operation = StatusText.OpScan;
                ProgressText = StatusText.ProgressCompleted;
                LastUpdatedTime = DateTime.Now;
                return;
            }

            DetectedFormat = result.Format;
            SuggestedExtension = result.SuggestedExtension;
            IsArchive = result.IsArchive;
            IsEncrypted = result.IsProbablyEncrypted;

            // 重扫时也要跟着刷新：上一次的偏移对新文件没有意义（识别结果是会被覆盖的）。
            EmbeddedArchiveOffset = result.EmbeddedArchiveOffset;
            ExtensionStatus = extensionStatus;

            if (result.IsArchive)
            {
                Status = StatusText.Recognized;
            }
            else if (result.IsKnownFormat)
            {
                Status = StatusText.Recognized;
            }
            else
            {
                Status = StatusText.UnknownFormat;
            }

            Operation = StatusText.OpScan;
            ProgressText = StatusText.ProgressCompleted;
            ErrorMessage = result.Message ?? string.Empty;
            LastUpdatedTime = DateTime.Now;
        }

        /// <summary>
        /// 更新耗时文本。
        /// </summary>
        public void UpdateElapsedText()
        {
            if (StartTime == null)
            {
                ElapsedText = "-";
                return;
            }

            DateTime end = EndTime ?? DateTime.Now;
            TimeSpan span = end - StartTime.Value;

            if (span.TotalMilliseconds < 0)
            {
                ElapsedText = "-";
                return;
            }

            ElapsedText = span.ToString(@"hh\:mm\:ss");
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

            if (propertyName != nameof(LastUpdatedTime))
            {
                _lastUpdatedTime = DateTime.Now;
                OnPropertyChanged(nameof(LastUpdatedTime));
            }

            if (propertyName == nameof(Password))
            {
                OnPropertyChanged(nameof(HasTaskPassword));
            }

            if (propertyName == nameof(ErrorMessage))
            {
                OnPropertyChanged(nameof(HasError));
            }

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
