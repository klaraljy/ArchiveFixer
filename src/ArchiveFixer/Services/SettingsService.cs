using System;
using System.IO;
using System.Text.Json;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 设置服务。
    /// 负责读取、保存 appsettings.json。
    /// 配置损坏时自动备份并恢复默认设置。
    /// </summary>
    public class SettingsService
    {
        private readonly PathService _pathService;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public SettingsService()
            : this(new PathService())
        {
        }

        public SettingsService(PathService pathService)
        {
            _pathService = pathService;
        }

        /// <summary>
        /// 设置文件路径。
        /// </summary>
        public string SettingsFilePath => _pathService.SettingsFilePath;

        /// <summary>
        /// 加载设置。
        /// </summary>
        public AppSettings Load()
        {
            try
            {
                EnsureSettingsFile();

                string json = File.ReadAllText(SettingsFilePath);

                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);

                if (settings == null)
                {
                    return ResetToDefault();
                }

                settings.Normalize();
                return settings;
            }
            catch
            {
                BackupBrokenSettings();
                return ResetToDefault();
            }
        }

        /// <summary>
        /// 保存设置。
        /// </summary>
        public void Save(AppSettings settings)
        {
            settings ??= CreateDefault();
            settings.Normalize();

            try
            {
                string? directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(settings, _jsonOptions);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // 设置保存失败不能导致主程序崩溃。
                // 这里不抛出异常，由调用方需要时自行记录日志。
            }
        }

        /// <summary>
        /// 创建默认设置。
        /// </summary>
        public AppSettings CreateDefault()
        {
            return AppSettings.CreateDefault();
        }

        /// <summary>
        /// 恢复默认设置并保存。
        /// </summary>
        public AppSettings ResetToDefault()
        {
            AppSettings settings = CreateDefault();
            Save(settings);
            return settings;
        }

        /// <summary>
        /// 确保配置文件存在。
        /// </summary>
        public void EnsureSettingsFile()
        {
            try
            {
                _pathService.EnsureBaseDirectories();

                if (!File.Exists(SettingsFilePath))
                {
                    Save(CreateDefault());
                }
            }
            catch
            {
                // 不能因为配置文件无法创建导致程序启动失败。
            }
        }

        /// <summary>
        /// 备份损坏配置。
        /// </summary>
        private void BackupBrokenSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    return;
                }

                string directory = Path.GetDirectoryName(SettingsFilePath) ?? AppContext.BaseDirectory;
                string brokenPath = Path.Combine(directory, "appsettings.broken.json");

                if (File.Exists(brokenPath))
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    brokenPath = Path.Combine(directory, $"appsettings.broken.{timestamp}.json");
                }

                File.Copy(SettingsFilePath, brokenPath, overwrite: true);
            }
            catch
            {
                // 备份失败也不影响恢复默认设置。
            }
        }
    }
}
