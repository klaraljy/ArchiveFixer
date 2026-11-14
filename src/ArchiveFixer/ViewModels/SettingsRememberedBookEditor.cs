using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// ④页「已记住的密码本」那一组的设置分组：清单刷新 + 逐项移除。
///
/// <para>清单项类型仍是主类的 <c>RememberedBookItem</c>（XAML 的 ItemsControl 直接绑它），
/// 本类只搬"读写清单"这一件事；界面属性名与通知名一个都没改。</para>
///
/// <para>⚠ 移除只改**清单**：磁盘上的密码本文件一个字节都不动，界面上的文案也这么写
/// （用户会担心"移除"是不是把文件删了）。</para>
/// </summary>
internal sealed class SettingsRememberedBookEditor
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<string> _notify;
    private readonly Action<string> _setMessage;

    internal SettingsRememberedBookEditor(
        Func<AppSettings> getSettings,
        Action<string> notify,
        Action<string> setMessage,
        ObservableCollection<SettingsViewModel.RememberedBookItem> items)
    {
        _getSettings = getSettings;
        _notify = notify;
        _setMessage = setMessage;
        Items = items;
    }

    /// <summary>"已记住的密码本"列表（顺序 = 启动时的合并顺序；实例由主类持有并暴露给 XAML）。</summary>
    internal ObservableCollection<SettingsViewModel.RememberedBookItem> Items { get; }

    /// <summary>
    /// 把清单刷成设置里的真实内容（不猜、不缓存）。
    /// 装载设置、恢复默认、以及移除一项之后都要重来一遍，否则界面上会留着已经移除的项。
    /// </summary>
    internal void Refresh()
    {
        Items.Clear();

        foreach (string path in _getSettings().PasswordBookPaths ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                Items.Add(new SettingsViewModel.RememberedBookItem { Path = path });
            }
        }

        _notify(nameof(SettingsViewModel.HasRememberedBooks));
        _notify(nameof(SettingsViewModel.IsRememberingPasswordList));
        _notify(nameof(SettingsViewModel.ShowRememberedBooksDisabledHint));
    }

    /// <summary>
    /// 从清单里移除一本（用户 2026-09-24 要求：已记住的密码本能逐项移除）。
    /// </summary>
    internal void Remove(object? parameter)
    {
        string path = parameter switch
        {
            SettingsViewModel.RememberedBookItem item => item.Path,
            string text => text,
            _ => string.Empty
        };

        AppSettings settings = _getSettings();

        if (string.IsNullOrWhiteSpace(path) || settings == null)
        {
            return;
        }

        var remaining = new List<string>();

        foreach (string existing in settings.PasswordBookPaths ?? new List<string>())
        {
            if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                remaining.Add(existing);
            }
        }

        settings.PasswordBookPaths = remaining;

        /*
         * 老字段跟着走：Normalize() 会把 PasswordBookPath 并回清单（那是给"回退到旧版本"用的迁移），
         * 不更新它的话，刚移除的这本会在保存时**被迁移逻辑加回来** —— 用户点了移除却还在。
         * 清单空了就一并清空（保存时 Normalize 也不会再补出东西来）。
         */
        settings.PasswordBookPath = remaining.Count > 0 ? remaining[remaining.Count - 1] : string.Empty;

        Refresh();

        _setMessage(
            $"已从「已记住的密码本」里移除「{System.IO.Path.GetFileName(path)}」；改完立刻自动存（见底栏那一行）。"
            + "磁盘上的那个文件不会被删除，也不会被修改。");
    }
}
