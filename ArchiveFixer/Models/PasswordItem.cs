using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 密码列表项。
    /// 
    /// 注意：
    /// 1. Value 可以在内存中保存明文密码。
    /// 2. 日志中绝不能输出 Value。
    /// 3. 默认界面显示时应使用 MaskedValue。
    /// 4. 只有用户明确导出密码列表时才允许导出明文。
    /// </summary>
    public class PasswordItem : INotifyPropertyChanged
    {
        private string _value = string.Empty;
        private string _source = "ManualList";
        private bool _isEnabled = true;
        private string _remark = string.Empty;
        private bool _showUnwrittenMarker = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 密码明文。
        /// 仅内存使用，不写入日志。
        /// </summary>
        public string Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(MaskedValue));
                    OnPropertyChanged(nameof(DisplayValue));
                    OnPropertyChanged(nameof(IsEmptyPassword));
                    OnPropertyChanged(nameof(LengthText));
                }
            }
        }

        /// <summary>
        /// 密码来源。
        /// Empty / TaskPassword / GlobalPassword / ImportedList / ManualList。
        /// </summary>
        public string Source
        {
            get => _source;
            set => SetProperty(ref _source, string.IsNullOrWhiteSpace(value) ? "ManualList" : value);
        }

        /// <summary>
        /// 是否启用。
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        /// <summary>
        /// 备注。
        /// </summary>
        public string Remark
        {
            get => _remark;
            set => SetProperty(ref _remark, value ?? string.Empty);
        }

        /// <summary>
        /// 是否为空密码。
        /// 注意：只有长度为 0 才算空密码。
        /// 纯空格密码不算空密码。
        /// </summary>
        public bool IsEmptyPassword => string.IsNullOrEmpty(Value);

        /// <summary>
        /// 密码长度显示，不暴露内容。
        /// </summary>
        public string LengthText => $"{Value?.Length ?? 0} 字符";

        /// <summary>
        /// 脱敏后的密码。
        /// 默认界面隐藏时使用。
        /// </summary>
        public string MaskedValue
        {
            get
            {
                if (string.IsNullOrEmpty(Value))
                {
                    return "空密码";
                }

                return "******";
            }
        }

        /// <summary>
        /// 默认显示值。
        /// 默认隐藏密码。
        /// </summary>
        public string DisplayValue => MaskedValue;

        /// <summary>
        /// 这一条是不是"手动添加、还没写回密码本"（列表里要给它一个小标记）。
        ///
        /// <para>为什么需要它：手动添加的密码以前**只在本次运行内有效**，用户 2026-09-24 就是因为
        /// 看不见这件事，才会觉得"我刚加的密码怎么没了"。标记让他一眼看出"哪些条还带不走"。</para>
        ///
        /// <para>它由 <c>PasswordListViewModel</c> 维护，判据是**按值、以文件为准**的
        /// （用户 2026-09-24 第 21 条）：来源是手动添加 ⨯ 这个值不在任何一本**已记住的密码本**里。
        /// 值集合来自真正解析过的密码本文件（启动合并时记下、写回成功后重读刷新），
        /// 所以写回成功 → 标记消失 → 重启后重新读文件判定 → 仍然消失。
        /// 写回之后又改了这条密码的值，新值不在文件里 → 标记重新亮起。</para>
        ///
        /// <para>它是**纯显示状态**：不影响密码候选顺序，也不影响密码尝试上限。</para>
        /// </summary>
        public bool ShowUnwrittenMarker
        {
            get => _showUnwrittenMarker;
            set => SetProperty(ref _showUnwrittenMarker, value);
        }

        public PasswordItem()
        {
        }

        public PasswordItem(
            string value,
            string source = "ManualList",
            bool isEnabled = true,
            string remark = "")
        {
            Value = value ?? string.Empty;
            Source = source;
            IsEnabled = isEnabled;
            Remark = remark ?? string.Empty;
        }

        /// <summary>
        /// 创建空密码项。
        /// </summary>
        public static PasswordItem CreateEmpty()
        {
            return new PasswordItem(
                string.Empty,
                "Empty",
                true,
                "空密码");
        }

        /// <summary>
        /// 创建任务密码项。
        /// </summary>
        public static PasswordItem CreateTaskPassword(string password)
        {
            return new PasswordItem(
                password ?? string.Empty,
                "TaskPassword",
                true,
                "单任务密码");
        }

        /// <summary>
        /// 创建统一密码项。
        /// </summary>
        public static PasswordItem CreateGlobalPassword(string password)
        {
            return new PasswordItem(
                password ?? string.Empty,
                "GlobalPassword",
                true,
                "统一密码");
        }

        /// <summary>
        /// 创建导入密码项。
        /// </summary>
        public static PasswordItem CreateImported(string password, string remark = "导入密码")
        {
            return new PasswordItem(
                password ?? string.Empty,
                "ImportedList",
                true,
                remark);
        }

        /// <summary>
        /// 创建手动密码项。
        /// </summary>
        public static PasswordItem CreateManual(string password, string remark = "手动添加")
        {
            return new PasswordItem(
                password ?? string.Empty,
                "ManualList",
                true,
                remark);
        }

        /// <summary>
        /// 返回脱敏字符串，避免 ToString 泄露密码。
        /// </summary>
        public override string ToString()
        {
            return $"{MaskedValue} ({Source})";
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
