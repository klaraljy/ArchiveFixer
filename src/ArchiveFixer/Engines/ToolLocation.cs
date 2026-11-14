namespace ArchiveFixer.Engines;

/// <summary>
/// 一条"解析出来的工具位置"：**路径和来源放在一起**，不存在"路径有了、来源还是上一轮"的半截状态。
///
/// <para><see cref="ExecutablePath"/> 为 <c>null</c> 且来源是 <see cref="ToolLocationSource.NotFound"/>
/// 表示**显式没找到**（例如"已装目录"与"内置"两档都被显式关掉的验收场景）：调用方回落到
/// "本来应该在的位置"只用于提示，判否仍走 <c>UnRarExists</c> / <c>RarExists</c> /
/// <c>WinRarExeExists</c> —— ⛔ 绝不拿 <c>File.Exists(路径)</c> 代替那三个判据。</para>
/// </summary>
internal sealed record ToolLocation(
    string? ExecutablePath,
    ToolLocationSource Source)
{
    /// <summary>来源是用户自选那一档。</summary>
    internal bool IsCustom => Source == ToolLocationSource.Custom;

    /// <summary>来源是本机已装 WinRAR 目录（UnRAR 解压引擎那一档）。</summary>
    internal bool IsWinRarInstallation => Source == ToolLocationSource.WinRarInstallation;

    /// <summary>来源是已装 WinRAR 目录里的 GUI 版（打包时顶 Rar.exe，要加 <c>-ibck</c>）。</summary>
    internal bool IsWinRarGui => Source == ToolLocationSource.WinRarGui;

    /// <summary>构造"找到"的结果。</summary>
    internal static ToolLocation Found(string path, ToolLocationSource source)
    {
        return new ToolLocation(path, source);
    }

    /// <summary>构造"没找到"的结果（路径显式为 null，⛔ 不悄悄回落到存在的路径）。</summary>
    internal static ToolLocation NotFound()
    {
        return new ToolLocation(null, ToolLocationSource.NotFound);
    }
}
