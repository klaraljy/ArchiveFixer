namespace ArchiveFixer.Engines;

/// <summary>
/// 解析结果的进程内**缓存**：每种工具只解析一次，直到 <see cref="Invalidate"/> 被调用。
///
/// <para>缓存的值是**整条结果**（路径 + 来源），不是零散字段 —— 这样不存在"路径换了、来源标记
/// 还是上一轮的"那种半截状态。7-Zip、UnRAR、Rar.exe、WinRAR.exe 四条链各占一格。</para>
///
/// <para>⛔ 这里不做探测、不读文件：只负责"记住上一次解析的结果"。解析顺序在
/// <see cref="ToolDiscovery"/>，失效由 <see cref="ToolLocator"/> 在设置变更后触发。</para>
/// </summary>
internal sealed class ToolPathCache
{
    /// <summary>7-Zip 的 exe + dll（成对解析，见 <see cref="SevenZipLocation"/>）。</summary>
    internal SevenZipLocation? SevenZip { get; set; }

    /// <summary>解压引擎 UnRAR 的位置。</summary>
    internal ToolLocation? UnRar { get; set; }

    /// <summary>打包用的 Rar.exe（或退一档的 WinRAR.exe）。</summary>
    internal ToolLocation? Rar { get; set; }

    /// <summary>"换引擎再试一遍"用的 WinRAR.exe。</summary>
    internal ToolLocation? WinRarGui { get; set; }

    /// <summary>让四条链全部回到"未解析"，下次读取重新探测（设置变更、文件被替换后调用）。</summary>
    internal void Invalidate()
    {
        SevenZip = null;
        UnRar = null;
        Rar = null;
        WinRarGui = null;
    }
}
