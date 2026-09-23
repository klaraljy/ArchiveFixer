using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
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

        /// <summary>
        /// 记住的密码本路径（**有序**，可多本）—— 用户 2026-09-24 要求多本密码本。
        ///
        /// <para>它与 <see cref="AppSettings.PasswordBookPaths"/> 是一份事实的两处落点：
        /// 设置文件里那份是"用户可以逐项移除"的权威清单，这里的这份负责给出**顺序**、
        /// 并在启动时被加密记忆一起带回来（设置被清掉时还能自愈）。</para>
        /// </summary>
        private readonly List<string> _bookPaths = new();

        /// <summary>
        /// 墓碑：用户删掉 / 清空过的值。
        ///
        /// <para>为什么必须有它：列表在启动时会从密码本 txt 重新合并回来，
        /// 没有墓碑的话"删掉一条 → 重启又活了"，用户会以为删除根本没生效（2026-09-24 同类现象）。</para>
        /// <para>导入**不**记墓碑（导入本身就是"再加回来"）；手动重新添加某个值会**撤销**它的墓碑。</para>
        /// </summary>
        private readonly List<string> _tombstones = new();

        /// <summary>落盘抑制计数（批量操作期间不为每一条写一次文件）。</summary>
        private int _persistSuspensions;

        private string _dataRootDirectory = PathService.DefaultDataRootDirectory;

        /// <summary>最近一次导入密码本时的提醒（编码识别、重复行等），供界面/日志说明情况。</summary>
        public IReadOnlyList<string> LastImportWarnings { get; private set; } = Array.Empty<string>();

        public IReadOnlyList<PasswordEntry> BookEntries => _bookEntries;

        /// <summary>最后一次成功导入的密码本文件路径（用于记住它、下次启动自动加载）。</summary>
        public string LastImportedBookPath { get; private set; } = string.Empty;

        /// <summary>
        /// 密码列表记忆的读写体（DPAPI 机器范围，加密落盘）。
        ///
        /// <para>它的数据根目录**跟着 <see cref="DataRootDirectory"/> 走**（同一个 setter 一起改）：
        /// 用户把缓存挪到别的盘之后，记忆文件也必须跟着挪，否则会出现"写在一个盘、读在另一个盘"。</para>
        /// </summary>
        public PasswordListStore ListStore { get; } = new();

        /// <summary>
        /// 「记住密码列表」总开关（来自 <see cref="AppSettings.RememberPasswordList"/>，默认开）。
        ///
        /// <para>false = **既不写、也不读**记忆文件（一个字节都不动，磁盘上已有的那份不会被删）；
        /// 列表退回"只在本次运行内有效"的老行为。</para>
        /// </summary>
        public bool RememberPasswordList { get; set; } = true;

        /// <summary>启动时由记忆恢复的条数（密码列表窗口上那句"记忆 N 条"用它）。</summary>
        public int RememberedEntryCount { get; private set; }

        /// <summary>
        /// 记忆相关的**一句人话**（读不出来 / 写不下去时的原因）。
        /// 界面在提示条上显示它，**不弹错误框、不阻断**（用户 2026-09-24 要求）。
        /// 空 = 一切正常。
        /// </summary>
        public string LastListWarning { get; private set; } = string.Empty;

        /// <summary>
        /// 记忆出问题时通知界面（只给提示条用）。
        /// 订阅者少一处也不影响正确性 —— 事实本身在 <see cref="LastListWarning"/> 里。
        /// </summary>
        public event Action<string>? RememberedListWarning;

        /// <summary>记住的密码本（有序，副本；改它请走 <see cref="SetRememberedBookPaths"/>）。</summary>
        public IReadOnlyList<string> RememberedBookPaths => _bookPaths;

        /// <summary>墓碑（副本，只读；供测试与排障核对）。</summary>
        public IReadOnlyList<string> Tombstones => _tombstones;

        public PasswordService()
        {
            ListStore.DataRootDirectory = _dataRootDirectory;

            /*
             * 变更即落盘（用户 2026-09-24 要求：内容 / 顺序 / 启用状态 / 手工条目跨重启保留）。
             *
             * 为什么挂在集合的 CollectionChanged 上、而不是逐个改这些方法：
             * 「启用 / 禁用」那两档是界面直接把 PasswordItem.IsEnabled 改掉的（复选框两向绑定），
             * 服务这边没有任何方法入口 —— 只改方法的写法会漏掉这一种，用户勾一下重启就白勾。
             */
            Passwords.CollectionChanged += Passwords_CollectionChanged;
        }

        /// <summary>
        /// 密码列表记忆文件落在哪个目录（沿用既有数据根目录，**不新造路径来源**）。
        ///
        /// 必须与 <see cref="PathService.DataRootDirectory"/> 保持一致：
        /// 之前在导入那边写死 <c>AppContext.BaseDirectory\data</c>，读取那边却看用户配置的缓存根目录，
        /// 用户一旦把缓存挪到别的盘（本项目要求缓存不能落 C 盘），就会"导入了、下次启动又说没配过"。
        /// </summary>
        public string DataRootDirectory
        {
            get => _dataRootDirectory;
            set
            {
                _dataRootDirectory = value ?? string.Empty;

                // 记忆文件与侧车文件共用同一个根：两处分开设过一次就会互相看不见。
                ListStore.DataRootDirectory = _dataRootDirectory;
            }
        }

        // ================================================================
        // 记忆：加载 / 保存 / 墓碑 / 多本密码本
        // ================================================================

        /// <summary>
        /// 启动时加载记忆（条目 + 顺序 + 启用状态 + 墓碑 + 记住的密码本）。
        ///
        /// <para>读失败一律**忽略**：返回原因放进 <see cref="LastListWarning"/>，列表保持空，
        /// 磁盘上那份文件既不被覆盖也不被删（换回原来的机器 / 系统时它还在）。</para>
        /// </summary>
        public PasswordListLoadStatus LoadRememberedList()
        {
            LastListWarning = string.Empty;
            RememberedEntryCount = 0;

            if (!RememberPasswordList)
            {
                return PasswordListLoadStatus.Disabled;
            }

            PasswordListLoadResult result = ListStore.Load();

            if (result.Status == PasswordListLoadStatus.NoFile)
            {
                return result.Status;
            }

            if (!result.HasMemory)
            {
                LastListWarning = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PasswordListMemoryUnavailableFormat,
                    result.FailureReason);

                RememberedListWarning?.Invoke(LastListWarning);
                return result.Status;
            }

            _persistSuspensions++;

            try
            {
                Passwords.Clear();

                foreach (PasswordListEntry entry in result.Snapshot.Entries)
                {
                    Passwords.Add(new PasswordItem
                    {
                        Value = entry.Value ?? string.Empty,
                        Source = entry.Source,
                        IsEnabled = entry.Enabled,
                        Remark = entry.Remark ?? string.Empty
                    });
                }

                _bookPaths.Clear();

                foreach (string book in result.Snapshot.BookPaths)
                {
                    AddBookPathCore(book);
                }

                _tombstones.Clear();
                _tombstones.AddRange(result.Snapshot.Tombstones);
            }
            finally
            {
                _persistSuspensions--;
            }

            RemoveDuplicatePasswordsKeepOrder();

            RememberedEntryCount = Passwords.Count;

            return result.Status;
        }

        /// <summary>
        /// 把"记住的密码本"整份替换掉（设置窗口里移除一项之后由主界面调它）。
        /// 顺序即合并顺序；重复项按 Windows 路径口径（大小写不敏感）去掉。
        /// </summary>
        public void SetRememberedBookPaths(IEnumerable<string>? paths)
        {
            _bookPaths.Clear();

            if (paths != null)
            {
                foreach (string path in paths)
                {
                    AddBookPathCore(path);
                }
            }

            PersistPasswordList();
        }

        /// <summary>记住一本密码本（已经在里面就不重复加，但**顺序不动**）。</summary>
        public void RememberBookPath(string path)
        {
            if (!AddBookPathCore(path))
            {
                return;
            }

            PersistPasswordList();
        }

        /// <summary>
        /// 把一本密码本**合并**进当前列表（启动时逐本调用）。
        ///
        /// <para>合并规则（用户 2026-09-24 定的口径）：
        /// 只补"列表里还没有的值"，**跳过墓碑**（用户删掉过的不许从书里复活），
        /// 新条目按**书的顺序**追加到末尾 —— 于是记忆里的顺序天然保留在前面。</para>
        /// </summary>
        /// <param name="bookPath">密码本 txt 路径。</param>
        /// <param name="parsedCount">这本书解析出多少条（含被跳过的）。</param>
        /// <param name="skippedByTombstone">其中多少条因为墓碑被跳过。</param>
        /// <param name="warnings">解析警告（编码识别、重复行等）。</param>
        /// <returns>真正补进列表的条数。</returns>
        public int MergePasswordBook(
            string bookPath,
            out int parsedCount,
            out int skippedByTombstone,
            out IReadOnlyList<string> warnings)
        {
            parsedCount = 0;
            skippedByTombstone = 0;
            warnings = Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(bookPath) || !File.Exists(bookPath))
            {
                return 0;
            }

            PasswordBookParseResult parsed = PasswordBookParser.ParseFile(bookPath);

            warnings = parsed.Warnings;
            parsedCount = parsed.Entries.Count;

            var existing = new HashSet<string>(
                Passwords.Select(item => item.Value ?? string.Empty),
                StringComparer.Ordinal);

            var tombstones = new HashSet<string>(_tombstones, StringComparer.Ordinal);

            int added = 0;

            _persistSuspensions++;

            try
            {
                foreach (PasswordEntry entry in parsed.Entries)
                {
                    string value = entry.Password ?? string.Empty;

                    if (tombstones.Contains(value))
                    {
                        // 用户明确删过这个值：书里还留着也不许我给他加回来（否则"删了又活"）。
                        skippedByTombstone++;
                        continue;
                    }

                    if (!existing.Add(value))
                    {
                        continue;
                    }

                    Passwords.Add(new PasswordItem
                    {
                        Value = value,
                        Source = "ImportedList",
                        IsEnabled = true,
                        Remark = BuildMergedRemark(entry)
                    });

                    added++;
                }

                // 映射式命中要能按包名匹配到**所有**已加载的书（以前只留最后一本的，多本时等于白读）。
                // 同一个包被合并两次（主界面多次触发自动加载）时按"名称 + 密码"去重，别让它翻倍。
                foreach (PasswordEntry entry in parsed.Entries)
                {
                    bool known = _bookEntries.Any(existing =>
                        string.Equals(existing.Password, entry.Password, StringComparison.Ordinal) &&
                        string.Equals(existing.Name, entry.Name, StringComparison.Ordinal));

                    if (!known)
                    {
                        _bookEntries.Add(entry);
                    }
                }
            }
            finally
            {
                _persistSuspensions--;
            }

            if (added > 0)
            {
                PersistPasswordList();
            }

            return added;
        }

        /// <summary>
        /// 把一份快照立刻写进记忆（用户点保存、或测试要一个确定的状态时用）。
        ///
        /// <para>平时不用手工调它：每次变更都会自动落盘。</para>
        /// </summary>
        public PasswordListSaveResult SaveRememberedList()
        {
            if (!RememberPasswordList)
            {
                return new PasswordListSaveResult(false, "「记住密码列表」已关闭", ListStore.FilePath);
            }

            return ListStore.Save(BuildSnapshot());
        }

        /// <summary>记一条墓碑（值被用户删掉 / 清空）。</summary>
        public void RecordTombstone(string? value)
        {
            value ??= string.Empty;

            if (!_tombstones.Contains(value, StringComparer.Ordinal))
            {
                _tombstones.Add(value);
            }
        }

        /// <summary>撤销墓碑（用户又把这个值加回来了：导入与手动添加都算）。</summary>
        public void ForgetTombstone(string? value)
        {
            value ??= string.Empty;
            _tombstones.RemoveAll(existing => string.Equals(existing, value, StringComparison.Ordinal));
        }

        private static string BuildMergedRemark(PasswordEntry entry)
        {
            if (entry.Kind == PasswordEntryKind.Mapped && !string.IsNullOrWhiteSpace(entry.Name))
            {
                return $"密码本：{entry.Name}";
            }

            return $"密码本（第 {entry.LineNumber} 行）";
        }

        private bool AddBookPathCore(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string trimmed = path.Trim();

            foreach (string existing in _bookPaths)
            {
                if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            _bookPaths.Add(trimmed);

            return true;
        }

        private PasswordListSnapshot BuildSnapshot()
        {
            var snapshot = new PasswordListSnapshot();

            foreach (PasswordItem item in Passwords)
            {
                if (item == null)
                {
                    continue;
                }

                snapshot.Entries.Add(new PasswordListEntry
                {
                    Value = item.Value ?? string.Empty,
                    Source = item.Source ?? string.Empty,
                    Enabled = item.IsEnabled,
                    Remark = item.Remark ?? string.Empty
                });
            }

            snapshot.BookPaths.AddRange(_bookPaths);
            snapshot.Tombstones.AddRange(_tombstones);

            return snapshot;
        }

        /// <summary>
        /// 变更之后落盘。关掉开关时**一个字节都不写**；写失败时只留一句人话（界面提示条用），
        /// 绝不抛异常 —— 记不住密码列表不该让"加一条密码"这个动作失败。
        /// </summary>
        private void PersistPasswordList()
        {
            if (_persistSuspensions > 0 || !RememberPasswordList)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(ListStore.DataRootDirectory))
            {
                return;
            }

            PasswordListSaveResult result = ListStore.Save(BuildSnapshot());

            if (result.Success)
            {
                return;
            }

            LastListWarning = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordListMemorySaveFailedFormat,
                result.FailureReason);

            RememberedListWarning?.Invoke(LastListWarning);
        }

        private void Passwords_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (PasswordItem item in e.OldItems)
                {
                    item.PropertyChanged -= PasswordItem_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (PasswordItem item in e.NewItems)
                {
                    item.PropertyChanged -= PasswordItem_PropertyChanged;
                    item.PropertyChanged += PasswordItem_PropertyChanged;
                }
            }

            // 增 / 删 / 清空 / 上下移（Move 也是集合变更）统统落一次盘：顺序就是尝试顺序，必须留住。
            PersistPasswordList();
        }

        private void PasswordItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            /*
             * 启用开关与备注是**界面直接改**的（没有走服务的方法），所以这里必须跟着落盘。
             * 值（Value）也一起管：用户改了一条密码的值，重启后当然该是改过的那个。
             */
            if (e.PropertyName is nameof(PasswordItem.IsEnabled)
                or nameof(PasswordItem.Value)
                or nameof(PasswordItem.Remark)
                or nameof(PasswordItem.Source))
            {
                PersistPasswordList();
            }
        }

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

            /*
             * 导入 = "再加回来"（用户 2026-09-24 的口径）：所以
             * ① 导入**不记墓碑** —— 用户是主动把这本书读进来的；
             * ② 书里出现的值**撤销墓碑** —— 他删过、现在又导进来，说明他要留着它。
             */
            _persistSuspensions++;

            try
            {
                foreach (PasswordEntry entry in parsed.Entries)
                {
                    string remark = entry.Kind == PasswordEntryKind.Mapped && !string.IsNullOrWhiteSpace(entry.Name)
                        ? $"导入：{entry.Name}"
                        : $"导入（第 {entry.LineNumber} 行）";

                    ForgetTombstone(entry.Password);

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
            }
            finally
            {
                _persistSuspensions--;
            }

            // 这本书从此"已记住"（不重复加；顺序按第一次导入的先后）。
            AddBookPathCore(txtPath);

            PersistPasswordList();

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

            _persistSuspensions++;

            try
            {
                // 用户重新加回一个删过的值 = 他改主意了：墓碑要撤掉，否则下次启动又被书里的旧值吃掉。
                ForgetTombstone(password);

                Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ManualList",
                    IsEnabled = true,
                    Remark = "手动添加"
                });

                RemoveDuplicatePasswordsKeepOrder();
            }
            finally
            {
                _persistSuspensions--;
            }

            PersistPasswordList();

            return true;
        }

        public bool RemovePassword(PasswordItem item)
        {
            if (item == null)
            {
                return false;
            }

            /*
             * 先记墓碑、再删。
             *
             * 顺序不能反：删除会让集合变更处理器立刻落盘，而墓碑必须在那一次落盘里就位 ——
             * 反过来的话，落盘的是一份"这条没了、墓碑也没有"的记忆，重启时密码本把它原样加回来，
             * 用户看到的就是"删了又活"。
             */
            RecordTombstone(item.Value);

            return Passwords.Remove(item);
        }

        public void ClearPasswords()
        {
            // 清空同理：**所有**被清掉的值都要进墓碑，否则重启整份密码本又回来了。
            foreach (PasswordItem item in Passwords)
            {
                RecordTombstone(item?.Value);
            }

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

            // Move 也是集合变更 → 变更处理器会立刻落盘（顺序就是尝试顺序，必须跨重启保留）。
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
