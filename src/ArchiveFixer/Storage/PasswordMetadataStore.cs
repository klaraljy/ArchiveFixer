using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// passwords.txt 的附属元数据：只存启用状态与备注，不重复存密码。
    /// 密码值仍以 passwords.txt 为唯一来源；这里读不出 / 对不上时回落启用、备注留空。
    /// </summary>
    public sealed class PasswordMetadataStore
    {
        public const string FileName = "passwords.meta.json";

        public string DataRootDirectory { get; set; } = string.Empty;

        public string FilePath => string.IsNullOrWhiteSpace(DataRootDirectory)
            ? string.Empty
            : Path.Combine(DataRootDirectory, FileName);

        public bool Exists => FilePath.Length > 0 && File.Exists(FilePath);

        public bool TryLoad(out Dictionary<string, PasswordMetadataEntry> entries, out string failureReason)
        {
            entries = new Dictionary<string, PasswordMetadataEntry>(StringComparer.Ordinal);
            failureReason = string.Empty;

            if (!Exists)
            {
                return false;
            }

            try
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                List<PasswordMetadataEntry>? list =
                    JsonSerializer.Deserialize<List<PasswordMetadataEntry>>(json);

                if (list == null)
                {
                    failureReason = "元数据为空";
                    return false;
                }

                foreach (PasswordMetadataEntry entry in list)
                {
                    if (string.IsNullOrEmpty(entry.Value))
                    {
                        continue;
                    }

                    entries[entry.Value] = entry;
                }

                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                entries.Clear();
                return false;
            }
        }

        public bool TrySave(IEnumerable<PasswordMetadataEntry> entries, out string failureReason)
        {
            failureReason = string.Empty;

            string path = FilePath;

            if (path.Length == 0)
            {
                failureReason = "数据根目录为空";
                return false;
            }

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                Directory.CreateDirectory(DataRootDirectory);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Copy(path, path + ".bak", overwrite: true);
                    }
                    catch
                    {
                    }
                }

                string json = JsonSerializer.Serialize(
                    entries.ToList(),
                    new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
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
    }

    public sealed class PasswordMetadataEntry
    {
        public string Value { get; set; } = string.Empty;

        public bool IsEnabled { get; set; } = true;

        public string Remark { get; set; } = string.Empty;
    }
}
