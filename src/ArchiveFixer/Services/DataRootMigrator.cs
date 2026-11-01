using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArchiveFixer.Storage;
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

            /*
             * 迁移**开始前**目标根有没有明文清单：用来区分"真正的新根"（连 txt 都没有，
             * 旧加密记忆可以一起带上）和"已有清单的根"（旧记忆一律不搬，免得把旧列表翻出来）。
             */
            bool destinationHadPasswords = File.Exists(Path.Combine(destinationRoot, "passwords.txt"));

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

                    /*
                     * ⛔ 2026-11-01：目标已经有 passwords.txt 时**不许**再把旧的加密记忆搬过来。
                     * 明文清单是主存储；旧 .dat 可能是很多轮之前的列表（真机就是 1 条测试密码），
                     * 搬进来只会在"哪天 txt 又丢了"时把旧列表翻出来。只有真正的新根（连 txt 都没有）
                     * 才把旧记忆一起带上 —— 那时它是唯一的历史。
                     */
                    if (fileName == "password-list.dat" && destinationHadPasswords)
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

            /*
             * ⛔ 2026-11-01：旧数据**不许复活用户已删除的密码**。
             * 墓碑（Tombstones）在目标根的加密记忆里；有墓碑的值即使旧数据里还有，也不能补回来。
             * 没有这一步时，只要"目标的值全在旧数据里"（典型：用户删掉几条之后），
             * 旧数据整份替换目标 → 删掉的密码全部复活，而且旧 .dat 还会把墓碑一起覆盖掉。
             */
            HashSet<string> tombstones = ReadDestinationTombstones(destinationRoot);

            bool destinationIsSubset = destinationLines.All(bestSourceLines.Contains)
                && !bestSourceLines.Any(tombstones.Contains);

            if (destinationIsSubset)
            {
                // 目标里的值全在旧数据里、且没有已删除的值：直接用旧文件恢复完整顺序。
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
                if (!mergedLines.Contains(line, StringComparer.Ordinal) && !tombstones.Contains(line))
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
                Path.Combine(destinationRoot, "passwords.meta.json"),
                mergedLines);
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

        /// <summary>
        /// 读目标根加密记忆里的墓碑（用户删掉 / 清空过的值）。
        ///
        /// <para>读不出来（文件不在 / 换机器 / 格式不认）⇒ 空集合：此时只按"目标已有值"判断，
        /// 不会因为读不到墓碑就凭空复活——那一步由 <c>destinationIsSubset</c> 的墓碑条件挡住。</para>
        /// </summary>
        private static HashSet<string> ReadDestinationTombstones(string destinationRoot)
        {
            try
            {
                var store = new PasswordListStore { DataRootDirectory = destinationRoot };
                PasswordListLoadResult result = store.Load();

                if (result.HasMemory)
                {
                    return new HashSet<string>(result.Snapshot.Tombstones, StringComparer.Ordinal);
                }
            }
            catch
            {
                // 读不到就当没有墓碑（保守：不额外复活，也不额外覆盖）。
            }

            return new HashSet<string>(StringComparer.Ordinal);
        }

        private static void MergeMetadata(
            string sourcePath,
            string destinationPath,
            IReadOnlyCollection<string>? allowedValues = null)
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

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    /*
                     * ⛔ 只为"最终真的在目标 passwords.txt 里"的值补元数据：
                     * 被墓碑挡住的、或根本没合并进去的值，不能留下一条孤儿备注（下次会跟着列表读出来）。
                     */
                    if (allowedValues != null && !allowedValues.Contains(value!))
                    {
                        continue;
                    }

                    if (existing.Add(value!))
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
