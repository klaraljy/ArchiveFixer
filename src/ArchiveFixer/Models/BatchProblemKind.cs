namespace ArchiveFixer.Models;

/// <summary>
/// 批末诊断清单里的**一组**（"哪一类错、点了哪几个包的名字"）。
///
/// <para>顺序就是枚举声明顺序，也是显示顺序：先失败各组，再"没做成但不是失败"的那几档。
/// 这个顺序是**表的顺序**，⛔ 不在渲染处再排一次（同一件事只有一个出口）。</para>
/// </summary>
public enum BatchProblemKind
{
    /// <summary>磁盘空间不足（空间门在解压**之前**就拦下了，包本身没问题）。</summary>
    DiskSpace = 0,

    /// <summary>密码类：密码错误 / 达到密码尝试上限 / 文件名已加密（三种终态合成一组，口径见 StatusText）。</summary>
    Password = 1,

    /// <summary>分卷缺失（不变量 7：必须报"缺哪几个"）。</summary>
    MissingVolume = 2,

    /// <summary>文件损坏。</summary>
    Corrupted = 3,

    /// <summary>权限不足。</summary>
    AccessDenied = 4,

    /// <summary>输出路径冲突。</summary>
    OutputConflict = 5,

    /// <summary>源文件已变化（不变量 11）。</summary>
    SourceChanged = 6,

    /// <summary>其他失败（解压失败 / 未知错误 / 没有可用的解压引擎 / 校验没通过 …）：兜底那一组。</summary>
    Other = 7,

    /// <summary>部分完成（做了一半，不得当成成功）。</summary>
    PartiallyCompleted = 8,

    /// <summary>已跳过（不是压缩包 / 同名冲突按用户选择跳过）。</summary>
    Skipped = 9,

    /// <summary>已取消。</summary>
    Cancelled = 10,

    /// <summary>没轮到（这一批结束时它还没有终态）。</summary>
    NotReached = 11
}
