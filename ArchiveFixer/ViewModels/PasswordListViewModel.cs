using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 窗口内提示条的类别。决定提示条的底色/边框/文字色（样式在 App.xaml 里）。
    ///
    /// 为什么要有它：导入结果、解析警告以前只写在窗口底部的一行小字里，
    /// 那一行又和按钮同宽、会被挤掉 —— 用户"导入了却像没导入"的观感就是这么来的。
    /// </summary>
    public enum PasswordNoticeKind
    {
        /// <summary>无提示。</summary>
        None,

        /// <summary>一般信息。</summary>
        Info,

        /// <summary>操作成功。</summary>
        Success,

        /// <summary>需要注意（解析警告、正在显示明文等）。</summary>
        Warning,

        /// <summary>失败。</summary>
        Error
    }

    /// <summary>
    /// 密码列表管理窗口 ViewModel。
    /// 
    /// 职责：
    /// 1. 添加密码。
    /// 2. 删除密码。
    /// 3. 清空密码。
    /// 4. 导入 txt 密码列表。
    /// 5. 上移下移。
    /// 6. 启用/禁用密码。
    /// 7. 显示/隐藏密码。
    /// </summary>
    public class PasswordListViewModel : ViewModelBase
    {
        private readonly PasswordService _passwordService;
        private readonly DialogService _dialogService;

        private PasswordItem? _selectedPassword;
        private string _newPassword = string.Empty;
        private bool _showPasswords;
        private string _message = string.Empty;
        private PasswordNoticeKind _noticeKind = PasswordNoticeKind.None;
        private string _noticeText = string.Empty;

        /// <summary>
        /// "空密码"这一次会话里不再询问。
        /// 只存内存、不落盘：下次启动仍然会问一次（写进设置反而会让用户忘了自己关过确认）。
        /// </summary>
        private bool _skipEmptyPasswordConfirm;

        public ObservableCollection<PasswordItem> Passwords { get; } = new();

        /// <summary>列表里一条密码都没有（窗口显示空状态引导）。</summary>
        public bool IsEmpty => Passwords.Count == 0;

        /// <summary>提示条类别。</summary>
        public PasswordNoticeKind NoticeKind
        {
            get => _noticeKind;
            private set
            {
                if (SetProperty(ref _noticeKind, value))
                {
                    OnPropertyChanged(nameof(HasNotice));
                    OnPropertyChanged(nameof(NoticeGlyph));
                }
            }
        }

        /// <summary>提示条左侧的小图标字符（由类别决定，XAML 只管显示）。</summary>
        public string NoticeGlyph => NoticeKind switch
        {
            PasswordNoticeKind.Success => "✓",
            PasswordNoticeKind.Warning => "!",
            PasswordNoticeKind.Error => "×",
            PasswordNoticeKind.Info => "i",
            _ => string.Empty
        };

        /// <summary>提示条文字。</summary>
        public string NoticeText
        {
            get => _noticeText;
            private set
            {
                if (SetProperty(ref _noticeText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasNotice));
                }
            }
        }

        /// <summary>是否显示提示条。</summary>
        public bool HasNotice =>
            NoticeKind != PasswordNoticeKind.None && !string.IsNullOrWhiteSpace(NoticeText);

        public PasswordItem? SelectedPassword
        {
            get => _selectedPassword;
            set
            {
                if (SetProperty(ref _selectedPassword, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>
        /// 新密码。
        /// 注意：不要 Trim，密码可能包含首尾空格。
        /// </summary>
        public string NewPassword
        {
            get => _newPassword;
            set
            {
                if (SetProperty(ref _newPassword, value ?? string.Empty))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>
        /// 是否显示明文密码。
        /// 默认 false。
        /// </summary>
        public bool ShowPasswords
        {
            get => _showPasswords;
            set => SetProperty(ref _showPasswords, value);
        }

        /// <summary>
        /// 兼容旧绑定名。
        /// </summary>
        public bool ShowPassword
        {
            get => ShowPasswords;
            set => ShowPasswords = value;
        }

        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value ?? string.Empty);
        }

        /// <summary>
        /// 兼容旧绑定名。
        /// </summary>
        public string StatusText
        {
            get => Message;
            set => Message = value;
        }

        public int TotalCount => Passwords.Count;

        public int EnabledCount => Passwords.Count(x => x.IsEnabled);

        public ICommand AddPasswordCommand { get; }
        public ICommand RemovePasswordCommand { get; }
        public ICommand ClearPasswordsCommand { get; }
        public ICommand ImportPasswordsCommand { get; }
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand ToggleShowPasswordsCommand { get; }

        /// <summary>
        /// 兼容旧命令名。
        /// </summary>
        public ICommand TogglePasswordVisibleCommand => ToggleShowPasswordsCommand;

        public ICommand EnableAllCommand { get; }
        public ICommand DisableAllCommand { get; }

        /// <summary>关掉窗口内的提示条。</summary>
        public ICommand DismissNoticeCommand { get; }

        public PasswordListViewModel()
            : this(new PasswordService(), new DialogService())
        {
        }

        public PasswordListViewModel(
            PasswordService passwordService,
            DialogService dialogService)
        {
            _passwordService = passwordService ?? new PasswordService();
            _dialogService = dialogService ?? new DialogService();

            Passwords.CollectionChanged += Passwords_CollectionChanged;

            AddPasswordCommand = new RelayCommand(AddPassword, CanAddPassword);
            RemovePasswordCommand = new RelayCommand(RemovePassword, CanRemoveSelected);
            ClearPasswordsCommand = new RelayCommand(ClearPasswords, () => Passwords.Count > 0);
            ImportPasswordsCommand = new RelayCommand(ImportPasswords);
            MoveUpCommand = new RelayCommand(MoveUp, CanMoveUp);
            MoveDownCommand = new RelayCommand(MoveDown, CanMoveDown);
            ToggleShowPasswordsCommand = new RelayCommand(ToggleShowPasswords);
            EnableAllCommand = new RelayCommand(EnableAll, () => Passwords.Count > 0);
            DisableAllCommand = new RelayCommand(DisableAll, () => Passwords.Count > 0);
            DismissNoticeCommand = new RelayCommand(DismissNotice, () => HasNotice);

            ReloadFromService();

            Message = "密码列表已加载。";
        }

        /// <summary>
        /// 设置窗口内提示条。提示文字与底部状态行分开：
        /// 底部那行是"刚才做了什么"，提示条是"结果是什么、要不要处理"。
        /// </summary>
        private void SetNotice(PasswordNoticeKind kind, string text)
        {
            NoticeKind = kind;
            NoticeText = text;

            RaiseCommandStates();
        }

        private void DismissNotice()
        {
            NoticeKind = PasswordNoticeKind.None;
            NoticeText = string.Empty;

            RaiseCommandStates();
        }

        public void ReloadFromService()
        {
            foreach (PasswordItem item in Passwords)
            {
                item.PropertyChanged -= PasswordItem_PropertyChanged;
            }

            Passwords.Clear();

            foreach (PasswordItem item in _passwordService.Passwords)
            {
                item.PropertyChanged -= PasswordItem_PropertyChanged;
                item.PropertyChanged += PasswordItem_PropertyChanged;
                Passwords.Add(item);
            }

            if (SelectedPassword != null && !Passwords.Contains(SelectedPassword))
            {
                SelectedPassword = null;
            }

            RefreshStatistics();
        }

        private bool CanAddPassword()
        {
            // 这里不能用 IsNullOrWhiteSpace，因为密码可能就是空格。
            return NewPassword != null;
        }

        private void AddPassword()
        {
            try
            {
                string password = NewPassword ?? string.Empty;

                if (password.Length == 0 && !_skipEmptyPasswordConfirm)
                {
                    // 用带可选项位的确认框：勾上"本次运行内不再询问"后，这一次会话就不再打断。
                    // 只记在内存里，重启后恢复询问 —— 免得用户忘了自己关过确认。
                    bool confirmEmpty = _dialogService.ShowConfirm(
                        "你输入的是空密码，确定要加入密码列表吗？",
                        "本次运行内不再询问空密码",
                        optionCheckedByDefault: false,
                        out bool skipNextTime);

                    if (skipNextTime)
                    {
                        _skipEmptyPasswordConfirm = true;
                    }

                    if (!confirmEmpty)
                    {
                        Message = "已取消添加空密码。";
                        SetNotice(PasswordNoticeKind.Info, "已取消添加空密码。");
                        return;
                    }
                }

                bool added = _passwordService.AddPassword(password);

                if (added)
                {
                    NewPassword = string.Empty;
                    ReloadFromService();
                    Message = "密码已添加。";
                    SetNotice(
                        PasswordNoticeKind.Success,
                        password.Length == 0
                            ? "已加入一条空密码（排在最前面尝试）。"
                            : "已加入 1 条密码，排在列表末尾。");
                }
                else
                {
                    Message = "密码已存在，未重复添加。";
                    SetNotice(PasswordNoticeKind.Info, "这条密码已经在列表里了，没有重复添加。");
                }
            }
            catch (Exception ex)
            {
                Message = "添加密码失败：" + ex.Message;
                SetNotice(PasswordNoticeKind.Error, "添加密码失败：" + ex.Message);
                _dialogService.ShowError("添加密码失败：" + ex.Message);
            }
        }

        private bool CanRemoveSelected()
        {
            return SelectedPassword != null;
        }

        private void RemovePassword()
        {
            if (SelectedPassword == null)
            {
                Message = "请先选择密码。";
                SetNotice(PasswordNoticeKind.Info, "请先在列表里选中一条密码。");
                return;
            }

            bool confirm = _dialogService.ShowConfirm("确定要删除选中的密码吗？");

            if (!confirm)
            {
                Message = "已取消删除。";
                return;
            }

            try
            {
                PasswordItem item = SelectedPassword;

                bool removed = _passwordService.RemovePassword(item);

                if (removed)
                {
                    SelectedPassword = null;
                    ReloadFromService();
                    Message = "密码已删除。";
                    SetNotice(PasswordNoticeKind.Success, "已删除 1 条密码。");
                }
                else
                {
                    Message = "删除失败，密码项不存在。";
                    SetNotice(PasswordNoticeKind.Error, "删除失败：密码项不存在。");
                }
            }
            catch (Exception ex)
            {
                Message = "删除密码失败：" + ex.Message;
                SetNotice(PasswordNoticeKind.Error, "删除密码失败：" + ex.Message);
                _dialogService.ShowError("删除密码失败：" + ex.Message);
            }
        }

        private void ClearPasswords()
        {
            if (Passwords.Count == 0)
            {
                Message = "密码列表为空。";
                SetNotice(PasswordNoticeKind.Info, "密码列表本来就是空的。");
                return;
            }

            // 危险操作：用警示样式的对话框，按钮文案写清"清空"而不是"是"（没有"不再询问"这一档）。
            bool confirm = _dialogService.ShowDestructiveConfirm(
                $"确定要清空密码列表吗？\n\n列表里现有的 {Passwords.Count} 条密码都会被移除，且无法撤销。",
                "清空");

            if (!confirm)
            {
                Message = "已取消清空。";
                return;
            }

            try
            {
                int removedCount = Passwords.Count;

                _passwordService.ClearPasswords();
                SelectedPassword = null;
                ReloadFromService();
                Message = "密码列表已清空。";
                SetNotice(PasswordNoticeKind.Success, $"已清空 {removedCount} 条密码。");
            }
            catch (Exception ex)
            {
                Message = "清空密码失败：" + ex.Message;
                SetNotice(PasswordNoticeKind.Error, "清空密码失败：" + ex.Message);
                _dialogService.ShowError("清空密码失败：" + ex.Message);
            }
        }

        private void ImportPasswords()
        {
            try
            {
                string path = _dialogService.ShowOpenSingleFileDialog(
                    "导入密码列表",
                    "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*");

                if (string.IsNullOrWhiteSpace(path))
                {
                    Message = "已取消导入。";
                    return;
                }

                var imported = _passwordService.ImportPasswordList(path);

                ReloadFromService();

                RememberPasswordBookPath(path);

                // 与主界面「工具 → 导入密码本…」同一套文案：条数 + 已记住 + 下次启动自动加载。
                // 以前这里只有"已导入 N 个新密码"，用户看不出到底记住没有，
                // 而且只写在底部一行小字里 —— 现在改成窗口内的提示条，不容易被忽略。
                int warningCount = _passwordService.LastImportWarnings.Count;

                string summary =
                    $"已从「{System.IO.Path.GetFileName(path)}」导入 {imported.Count} 条新密码，" +
                    $"当前共 {TotalCount} 条（启用 {EnabledCount} 条）。" +
                    "已记住这个文件，下次启动会自动加载。";

                if (warningCount > 0)
                {
                    summary += Environment.NewLine + "解析警告（这些行被跳过，密码本身没有丢）："
                        + Environment.NewLine + "· " + string.Join(Environment.NewLine + "· ", _passwordService.LastImportWarnings);

                    Message = $"已导入 {imported.Count} 条密码，有 {warningCount} 条解析警告。";
                    SetNotice(PasswordNoticeKind.Warning, summary);
                }
                else
                {
                    Message = $"已导入 {imported.Count} 条密码，并记住了这个文件，下次启动会自动加载。";
                    SetNotice(PasswordNoticeKind.Success, summary);
                }
            }
            catch (Exception ex)
            {
                Message = "导入密码失败：" + ex.Message;
                SetNotice(PasswordNoticeKind.Error, "导入密码失败：" + ex.Message);
                _dialogService.ShowError("导入密码失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 记住密码本路径。
        ///
        /// 两个地方都要写，少一个就会出现"这次导了、下次启动不认"：
        /// 1. <c>appsettings.json</c> 的 <c>PasswordBookPath</c> —— 与主界面导入走的是同一个设置项；
        /// 2. 侧车文件 <c>password-book.path</c> —— 由 <see cref="PasswordService.ImportPasswordList"/> 自己写。
        ///
        /// 设置文件按"数据根目录"定位：PasswordService.DataRootDirectory 与
        /// PathService.DataRootDirectory 被主界面刻意同步成同一个值（MainViewModel.ApplyEngineSettings），
        /// 所以这里用前者构造 PathService，才不会出现用户改过缓存根目录后写到另一个盘去。
        /// </summary>
        private void RememberPasswordBookPath(string path)
        {
            try
            {
                var pathService = new PathService
                {
                    DataRootDirectory = _passwordService.DataRootDirectory
                };

                var settingsService = new SettingsService(pathService);

                // 先 Load 再改：不能拿一份全新的默认设置整体覆盖用户的 appsettings.json。
                AppSettings settings = settingsService.Load();
                settings.PasswordBookPath = path;
                settingsService.Save(settings);
            }
            catch (Exception ex)
            {
                // 记不住路径不该让导入本身失败，但要说清楚 —— 否则用户下次启动发现密码本没自动加载会以为是 bug。
                Message += "（提示：路径没能记进设置，下次启动可能不会自动加载：" + ex.Message + "）";
            }
        }

        private bool CanMoveUp()
        {
            if (SelectedPassword == null)
            {
                return false;
            }

            int index = Passwords.IndexOf(SelectedPassword);
            return index > 0;
        }

        private void MoveUp()
        {
            if (SelectedPassword == null)
            {
                Message = "请先选择密码。";
                SetNotice(PasswordNoticeKind.Info, "请先在列表里选中一条密码。");
                return;
            }

            try
            {
                PasswordItem item = SelectedPassword;

                if (_passwordService.MoveUp(item))
                {
                    ReloadFromService();
                    SelectedPassword = item;
                    Message = "已上移。";
                    SetNotice(PasswordNoticeKind.Info, "已上移一位：越靠前的密码越先被尝试。");
                }
            }
            catch (Exception ex)
            {
                Message = "上移失败：" + ex.Message;
                SetNotice(PasswordNoticeKind.Error, "上移失败：" + ex.Message);
            }
        }

        private bool CanMoveDown()
        {
            if (SelectedPassword == null)
            {
                return false;
            }

            int index = Passwords.IndexOf(SelectedPassword);
            return index >= 0 && index < Passwords.Count - 1;
        }

        private void MoveDown()
        {
            if (SelectedPassword == null)
            {
                Message = "请先选择密码。";
                SetNotice(PasswordNoticeKind.Info, "请先在列表里选中一条密码。");
                return;
            }

            try
            {
                PasswordItem item = SelectedPassword;

                if (_passwordService.MoveDown(item))
                {
                    ReloadFromService();
                    SelectedPassword = item;
                    Message = "已下移。";
                    SetNotice(PasswordNoticeKind.Info, "已下移一位：越靠后的密码越晚被尝试。");
                }
            }
            catch (Exception ex)
            {
                Message = "下移失败：" + ex.Message;
                SetNotice(PasswordNoticeKind.Error, "下移失败：" + ex.Message);
            }
        }

        private void ToggleShowPasswords()
        {
            ShowPasswords = !ShowPasswords;
            Message = ShowPasswords ? "已显示明文密码。" : "已隐藏明文密码。";

            if (ShowPasswords)
            {
                SetNotice(
                    PasswordNoticeKind.Warning,
                    "正在显示明文密码。注意旁人视线，不需要看时请点「隐藏明文」或直接关窗。");
            }
            else
            {
                SetNotice(PasswordNoticeKind.Info, "已恢复隐藏，密码列重新显示为圆点。");
            }
        }

        private void EnableAll()
        {
            foreach (PasswordItem item in Passwords)
            {
                item.IsEnabled = true;
            }

            RefreshStatistics();
            Message = "已启用全部密码。";
            SetNotice(PasswordNoticeKind.Success, $"已启用全部 {TotalCount} 条密码，它们都会参与尝试。");
        }

        private void DisableAll()
        {
            foreach (PasswordItem item in Passwords)
            {
                item.IsEnabled = false;
            }

            RefreshStatistics();
            Message = "已禁用全部密码。";
            SetNotice(PasswordNoticeKind.Warning, "已禁用全部密码：列表保留，但一条都不会被尝试。");
        }

        public string GetPasswordDisplayText(PasswordItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            return ShowPasswords
                ? item.Value ?? string.Empty
                : item.MaskedValue;
        }

        private void Passwords_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (PasswordItem item in e.OldItems)
                {
                    item.PropertyChanged -= PasswordItem_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (PasswordItem item in e.NewItems)
                {
                    item.PropertyChanged -= PasswordItem_PropertyChanged;
                    item.PropertyChanged += PasswordItem_PropertyChanged;
                }
            }

            RefreshStatistics();
        }

        private void PasswordItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PasswordItem.IsEnabled)
                or nameof(PasswordItem.Value)
                or nameof(PasswordItem.Remark)
                or nameof(PasswordItem.Source))
            {
                RefreshStatistics();
            }
        }

        private void RefreshStatistics()
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(EnabledCount));
            OnPropertyChanged(nameof(IsEmpty));
            RaiseCommandStates();
        }

        private void RaiseCommandStates()
        {
            RaiseCanExecuteChanged(AddPasswordCommand);
            RaiseCanExecuteChanged(RemovePasswordCommand);
            RaiseCanExecuteChanged(ClearPasswordsCommand);
            RaiseCanExecuteChanged(MoveUpCommand);
            RaiseCanExecuteChanged(MoveDownCommand);
            RaiseCanExecuteChanged(EnableAllCommand);
            RaiseCanExecuteChanged(DisableAllCommand);
            RaiseCanExecuteChanged(DismissNoticeCommand);
        }

        private static void RaiseCanExecuteChanged(ICommand command)
        {
            if (command is RelayCommand relayCommand)
            {
                relayCommand.RaiseCanExecuteChanged();
            }
        }
    }
}
