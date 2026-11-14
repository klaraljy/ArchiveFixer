using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 「容器里装的是分卷的第 1 卷、后续卷在容器外面」时的**拼装计划**（用户 2026-09-25 第 42 条，
/// 他选的方案 A：做，但**默认关**，②页那个开关打开才走这条路）。
///
/// <para>为什么需要它：引擎（7z / UnRAR）是按**文件名**在**同一个目录**里找同组的其他卷的。
/// 容器里那一段首卷被抠出来之后躺在工作区、名字也不带卷号（<c>封面.zip</c>）——
/// 引擎两个条件都不满足，于是报"分卷缺失"。要让两边凑上，就得在**我们自己的工作区**里
/// 摆出一套"名字成套、同在一个目录"的卷。</para>
///
/// <para>⛔ 拼装**不动用户的源文件**：首卷来自容器（派生数据），后续卷只是被"多起一个名字"
/// （同盘硬链接，零字节复制）或复制一份（跨盘时没办法），源文件的内容/名字/位置一个字节都不变。</para>
/// </summary>
public sealed class SplitVolumeAssemblyPlan
{
    /// <summary>能不能拼（false 时看 <see cref="Reason"/>，一个字都不许动）。</summary>
    public bool CanAssemble { get; init; }

    /// <summary>不能拼的原因 / 能拼时的一句说明（给人看的中文）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>拼到哪个目录（调用方给：任务工作区下的一个子目录）。</summary>
    public string AssemblyDirectory { get; init; } = string.Empty;

    /// <summary>标准首卷名（例如 <c>set.7z.001</c>）—— 交给引擎的就是这个文件。</summary>
    public string FirstVolumeName { get; init; } = string.Empty;

    /// <summary>要接进来的每一条（第 1 条是首卷，其余按卷序）。</summary>
    public IReadOnlyList<SplitVolumeAssemblyEntry> Entries { get; init; } = Array.Empty<SplitVolumeAssemblyEntry>();

    /// <summary>这一组后续卷的字节数合计（跨盘要复制的就是它们）。</summary>
    public long ContinuationBytes => Entries.Where(entry => !entry.IsFirstVolume).Sum(entry => entry.Length);
}

