namespace ArchiveFixer.Extraction;

/// <summary>一条清单条目与盘上实际的比对结论（**逐条**，这就是"敢不敢发布"的最小单位）。</summary>
public enum PartialEntryState
{
    /// <summary>清单里有、盘上也在、大小对得上、引擎也没点它的名 ⇒ **可以发布**。</summary>
    Publishable = 0,

    /// <summary>清单里有、盘上没有 ⇒ 缺（引擎没解出来 / 解到一半就断了）。</summary>
    Missing = 1,

    /// <summary>盘上有，但大小与清单对不上（半截 / 被截断）⇒ 坏。</summary>
    SizeMismatch = 2,

    /// <summary>盘上有、大小也对，但**引擎点了它的名**（CRC/Data Error）⇒ 坏。</summary>
    EngineReported = 3
}
