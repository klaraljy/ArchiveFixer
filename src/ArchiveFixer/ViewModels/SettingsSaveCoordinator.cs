using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 设置页那条"保存"命令的落盘动作：保存前校验 → 快照 → 归一化 → 推给引擎层 → 真写盘 → 说结果。
///
/// <para>它同时是"名为保存、其实不保存"这个历史缺陷的修复点：本方法以前只做校验 + 归一化 + 写
/// <c>Message</c>，落盘一直由主窗口那颗「保存设置」按钮补一刀 —— 那颗按钮随自动保存退役之后，
/// ⛔ 名字必须与事实一致：要么改名，要么真存，这里选后者。</para>
///
/// <para>校验与自动保存那条路（<c>SettingsPersistenceCoordinator</c>）走的是
/// <see cref="SettingsValueValidation.DescribeAutoSaveBlock"/> 这一份实现，⛔ 两条路不许各写一套。</para>
///
/// <para>幂等性：保存的**同时**把引擎相关的项推给引擎层（优先级 / 两条工具路径 / 保留受损文件），
/// 与主窗口的 <c>ApplyEngineSettings</c> 幂等，谁先谁后都不会打架。</para>
/// </summary>
internal sealed class SettingsSaveCoordinator
{
    private readonly SettingsService _settingsService;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<string> _setMessage;
    private readonly Action<bool?> _setDialogResult;
    private readonly Action _refreshPreviews;

    internal SettingsSaveCoordinator(
        SettingsService settingsService,
        Func<AppSettings> getSettings,
        Action<string> setMessage,
        Action<bool?> setDialogResult,
        Action refreshPreviews)
    {
        _settingsService = settingsService;
        _getSettings = getSettings;
        _setMessage = setMessage;
        _setDialogResult = setDialogResult;
        _refreshPreviews = refreshPreviews;
    }

    /// <summary>执行一次保存（用户点「保存」时走这里，与自动保存那条路共用同一套校验）。</summary>
    internal void Save()
    {
        try
        {
            /*
             * 先复位"最近一次保存的结果"。它是**一次**保存的结论，不是历史累计：
             * 选项卡形态下窗口不再关闭，上一次的成功标记留着会让"这次校验没过"
             * 被读成"这次成功了"（调用方正是按 DialogResult 决定要不要应用设置的）。
             */
            _setDialogResult(null);

            /*
             * 两条外部工具路径**拦在保存之前**。
             *
             * 为什么必须拦：Normalize() 会把"文件不存在"的工具路径直接清空，
             * 于是"填错路径 → 保存 → 看到『设置已保存』"是一条**静默丢弃用户输入**的路。
             * 宁可停在窗口里说清楚，也不要假装保存成功。
             */
            string? blocked = SettingsValueValidation.DescribeAutoSaveBlock(_getSettings());

            if (blocked != null)
            {
                _setMessage("设置未保存：" + blocked);
                return;
            }

            /*
             * 危险模式那一段保存前拦截**已随该功能整块退役**（用户 2026-09-25 第 32 条）。
             * 现在③页那两档「删除操作」不需要任何凭证：选了「彻底删除」时界面会在选项下面
             * 常驻一条不可关闭的红字提示，保存不再被拦。
             */
            AppSettings settings = _getSettings();

            /*
             * Normalize() 会把超范围的数字夹回合法区间（密码尝试上限 5000 → 1000；安全上限 8192 GiB → 4096）。
             * 静默改掉用户填的数字是"我以为我设成了 5000"的经典来源 —— 所以**先留一份快照**，
             * 归一化之后再比一次，夹过就说清楚（判据与措辞只有一处实现：SettingsValueValidation）。
             */
            SettingsViewModel.SettingsClampSnapshot requested =
                SettingsViewModel.SettingsClampSnapshot.Capture(settings);

            settings.Normalize();

            /*
             * 保存的**同时**把引擎相关的项推给引擎层（优先级 / 两条工具路径 / 保留受损文件）。
             *
             * 为什么在这里推：设置界面是用户改这些值的唯一入口，而引擎选择发生在
             * "下一次点击处理"那一刻 —— 不推的话，用户改完顺序、关掉窗口、立刻处理一个包，
             * 用的还是旧顺序（"改了没反应"）。
             */
            EngineRuntimeSettings.Apply(settings);
            _refreshPreviews();

            string clampNotice = SettingsValueValidation.DescribeClampNotice(requested, settings);

            if (!_settingsService.Save(settings))
            {
                _setMessage("设置没能写进磁盘（磁盘只读 / 被占用？）—— 请检查程序目录是否可写。");
                _setDialogResult(false);
                return;
            }

            _setMessage(clampNotice.Length > 0 ? "设置已保存" + clampNotice : "设置已保存。");

            _setDialogResult(true);
        }
        catch (Exception ex)
        {
            ArchiveFixer.Helpers.AppDiagnostics.Problem("设置落盘", "降级处理", ex);
            _setMessage("保存设置失败：" + ex.Message);
        }
    }
}
