using System;
using System.Collections.Generic;

namespace ArchiveFixer.Security;

/// <summary>一次批量预检的结果。</summary>
public sealed class PathSafetyReport
{
    /// <summary>没有任何危险条目。<b>只代表"条目名"这一层没问题</b>，不代表解压落点安全。</summary>
    public bool IsSafe { get; init; }

    /// <summary>是否真的拿到了条目清单；false = 引擎没给清单，不能把"没检查"当安全。</summary>
    public bool Known { get; init; }

    public IReadOnlyList<UnsafeArchiveEntry> UnsafeEntries { get; init; } = Array.Empty<UnsafeArchiveEntry>();

    /// <summary>
    /// **命名类危险**（Windows 保留设备名 / 结尾点或空格 / 备用数据流）：不影响解压、**不拒绝整包**，
    /// 交给引擎落盘时自动改名（实测 7z：`CON` ⇒ `_CON`、`结尾点.txt.` ⇒ `结尾点.txt_`）。
    ///
    /// <para>用户 2026-10-24 口径：这一类是「清洗后再落盘」，⛔ 不许升级成整包拒绝
    /// （一条怪名字不该让整包其他文件都拿不到）。</para>
    /// </summary>
    public IReadOnlyList<UnsafeArchiveEntry> NamingHazards { get; init; } = Array.Empty<UnsafeArchiveEntry>();

    /// <summary>命名类警告的一句话结论（没有就是空串）。</summary>
    public string NamingSummary { get; init; } = string.Empty;

    /// <summary>实际检查过的条目数（含目录条目）。</summary>
    public int CheckedEntryCount { get; init; }

    /// <summary>一句话结论，直接进提示框 / 日志。</summary>
    public string Summary { get; init; } = string.Empty;
}
