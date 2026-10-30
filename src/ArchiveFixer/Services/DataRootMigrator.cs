using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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

            var sourceDirectories = EnumerateSourceDirectories(programDirectory)
                .Where(directory => !IsSameDirectory(directory, destinationRoot))
                .ToList();

            foreach (string sourceDirectory in sourceDirectories)
            {
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

            MergePasswordsIfSourceIsRicher(sourceDirectories, destinationRoot, copied, log);
            return copied;
        }

        /// <summary>
        /// 旧密码本可能比新固定的多很多条（2026-10-30 真机：旧 data 13 条、
        /// 新根只剩 1 条，导致原来能解的包全部变成“文件名已加密”）。
        /// 这里按“值”做一次保守合并：目标已有的值绝不丢，旧数据多出来的值补进去。
        /// </summary>
        private static void MergePasswordsIfSourceIsRicher(
            IReadOnlyList<string> sourceDirectories,
            string destinationRoot,
            List<string> copied,
            Action<string>? log)
        {
            string destinationPasswords = Path.Combine(destinationRoot, "passwords.txt");

            if (!File.Exists(destinationPasswords))
            {
                return;
            }

            List<string> destinationLines = ReadPasswordLines(destinationPasswords);
            string? bestSourceDirectory = null;
            List<string> bestSourceLines = destinationLines;

            foreach (string sourceDirectory in sourceDirectories)
            {
                string sourcePasswords = Path.Combine(sourceDirectory, "passwords.txt");

                if (!File.Exists(sourcePasswords))
                {
                    continue;
                }

                List<string> sourceLines = ReadPasswordLines(sourcePasswords);

                if (sourceLines.Count > bestSourceLines.Count)
                {
                    bestSourceDirectory = sourceDirectory;
                    bestSourceLines = sourceLines;
                }
            }

            if (bestSourceDirectory == null)
            {
                return;
            }

            bool destinationIsSubset = destinationLines.All(bestSourceLines.Contains);

            if (destinationIsSubset)
            {
                // 目标里的值全在旧数据里：直接用旧文件恢复完整顺序。
                BackupFile(destinationPasswords);
                File.Copy(Path.Combine(bestSourceDirectory, "passwords.txt"), destinationPasswords, overwrite: true);

                string sourceMeta = Path.Combine(bestSourceDirectory, "passwords.meta.json");
                string destinationMeta = Path.Combine(destinationRoot, "passwords.meta.json");

                if (File.Exists(sourceMeta))
                {
                    BackupFile(destinationMeta);
                    File.Copy(sourceMeta, destinationMeta, overwrite: true);
                }

                string sourceEncrypted = Path.Combine(bestSourceDirectory, "password-list.dat");
                string destinationEncrypted = Path.Combine(destinationRoot, "password-list.dat");

                if (File.Exists(sourceEncrypted))
                {
                    BackupFile(destinationEncrypted);
                    File.Copy(sourceEncrypted, destinationEncrypted, overwrite: true);
                }

                copied.Add(destinationPasswords);
                log?.Invoke("旧密码本已补全（目标里的密码都在旧数据中，按旧顺序恢复）。");
                return;
            }

            // 目标有旧数据里没有的新密码：保留目标顺序，再补旧数据里缺的值。
            List<string> mergedLines = new(destinationLines);

            foreach (string line in bestSourceLines)
            {
                if (!mergedLines.Contains(line, StringComparer.Ordinal))
                {
                    mergedLines.Add(line);
                }
            }

            if (mergedLines.Count == destinationLines.Count)
            {
                return;
            }

            BackupFile(destinationPasswords);
            File.WriteAllLines(destinationPasswords, mergedLines);
            MergeMetadata(
                Path.Combine(bestSourceDirectory, "passwords.meta.json"),
                Path.Combine(destinationRoot, "passwords.meta.json"));
            copied.Add(destinationPasswords);
            log?.Invoke("旧密码本已补全（保留新密码，补齐旧数据里缺的值）。");
        }

        private static List<string> ReadPasswordLines(string path)
        {
            try
            {
                return File.ReadAllLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void MergeMetadata(string sourcePath, string destinationPath)
        {
            try
            {
                if (!File.Exists(sourcePath) || !File.Exists(destinationPath))
                {
                    return;
                }

                JsonArray? source = JsonNode.Parse(File.ReadAllText(sourcePath)) as JsonArray;
                JsonArray? destination = JsonNode.Parse(File.ReadAllText(destinationPath)) as JsonArray;

                if (source == null || destination == null)
                {
                    return;
                }

                var existing = new HashSet<string>(
                    destination
                        .Select(node => node?["Value"]?.GetValue<string>())
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!),
                    StringComparer.Ordinal);

                foreach (JsonNode? node in source)
                {
                    string? value = node?["Value"]?.GetValue<string>();

                    if (!string.IsNullOrWhiteSpace(value) && existing.Add(value!))
                    {
                        destination.Add(node!.DeepClone());
                    }
                }

                BackupFile(destinationPath);
                File.WriteAllText(
                    destinationPath,
                    destination.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 密码本文件本身已经合并；元数据失败不应阻止启动。
            }
        }

        private static void BackupFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                string backup = path + ".bak-before-merge";

                if (!File.Exists(backup))
                {
                    File.Copy(path, backup, overwrite: false);
                }
            }
            catch
            {
            }
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
