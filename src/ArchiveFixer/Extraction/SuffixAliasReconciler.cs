using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 后缀清洗产生“标准名”后，KeepInPlace 收尾要把标准名还回用户原名。
    /// 判据只认文件身份（卷序列号 + File ID），不按路径/名字猜；返回真正动过的别名对。
    /// </summary>
    public static class SuffixAliasReconciler
    {
        public static IReadOnlyList<(string Original, string Renamed)> Reconcile(
            IEnumerable<(string Original, string Renamed)> aliases,
            Func<string, string?> backupFor,
            Action<string>? log = null)
        {
            var changed = new List<(string Original, string Renamed)>();

            foreach ((string original, string renamed) in (aliases ?? Array.Empty<(string, string)>()).Distinct())
            {
                if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(renamed))
                {
                    continue;
                }

                if (!File.Exists(renamed))
                {
                    continue;
                }

                string? backup = backupFor?.Invoke(original);

                if (string.IsNullOrWhiteSpace(backup)
                    || !File.Exists(backup)
                    || !FileIdentity.IsSamePhysicalFile(backup, renamed))
                {
                    log?.Invoke($"{Path.GetFileName(original)}：后缀清洗别名与保护快照不是同一份文件，保留现状不删");
                    continue;
                }

                try
                {
                    if (!File.Exists(original))
                    {
                        string? directory = Path.GetDirectoryName(original);

                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        FileOperations.Move(renamed, original);
                        changed.Add((original, renamed));
                        log?.Invoke($"{Path.GetFileName(original)}：后缀清洗用过的标准名已收回原名（源字节不动）");
                    }
                    else if (FileIdentity.IsSamePhysicalFile(original, renamed))
                    {
                        FileOperations.Delete(renamed);
                        changed.Add((original, renamed));
                        log?.Invoke($"{Path.GetFileName(original)}：标准名与原名是同一份文件，已收掉多余的标准名");
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"{Path.GetFileName(original)}：后缀清洗别名收口失败：{ex.Message}");
                }
            }

            return changed;
        }
    }
}
