namespace ArchiveFixer.ViewModels;

/// <summary>设置页/打包页的打开、打包选择落盘、恢复默认。</summary>
internal sealed class SettingsCommandsCoordinator
{
    private readonly MainViewModel _vm;

    public SettingsCommandsCoordinator(MainViewModel vm)
    {
        _vm = vm;
    }

    internal void OpenSettings()
    {
        RequestTab(MainViewModel.SettingsTabIndex, "设置已经搬进「设置」选项卡（原来的设置窗口已退休）。");
    }

    internal void OpenPacking()
    {
        RequestTab(MainViewModel.PackingTabIndex, "打包已经搬进「打包」选项卡（原来的打包窗口已退休）。");
    }

    internal void RequestTab(int index, string log)
    {
        _vm.AppendLog("INFO", log);

        _vm.RaiseTabRequested(index);
    }

    internal void SavePackingSettings()
    {
        /*
         * 与自动保存**同一道闸门**：设置里有非法值（工具路径指向一个不存在的文件）时先不落盘并说清 ——
         * ⛔ 打包这条路不许变成"把非法设置偷偷写进盘"的后门（那一格决定引擎，写进去下次启动就照它走）。
         */
        string? blocked = _vm.SettingsEditor?.DescribeAutoSaveBlock();

        if (!string.IsNullOrEmpty(blocked))
        {
            _vm.AppendLog("WARN", "打包的选择还没写进设置（改成合法值之后会自动存）：" + blocked);
            return;
        }

        _vm.WriteSettingsToDisk("打包的选择");
    }

    internal void ResetSettings()
    {
        bool confirm = _vm.DialogService.ShowConfirm("确定要恢复默认设置吗？");

        if (!confirm)
        {
            return;
        }

        _vm.Settings = _vm.SettingsService.ResetToDefault();

        // ⛔ 恢复默认也是"搬值"：走 SyncOutputLocationFromSettings，不许让 setter 把落点档位再改一遍。
        _vm.SyncOutputLocationFromSettings();
        _vm.RefreshOutputPaths();

        /*
         * 恢复默认会把并发档、落点、源包/其余物那几档一起改回默认值 ——
         * 那几个展示属性是"包在设置外面"的（AppSettings 不发通知），
         * 不显式刷新的话页面上还会停着改之前那句话。
         *
         * ⚠ ②页那个「全速」勾选框也是这一类（2026-09-26 同步审计逮到）：它的 getter 读
         * `Settings.RunAtFullSpeed`，恢复默认把它清成 false —— 不通知的话界面还勾着"全速"，
         * 而真正干活的那一套已经按并发档排队了。
         */
        _vm.RaiseSettingsChanged();
        _vm.RaiseRunAtFullSpeedChanged();
        _vm.RefreshSpaceModeText();
        _vm.NotifyOutputPlacementChangedEverywhere();
        _vm.SettingsEditor?.NotifyProcessingOptionsChanged();
        _vm.SettingsEditor?.NotifyRememberedBooksChanged();

        _vm.AppendLog("INFO", "已恢复默认设置");
    }
}

