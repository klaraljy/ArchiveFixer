using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 把旧版本绑在程序目录 / 旧 %AppData% 里的用户数据，一次性迁到固定数据根。
    ///
    /// <para>固定根 = <see cref="PathService.DefaultDataRootDirectory"/>（%LOCALAPPDATA%\ArchiveFixer）。
    /// 只复制“设置 + 密码”，日志/临时文件不迁；⛔ 永远不删除旧文件，也⛔ 不覆盖新文件。</para>
    /// </summary>
    public static class DataRootMigrator
    {
        public static readonly string[] MigratableFileNames =
        {
            "appsettings.json",
            "passwords.txt",
            "passwords.meta.json",
            "password-list.dat"
        };

        /// <summary>执行一次迁移；返回实际复制到的文件路径。</summary>
        public static IReadOnlyList<string> MigrateIfNeeded(
            string programDirectory,
            string destinationRoot,
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

            foreach (string sourceDirectory in EnumerateSourceDirectories(programDirectory))
            {
                if (IsSameDirectory(sourceDirectory, destinationRoot))
                {
                    continue;
                }

                foreach (string fileName in MigratableFileNames)
                {
                    string source = Path.Combine(sourceDirectory, fileName);
                    string destination = Path.Combine(destinationRoot, fileName);

                    if (!File.Exists(source) || File.Exists(destination))
                    {
                        continue;
                    }

                    try
                    {
                        File.Copy(source, destination, overwrite: false);
                        copied.Add(destination);
                        log?.Invoke($"旧数据已迁入固定数据目录：{fileName}");
                    }
                    catch
                    {
                        // 迁移失败不影响启动；用户仍可手工把旧文件放过去。
                    }
                }
            }

            return copied;
        }

        private static IEnumerable<string> EnumerateSourceDirectories(string programDirectory)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(programDirectory))
            {
                string programData = Path.Combine(programDirectory, "data");
                foreach (string directory in AddAll(programData))
                {
                    yield return directory;
                }

                // 最老的版本把 appsettings.json 直接放在程序目录；只作为
                // appsettings 的来源，不把整个程序目录当数据目录。
                foreach (string directory in AddAll(programDirectory))
                {
                    yield return directory;
                }
            }

            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(roaming))
            {
                foreach (string directory in AddAll(Path.Combine(roaming, PathService.DataFolderName)))
                {
                    yield return directory;
                }
            }

            foreach (string directory in AddAll(ReadRegisteredInstallDataDirectory()))
            {
                yield return directory;
            }

            IEnumerable<string> AddAll(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    yield break;
                }

                string? added = null;
                try
                {
                    string full = Path.GetFullPath(path);
                    if (Directory.Exists(full) && seen.Add(full))
                    {
                        added = full;
                    }
                }
                catch
                {
                }

                if (added != null)
                {
                    yield return added;
                }
            }
        }

        private static string? ReadRegisteredInstallDataDirectory()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\ArchiveFixer");
                string? installDir = key?.GetValue("InstallDir") as string;

                return string.IsNullOrWhiteSpace(installDir)
                    ? null
                    : Path.Combine(installDir, "data");
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSameDirectory(string left, string right)
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
