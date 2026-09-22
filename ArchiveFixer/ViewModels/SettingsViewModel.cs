using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 输出位置：四种互斥的落点。
    ///
    /// 为什么不直接摆两个复选框：产品里"落到哪"本来就是四选一，
    /// 用「解压到原目录」+「保留同名文件夹」两个独立开关表达，
    /// 用户得自己在脑子里做组合，还很容易选出想要的那一种（用户原话"根本看不懂输出的哪"）。
    /// 这里把它还原成四选一，落到设置里仍然是**原来那两个布尔**，不新增设置项。
    /// </summary>
    public enum OutputPlacementOption
    {
        /// <summary>解压到以压缩包名命名的子文件夹（111\222.rar → 111\222\内容物）。默认。</summary>
        ArchiveNamedSubfolder,

        /// <summary>解压到压缩包所在目录（111\222.rar → 111\内容物）。</summary>
        SourceDirectory,

        /// <summary>解压到指定位置，并建立同名子文件夹（→ 333\222\内容物）。</summary>
        CustomNamedSubfolder,

        /// <summary>解压到指定位置，直接放在该目录下（→ 333\内容物）。</summary>
        CustomFlat
    }

    /// <summary>
    /// 设置窗口 ViewModel。
    /// 负责展示、修改和恢复默认设置。
    /// </summary>
    public class SettingsViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService;
        private readonly DialogService _dialogService;

        private AppSettings _settings;
        private bool? _dialogResult;
        private string _message = string.Empty;

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
        /// 输出位置（四选一）。读写的就是设置里那两个布尔，没有新增设置项。
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
        /// 源包处理档（决策 D-9，三选一）：一键处理里源包是移入其余物 / 留在原地 / 校验通过后删除。
        ///
        /// 读写的是设置里的字符串（<see cref="AppSettings.SourceHandling"/>），
        /// 解析/序列化都走 <see cref="AppSettings.ParseSourceHandling"/> /
        /// <see cref="AppSettings.ToSourceHandlingValue"/> —— 与解压时的口径是同一份实现
        /// （与 <see cref="TerminalLayout"/> 同一套写法，避免"界面上选了这个、跑起来是那个"）。
        ///
        /// ⚠ 这一档**只影响一键处理**；手动「只解压」是地基路径，永远不动源包。
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

        /// <summary>当前落点的一句话说明 + 具体例子。</summary>
        public string OutputPlacementSummary => OutputPlacementSummaryConverter.Describe(
            Settings.ExtractToOriginalDirectory,
            Settings.KeepArchiveNameFolder,
            Settings.CustomOutputDirectory ?? string.Empty);

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

        /// <summary>选择"结果归集"的目标目录（M3）。</summary>
        public ICommand SelectCollectTargetDirectoryCommand { get; }

        /// <summary>选择缓存根目录。</summary>
        public ICommand SelectCacheRootDirectoryCommand { get; }

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

            Message = "设置已加载。";
        }

        private void Save()
        {
            try
            {
                /*
                 * 缓存根目录先校验再保存（不能落到 C 盘）。
                 *
                 * 为什么在这里**拦住**而不是"存下来再警告"：缓存根目录决定工作区落点，
                 * 存进配置之后下一次启动就会照它建目录 —— 用户看到警告时目录已经建好了，
                 * "提示"就变成了既成事实。校验不过就停在设置窗口里，改完再保存。
                 */
                if (!ValidateCacheRootDirectory(Settings.CacheRootDirectory, out string cacheMessage))
                {
                    Message = "设置未保存：" + cacheMessage;
                    return;
                }

                /*
                 * Normalize() 会把超范围的数字夹回合法区间（例如密码尝试上限 5000 → 1000）。
                 * 静默改掉用户填的数字是"我以为我设成了 5000"的经典来源，所以这里比一下前后值，
                 * 被夹过就在底栏说清楚 —— 用户填错的数字必须看得见。
                 */
                int requestedPasswordAttempts = Settings.MaxPasswordAttemptsPerLayer;

                Settings.Normalize();

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
            Message = "已恢复默认设置，点击保存后生效。";
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

                // 选了自定义目录 = 切到"解压到指定位置"那一档；
                // 保留用户原来对"要不要同名子文件夹"的选择（KeepArchiveNameFolder 不动）。
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
        /// 两个布尔 ↔ 四选一的唯一映射处。
        /// </summary>
        public static OutputPlacementOption ResolveOutputPlacement(
            bool extractToOriginalDirectory,
            bool keepArchiveNameFolder)
        {
            if (extractToOriginalDirectory)
            {
                return keepArchiveNameFolder
                    ? OutputPlacementOption.ArchiveNamedSubfolder
                    : OutputPlacementOption.SourceDirectory;
            }

            return keepArchiveNameFolder
                ? OutputPlacementOption.CustomNamedSubfolder
                : OutputPlacementOption.CustomFlat;
        }

        private static void ApplyOutputPlacement(AppSettings settings, OutputPlacementOption option)
        {
            switch (option)
            {
                case OutputPlacementOption.SourceDirectory:
                    settings.ExtractToOriginalDirectory = true;
                    settings.KeepArchiveNameFolder = false;
                    break;

                case OutputPlacementOption.CustomNamedSubfolder:
                    settings.ExtractToOriginalDirectory = false;
                    settings.KeepArchiveNameFolder = true;
                    break;

                case OutputPlacementOption.CustomFlat:
                    settings.ExtractToOriginalDirectory = false;
                    settings.KeepArchiveNameFolder = false;
                    break;

                default:
                    settings.ExtractToOriginalDirectory = true;
                    settings.KeepArchiveNameFolder = true;
                    break;
            }
        }

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
                    ? "已选择缓存根目录；点「保存」后生效。"
                    : "这个位置不能用：" + reason;
            }
            catch (Exception ex)
            {
                Message = "选择缓存根目录失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 选择结果归集的目标目录。
        /// 这里**不**动"解压到压缩包所在目录"开关 —— 归集是解压之后的一步，
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
