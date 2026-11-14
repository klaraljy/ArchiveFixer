namespace ArchiveFixer.Detection;

/// <summary>
/// **ZIP 族内部的两种分卷**（命名、入口、内容证据都不同，⛔ 不能混）。
/// </summary>
public enum ZipVariant
{
    /// <summary>判不出。</summary>
    Unknown = 0,

    /// <summary>跨盘 ZIP：<c>X.z01…X.zip</c>，入口是末片 <c>X.zip</c>。</summary>
    Spanned = 1,

    /// <summary>通用分片：<c>X.zip.001…X.zip.00N</c>，入口是第 1 片。</summary>
    Numbered = 2
}
