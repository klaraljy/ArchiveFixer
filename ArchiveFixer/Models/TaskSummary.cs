using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 任务汇总统计。
    /// 用于主界面底部统计区。
    /// </summary>
    public class TaskSummary : INotifyPropertyChanged
    {
        private int _totalCount;
        private int _selectedCount;
        private int _recognizedCount;
        private int _unknownCount;
        private int _renameSuccessCount;
        private int _renameFailedCount;
        private int _testSuccessCount;
        private int _testFailedCount;
        private int _extractSuccessCount;
        private int _extractFailedCount;
        private int _passwordErrorCount;
        private int _corruptedCount;
        private int _skippedCount;
        private int _cancelledCount;
        private int _otherFailedCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 总任务数。
        /// </summary>
        public int TotalCount
        {
            get => _totalCount;
            set => SetProperty(ref _totalCount, value);
        }

        /// <summary>
        /// 已选择任务数。
        /// </summary>
        public int SelectedCount
        {
            get => _selectedCount;
            set => SetProperty(ref _selectedCount, value);
        }

        /// <summary>
        /// 已识别任务数。
        /// </summary>
        public int RecognizedCount
        {
            get => _recognizedCount;
            set => SetProperty(ref _recognizedCount, value);
        }

        /// <summary>
        /// 未知格式任务数。
        /// </summary>
        public int UnknownCount
        {
            get => _unknownCount;
            set => SetProperty(ref _unknownCount, value);
        }

        /// <summary>
        /// 改名成功数。
        /// </summary>
        public int RenameSuccessCount
        {
            get => _renameSuccessCount;
            set => SetProperty(ref _renameSuccessCount, value);
        }

        /// <summary>
        /// 改名失败数。
        /// </summary>
        public int RenameFailedCount
        {
            get => _renameFailedCount;
            set => SetProperty(ref _renameFailedCount, value);
        }

        /// <summary>
        /// 测试成功数。
        /// </summary>
        public int TestSuccessCount
        {
            get => _testSuccessCount;
            set => SetProperty(ref _testSuccessCount, value);
        }

        /// <summary>
        /// 测试失败数。
        /// </summary>
        public int TestFailedCount
        {
            get => _testFailedCount;
            set => SetProperty(ref _testFailedCount, value);
        }

        /// <summary>
        /// 解压成功数。
        /// </summary>
        public int ExtractSuccessCount
        {
            get => _extractSuccessCount;
            set => SetProperty(ref _extractSuccessCount, value);
        }

        /// <summary>
        /// 解压失败数。
        /// </summary>
        public int ExtractFailedCount
        {
            get => _extractFailedCount;
            set => SetProperty(ref _extractFailedCount, value);
        }

        /// <summary>
        /// 密码错误数。
        /// </summary>
        public int PasswordErrorCount
        {
            get => _passwordErrorCount;
            set => SetProperty(ref _passwordErrorCount, value);
        }

        /// <summary>
        /// 文件损坏数。
        /// </summary>
        public int CorruptedCount
        {
            get => _corruptedCount;
            set => SetProperty(ref _corruptedCount, value);
        }

        /// <summary>
        /// 已跳过数。
        /// </summary>
        public int SkippedCount
        {
            get => _skippedCount;
            set => SetProperty(ref _skippedCount, value);
        }

        /// <summary>
        /// 已取消数。
        /// </summary>
        public int CancelledCount
        {
            get => _cancelledCount;
            set => SetProperty(ref _cancelledCount, value);
        }

        /// <summary>
        /// 其他失败数。
        /// </summary>
        public int OtherFailedCount
        {
            get => _otherFailedCount;
            set => SetProperty(ref _otherFailedCount, value);
        }

        /// <summary>
        /// 是否有任务。
        /// </summary>
        public bool HasTasks => TotalCount > 0;

        /// <summary>
        /// 是否有失败任务。
        /// </summary>
        public bool HasFailures =>
            RenameFailedCount > 0 ||
            TestFailedCount > 0 ||
            ExtractFailedCount > 0 ||
            PasswordErrorCount > 0 ||
            CorruptedCount > 0 ||
            OtherFailedCount > 0 ||
            UnknownCount > 0;

        /// <summary>
        /// 失败总数。
        /// </summary>
        public int FailedTotalCount =>
            RenameFailedCount +
            TestFailedCount +
            ExtractFailedCount +
            PasswordErrorCount +
            CorruptedCount +
            OtherFailedCount;

        /// <summary>
        /// 清空统计。
        /// </summary>
        public void Reset()
        {
            TotalCount = 0;
            SelectedCount = 0;
            RecognizedCount = 0;
            UnknownCount = 0;
            RenameSuccessCount = 0;
            RenameFailedCount = 0;
            TestSuccessCount = 0;
            TestFailedCount = 0;
            ExtractSuccessCount = 0;
            ExtractFailedCount = 0;
            PasswordErrorCount = 0;
            CorruptedCount = 0;
            SkippedCount = 0;
            CancelledCount = 0;
            OtherFailedCount = 0;

            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(HasFailures));
            OnPropertyChanged(nameof(FailedTotalCount));
        }

        /// <summary>
        /// 从另一个 Summary 复制数据。
        /// </summary>
        public void CopyFrom(TaskSummary source)
        {
            if (source == null)
            {
                Reset();
                return;
            }

            TotalCount = source.TotalCount;
            SelectedCount = source.SelectedCount;
            RecognizedCount = source.RecognizedCount;
            UnknownCount = source.UnknownCount;
            RenameSuccessCount = source.RenameSuccessCount;
            RenameFailedCount = source.RenameFailedCount;
            TestSuccessCount = source.TestSuccessCount;
            TestFailedCount = source.TestFailedCount;
            ExtractSuccessCount = source.ExtractSuccessCount;
            ExtractFailedCount = source.ExtractFailedCount;
            PasswordErrorCount = source.PasswordErrorCount;
            CorruptedCount = source.CorruptedCount;
            SkippedCount = source.SkippedCount;
            CancelledCount = source.CancelledCount;
            OtherFailedCount = source.OtherFailedCount;

            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(HasFailures));
            OnPropertyChanged(nameof(FailedTotalCount));
        }

        /// <summary>
        /// 创建一个当前对象的副本。
        /// </summary>
        public TaskSummary Clone()
        {
            return new TaskSummary
            {
                TotalCount = TotalCount,
                SelectedCount = SelectedCount,
                RecognizedCount = RecognizedCount,
                UnknownCount = UnknownCount,
                RenameSuccessCount = RenameSuccessCount,
                RenameFailedCount = RenameFailedCount,
                TestSuccessCount = TestSuccessCount,
                TestFailedCount = TestFailedCount,
                ExtractSuccessCount = ExtractSuccessCount,
                ExtractFailedCount = ExtractFailedCount,
                PasswordErrorCount = PasswordErrorCount,
                CorruptedCount = CorruptedCount,
                SkippedCount = SkippedCount,
                CancelledCount = CancelledCount,
                OtherFailedCount = OtherFailedCount
            };
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

            if (propertyName == nameof(TotalCount))
            {
                OnPropertyChanged(nameof(HasTasks));
            }

            if (propertyName is nameof(RenameFailedCount)
                or nameof(TestFailedCount)
                or nameof(ExtractFailedCount)
                or nameof(PasswordErrorCount)
                or nameof(CorruptedCount)
                or nameof(OtherFailedCount)
                or nameof(UnknownCount))
            {
                OnPropertyChanged(nameof(HasFailures));
                OnPropertyChanged(nameof(FailedTotalCount));
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
