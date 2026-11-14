using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 设置自动保存/写盘唯一出口；MainViewModel 只保留薄转发。
/// </summary>
internal sealed class SettingsPersistenceCoordinator
{
    private readonly SettingsService _settingsService;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<SettingsViewModel?> _editor;
    private readonly Action<string, string> _log;
    private readonly Action _applySideEffects;
    private string _lastSavedSettingsJson = string.Empty;
    private string _lastAutoSaveBlockNote = string.Empty;
    private IDisposable? _settingsAutoSaveTimer;

    public SettingsPersistenceCoordinator(
        SettingsService settingsService,
        Func<AppSettings> getSettings,
        Func<SettingsViewModel?> editor,
        Action<string, string> log,
        Action applySideEffects)
    {
        _settingsService = settingsService;
        _getSettings = getSettings;
        _editor = editor;
        _log = log;
        _applySideEffects = applySideEffects;
    }

    internal void StartSettingsAutoSaveTimer()
    {
        _settingsAutoSaveTimer = UiThread.StartTimer?.Invoke(
            MainViewModel.SettingsAutoSaveInterval,
            () => AutoSaveSettingsIfChanged());
    }

    internal bool AutoSaveSettingsIfChanged()
    {
        try
        {
            /*
             * ⚠ 快照必须在 Serialize **之前**取：Serialize 会顺手 Normalize（那正是它作为唯一序列化出口
             * 的职责），而 Normalize 会把超范围的数字**夹回**合法区间 —— 夹过就必须让用户看见
             * （用户 2026-09-25 第 36 条："不许静默改掉你填的数字"）。
             * 以前这件事是"点保存"那条路在 `SettingsViewModel.Message` 里说的，而现在保存是自动的、
             * 那个框也已经删掉 —— 所以改到这里：夹回的结论并进底栏那一行（见 DescribeClampNotice）。
             */
            SettingsViewModel.SettingsClampSnapshot requested = SettingsViewModel.SettingsClampSnapshot.Capture(_getSettings());

            string json = _settingsService.Serialize(_getSettings());

            if (string.Equals(json, _lastSavedSettingsJson, StringComparison.Ordinal))
            {
                return false;
            }

            string? blocked = _editor()?.DescribeAutoSaveBlock();

            if (!string.IsNullOrWhiteSpace(blocked))
            {
                // 只在第一次拦住时写一条（改一次路径、tick 一次，不刷屏）。
                if (!string.Equals(_lastAutoSaveBlockNote, blocked, StringComparison.Ordinal))
                {
                    _lastAutoSaveBlockNote = blocked;
                    _log("WARN", $"设置暂时没有自动保存：{blocked}（改好之后会自动存，不用点任何按钮）");
                }

                return false;
            }

            _lastAutoSaveBlockNote = string.Empty;

            if (!WriteSettingsToDisk("设置"))
            {
                return false;
            }

            _log("INFO", "设置已自动保存（改动即时落盘，重启后仍在）");

            string clampNotice = SettingsViewModel.DescribeClampNotice(requested, _getSettings());

            if (clampNotice.Length > 0)
            {
                _log("WARN", "设置里的数字超出允许范围，已按合法值生效：" + clampNotice);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log("WARN", "自动保存设置失败：" + ex.Message);
            return false;
        }
    }

    internal bool WriteSettingsToDisk(string what)
    {
        if (_settingsService.Save(_getSettings()))
        {
            _lastSavedSettingsJson = _settingsService.Serialize(_getSettings());

            /*
             * ⚠ 收尾动作必须在这里做（2026-09-26 补）：指纹基线一更新，自动保存那一跳就"看不出变化"、
             * 于是**不会再走** _applySideEffects() —— 而那几条"自己写盘"的路
             * （一键处理面板「存为默认」/ 导入密码本 / 记住的密码本 / ①页「选择…」/ 打包那几档）
             * 恰恰都会改到"包在设置外面"的展示属性（落点那一行、并发那句话、密码本摘要）。
             * 放在这个唯一出口上，五条路就都跟着刷新了。
             */
            _applySideEffects();
            return true;
        }

        _log("WARN", $"{what}没能写进设置（写盘失败：磁盘只读 / 被占用？）—— 下次打开会回到上一次的值。");
        return false;
    }

    internal void MarkCurrentSettingsAsSaved() => _lastSavedSettingsJson = _settingsService.Serialize(_getSettings());
    internal void FlushSettingsAutoSave()
    {
        _settingsAutoSaveTimer?.Dispose();
        AutoSaveSettingsIfChanged();
    }
}



