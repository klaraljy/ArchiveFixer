using ArchiveFixer.Models;
using System.IO;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 设置里"用户填的值能不能落盘"的校验与归一化提示（保存前拦截的唯一出口）。
///
/// <para>外部工具路径为什么必须拦在保存之前：<see cref="AppSettings.Normalize"/> 对**不存在的**
/// 工具路径的处理是**直接清空**（以免留一个失效路径让整个程序找不到引擎）。于是
/// "填了一个不存在的路径 → 点保存 → 看到『设置已保存』→ 关窗"这条路上，用户填的路径被静默丢掉：
/// 下次打开设置那一格是空的、真正用的是内置的那一份，而用户以为自己在用自己的那个版本。</para>
///
/// <para>口径是**拦在保存之前**，停在设置界面上说清"填的这个文件不存在"以及"留空是什么行为"，
/// 改完再保存。检验的是"文件存不存在"，不是"它是不是 7z" —— 后者由 <c>ToolLocator</c> / 引擎
/// 自己判定，这里越权猜只会误伤。</para>
///
/// <para>设置页那条路（<c>SaveCommand</c>）与自动保存那条路（<c>SettingsPersistenceCoordinator</c>）
/// 走的是**同一份实现**，⛔ 自动保存不许绕过这道闸门。</para>
/// </summary>
internal static class SettingsValueValidation
{
    /// <summary>7z.exe 那一格留空时的白话说明（保存拦截与自动保存拦截共用一份措辞）。</summary>
    private const string SevenZipEmptyHint =
        "留空表示用程序目录下内置的 tools\\7zip\\7z.exe。";

    /// <summary>UnRAR.exe 那一格留空时的白话说明（说明"自动解析"的两级顺序）。</summary>
    private const string UnRarEmptyHint =
        "留空表示自动解析：先找本机已装 WinRAR 目录里的 UnRAR.exe，再退回程序内置的 tools\\unrar\\UnRAR.exe。";

    /// <summary>
    /// 校验一格外部工具路径：空 / 空白 = 合法（= 自动解析），非空则文件必须真实存在。
    /// </summary>
    internal static bool ValidateToolExePath(string? path, string label, string emptyHint, out string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            message = string.Empty;
            return true;
        }

        if (File.Exists(path.Trim()))
        {
            message = string.Empty;
            return true;
        }

        message = $"{label}指向的文件不存在：{path.Trim()}。" +
                  $"请改成一个真实存在的文件，或者清空这一格 —— {emptyHint}";
        return false;
    }

    /// <summary>
    /// 自动保存前的那道校验（用户 2026-09-26："设置要改了就自动存"）。
    ///
    /// <para>返回 <c>null</c> = 可以落盘；否则返回"为什么先不存"的一句话。</para>
    ///
    /// <para>判据与设置页的 <c>SaveCommand</c> 完全同一套（同一批私有校验方法）——
    /// ⛔ 自动保存绝不能因为"它是自动的"就绕过"工具路径必须存在"这一条：
    /// 它以前是"保存"这一步拦下的，现在保存随时会发生，拦截点必须跟着走。</para>
    /// </summary>
    internal static string? DescribeAutoSaveBlock(AppSettings settings)
    {
        if (!ValidateToolExePath(
                settings.CustomSevenZipExePath,
                "7z.exe 路径",
                SevenZipEmptyHint,
                out string sevenZipMessage))
        {
            return sevenZipMessage;
        }

        if (!ValidateToolExePath(
                settings.CustomUnRarExePath,
                "UnRAR.exe 路径",
                UnRarEmptyHint,
                out string unRarMessage))
        {
            return unRarMessage;
        }

        /*
         * 打包用的 Rar.exe 路径：校验口径与上面两条**完全一致**（同一处实现），
         * 但"留空"的说明必须把那句话带上 —— 这一格是许可边界最容易被误解的地方：
         * 它要的是**用户自己安装的** WinRAR 里的那一份，程序只检测与调用、绝不随包分发。
         * 空白提示直接引 StatusText 的同一份措辞，免得两处各说一套。
         */
        if (!ValidateToolExePath(
                settings.CustomRarExePath,
                "Rar.exe 路径",
                StatusText.SettingsRarExePathEmptyHint,
                out string rarMessage))
        {
            return rarMessage;
        }

        return null;
    }

    /// <summary>
    /// 归一化之后"有没有哪个数字被夹回"的那句话（空字符串 = 一个都没被夹）。
    ///
    /// <para>⛔ 只有这一处实现：设置页那条路把它接在"设置已保存"后面，自动保存那条路把它并进
    /// 底栏（用户 2026-09-26 第 1 条把那个框删掉之后，这里是唯一还会说"你填的数被夹了"的地方）。</para>
    ///
    /// <para>为什么必须先留快照：<see cref="AppSettings.Normalize"/> 会把超范围的值**就地**改成
    /// 合法值，归一化之后再想比就已经晚了 —— 两条保存路都必须**先**取快照
    /// （见 <see cref="SettingsViewModel.SettingsClampSnapshot"/>）。</para>
    /// </summary>
    internal static string DescribeClampNotice(SettingsViewModel.SettingsClampSnapshot requested, AppSettings settings)
    {
        settings ??= new AppSettings();

        bool passwordClamped = requested.PasswordAttempts != settings.MaxPasswordAttemptsPerLayer;

        bool capsClamped =
            requested.SingleFileGiB != settings.MaxSingleExtractedFileGiB ||
            requested.TotalGiB != settings.MaxExtractedTotalGiB ||
            requested.FileCount != settings.MaxExtractedFileCount ||
            !requested.Ratio.Equals(settings.MaxExtractionRatio);

        return (passwordClamped, capsClamped) switch
        {
            (true, true) =>
                $"每层密码尝试上限 {requested.PasswordAttempts} 超出 1~1000、安全上限也有超范围的值，"
                + $"已按 密码 {settings.MaxPasswordAttemptsPerLayer} / 单文件 {settings.MaxSingleExtractedFileGiB} GiB"
                + $" / 总大小 {settings.MaxExtractedTotalGiB} GiB / 文件数 {settings.MaxExtractedFileCount}"
                + $" / 展开比 {settings.MaxExtractionRatio:0.##} 倍 生效。",
            (true, false) =>
                $"每层密码尝试上限 {requested.PasswordAttempts} 超出 1~1000，已按 {settings.MaxPasswordAttemptsPerLayer} 生效。",
            (false, true) =>
                $"安全上限超出允许范围，已按 单文件 {settings.MaxSingleExtractedFileGiB} GiB / 总大小 {settings.MaxExtractedTotalGiB} GiB"
                + $" / 文件数 {settings.MaxExtractedFileCount} / 展开比 {settings.MaxExtractionRatio:0.##} 倍 生效。",
            _ => string.Empty
        };
    }
}
