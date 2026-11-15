using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Services.Password;

/// <summary>
/// **密码列表记忆**：启动时加载 / 变更时落盘 / 墓碑与记住的密码本随列表一起往返。
///
/// <para>载荷本体是 <see cref="PasswordListStore"/>（DPAPI 机器范围加密到
/// <c>password-list.dat</c>）；内置文本库是主存储时，文本清单与元数据由
/// <see cref="PasswordTextLibrary"/> 读写。这里只编排"读什么、写什么、什么时候不写"，
/// 并把失败原因交给 <see cref="PasswordListHealth"/>。</para>
///
/// <para><b>两处刻意的"不写"</b>：① <see cref="RememberPasswordList"/> 关掉时既不读也不写；
/// ② <see cref="PasswordListHealth.TextStoreWriteBlocked"/> 为 true 时本次一个字节都不写
/// （拿旧记忆覆盖用户真实列表是事故，见 2026-11-01 真机 13 条 → 1 条）。</para>
/// </summary>
internal sealed class PasswordListMemory
{
    private readonly PasswordListState _state;
    private readonly PasswordAttemptStats _stats;
    private readonly PasswordTextLibrary _text;
    private readonly PasswordListHealth _health;
    private readonly PasswordBookLibrary _books;

    /// <summary>记忆文件写入的串行锁（见 <see cref="PersistPasswordList"/> 的说明）。</summary>
    private readonly object _persistGate = new();

    internal PasswordListMemory(
        PasswordListState state,
        PasswordAttemptStats stats,
        PasswordTextLibrary text,
        PasswordListHealth health,
        PasswordBookLibrary books)
    {
        _state = state;
        _stats = stats;
        _text = text;
        _health = health;
        _books = books;
    }

    /// <summary>密码列表记忆的读写体（DPAPI 机器范围，加密落盘）。</summary>
    internal PasswordListStore ListStore { get; } = new();

    /// <summary>「记住密码列表」总开关：false = 既不写、也不读记忆文件（磁盘上已有的那份不会被删）。</summary>
    internal bool RememberPasswordList { get; set; } = true;

    /// <summary>启动时由记忆恢复的条数（密码列表窗口上那句"记忆 N 条"用它）。</summary>
    internal int RememberedEntryCount { get; private set; }

    /// <summary>
    /// 密码列表记忆落在哪个目录（沿用既有数据根目录，**不新造路径来源**）。
    /// 记忆文件与文本库文件共用同一个根：两处分开设过一次就会互相看不见。
    /// </summary>
    internal string DataRootDirectory
    {
        get => ListStore.DataRootDirectory;
        set
        {
            ListStore.DataRootDirectory = value ?? string.Empty;

            // 记忆文件与侧车文件共用同一个根。
            _text.SetDataRootDirectory(value);
        }
    }

    /// <summary>
    /// 启动时加载记忆（条目 + 顺序 + 启用状态 + 墓碑 + 记住的密码本）。
    /// 读失败一律**忽略**：返回原因放进 <see cref="PasswordListHealth.LastListWarning"/>，
    /// 列表保持原样，磁盘上那份文件既不被覆盖也不被删。
    /// </summary>
    internal PasswordListLoadStatus LoadRememberedList()
    {
        _health.ResetWarning();
        RememberedEntryCount = 0;

        if (!RememberPasswordList)
        {
            return PasswordListLoadStatus.Disabled;
        }

        _health.TextStoreWriteBlocked = false;

        /*
         * 2026-11-01 真机：passwords.txt 丢失时，旧写法直接回落到 DPAPI 记忆并立即写回，
         * 等于拿很旧的记忆覆盖用户列表（真机 13 条 → 只剩 1 条测试密码）。先做两件事：
         * ① 有产品自己的 .bak ⇒ 按 .bak 恢复（那是最后一次保存前的真实内容，不是猜）；
         * ② 没有 .bak 但仍有"主清单存在过"的证据（元数据）⇒ 本次一个字节都不写，等用户手动恢复。
         */
        if (_text.UseTextStore && !_text.Exists)
        {
            if (_text.TryRestoreFromBackup(out _))
            {
                _health.Warn("passwords.txt 不存在，已按同目录的 passwords.txt.bak 自动恢复。");
            }
            else if (_text.BackupExists || _text.MetadataExists)
            {
                _health.TextStoreWriteBlocked = true;
                _health.Warn(
                    "passwords.txt 不存在，且 passwords.txt.bak 恢复失败；为避免用旧的加密记忆覆盖密码列表，"
                    + "本次不会写回任何密码文件。请把同目录的 passwords.txt.bak 改回 passwords.txt 后重启。");

                return PasswordListLoadStatus.NoFile;
            }
        }

        bool textStorePresent = _text.UseTextStore && _text.Exists;

        /*
         * 2026-10-25：内置明文清单是主存储。存在就只读它（顺序 = 行顺序），
         * 不再从外部密码本自动合并；没有它才回落到旧的 DPAPI 记忆做一次性迁移。
         */
        if (textStorePresent)
        {
            PasswordListLoadStatus? textStatus = _text.TryLoadIntoState();

            if (textStatus.HasValue)
            {
                if (textStatus.Value == PasswordListLoadStatus.Loaded)
                {
                    RememberedEntryCount = _state.Passwords.Count;
                }

                return textStatus.Value;
            }
        }

        // 启动期不在 UI 线程上直接读盘：坏盘上读密码列表会把窗口拖死（2026-11-10 真机）。
        // 超时或失败一律当"没有记忆"：只管本次运行，不写回、不删原件。
        PasswordListLoadResult result = ArchiveFixer.Helpers.SafeIo.Run(
            () => ListStore.Load(),
            new PasswordListLoadResult(PasswordListLoadStatus.NoFile, null, string.Empty),
            ArchiveFixer.Helpers.SafeIo.DefaultTimeout);

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

            _health.Warn(warning);

            return result.Status;
        }

        _state.Suspend();

        try
        {
            _state.Passwords.Clear();

            foreach (PasswordListEntry entry in result.Snapshot.Entries)
            {
                _state.Passwords.Add(new PasswordItem
                {
                    Value = entry.Value ?? string.Empty,
                    Source = entry.Source,
                    IsEnabled = entry.Enabled,
                    Remark = entry.Remark ?? string.Empty
                });
            }

            _books.ReplaceBookPathsQuiet(result.Snapshot.BookPaths);

            _state.ReplaceTombstones(result.Snapshot.Tombstones);

            /*
             * 成功次数（v2 载荷）跟着记忆一起回来 —— 用户"按使用频率排序"的期望跨重启成立。
             * 旧版本写下的文件（v1）里没有这一项 → 空表，一切按原顺序（不报错、不猜）。
             */
            _stats.LoadSuccessCounts(result.Snapshot.SuccessCounts);
        }
        finally
        {
            _state.Resume();
        }

        _state.RemoveDuplicatePasswordsKeepOrder();

        RememberedEntryCount = _state.Passwords.Count;

        // 旧 DPAPI 记忆读成功后做一次性迁移：立即写内置 txt，之后以 txt 为准。
        // ⛔ 只在"本来就没有 txt、也没有它存在过的证据"时才写；txt 读过但失败时绝不覆盖。
        if (_text.UseTextStore && !textStorePresent)
        {
            PersistPasswordList();
        }

        return result.Status;
    }

    /// <summary>
    /// 把一份快照立刻写进记忆（用户点保存、或测试要一个确定的状态时用）。
    /// 平时不用手工调它：每次变更都会自动落盘。
    /// </summary>
    internal PasswordListSaveResult SaveRememberedList()
    {
        if (!RememberPasswordList)
        {
            return new PasswordListSaveResult(false, "「记住密码列表」已关闭", ListStore.FilePath);
        }

        return ListStore.Save(BuildSnapshot());
    }

    /// <summary>
    /// 变更之后落盘。关掉开关/禁止写回时**一个字节都不写**；写失败时只留一句人话（界面提示条用），
    /// 绝不抛异常 —— 记不住密码列表不该让"加一条密码"这个动作失败。
    ///
    /// <para>
    /// ⚠ 整个过程串行化（<see cref="_persistGate"/>）：并行解压时多个任务会同时
    /// <see cref="PasswordAttemptStats.RecordSuccess"/>，而记忆文件是**原子写 + 整体替换**的
    /// （同一个 <c>.tmp</c>），两个线程一起写会互相把临时文件搬走，一方报"写失败"
    /// （内容不会坏，但会平白多一句失败提示）。
    /// </para>
    /// </summary>
    internal void PersistPasswordList()
    {
        // 与原来的 _persistSuspensions 同一道闸门：批量编辑（加载记忆 / 导入 / 合并 / 加一条）期间
        // 哪怕并行解压的线程记了一笔成功次数，也不许写盘 —— 否则会落一份"编辑到一半"的快照。
        if (_state.IsChangeSuspended || !RememberPasswordList || _health.TextStoreWriteBlocked)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ListStore.DataRootDirectory))
        {
            return;
        }

        PasswordListSaveResult result;
        string textFailure;

        lock (_persistGate)
        {
            // 内置 txt 是主存储：顺序 = 当前列表顺序（上下移动后立即写）。
            textFailure = _text.Save(_state);

            // DPAPI 记忆继续保留，供迁移 / 统计等旧逻辑使用；它不是用户要写回的那一份。
            result = ListStore.Save(BuildSnapshot());
        }

        if (result.Success && textFailure.Length == 0)
        {
            return;
        }

        _health.Warn(
            textFailure.Length > 0
                ? textFailure
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PasswordListMemorySaveFailedFormat,
                    result.FailureReason));
    }

    private PasswordListSnapshot BuildSnapshot()
    {
        var snapshot = new PasswordListSnapshot();

        foreach (PasswordItem item in _state.Passwords)
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

        snapshot.BookPaths.AddRange(_books.BookPaths);
        snapshot.Tombstones.AddRange(_state.Tombstones);

        // 成功次数跟着列表一起落盘；并行解压会在别的线程记账，所以取副本要加锁。
        _stats.CopySuccessCountsTo(snapshot.SuccessCounts);

        return snapshot;
    }
}
