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

        /// <summary>最后一次成功导入的密码本文件路径（用于记住它、下次启动自动加载）。</summary>
        public string LastImportedBookPath { get; private set; } = string.Empty;

        /// <summary>
        /// "记住上次导入的密码本"这个侧车文件写在哪个目录。
        ///
        /// 必须与 <see cref="PathService.DataRootDirectory"/> 保持一致：
        /// 之前在导入这边写死 <c>AppContext.BaseDirectory\data</c>，读取那边却看用户配置的缓存根目录，
        /// 用户一旦把缓存挪到别的盘（本项目要求缓存不能落 C 盘），就会"导入了、下次启动又说没配过"。
        /// </summary>
        public string DataRootDirectory { get; set; } = PathService.DefaultDataRootDirectory;

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

            LastImportedBookPath = txtPath;

            /*
             * 把路径写盘 —— 挂在"导入"这个动作上，而不是挂在某个窗口上。
             * 用户从「密码列表」窗口导入、还是从「工具 → 导入密码本…」导入，都要记住；
             * 之前只在菜单那条路上存了设置，用户走窗口那条路就白导了。
             */
            try
            {
                string dataRoot = DataRootDirectory;
                Directory.CreateDirectory(dataRoot);
                File.WriteAllText(Path.Combine(dataRoot, "password-book.path"), txtPath, new UTF8Encoding(false));
            }
            catch
            {
                // 记不住路径不该让导入本身失败。
            }

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
                // 一键处理/批量开始前那一次询问得到的密码（ExtractionCoordinator.ManualPasswordSource）：
                // 以前它走 default 分支，日志里写成"密码候选第 N 项"，看不出这个密码是用户当场给的
                // （只对本次运行有效、不落盘）。文案里说清来源，排障时才知道该去哪儿改。
                "ManualBatch" => "尝试本次运行手动输入的密码：******",
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
