using ArchiveFixer.Extraction;
using ArchiveFixer.Models;

namespace ArchiveFixer.ViewModels;

/// <summary>一键处理选项/「以后不再询问」的设置落盘。</summary>
internal static class OneClickOptionsStore
{
    internal static void SaveAsDefaults(MainViewModel vm, OneClickRunOptions options)
    {
        if (options == null || vm.Settings == null)
        {
            return;
        }

        if (options.IsPlacementValid)
        {
            bool custom = OutputPlacement.UsesCustomRoot(options.PlacementMode);

            /*
             * 落点的真值是用户可见的两个布尔 + 自定义根；PlacementMode 只是为了兼容旧配置保留。
             * 这里必须把三者同时写成一致状态，不能只写 PlacementMode：
             * 下一次启动会从布尔重新派生，单写 enum 会被 Normalize 立刻纠正回旧落点。
             */
            vm.Settings.ExtractToOriginalDirectory = !custom;
            vm.Settings.KeepArchiveNameFolder = true;
            vm.Settings.PlacementMode = options.PlacementMode;

            /*
             * 自定义根只在"指定位置"两档下才有意义，也只在非空时才写 ——
             * 空串写进去等于把用户上次选的目录抹掉（而落点那两档并不需要它）。
             */
            if (custom && !string.IsNullOrWhiteSpace(options.CustomRoot))
            {
                vm.Settings.CustomOutputDirectory = options.CustomRoot.Trim();

                // 主界面"输出目录"那一格与设置保持同一个值（它同时也是解压管线用的那个）。
                vm.SetSelectedOutputDirectoryRaw(vm.Settings.CustomOutputDirectory);
            }
        }

        // 终端落法那一档**已退役**（用户 2026-09-27 删除）：弹窗里不再有它，也不写回设置。
        vm.Settings.SourceHandling = AppSettings.ToSourceHandlingValue(options.SourceHandling);

        /*
         * 「删除操作」那一档（2026-09-25 第 33 条补）：弹窗的折叠区里也能改它，
         * 勾了「存为默认」就一起写回 —— 否则用户下次会发现"我在弹窗里选的没被记住"。
         */
        vm.Settings.RestHandlingAfterVerify = RestHandlingModes.Normalize(options.RestHandling);

        vm.Settings.Normalize();

        /*
         * 确认框里改过落点、又选了「保存为默认」→ ①页那一行与②页那几个控件都要跟着走
         * （这里也是直接写 vm.Settings 的路径之一，2026-09-25 第 31 条同一口径）。
         */
        vm.NotifyOutputPlacementChangedEverywhere();

        if (!vm.WriteSettingsToDisk("把本次选项存为默认"))
        {
            vm.DialogService.ShowError("把本次选项存为默认失败：设置文件写不进去（磁盘只读 / 被占用？）。");
            return;
        }

        vm.RaiseSettingsChanged();
        vm.RefreshOutputPaths();
        vm.UpdateSummary();

        vm.AppendLog("INFO", $"本次选项已写入设置（{vm.PathService.SettingsFilePath}）。");
    }

    internal static void SaveSkipConfirm(MainViewModel vm, bool skip)
    {
        if (vm.Settings == null || vm.Settings.SkipOneClickConfirm == skip)
        {
            return;
        }

        vm.Settings.SkipOneClickConfirm = skip;

        if (!vm.WriteSettingsToDisk("「以后不再询问」"))
        {
            return;
        }

        // ②页那个同义开关绑的是 vm.SettingsEditor.vm.Settings（POCO 不发通知）—— 不喊这一声它还显示着"会弹"。
        vm.RaiseSettingsChanged();
        vm.SettingsEditor?.NotifyProcessingOptionsChanged();
    }
}


