namespace ArchiveFixer.ViewModels.PasswordList;

/// <summary>
/// 密码列表窗口的「失败 / 结果提示」模型：窗口内提示条 + 底部状态行。
///
/// <para>窗口里的结果只在两个地方说：底部状态行是"刚才做了什么"，提示条是
/// "结果是什么、要不要处理"。这里只存这两个事实（类别 / 文字 / 状态行），
/// 绑定与命令状态由 <see cref="PasswordListViewModel"/> 转发。</para>
/// </summary>
internal sealed class PasswordNoticeModel : ViewModelBase
{
    private PasswordNoticeKind _kind = PasswordNoticeKind.None;
    private string _text = string.Empty;
    private string _message = string.Empty;

    /// <summary>提示条类别。</summary>
    internal PasswordNoticeKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(HasNotice));
                OnPropertyChanged(nameof(Glyph));
            }
        }
    }

    /// <summary>提示条左侧的小图标字符（由类别决定，XAML 只管显示）。</summary>
    internal string Glyph => Kind switch
    {
        PasswordNoticeKind.Success => "✓",
        PasswordNoticeKind.Warning => "!",
        PasswordNoticeKind.Error => "×",
        PasswordNoticeKind.Info => "i",
        _ => string.Empty
    };

    /// <summary>提示条文字。</summary>
    internal string Text
    {
        get => _text;
        private set
        {
            if (SetProperty(ref _text, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    /// <summary>是否显示提示条。</summary>
    internal bool HasNotice =>
        Kind != PasswordNoticeKind.None && !string.IsNullOrWhiteSpace(Text);

    /// <summary>底部状态行（旧绑定名 <c>StatusText</c> 也落在这里）。</summary>
    internal string Message
    {
        get => _message;
        set => SetProperty(ref _message, value ?? string.Empty);
    }

    /// <summary>
    /// 设置提示条。提示文字与底部状态行分开：
    /// 底部那行是"刚才做了什么"，提示条是"结果是什么、要不要处理"。
    /// </summary>
    internal void Show(PasswordNoticeKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    /// <summary>关掉提示条（底部状态行不动）。</summary>
    internal void Clear()
    {
        Kind = PasswordNoticeKind.None;
        Text = string.Empty;
    }
}
