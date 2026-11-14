using System.Collections.Generic;

namespace ArchiveFixer.Storage;

/// <summary>
/// 一次要落盘的密码列表全貌：条目（含顺序）+ 记住的密码本路径 + 墓碑 + **每条密码的成功次数**。
///
/// <para><b>墓碑</b> = 用户删掉 / 清空过的那些值。启动时把密码本合并回来时会**跳过**它们，
/// 否则"删掉一条、重启又从 txt 里复活"——那正是用户 2026-09-24 报的那类现象的另一半。</para>
///
/// <para><b>成功次数</b>（用户 2026-09-24 第 14 条："密码本里面的优先级差不多就是使用次数的频繁程度"）
/// = 这条密码在这台机器上成功解开过多少次。它跟着列表一起加密落盘，用来给同层级的候选排序
/// （次数多的排前面；次数相同保持原顺序）。⚠ 空密码（<c>""</c>）不记账：它不是"一条密码"，
/// 而是"不需要密码"那一步。</para>
/// </summary>
public sealed class PasswordListSnapshot
{
    public List<PasswordListEntry> Entries { get; set; } = new();

    public List<string> BookPaths { get; set; } = new();

    public List<string> Tombstones { get; set; } = new();

    /// <summary>密码值 → 成功次数（明文值，只允许存在于内存与**加密后的**文件里，§8 红线）。</summary>
    public Dictionary<string, int> SuccessCounts { get; set; } = new(StringComparer.Ordinal);
}

