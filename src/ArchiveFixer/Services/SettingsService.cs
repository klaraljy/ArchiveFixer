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
        /// 把一份设置序列化成落盘用的那段 JSON（**自动保存的"变了没有"判据**就用它：
        /// 与上一次写进去的那份逐字符比，不同才写 —— 用户 2026-09-26 要的"改了就自动存"）。
        ///
        /// <para>⛔ 序列化只有这一处实现：<see cref="Save"/> 也用它，否则"比较用的字符串"
        /// 与"真正写下去的字符串"会漂移（那就会出现"每次都觉得变了、每跳一次写一次盘"）。</para>
        /// </summary>
        public string Serialize(AppSettings? settings)
        {
            settings ??= CreateDefault();
            settings.Normalize();

            return JsonSerializer.Serialize(settings, _jsonOptions);
        }

        /// <summary>
        /// 保存设置。
        ///
        /// <para>返回值 = **真的写进磁盘了没有**（2026-09-26）：以前它把写盘异常整个吞掉、对外返回 void，
        /// 于是每个调用点都只能写一句"保存失败"的兜底 catch（那些 catch 永远不会被触发），
        /// 而"到底存下来没有"谁也答不上来 —— 自动保存要如实告诉用户"这次存没存"，
        /// 就必须让这一层说实话。异常仍然不往上抛（设置保存失败不该让主程序崩）。</para>
        /// </summary>
        /// <returns>写成功 true；磁盘只读 / 被占用等失败 false（调用方按需记日志或提示）。</returns>
        public bool Save(AppSettings settings)
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

                string json = Serialize(settings);
                File.WriteAllText(SettingsFilePath, json);
                return true;
            }
            catch
            {
                // 设置保存失败不能导致主程序崩溃；由调用方按返回值决定怎么告诉用户。
                return false;
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
