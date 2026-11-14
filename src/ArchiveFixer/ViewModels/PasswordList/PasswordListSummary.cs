using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ArchiveFixer.ViewModels.PasswordList;

/// <summary>
/// 列表的只读摘要，以及"未写回密码本"这一条的**事实**判定。
///
/// <para>窗口顶部那句"当前列表：N 条（其中启动时由记忆恢复 M 条）＋ 记住的密码本 K 本"
/// 与统计块（总数 / 启用数）都从这里算，<see cref="PasswordListViewModel"/> 只负责转发绑定。</para>
///
/// <para>「未写回」标记是**按值、且以文件为准**的：来源是手动添加 ⨯ 这个值不在任何一本
/// 已记住的密码本里（用户 2026-09-24 第 21 条）。⛔ 绝不靠"本次运行里记过一笔"这种只在
/// 内存里的账 —— 那正是"提示写回成功、重启又显示未写回"的旧病根。</para>
/// </summary>
internal sealed class PasswordListSummary
{
    /// <summary>
    /// 手动添加的条目在内存里就是这个来源（<see cref="PasswordService.AddPassword"/> 写的）。
    /// 只认这一个值 = 只对"手动加的"提供写回。
    /// </summary>
    internal const string ManualSource = "ManualList";

    /// <summary><see cref="PasswordService.AddPassword"/> 给手动条目的默认备注（只在没被用户改过时才更新它）。</summary>
    internal const string DefaultManualRemark = "手动添加";

    private readonly PasswordService _passwordService;
    private readonly ObservableCollection<PasswordItem> _passwords;

    internal PasswordListSummary(PasswordService passwordService, ObservableCollection<PasswordItem> passwords)
    {
        _passwordService = passwordService;
        _passwords = passwords;
    }

    /// <summary>列表里一条密码都没有（窗口显示空状态引导）。</summary>
    internal bool IsEmpty => _passwords.Count == 0;

    internal int TotalCount => _passwords.Count;

    internal int EnabledCount => _passwords.Count(item => item.IsEnabled);

    /// <summary>「记住密码列表」当前是不是开着（窗口顶部那句摘要与设置里的开关是同一个事实）。</summary>
    internal bool IsRememberingEnabled => _passwordService.RememberPasswordList;

    /// <summary>
    /// 窗口顶部那句"当前列表：N 条（其中启动时由记忆恢复 M 条）＋ 记住的密码本 K 本"。
    ///
    /// <para>用户 2026-09-24 要求窗口上能一眼看出"列表现在到底靠什么活着"：
    /// 是记忆恢复来的、还是这轮从密码本里合并出来的、又记住几本书。</para>
    /// </summary>
    internal string MemorySummary
    {
        get
        {
            if (!IsRememberingEnabled)
            {
                return StatusText.PasswordListMemoryDisabledSummary;
            }

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.PasswordListMemorySummaryFormat,
                TotalCount,
                _passwordService.RememberedEntryCount,
                _passwordService.RememberedBookPaths.Count);
        }
    }

    /// <summary>还没写回密码本的手工条目条数（按值、以文件为准）。</summary>
    internal int UnwrittenManualCount => CollectUnwrittenManualValues().Count;

    /// <summary>有没有待写回的手工条目（「写回密码本…」按钮的可用性判据）。</summary>
    internal bool HasUnwrittenManualPasswords => UnwrittenManualCount > 0;

    /// <summary>
    /// 挑出待写回的值：手动添加、且它的值**不在任何一本已记住的密码本里**。
    /// 列表顺序原样保留（写回后文件里的先后就是用户看到的先后）。
    /// </summary>
    internal List<string> CollectUnwrittenManualValues()
    {
        return _passwords
            .Where(IsUnwrittenManual)
            .Select(item => item.Value ?? string.Empty)
            .ToList();
    }

    /// <summary>
    /// 把列表里每一条的「未写回」标记刷新成当前事实
    /// （列表重建后、以及某条的密码值被改过之后都要刷新）。
    /// </summary>
    internal void RefreshMarkers()
    {
        foreach (PasswordItem item in _passwords)
        {
            if (item != null)
            {
                item.ShowUnwrittenMarker = IsUnwrittenManual(item);
            }
        }
    }

    /// <summary>
    /// 写回成功之后刷新**事实**（用户 2026-09-24 第 21 条的核心修法）。
    ///
    /// <para>顺序不能反、也不能省：</para>
    /// <list type="number">
    /// <item><description>目标文件从此是一本"已记住"的密码本（写进去了，就是他在用的书）；</description></item>
    /// <item><description><b>重新读一遍文件</b>得出值集合 —— ⛔ 绝不把"我刚写过"记账当成事实；</description></item>
    /// <item><description>备注与标记按新事实重算（备注仍停在"手动添加"时才顺手更新，不覆盖用户自己写的）。</description></item>
    /// </list>
    /// <para>刻意**不改 <c>Source</c>、也不改 <c>Value</c>**：来源仍是"手动添加"（它就是手动加的），
    /// 密码本体一个字符都不许动（含首尾空格）。</para>
    /// </summary>
    internal void MarkAsWrittenBack(string targetPath, IReadOnlyList<string> writtenValues)
    {
        _passwordService.RememberBookPath(targetPath);

        // 写回本身已经落盘（PasswordService 的落盘开关由它自己管），这里再刷一次值集合。
        _passwordService.RefreshRememberedBookValues();

        var written = new HashSet<string>(writtenValues, StringComparer.Ordinal);

        foreach (PasswordItem item in _passwords)
        {
            if (item == null ||
                !string.Equals(item.Source, ManualSource, StringComparison.Ordinal) ||
                !written.Contains(item.Value ?? string.Empty))
            {
                continue;
            }

            if (string.Equals(item.Remark, DefaultManualRemark, StringComparison.Ordinal))
            {
                item.Remark = StatusText.PasswordWriteBackDoneRemark;
            }
        }

        RefreshMarkers();
    }

    /// <summary>
    /// 这一条是不是"手动添加、还没写回"（用户 2026-09-24 第 21 条的口径）。
    ///
    /// <para>判据两条：来源是手动 ⨯ **这个值不在任何一本已记住的密码本里**。
    /// 第二条查的是 <see cref="PasswordService.IsValueInRememberedBooks"/> ——
    /// 它认的是**从文件里解析出来的值**（启动合并每本书时记下、"写回"成功后重读文件刷新）。
    /// 于是"写回成功 → 标记消失 → 重启后仍然消失"是同一个事实的两次查询，而不是两套账。</para>
    ///
    /// <para>没有任何已记住的书 / 书不存在 / 读不出来：查询恒为 false → 标记照旧亮着。
    /// 这是**刻意**的诚实行为：那时程序确实不知道这个值在不在用户的书里，就不许说已写回。</para>
    /// </summary>
    private bool IsUnwrittenManual(PasswordItem? item)
    {
        return item != null
            && string.Equals(item.Source, ManualSource, StringComparison.Ordinal)
            && !IsInRememberedBook(item);
    }

    /// <summary>这一条的值是不是已经躺在某一本已记住的密码本里。</summary>
    private bool IsInRememberedBook(PasswordItem item)
    {
        string value = item.Value ?? string.Empty;

        return value.Length > 0 && _passwordService.IsValueInRememberedBooks(value);
    }
}
