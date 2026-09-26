using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Models
{
    public class RenamePreviewItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private string _originalPath = string.Empty;
        private string _originalFileName = string.Empty;
        private string _detectedFormat = "Unknown";
        private string _operation = string.Empty;
        private string _newFileName = string.Empty;
        private string _newPath = string.Empty;
        private string _conflictAction = "AutoRename";
        private string _status = StatusText.RenameReady;
        private string _errorMessage = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string OriginalPath
        {
            get => _originalPath;
            set
            {
                if (SetProperty(ref _originalPath, value ?? string.Empty))
                {
                    OriginalFileName = Path.GetFileName(_originalPath);
                    OnPropertyChanged(nameof(CanRename));
                }
            }
        }

        public string OriginalFileName
        {
            get => _originalFileName;
            set => SetProperty(ref _originalFileName, value ?? string.Empty);
        }

        public string DetectedFormat
        {
            get => _detectedFormat;
            set => SetProperty(ref _detectedFormat, string.IsNullOrWhiteSpace(value) ? "Unknown" : value);
        }

        public string Operation
        {
            get => _operation;
            set => SetProperty(ref _operation, value ?? string.Empty);
        }

        /// <summary>
        /// 新文件名（预览表里那一列可编辑的格子）。
        ///
        /// <para>⚠ 改完必须**当场**同步 <see cref="NewPath"/>（2026-09-26 审计修的真缺陷）：
        /// 以前只在"点确认改名"那一刻同步（<see cref="SyncNewPathFromNewFileName"/> 由
        /// <c>RenamePreviewViewModel.ValidateSelectedItems</c> 调），而 <see cref="CanRename"/>、
        /// 预览表的四个统计卡、」确认改名「按钮的可用性读的全是 <c>NewPath</c> ——
        /// 于是用户改完名字之后，界面上显示的还是**旧结论**（旧落点、旧冲突数），
        /// 正是本项目最忌讳的"界面撒谎"。</para>
        ///
        /// <para>同步里带了校验（空名 / 非法字符 / 带路径分隔符 → 标成不能改名），
        /// 所以"改成非法名字"也会当场在表里反映出来，不必等到确认。</para>
        /// </summary>
        public string NewFileName
        {
            get => _newFileName;
            set
            {
                if (SetProperty(ref _newFileName, value ?? string.Empty))
                {
                    SyncNewPathFromNewFileName();
                }
            }
        }

        public string NewPath
        {
            get => _newPath;
            set
            {
                if (SetProperty(ref _newPath, value ?? string.Empty))
                {
                    string fileName = Path.GetFileName(_newPath);

                    if (!string.Equals(_newFileName, fileName, StringComparison.Ordinal))
                    {
                        _newFileName = fileName;
                        OnPropertyChanged(nameof(NewFileName));
                        OnPropertyChanged(nameof(NewFileNameDisplay));
                    }

                    OnPropertyChanged(nameof(CanRename));
                }
            }
        }

        public string ConflictAction
        {
            get => _conflictAction;
            set => SetProperty(ref _conflictAction, string.IsNullOrWhiteSpace(value) ? "AutoRename" : value);
        }

        /*
         * ===== 「询问」档在改名路径上的落地（ConflictAction = Ask）=====
         *
         * 背景（真实缺陷）：解压落点上 Ask 已经真的会问了，但**改名路径**以前把 Ask 归到
         * "其余 → 自动重命名"，于是预览表里会出现「询问」+「将自动重命名」并列 —— 界面在自相矛盾。
         *
         * 现在：撞名时预览把这一条标成"待你选择"，行内给一个下拉框（覆盖 / 跳过 / 自动重命名）。
         * **在预览里逐条确认**是刻意的选择：
         * ① 改名本来就必须先预览（不变量 3），这里就是"先给你看"的那个地方，再弹第二个对话框纯属多余；
         * ② 一批几十个文件时逐条弹窗等于不可用，而行内下拉天然支持"一次看完、一次点确认"。
         *
         * ⛔ 不变量 3 的红线：**默认仍是"自动重命名"，绝不默认覆盖**。
         * 用户不选、或者干脆关掉窗口，这一条就走自动重命名（内容不丢、已有文件一个字节不动）。
         */
        private bool _needsConflictChoice;
        private string _conflictChoice = string.Empty;

        /// <summary>
        /// 这一条撞名了、而且档位是 <c>Ask</c>：需要用户在预览里逐条选怎么办。
        ///
        /// setter 是 public 的：<c>RenameService.ApplyConflictChoice</c> 把用户的结论写回之后
        /// 要把它清掉（清掉 = 这一条已经定案，界面上的下拉框收起来）。
        /// </summary>
        public bool NeedsConflictChoice
        {
            get => _needsConflictChoice;
            set
            {
                if (SetProperty(ref _needsConflictChoice, value))
                {
                    OnPropertyChanged(nameof(ConflictChoiceHint));
                }
            }
        }

        /// <summary>
        /// 用户选定的处理方式：<c>Overwrite</c> / <c>Skip</c> / <c>AutoRename</c>；
        /// 空串 = **还没选**（执行时按保守档走自动重命名，绝不覆盖）。
        /// </summary>
        public string ConflictChoice
        {
            get => _conflictChoice;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

                if (SetProperty(ref _conflictChoice, normalized))
                {
                    OnPropertyChanged(nameof(ConflictChoiceHint));
                }
            }
        }

        /// <summary>给用户看的一句提示（表头下拉旁边那一格）。</summary>
        public string ConflictChoiceHint
        {
            get
            {
                if (!NeedsConflictChoice)
                {
                    return string.Empty;
                }

                return string.IsNullOrWhiteSpace(ConflictChoice)
                    ? "待选择（默认：自动重命名）"
                    : ConflictChoice switch
                    {
                        "Overwrite" => "覆盖（先移开旧文件再落位）",
                        "Skip" => "跳过（已有文件不动）",
                        _ => "自动重命名"
                    };
            }
        }

        /// <summary>标记这一条需要用户选（由预览生成阶段调用，见 <c>RenameService.BuildPreview</c>）。</summary>
        public void MarkNeedsConflictChoice()
        {
            NeedsConflictChoice = true;
            ConflictChoice = string.Empty;
            Status = StatusText.TargetExists;

            /*
             * 这句以前写的是"在「冲突处理」列里选" —— 而真正带下拉框的那一列叫「冲突」，
             * 「冲突处理」那一列是**只读**的档位文字。用户照着提示去点一个点不动的格子，
             * 只会以为程序坏了（2026-09-26 审计：文案指路必须指对界面）。
             */
            ErrorMessage = StatusText.RenameConflictNeedChoiceReason;
            OnPropertyChanged(nameof(CanRename));
        }

        public string Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, string.IsNullOrWhiteSpace(value) ? StatusText.RenameReady : value))
                {
                    OnPropertyChanged(nameof(CanRename));
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetProperty(ref _errorMessage, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool CanRename =>
            IsSelected &&
            !string.IsNullOrWhiteSpace(OriginalPath) &&
            !string.IsNullOrWhiteSpace(NewPath) &&
            !SafePathHelper.PathEquals(OriginalPath, NewPath) &&
            !string.Equals(Status, StatusText.RenameCannot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Status, StatusText.RenameWillSkip, StringComparison.OrdinalIgnoreCase);

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        /// <summary>预览表里「原文件名」那一格显示的名字（**中间省略，尾部后缀必须看得见**）。</summary>
        public string OriginalFileNameDisplay => FileNameMiddleEllipsis.Elide(OriginalFileName, DisplayUnits);

        /// <summary>
        /// 预览表里「新文件名」那一格显示的名字（同上）。
        /// 编辑那一格时控件换成真文本框，看到的是**全名**（省略只发生在"看"的时候）。
        /// </summary>
        public string NewFileNameDisplay => FileNameMiddleEllipsis.Elide(NewFileName, DisplayUnits);

        /// <summary>
        /// 两列名字的显示宽度上限（半角字符数）。
        ///
        /// <para>⚠ 这个数是**真机校准**出来的：36 在"全中文的长名字"上仍然会被列宽尾部再裁一刀
        /// （26 个汉字 ≈ 26 个单位，但中文字形比"1 个半角"宽，实测 260px 那一列放不下 36 个单位
        /// 的中英混排）。30 是实测放得下的值 —— 中间省略必须**保证后缀完整可见**，
        /// 否则这一列等于没修（2026-09-26 真机跑 C35 时逮到的）。</para>
        /// </summary>
        private const int DisplayUnits = 30;

        public RenamePreviewItem()
        {
        }

        public RenamePreviewItem(
            string originalPath,
            string detectedFormat,
            string operation,
            string newPath,
            string conflictAction = "AutoRename")
        {
            OriginalPath = originalPath;
            OriginalFileName = Path.GetFileName(originalPath);
            DetectedFormat = detectedFormat;
            Operation = operation;
            NewPath = newPath;
            NewFileName = Path.GetFileName(newPath);
            ConflictAction = conflictAction;
            Status = StatusText.RenameReady;
            ErrorMessage = string.Empty;
        }

        /// <summary>
        /// 根据用户编辑后的 NewFileName 同步 NewPath。
        /// 确认改名前必须调用。
        /// </summary>
        public void SyncNewPathFromNewFileName()
        {
            string fileName = NewFileName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                MarkInvalid("新文件名不能为空");
                return;
            }

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MarkInvalid("新文件名包含非法字符");
                return;
            }

            if (fileName.Contains(Path.DirectorySeparatorChar) ||
                fileName.Contains(Path.AltDirectorySeparatorChar))
            {
                MarkInvalid("新文件名不能包含路径分隔符");
                return;
            }

            string? directory = Path.GetDirectoryName(NewPath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.GetDirectoryName(OriginalPath);
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                MarkInvalid("无法确定目标目录");
                return;
            }

            string targetPath = Path.Combine(directory, fileName);

            if (SafePathHelper.PathEquals(OriginalPath, targetPath))
            {
                MarkSkip("新路径与原路径相同，跳过");
                return;
            }

            NewPath = targetPath;

            if (string.Equals(Status, StatusText.RenameCannot, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Status, StatusText.RenameWillSkip, StringComparison.OrdinalIgnoreCase))
            {
                Status = StatusText.RenameReady;
                ErrorMessage = string.Empty;
                IsSelected = true;
            }

            OnPropertyChanged(nameof(CanRename));
        }

        public void MarkConflict(string message)
        {
            Status = StatusText.TargetExists;
            ErrorMessage = message ?? string.Empty;
            OnPropertyChanged(nameof(CanRename));
        }

        public void MarkAutoRename(string message)
        {
            Status = StatusText.WillAutoRename;
            ErrorMessage = message ?? string.Empty;
            OnPropertyChanged(nameof(CanRename));
        }

        public void MarkSkip(string message)
        {
            Status = StatusText.RenameWillSkip;
            ErrorMessage = message ?? string.Empty;
            IsSelected = false;
            OnPropertyChanged(nameof(CanRename));
        }

        public void MarkInvalid(string message)
        {
            Status = StatusText.RenameCannot;
            ErrorMessage = message ?? string.Empty;
            IsSelected = false;
            OnPropertyChanged(nameof(CanRename));
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

            if (propertyName is nameof(IsSelected)
                or nameof(OriginalPath)
                or nameof(NewPath)
                or nameof(NewFileName)
                or nameof(Status))
            {
                OnPropertyChanged(nameof(CanRename));
            }

            /*
             * 两个"显示用"的名字跟着原名/新名一起刷新：
             * 省略只在**显示**这一层做，属性变了不通知的话，表里那一格还留着旧名字的省略结果。
             */
            if (propertyName is nameof(OriginalFileName))
            {
                OnPropertyChanged(nameof(OriginalFileNameDisplay));
            }

            if (propertyName is nameof(NewFileName))
            {
                OnPropertyChanged(nameof(NewFileNameDisplay));
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
