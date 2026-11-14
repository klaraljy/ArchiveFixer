using ArchiveFixer.Models;
using ArchiveFixer.Security;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// ②页「内容物保留关键词」那一栏的设置分组（列表 + 单行输入框 + 生效条数说明）。
///
/// <para><b>用户原话（2026-10-04 第二次改口径）</b>：「你这个内容物保留关键词的输入框这么弄的这么大，
/// 你应该弄的像密码一样，而且是放在解压方式里面」。所以形态是**一条条加**：一个单行输入框 +
/// 「添加」（回车也行），下面一行一个词、每条右边一个「移除」—— 与④页密码清单同一种手感。</para>
///
/// <para>真值仍然是设置里那一个字符串数组（<see cref="AppSettings.ContentKeepKeywords"/>，
/// 落盘的键一个字符都没变）—— 「记忆」由自动保存负责，与其它设置项同一条路。
/// 判据唯一出口仍是 <see cref="ContentKeepRules"/>（包含即命中、大小写不敏感、空项忽略、
/// ⛔ 不做通配 / 正则、只吃文件名）；加减两步都**当场归一化**（Trim / 丢空项 / 去重），
/// 再把归一化后的清单**刷回界面**（值对而界面不刷新 = 用户读成"没生效"）。</para>
///
/// <para>本类只搬"这一组的读写 + 通知"，XAML 绑定的属性名仍留在主类上做薄转发。</para>
/// </summary>
internal sealed class SettingsContentKeepKeywordEditor
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<string> _notify;

    private string _newKeyword = string.Empty;

    internal SettingsContentKeepKeywordEditor(Func<AppSettings> getSettings, Action<string> notify)
    {
        _getSettings = getSettings;
        _notify = notify;
    }

    /// <summary>列出来的关键词（顺序 = 用户加的顺序，与落盘那份逐字相同）。</summary>
    internal ObservableCollection<string> Items { get; } = new();

    /// <summary>单行输入框里正在敲的那个词（回车 = 添加，见 <c>ExtractionTab.xaml</c> 的 KeyBinding）。</summary>
    internal string NewKeyword
    {
        get => _newKeyword;

        set
        {
            string normalized = value ?? string.Empty;

            if (string.Equals(_newKeyword, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _newKeyword = normalized;
            _notify(nameof(SettingsViewModel.NewContentKeepKeyword));
            _notify(nameof(SettingsViewModel.CanAddContentKeepKeyword));
        }
    }

    /// <summary>
    /// 「添加」按钮能不能点（输入框空白 ⇒ 禁用）。点了什么都不会发生，不如一开始就说清楚 ——
    /// 否则用户会读成"这个按钮坏了"。
    /// </summary>
    internal bool CanAdd => !string.IsNullOrWhiteSpace(NewKeyword);

    /// <summary>
    /// 那一栏下面那句白话说明 + 当前生效几条（**同一份真值**，不给用户一个"填了不知道有没有用"的框）。
    /// </summary>
    internal string Summary
    {
        get
        {
            int count = ContentKeepRules.NormalizeKeywords(_getSettings().ContentKeepKeywords).Count;

            return count == 0
                ? StatusText.ContentKeepKeywordsEmptySummary
                : string.Format(StatusText.ContentKeepKeywordsActiveSummaryFormat, count);
        }
    }

    /// <summary>
    /// 「添加」：把输入框里那个词并进设置（归一化 + 大小写不敏感去重仍走 <see cref="ContentKeepRules"/>），
    /// 然后清空输入框、刷新列表。
    ///
    /// <para>输入框是空白 ⇒ 列表一个字节都不变（幂等）；已经有过同一个词 ⇒ 列表也不变，
    /// 但输入框照样清空 —— 不留一个"我按了、却好像没反应"的词在里面。</para>
    /// </summary>
    internal void Add()
    {
        string keyword = ContentKeepRules.NormalizeKeywords(new[] { NewKeyword })
            .FirstOrDefault() ?? string.Empty;

        if (keyword.Length == 0)
        {
            return;
        }

        AppSettings settings = _getSettings();

        settings.ContentKeepKeywords = ContentKeepRules.NormalizeKeywords(
            (settings.ContentKeepKeywords ?? new List<string>()).Append(keyword));

        NewKeyword = string.Empty;

        Refresh();
    }

    /// <summary>「移除」：只从清单里去掉这一个（大小写不敏感），别的一个都不动。</summary>
    internal void Remove(object? parameter)
    {
        string keyword = parameter as string ?? string.Empty;

        if (keyword.Length == 0)
        {
            return;
        }

        AppSettings settings = _getSettings();
        var remaining = new List<string>();

        foreach (string existing in ContentKeepRules.NormalizeKeywords(settings.ContentKeepKeywords))
        {
            if (!string.Equals(existing, keyword, StringComparison.OrdinalIgnoreCase))
            {
                remaining.Add(existing);
            }
        }

        settings.ContentKeepKeywords = remaining;

        Refresh();
    }

    /// <summary>
    /// 把关键词列表刷成设置里的真实内容（不猜、不缓存）—— 装载设置、恢复默认、加一条、移除一条都走它。
    /// </summary>
    internal void Refresh()
    {
        Items.Clear();

        foreach (string keyword in ContentKeepRules.NormalizeKeywords(_getSettings().ContentKeepKeywords))
        {
            Items.Add(keyword);
        }

        _notify(nameof(SettingsViewModel.ContentKeepKeywordsSummary));
        _notify(nameof(SettingsViewModel.CanAddContentKeepKeyword));
    }
}
