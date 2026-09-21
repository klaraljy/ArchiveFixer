using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Text.Json;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels
{
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
            set => SetProperty(ref _settings, value);
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

                OnPropertyChanged(nameof(Settings));
                Message = "已选择输出目录。";
            }
            catch (Exception ex)
            {
                Message = "选择输出目录失败：" + ex.Message;
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
