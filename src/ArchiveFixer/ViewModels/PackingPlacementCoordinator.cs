using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Services;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// ⑤打包页的「落点」那一组：设置 ↔ 页面两档的读写、那行「落点：…」的算法、挑目录。
///
/// <para>落点规则本身在 <see cref="PackingPaths"/>；这里只做"页面 ↔ 设置 ↔ 请求选项"的搬运，
/// 不建目录、不动磁盘。默认档 = **源旁边**（用户原话："默认放在源文件目录旁边"）。</para>
/// </summary>
internal sealed class PackingPlacementCoordinator
{
    private readonly PackingViewModel _vm;
    private readonly DialogService _dialogService;

    internal PackingPlacementCoordinator(PackingViewModel viewModel, DialogService dialogService)
    {
        _vm = viewModel;
        _dialogService = dialogService;
    }

    /// <summary>把设置里那一套读进页面（打开⑤页 / 换设置对象时）。只改状态与通知，⛔ 不写回设置。</summary>
    internal void LoadFromSettings()
    {
        AppSettings settings = _vm.Settings;

        _vm.ApplyPlacementFromSettings(
            PackingRunOptions.ParseTargetMode(settings.PackTargetMode) == PackingTargetMode.Custom,
            settings.PackCustomOutputDirectory);
    }

    /// <summary>把页面上的两档写回设置（"相应的保存记忆操作"）。</summary>
    internal void Persist()
    {
        AppSettings settings = _vm.Settings;

        settings.PackTargetMode = (_vm.PlacementIsCustom
            ? PackingTargetMode.Custom
            : PackingTargetMode.Local).ToString();

        settings.PackCustomOutputDirectory = _vm.CustomPlacementDirectory;

        _vm.SettingsChanged?.Invoke();
    }

    /// <summary>
    /// 页面这几档 → 一套请求选项（⛔ 唯一出口：预览、弹窗抬头、真跑都引它）。
    ///
    /// <para>落点取**页面**上那两档；原包 / 其余物那两档取设置里记得的那一套。</para>
    /// </summary>
    internal PackingRunOptions BuildOptions()
    {
        PackingRunOptions saved = PackingRunOptions.FromSettings(_vm.Settings);

        return new PackingRunOptions
        {
            TargetMode = _vm.PlacementIsCustom ? PackingTargetMode.Custom : PackingTargetMode.Local,
            CustomOutputDirectory = _vm.CustomPlacementDirectory,
            SourceHandling = saved.SourceHandling,
            RestHandling = saved.RestHandling
        };
    }

    /// <summary>重算那行「落点」（很便宜：只解析路径，不扫源目录）。</summary>
    internal string Describe()
    {
        if (string.IsNullOrWhiteSpace(_vm.SourceFolder))
        {
            return "落点：还没选源 —— 先选一个文件夹（或一个文件）。";
        }

        PackingSourceResolution resolution = PackingSourceResolver.Resolve(_vm.SourceFolder);

        if (!resolution.Success)
        {
            return "落点：" + resolution.Reason;
        }

        PackingRunOptions options = BuildOptions();
        string directory = PackingPaths.ResolveTargetDirectory(resolution, options);

        return options.TargetMode switch
        {
            PackingTargetMode.Custom => $"落点：指定位置 —— {directory}",
            _ => $"落点：默认（源旁边）—— {directory}"
        };
    }

    /// <summary>「选择…」：挑一个目录（⛔ 只改设置，**不建目录** —— 与①页同一口径）。</summary>
    internal void PickDirectory()
    {
        string? picked = _dialogService.ShowFolderBrowserDialog(
            "选择最终压缩包放在哪",
            string.IsNullOrWhiteSpace(_vm.CustomPlacementDirectory) ? _vm.SourceFolder : _vm.CustomPlacementDirectory);

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        // 挑了目录 = 用户要"指定位置"，顺手把那一档切过去（他不会希望挑了却不生效）。
        _vm.ApplyPlacementPicked(picked);
    }
}
