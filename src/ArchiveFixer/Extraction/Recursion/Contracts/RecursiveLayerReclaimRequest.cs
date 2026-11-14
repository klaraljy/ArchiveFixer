using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;
/// <summary>
/// **递归路「逐层回收」的一次请求**（用户 2026-10-05：「这个我不是说了要同步吗」）。
///
/// <para><b>它是什么</b>：一条递归链里，**上一层交出来的那个内层包**——它已经被这一层
/// 真的解开、这一层也已经证明是完整的（<see cref="Verdict"/>），所以它对本层就是
/// "解出这一层内容所消耗掉的那份源"，用完了就该当场还回去（AGENTS.md §11.3 的口径）。</para>
///
/// <para>⛔ 递归核心**不删任何东西**：它只把"哪几个文件 + 这一层的完整性结论"交给调用方
/// （<see cref="RecursiveExtractor.LayerReclaim"/>），删除判据与执行体仍然只有既有那一套
/// （<c>ExtractionCoordinator.PurgeLayerSourcePackage</c> → <c>SourceCleanupService</c>）。</para>
/// </summary>
public sealed class RecursiveLayerReclaimRequest
{
    /// <summary>动这一层的时候用来说话（唯一出口 <c>RecursiveExtractor.DescribeProbeLabel</c>）。</summary>
    public string ChildLayerLabel { get; init; } = string.Empty;

    /// <summary>
    /// **要删的那几个文件**（绝对路径，已经去重）：上一层交出来的那个内层归档，
    /// 是分卷组时**整组一起**（组员判据转调既有那一把尺子，⛔ 这里不另写一套名字规则）。
    ///
    /// <para>⛔ 只装**真被解开过**的那些：从未入队（停在多分支 / 到层数上限 / 超过每层数量上限）
    /// 的内层包一个都不在里面 —— 它们原样留在成品目录里当内容物（用户 2026-10-04 那条口径）。</para>
    /// </summary>
    public IReadOnlyList<string> GroupFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// **这一层自己能不能被证明是完整的**（L4 三态，唯一出口
    /// <c>ResultCompletenessClassifier</c>；判据 = 拿**这一层的清单**逐条核对过）。
    /// 判不出 / 判否 ⇒ 调用方什么都不做（兜底落在"什么都不做"那一档）。
    /// </summary>
    public ResultCompletenessVerdict Verdict { get; init; } =
        new() { State = ResultCompleteness.Undeterminable };

    /// <summary>
    /// 这一层工作区所在的**任务工作区目录**（<c>&lt;工作区根&gt;\&lt;taskId&gt;</c>）。
    ///
    /// <para>用途只有一个：定稿侧那道「半套分卷」闸门扫"成品目录树"时要把它排除掉 ——
    /// 递归还没发布，同一条递归链里别的层产物目录里的同基名归档件**不是"成品目录里留下的另一片"**，
    /// 拿它当伙伴会把每一次合法的逐层回收全拦死（与 <c>RestVolumeCompletenessGate</c> 里
    /// "工作区里的东西不算"同一条道理；⛔ 只排除这一棵，别的照旧一律算）。</para>
    /// </summary>
    public string WorkspaceTaskDirectory { get; init; } = string.Empty;
}
