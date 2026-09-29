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
        /// 每条密码成功解开过多少次（用户 2026-09-24 第 14 条："密码本里面的优先级差不多就是
        /// 使用次数的频繁程度"）。**大小写敏感**（与密码本解析 / 去重口径一致：密码就是字节）。
        ///
        /// <para>它有两处用处：① 同层级的候选按它**从多到少**排；② 「记住密码列表」开着时随列表
        /// 一起加密落盘（<see cref="PasswordListSnapshot.SuccessCounts"/>），重启后优先级不丢。</para>
        /// <para>⚠ 并行解压时多个任务会同时调 <see cref="RecordPasswordSuccess"/>，
        /// 所以对它的读写一律在 <see cref="_passwordStatsLock"/> 里 —— <c>Dictionary</c> 不是线程安全的。</para>
        /// </summary>
        private readonly Dictionary<string, int> _successCounts = new(StringComparer.Ordinal);

        /// <summary>
        /// 本次运行里**已经成功试出来过**的密码（顺序 = 首次成功的先后）。
        ///
        /// <para>用户原话："如果测密码，只要测到一个成功了就不要去再尝试了，你现在每次都尝试，
        /// 如果是密码本里面的第 5 个，如果是 200 个压缩包，那你岂不是要尝试 1000 遍"。
        /// 这些值会被提到每个后续任务的候选**最前面**（空密码之后），于是 N 个同源包从 O(N×5)
        /// 降到 O(N)。</para>
        ///
        /// <para>⛔ 只**提前**、绝不**删减**：不同包的密码可能不同，原来的候选链在后面原样保留
        /// （用户 2026-09-24 第 14 条的要求）。只记内存、不落盘 —— 它是"本批"的事实，
        /// 跨重启的优先级由成功次数承担。</para>
        /// </summary>
        private readonly List<string> _batchVerifiedPasswords = new();

        /// <summary>成功次数与"本批已成功"清单的锁（并行解压会从多个线程记账）。</summary>
        private readonly object _passwordStatsLock = new();

        /// <summary>记忆文件写入的串行锁（见 <see cref="PersistPasswordList"/> 的说明）。</summary>
        private readonly object _persistGate = new();

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

        /// <summary>
        /// 每本已记住的密码本**解析出来的值集合**（键 = 书路径，Windows 口径大小写不敏感）。
        ///
        /// <para><b>它解决哪一个现象</b>（用户 2026-09-24 第 21 条原话："上面显示添加成功了，为什么重启了一下，
        /// 又显示未添加，你到底是不是保存在本地的"）：以前「未写回」标记只看
        /// <see cref="PasswordItem.Source"/> 是不是 <c>ManualList</c>，而落盘的只有**来源** ——
        /// "这条值到底在不在密码本文件里"这个事实既没被记录、也没被重新判定。
        /// 写回成功只是当场把内存里的账记了一笔，重启后账没了、来源又恢复成 <c>ManualList</c>，
        /// 标记于是原样长回来。现在判据改成**按值**：值出现在任何一本已记住的书里，才算已写回。</para>
        ///
        /// <para><b>为什么不合并成一个全局集合</b>：用户在设置里移除一本书之后，那本书里的值就
        /// 不再算"已写回"了；按书记账才能把移除的那一本单独丢掉，不必为此重新读所有文件。</para>
        ///
        /// <para><b>诚实原则</b>：这里只放"真的从文件里读出来的值"。没有任何已记住的书、
        /// 书不存在、读不出来 —— 都**不放**任何东西，标记照旧亮着（绝不假装已写回）。</para>
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _bookValuesByPath =
            new(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// 已记住的密码本里一共读到过多少个不同的值（排障与测试用；读不到的书一个都不算）。
        ///
        /// <para>只数"当前还在记住清单里"的书：用户移除一本书之后，它的值不该再被算进来
        /// （否则那条目会一直显示"已写回"，而实际上它已经不在任何一本被记住的书里了）。</para>
        /// </summary>
        public int RememberedBookValueCount
        {
            get
            {
                var values = new HashSet<string>(StringComparer.Ordinal);

                foreach (string path in _bookPaths)
                {
                    if (_bookValuesByPath.TryGetValue(path, out HashSet<string>? known))
                    {
                        values.UnionWith(known);
                    }
                }

                return values.Count;
            }
        }

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
                /*
                 * 读不出来（换机器 / 重装系统 / 文件被改坏）：**先把原件另存一份备份，再往下走**。
                 *
                 * ⚠ 老做法只写一句"已忽略、文件没被动"就返回，而下一句"程序没有覆盖、也没有删除那份记忆"
                 * 是**假的**：启动接线随后就会按设置里的密码本清单落盘一次，Save 是替换式的 ——
                 * 那份解不开的记忆当场被覆盖，用户却因为这句话放心了（2026-09-29 复核逮到）。
                 * 现在：原件不动，额外留一份 `<文件名>.unreadable-<时间戳>.bak`，文案如实点名它。
                 */
                string warning = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PasswordListMemoryUnavailableFormat,
                    result.FailureReason);

                if (ListStore.TryBackupUnreadableFile(out string backupFileName, out string backupError))
                {
                    warning += string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PasswordListMemoryBackedUpFormat,
                        backupFileName);
                }
                else
                {
                    warning += string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PasswordListMemoryBackupFailedFormat,
                        backupError);
                }

                LastListWarning = warning;

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

                /*
                 * 成功次数（v2 载荷）跟着记忆一起回来 —— 用户"按使用频率排序"的期望跨重启成立。
                 * 旧版本写下的文件（v1）里没有这一项 → 空表，一切按原顺序（不报错、不猜）。
                 */
                lock (_passwordStatsLock)
                {
                    _successCounts.Clear();

                    foreach (KeyValuePair<string, int> pair in result.Snapshot.SuccessCounts)
                    {
                        _successCounts[pair.Key] = pair.Value;
                    }
                }
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
        /// 把"记住的密码本"整份替换掉（④「密码」页里移除一项之后由主界面调它）。
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

            // 被移除的那些书，它的值账要一起丢掉：不然"值在某本书里"会一直算数，
            // 而用户刚刚在设置里把那本书删掉了 —— 那正是"显示已写回、其实带不走"的假象。
            PruneBookValues();

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
        /// 这个值是不是**确实躺在某一本已记住的密码本里**（用户 2026-09-24 第 21 条的判据）。
        ///
        /// <para>纯事实查询：只认从文件里解析出来的值。没有任何已记住的书、书不存在、
        /// 书读不出来、值是空串 —— 一律返回 false（调用方据此保持「未写回」标记，不假装已写回）。</para>
        /// </summary>
        public bool IsValueInRememberedBooks(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (string path in _bookPaths)
            {
                if (_bookValuesByPath.TryGetValue(path, out HashSet<string>? known) && known.Contains(value))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// **重新读一遍**所有已记住的密码本来重建值集合，返回其中真正读到的本书。
        ///
        /// <para>什么时候必须调它：「写回密码本」成功之后 —— 值刚刚被追加进文件，只有重新读文件
        /// 才知道它真的在里面了（⛔ 不许把"我刚写过"当成事实记一笔，那正是重启后标记复活的旧病根）。
        /// 读不到的书整本丢掉旧账，绝不保留它上一次读到的值。</para>
        /// </summary>
        public int RefreshRememberedBookValues()
        {
            _bookValuesByPath.Clear();

            int readableBooks = 0;

            foreach (string path in _bookPaths.ToList())
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                PasswordBookParseResult parsed;

                try
                {
                    parsed = PasswordBookParser.ParseFile(path);
                }
                catch (Exception)
                {
                    // 读不出来 = 这本书现在提供不了任何事实（权限、被占用、编码彻底认不出…）。
                    // 吞掉异常是刻意的：它只影响一个显示标记，绝不配让"写回"这个动作失败或崩掉。
                    continue;
                }

                RecordBookValues(path, parsed.Entries);
                readableBooks++;
            }

            return readableBooks;
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

            /*
             * 合并这一步顺手把"这本书里有哪些值"记下来（用户 2026-09-24 第 21 条）：
             * 启动时逐本合并正是"加载每本书的值集合"那一刻，而这里的解析结果**现成**，
             * 不必再读一遍文件。判"未写回"标记时问的就是这份事实。
             */
            RecordBookValues(bookPath, parsed.Entries);

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

        /// <summary>
        /// 把一本书这次解析出来的值**整份替换**进 <see cref="_bookValuesByPath"/>。
        ///
        /// <para>为什么是"整份替换"而不是"逐条追加"：用户手改过密码本（删掉几行）之后，
        /// 上一次读到的值不能继续算数 —— 否则那条目会一直显示"已写回"，而文件里其实已经没有了。</para>
        ///
        /// <para>含被墓碑跳过的值：墓碑管的是"要不要放进尝试列表"，与"文件里有没有这个值"是两件事。</para>
        /// </summary>
        private void RecordBookValues(string? bookPath, IEnumerable<PasswordEntry>? entries)
        {
            if (string.IsNullOrWhiteSpace(bookPath))
            {
                return;
            }

            var values = new HashSet<string>(StringComparer.Ordinal);

            if (entries != null)
            {
                foreach (PasswordEntry entry in entries)
                {
                    if (entry != null && !string.IsNullOrEmpty(entry.Password))
                    {
                        values.Add(entry.Password);
                    }
                }
            }

            _bookValuesByPath[bookPath.Trim()] = values;
        }

        /// <summary>把"已经不在记住清单里"的书的值账丢掉（用户移除一本书之后）。</summary>
        private void PruneBookValues()
        {
            if (_bookValuesByPath.Count == 0)
            {
                return;
            }

            var stale = _bookValuesByPath.Keys
                .Where(path => !_bookPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (string path in stale)
            {
                _bookValuesByPath.Remove(path);
            }
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

            // 成功次数（第 14 条的"按使用频率排序"）跟着列表一起落盘；并行解压会在别的线程记账，所以取副本要加锁。
            lock (_passwordStatsLock)
            {
                foreach (KeyValuePair<string, int> pair in _successCounts)
                {
                    snapshot.SuccessCounts[pair.Key] = pair.Value;
                }
            }

            return snapshot;
        }

        /// <summary>
        /// 变更之后落盘。关掉开关时**一个字节都不写**；写失败时只留一句人话（界面提示条用），
        /// 绝不抛异常 —— 记不住密码列表不该让"加一条密码"这个动作失败。
        ///
        /// <para>
        /// ⚠ 整个过程串行化（<see cref="_persistGate"/>）：并行解压时多个任务会同时
        /// <see cref="RecordPasswordSuccess"/>，而记忆文件是**原子写 + 整体替换**的（同一个 <c>.tmp</c>），
        /// 两个线程一起写会互相把临时文件搬走，一方报"写失败"（内容不会坏，但会平白多一句失败提示）。
        /// </para>
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

            PasswordListSaveResult result;

            lock (_persistGate)
            {
                result = ListStore.Save(BuildSnapshot());
            }

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

            lock (_passwordStatsLock)
            {
                _recentSuccess[archivePath.Trim()] = password ?? string.Empty;
            }

            RecordVerifiedPassword(password);
        }

        /// <summary>
        /// 一条密码刚刚被验证成功：记进"本批已成功"清单（供后续任务复用）并累加成功次数。
        ///
        /// <para>
        /// ⚠ 空密码不算"一条密码"（它是 §9.2 的第一档"不需要密码"，本来就是每个包的第一步），
        /// 所以不记账、也不进复用清单 —— 否则"复用"这一档会变成一句"再试一次空密码"。
        /// </para>
        /// </summary>
        private void RecordVerifiedPassword(string? password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return;
            }

            lock (_passwordStatsLock)
            {
                _successCounts[password] = _successCounts.TryGetValue(password, out int count) ? count + 1 : 1;

                if (!_batchVerifiedPasswords.Contains(password, StringComparer.Ordinal))
                {
                    _batchVerifiedPasswords.Add(password);
                }
            }

            // 成功次数要跨重启（用户要的"按使用频率排序"第二次启动仍然成立）。
            // 关掉「记住密码列表」时这个方法自己会一个字节都不写。
            PersistPasswordList();
        }

        /// <summary>这条密码成功解开过多少次（0 = 没有记录）。给测试与排障用。</summary>
        public int GetSuccessCount(string? password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return 0;
            }

            lock (_passwordStatsLock)
            {
                return _successCounts.TryGetValue(password, out int count) ? count : 0;
            }
        }

        /// <summary>
        /// 本次运行里已经成功过的密码（副本，顺序 = 首次成功的先后）。
        ///
        /// <para>⚠ **2026-09-26 第 45 条改了位置**：它们现在是候选表里的**第一条**（不再是"空密码之后"）——
        /// 用户原话："我这个在用特定解压，是多相同文件，密码都是一样的，你这个复用本批次已成功的密码，
        /// 不就是在放屁吗，每次都是重新一轮密码检测"。有已知密码时，空密码**根本不再进候选**
        /// （加密包的空密码永远不可能对，见第 37 条实测）。</para>
        /// </summary>
        public IReadOnlyList<string> BatchVerifiedPasswords
        {
            get
            {
                lock (_passwordStatsLock)
                {
                    return _batchVerifiedPasswords.ToList();
                }
            }
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
        /// 顺序按 AGENTS.md §9.2 定死，**只有一处被用户明确要求调整过**（2026-09-24 第 14 条）：
        /// 空密码 → **本批已成功的密码（复用）** → 本任务最近成功 → 密码本映射命中 → 单任务密码
        /// → 统一密码 → 密码列表 → 同目录说明文件。
        ///
        /// <para>
        /// 为什么要这个顺序：越靠前的越可能命中、也越"便宜"（不用用户等）；
        /// 旁路说明文件是猜的，放最后，避免把说明文件里的一句噪声排到用户明确给的密码前面。
        /// </para>
        /// <para>
        /// <b>「本批已成功的密码」为什么能插到这么前</b>（用户原话："只要测到一个成功了就不要去再尝试了……
        /// 如果是密码本里面的第 5 个，如果是 200 个压缩包，那你岂不是要尝试 1000 遍"）：
        /// 同一个批次里的包**大多是同源的**（同一批资源、同一个打包者），第一个包试出来的密码
        /// 极可能就是后面每个包的密码。把它提到最前，200 个包从 O(N×k) 降到 O(N)。
        /// ⛔ 但它只是**提前**，不是**替换**：原来的候选链一条都不删（不同包的密码可能确实不同）。
        /// </para>
        /// <para>
        /// 同层级的候选按**成功次数从多到少**排（次数相同保持原顺序）—— 用户第 14 条："密码本里面的
        /// 优先级差不多就是使用次数的频繁程度"。排序是稳定的，所以"没记录过"的那些仍按原顺序，
        /// 用户能解释"为什么这条排在前面"。
        /// </para>
        /// </summary>
        private const bool RedCheckIgnoreBatchPassword = false;   // 红检开关（临时，已复位）

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

            /*
             * ==================== 顺序：**本批已成功的密码排在最前**（用户 2026-09-26 第 45 条）====================
             *
             * 他的原话："我这个在用特定解压，是多相同文件，密码都是一样的，你这个复用本批次已成功的密码，
             * 不就是在放屁吗，每次都是重新一轮密码检测" —— 真机日志（38 个同源包）逐字对应：
             * 每个包都是 `候选 1/10，尝试空密码` → `密码错误` → `候选 2/10，复用本批已成功的密码` 才解开。
             *
             * 两处都错了：
             * · 「空密码」对**加密包永远不可能对**（第 37 条实测：7-Zip / WinRAR 都造不出用空密码加密的包），
             *   一批同源包里有已知密码时还先试它，纯粹是白跑一轮；
             * · 第 14 条那个位置（"紧跟在空密码之后"）是在**还没有已知密码**的前提下定的 ——
             *   一旦本批有成功的密码，它就该是**第一个**。
             *
             * ⛔ 保留的边界：本批**还没有**成功过密码时，顺序与从前逐字相同（空密码仍在最前，`tryEmptyFirst` 说了算）——
             * 第一条候选怎么来的这件事不许变，否则"第一条就是密码本第一条"这类现场会再次说不清。
             */
            IReadOnlyList<string> verifiedPasswords = OrderBySuccessCount(BatchVerifiedPasswords, value => value)
                .Where(value => !string.IsNullOrEmpty(value))
                .ToList();

            bool hasVerifiedBatchPassword = verifiedPasswords.Count > 0;

            if (tryEmptyFirst && (!hasVerifiedBatchPassword || RedCheckIgnoreBatchPassword))
            {
                AddCandidate(string.Empty, "Empty", "空密码");
            }

            /*
             * 多条时按成功次数从多到少（次数相同按首次成功的先后）—— 与同层级排序同一口径。
             */
            foreach (string verified in verifiedPasswords)
            {
                AddCandidate(verified, "BatchSuccess", "复用本批已成功的密码");
            }

            if (!string.IsNullOrWhiteSpace(archivePath) &&
                _recentSuccess.TryGetValue(archivePath.Trim(), out string? recentPassword))
            {
                AddCandidate(recentPassword ?? string.Empty, "RecentSuccess", "本任务最近成功的密码");
            }

            foreach (PasswordEntry entry in OrderBySuccessCount(MatchMappedEntries(archiveFileName), entry => entry.Password))
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
                /*
                 * 密码列表这一层：先按**成功次数从多到少**排，再逐个加进候选。
                 *
                 * 原始序号（"密码列表第 N 项"）跟着一起带过去：备注里写的是它在**用户列表里**的位置，
                 * 而不是排序后的位置 —— 用户拿着日志回列表里找那一条时才找得到。
                 */
                var indexed = new List<(PasswordItem Item, int Index)>();

                int index = 0;

                foreach (PasswordItem item in passwordList)
                {
                    index++;

                    if (item == null || !item.IsEnabled)
                    {
                        continue;
                    }

                    indexed.Add((item, index));
                }

                foreach ((PasswordItem item, int originalIndex) in OrderBySuccessCount(
                    indexed,
                    pair => pair.Item.Value))
                {
                    /*
                     * 注意：
                     * 密码允许是空格开头、空格结尾。
                     * 不要 Trim。
                     */
                    string value = item.Value ?? string.Empty;

                    AddCandidate(value, "ImportedList", $"密码列表第 {originalIndex} 项");
                }
            }

            /*
             * 密码本里那些"按映射式切过"的行：再把**整行**试一遍（2026-09-25 第 30 条）。
             *
             * 为什么（真会咬人的形态）：映射式与列表式只能靠"行里有没有冒号"猜，而列表式的密码本身
             * 完全可能带冒号（`abc:123`、`www.xxx.com:8888`）。那种书里，我们一直在拿冒号**右侧**当密码试，
             * 正确的整行**从来没有被试过** —— 用户看到的就是"密码本第一条就是这个包的密码，却一直密码错误"。
             *
             * 位置：排在显式给的密码（统一密码 / 密码列表）**之后**，所以对正常的映射式密码本
             * （几百行的"资源名:密码"）只是尾部多了些噪音候选，不会把用户真正要试的那几条挤出尝试上限。
             */
            foreach (PasswordEntry entry in _bookEntries)
            {
                if (entry == null || entry.Kind != PasswordEntryKind.Mapped || string.IsNullOrEmpty(entry.RawLine))
                {
                    continue;
                }

                AddCandidate(
                    entry.RawLine!,
                    "BookRawLine",
                    $"密码本（第 {entry.LineNumber} 行整行）");
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
        /// 按**成功次数从多到少**排（次数相同保持原顺序）。用户 2026-09-24 第 14 条：
        /// "密码本里面的优先级差不多就是使用次数的频繁程度"。
        ///
        /// <para>
        /// 用 <c>OrderByDescending</c>（LINQ 的排序是**稳定**的）：没记录过次数的那些（计 0）
        /// 仍然保持用户排的顺序，所以"为什么这条在前"永远解释得清。
        /// </para>
        /// </summary>
        private List<T> OrderBySuccessCount<T>(IEnumerable<T> items, Func<T, string?> valueSelector)
        {
            if (items == null)
            {
                return new List<T>();
            }

            return items
                .OrderByDescending(item => GetSuccessCount(valueSelector(item)))
                .ToList();
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
             * 导入进来的值**确实在用户那本书里** —— 记下来，"未写回"标记就不会误报
             * （用户 2026-09-24 第 21 条：判据按值、且以文件为准）。
             * 放在 AddBookPathCore 之前也没关系：查询时会先看书在不在记住清单里，
             * 而下面紧接着就会把这本书加进清单。
             */
            RecordBookValues(txtPath, parsed.Entries);

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

                // 用户 2026-09-24 第 14 条：本批已经试出来的密码会被提到候选最前面。
                // 日志必须能看出"这一条是复用来的"（否则排障时看不出顺序为什么变了）。
                "BatchSuccess" => "复用本批已成功的密码：******",
                "RecentSuccess" => "尝试本任务最近成功的密码：******",
                "BookMapped" => "尝试密码本命中项：******",
                "TaskPassword" => "尝试单任务密码：******",
                "GlobalPassword" => "尝试统一密码：******",
                "ImportedList" => $"尝试密码列表第 {index} 项：******",
                // 密码本里按映射式切过的那一行，按"整行就是密码"再试一次（列表式密码可能自带冒号）。
                "BookRawLine" => $"尝试密码本整行（第 {index} 项）：******",
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
