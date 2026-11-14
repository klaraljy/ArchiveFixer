using System.Windows.Input;

namespace ArchiveFixer.ViewModels.PasswordList;

/// <summary>
/// 密码列表窗口的命令集：只把按钮接到编辑 / 导入导出 / 写回 / 提示条上，
/// 并在状态变化时统一刷新可执行状态。
///
/// <para>命令一律由 <see cref="PasswordListViewModel"/> 以只读属性转发给 XAML，
/// 绑定名与旧实现完全一致。</para>
/// </summary>
internal sealed class PasswordListCommands
{
    internal PasswordListCommands(
        PasswordListViewModel vm,
        PasswordListEditActions edit,
        PasswordListImportExport importExport,
        PasswordWriteBackActions writeBack,
        PasswordNoticeModel notice)
    {
        AddPasswordCommand = new RelayCommand(edit.AddPassword, edit.CanAddPassword);
        RemovePasswordCommand = new RelayCommand(edit.RemovePassword, edit.CanRemoveSelected);
        ClearPasswordsCommand = new RelayCommand(edit.ClearPasswords, () => vm.Passwords.Count > 0);
        ImportPasswordsCommand = new RelayCommand(importExport.ImportPasswords);
        ExportPlaintextPasswordsCommand = new RelayCommand(importExport.ExportPlaintextPasswords, () => vm.Passwords.Count > 0);
        WriteBackPasswordsCommand = new RelayCommand(writeBack.WriteBackPasswords, writeBack.CanWriteBackPasswords);
        SavePasswordListCommand = new RelayCommand(importExport.SavePasswordList, () => vm.Passwords.Count > 0);
        MoveUpCommand = new RelayCommand(edit.MoveUp, edit.CanMoveUp);
        MoveDownCommand = new RelayCommand(edit.MoveDown, edit.CanMoveDown);
        ToggleShowPasswordsCommand = new RelayCommand(edit.ToggleShowPasswords);
        EnableAllCommand = new RelayCommand(edit.EnableAll, () => vm.Passwords.Count > 0);
        DisableAllCommand = new RelayCommand(edit.DisableAll, () => vm.Passwords.Count > 0);
        DismissNoticeCommand = new RelayCommand(notice.Clear, () => notice.HasNotice);
    }

    internal ICommand AddPasswordCommand { get; }

    internal ICommand RemovePasswordCommand { get; }

    internal ICommand ClearPasswordsCommand { get; }

    internal ICommand ImportPasswordsCommand { get; }

    /// <summary>
    /// 把当前完整列表导出成一份**明文** txt，供换机器/备份用。
    /// 只导出副本，不移动、不删除程序内置的加密密码本。
    /// </summary>
    internal ICommand ExportPlaintextPasswordsCommand { get; }

    /// <summary>把手工添加的密码追加进用户自己的密码本 txt（写前自动备份）。</summary>
    internal ICommand WriteBackPasswordsCommand { get; }

    /// <summary>把当前密码列表显式保存到内置 passwords.txt（不看"记住密码列表"开关）。</summary>
    internal ICommand SavePasswordListCommand { get; }

    internal ICommand MoveUpCommand { get; }

    internal ICommand MoveDownCommand { get; }

    internal ICommand ToggleShowPasswordsCommand { get; }

    internal ICommand EnableAllCommand { get; }

    internal ICommand DisableAllCommand { get; }

    /// <summary>关掉窗口内的提示条。</summary>
    internal ICommand DismissNoticeCommand { get; }

    /// <summary>把所有命令的可执行状态重算一遍（列表/选中项/提示条变化时调用）。</summary>
    internal void RaiseAll()
    {
        RaiseCanExecuteChanged(AddPasswordCommand);
        RaiseCanExecuteChanged(RemovePasswordCommand);
        RaiseCanExecuteChanged(ClearPasswordsCommand);
        RaiseCanExecuteChanged(ExportPlaintextPasswordsCommand);
        RaiseCanExecuteChanged(SavePasswordListCommand);
        RaiseCanExecuteChanged(WriteBackPasswordsCommand);
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
