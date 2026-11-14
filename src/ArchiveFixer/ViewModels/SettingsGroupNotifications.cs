using System;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 设置分组之间「改了一个入口、另一处要跟着重读」的那张通知清单（**唯一出口**）。
///
/// <para><c>AppSettings</c> 是普通 POCO、自己不发通知，所以凡是"包在设置外面"的派生属性
/// （落点那几档、源包 / 删除操作那几档、工具路径那几格……）都必须在这里显式点名；漏掉一个，
/// 界面上就有一块停在旧状态 —— 那正是历史上"改了没反应"的形态。</para>
///
/// <para>本类只负责**说一声**；真正的刷新动作（引擎预览、RAR 状态）由主类接在
/// <c>RaiseOutputPlacementChanged</c> 里，顺序与拆分前一致。</para>
/// </summary>
internal sealed class SettingsGroupNotifications
{
    private readonly Action<string> _notify;

    internal SettingsGroupNotifications(Action<string> notify)
    {
        _notify = notify;
    }

    /// <summary>
    /// ③页「源包操作 / 删除操作」那一组：主类漏过 <see cref="SettingsViewModel.RestHandling"/> 一次，
    /// 真机形态是"关掉一键处理的弹窗回到③页，删除操作那组三个单选一个黑点都没有"。
    /// </summary>
    internal void NotifyProcessingOptions()
    {
        _notify(nameof(SettingsViewModel.SourceHandling));
        _notify(nameof(SettingsViewModel.RestHandling));
        _notify(nameof(SettingsViewModel.IsRestDeleteSelected));
        _notify(nameof(SettingsViewModel.RestHandlingSummary));

        /*
         * 还有一批设置项是**直接**绑 `SettingsEditor.Settings.X` 的（②页「以后不再询问」、
         * ③页「导入后提醒」、⑥页那十几格……）。AppSettings 是普通 POCO、自己不发通知，
         * 所以"值被别的入口改了"之后，这些控件不会重新求值 —— 真机形态是：
         * 弹窗里勾了「以后不再询问」（确实生效、弹窗真的不再出现），回②页一看那个勾**还在没勾**。
         *
         * 通知 `Settings` 本身 = 让所有 `...Settings.X` 的绑定重新求值一次（OneWay 会重读）。
         */
        _notify(nameof(SettingsViewModel.Settings));
    }

    /// <summary>
    /// ②页落点那一组派生属性。这几个属性是"包在 Settings 外面"的（AppSettings 不实现
    /// INotifyPropertyChanged），恢复默认 / 重新加载设置 / 换输出位置之后必须显式通知，
    /// 否则界面还显示旧值（值写对了、界面显示旧值 = 用户读成"没生效"）。
    /// </summary>
    internal void NotifyOutputPlacement()
    {
        _notify(nameof(SettingsViewModel.OutputPlacement));
        _notify(nameof(SettingsViewModel.IsCustomOutputEnabled));
        _notify(nameof(SettingsViewModel.OutputPlacementSummary));
        _notify(nameof(SettingsViewModel.CustomOutputDirectory));

        _notify(nameof(SettingsViewModel.CustomUnRarExePath));
        _notify(nameof(SettingsViewModel.CustomRarExePath));
        _notify(nameof(SettingsViewModel.KeepBrokenFiles));
    }
}
