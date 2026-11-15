using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services.Password;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArchiveFixer.Services;

/// <summary>
/// 密码列表 / 外部密码本 / 候选 / 记忆的统一门面。
///
/// <para><b>公开 API 保持不变</b>，实现按职责拆到 <c>Services/Password/</c> 下的协作类：
/// 列表内存状态（<see cref="PasswordListState"/>）、试密码账本（<see cref="PasswordAttemptStats"/>）、
/// 内置文本库（<see cref="PasswordTextLibrary"/>）、外部密码本（<see cref="PasswordBookLibrary"/>）、
/// 记忆加载/落盘（<see cref="PasswordListMemory"/>）、候选组装（<see cref="PasswordCandidateBuilder"/>）、
/// 侧车候选（<see cref="PasswordSidecarCandidateSource"/>）、失败模型（<see cref="PasswordListHealth"/>）。</para>
///
/// <para>这里只做转发与装配；任何"顺序 / 存储格式 / 隐私边界"的判据都在协作类里，只有一处出处。</para>
/// </summary>
public class PasswordService
{
    private readonly PasswordListHealth _health;
    private readonly PasswordListState _listState;
    private readonly PasswordAttemptStats _attemptStats;
    private readonly PasswordTextLibrary _textLibrary;
    private readonly PasswordBookLibrary _bookLibrary;
    private readonly PasswordListMemory _memory;
    private readonly PasswordCandidateBuilder _candidateBuilder;

    public PasswordService()
    {
        _health = new PasswordListHealth();
        _listState = new PasswordListState();
        _attemptStats = new PasswordAttemptStats();
        _textLibrary = new PasswordTextLibrary(_listState, _health);
        _bookLibrary = new PasswordBookLibrary(_listState, _textLibrary);
        _memory = new PasswordListMemory(_listState, _attemptStats, _textLibrary, _health, _bookLibrary);
        _candidateBuilder = new PasswordCandidateBuilder(
            _attemptStats,
            _bookLibrary,
            new PasswordSidecarCandidateSource());

        // 会被记住的状态一变就落盘（什么时候不该写由 PasswordListMemory 自己判断）。
        _listState.Changed += _memory.PersistPasswordList;
        _attemptStats.SuccessRecorded += _memory.PersistPasswordList;
    }

    /// <summary>密码列表（顺序 = 尝试顺序；界面直接改 IsEnabled/Remark 也会触发落盘）。</summary>
    public ObservableCollection<PasswordItem> Passwords => _listState.Passwords;

    /// <summary>密码本里的全部条目（含映射式的"名称"，用于按归档名匹配）。</summary>
    public IReadOnlyList<PasswordEntry> BookEntries => _bookLibrary.BookEntries;

    /// <summary>最近一次导入密码本时的提醒（编码识别、重复行等），供界面/日志说明情况。</summary>
    public IReadOnlyList<string> LastImportWarnings => _bookLibrary.LastImportWarnings;

    /// <summary>最后一次成功导入的密码本文件路径（用于记住它、下次启动自动加载）。</summary>
    public string LastImportedBookPath => _bookLibrary.LastImportedBookPath;

    /// <summary>
    /// 密码列表记忆的读写体（DPAPI 机器范围，加密落盘）。
    /// 它的数据根目录跟着 <see cref="DataRootDirectory"/> 走（同一个 setter 一起改）。
    /// </summary>
    public PasswordListStore ListStore => _memory.ListStore;

    /// <summary>程序自带的明文密码清单（用户 2026-10-25：不再链接外部密码本）。</summary>
    public PasswordTextStore TextStore => _textLibrary.TextStore;

    /// <summary>passwords.txt 的启用 / 备注元数据（密码值仍以 passwords.txt 为准）。</summary>
    public PasswordMetadataStore MetadataStore => _textLibrary.MetadataStore;

    /// <summary>
    /// 是否把内置 <c>passwords.txt</c> 当主存储（生产入口默认 true；单元测试默认 false，保持旧 DPAPI 夹具语义）。
    /// </summary>
    public bool UseTextStore
    {
        get => _textLibrary.UseTextStore;
        set => _textLibrary.UseTextStore = value;
    }

    /// <summary>
    /// 本次启动**禁止再写 passwords.txt / 元数据 / 加密记忆**。
    ///
    /// <para>只在一种情况下为 true：主清单不见了、同目录的 <c>.bak</c> 也恢复不了，
    /// 但还有"主清单确实存在过"的证据（元数据/备份）。这一档宁可不写，也不能写错；
    /// 用户把 <c>passwords.txt.bak</c> 改回原名重启即可恢复。</para>
    /// </summary>
    public bool TextStoreWriteBlocked => _health.TextStoreWriteBlocked;

    /// <summary>
    /// 「记住密码列表」总开关（来自 <see cref="AppSettings.RememberPasswordList"/>，默认开）。
    ///
    /// <para>false = **既不写、也不读**记忆文件（一个字节都不动，磁盘上已有的那份不会被删）；
    /// 列表退回"只在本次运行内有效"的老行为。</para>
    /// </summary>
    public bool RememberPasswordList
    {
        get => _memory.RememberPasswordList;
        set => _memory.RememberPasswordList = value;
    }

    /// <summary>启动时由记忆恢复的条数（密码列表窗口上那句"记忆 N 条"用它）。</summary>
    public int RememberedEntryCount => _memory.RememberedEntryCount;

    /// <summary>
    /// 记忆相关的**一句人话**（读不出来 / 写不下去时的原因）。
    /// 界面在提示条上显示它，**不弹错误框、不阻断**（用户 2026-09-24 要求）。空 = 一切正常。
    /// </summary>
    public string LastListWarning => _health.LastListWarning;

    /// <summary>
    /// 记忆出问题时通知界面（只给提示条用）。
    /// 订阅者少一处也不影响正确性 —— 事实本身在 <see cref="LastListWarning"/> 里。
    /// </summary>
    public event Action<string>? RememberedListWarning
    {
        add => _health.RememberedListWarning += value;
        remove => _health.RememberedListWarning -= value;
    }

    /// <summary>记住的密码本（有序，副本；改它请走 <see cref="SetRememberedBookPaths"/>）。</summary>
    public IReadOnlyList<string> RememberedBookPaths => _bookLibrary.BookPaths;

    /// <summary>墓碑（副本，只读；供测试与排障核对）。</summary>
    public IReadOnlyList<string> Tombstones => _listState.Tombstones;

    /// <summary>
    /// 已记住的密码本里一共读到过多少个不同的值（排障与测试用；读不到的书一个都不算）。
    ///
    /// <para>只数"当前还在记住清单里"的书：用户移除一本书之后，它的值不该再被算进来
    /// （否则那条目会一直显示"已写回"，而实际上它已经不在任何一本被记住的书里了）。</para>
    /// </summary>
    public int RememberedBookValueCount => _bookLibrary.RememberedBookValueCount;

    /// <summary>
    /// 密码列表记忆落在哪个目录（沿用既有数据根目录，**不新造路径来源**）。
    ///
    /// 必须与 <see cref="PathService.DataRootDirectory"/> 保持一致：之前在导入那边写死
    /// <c>AppContext.BaseDirectory\data</c>、读取那边却另算一个目录，用户一旦换过位置就会
    /// "导入了、下次启动又说没配过"。
    /// </summary>
    public string DataRootDirectory
    {
        get => _memory.DataRootDirectory;
        set
        {
            _memory.DataRootDirectory = value;

            // 导入外部书时那条 sidecar 也落在同一个数据根（内置文本库模式不写它）。
            _bookLibrary.DataRootDirectory = value ?? string.Empty;
        }
    }

    // ================================================================
    // 记忆：加载 / 保存
    // ================================================================

    /// <summary>
    /// 启动时加载记忆（条目 + 顺序 + 启用状态 + 墓碑 + 记住的密码本）。
    ///
    /// <para>读失败一律**忽略**：返回原因放进 <see cref="LastListWarning"/>，列表保持原样，
    /// 磁盘上那份文件既不被覆盖也不被删（换回原来的机器 / 系统时它还在）。</para>
    /// </summary>
    public PasswordListLoadStatus LoadRememberedList() => _memory.LoadRememberedList();

    /// <summary>
    /// 把一份快照立刻写进记忆（用户点保存、或测试要一个确定的状态时用）。
    /// 平时不用手工调它：每次变更都会自动落盘。
    /// </summary>
    public PasswordListSaveResult SaveRememberedList() => _memory.SaveRememberedList();

    // ================================================================
    // 记住的密码本 / 写回判定
    // ================================================================

    /// <summary>
    /// 把"记住的密码本"整份替换掉（④「密码」页里移除一项之后由主界面调它）。
    /// 顺序即合并顺序；重复项按 Windows 路径口径（大小写不敏感）去掉。
    /// </summary>
    public void SetRememberedBookPaths(IEnumerable<string>? paths) => _bookLibrary.SetRememberedBookPaths(paths);

    /// <summary>记住一本密码本（已经在里面就不重复加，但**顺序不动**）。</summary>
    public void RememberBookPath(string path) => _bookLibrary.RememberBookPath(path);

    /// <summary>
    /// 这个值是不是**确实躺在某一本已记住的密码本里**（用户 2026-09-24 第 21 条的判据）。
    /// 纯事实查询：只认从文件里解析出来的值；没有书、书不存在、读不出来、空串 —— 一律 false。
    /// </summary>
    public bool IsValueInRememberedBooks(string? value) => _bookLibrary.IsValueInRememberedBooks(value);

    /// <summary>
    /// **重新读一遍**所有已记住的密码本来重建值集合，返回其中真正读到的本书。
    /// 写回密码本成功后必须调它：只有重新读文件才知道值真的在里面了。
    /// </summary>
    public int RefreshRememberedBookValues() => _bookLibrary.RefreshRememberedBookValues();

    /// <summary>
    /// 把一本密码本**合并**进当前列表（启动时逐本调用）。
    /// 只补"列表里还没有的值"、跳过墓碑，新条目按书的顺序追加到末尾。
    /// </summary>
    public int MergePasswordBook(
        string bookPath,
        out int parsedCount,
        out int skippedByTombstone,
        out IReadOnlyList<string> warnings) =>
        _bookLibrary.MergePasswordBook(bookPath, out parsedCount, out skippedByTombstone, out warnings);

    // ================================================================
    // 密码列表编辑（墓碑随删除/清空记账）
    // ================================================================

    /// <summary>记一条墓碑（值被用户删掉 / 清空）。</summary>
    public void RecordTombstone(string? value) => _listState.RecordTombstone(value);

    /// <summary>撤销墓碑（用户又把这个值加回来了：导入与手动添加都算）。</summary>
    public void ForgetTombstone(string? value) => _listState.ForgetTombstone(value);

    public bool AddPassword(string password) => _listState.Add(password);

    public bool RemovePassword(PasswordItem item) => _listState.Remove(item);

    public void ClearPasswords() => _listState.Clear();

    public bool MoveUp(PasswordItem item) => _listState.MoveUp(item);

    public bool MoveDown(PasswordItem item) => _listState.MoveDown(item);

    // ================================================================
    // 本轮试密码的运行期账本
    // ================================================================

    /// <summary>记录某个归档刚刚用哪个密码成功过（最近成功只留内存；成功次数跨重启）。</summary>
    public void RecordPasswordSuccess(string? archivePath, string? password) =>
        _attemptStats.RecordSuccess(archivePath, password);

    /// <summary>**本批开工**：清空"本批已成功的密码"（只在本批内生效，不影响下一批）。</summary>
    public void BeginBatch() => _attemptStats.BeginBatch();

    /// <summary>这条密码**本次导入内**成功过多少次（0 = 本批还没成功过；候选排序只读它）。</summary>
    public int GetBatchSuccessCount(string? password) => _attemptStats.GetBatchSuccessCount(password);

    /// <summary>这条密码成功解开过多少次（0 = 没有记录）。给测试与排障用。</summary>
    public int GetSuccessCount(string? password) => _attemptStats.GetSuccessCount(password);

    /// <summary>
    /// 本次运行里已经成功过的密码（副本，顺序 = 首次成功的先后）。
    ///
    /// <para>⚠ 2026-09-26 第 45 条：它们现在是候选表里的**第一条**（不再是"空密码之后"）——
    /// 有已知密码时，空密码**根本不再进候选**。本档内部按本批成功频率排（稳定排序）。</para>
    /// </summary>
    public IReadOnlyList<string> BatchVerifiedPasswords => _attemptStats.BatchVerifiedPasswords;

    // ================================================================
    // 候选 / 日志文案
    // ================================================================

    /// <summary>按归档文件名匹配密码本里的映射式条目（命中优先于遍历整表）。</summary>
    public IReadOnlyList<PasswordEntry> MatchMappedEntries(string? archiveFileName) =>
        _candidateBuilder.MatchMappedEntries(archiveFileName);

    /// <summary>
    /// 组装某个任务的密码候选。
    ///
    /// <para>顺序（AGENTS.md §9.2，2026-09-26 第 45 条唯一调整过的位置）：
    /// 空密码 → **本批已成功的密码（复用）** → 本任务最近成功 → 密码本映射命中 → 单任务密码
    /// → 统一密码 → 密码列表 → 密码本整行候选 → 同目录说明文件。复用只是**提前**，不删减原链。</para>
    /// </summary>
    public List<PasswordItem> GetPasswordCandidates(
        ArchiveTask task,
        string globalPassword,
        IEnumerable<PasswordItem> passwordList,
        bool tryEmptyFirst,
        bool includeSidecarCandidates = false) =>
        _candidateBuilder.GetPasswordCandidates(
            task,
            globalPassword,
            passwordList,
            tryEmptyFirst,
            includeSidecarCandidates);

    /// <summary>一次密码尝试的日志文案（来源 + 脱敏占位符，**绝不出现明文**）。</summary>
    public string BuildTryPasswordLogText(PasswordItem candidate, int index) =>
        _candidateBuilder.BuildTryPasswordLogText(candidate, index);

    /// <summary>脱敏后的密码（日志/界面统一走它）。</summary>
    public string MaskPassword(string password) => PasswordMasker.Mask(password);

    // ================================================================
    // 导入密码本
    // ================================================================

    /// <summary>
    /// 把用户导入的密码本复制一份到**程序安装目录**（与 exe 同级），返回复制后的路径。
    /// 用户 2026-10-19 口径：导入的是 .txt，就把它复制到安装位置；⛔ 不是记住/链接用户原文件。
    /// </summary>
    public static string CopyImportedBookToInstallDirectory(string sourcePath) =>
        PasswordBookLibrary.CopyImportedBookToInstallDirectory(sourcePath);

    /// <summary>
    /// 导入密码本文件。列表式与映射式都支持；映射式的条目**也放进界面列表**，
    /// 名称额外留在密码本里用于按归档名匹配。密码一律不 Trim。
    /// </summary>
    public List<PasswordItem> ImportPasswordList(string txtPath) => _bookLibrary.ImportPasswordList(txtPath);
}
