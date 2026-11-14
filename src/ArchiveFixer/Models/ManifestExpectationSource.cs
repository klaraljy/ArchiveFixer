namespace ArchiveFixer.Models;

/// <summary>L3 的预期清单**从哪来**（机器可判；日志/证据只读它，⛔ 不比中文）。</summary>
public enum ManifestExpectationSource
{
    /// <summary>取不到清单 ⇒ L4 只能判「判不出」（兜底那一档）。</summary>
    None = 0,

    /// <summary>让调用方**现问引擎**列一份（真包 / 内嵌归档抠出来的副本都走这条）。</summary>
    QueryArchive = 1,

    /// <summary>手上已经有第 0 层的清单（内嵌 ZIP 直读那条路带上来的）。</summary>
    OuterList = 2,

    /// <summary>**叶子层**（产出最终内容物的那一层）的清单 —— 展开 &gt; 1 层时用这一档。</summary>
    LeafLayer = 3
}
