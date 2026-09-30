using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
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
        /// <summary>
        /// 手动添加的条目在内存里就是这个来源（<see cref="PasswordService.AddPassword"/> 写的）。
        /// 只认这一个值 = 只对"手动加的"提供写回，导入进来的本来就在密码本文件里。
        /// </summary>
        private const string ManualSource = "ManualList";

        /// <summary><see cref="PasswordService.AddPassword"/> 给手动条目的默认备注（只在没被用户改过时才更新它）。</summary>
        private const string DefaultManualRemark = "手动添加";

        private readonly PasswordService _passwordService;
        private readonly DialogService _dialogService;

        /// <summary>把手工条目追加进用户自己的密码本 txt（写前备份、只追加、写完自检）。</summary>
        private readonly PasswordBookWriter _passwordBookWriter;

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

        /// <summary>「记住密码列表」当前是不是开着（窗口顶部那句摘要与设置里的开关是同一个事实）。</summary>
        public bool IsRememberingEnabled => _passwordService.RememberPasswordList;

        /// <summary>
        /// 窗口顶部那句"当前列表：N 条（其中启动时由记忆恢复 M 条）＋ 记住的密码本 K 本"。
        ///
        /// <para>用户 2026-09-24 要求窗口上能一眼看出"列表现在到底靠什么活着"：
        /// 是记忆恢复来的、还是这轮从密码本里合并出来的、又记住几本书。</para>
        /// </summary>
        public string MemorySummary
        {
            get
            {
                if (!IsRememberingEnabled)
                {
                    return Models.StatusText.PasswordListMemoryDisabledSummary;
                }

                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordListMemorySummaryFormat,
                    TotalCount,
                    _passwordService.RememberedEntryCount,
                    _passwordService.RememberedBookPaths.Count);
            }
        }

        /// <summary>
        /// 还没写回密码本的**手工条目**条数（来源是手动添加，且它的值**不在任何一本已记住的密码本里**）。
        ///
        /// <para>它就是「写回密码本…」按钮的可用性判据 —— 一条都没有时按钮置灰，
        /// 用户点不动，也就不会产生"点了一下什么都没发生"的困惑。</para>
        ///
        /// <para>判据是**按值、且以文件为准**的（用户 2026-09-24 第 21 条）：重启之后程序重新读一遍
        /// 每本已记住的书，值真的在里面才算已写回。⛔ 绝不靠"本次运行里记过一笔"这种只在内存里的账 ——
        /// 那正是"提示写回成功、重启又显示未写回"的旧病根。</para>
        /// </summary>
        public int UnwrittenManualCount => CollectUnwrittenManualValues().Count;

        /// <summary>有没有待写回的手工条目。</summary>
        public bool HasUnwrittenManualPasswords => UnwrittenManualCount > 0;

        public ICommand AddPasswordCommand { get; }
        public ICommand RemovePasswordCommand { get; }
        public ICommand ClearPasswordsCommand { get; }
        public ICommand ImportPasswordsCommand { get; }

        /// <summary>把手工添加的密码追加进用户自己的密码本 txt（写前自动备份）。</summary>
        public ICommand WriteBackPasswordsCommand { get; }

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
            _passwordBookWriter = passwordBookWriter ?? new PasswordBookWriter();

            Passwords.CollectionChanged += Passwords_CollectionChanged;

            AddPasswordCommand = new RelayCommand(AddPassword, CanAddPassword);
            RemovePasswordCommand = new RelayCommand(RemovePassword, CanRemoveSelected);
            ClearPasswordsCommand = new RelayCommand(ClearPasswords, () => Passwords.Count > 0);
            ImportPasswordsCommand = new RelayCommand(ImportPasswords);
            WriteBackPasswordsCommand = new RelayCommand(WriteBackPasswords, CanWriteBackPasswords);
            MoveUpCommand = new RelayCommand(MoveUp, CanMoveUp);
            MoveDownCommand = new RelayCommand(MoveDown, CanMoveDown);
            ToggleShowPasswordsCommand = new RelayCommand(ToggleShowPasswords);
            EnableAllCommand = new RelayCommand(EnableAll, () => Passwords.Count > 0);
            DisableAllCommand = new RelayCommand(DisableAll, () => Passwords.Count > 0);
            DismissNoticeCommand = new RelayCommand(DismissNotice, () => HasNotice);

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

            // 标记是"当前事实"的显示状态，列表重建后必须跟着重算（否则新加的条目不会亮标记）。
            RefreshUnwrittenMarkers();

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
        /// 所以这里用前者构造 PathService，两边算出来的是同一个目录。
        /// </summary>
        private void RememberPasswordBookPath(string path)
        {
            try
            {
                SettingsService settingsService = CreateSettingsService();

                // 先 Load 再改：不能拿一份全新的默认设置整体覆盖用户的 appsettings.json。
                AppSettings settings = settingsService.Load();

                /*
                 * 2026-09-24 起"记住哪一本"是**有序清单**（多本密码本）：这里是**追加**，
                 * 不是在清单上覆盖 —— 覆盖的后果正是用户报的那个现象"导入第二本，第一本不见了"。
                 * 去重按 Windows 路径口径（大小写不敏感），重复导入同一本不会长出第二项。
                 */
                settings.PasswordBookPaths = AppendBookPath(settings.PasswordBookPaths, path);

                // 老字段一起维护（回退到旧版本时那边只认它）。
                settings.PasswordBookPath = path;

                settingsService.Save(settings);

                /*
                 * 侧车文件：ImportPasswordList 顺手写的那一份在这里补上。
                 * 用户如果是"先写回、再首次导入"这个顺序，少了这一笔主界面就会说"没配过密码本"。
                 * 只写路径，绝不碰密码本内容本身。
                 */
                WritePasswordBookSidecar(path);
            }
            catch (Exception ex)
            {
                // 记不住路径不该让导入本身失败，但要说清楚 —— 否则用户下次启动发现密码本没自动加载会以为是 bug。
                Message += "（提示：路径没能记进设置，下次启动可能不会自动加载：" + ex.Message + "）";
            }
        }

        /// <summary>把一本密码本追加进清单（已存在则原样返回，顺序不动）。</summary>
        private static List<string> AppendBookPath(IEnumerable<string>? existing, string path)
        {
            var result = new List<string>();

            if (existing != null)
            {
                foreach (string item in existing)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        result.Add(item);
                    }
                }
            }

            foreach (string item in result)
            {
                if (string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                result.Add(path);
            }

            return result;
        }

        /// <summary>把密码本路径写进侧车文件（与 <see cref="PasswordService"/> 同一份口径与位置）。</summary>
        private void WritePasswordBookSidecar(string path)
        {
            string dataRoot = _passwordService.DataRootDirectory;

            Directory.CreateDirectory(dataRoot);

            File.WriteAllText(
                Path.Combine(dataRoot, "password-book.path"),
                path,
                new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------ 写回密码本（用户 2026-09-24）

        private bool CanWriteBackPasswords()
        {
            return UnwrittenManualCount > 0;
        }

        /// <summary>
        /// 把列表里**手动添加**的密码追加进用户自己的密码本 txt。
        ///
        /// <para>为什么只能是"追加到用户那份 txt"：列表本身虽然按本机加密保存（AGENTS.md §6 不变量 5，
        /// 用户 2026-09-24 拍板），但那份记忆**换机器 / 重装系统就解不开**、关掉「记住密码列表」也没有 ——
        /// 真正带得走、也永远不会被程序碰的载体，只有用户自己的密码本文件。
        /// ⛔ 绝不写进 <c>appsettings.json</c>、也绝不写进程序自己的 <c>data\</c> 下任何文件。</para>
        ///
        /// <para>整条流程：挑出待写条目 → 解析目标文件（没有就问一次）→ 确认框里逐条列出明文
        /// （界面上给他看是应该的；<b>日志里绝不出现明文</b>，见 §8）→ 追加 + 备份 + 自检 →
        /// 如实显示"写了几条 / 跳过几条 / 写到哪 / 备份在哪"。</para>
        /// </summary>
        private void WriteBackPasswords()
        {
            try
            {
                IReadOnlyList<string> pending = CollectUnwrittenManualValues();

                if (pending.Count == 0)
                {
                    Message = Models.StatusText.PasswordWriteBackStatusNoOp;
                    SetNotice(PasswordNoticeKind.Info, Models.StatusText.PasswordWriteBackNothingToWrite);
                    return;
                }

                string targetPath = ResolveWriteBackTargetPath();

                if (targetPath.Length == 0)
                {
                    Message = Models.StatusText.PasswordWriteBackStatusCancelled;
                    return;
                }

                string confirmMessage = BuildWriteBackConfirmMessage(pending.Count, targetPath);

                bool confirmed = _dialogService.ShowConfirm(
                    confirmMessage,
                    optionText: string.Empty,
                    optionCheckedByDefault: false,
                    detail: BuildWriteBackConfirmDetail(pending),
                    out _);

                if (!confirmed)
                {
                    // 取消则什么都不做：不备份、不打开文件、一个字节都不动。
                    Message = Models.StatusText.PasswordWriteBackStatusCancelled;
                    WriteWriteBackLog(Models.StatusText.PasswordWriteBackLogCancelled);
                    return;
                }

                PasswordBookWriteBackResult result = _passwordBookWriter.WriteBack(targetPath, pending);

                ReportWriteBackResult(result, pending);
            }
            catch (Exception ex)
            {
                // 兜底：真正会出事的地方（文件、对话框）各自都有明确的失败分支，
                // 走到这里说明是没预料到的异常。如实显示，绝不假装成功。
                string reason = ex.GetType().Name + "：" + ex.Message;

                Message = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackFailedFormat,
                    reason);

                SetNotice(PasswordNoticeKind.Error, Message);
                _dialogService.ShowError(Message);
            }
        }

        /// <summary>
        /// 挑出待写回的值：手动添加、且它的值**不在任何一本已记住的密码本里**。
        /// 列表顺序原样保留（写回后文件里的先后就是用户看到的先后）。
        ///
        /// <para>判据必须是"按值 + 以文件为准"的，不能只看来源，也不能只看"本次运行里写过没有"：
        /// 用户写回之后又把某条密码改了，新值并不在文件里，那一条就要**重新变回未写回**
        /// （标记重新出现）；反过来，重启之后来源还是 <c>ManualList</c>，但值已经在书里了，
        /// 那一条就**不许**再算未写回 —— 那正是用户报的第 21 条。</para>
        /// </summary>
        private List<string> CollectUnwrittenManualValues()
        {
            return Passwords
                .Where(IsUnwrittenManual)
                .Select(item => item.Value ?? string.Empty)
                .ToList();
        }

        /// <summary>
        /// 这一条是不是"手动添加、还没写回"（用户 2026-09-24 第 21 条的口径）。
        ///
        /// <para>判据两条：来源是手动 ⨯ **这个值不在任何一本已记住的密码本里**。
        /// 第二条查的是 <see cref="PasswordService.IsValueInRememberedBooks"/> ——
        /// 它认的是**从文件里解析出来的值**（启动合并每本书时记下、"写回"成功后重读文件刷新）。
        /// 于是"写回成功 → 标记消失 → 重启后仍然消失"是同一个事实的两次查询，而不是两套账。</para>
        ///
        /// <para>没有任何已记住的书 / 书不存在 / 读不出来：查询恒为 false → 标记照旧亮着。
        /// 这是**刻意**的诚实行为：那时程序确实不知道这个值在不在用户的书里，就不许说已写回。</para>
        /// </summary>
        private bool IsUnwrittenManual(PasswordItem? item)
        {
            return item != null
                && string.Equals(item.Source, ManualSource, StringComparison.Ordinal)
                && !IsInRememberedBook(item);
        }

        /// <summary>这一条的值是不是已经躺在某一本已记住的密码本里。</summary>
        private bool IsInRememberedBook(PasswordItem item)
        {
            string value = item.Value ?? string.Empty;

            return value.Length > 0 && _passwordService.IsValueInRememberedBooks(value);
        }

        /// <summary>
        /// 把列表里每一条的「未写回」标记刷新成当前事实（列表重建后、以及某条的密码值被改过之后都要刷新）。
        /// </summary>
        private void RefreshUnwrittenMarkers()
        {
            foreach (PasswordItem item in Passwords)
            {
                if (item != null)
                {
                    item.ShowUnwrittenMarker = IsUnwrittenManual(item);
                }
            }
        }

        /// <summary>
        /// 写回成功之后刷新**事实**（用户 2026-09-24 第 21 条的核心修法）。
        ///
        /// <para>顺序不能反、也不能省：</para>
        /// <list type="number">
        /// <item><description>目标文件从此是一本"已记住"的密码本（写进去了，就是他在用的书）；</description></item>
        /// <item><description><b>重新读一遍文件</b>得出值集合 —— ⛔ 绝不把"我刚写过"记账当成事实。
        /// 只有文件里真的有，重启之后重新读出来的结论才对得上；这一条正是旧实现"当场清标记、
        /// 重启又长回来"的根因。</description></item>
        /// <item><description>备注与标记按新事实重算（备注仍停在"手动添加"时才顺手更新，不覆盖用户自己写的）。</description></item>
        /// </list>
        /// <para>刻意**不改 <c>Source</c>、也不改 <c>Value</c>**：来源仍是"手动添加"（它就是手动加的），
        /// 密码本体一个字符都不许动（含首尾空格）。</para>
        /// </summary>
        private void MarkAsWrittenBack(string targetPath, IReadOnlyList<string> writtenValues)
        {
            _passwordService.RememberBookPath(targetPath);

            // 写回本身已经落盘（PasswordService 的落盘开关由它自己管），这里再刷一次值集合。
            _passwordService.RefreshRememberedBookValues();

            var written = new HashSet<string>(writtenValues, StringComparer.Ordinal);

            foreach (PasswordItem item in Passwords)
            {
                if (item == null ||
                    !string.Equals(item.Source, ManualSource, StringComparison.Ordinal) ||
                    !written.Contains(item.Value ?? string.Empty))
                {
                    continue;
                }

                if (string.Equals(item.Remark, DefaultManualRemark, StringComparison.Ordinal))
                {
                    item.Remark = Models.StatusText.PasswordWriteBackDoneRemark;
                }
            }

            RefreshUnwrittenMarkers();
            RefreshStatistics();
        }

        /// <summary>
        /// 定下这次写哪个文件：优先设置里的 <c>PasswordBookPath</c>；
        /// 没有（或那个文件已经不在了）就问一次 —— 用既有的文件对话框服务，不另造一个。
        /// 选完沿「导入 txt」同一条路记住路径，下次启动不必再问。
        /// </summary>
        private string ResolveWriteBackTargetPath()
        {
            AppSettings settings = LoadSettings();
            string configured = settings.PasswordBookPath ?? string.Empty;

            if (configured.Length > 0 && File.Exists(configured))
            {
                return configured;
            }

            if (configured.Length > 0)
            {
                // 路径记着、文件没了（用户搬走/改名了）。说清这件事再问他选哪个，
                // 否则他会以为自己上次根本没选成功。
                SetNotice(
                    PasswordNoticeKind.Warning,
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        Models.StatusText.PasswordWriteBackTargetMissingFormat,
                        Path.GetFileName(configured)));
            }

            string chosen = _dialogService.ShowOpenSingleFileDialog(
                "选择要写回的密码本文件",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*");

            if (string.IsNullOrWhiteSpace(chosen))
            {
                return string.Empty;
            }

            // 与「导入 txt」同一条路：写进 appsettings.json 的 PasswordBookPath + 侧车文件。
            RememberPasswordBookPath(chosen);

            return chosen;
        }

        /// <summary>
        /// 确认框正文：**只有数量与文件名**（正文会被无界面宿主的降级日志记下来）。
        /// 明文清单走 <see cref="BuildWriteBackConfirmDetail"/> 的 Detail 区。
        /// </summary>
        private string BuildWriteBackConfirmMessage(int count, string targetPath)
        {
            var builder = new StringBuilder();

            builder.Append(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Models.StatusText.PasswordWriteBackConfirmFormat,
                count,
                Path.GetFileName(targetPath)));

            builder.AppendLine();
            builder.Append(Models.StatusText.PasswordWriteBackConfirmNote);

            builder.AppendLine();
            builder.Append(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Models.StatusText.PasswordWriteBackAttemptLimitHintFormat,
                LoadSettings().MaxPasswordAttemptsPerLayer));

            return builder.ToString();
        }

        /// <summary>
        /// 确认框 Detail 区：**逐条列出将要写入的密码明文**（用户明确要求，界面上给他看是应该的）。
        /// 它只显示、不进日志；对话框里那块是等宽、可滚动、可复制的。
        /// </summary>
        private static string BuildWriteBackConfirmDetail(IReadOnlyList<string> pending)
        {
            var builder = new StringBuilder();

            builder.AppendLine(Models.StatusText.PasswordWriteBackConfirmDetailHeader);

            for (int i = 0; i < pending.Count; i++)
            {
                builder.AppendLine(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackConfirmDetailItemFormat,
                    i + 1,
                    pending[i]));
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 把写回结论**如实**显示出来：写了几条 / 跳过几条 / 写到哪个文件 / 备份在哪。
        /// 日志里只有数量与文件名 —— <b>绝不出现明文密码</b>（§8 隐私红线）。
        /// </summary>
        private void ReportWriteBackResult(PasswordBookWriteBackResult result, IReadOnlyList<string> pending)
        {
            string fileName = Path.GetFileName(result.TargetPath);

            if (!result.Success)
            {
                Message = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackFailedFormat,
                    result.FailureReason);

                SetNotice(PasswordNoticeKind.Error, Message);
                WriteWriteBackLog(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackLogFailedFormat,
                    result.FailureReason));

                _dialogService.ShowError(Message);
                return;
            }

            if (result.AppendedCount == 0)
            {
                Message = Models.StatusText.PasswordWriteBackStatusNoOp;

                SetNotice(
                    PasswordNoticeKind.Info,
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        Models.StatusText.PasswordWriteBackNothingWrittenFormat,
                        result.SkippedCount,
                        fileName));

                WriteWriteBackLog(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackLogNoOpFormat,
                    fileName,
                    result.SkippedCount));

                return;
            }

            // 写回成功：目标书里现在确实有这些值 —— 重新读文件刷新值集合，标记随之消失
            //（用户一眼能看出"哪些条重启会没"；重启后再查一次，结论不变）。
            MarkAsWrittenBack(result.TargetPath, pending);

            var detail = new StringBuilder();

            detail.Append(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Models.StatusText.PasswordWriteBackBackupLineFormat,
                result.BackupPath));

            string skipped = BuildSkippedSummary(result);

            if (skipped.Length > 0)
            {
                detail.Append(' ').Append(skipped);
            }

            Message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Models.StatusText.PasswordWriteBackStatusFormat,
                result.AppendedCount,
                fileName);

            SetNotice(
                result.SkippedCount > 0 ? PasswordNoticeKind.Warning : PasswordNoticeKind.Success,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackSucceededFormat,
                    result.AppendedCount,
                    fileName,
                    detail.ToString()));

            WriteWriteBackLog(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Models.StatusText.PasswordWriteBackLogFormat,
                result.AppendedCount,
                fileName));

            RefreshStatistics();
        }

        /// <summary>把"跳过了几条、为什么跳"拼成一句（逐项只在真的有数时才出现）。</summary>
        private static string BuildSkippedSummary(PasswordBookWriteBackResult result)
        {
            if (result.SkippedCount == 0)
            {
                return string.Empty;
            }

            var reasons = new List<string>();

            if (result.SkippedEmptyCount > 0)
            {
                reasons.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackSkippedEmptyText,
                    result.SkippedEmptyCount));
            }

            if (result.SkippedBlankCount > 0)
            {
                reasons.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackSkippedBlankText,
                    result.SkippedBlankCount));
            }

            if (result.AlreadyPresentCount > 0)
            {
                reasons.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackSkippedExistingText,
                    result.AlreadyPresentCount));
            }

            if (result.SkippedDuplicateCount > 0)
            {
                reasons.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.PasswordWriteBackSkippedDuplicateText,
                    result.SkippedDuplicateCount));
            }

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Models.StatusText.PasswordWriteBackSkippedFormat,
                result.SkippedCount,
                string.Join("、", reasons));
        }

        /// <summary>
        /// 读设置（只读，不保存）。定位口径与 <see cref="RememberPasswordBookPath"/> 完全一致，见那里的说明。
        /// </summary>
        private AppSettings LoadSettings()
        {
            try
            {
                return CreateSettingsService().Load();
            }
            catch (Exception ex)
            {
                // 设置读不出来不该让写回整条路瘫掉：用一份默认设置继续（它会走"问一次选文件"那条分支）。
                Message += "（提示：设置读取失败，已按默认设置继续：" + ex.Message + "）";

                return AppSettings.CreateDefault();
            }
        }

        /// <summary>构造设置服务（写回与"记住路径"共用，保证两处定位到同一个文件）。</summary>
        private SettingsService CreateSettingsService()
        {
            var pathService = new PathService
            {
                DataRootDirectory = _passwordService.DataRootDirectory
            };

            return new SettingsService(pathService);
        }

        /// <summary>
        /// 写一条写回日志。
        ///
        /// <para>落点与主界面同一个数据根目录（<see cref="PasswordService.DataRootDirectory"/>，
        /// 主界面刻意把它与 <c>PathService.DataRootDirectory</c> 同步成同一个值）。
        /// 传进来的文案**只有数量与文件名**，绝不含密码明文（§8 隐私红线）。</para>
        /// </summary>
        private void WriteWriteBackLog(string message)
        {
            try
            {
                var pathService = new PathService
                {
                    DataRootDirectory = _passwordService.DataRootDirectory
                };

                var logService = new LogService(pathService);

                /*
                 * Initialize 在这里的作用只有一个：把这个 LogService 的"当前日志文件"算出来。
                 * 传 enableFileLog: false 是故意的 —— 不带时间戳的文件名会**把主界面这一天已经写下的日志清空**
                 * （Initialize 首行是 WriteAllText），那等于为了记一条日志毁掉当天的日志。
                 * 关掉它，Write 就走"追加到那个按时间戳命名的文件"，与主界面同一个落点、不互相覆盖。
                 */
                logService.Initialize(enableFileLog: false);

                logService.WriteInfo(message);
            }
            catch
            {
                // 写不出日志不该让"写回"这个动作失败：日志是附带的，文件才是结果。
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
                /*
                 * 密码的值被改过：这条是否"还没写回"要重新算。
                 * 按值的账没变，但比对的对象变了 —— 写回后改成新密码，标记必须重新亮起来
                 * （新值并不在密码本文件里，重启照样丢）。
                 */
                RefreshUnwrittenMarkers();
                RefreshStatistics();
            }
        }

        private void RefreshStatistics()
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

            RaiseCommandStates();
        }

        private void RaiseCommandStates()
        {
            RaiseCanExecuteChanged(AddPasswordCommand);
            RaiseCanExecuteChanged(RemovePasswordCommand);
            RaiseCanExecuteChanged(ClearPasswordsCommand);
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
}
