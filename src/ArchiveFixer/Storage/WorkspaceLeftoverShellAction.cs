namespace ArchiveFixer.Storage;

/// <summary>上一次被中断留下的空壳，这一次跑的时候怎么处置了。</summary>
public enum WorkspaceLeftoverShellAction
{
    /// <summary>
    /// 这一层目录里**没有我们的脚印**（那个工作区目录根本不在）、或者它压根不存在
    /// —— 什么都没做，也没什么可说的（⛔ 空目录本身不构成"是我们建的"的理由）。
    /// </summary>
    NoFootprint = 0,

    /// <summary>收掉了：空的工作区目录先删，再删那个已经空掉的目标目录壳。</summary>
    Reclaimed = 1,

    /// <summary>**发现上次没跑完的残留，但一个字节都没动**（里面还有别的东西 / 工作区不是空的）。</summary>
    LeftIntact = 2
}
