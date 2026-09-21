using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Services
{
    public class PasswordService
    {
        public ObservableCollection<PasswordItem> Passwords { get; } = new();

        /// <summary>密码本里的全部条目（含映射式的"名称"，用于按归档名匹配）。</summary>
        private readonly List<PasswordEntry> _bookEntries = new();

        /// <summary>每个归档最近一次成功的密码：同一个包重试时先试它，省掉一轮无谓试错。</summary>
        private readonly Dictionary<string, string> _recentSuccess = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>最近一次导入密码本时的提醒（编码识别、重复行等），供界面/日志说明情况。</summary>
        public IReadOnlyList<string> LastImportWarnings { get; private set; } = Array.Empty<string>();

        public IReadOnlyList<PasswordEntry> BookEntries => _bookEntries;

        /// <summary>记录某个归档刚刚用哪个密码成功过（只在内存里，不落盘）。</summary>
        public void RecordPasswordSuccess(string? archivePath, string? password)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return;
            }

            _recentSuccess[archivePath.Trim()] = password ?? string.Empty;
        }

        /// <summary>按归档文件名匹配密码本里的映射式条目（命中优先于遍历整表）。</summary>
        public IReadOnlyList<PasswordEntry> MatchMappedEntries(string? archiveFileName)
        {
            if (string.IsNullOrWhiteSpace(archiveFileName) || _bookEntries.Count == 0)
            {
                return Array.Empty<PasswordEntry>();
            }

            return PasswordBookParser.MatchByName(_bookEntries, archiveFileName);
        }

        /// <summary>
        /// 组装某个任务的密码候选。
        ///
        /// 顺序按 AGENTS.md §9.2 定死，不要随意调换：
        /// 空密码 → 本任务最近成功 → 密码本映射命中 → 单任务密码 → 统一密码 → 密码列表 → 同目录说明文件
        ///
        /// 为什么要这个顺序：越靠前的越可能命中、也越"便宜"（不用用户等）；
        /// 旁路说明文件是猜的，放最后，避免把说明文件里的一句噪声排到用户明确给的密码前面。
        /// </summary>
        public List<PasswordItem> GetPasswordCandidates(
            ArchiveTask task,
            string globalPassword,
            IEnumerable<PasswordItem> passwordList,
            bool tryEmptyFirst,
            bool includeSidecarCandidates = false)
        {
            var result = new List<PasswordItem>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            void AddCandidate(string value, string source, string remark)
            {
                value ??= string.Empty;

                if (!used.Add(value))
                {
                    return;
                }

                result.Add(new PasswordItem
                {
                    Value = value,
                    Source = source,
                    IsEnabled = true,
                    Remark = remark
                });
            }

            string archivePath = task?.CurrentPath ?? string.Empty;
            string archiveFileName = string.IsNullOrWhiteSpace(archivePath)
                ? string.Empty
                : Path.GetFileName(archivePath);

            if (tryEmptyFirst)
            {
                AddCandidate(string.Empty, "Empty", "空密码");
            }

            if (!string.IsNullOrWhiteSpace(archivePath) &&
                _recentSuccess.TryGetValue(archivePath.Trim(), out string? recentPassword))
            {
                AddCandidate(recentPassword ?? string.Empty, "RecentSuccess", "本任务最近成功的密码");
            }

            foreach (PasswordEntry entry in MatchMappedEntries(archiveFileName))
            {
                string remark = string.IsNullOrWhiteSpace(entry.Name)
                    ? "密码本命中"
                    : $"密码本命中：{entry.Name}";

                AddCandidate(entry.Password, "BookMapped", remark);
            }

            if (task != null && !string.IsNullOrEmpty(task.Password))
            {
                AddCandidate(task.Password, "TaskPassword", "单任务密码");
            }

            if (!string.IsNullOrEmpty(globalPassword))
            {
                AddCandidate(globalPassword, "GlobalPassword", "统一密码");
            }

            if (passwordList != null)
            {
                int index = 0;

                foreach (PasswordItem item in passwordList)
                {
                    index++;

                    if (item == null)
                    {
                        continue;
                    }

                    if (!item.IsEnabled)
                    {
                        continue;
                    }

                    /*
                     * 注意：
                     * 密码允许是空格开头、空格结尾。
                     * 不要 Trim。
                     */
                    string value = item.Value ?? string.Empty;

                    AddCandidate(value, "ImportedList", $"密码列表第 {index} 项");
                }
            }

            /*
             * 同目录说明文件里的密码：**显式开启才用**。
             * 它是"猜"出来的候选，所以排在用户明确给的密码之后（AGENTS.md §9.4）。
             */
            if (includeSidecarCandidates && !string.IsNullOrWhiteSpace(archivePath))
            {
                foreach (SidecarCandidate candidate in SidecarPasswordReader.ReadCandidates(archivePath))
                {
                    string fileName = Path.GetFileName(candidate.SourceFile);

                    AddCandidate(
                        candidate.Password,
                        "Sidecar",
                        $"同目录说明文件 {fileName} 第 {candidate.LineNumber} 行");
                }
            }

            /*
             * 如果用户关闭了空密码优先，并且没有任何密码，
             * 仍然补一个空密码，保证普通无密码压缩包能解压。
             */
            if (result.Count == 0)
            {
                AddCandidate(string.Empty, "Empty", "空密码");
            }

            return result;
        }

        /// <summary>
        /// 导入密码本文件。
        ///
        /// 两种写法都支持（AGENTS.md §9.1）：
        /// - 列表式：一行一个密码，<c>#</c> 开头是注释
        /// - 映射式：<c>名称:密码</c> / <c>名称：密码</c>
        ///
        /// 映射式的条目**也放进界面列表**（否则用户导入完看到列表是空的会以为失败），
        /// 但它的"名称"额外留在密码本里，用于按归档名做命中匹配。
        /// 密码一律不 Trim —— 密码可能真的带首尾空格。
        /// </summary>
        public List<PasswordItem> ImportPasswordList(string txtPath)
        {
            var imported = new List<PasswordItem>();

            if (string.IsNullOrWhiteSpace(txtPath) || !File.Exists(txtPath))
            {
                LastImportWarnings = new[] { "密码本文件不存在，未导入任何内容。" };
                return imported;
            }

            PasswordBookParseResult parsed = PasswordBookParser.ParseFile(txtPath);

            LastImportWarnings = parsed.Warnings;

            _bookEntries.Clear();
            _bookEntries.AddRange(parsed.Entries);

            foreach (PasswordEntry entry in parsed.Entries)
            {
                string remark = entry.Kind == PasswordEntryKind.Mapped && !string.IsNullOrWhiteSpace(entry.Name)
                    ? $"导入：{entry.Name}"
                    : $"导入（第 {entry.LineNumber} 行）";

                var item = new PasswordItem
                {
                    Value = entry.Password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = remark
                };

                Passwords.Add(item);
                imported.Add(item);
            }

            RemoveDuplicatePasswordsKeepOrder();

            return imported;
        }

        public bool AddPassword(string password)
        {
            password ??= string.Empty;

            if (Passwords.Any(x =>
                    string.Equals(x.Value, password, StringComparison.Ordinal)))
            {
                return false;
            }

            Passwords.Add(new PasswordItem
            {
                Value = password,
                Source = "ManualList",
                IsEnabled = true,
                Remark = "手动添加"
            });

            RemoveDuplicatePasswordsKeepOrder();

            return true;
        }

        public bool RemovePassword(PasswordItem item)
        {
            if (item == null)
            {
                return false;
            }

            return Passwords.Remove(item);
        }

        public void ClearPasswords()
        {
            Passwords.Clear();
        }

        public bool MoveUp(PasswordItem item)
        {
            if (item == null)
            {
                return false;
            }

            int index = Passwords.IndexOf(item);

            if (index <= 0)
            {
                return false;
            }

            Passwords.Move(index, index - 1);

            return true;
        }

        public bool MoveDown(PasswordItem item)
        {
            if (item == null)
            {
                return false;
            }

            int index = Passwords.IndexOf(item);

            if (index < 0 || index >= Passwords.Count - 1)
            {
                return false;
            }

            Passwords.Move(index, index + 1);

            return true;
        }

        public string MaskPassword(string password)
        {
            return PasswordMasker.Mask(password);
        }

        public string BuildTryPasswordLogText(PasswordItem candidate, int index)
        {
            if (candidate == null)
            {
                return $"尝试密码候选第 {index} 项：******";
            }

            string source = candidate.Source ?? string.Empty;

            return source switch
            {
                "Empty" => "尝试空密码",
                "RecentSuccess" => "尝试本任务最近成功的密码：******",
                "BookMapped" => "尝试密码本命中项：******",
                "TaskPassword" => "尝试单任务密码：******",
                "GlobalPassword" => "尝试统一密码：******",
                "ImportedList" => $"尝试密码列表第 {index} 项：******",
                "ManualList" => $"尝试手动密码第 {index} 项：******",
                "Sidecar" => "尝试同目录说明文件里的密码：******",
                _ => $"尝试密码候选第 {index} 项：******"
            };
        }

        private void RemoveDuplicatePasswordsKeepOrder()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<PasswordItem>();

            foreach (PasswordItem item in Passwords)
            {
                string value = item.Value ?? string.Empty;

                if (used.Add(value))
                {
                    unique.Add(item);
                }
            }

            if (unique.Count == Passwords.Count)
            {
                return;
            }

            Passwords.Clear();

            foreach (PasswordItem item in unique)
            {
                Passwords.Add(item);
            }
        }
    }
}
