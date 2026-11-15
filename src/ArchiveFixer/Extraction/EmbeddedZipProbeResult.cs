using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// "能不能直读这个内嵌 ZIP"的**只读**结论（一个字节都不写）。
///
/// <para>它是"回落信号"的载体：<see cref="Supported"/> 为 false 且 <see cref="PathRejected"/> 为 false
/// 时，调用方应当原地回落到"抠取 + 7z"；<see cref="PathRejected"/> 为 true 时是**硬失败**
/// （条目名越界，抠出来交给 7z 也会被同一套路径预检拒掉，白拷一份等大的临时文件）。</para>
/// </summary>
public sealed class EmbeddedZipProbeResult
{
    /// <summary>直读可用。</summary>
    public bool Supported { get; init; }

    /// <summary>不支持的原因（回落信号；<see cref="Supported"/> 为 true 时为空）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>条目名预检未通过 = **硬失败**，不是回落信号（见类注释）。</summary>
    public bool PathRejected { get; init; }

    /// <summary>
    /// 加密条目：候选密码**一个都没通过校验** = **硬失败**，不是回落信号。
    ///
    /// <para>为什么不能回落：回落那条路是"抠出副本 + 7z"，而 7-Zip 对 AES 包的密码只按
    /// ANSI 字节派生（无视归档里的 UTF-8 声明），UTF-8 打包的中文密码在它那里**永远**是"密码错误"。
    /// 回落只会白拷一份等大的副本，再报一次同样的错 —— 那正是用户 2026-09-25 报的现场。</para>
    /// </summary>
    public bool PasswordRejected { get; init; }

    /// <summary>
    /// 这份归档**只差一个密码就能直读**（里面是 AES 条目，而这次没给候选密码）。
    ///
    /// <para>识别阶段（拿不到密码）用得上它：空间账面上要按"不需要那份等大的临时副本"算，
    /// 否则一个 40 GB 的加密双面文件会被一笔根本不会发生的抠取副本卡在空间门上。
    /// 密码不对时是 <see cref="PasswordRejected"/>（硬失败），同样不会抠副本 —— 所以这个乐观是准的。</para>
    /// </summary>
    public bool RequiresPassword { get; init; }

    /// <summary>硬失败时给用户看的那句话（与 <see cref="PathRejected"/> 同一口径）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>条目清单（含目录条目）。</summary>
    public IReadOnlyList<EmbeddedZipEntry> Entries { get; init; } = Array.Empty<EmbeddedZipEntry>();

    /// <summary>
    /// 同一个清单，翻成引擎口径的 <see cref="ArchiveListResult"/> ——
    /// 于是既有那套"路径预检 / 资源预算 / 空间核算 / 危险条目统计"**原样复用**，不需要第二套实现。
    /// </summary>
    public ArchiveListResult? List { get; init; }

    /// <summary>归档区间字节数（<c>End − Offset</c>）：也用在展开比的分母上。</summary>
    public long ArchiveLength { get; init; }

    /// <summary>有效起点（= 调用方给的 Offset）。</summary>
    public long Offset { get; init; }

    /// <summary>有效终点（<c>ArchiveEnd</c> 缺失时回落到文件末尾，与抠取侧同一口径）。</summary>
    public long End { get; init; }

    /// <summary>清单里所有文件条目的原始大小合计（进度分母）。</summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// 解锁成功的那个候选在调用方给的候选表里的序号（1 起；0 = 这份归档没加密、不需要密码）。
    ///
    /// <para>⛔ 这里刻意**不**放密码明文：调用方拿序号去自己的候选表里取备注（已经是脱敏的），
    /// 日志与结论里都只说"第 N 个候选 + 哪种字节编码"（AGENTS.md §8）。</para>
    /// </summary>
    public int ResolvedPasswordIndex { get; init; }

    /// <summary>解锁成功时用的是哪种密码字节编码（<see cref="ResolvedPasswordIndex"/> 为 0 时无意义）。</summary>
    public ZipAesPasswordEncoding ResolvedPasswordEncoding { get; init; }

    /// <summary>
    /// 命名类警告（保留设备名 / 结尾点或空格 / 备用数据流）：不拒绝整包，落盘时由清洗步骤改名；
    /// 没有就是空串。调用方负责把它写成一行 WARN（用户 2026-10-24：日志要详细）。
    /// </summary>
    public string NamingSummary { get; init; } = string.Empty;
}
