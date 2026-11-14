using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 设置页里"要弹一个系统对话框 / 要打开浏览器"的那几条命令：选择输出目录、选择归集目录、
/// 选择用户自己装的 <c>Rar.exe</c>、打开 WinRAR 官方下载页。
///
/// <para>本类只搬"弹窗 + 把结果写回设置 + 说一句"；通知与派生刷新仍由主类的回调接住
/// （落点那几档、引擎 / RAR 预览都不是本类能算的）。</para>
///
/// <para>⛔ 用户可见文案全部来自 <c>StatusText</c> 或与拆分前逐字相同；WinRAR 那一颗只**指路** ——
/// 不下载、不代装、不捆绑（RARLAB EULA §3b/§3c）。</para>
/// </summary>
internal sealed class SettingsDialogCommands
{
    private readonly DialogService _dialogService;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<Action<string>?> _outputDirectoryPicked;
    private readonly Action<string> _notify;
    private readonly Action _raiseOutputPlacementChanged;
    private readonly Action<string> _setMessage;
    private readonly Action<string> _setCustomRarExePath;

    internal SettingsDialogCommands(
        DialogService dialogService,
        Func<AppSettings> getSettings,
        Func<Action<string>?> outputDirectoryPicked,
        Action<string> notify,
        Action raiseOutputPlacementChanged,
        Action<string> setMessage,
        Action<string> setCustomRarExePath)
    {
        _dialogService = dialogService;
        _getSettings = getSettings;
        _outputDirectoryPicked = outputDirectoryPicked;
        _notify = notify;
        _raiseOutputPlacementChanged = raiseOutputPlacementChanged;
        _setMessage = setMessage;
        _setCustomRarExePath = setCustomRarExePath;
    }

    /// <summary>
    /// ②页「指定位置 → 选择…」。
    ///
    /// <para>⚠ 刻意**不建目录**（用户 2026-09-25 第 28 条）：选择位置只是改设置 ——
    /// 挑完又改主意时，盘上不该留下一个空的"指定位置"目录。目录只在定稿那一步才建。</para>
    ///
    /// <para>换输出位置 = **换了一块盘**（用户 2026-09-27 第 2 条），所以选完要喊一声
    /// （<c>OutputDirectoryPicked</c>）：真正做空间体检的是主视图模型，本类只负责"说一声"。</para>
    /// </summary>
    internal void SelectOutputDirectory()
    {
        try
        {
            string folder = _dialogService.ShowFolderBrowserDialog();

            if (string.IsNullOrWhiteSpace(folder))
            {
                _setMessage("已取消选择输出目录。");
                return;
            }

            AppSettings settings = _getSettings();

            settings.CustomOutputDirectory = folder;
            settings.ExtractToOriginalDirectory = false;

            // 选了自定义目录 = 切到"指定位置 + 同名子文件夹"那一档（只剩这一档用得上路径）。
            settings.KeepArchiveNameFolder = true;

            _notify(nameof(SettingsViewModel.Settings));
            _notify(nameof(SettingsViewModel.CustomOutputDirectory));
            _raiseOutputPlacementChanged();

            _outputDirectoryPicked()?.Invoke(folder);

            _setMessage("已选择输出目录。");
        }
        catch (Exception ex)
        {
            ArchiveFixer.Helpers.AppDiagnostics.Problem("设置落盘", "降级处理", ex);
            _setMessage("选择输出目录失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 选择结果归集的目标目录。
    /// 这里**不**动落点开关 —— 归集是解压之后的一步，和"解压到哪里"是两件事，
    /// 顺手改掉会让用户莫名其妙地换了输出位置。
    /// </summary>
    internal void SelectCollectTargetDirectory()
    {
        try
        {
            string folder = _dialogService.ShowFolderBrowserDialog();

            if (string.IsNullOrWhiteSpace(folder))
            {
                _setMessage("已取消选择归集目录。");
                return;
            }

            _getSettings().CollectTargetDirectory = folder;

            _notify(nameof(SettingsViewModel.Settings));
            _setMessage("已选择归集目标目录。");
        }
        catch (Exception ex)
        {
            ArchiveFixer.Helpers.AppDiagnostics.Problem("设置落盘", "降级处理", ex);
            _setMessage("选择归集目录失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 浏览选择用户自己装的 <c>Rar.exe</c>（打包做外层 rar 时用）。
    /// 7z / UnRAR 那两格是纯文本框（历史如此），这一格按用户要求配一个"浏览"按钮 ——
    /// WinRAR 常常装在非默认目录，手打路径最容易出错。
    /// </summary>
    internal void SelectRarExe()
    {
        try
        {
            string picked = _dialogService.ShowOpenSingleFileDialog(
                "选择你自己安装的 WinRAR 里的 Rar.exe",
                "Rar.exe (Rar.exe)|Rar.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*");

            if (string.IsNullOrWhiteSpace(picked))
            {
                _setMessage("已取消选择 Rar.exe。");
                return;
            }

            _setCustomRarExePath(picked);

            _setMessage("已选择 Rar.exe；改完立刻自动存（见底栏那一行）。" + StatusText.SettingsRarExePathHint);
        }
        catch (Exception ex)
        {
            ArchiveFixer.Helpers.AppDiagnostics.Problem("设置落盘", "降级处理", ex);
            _setMessage("选择 Rar.exe 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 打开 WinRAR 官方下载页（用户 2026-10-05：「在设置里面写一个推荐一同安装 WinRAR」）。
    ///
    /// <para>⛔ 只**指路**：网址走 <see cref="SafePathHelper.OpenExternalLink"/> 的白名单出口
    /// （不下载、不代装、不把安装包打进我们自己的 setup.exe —— RARLAB EULA §3b/§3c 禁止 bundling）。
    /// 打不开浏览器 ⇒ 如实说清并给出官网域名，⛔ 不静默失败。</para>
    /// </summary>
    internal void OpenWinRarDownloadPage()
    {
        try
        {
            if (SafePathHelper.OpenExternalLink(SettingsViewModel.WinRarDownloadUrl))
            {
                _setMessage(StatusText.SettingsWinRarDownloadOpenedMessage);
                return;
            }

            _setMessage(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SettingsWinRarDownloadFailedFormat,
                "系统里没有可用的浏览器关联"));
        }
        catch (Exception ex)
        {
            ArchiveFixer.Helpers.AppDiagnostics.Problem("设置落盘", "降级处理", ex);
            _setMessage(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SettingsWinRarDownloadFailedFormat,
                ex.Message));
        }
    }
}
