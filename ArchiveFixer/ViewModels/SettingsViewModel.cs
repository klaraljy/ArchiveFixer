using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 输出位置：**两档互斥**（用户 2026-09-24 第 13 条删掉了"摊平"那两档）。
    ///
    /// <para>
    /// 为什么不直接摆一个复选框：产品里"落到哪"本来就是二选一，
    /// 落到设置里仍然是**原来那两个布尔**（不新增设置项）。
    /// 旧版本的四选一里有两档已被用户亲手删除（解压到当前目录 / 直接解到指定目录），
    /// 界面上再也选不到它们；旧配置里剩下的那两个布尔组合由
    /// <see cref="ArchiveFixer.Extraction.OutputPlacement.FromLegacyFlags"/> 迁移到这两档。
    /// </para>
    /// </summary>
    public enum OutputPlacementOption
    {
        /// <summary>以包名命名的子文件夹（111\222.rar → 111\222\内容物）。默认。</summary>
        ArchiveNamedSubfolder,

        /// <summary>指定位置 + 同名子文件夹（→ 333\222\内容物；选中文件夹时用该文件夹的名字）。</summary>
        CustomNamedSubfolder
    }

    /// <summary>
    /// 设置界面上的**一行引擎**（检测状态 + 路径 + 版本 + 是不是当前在用）。
    ///
    /// 为什么要显示这三样（AGENTS.md §3.1：界面显示当前是否在用）：
    /// 用户机器上可能有**两份** UnRAR —— 自己装的 WinRAR 里那份（本机实测 6.11），
    /// 以及我们内置的 <c>tools\unrar\</c>（7.23）。"到底用的哪一个、哪个版本"决定了
    /// 遇到格式支持问题时报什么，也在报告里作为溯源依据（不变量 14）。
    /// 只写一句"已启用"是不够的。
    /// </summary>
    public sealed class EngineOptionItem
    {
        /// <summary>检测状态文案。**不是任务状态**（不进 StatusText / 不参与统计与配色），只是这一页的说明文字。</summary>
        public const string DetectedText = "已检测到";

        /// <summary>没检测到。</summary>
        public const string NotDetectedText = "未检测到";

        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        /// <summary>这个文件是从哪一档找到的（用户自选 / 已装 WinRAR 目录 / 内置）。</summary>
        public string SourceText { get; init; } = string.Empty;

        public bool IsAvailable { get; init; }

        public string StatusText => IsAvailable ? DetectedText : NotDetectedText;

        /// <summary>排在第几位（1 基，给人看）。</summary>
        public int Order { get; init; }

        /// <summary>是不是"当前会真正被用上"的那一个（依据能力 + 优先级算出来，见 EngineSelectionSummary）。</summary>
        public bool IsInUse { get; init; }

        /// <summary>"当前在用"的说明（为空表示这一行当前用不上）。</summary>
        public string UsageText { get; init; } = string.Empty;

        public bool CanMoveUp { get; init; }

        public bool CanMoveDown { get; init; }

        /// <summary>一句话：名字 + 版本 + 状态（列表里第一行文字）。</summary>
        public string HeaderText => IsAvailable
            ? $"{Order}. {DisplayName}　{Version}　{StatusText}"
            : $"{Order}. {DisplayName}　{StatusText}";
    }

    /// <summary>
    /// 设置编辑器：**②解压方式 / ③清理与删除 / ④密码 / ⑥设置**四页共用（2026-09-24 第 11 条之后，
    /// 原「设置窗口」退休，这里就是它的全部逻辑 —— 一行没改）。
    /// 负责展示、修改和恢复默认设置。
    /// </summary>
    public class SettingsViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService;
        private readonly DialogService _dialogService;

        private AppSettings _settings;
        private bool? _dialogResult;
        private string _message = string.Empty;
        private string _engineSelectionSummary = string.Empty;
        private string _rarToolStatusText = string.Empty;

        public AppSettings Settings
        {
            get => _settings;
            set
            {
                if (SetProperty(ref _settings, value))
                {
                    RaiseOutputPlacementChanged();
                }
            }
        }

        /// <summary>
        /// 输出位置（**二选一**）。读写的就是设置里那两个布尔，没有新增设置项。
        /// </summary>
        public OutputPlacementOption OutputPlacement
        {
            get => ResolveOutputPlacement(
                Settings.ExtractToOriginalDirectory,
                Settings.KeepArchiveNameFolder);

            set
            {
                if (value == OutputPlacement)
                {
                    return;
                }

                ApplyOutputPlacement(Settings, value);
                RaiseOutputPlacementChanged();
            }
        }

        /// <summary>
        /// "指定位置"路径。包一层是为了让上面的示例路径跟着输入实时变
        /// （<see cref="AppSettings"/> 不实现 INotifyPropertyChanged，直接绑它不会刷新）。
        /// </summary>
        public string CustomOutputDirectory
        {
            get => Settings.CustomOutputDirectory ?? string.Empty;
            set
            {
                string normalized = value ?? string.Empty;

                if (string.Equals(Settings.CustomOutputDirectory, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                Settings.CustomOutputDirectory = normalized;
                OnPropertyChanged();
                RaiseOutputPlacementChanged();
            }
        }

        /// <summary>只有"解压到指定位置"两种模式才用得上路径输入框。</summary>
        public bool IsCustomOutputEnabled => !Settings.ExtractToOriginalDirectory;

        /// <summary>
        /// ②页「特定解压」那一栏的**全部规则**（按注册表渲染，顺序 = 注册表声明顺序 = 落盘顺序）。
        ///
        /// <para>
        /// ⚠ 这一栏是"可加的"：界面上不写死任何一条规则，加规则时 XAML 一个字都不用改
        /// （有测试钉住：塞一条假描述进注册表 → 这里自动多一条、①页 ToolTip 里也会出现）。
        /// </para>
        /// </summary>
        public ObservableCollection<SpecialExtractionRuleItem> SpecialExtractionRules { get; } = new();

        /// <summary>
        /// ①页那个开关的 ToolTip（用户 2026-09-24 要求："列出当前启用的规则（没有就写去②页挑）"）。
        ///
        /// <para>
        /// 列的是**规则清单里勾上的那些**（不管总开关开没开）—— 用户要能一眼看出
        /// "打开这个开关会发生什么"，而不是打开之后才发现自己上次关掉了全部规则。
        /// </para>
        /// </summary>
        public string SpecialExtractionToolTip
        {
            get
            {
                List<string> names = SpecialExtractionRules
                    .Where(item => item.IsEnabled)
                    .Select(item => item.Name)
                    .ToList();

                return names.Count == 0
                    ? StatusText.SpecialExtractionNoRuleHint
                    : string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.SpecialExtractionToolTipFormat,
                        string.Join("、", names));
            }
        }

        /// <summary>
        /// 把那一栏刷成注册表里的真实内容（构造 / 换设置对象 / 恢复默认之后都要来一遍）。
        ///
        /// <para>
        /// 勾选状态从设置里现读（不猜、不缓存）：<c>RestoreDefault</c> 换掉整个 <see cref="AppSettings"/>
        /// 之后，界面上若还留着上一次的勾，用户就会对着一个**不成立**的状态点保存。
        /// </para>
        /// </summary>
        private void RefreshSpecialExtractionRules()
        {
            SpecialExtractionRules.Clear();

            foreach (SpecialExtractionRule rule in ArchiveFixer.Extraction.SpecialExtractionRules.All)
            {
                SpecialExtractionRules.Add(new SpecialExtractionRuleItem(Settings, rule, OnSpecialExtractionRuleChanged));
            }

            OnPropertyChanged(nameof(SpecialExtractionToolTip));
        }

        private void OnSpecialExtractionRuleChanged()
        {
            OnPropertyChanged(nameof(SpecialExtractionToolTip));
        }

        /// <summary>
        /// 终端落法（规格 §3.1 的可选项）：内容物最里面那一层文件夹叫什么。
        ///
        /// 读写的是设置里的字符串（<see cref="AppSettings.TerminalLayoutMode"/>），
        /// 解析/序列化都走 <see cref="OutputPlacement.ParseTerminalLayoutMode"/> /
        /// <see cref="OutputPlacement.ToSettingValue"/> —— 与解压时的口径是同一份实现。
        ///
        /// 之前这一档**在界面上根本不存在**，解压管线把 KeepLastFolder 写死，
        /// 于是"用压缩包名当最后一层"这个用户可选的行为完全没法选。
        /// </summary>
        public TerminalLayoutMode TerminalLayout
        {
            // ⚠ 必须写全限定名：本类有一个同名的属性 OutputPlacement，写 OutputPlacement.X 会被
            // 解析成"访问那个属性上的成员"（C# 的 color-color 规则），编译期就报错。
            get => ArchiveFixer.Extraction.OutputPlacement.ParseTerminalLayoutMode(Settings.TerminalLayoutMode);
            set
            {
                string stored = ArchiveFixer.Extraction.OutputPlacement.ToSettingValue(value);

                if (string.Equals(Settings.TerminalLayoutMode, stored, StringComparison.Ordinal))
                {
                    return;
                }

                Settings.TerminalLayoutMode = stored;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 源包处理档（决策 D-9，三选一）：源包是移入其余物 / 留在原地 / 校验通过后删除。
        ///
        /// 读写的是设置里的字符串（<see cref="AppSettings.SourceHandling"/>），
        /// 解析/序列化都走 <see cref="AppSettings.ParseSourceHandling"/> /
        /// <see cref="AppSettings.ToSourceHandlingValue"/> —— 与解压时的口径是同一份实现
        /// （与 <see cref="TerminalLayout"/> 同一套写法，避免"界面上选了这个、跑起来是那个"）。
        ///
        /// ⚠ <b>两条路径读的是同一档</b>（用户 2026-09-22 版本二，**推翻**早先
        /// "这一档只影响一键处理、地基路径永远不动源包"的说法）：
        /// 一键处理与手动「只解压」都按它处理源包 —— "成功 + 校验通过 + 未取消 + 属于本任务分卷组"
        /// 四条同时成立才动源包（见 <c>Models/AppSettings.cs</c> 与 <c>docs/输出与整理模型.md</c> §3.4）。
        /// </summary>
        public SourceHandlingMode SourceHandling
        {
            get => AppSettings.ParseSourceHandling(Settings.SourceHandling);
            set
            {
                string stored = AppSettings.ToSourceHandlingValue(value);

                if (string.Equals(Settings.SourceHandling, stored, StringComparison.Ordinal))
                {
                    return;
                }

                Settings.SourceHandling = stored;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 场景 B 塌缩（规格 §3.3，默认**开**）：包基名与所在目录同名、且目录下只有这一个包时，
        /// 去掉重复的一层（<c>111\222\名字\名字.rar</c> → 产物落 <c>111\222\名字\</c>）。
        /// </summary>
        public bool CollapseRepeatedFolderLayer
        {
            get => Settings.CollapseRepeatedFolderLayer;
            set
            {
                if (Settings.CollapseRepeatedFolderLayer == value)
                {
                    return;
                }

                Settings.CollapseRepeatedFolderLayer = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// ②页「特定解压」那一栏里的**一行**（一条规则 = 名称 + 说明 + 开关 + ToolTip）。
        ///
        /// <para>
        /// 用户 2026-09-24 原话："在解压方式里面就可以去添加一个特定解压这一栏，
        /// **也就是以后可能会经常加的东西**"。所以这一行的内容**全部来自注册表**
        /// （<see cref="SpecialExtractionRules.All"/>）：界面里没有一条写死的规则，
        /// 加规则时这个类与 XAML 都不用动。
        /// </para>
        /// </summary>
        public sealed class SpecialExtractionRuleItem : ViewModelBase
        {
            private readonly AppSettings _settings;
            private readonly Action _changed;
            private bool _isEnabled;

            internal SpecialExtractionRuleItem(AppSettings settings, SpecialExtractionRule rule, Action changed)
            {
                _settings = settings;
                _changed = changed;

                Id = rule.Id;
                Name = rule.Name;
                Description = rule.Description;

                // ⚠ 必须写全限定名：本类的宿主（SettingsViewModel）有一个**同名属性**
                // SpecialExtractionRules（②页那一栏的集合），简单名会被解析成那个属性
                //（C# 的 color-color 规则，与 TerminalLayout 那处的坑是同一个）。
                _isEnabled = ArchiveFixer.Extraction.SpecialExtractionRules.IsEnabled(
                    ArchiveFixer.Extraction.SpecialExtractionRules.Normalize(settings.SpecialExtractionRules),
                    rule.Id);
            }

            /// <summary>稳定 Id（落盘的就是它；界面上不显示，只在说明里出现）。</summary>
            public string Id { get; }

            /// <summary>中文名（注册表给的）。</summary>
            public string Name { get; }

            /// <summary>一句说明（注册表给的）。</summary>
            public string Description { get; }

            /// <summary>ToolTip（与说明同一份，别处不再写第二份措辞）。</summary>
            public string ToolTip => Description;

            /// <summary>
            /// 这条规则开不开。写回的是设置里的规则 Id 清单
            /// （<see cref="AppSettings.SpecialExtractionRules"/>），并立刻归一化一次 ——
            /// 于是"界面上的勾"与"落盘的清单"永远是同一个事实。
            /// </summary>
            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    if (_isEnabled == value)
                    {
                        return;
                    }

                    _isEnabled = value;

                    List<string> ids = ArchiveFixer.Extraction.SpecialExtractionRules.Normalize(_settings.SpecialExtractionRules);

                    ids.RemoveAll(id => string.Equals(id, Id, StringComparison.OrdinalIgnoreCase));

                    if (value)
                    {
                        ids.Add(Id);
                    }

                    _settings.SpecialExtractionRules = ArchiveFixer.Extraction.SpecialExtractionRules.Normalize(ids);

                    OnPropertyChanged();
                    _changed();
                }
            }
        }

        /// <summary>
        /// 缓存根目录（日志 / 临时 / 工作区都放它下面）。
        /// 留空 = 程序目录下的 <c>data</c>（默认，跟着安装位置走）；填了必须是绝对路径且**不能是 C 盘**。
        /// </summary>
        public string CacheRootDirectory
        {
            get => Settings.CacheRootDirectory ?? string.Empty;
            set
            {
                string normalized = value ?? string.Empty;

                if (string.Equals(Settings.CacheRootDirectory, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                Settings.CacheRootDirectory = normalized;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 缓存根目录的校验（**必须在保存前过这一关**）。
        ///
        /// 规则来自用户明确要求与规格 §7 决策 D-7：缓存绝不落 C 盘（%AppData% / 系统盘），
        /// 绿色软件跟着安装位置走。留空是允许的（= 程序目录下的 data）。
        ///
        /// 返回 false 时 <paramref name="message"/> 是给用户看的原因与改法 ——
        /// 只说"不合法"用户没法行动，必须说清"该改成什么样"。
        /// </summary>
        public static bool ValidateCacheRootDirectory(string? path, out string message)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                message = string.Empty;
                return true;
            }

            string trimmed = path.Trim();

            if (!Path.IsPathRooted(trimmed))
            {
                message = "缓存根目录必须是绝对路径（例如 D:\\ArchiveFixer-cache）；留空表示用程序目录下的 data。";
                return false;
            }

            // UNC（\\server\share）没有盘符，按"不是 C 盘"处理。
            if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            {
                message = string.Empty;
                return true;
            }

            string? root = null;

            try
            {
                root = Path.GetPathRoot(Path.GetFullPath(trimmed));
            }
            catch
            {
                // 取不到盘根 → 由下面的统一提示拦下。
            }

            if (string.IsNullOrWhiteSpace(root) || root.Length < 1)
            {
                message = $"缓存根目录认不出盘符：{trimmed}。请用形如 D:\\ArchiveFixer-cache 的绝对路径。";
                return false;
            }

            if (char.ToUpperInvariant(root[0]) == 'C')
            {
                message = "缓存不能放在 C 盘（系统盘）：%AppData% 与系统盘都已被明确否掉，缓存要跟着安装位置走。" +
                          "请换一个盘，例如 D:\\ArchiveFixer-cache；留空则用程序目录下的 data。";
                return false;
            }

            message = string.Empty;
            return true;
        }

        /// <summary>
        /// 外部工具路径的校验（**必须在保存前过这一关**）。
        ///
        /// <para>
        /// 为什么不能"先存下来再警告"：<see cref="AppSettings.Normalize"/> 对**不存在的**工具路径
        /// 的处理是**直接清空**（以免留一个失效路径让整个程序找不到引擎）。于是"填了一个不存在的路径
        /// → 点保存 → 看到『设置已保存』→ 关窗"这条路上，用户填的路径被静默丢掉了：
        /// 下次打开设置那一格是空的、真正用的是内置的那一份，而用户以为自己在用自己的那个版本。
        /// </para>
        /// <para>
        /// 校验口径与缓存根目录一致：**拦在保存之前**，停在设置界面上说清"填的这个文件不存在"
        /// 以及"留空是什么行为"，改完再保存。检验的是"文件存不存在"，不是"它是不是 7z" ——
        /// 后者由 <c>ToolLocator</c> / 引擎自己判定，这里越权猜只会误伤。
        /// </para>
        /// </summary>
        public static bool ValidateToolExePath(string? path, string label, string emptyHint, out string message)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                message = string.Empty;
                return true;
            }

            if (File.Exists(path.Trim()))
            {
                message = string.Empty;
                return true;
            }

            message = $"{label}指向的文件不存在：{path.Trim()}。" +
                      $"请改成一个真实存在的文件，或者清空这一格 —— {emptyHint}";
            return false;
        }

        /// <summary>当前落点的一句话说明 + 具体例子。</summary>
        public string OutputPlacementSummary => OutputPlacementSummaryConverter.Describe(
            Settings.ExtractToOriginalDirectory,
            Settings.KeepArchiveNameFolder,
            Settings.CustomOutputDirectory ?? string.Empty);

        /// <summary>
        /// 把编辑器挂到**共享的**设置对象上（选项卡形态：②③④⑥四页共用一个编辑器）。
        ///
        /// <para>
        /// 与构造函数的区别必须说清：构造函数克隆一份设置，好让"整份回退"成为可能
        /// （原设置窗口的「取消」用它；选项卡形态下改用 <see cref="AttachSharedSettings"/> 共享同一份）。
        /// 选项卡里没有那个"关窗即提交/丢弃"的时刻，而且同一项可能在多处出现
        /// （并发档既在输入框里、又决定危险模式凭证盖不盖得住），两份值一定会打架。
        /// 所以这里**不克隆** —— 界面改动即时进入内存，由底栏的「保存设置」负责校验与落盘。
        /// </para>
        /// <para>
        /// 挂了之后要显式通知一轮：<see cref="AppSettings"/> 不实现 INotifyPropertyChanged，
        /// 不通知的话界面还绑在上一份对象上，看起来就是"恢复默认没反应"。
        /// </para>
        /// </summary>
        public void AttachSharedSettings(AppSettings settings)
        {
            _settings = settings ?? _settingsService.CreateDefault();

            OnPropertyChanged(nameof(Settings));
            RefreshRememberedBooks();
            RefreshSpecialExtractionRules();
            RaiseOutputPlacementChanged();
        }

        /// <summary>
        /// 引擎优先级列表（界面上的顺序 = 落盘的顺序，见 <see cref="AppSettings.EnginePriority"/>）。
        ///
        /// 每行带检测状态、路径、版本与"当前在用"标记 —— 排第一但没装时，
        /// 用户要能一眼看出"它没被用上是因为没检测到，不是因为程序坏了"。
        /// </summary>
        public ObservableCollection<EngineOptionItem> Engines { get; } = new();

        /// <summary>
        /// 设置 → 密码设置 →「已记住的密码本」的一行。
        ///
        /// <para>界面上显示**文件名**（可读），完整路径放在 ToolTip 里（用户要核对时才看）——
        /// 与主界面密码本摘要同一口径；而日志里永远只有文件名（§8：个人路径不入日志）。</para>
        /// </summary>
        public sealed class RememberedBookItem
        {
            public string Path { get; init; } = string.Empty;

            public string FileName => System.IO.Path.GetFileName(Path);

            /// <summary>这个文件现在还在不在（不在了也要列出来 —— 静默丢掉一项比列着更让人困惑）。</summary>
            public bool Exists => System.IO.File.Exists(Path);

            public string DisplayName => FileName;

            /// <summary>文件不在了时的补充说明。</summary>
            public string Note => Exists ? string.Empty : "（这个文件现在不在了）";
        }

        /// <summary>"已记住的密码本"列表（顺序 = 启动时的合并顺序）。</summary>
        public ObservableCollection<RememberedBookItem> RememberedBooks { get; } = new();

        /// <summary>记住的密码本一本都没有（界面显示空状态说明，不给用户一个空白框）。</summary>
        public bool HasRememberedBooks => RememberedBooks.Count > 0;

        /// <summary>
        /// 「记住密码列表」开关（读写的是设置里那一个布尔）。
        ///
        /// <para>为什么要绕一层：<see cref="AppSettings"/> 是普通对象（没有 INotifyPropertyChanged），
        /// 直接绑 <c>Settings.RememberPasswordList</c> 的话，勾选不会让下面那段"关掉之后会怎样"的
        /// 说明跟着变 —— 而那句话恰恰是用户最需要看清楚的。走属性就能把通知发出来。</para>
        /// </summary>
        public bool IsRememberingPasswordList
        {
            get => Settings?.RememberPasswordList ?? true;
            set
            {
                if (Settings == null || Settings.RememberPasswordList == value)
                {
                    return;
                }

                Settings.RememberPasswordList = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowRememberedBooksDisabledHint));
            }
        }

        /// <summary>「记住密码列表」关着时要显示那句"这份清单这次不生效"。</summary>
        public bool ShowRememberedBooksDisabledHint => !IsRememberingPasswordList;

        /// <summary>"当前在用哪个引擎"的一句话（按能力 + 优先级算出来的，与真正执行时同一份逻辑）。</summary>
        public string EngineSelectionSummary
        {
            get => _engineSelectionSummary;
            private set => SetProperty(ref _engineSelectionSummary, value ?? string.Empty);
        }

        /// <summary>用户自选的 UnRAR.exe 路径；留空 = 自动（已装 WinRAR 目录 → 内置 tools\unrar）。</summary>
        public string CustomUnRarExePath
        {
            get => Settings.CustomUnRarExePath ?? string.Empty;
            set
            {
                string normalized = value ?? string.Empty;

                if (string.Equals(Settings.CustomUnRarExePath, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                Settings.CustomUnRarExePath = normalized;
                OnPropertyChanged();
                RefreshEngineList();
            }
        }

        /// <summary>
        /// 用户自选的 <c>Rar.exe</c> 路径（**打包**功能做外层 rar 时用；用户 2026-09-23 决定加这一格）。
        ///
        /// <para>留空 = 自动：本机已装 WinRAR 目录里的 <c>Rar.exe</c>，再退 <c>WinRAR.exe</c>。</para>
        ///
        /// <para>⚠ 它**只**填用户自己安装 / 下载的那一份：<c>Rar.exe</c> 是共享软件，
        /// RARLAB 的 EULA 禁止随其它软件包分发（AGENTS.md §3.1），所以程序绝不内置、绝不复制它。
        /// 这一格存在的意义是"用户把 WinRAR 装在非默认目录"时不必去改系统环境变量。</para>
        /// </summary>
        public string CustomRarExePath
        {
            get => Settings.CustomRarExePath ?? string.Empty;
            set
            {
                string normalized = value ?? string.Empty;

                if (string.Equals(Settings.CustomRarExePath, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                Settings.CustomRarExePath = normalized;
                OnPropertyChanged();
                RefreshRarStatus();
            }
        }

        /// <summary>
        /// 工具状态那一行："当前用的是自选的那一份，还是本机已装目录里的那一份"（不变量 14）。
        ///
        /// <para>与 <see cref="RefreshEngineList"/> 同一套做法：拿一个**临时** ToolLocator 预览
        /// "改完之后会怎样"，不碰运行时的全局解析结果（点「取消」不能生效）。</para>
        /// </summary>
        public string RarToolStatusText
        {
            get => _rarToolStatusText;
            private set => SetProperty(ref _rarToolStatusText, value ?? string.Empty);
        }

        /// <summary>那一格的完整说明（**许可边界**写在这里；界面提示与校验失败共用同一份措辞）。</summary>
        public string RarExePathHint => StatusText.SettingsRarExePathHint;

        /// <summary>保留受损文件（-kb，默认关）。只影响半成品留不留，绝不影响成败判定。</summary>
        public bool KeepBrokenFiles
        {
            get => Settings.KeepBrokenFiles;
            set
            {
                if (Settings.KeepBrokenFiles == value)
                {
                    return;
                }

                Settings.KeepBrokenFiles = value;
                OnPropertyChanged();
            }
        }

        public bool? DialogResult
        {
            get => _dialogResult;
            set => SetProperty(ref _dialogResult, value);
        }

        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value ?? string.Empty);
        }

        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ResetDefaultCommand { get; }
        public ICommand SelectOutputDirectoryCommand { get; }

        /// <summary>把某个引擎在优先级列表里上移一位。</summary>
        public ICommand MoveEngineUpCommand { get; }

        /// <summary>把某个引擎在优先级列表里下移一位。</summary>
        public ICommand MoveEngineDownCommand { get; }

        /// <summary>选择"结果归集"的目标目录（M3）。</summary>
        public ICommand SelectCollectTargetDirectoryCommand { get; }

        /// <summary>选择缓存根目录。</summary>
        public ICommand SelectCacheRootDirectoryCommand { get; }

        /// <summary>
        /// 浏览选择用户自己装的 <c>Rar.exe</c>（打包做外层 rar 时用）。
        /// 7z / UnRAR 那两格是纯文本框（历史如此），这一格按用户要求配一个"浏览"按钮 ——
        /// WinRAR 常常装在非默认目录，手打路径最容易出错。
        /// </summary>
        public ICommand SelectRarExeCommand { get; }

        /// <summary>把一本"已记住的密码本"从清单里移除（**只影响自动加载，磁盘上的文件一个字节都不动**）。</summary>
        public ICommand RemoveRememberedBookCommand { get; }

        public SettingsViewModel()
            : this(new AppSettings(), new SettingsService())
        {
        }

        public SettingsViewModel(AppSettings settings, SettingsService settingsService)
        {
            _settingsService = settingsService ?? new SettingsService();
            _dialogService = new DialogService();
            _settings = CloneSettings(settings ?? _settingsService.CreateDefault());

            SaveCommand = new RelayCommand(Save);
            CancelCommand = new RelayCommand(Cancel);
            ResetDefaultCommand = new RelayCommand(ResetDefault);
            SelectOutputDirectoryCommand = new RelayCommand(SelectOutputDirectory);
            SelectCollectTargetDirectoryCommand = new RelayCommand(SelectCollectTargetDirectory);
            SelectCacheRootDirectoryCommand = new RelayCommand(SelectCacheRootDirectory);
            SelectRarExeCommand = new RelayCommand(SelectRarExe);
            RemoveRememberedBookCommand = new RelayCommand(RemoveRememberedBook);
            MoveEngineUpCommand = new RelayCommand(parameter => MoveEngine(parameter, -1));
            MoveEngineDownCommand = new RelayCommand(parameter => MoveEngine(parameter, +1));

            RefreshEngineList();
            RefreshRarStatus();
            RefreshRememberedBooks();
            RefreshSpecialExtractionRules();

            Message = "设置已加载。";
        }

        private void Save()
        {
            try
            {
                /*
                 * 先复位"最近一次保存的结果"。它是**一次**保存的结论，不是历史累计：
                 * 选项卡形态下窗口不再关闭，上一次的成功标记留着会让"这次校验没过"
                 * 被读成"这次成功了"（调用方正是按 DialogResult 决定要不要应用设置的）。
                 */
                DialogResult = null;

                /*
                 * 缓存根目录先校验再保存（不能落到 C 盘）。
                 *
                 * 为什么在这里**拦住**而不是"存下来再警告"：缓存根目录决定工作区落点，
                 * 存进配置之后下一次启动就会照它建目录 —— 用户看到警告时目录已经建好了，
                 * "提示"就变成了既成事实。校验不过就停在设置界面上，改完再保存。
                 */
                if (!ValidateCacheRootDirectory(Settings.CacheRootDirectory, out string cacheMessage))
                {
                    Message = "设置未保存：" + cacheMessage;
                    return;
                }

                /*
                 * 两条外部工具路径同样**拦在保存之前**。
                 *
                 * 为什么必须拦：Normalize() 会把"文件不存在"的工具路径直接清空，
                 * 于是"填错路径 → 保存 → 看到『设置已保存』"是一条**静默丢弃用户输入**的路
                 * （见 ValidateToolExePath 的注释）。宁可停在窗口里说清楚，也不要假装保存成功。
                 */
                if (!ValidateToolExePath(
                        Settings.CustomSevenZipExePath,
                        "7z.exe 路径",
                        "留空表示用程序目录下内置的 tools\\7zip\\7z.exe。",
                        out string sevenZipMessage))
                {
                    Message = "设置未保存：" + sevenZipMessage;
                    return;
                }

                if (!ValidateToolExePath(
                        Settings.CustomUnRarExePath,
                        "UnRAR.exe 路径",
                        "留空表示自动解析：先找本机已装 WinRAR 目录里的 UnRAR.exe，再退回程序内置的 tools\\unrar\\UnRAR.exe。",
                        out string unRarMessage))
                {
                    Message = "设置未保存：" + unRarMessage;
                    return;
                }

                /*
                 * 打包用的 Rar.exe 路径：校验口径与上面两条**完全一致**（同一处实现），
                 * 但"留空"的说明必须把那句话带上 —— 这一格是许可边界最容易被误解的地方：
                 * 它要的是**用户自己安装的** WinRAR 里的那一份，程序只检测与调用、绝不随包分发
                 * （AGENTS.md §3.1）。空白提示直接引 StatusText 的同一份措辞，免得两处各说一套。
                 */
                if (!ValidateToolExePath(
                        Settings.CustomRarExePath,
                        "Rar.exe 路径",
                        StatusText.SettingsRarExePathEmptyHint,
                        out string rarMessage))
                {
                    Message = "设置未保存：" + rarMessage;
                    return;
                }

                /*
                 * 危险模式：**没有自测凭证（或凭证盖不住当前并发档）就不许在设置里打开**
                 * （用户 2026-09-22 的协议）。
                 *
                 * 拦在保存之前而不是"存下来再关回去"：Normalize() 会把"开着但没凭证"的状态静默关掉，
                 * 于是"勾上 → 保存 → 看到『设置已保存』"会变成一条**静默丢弃用户选择**的路
                 * （与工具路径那条同一个口径）。这里直接说清该怎么做，并让用户留在窗口里。
                 *
                 * 为什么连并发档一起判：自测是"拿并发数 × 2 个文件真跑一遍"，凭证只对它跑过的那一档成立。
                 * 把并发调到 8 再勾这个开关，存下来的是一个**开不起来**的组合 ——
                 * 当场说清，比让用户到跑批时才发现"以为在删、其实没删"要好。
                 */
                if (Settings.DangerousSpaceModeEnabled &&
                    !ArchiveFixer.Storage.DangerModeSelfTestStamp.Covers(
                        Settings.DangerModeSelfTestStamp,
                        Settings.MaxParallelExtractCount))
                {
                    string coverage = ArchiveFixer.Storage.DangerModeSelfTestStamp.DescribeCoverage(
                        Settings.DangerModeSelfTestStamp,
                        Settings.MaxParallelExtractCount);

                    Message = "设置未保存："
                              + (string.IsNullOrWhiteSpace(coverage) ? StatusText.DangerModeNeedsSelfTest : coverage);

                    return;
                }

                /*
                 * Normalize() 会把超范围的数字夹回合法区间（例如密码尝试上限 5000 → 1000）。
                 * 静默改掉用户填的数字是"我以为我设成了 5000"的经典来源，所以这里比一下前后值，
                 * 被夹过就在底栏说清楚 —— 用户填错的数字必须看得见。
                 */
                int requestedPasswordAttempts = Settings.MaxPasswordAttemptsPerLayer;

                Settings.Normalize();

                /*
                 * 保存的**同时**把引擎相关的项推给引擎层（优先级 / 两条工具路径 / 保留受损文件）。
                 *
                 * 为什么在这里推：设置界面是用户改这些值的唯一入口，而引擎选择发生在
                 * "下一次点击处理"那一刻 —— 不推的话，用户改完顺序、关掉窗口、立刻处理一个包，
                 * 用的还是旧顺序（"改了没反应"）。这条推送与主窗口的
                 * ApplyEngineSettings 幂等，谁先谁后都不会打架。
                 */
                EngineRuntimeSettings.Apply(Settings);
                RefreshEngineList();
                RefreshRarStatus();

                Message = requestedPasswordAttempts != Settings.MaxPasswordAttemptsPerLayer
                    ? $"设置已保存（每层密码尝试上限 {requestedPasswordAttempts} 超出 1~1000，已按 {Settings.MaxPasswordAttemptsPerLayer} 生效）。"
                    : "设置已保存。";

                DialogResult = true;
            }
            catch (Exception ex)
            {
                Message = "保存设置失败：" + ex.Message;
            }
        }

        private void Cancel()
        {
            Message = "已取消。";
            DialogResult = false;
        }

        private void ResetDefault()
        {
            Settings = _settingsService.CreateDefault();
            RefreshRememberedBooks();
            RefreshSpecialExtractionRules();
            Message = "已恢复默认设置，点击保存后生效。";
        }

        /// <summary>
        /// 把「已记住的密码本」那一组刷成设置里的真实内容（不猜、不缓存）。
        /// 恢复默认设置、以及移除一项之后都要重来一遍，否则界面上会留着已经移除的项。
        /// </summary>
        private void RefreshRememberedBooks()
        {
            RememberedBooks.Clear();

            foreach (string path in Settings?.PasswordBookPaths ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    RememberedBooks.Add(new RememberedBookItem { Path = path });
                }
            }

            OnPropertyChanged(nameof(HasRememberedBooks));
            OnPropertyChanged(nameof(IsRememberingPasswordList));
            OnPropertyChanged(nameof(ShowRememberedBooksDisabledHint));
        }

        /// <summary>
        /// 从清单里移除一本（用户 2026-09-24 要求：已记住的密码本能逐项移除）。
        ///
        /// <para>⚠ 只改**清单**：磁盘上的密码本文件一个字节都不动，界面上的文案也这么写
        /// （用户会担心"移除"是不是把文件删了）。</para>
        /// </summary>
        private void RemoveRememberedBook(object? parameter)
        {
            string path = parameter switch
            {
                RememberedBookItem item => item.Path,
                string text => text,
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(path) || Settings == null)
            {
                return;
            }

            var remaining = new List<string>();

            foreach (string existing in Settings.PasswordBookPaths ?? new List<string>())
            {
                if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                {
                    remaining.Add(existing);
                }
            }

            Settings.PasswordBookPaths = remaining;

            /*
             * 老字段跟着走：Normalize() 会把 PasswordBookPath 并回清单（那是给"回退到旧版本"用的迁移），
             * 不更新它的话，刚移除的这本会在保存时**被迁移逻辑加回来** —— 用户点了移除却还在。
             * 清单空了就一并清空（保存时 Normalize 也不会再补出东西来）。
             */
            Settings.PasswordBookPath = remaining.Count > 0 ? remaining[remaining.Count - 1] : string.Empty;

            RefreshRememberedBooks();

            Message = $"已从「已记住的密码本」里移除「{System.IO.Path.GetFileName(path)}」；点底栏「保存设置」后生效。"
                      + "磁盘上的那个文件不会被删除，也不会被修改。";
        }

        private void SelectOutputDirectory()
        {
            try
            {
                string folder = _dialogService.ShowFolderBrowserDialog();

                if (string.IsNullOrWhiteSpace(folder))
                {
                    Message = "已取消选择输出目录。";
                    return;
                }

                Settings.CustomOutputDirectory = folder;
                Settings.ExtractToOriginalDirectory = false;

                /*
                 * ⚠ 刻意**不建目录**（用户 2026-09-25 第 28 条）：选择位置只是改设置 ——
                 * 挑完又改主意时，盘上不该留下一个空的"指定位置"目录。
                 * 目录只在定稿那一步才建（ExtractionCoordinator 的 stage commit）。
                 */

                // 选了自定义目录 = 切到"指定位置 + 同名子文件夹"那一档（只剩这一档用得上路径）。
                Settings.KeepArchiveNameFolder = true;
                OnPropertyChanged(nameof(Settings));
                OnPropertyChanged(nameof(CustomOutputDirectory));
                RaiseOutputPlacementChanged();

                Message = "已选择输出目录。";
            }
            catch (Exception ex)
            {
                Message = "选择输出目录失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 两个布尔 ↔ 两档的**唯一映射处**（用户 2026-09-24 第 13 条之后只剩两档）。
        ///
        /// <para>
        /// 唯一的判断题是 <c>ExtractToOriginalDirectory</c>（是不是"指定了位置"）。
        /// <c>KeepArchiveNameFolder</c> 已经**不参与判断**（两档都建同名子文件夹）——
        /// 旧的"摊平"组合（<c>true,false</c> / <c>false,false</c>）由设置层的
        /// <see cref="AppSettings.Normalize"/> 迁移掉，界面上再也选不出来。
        /// </para>
        /// </summary>
        public static OutputPlacementOption ResolveOutputPlacement(
            bool extractToOriginalDirectory,
            bool keepArchiveNameFolder)
        {
            _ = keepArchiveNameFolder;

            return extractToOriginalDirectory
                ? OutputPlacementOption.ArchiveNamedSubfolder
                : OutputPlacementOption.CustomNamedSubfolder;
        }

        private static void ApplyOutputPlacement(AppSettings settings, OutputPlacementOption option)
        {
            // 落盘仍是那两个布尔（旧版本也读得懂），但第二个**永远是 true**：没有"不建子文件夹"的档了。
            settings.ExtractToOriginalDirectory = option != OutputPlacementOption.CustomNamedSubfolder;
            settings.KeepArchiveNameFolder = true;
        }

        /// <summary>
        /// 让外面（①「任务」页那一格）也能把落点相关的显示拉回同一份真值（用户 2026-09-25 第 27 条）。
        ///
        /// <para>为什么必须有这个公开入口：①页那个「未指定位置」开关直接写的是
        /// <c>Settings.ExtractToOriginalDirectory</c>（两档落点的唯一判据），
        /// 而②页的单选按钮 / 那把「选择」按钮绑的是本类**算出来**的
        /// <see cref="OutputPlacement"/> / <see cref="IsCustomOutputEnabled"/> / <see cref="OutputPlacementSummary"/> ——
        /// AppSettings 是普通 POCO、不发通知，不喊这一声②页就会继续显示老档位
        /// （"①页勾了、②页没变"正是历史上那类"改了没反应"的形态）。</para>
        ///
        /// <para>它就是 <see cref="RaiseOutputPlacementChanged"/>，语义一致、不另写一套。</para>
        /// </summary>
        public void NotifyOutputPlacementChanged() => RaiseOutputPlacementChanged();

        private void RaiseOutputPlacementChanged()
        {
            OnPropertyChanged(nameof(OutputPlacement));
            OnPropertyChanged(nameof(IsCustomOutputEnabled));
            OnPropertyChanged(nameof(OutputPlacementSummary));
            OnPropertyChanged(nameof(CustomOutputDirectory));

            // 这几个属性是"包在 Settings 外面"的（AppSettings 不实现 INotifyPropertyChanged），
            // 恢复默认 / 重新加载设置之后必须显式通知，否则界面还显示旧值。
            OnPropertyChanged(nameof(TerminalLayout));
            OnPropertyChanged(nameof(CollapseRepeatedFolderLayer));
            OnPropertyChanged(nameof(SourceHandling));
            OnPropertyChanged(nameof(CacheRootDirectory));
            OnPropertyChanged(nameof(CustomUnRarExePath));
            OnPropertyChanged(nameof(CustomRarExePath));
            OnPropertyChanged(nameof(KeepBrokenFiles));

            RefreshEngineList();
            RefreshRarStatus();
        }

        /// <summary>
        /// 重算引擎列表（顺序、检测状态、路径、版本、当前在用）。
        ///
        /// ⚠ 刻意**不**用 <c>ToolLocator.Default</c> 去探测：那是运行时的全局解析结果。
        /// 用户在设置界面改路径、还没点「保存设置」时，界面必须显示"改完之后会怎样"，
        /// 而点「取消」时又不能把这个改动漏到运行时（那正是"取消了却生效了"的经典缺陷）。
        /// 所以这里用一个**临时**的 ToolLocator + 注册表 + 选择器，只做预览，不产生副作用。
        /// </summary>
        private void RefreshEngineList()
        {
            try
            {
                Settings.EnginePriority = EngineIds.Normalize(Settings.EnginePriority);

                var previewTools = new ToolLocator
                {
                    CustomSevenZipExePath = Settings.CustomSevenZipExePath ?? string.Empty,
                    CustomUnRarExePath = Settings.CustomUnRarExePath ?? string.Empty
                };

                EngineRegistry registry = EngineRegistry.CreateDefault(previewTools);
                var selector = new EngineSelector(registry, Settings.EnginePriority);

                EngineSelection forRar = selector.Explain("RAR5", EngineOperation.Extract);
                EngineSelection forGeneric = selector.Explain("ZIP", EngineOperation.Extract);

                /*
                 * ⚠ 列表顺序必须用**设置里那份**优先级，不能用全局运行时那份：
                 * 用户点了「下移」之后、还没点「保存」之前，界面要立刻反映新顺序；
                 * 而全局设置此刻不能动（点「取消」不能生效）。
                 * 实测踩过：这里原来读的是全局 → 点「下移」看着毫无反应。
                 */
                IReadOnlyList<IArchiveEngine> ordered = registry.EnginesInPriorityOrder(Settings.EnginePriority);

                Engines.Clear();

                for (int i = 0; i < ordered.Count; i++)
                {
                    IArchiveEngine engine = ordered[i];

                    bool inUse = (forRar.Selected != null &&
                                  string.Equals(forRar.Selected.Id, engine.Id, StringComparison.OrdinalIgnoreCase))
                                 || (forGeneric.Selected != null &&
                                     string.Equals(forGeneric.Selected.Id, engine.Id, StringComparison.OrdinalIgnoreCase));

                    Engines.Add(new EngineOptionItem
                    {
                        Id = engine.Id,
                        DisplayName = engine.DisplayName,
                        Version = engine.Version,
                        Path = DescribeEnginePath(engine.Id, previewTools),
                        SourceText = DescribeEngineSource(engine.Id, previewTools),
                        IsAvailable = engine.IsAvailable,
                        Order = i + 1,
                        IsInUse = inUse,
                        UsageText = BuildUsageText(engine.Id, forRar, forGeneric),
                        CanMoveUp = i > 0,
                        CanMoveDown = i < ordered.Count - 1
                    });
                }

                EngineSelectionSummary = BuildEngineSelectionSummary(forRar, forGeneric);
            }
            catch (Exception ex)
            {
                // 设置页不该因为"探测引擎"整个打不开：报一句就够，其余设置照常可改。
                EngineSelectionSummary = "引擎检测失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 重算"外层 rar 这一步用的是哪一份 Rar.exe"那一行（用户 2026-09-23 要求能看出
        /// **用的是自选的那份还是已装目录那份**）。
        ///
        /// 与 <see cref="RefreshEngineList"/> 同一口径：临时 ToolLocator，只预览、不产生副作用
        /// （改完路径还没点保存时界面要显示"改完之后会怎样"，点「取消」不能生效）。
        /// </summary>
        private void RefreshRarStatus()
        {
            try
            {
                var previewTools = new ToolLocator
                {
                    CustomRarExePath = Settings.CustomRarExePath ?? string.Empty
                };

                RarToolStatusText = StatusText.SettingsRarExePathStatusPrefix + previewTools.DescribeRarResolution();
            }
            catch (Exception ex)
            {
                // 设置页不该因为探测失败整个打不开：报一句就够，其余设置照常可改。
                RarToolStatusText = StatusText.SettingsRarExePathStatusPrefix + "检测失败：" + ex.Message;
            }
        }

        /// <summary>浏览选择自己装的 <c>Rar.exe</c>。</summary>
        private void SelectRarExe()
        {
            try
            {
                string picked = _dialogService.ShowOpenSingleFileDialog(
                    "选择你自己安装的 WinRAR 里的 Rar.exe",
                    "Rar.exe (Rar.exe)|Rar.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*");

                if (string.IsNullOrWhiteSpace(picked))
                {
                    Message = "已取消选择 Rar.exe。";
                    return;
                }

                CustomRarExePath = picked;

                Message = "已选择 Rar.exe；点底栏「保存设置」后生效。" + StatusText.SettingsRarExePathHint;
            }
            catch (Exception ex)
            {
                Message = "选择 Rar.exe 失败：" + ex.Message;
            }
        }

        private static string DescribeEnginePath(string engineId, ToolLocator tools)
        {
            return string.Equals(engineId, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase)
                ? tools.UnRarExePath
                : tools.SevenZipExePath;
        }

        private static string DescribeEngineSource(string engineId, ToolLocator tools)
        {
            if (string.Equals(engineId, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase))
            {
                if (tools.IsUsingCustomUnRarPath)
                {
                    return "用户自选路径";
                }

                return tools.IsUsingWinRarInstallation ? "本机已装 WinRAR 目录" : "程序内置 tools\\unrar";
            }

            return tools.IsUsingCustomPath ? "用户自选路径" : "程序内置 tools\\7zip";
        }

        private static string BuildUsageText(string engineId, EngineSelection forRar, EngineSelection forGeneric)
        {
            bool rar = forRar.Selected != null &&
                       string.Equals(forRar.Selected.Id, engineId, StringComparison.OrdinalIgnoreCase);

            bool generic = forGeneric.Selected != null &&
                           string.Equals(forGeneric.Selected.Id, engineId, StringComparison.OrdinalIgnoreCase);

            if (rar && generic)
            {
                return "当前在用：所有格式";
            }

            if (rar)
            {
                return "当前在用：RAR 包";
            }

            if (generic)
            {
                return "当前在用：zip / 7z 等其它格式";
            }

            return "当前未参与（能力或优先级排在后面）";
        }

        private static string BuildEngineSelectionSummary(EngineSelection forRar, EngineSelection forGeneric)
        {
            string rar = forRar.Selected == null ? "没有可用引擎" : forRar.Describe();
            string generic = forGeneric.Selected == null ? "没有可用引擎" : forGeneric.Describe();

            string text = $"当前在用：RAR 包 → {rar}；zip / 7z 等其它格式 → {generic}。";

            if (forRar.SkippedUnavailable.Count > 0 || forGeneric.SkippedUnavailable.Count > 0)
            {
                text += " 排在前面的引擎没检测到时会被自动跳过，不会因此打不开包。";
            }

            return text;
        }

        /// <summary>
        /// 上移 / 下移一位。<paramref name="delta"/> 为 -1 上移、+1 下移。
        /// 只改顺序，不增删引擎（列表里没有"删掉某个引擎"这一档）。
        /// </summary>
        private void MoveEngine(object? parameter, int delta)
        {
            if (parameter is not EngineOptionItem item)
            {
                return;
            }

            List<string> priority = EngineIds.Normalize(Settings.EnginePriority);

            int index = priority.FindIndex(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
            int target = index + delta;

            if (index < 0 || target < 0 || target >= priority.Count)
            {
                return;
            }

            string moved = priority[index];
            priority[index] = priority[target];
            priority[target] = moved;

            Settings.EnginePriority = priority;

            RefreshEngineList();

            Message = $"引擎顺序已调整：{string.Join(" → ", priority.Select(DescribeEngineIdForMessage))}（点底栏「保存设置」后生效）";
        }

        private static string DescribeEngineIdForMessage(string id)
        {
            if (string.Equals(id, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase))
            {
                return "UnRAR";
            }

            return string.Equals(id, EngineIds.SevenZip, StringComparison.OrdinalIgnoreCase) ? "7-Zip" : id;
        }

        /// <summary>选择缓存根目录（日志 / 临时 / 工作区都放它下面）。</summary>
        private void SelectCacheRootDirectory()
        {
            try
            {
                string folder = _dialogService.ShowFolderBrowserDialog();

                if (string.IsNullOrWhiteSpace(folder))
                {
                    Message = "已取消选择缓存根目录。";
                    return;
                }

                CacheRootDirectory = folder;

                Message = ValidateCacheRootDirectory(folder, out string reason)
                    ? "已选择缓存根目录；点底栏「保存设置」后生效。"
                    : "这个位置不能用：" + reason;
            }
            catch (Exception ex)
            {
                Message = "选择缓存根目录失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 选择结果归集的目标目录。
        /// 这里**不**动落点开关 —— 归集是解压之后的一步，
        /// 和"解压到哪里"是两件事，顺手改掉会让用户莫名其妙地换了输出位置。
        /// </summary>
        private void SelectCollectTargetDirectory()
        {
            try
            {
                string folder = _dialogService.ShowFolderBrowserDialog();

                if (string.IsNullOrWhiteSpace(folder))
                {
                    Message = "已取消选择归集目录。";
                    return;
                }

                Settings.CollectTargetDirectory = folder;

                OnPropertyChanged(nameof(Settings));
                Message = "已选择归集目标目录。";
            }
            catch (Exception ex)
            {
                Message = "选择归集目录失败：" + ex.Message;
            }
        }

        private static AppSettings CloneSettings(AppSettings source)
        {
            string json = JsonSerializer.Serialize(source);
            AppSettings? cloned = JsonSerializer.Deserialize<AppSettings>(json);

            cloned ??= new AppSettings();
            cloned.Normalize();

            return cloned;
        }
    }
}
