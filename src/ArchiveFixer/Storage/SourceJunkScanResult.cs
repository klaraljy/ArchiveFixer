using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Storage;

/// <summary>一次"无用物"扫描的结论。</summary>
public sealed class SourceJunkScanResult
{
    /// <summary>要报出来的条目（最多 <see cref="SourceJunkScanner.MaxReportedItems"/> 条）。</summary>
    public IReadOnlyList<SourceJunkItem> Items { get; init; } = Array.Empty<SourceJunkItem>();

    /// <summary>
    /// **这次认出来的全部条目**（⛔ 不受 <see cref="SourceJunkScanner.MaxReportedItems"/> 截断）。
    ///
    /// <para>为什么要留一份不截断的：那份上限是**给提示框看的**（提示要短），可"把无用物从任务列表里
    /// 移出去"要按**全部**命中来做 —— 真机 CCCC 2026-10-07 实测：扫到 16 条、`Items` 只有 10 条
    /// （前 10 个按名字序），而用户列表里那几个 `.txt` 恰好在后 6 条里 ⇒ 日志写"移掉 0 个"，
    /// 他看到的列表一个都没动。判据仍只有 `SourceJunkScanner` 这一处（⛔ 不在这里另写一套）。</para>
    /// </summary>
    public IReadOnlyList<SourceJunkItem> AllItems { get; init; } = Array.Empty<SourceJunkItem>();

    /// <summary>
    /// **要拿去移出任务列表的那份名单**（唯一出口）：<see cref="AllItems"/> 非空就用它，
    /// 否则退回 <see cref="Items"/>（手工构造的结论 / 老调用方只填了 `Items`）。
    ///
    /// <para>⛔ 唯一消费点（`ScanCoordinator.RemoveJunkTasksFromListAsync`，只对文件夹来源的任务）
    /// **必须读这一个属性**：真机 2026-10-07 10:15 那一批按 `Items`（上限 10 条）移，
    /// 17 个无用物先只走了 10 个（用户当场问「为什么一开始只清理 10 个」）。</para>
    /// </summary>
    public IReadOnlyList<SourceJunkItem> AllHits => AllItems.Count > 0 ? AllItems : Items;

    /// <summary>超过上限、只报了个数的那部分（提示里写成"还有 N 个"）。</summary>
    public int ExtraCount { get; init; }

    /// <summary>这次一共认出多少个（= <see cref="Items"/> + <see cref="ExtraCount"/>）。</summary>
    public int TotalCount => Items.Count + ExtraCount;

    /// <summary>真的要报给用户吗（false = 这一段是空的，提示里整段不出现）。</summary>
    public bool HasAnything => Items.Count > 0;

    /// <summary>实际扫过的源目录数。</summary>
    public int DirectoryCount { get; init; }

    /// <summary>因为"目录数上限"没扫的目录数（0 = 全扫了）。</summary>
    public int SkippedDirectoryCount { get; init; }

    /// <summary>
    /// 撞到某个硬上限（每个目录 2000 个文件 / 魔数体检预算）而只核对了前一部分。
    /// **必须如实说明**：不写这一条，用户会以为"就这么几个"。
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>供日志用的"前几条名字"（最多 <see cref="SourceJunkScanner.MaxNamesInLog"/> 个）。</summary>
    public IReadOnlyList<string> SampleNames =>
        Items.Take(SourceJunkScanner.MaxNamesInLog).Select(item => item.FileName).ToList();
}
