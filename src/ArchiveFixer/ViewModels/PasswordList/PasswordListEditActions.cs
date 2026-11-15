using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using System;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ArchiveFixer.ViewModels.PasswordList;

/// <summary>
/// 列表编辑动作：添加、删除、清空、上下移动、启用/禁用全部、明文显示切换，
/// 以及列表项事件与集合重建的订阅同步。
///
/// <para>这里只动列表与密码服务；绑定属性、提示条和命令状态刷新都回到
/// <see cref="PasswordListViewModel"/> 的薄转发上。</para>
/// </summary>
internal sealed class PasswordListEditActions
{
    private readonly PasswordListViewModel _vm;

    /// <summary>
    /// "空密码"这一次会话里不再询问。
    /// 只存内存、不落盘：下次启动仍然会问一次（写进设置反而会让用户忘了自己关过确认）。
    /// </summary>
    private bool _skipEmptyPasswordConfirm;

    internal PasswordListEditActions(PasswordListViewModel vm)
    {
        _vm = vm;
    }

    /// <summary>
    /// 用服务里的密码重建列表（列表是绑定源，所以先退订旧项、再逐条挂新项）。
    /// </summary>
    internal void ReloadFromService()
    {
        foreach (PasswordItem item in _vm.Passwords)
        {
            DetachItem(item);
        }

        _vm.Passwords.Clear();

        foreach (PasswordItem item in _vm.Service.Passwords)
        {
            AttachItem(item);
            _vm.Passwords.Add(item);
        }

        if (_vm.SelectedPassword != null && !_vm.Passwords.Contains(_vm.SelectedPassword))
        {
            _vm.SelectedPassword = null;
        }

        // 标记是"当前事实"的显示状态，列表重建后必须跟着重算（否则新加的条目不会亮标记）。
        _vm.RefreshUnwrittenMarkers();

        _vm.RefreshStatistics();
    }

    /// <summary>集合变化时同步列表项的订阅（同一项重复出现时只保留一份订阅）。</summary>
    internal void OnPasswordsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (PasswordItem item in e.OldItems)
            {
                DetachItem(item);
            }
        }

        if (e.NewItems != null)
        {
            foreach (PasswordItem item in e.NewItems)
            {
                AttachItem(item);
            }
        }

        _vm.RefreshStatistics();
    }

    internal bool CanAddPassword()
    {
        // 这里不能用 IsNullOrWhiteSpace，因为密码可能就是空格。
        return _vm.NewPassword != null;
    }

    internal void AddPassword()
    {
        try
        {
            string password = _vm.NewPassword ?? string.Empty;

            if (password.Length == 0 && !_skipEmptyPasswordConfirm)
            {
                // 用带可选项位的确认框：勾上"本次运行内不再询问"后，这一次会话就不再打断。
                // 只记在内存里，重启后恢复询问 —— 免得用户忘了自己关过确认。
                bool confirmEmpty = _vm.Dialog.ShowConfirm(
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
                    _vm.Message = "已取消添加空密码。";
                    _vm.SetNotice(PasswordNoticeKind.Info, "已取消添加空密码。");
                    return;
                }
            }

            bool added = _vm.Service.AddPassword(password);

            if (added)
            {
                _vm.NewPassword = string.Empty;
                ReloadFromService();
                _vm.Message = "密码已添加。";
                _vm.SetNotice(
                    PasswordNoticeKind.Success,
                    password.Length == 0
                        ? "已加入一条空密码（排在最前面尝试）。"
                        : "已加入 1 条密码，排在列表末尾。");
            }
            else
            {
                _vm.Message = "密码已存在，未重复添加。";
                _vm.SetNotice(PasswordNoticeKind.Info, "这条密码已经在列表里了，没有重复添加。");
            }
        }
        catch (Exception ex)
        {
            _vm.Message = "添加密码失败：" + ex.Message;
            _vm.SetNotice(PasswordNoticeKind.Error, "添加密码失败：" + ex.Message);
            _vm.Dialog.ShowError("添加密码失败：" + ex.Message);
        }
    }

    internal bool CanRemoveSelected() => _vm.SelectedPassword != null;

    internal void RemovePassword()
    {
        PasswordItem? selected = _vm.SelectedPassword;

        if (selected == null)
        {
            ShowSelectionRequired();
            return;
        }

        bool confirm = _vm.Dialog.ShowConfirm("确定要删除选中的密码吗？");

        if (!confirm)
        {
            _vm.Message = "已取消删除。";
            return;
        }

        try
        {
            bool removed = _vm.Service.RemovePassword(selected);

            if (removed)
            {
                _vm.SelectedPassword = null;
                ReloadFromService();
                _vm.Message = "密码已删除。";
                _vm.SetNotice(PasswordNoticeKind.Success, "已删除 1 条密码。");
            }
            else
            {
                _vm.Message = "删除失败，密码项不存在。";
                _vm.SetNotice(PasswordNoticeKind.Error, "删除失败：密码项不存在。");
            }
        }
        catch (Exception ex)
        {
            _vm.Message = "删除密码失败：" + ex.Message;
            _vm.SetNotice(PasswordNoticeKind.Error, "删除密码失败：" + ex.Message);
            _vm.Dialog.ShowError("删除密码失败：" + ex.Message);
        }
    }

    internal void ClearPasswords()
    {
        if (_vm.Passwords.Count == 0)
        {
            _vm.Message = "密码列表为空。";
            _vm.SetNotice(PasswordNoticeKind.Info, "密码列表本来就是空的。");
            return;
        }

        // 危险操作：用警示样式的对话框，按钮文案写清"清空"而不是"是"（没有"不再询问"这一档）。
        bool confirm = _vm.Dialog.ShowDestructiveConfirm(
            $"确定要清空密码列表吗？\n\n列表里现有的 {_vm.Passwords.Count} 条密码都会被移除，且无法撤销。",
            "清空");

        if (!confirm)
        {
            _vm.Message = "已取消清空。";
            return;
        }

        try
        {
            int removedCount = _vm.Passwords.Count;

            _vm.Service.ClearPasswords();
            _vm.SelectedPassword = null;
            ReloadFromService();
            _vm.Message = "密码列表已清空。";
            _vm.SetNotice(PasswordNoticeKind.Success, $"已清空 {removedCount} 条密码。");
        }
        catch (Exception ex)
        {
            _vm.Message = "清空密码失败：" + ex.Message;
            _vm.SetNotice(PasswordNoticeKind.Error, "清空密码失败：" + ex.Message);
            _vm.Dialog.ShowError("清空密码失败：" + ex.Message);
        }
    }

    internal bool CanMoveUp()
    {
        if (_vm.SelectedPassword == null)
        {
            return false;
        }

        int index = _vm.Passwords.IndexOf(_vm.SelectedPassword);
        return index > 0;
    }

    internal void MoveUp()
    {
        PasswordItem? item = _vm.SelectedPassword;

        if (item == null)
        {
            ShowSelectionRequired();
            return;
        }

        try
        {
            if (_vm.Service.MoveUp(item))
            {
                ReloadFromService();
                _vm.SelectedPassword = item;
                _vm.Message = "已上移。";
                _vm.SetNotice(PasswordNoticeKind.Info, "已上移一位：越靠前的密码越先被尝试。");
            }
        }
        catch (Exception ex)
        {
            _vm.Message = "上移失败：" + ex.Message;
            _vm.SetNotice(PasswordNoticeKind.Error, "上移失败：" + ex.Message);
        }
    }

    internal bool CanMoveDown()
    {
        if (_vm.SelectedPassword == null)
        {
            return false;
        }

        int index = _vm.Passwords.IndexOf(_vm.SelectedPassword);
        return index >= 0 && index < _vm.Passwords.Count - 1;
    }

    internal void MoveDown()
    {
        PasswordItem? item = _vm.SelectedPassword;

        if (item == null)
        {
            ShowSelectionRequired();
            return;
        }

        try
        {
            if (_vm.Service.MoveDown(item))
            {
                ReloadFromService();
                _vm.SelectedPassword = item;
                _vm.Message = "已下移。";
                _vm.SetNotice(PasswordNoticeKind.Info, "已下移一位：越靠后的密码越晚被尝试。");
            }
        }
        catch (Exception ex)
        {
            _vm.Message = "下移失败：" + ex.Message;
            _vm.SetNotice(PasswordNoticeKind.Error, "下移失败：" + ex.Message);
        }
    }

    internal void ToggleShowPasswords()
    {
        _vm.ShowPasswords = !_vm.ShowPasswords;
        _vm.Message = _vm.ShowPasswords ? "已显示明文密码。" : "已隐藏明文密码。";

        if (_vm.ShowPasswords)
        {
            _vm.SetNotice(
                PasswordNoticeKind.Warning,
                "正在显示明文密码。注意旁人视线，不需要看时请点「隐藏明文」或直接关窗。");
        }
        else
        {
            _vm.SetNotice(PasswordNoticeKind.Info, "已恢复隐藏，密码列重新显示为圆点。");
        }
    }

    internal void EnableAll()
    {
        foreach (PasswordItem item in _vm.Passwords)
        {
            item.IsEnabled = true;
        }

        _vm.RefreshStatistics();
        _vm.Message = "已启用全部密码。";
        _vm.SetNotice(PasswordNoticeKind.Success, $"已启用全部 {_vm.TotalCount} 条密码，它们都会参与尝试。");
    }

    internal void DisableAll()
    {
        foreach (PasswordItem item in _vm.Passwords)
        {
            item.IsEnabled = false;
        }

        _vm.RefreshStatistics();
        _vm.Message = "已禁用全部密码。";
        _vm.SetNotice(PasswordNoticeKind.Warning, "已禁用全部密码：列表保留，但一条都不会被尝试。");
    }

    /// <summary>按当前"是否显示明文"给出这一行的显示文字。</summary>
    internal string GetPasswordDisplayText(PasswordItem item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        return _vm.ShowPasswords
            ? item.Value ?? string.Empty
            : item.MaskedValue;
    }

    private void ShowSelectionRequired()
    {
        _vm.Message = "请先选择密码。";
        _vm.SetNotice(PasswordNoticeKind.Info, "请先在列表里选中一条密码。");
    }

    private void AttachItem(PasswordItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        item.PropertyChanged += OnItemPropertyChanged;
    }

    private void DetachItem(PasswordItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PasswordItem.IsEnabled)
            or nameof(PasswordItem.Value)
            or nameof(PasswordItem.Remark)
            or nameof(PasswordItem.Source))
        {
            /*
             * 密码的值被改过：这条是否"还没写回"要重新算。
             * 按值的账没变，但比对的对象变了 —— 写回后改成新密码，标记必须重新亮起来
             * （新值并不在密码本文件里，重启照样丢）。
             */
            _vm.RefreshUnwrittenMarkers();
            _vm.RefreshStatistics();
        }
    }
}
