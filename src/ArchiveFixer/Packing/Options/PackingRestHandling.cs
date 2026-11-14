using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing;

public enum PackingRestHandling
{
    /// <summary>不动：分卷文件夹留着（可以自己检查分卷）。</summary>
    Keep = 0,

    /// <summary>移入回收站（可还原）。</summary>
    RecycleBin = 1,

    /// <summary>彻底删除（默认）——用户原话："这个对用户来说一点用没有"。</summary>
    Delete = 2
}

/// <summary>
/// 分卷规则：**按卷数**（用户 2026-09-26 拍板的 (B) 方案）。
///
/// <para>内容 &lt; 1 GiB → 切 **2** 卷；≥ 1 GiB → 切 **3** 卷。
/// 每卷上限 = ⌈内容 ÷ 卷数⌉，**向上取整到整 MiB**（<c>7z -v</c> 的单位是 MiB）。</para>
///
/// <para>⚠ 为什么不是"平均分"：7-Zip 的 <c>-v&lt;大小&gt;</c> 是**每卷装满到上限、最后一卷拿余数**
/// （本机实测：1 MiB 分卷 → <c>.001</c> 正好 1048576 字节 + <c>.002</c> 是零头）。
/// 所以"目标卷数"是**上限**：内容能压缩时成品更小，实际可能切出更少的卷（更小是好事，不是出错）。
/// 日志里两个数都会写（目标 N 卷 / 实际 M 卷）。</para>
/// </summary>

