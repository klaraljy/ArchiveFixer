using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels.PasswordList;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 密码列表管理窗口 ViewModel。
///
/// <para>这个类只保留 XAML 绑定状态与公开 API 的薄转发。具体动作按职责拆在
/// <c>ViewModels/PasswordList/</c> 下的协作类里：</para>
/// <list type="bullet">
/// <item><description><see cref="PasswordListEditActions"/>：添加 / 删除 / 清空 / 上下移 / 启停 / 明文显示，以及列表项事件订阅；</description></item>
/// <item><description><see cref="PasswordListImportExport"/>：导入 txt / 导出明文 / 显式保存；</description></item>
/// <item><description><see cref="PasswordWriteBackActions"/>：写回用户自己的密码本；</description></item>
/// <item><description><see cref="PasswordListSummary"/>：条数、记忆摘要与"未写回"事实；</description></item>
/// <item><description><see cref="PasswordNoticeModel"/>：提示条与底部状态行（失败 / 结果模型）；</description></item>
/// <item><description><see cref="PasswordListCommands"/>：命令接线与可执行状态刷新。</description></item>
/// </list>
/// </summary>
public class PasswordListViewModel : ViewModelBase
{
    private readonly PasswordService _passwordService;
    private readonly DialogService _dialogService;

    private readonly PasswordListSummary _summary;
    private readonly PasswordNoticeModel _notice = new();
    private readonly PasswordListEditActions _edit;
    private readonly PasswordListCommands _commands;

    private PasswordItem? _selectedPassword;
    private string _newPassword = string.Empty;
    private bool _showPasswords;

    public ObservableCollection<PasswordItem> Passwords { get; } = new();

    /// <summary>列表里一条密码都没有（窗口显示空状态引导）。</summary>
    public bool IsEmpty => _summary.IsEmpty;

    /// <summary>提示条类别。</summary>
    public PasswordNoticeKind NoticeKind => _notice.Kind;

    /// <summary>提示条左侧的小图标字符（由类别决定，XAML 只管显示）。</summary>
    public string NoticeGlyph => _notice.Glyph;

    /// <summary>提示条文字。</summary>
    public string NoticeText => _notice.Text;

    /// <summary>是否显示提示条。</summary>
    public bool HasNotice => _notice.HasNotice;

    public PasswordItem? SelectedPassword
    {
        get => _selectedPassword;
        set
        {
            if (SetProperty(ref _selectedPassword, value))
            {
                _commands.RaiseAll();
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
                _commands.RaiseAll();
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

    /// <summary>底部状态行（"刚才做了什么"）。</summary>
    public string Message
    {
        get => _notice.Message;
        set => _notice.Message = value ?? string.Empty;
    }

    /// <summary>
    /// 兼容旧绑定名。
    /// </summary>
    public string StatusText
    {
        get => Message;
        set => Message = value;
    }

    public int TotalCount => _summary.TotalCount;

    public int EnabledCount => _summary.EnabledCount;

    /// <summary>「记住密码列表」当前是不是开着（窗口顶部那句摘要与设置里的开关是同一个事实）。</summary>
    public bool IsRememberingEnabled => _summary.IsRememberingEnabled;

    /// <summary>
    /// 窗口顶部那句"当前列表：N 条（其中启动时由记忆恢复 M 条）＋ 记住的密码本 K 本"。
    /// </summary>
    public string MemorySummary => _summary.MemorySummary;

    /// <summary>
    /// 还没写回密码本的**手工条目**条数（来源是手动添加，且它的值**不在任何一本已记住的密码本里**）。
    /// </summary>
    public int UnwrittenManualCount => _summary.UnwrittenManualCount;

    /// <summary>有没有待写回的手工条目。</summary>
    public bool HasUnwrittenManualPasswords => _summary.HasUnwrittenManualPasswords;

    public ICommand AddPasswordCommand => _commands.AddPasswordCommand;

    public ICommand RemovePasswordCommand => _commands.RemovePasswordCommand;

    public ICommand ClearPasswordsCommand => _commands.ClearPasswordsCommand;

    public ICommand ImportPasswordsCommand => _commands.ImportPasswordsCommand;

    /// <summary>
    /// 把当前完整列表导出成一份**明文** txt，供换机器/备份用。
    /// 只导出副本，不移动、不删除程序内置的加密密码本。
    /// </summary>
    public ICommand ExportPlaintextPasswordsCommand => _commands.ExportPlaintextPasswordsCommand;

    /// <summary>把手工添加的密码追加进用户自己的密码本 txt（写前自动备份）。</summary>
    public ICommand WriteBackPasswordsCommand => _commands.WriteBackPasswordsCommand;

    /// <summary>把当前密码列表显式保存到内置 passwords.txt（不看"记住密码列表"开关）。</summary>
    public ICommand SavePasswordListCommand => _commands.SavePasswordListCommand;

    public ICommand MoveUpCommand => _commands.MoveUpCommand;

    public ICommand MoveDownCommand => _commands.MoveDownCommand;

    public ICommand ToggleShowPasswordsCommand => _commands.ToggleShowPasswordsCommand;

    /// <summary>
    /// 兼容旧命令名。
    /// </summary>
    public ICommand TogglePasswordVisibleCommand => ToggleShowPasswordsCommand;

    public ICommand EnableAllCommand => _commands.EnableAllCommand;

    public ICommand DisableAllCommand => _commands.DisableAllCommand;

    /// <summary>关掉窗口内的提示条。</summary>
    public ICommand DismissNoticeCommand => _commands.DismissNoticeCommand;

    public PasswordListViewModel()
        : this(new PasswordService(), new DialogService())
    {
    }

    public PasswordListViewModel(
        PasswordService passwordService,
        DialogService dialogService)
        : this(passwordService, dialogService, new PasswordBookWriter())
    {
    }

    public PasswordListViewModel(
        PasswordService passwordService,
        DialogService dialogService,
        PasswordBookWriter passwordBookWriter)
    {
        _passwordService = passwordService ?? new PasswordService();
        _dialogService = dialogService ?? new DialogService();

        _summary = new PasswordListSummary(_passwordService, Passwords);
        _edit = new PasswordListEditActions(this);

        var remembering = new PasswordBookRemembering(this);
        var writeBackLog = new PasswordWriteBackLog(_passwordService);
        var writeBack = new PasswordWriteBackActions(
            this,
            _summary,
            remembering,
            writeBackLog,
            passwordBookWriter ?? new PasswordBookWriter());
        var importExport = new PasswordListImportExport(this, remembering, writeBackLog, writeBack);

        _commands = new PasswordListCommands(this, _edit, importExport, writeBack, _notice);

        Passwords.CollectionChanged += _edit.OnPasswordsCollectionChanged;
        _notice.PropertyChanged += Notice_PropertyChanged;

        /*
         * 记忆出问题（读不出来 / 存不下去）时，服务会喊一声 —— 这里把它变成提示条。
         *
         * 用户 2026-09-24 明确要求：**不弹错误框、不阻断**，只在界面上给一句明确的话。
         * 订阅挂在服务上（不是窗口上），所以只要这个 ViewModel 活着就一直有效。
         */
        _passwordService.RememberedListWarning += PasswordService_RememberedListWarning;

        ReloadFromService();

        Message = "密码列表已加载。";

        /*
         * 启动时如果已经带着一条"读不出来"的原因（启动那一次读记忆发生在窗口打开之前），
         * 打开窗口的第一眼就要看见它 —— 否则列表是空的，用户只会以为密码被弄丢了。
         */
        if (_passwordService.LastListWarning.Length > 0)
        {
            SetNotice(PasswordNoticeKind.Warning, _passwordService.LastListWarning);
        }
    }

    private void PasswordService_RememberedListWarning(string warning)
    {
        if (!string.IsNullOrWhiteSpace(warning))
        {
            SetNotice(PasswordNoticeKind.Warning, warning);
        }
    }

    /// <summary>
    /// 退订密码服务上的事件（窗口关闭时由 <c>PasswordListWindow.OnClosed</c> 调）。
    ///
    /// <para>为什么必须退订：那个服务与主界面同寿命，而窗口每次打开都会新建一个 ViewModel ——
    /// 不退订的话，服务会把每一个开过的窗口 ViewModel 一直拽住（关窗也回收不掉）。</para>
    /// </summary>
    public void Detach()
    {
        _passwordService.RememberedListWarning -= PasswordService_RememberedListWarning;
    }

    /// <summary>用服务里的密码重建列表（导入、增删改后由协作类调用；测试也直接调它）。</summary>
    public void ReloadFromService() => _edit.ReloadFromService();

    /// <summary>按当前"是否显示明文"给出这一行的显示文字。</summary>
    public string GetPasswordDisplayText(PasswordItem item) => _edit.GetPasswordDisplayText(item);

    /// <summary>协作类读取的密码服务（与主界面同寿命）。</summary>
    internal PasswordService Service => _passwordService;

    /// <summary>协作类读取的对话框服务（不自己弹窗，只走既有服务）。</summary>
    internal DialogService Dialog => _dialogService;

    /// <summary>设置窗口内提示条（提示文字与底部状态行分开）。</summary>
    internal void SetNotice(PasswordNoticeKind kind, string text) => _notice.Show(kind, text);

    /// <summary>把列表项的「未写回」标记刷新成当前事实。</summary>
    internal void RefreshUnwrittenMarkers() => _summary.RefreshMarkers();

    /// <summary>写回成功后按文件里的事实刷新标记，并把统计/命令状态重算一遍。</summary>
    internal void MarkAsWrittenBack(string targetPath, IReadOnlyList<string> writtenValues)
    {
        _summary.MarkAsWrittenBack(targetPath, writtenValues);
        RefreshStatistics();
    }

    /// <summary>
    /// 统计与摘要的通知出口：条数、"未写回"条数、记忆摘要一起重算，再刷新命令状态。
    /// </summary>
    internal void RefreshStatistics()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(IsEmpty));

        // 顶部那句"当前列表：N 条 + 记住的密码本 K 本"跟着条数走。
        OnPropertyChanged(nameof(MemorySummary));
        OnPropertyChanged(nameof(IsRememberingEnabled));

        // "未写回的手工条目"是按钮可用性 + 列表标记的共同判据，统计一变就一起重算。
        OnPropertyChanged(nameof(UnwrittenManualCount));
        OnPropertyChanged(nameof(HasUnwrittenManualPasswords));

        _commands.RaiseAll();
    }

    private void Notice_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PasswordNoticeModel.Kind):
                OnPropertyChanged(nameof(NoticeKind));
                break;

            case nameof(PasswordNoticeModel.Glyph):
                OnPropertyChanged(nameof(NoticeGlyph));
                break;

            case nameof(PasswordNoticeModel.Text):
                OnPropertyChanged(nameof(NoticeText));
                break;

            case nameof(PasswordNoticeModel.HasNotice):
                OnPropertyChanged(nameof(HasNotice));
                _commands.RaiseAll();
                break;

            case nameof(PasswordNoticeModel.Message):
                OnPropertyChanged(nameof(Message));
                break;
        }
    }
}
