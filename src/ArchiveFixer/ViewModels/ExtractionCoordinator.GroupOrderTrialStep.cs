using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 整组试开（数字 ZIP / 跨盘 ZIP 尾巴 + 排序试开；第 10 块，从 Password 大杂烩搬出）。
/// 实现已按职责搬到 <see cref="GroupOrderTrialHandler"/>
/// （<c>ViewModels/Extraction/Steps/GroupOrderTrialHandler.cs</c>），这里只留同签名薄转发与结论类型。
///
/// <para>⛔ 本分片不再放任何判据：搬走的是**实现**，两个调用点
/// （<c>ExtractionOrderTrial</c> / <c>ExtractionCoordinator.VolumeStepStep</c>）一个字都没改。</para>
///
/// <para>宿主只有**一个**实例 ⇒ 处理器生命周期与原来完全一致（处理器不另存状态，只持有宿主）。
/// 嵌套类型 <c>GroupOrderTrialResult</c> 留在宿主这一侧：同类调用点按裸名引用它，
/// 搬走会改公开表面。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>搬出去的那一份整组试开实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private GroupOrderTrialHandler? _groupOrderTrialHandler;

    private GroupOrderTrialHandler GroupOrderTrials => _groupOrderTrialHandler ??= new GroupOrderTrialHandler(this);

    /// <summary>
    /// 整组试开（同签名薄转发；实现已搬到 <see cref="GroupOrderTrialHandler"/>）。
    /// ⛔ 判据、文案、日志顺序一处未改；转发本身不添任何分支。
    /// </summary>
    internal Task<GroupOrderTrialResult> ProbeGatherGroupAsync(
        IReadOnlyList<string> pieces,
        IReadOnlyList<string> candidatePool,
        string entryLayer,
        string? cardEntryPath,
        string referenceName,
        bool allowFallback,
        CancellationToken cancellationToken) =>
        GroupOrderTrials.ProbeGatherGroupAsync(
            pieces,
            candidatePool,
            entryLayer,
            cardEntryPath,
            referenceName,
            allowFallback,
            cancellationToken);

    /// <summary>
    /// **批末补判**：把名单里那几个任务逐条重判一次（用户口径 2 的后半句）。
    ///
    /// <para>三步，全部读**盘上事实**：① 再收一次卷（<see cref="TryGatherVolumesBeforeExtract"/>）；
    /// ② 现归一次组（<see cref="VolumeRepairHandler.RebuildVolumeGroup"/>，既有出口）—— 看这一组现在齐不齐；
    /// ③ 齐了 ⇒ 把这一单的起点改到第 1 卷、返回给调用方**照常跑这一组**；
    /// 仍然缺 ⇒ <paramref name="finalPass"/> 为 true 才如实报缺卷，否则继续留在名单里。</para>
    ///
    /// <para>⛔ 判据只有"盘上有没有那几卷"，⛔ 不比任何中文文案、⛔ 不拿任务账上那份过期结论当数。</para>
    /// </summary>
    /// <returns>现在该补跑的那几个任务（调用方负责启动 + 等它们收尾）。</returns>
    /// <summary>
    /// **(c) 硬链接试开证实**（用户 2026-10-17 四步里的第三步）：把这一组按**规范卷名链**交给引擎真开一次。
    ///
    /// <para>返回 null = 通过；非 null = 不过的原因。⛔ 判不出也算不过（没有引擎 / 认不出第 1 卷）——
    /// 整组不动，⛔ 绝不"没试过就当过了"（2026-10-17 日志里那句「可证完整且试开通过」的假话
    /// 就是这么来的：那一趟其实一次引擎都没调）。</para>
    ///
    /// <para>只读：<see cref="VolumeProbeVerifier"/> 只在试开目录里建硬链接、收工把整棵试开目录删掉，
    /// 用户那一份一个字节都不动。</para>
    /// </summary>
    /// <summary>试开定序的结论（<see cref="Confirmed"/> = 赢的那一版；<see cref="Failure"/> 非空 = 不做）。</summary>
    internal sealed class GroupOrderTrialResult
    {
        /// <summary>这一档**适用**（通用分片、片数 ≤5、末片可定）—— 失败时调用方不再按名字硬拼。</summary>
        public bool Applicable { get; init; }

        /// <summary>试开确认了顺序。</summary>
        public bool Confirmed { get; init; }

        /// <summary>赢的那一版（第 1 片 … 末片）。</summary>
        public IReadOnlyList<string> OrderedPieces { get; init; } = Array.Empty<string>();

        /// <summary>规范名的基名（由末片卡上取）。</summary>
        public string Stem { get; init; } = string.Empty;

        /// <summary>内容判定的命名族（`ZipNumbered` / `ZipSpanned`）。</summary>
        public VolumeNamingFamily Family { get; init; } = VolumeNamingFamily.ZipNumbered;

        public string? Failure { get; init; }

        public string Reason { get; init; } = string.Empty;

        public static GroupOrderTrialResult Fail(string reason, bool applicable = false) =>
            new() { Failure = reason, Applicable = applicable };
    }

}
