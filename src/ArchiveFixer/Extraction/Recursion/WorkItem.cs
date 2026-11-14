using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>队列里的一项：一个待解的归档 + 它在第几层 + 它的工作区目录。</summary>
internal sealed class WorkItem
{
    internal int Depth { get; init; }

    internal string ArchivePath { get; init; } = string.Empty;


    internal WorkspaceLayer Layer { get; init; } = new WorkspaceLayer();

    /// <summary>是否是任务自己的那个包（第 0 层）。</summary>
    internal bool IsRoot { get; init; }

    /// <summary>
    /// **本层列出的清单成功、且一个加密条目都没有**（候选循环里列完清单就写这里，
    /// 再由本层报告 <see cref="RecursionLayerReport.ListingSaidNoEncryption"/> 带走）。
    /// </summary>
    internal bool ListingSaidNoEncryption { get; set; }

    /// <summary>本层真正试过几个候选密码（候选循环里写，报告带走）。</summary>
    internal int TriedCandidateCount { get; set; }

    /// <summary>
    /// 这一层停手是因为**连续两个候选的结果完全相同**（换密码不会改变结果；唯一出口见
    /// <see cref="RecursionLayerReport.CandidatesProducedIdenticalResults"/>）。
    /// ⛔ 只由「引擎说成功、但产物没通过校验」那一支写 —— 错密码那一支**不许**用它提前停
    /// （错密码留下的半截产物也会一模一样，而正确密码排在后面时产物会完全不同）。
    /// </summary>
    internal bool CandidatesProducedIdenticalResults { get; set; }

    /// <summary>
    /// 上一候选**校验判否**时的产物读数（文件数 / 字节数）——「引擎说成功但产物不对」那一支据此判"结果相同"。
    /// </summary>
    internal int LastVerifiedCandidateFileCount { get; set; } = -1;

    internal long LastVerifiedCandidateBytes { get; set; } = -1;

    /// <summary>
    /// **本层开工时该当场回收的过程物**（递归路逐层回收，见 <see cref="RecursiveExtractor.LayerReclaim"/>）。
    ///
    /// <para>由**父层**入队这一项时一并算好、写死在项上：那些文件全部来自父层的产物目录，
    /// 而入队那一刻父层已经跑完、盘上事实就在眼前 —— 等到本层开工时再去父层目录里"重新认一遍"
    /// 只会读到已经被搬走的现状（`CrossLayerVolumeGather` 就可能动过那些片）。
    /// ⛔ 只在真正入队的那几个归档上建项：没入队的（多分支不展开 / 到层数上限 /
    /// 超过每层数量上限）一个都不建 —— 它们要原样留在成品目录里当内容物。</para>
    ///
    /// <para>第 0 层**永远为空**：它的输入是用户给的源包，口径是"最外层源包只留到链尾"。</para>
    /// </summary>
    internal IReadOnlyList<RecursiveLayerReclaimRequest> Reclaims { get; init; } =
        Array.Empty<RecursiveLayerReclaimRequest>();

    /// <summary>
    /// 非 null 表示"只处理用户点名的这几个归档"。
    ///
    /// 存的是**文件名**而不是完整路径：决策里带的是上一次运行的工作区路径，本次续跑新建工作区后
    /// 前缀必然不同，按完整路径比对会把用户的选择全部判成"没了"。层内部的文件名是唯一稳定的定位键；
    /// 同一层里出现同名归档时两个都会被处理 —— 这是刻意的取舍：宁可多解一个同名分支，
    /// 也不能因为工作区换了就把用户点过的分支静默丢掉。
    ///
    /// 仅在 previousDecision 非 null 时落在第 0 层，后续层不继承（用户回答的是源包层面的问题）。
    /// </summary>
    internal IReadOnlyList<string>? DecisionNames { get; init; }
}
