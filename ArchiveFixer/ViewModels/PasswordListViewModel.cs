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

        public ObservableCollection<PasswordItem> Passwords { get; } = new();

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

            ReloadFromService();

            Message = "密码列表已加载。";
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

                if (password.Length == 0)
                {
                    bool confirmEmpty = _dialogService.ShowConfirm("你输入的是空密码，确定要加入密码列表吗？");

                    if (!confirmEmpty)
                    {
                        Message = "已取消添加空密码。";
                        return;
                    }
                }

                bool added = _passwordService.AddPassword(password);

                if (added)
                {
                    NewPassword = string.Empty;
                    ReloadFromService();
                    Message = "密码已添加。";
                }
                else
                {
                    Message = "密码已存在，未重复添加。";
                }
            }
            catch (Exception ex)
            {
                Message = "添加密码失败：" + ex.Message;
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
                }
                else
                {
                    Message = "删除失败，密码项不存在。";
                }
            }
            catch (Exception ex)
            {
                Message = "删除密码失败：" + ex.Message;
                _dialogService.ShowError("删除密码失败：" + ex.Message);
            }
        }

        private void ClearPasswords()
        {
            if (Passwords.Count == 0)
            {
                Message = "密码列表为空。";
                return;
            }

            bool confirm = _dialogService.ShowConfirm("确定要清空密码列表吗？");

            if (!confirm)
            {
                Message = "已取消清空。";
                return;
            }

            try
            {
                _passwordService.ClearPasswords();
                SelectedPassword = null;
                ReloadFromService();
                Message = "密码列表已清空。";
            }
            catch (Exception ex)
            {
                Message = "清空密码失败：" + ex.Message;
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

                Message = $"已导入 {imported.Count} 个新密码。";
            }
            catch (Exception ex)
            {
                Message = "导入密码失败：" + ex.Message;
                _dialogService.ShowError("导入密码失败：" + ex.Message);
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
                }
            }
            catch (Exception ex)
            {
                Message = "上移失败：" + ex.Message;
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
                }
            }
            catch (Exception ex)
            {
                Message = "下移失败：" + ex.Message;
            }
        }

        private void ToggleShowPasswords()
        {
            ShowPasswords = !ShowPasswords;
            Message = ShowPasswords ? "已显示明文密码。" : "已隐藏明文密码。";
        }

        private void EnableAll()
        {
            foreach (PasswordItem item in Passwords)
            {
                item.IsEnabled = true;
            }

            RefreshStatistics();
            Message = "已启用全部密码。";
        }

        private void DisableAll()
        {
            foreach (PasswordItem item in Passwords)
            {
                item.IsEnabled = false;
            }

            RefreshStatistics();
            Message = "已禁用全部密码。";
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
