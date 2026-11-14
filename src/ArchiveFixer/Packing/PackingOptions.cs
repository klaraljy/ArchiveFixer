using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing;

/// <summary>用户选的东西是**文件夹**还是**单个文件**（用户 2026-09-26 第 46 条要求支持单文件）。</summary>
public sealed class PackingRunOptions
{
    /// <summary>最终 <c>.rar</c> 放哪：**默认 = 源旁边** / 指定位置。</summary>
    public PackingTargetMode TargetMode { get; init; } = PackingTargetMode.Local;

    /// <summary>指定位置的目录（<see cref="PackingTargetMode.Custom"/> 时用）。</summary>
    public string CustomOutputDirectory { get; init; } = string.Empty;

    /// <summary>原包操作：不动（默认）/ 移入其余物。</summary>
    public PackingSourceHandling SourceHandling { get; init; } = PackingSourceHandling.KeepInPlace;

    /// <summary>其余物操作：不动 / 回收站 / 彻底删除（**默认彻底删除**）。</summary>
    public PackingRestHandling RestHandling { get; init; } = PackingRestHandling.Delete;

    /// <summary>从设置里读一套（打开弹窗时的初值）。</summary>
    public static PackingRunOptions FromSettings(AppSettings? settings)
    {
        if (settings == null)
        {
            return new PackingRunOptions();
        }

        return new PackingRunOptions
        {
            TargetMode = ParseTargetMode(settings.PackTargetMode),
            CustomOutputDirectory = settings.PackCustomOutputDirectory ?? string.Empty,
            SourceHandling = ParseSourceHandling(settings.PackSourceHandling),
            RestHandling = ParseRestHandling(settings.PackRestHandling)
        };
    }

    /// <summary>写回设置（"相应的保存记忆操作"）。</summary>
    public void SaveTo(AppSettings? settings)
    {
        if (settings == null)
        {
            return;
        }

        settings.PackTargetMode = TargetMode.ToString();
        settings.PackCustomOutputDirectory = CustomOutputDirectory ?? string.Empty;
        settings.PackSourceHandling = SourceHandling.ToString();
        settings.PackRestHandling = RestHandling.ToString();
    }

    /// <summary>
    /// ⚠ 这一档会**连原包一起删掉**：原包移入其余物 + 其余物被删。
    /// 界面必须当场红字说清（用户 2026-09-26 见过这条说明）。
    /// </summary>
    public bool WouldDeleteSource =>
        SourceHandling == PackingSourceHandling.MoveToRest && RestHandling != PackingRestHandling.Keep;

    /// <summary>
    /// 那句红字（不发生时为 <c>null</c>）。
    ///
    /// <para>⛔ 这里不许写 Markdown 的星号加粗：WPF 不认它，弹窗里会**原样**显示成星号
    /// （2026-09-26 审计；<c>UserFacingTextTests</c> 会拦）。强调用「」。</para>
    /// </summary>
    public string? SourceLossWarning => WouldDeleteSource
        ? "⚠ 你选的是「原包移入其余物」+「其余物" + RestHandlingText + "」—— "
          + "这等于「连你的原文件夹/原文件一起" + (RestHandling == PackingRestHandling.Delete ? "彻底删除" : "丢进回收站") + "」。"
          + "只想留 .rar 的话，把原包操作改成「不动」。"
        : null;

    /// <summary>其余物那一档的中文（界面上单选项上的字）。</summary>
    public string RestHandlingText => RestHandling switch
    {
        PackingRestHandling.RecycleBin => "移入回收站",
        PackingRestHandling.Delete => "彻底删除",
        _ => "不动"
    };

    /// <summary>原包那一档的中文。</summary>
    public string SourceHandlingText => SourceHandling switch
    {
        PackingSourceHandling.MoveToRest => "移入其余物",
        _ => "不动"
    };

    /// <summary>落点那一档的中文。</summary>
    public string TargetModeText => TargetMode == PackingTargetMode.Custom
        ? "指定位置"
        : "默认（源旁边）";

    /// <summary>进日志的一句话（⛔ 不含密码）。</summary>
    public string DescribeForLog() =>
        $"落点：{TargetModeText}"
        + (TargetMode == PackingTargetMode.Custom && !string.IsNullOrWhiteSpace(CustomOutputDirectory)
            ? $"（{CustomOutputDirectory}）"
            : string.Empty)
        + $"；原包操作：{SourceHandlingText}；其余物操作：{RestHandlingText}";

    public static PackingTargetMode ParseTargetMode(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out PackingTargetMode parsed) ? parsed : PackingTargetMode.Local;

    public static PackingSourceHandling ParseSourceHandling(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out PackingSourceHandling parsed)
            ? parsed
            : PackingSourceHandling.KeepInPlace;

    public static PackingRestHandling ParseRestHandling(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out PackingRestHandling parsed)
            ? parsed
            : PackingRestHandling.Delete;
}

/// <summary>

