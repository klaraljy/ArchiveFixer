namespace ArchiveFixer.Engines;

/// <summary>
/// 一次工具解析结果"从哪一档来"。
///
/// <para>来源和路径放在同一条结果里（见 <see cref="ToolLocation"/>），调用方不用再靠几个互相独立的
/// bool 拼"到底用的是自选、已装还是内置" —— 旧实现里那几个字段正是最容易出现
/// "路径换了、来源没换"的地方。</para>
/// </summary>
internal enum ToolLocationSource
{
    /// <summary>没找到：<see cref="ToolLocation.ExecutablePath"/> 为 null，由调用方回落到"本来应该在的位置"。</summary>
    NotFound,

    /// <summary>用户在设置里自选的那一份。</summary>
    Custom,

    /// <summary>随程序分发的内置那一份。</summary>
    Bundled,

    /// <summary>用户已装 WinRAR 目录里的那一份（只读探测，绝不复制）。</summary>
    WinRarInstallation,

    /// <summary>已装 WinRAR 目录里的 GUI 版 <c>WinRAR.exe</c>（打包侧用它顶替 Rar.exe，要补 <c>-ibck</c>）。</summary>
    WinRarGui
}
