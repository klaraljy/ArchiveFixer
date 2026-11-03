using System;
using System.IO;
using System.Text;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 旧设置的一次性迁移（阶段 6 影子）：版本 + once + 备份 + 失败不改原文件。
    /// 真正的迁移规则仍在 <see cref="AppSettings.Normalize"/>；本类负责“只跑一次”的边界。
    /// </summary>
    public static class LegacySettingsMigration
    {
        public const int CurrentVersion = 1;

        public const string VersionFileName = "settings.migration.version";

        public sealed class Result
        {
            public bool Ran { get; init; }

            public bool Succeeded { get; init; }

            public int FromVersion { get; init; }

            public int ToVersion { get; init; }

            public string BackupPath { get; init; } = string.Empty;

            public string FailureReason { get; init; } = string.Empty;
        }

        public static Result EnsureMigrated(
            AppSettings settings,
            string settingsFilePath,
            string versionDirectory)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            string versionPath = Path.Combine(versionDirectory ?? string.Empty, VersionFileName);

            if (ReadVersion(versionPath) >= CurrentVersion)
            {
                return new Result { Ran = false, Succeeded = true, ToVersion = CurrentVersion };
            }

            string backupPath = string.Empty;

            try
            {
                if (!string.IsNullOrWhiteSpace(versionDirectory))
                {
                    Directory.CreateDirectory(versionDirectory);
                }

                if (File.Exists(settingsFilePath))
                {
                    backupPath = settingsFilePath + ".migration-backup";
                    File.Copy(settingsFilePath, backupPath, overwrite: true);
                }

                settings.Normalize();

                string temp = versionPath + ".tmp";
                File.WriteAllText(temp, CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false));
                File.Move(temp, versionPath, overwrite: true);

                return new Result
                {
                    Ran = true,
                    Succeeded = true,
                    FromVersion = 0,
                    ToVersion = CurrentVersion,
                    BackupPath = backupPath
                };
            }
            catch (Exception ex)
            {
                return new Result
                {
                    Ran = true,
                    Succeeded = false,
                    FromVersion = 0,
                    ToVersion = 0,
                    BackupPath = backupPath,
                    FailureReason = ex.Message
                };
            }
        }

        private static int ReadVersion(string path)
        {
            try
            {
                return File.Exists(path)
                    && int.TryParse(File.ReadAllText(path), out int version)
                    ? version
                    : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
