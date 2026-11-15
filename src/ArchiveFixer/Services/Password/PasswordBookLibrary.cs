using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Services.Password;

/// <summary>
/// **外部密码本**：记住哪些书（有序、可多本）、每本书解析出的值集合、按归档名匹配的映射条目，
/// 以及"导入一本书 / 合并进列表 / 写回成功后重新读文件"这三个动作。
///
/// <para>两套口径必须分清（用户 2026-09-24 第 21 条）：<see cref="BookEntries"/> 是书里解析出来的条目；
/// <see cref="IsValueInRememberedBooks"/> 是"这个值是否确实躺在文件里"的纯事实查询（按书记账）。</para>
///
/// <para>⛔ 绝不把"我刚写过"当成事实：写回成功后必须重新读文件（<see cref="RefreshRememberedBookValues"/>），
/// 否则重启后「未写回」标记会复活 —— 那正是旧病根。</para>
/// </summary>
internal sealed class PasswordBookLibrary
{
    private readonly PasswordListState _state;
    private readonly PasswordTextLibrary _text;

    /// <summary>记住的密码本路径（**有序**，可多本）。</summary>
    private readonly List<string> _bookPaths = new();

    /// <summary>密码本里的全部条目（含映射式的"名称"，用于按归档名匹配）。</summary>
    private readonly List<PasswordEntry> _bookEntries = new();

    /// <summary>
    /// 每本已记住的密码本**解析出来的值集合**（键 = 书路径，Windows 口径大小写不敏感）。
    /// 只放"真的从文件里读出来的值"：没有记住的书、书不存在、读不出来 —— 都不放任何东西，
    /// 「未写回」标记照旧亮着（绝不假装已写回）。
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _bookValuesByPath =
        new(StringComparer.OrdinalIgnoreCase);

    internal PasswordBookLibrary(PasswordListState state, PasswordTextLibrary text)
    {
        _state = state;
        _text = text;
    }

    /// <summary>最近一次导入密码本时的提醒（编码识别、重复行等），供界面/日志说明情况。</summary>
    internal IReadOnlyList<string> LastImportWarnings { get; private set; } = Array.Empty<string>();

    /// <summary>最后一次成功导入的密码本文件路径（用于记住它、下次启动自动加载）。</summary>
    internal string LastImportedBookPath { get; private set; } = string.Empty;

    /// <summary>写到侧车文件 <c>password-book.path</c> 时用的数据根（与密码列表记忆同一个根）。</summary>
    internal string DataRootDirectory { get; set; } = string.Empty;

    internal IReadOnlyList<PasswordEntry> BookEntries => _bookEntries;

    /// <summary>记住的密码本（有序，副本；改它请走 <see cref="SetRememberedBookPaths"/>）。</summary>
    internal IReadOnlyList<string> BookPaths => _bookPaths;

    /// <summary>
    /// 已记住的密码本里一共读到过多少个不同的值（排障与测试用；读不到的书一个都不算）。
    /// 只数"当前还在记住清单里"的书：用户移除一本书之后，它的值不该再被算进来。
    /// </summary>
    internal int RememberedBookValueCount
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

    /// <summary>
    /// 把"记住的密码本"整份替换掉（④「密码」页里移除一项之后由主界面调它）。
    /// 顺序即合并顺序；重复项按 Windows 路径口径（大小写不敏感）去掉。
    /// </summary>
    internal void SetRememberedBookPaths(IEnumerable<string>? paths)
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

        _state.NotifyChanged();
    }

    /// <summary>从记忆读回书清单（不剪枝、不落盘：加载期间由记忆自己决定写不写）。</summary>
    internal void ReplaceBookPathsQuiet(IEnumerable<string> paths)
    {
        _bookPaths.Clear();

        foreach (string path in paths)
        {
            AddBookPathCore(path);
        }
    }

    /// <summary>记住一本密码本（已经在里面就不重复加，但**顺序不动**）。</summary>
    internal void RememberBookPath(string path)
    {
        if (!AddBookPathCore(path))
        {
            return;
        }

        _state.NotifyChanged();
    }

    /// <summary>
    /// 这个值是不是**确实躺在某一本已记住的密码本里**（用户 2026-09-24 第 21 条的判据）。
    ///
    /// <para>纯事实查询：只认从文件里解析出来的值。没有任何已记住的书、书不存在、
    /// 书读不出来、值是空串 —— 一律返回 false。</para>
    /// </summary>
    internal bool IsValueInRememberedBooks(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        /*
         * 生产入口把内置 passwords.txt 当主存储；它没有"已记住的外部密码本路径"，
         * 所以不能只查 _bookValuesByPath，否则手动密码明明已落盘，列表仍永远显示
         * 「手动添加 · 未写回」。内置文本存储也必须算"已经写回"。
         */
        if (_text.ContainsValue(value))
        {
            return true;
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
    /// 才知道它真的在里面了。读不到的书整本丢掉旧账，绝不保留它上一次读到的值。</para>
    /// </summary>
    internal int RefreshRememberedBookValues()
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
    /// 把一本密码本**合并**进当前列表（启动时逐本调用）：只补"列表里还没有的值"、**跳过墓碑**，
    /// 新条目按**书的顺序**追加到末尾 —— 于是记忆里的顺序天然保留在前面。
    /// </summary>
    /// <param name="bookPath">密码本 txt 路径。</param>
    /// <param name="parsedCount">这本书解析出多少条（含被跳过的）。</param>
    /// <param name="skippedByTombstone">其中多少条因为墓碑被跳过。</param>
    /// <param name="warnings">解析警告（编码识别、重复行等）。</param>
    /// <returns>真正补进列表的条数。</returns>
    internal int MergePasswordBook(
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

        // 合并这一步的解析结果**现成**，顺手记下"这本书里有哪些值"，判「未写回」时问的就是这份事实。
        RecordBookValues(bookPath, parsed.Entries);

        var existing = new HashSet<string>(
            _state.Passwords.Select(item => item.Value ?? string.Empty),
            StringComparer.Ordinal);

        var tombstones = new HashSet<string>(_state.Tombstones, StringComparer.Ordinal);

        int added = 0;

        _state.Suspend();

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

                _state.Passwords.Add(new PasswordItem
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
            _state.Resume();
        }

        if (added > 0)
        {
            _state.NotifyChanged();
        }

        return added;
    }

    /// <summary>
    /// 把用户导入的密码本复制一份到**程序安装目录**（与 exe 同级），返回复制后的路径。
    /// 用户 2026-10-19 口径：导入的是 .txt 就复制到安装位置；⛔ 不是记住/链接用户原文件。
    /// </summary>
    internal static string CopyImportedBookToInstallDirectory(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return sourcePath;
        }

        string sourceFull = Path.GetFullPath(sourcePath);
        string installDirectory = AppContext.BaseDirectory;
        string target = Path.Combine(installDirectory, Path.GetFileName(sourceFull));

        if (string.Equals(sourceFull, target, StringComparison.OrdinalIgnoreCase))
        {
            return sourceFull;
        }

        Directory.CreateDirectory(installDirectory);
        File.Copy(sourceFull, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// 导入密码本文件：列表式与映射式都支持，映射式条目**也放进界面列表**，
    /// 名称额外留在密码本里用于按归档名匹配。密码一律不 Trim。
    /// </summary>
    internal List<PasswordItem> ImportPasswordList(string txtPath)
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

        // 导入进来的值**确实在用户那本书里** —— 记下来，"未写回"标记就不会误报。
        RecordBookValues(txtPath, parsed.Entries);

        // 把路径写盘挂在"导入"这个动作上；生产入口改用内置 passwords.txt 后不再写侧车、不登记外部书。
        if (!_text.UseTextStore)
        {
            TryWriteBookPathSidecar(txtPath);
        }

        _bookEntries.Clear();
        _bookEntries.AddRange(parsed.Entries);

        // 导入 = "再加回来"：不记墓碑，且书里出现的值撤销墓碑（他删过、现在又导进来就是要留着）。
        _state.Suspend();

        try
        {
            foreach (PasswordEntry entry in parsed.Entries)
            {
                string remark = entry.Kind == PasswordEntryKind.Mapped && !string.IsNullOrWhiteSpace(entry.Name)
                    ? $"导入：{entry.Name}"
                    : $"导入（第 {entry.LineNumber} 行）";

                _state.ForgetTombstone(entry.Password);

                var item = new PasswordItem
                {
                    Value = entry.Password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = remark
                };

                _state.Passwords.Add(item);
                imported.Add(item);
            }

            _state.RemoveDuplicatePasswordsKeepOrder();
        }
        finally
        {
            _state.Resume();
        }

        // 这本书从此"已记住"（不重复加；顺序按第一次导入的先后）。
        // 内置 txt 模式不再登记外部书：导入只做一次性复制/合并。
        if (!_text.UseTextStore)
        {
            AddBookPathCore(txtPath);
        }

        _state.NotifyChanged();

        return imported;
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
    /// 把一本书这次解析出来的值**整份替换**进值账（用户手改过书、删掉几行之后，旧值不许继续算数）。
    /// 含被墓碑跳过的值：墓碑管的是"要不要放进尝试列表"，与"文件里有没有这个值"是两件事。
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

    /// <summary>旧口径的侧车文件 <c>password-book.path</c>（只在"不用内置文本库"时写；失败一律吞掉）。</summary>
    private void TryWriteBookPathSidecar(string txtPath)
    {
        if (DataRootDirectory.Length == 0)
        {
            return;
        }

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
    }
}
