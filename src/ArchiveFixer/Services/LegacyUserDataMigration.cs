using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 把旧版本留在 <c>%APPDATA%\ArchiveFixer</c> 或安装目录 <c>data\</c> 里的
    /// 设置/密码一次性补到固定数据根。
    ///
    /// <para>只补**目标不存在**的文件，⛔ 不覆盖当前设置/密码；日志、temp、备份不迁。
    /// 只在目标根没有迁移标记时执行一次；旧文件保留不删，用户可自行清理。</para>
    /// </summary>
    internal static class LegacyUserDataMigration
    {
        internal const string VersionFileName = "legacy-user-data.migration.version";
        internal const int CurrentVersion = 1;

        private static readonly string[] MigratableFiles =
        {
            "appsettings.json",
            "passwords.txt",
            "passwords.meta.json",
            "password-list.dat",
            "password-book.path"
        };

        internal static IReadOnlyList<string> MigrateIfNeeded(
            string programDirectory,
            string destinationRoot,
            string? legacyRoamingDirectory = null,
            Action<string>? log = null)
        {
            var copied = new List<string>();

            if (string.IsNullOrWhiteSpace(destinationRoot))
            {
                return copied;
            }

            try
            {
                Directory.CreateDirectory(destinationRoot);
            }
            catch
            {
                return copied;
            }

            string versionPath = Path.Combine(destinationRoot, VersionFileName);
            if (ReadVersion(versionPath) >= CurrentVersion)
            {
                return copied;
            }

            var sources = new List<string>();

            if (!string.IsNullOrWhiteSpace(programDirectory))
            {
                sources.Add(Path.Combine(programDirectory, "data"));
            }

            string roaming = legacyRoamingDirectory
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    PathService.DataFolderName);

            if (!string.IsNullOrWhiteSpace(roaming))
            {
                sources.Add(roaming);
            }

            foreach (string sourceDirectory in sources)
            {
                if (string.IsNullOrWhiteSpace(sourceDirectory)
                    || !Directory.Exists(sourceDirectory)
                    || PathsEqual(sourceDirectory, destinationRoot))
                {
                    continue;
                }

                foreach (string fileName in MigratableFiles)
                {
                    string source = Path.Combine(sourceDirectory, fileName);
                    string destination = Path.Combine(destinationRoot, fileName);

                    if (!File.Exists(source) || File.Exists(destination))
                    {
                        continue;
                    }

                    try
                    {
                        CopyAtomically(source, destination);
                        copied.Add(fileName);
                        log?.Invoke($"旧数据已补入固定数据目录：{fileName}");
                    }
                    catch
                    {
                        // 迁移失败不阻断启动；目标没有的文件留给用户手工处理。
                    }
                }
            }

            WriteVersion(versionPath);
            return copied;
        }

        private static void CopyAtomically(string source, string destination)
        {
            string temp = destination + ".legacy-migration-tmp";

            try
            {
                File.Copy(source, temp, overwrite: true);
                if (new FileInfo(source).Length != new FileInfo(temp).Length)
                {
                    throw new IOException("迁移临时文件长度不一致");
                }

                File.Move(temp, destination, overwrite: false);
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                }
            }
        }

        private static void WriteVersion(string path)
        {
            try
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, CurrentVersion.ToString());
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                // 标记写不进去不影响已经补入的文件；下次会再尝试。
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

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
