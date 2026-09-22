using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
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

            Message = "设置已加载。";
        }

        private void Save()
        {
            try
            {
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
