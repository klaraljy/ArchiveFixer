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
/// 一层的执行报告。"失败定位到层"（AGENTS.md §6 第 14 条）靠的就是它。
/// 注意：本类**不得**出现明文密码，只有 <see cref="UsedPasswordMasked"/> 这个脱敏占位符。
/// </summary>
public sealed class RecursionLayerReport
{
    public int Depth { get; init; }

    /// <summary>本层解的那个归档。</summary>
    public string ArchivePath { get; init; } = string.Empty;

    /// <summary>
    /// **这一层的清单成功列出来了、而且里面一个加密条目都没有**（用户 2026-09-27 口径
    /// 「包没加密 / 结果不变 → 不许再烧候选，也不许把结论写成「密码错误」」）。
    ///
    /// <para>事实来源 = 候选循环里那次列目录（`listedThisCandidate is { Success: true, IsEncrypted: false }`），
    /// 经 <c>WorkItem</c> 上的同名事实位带到本报告；协调器据此落 `PasswordStatus = 不需要密码`
    /// 与「问题不在密码上」那句文案（此前递归路一个字都不写）。</para>
    /// </summary>
    public bool ListingSaidNoEncryption { get; init; }

    /// <summary>
    /// **这一层真正试过几个候选密码**（用户 2026-09-24 要求"原因里必须写明试了几个候选"）。
    ///
    /// <para>事实来源 = 候选循环的 `triedCandidates`，由 <c>WorkItem</c> 带到本报告；协调器在收尾那一步
    /// 用它把「（已试 N 个候选，产物始终不完整）」补进失败原因（此前只有单层路写这一句）。</para>
    /// </summary>
    public int TriedCandidateCount { get; init; }

    /// <summary>
    /// **这一层停手是因为「连续两个候选解出来的产物完全相同」**（换密码不会改变结果）。
    ///
    /// <para>事实来源 = 候选循环里那两处比较（错密码那一支按"产物文件数 + 字节数"比，
    /// 「引擎说成功但产物没通过校验」那一支按校验结果比），经 <c>WorkItem</c> 带到本报告；
    /// 协调器据此把「连续两个候选解出来的产物完全相同」补进失败原因 —— 与单层路
    /// （<c>ExtractionCoordinator</c> 的 <c>passwordIrrelevantReason</c>）同一个口径。</para>
    /// </summary>
    public bool CandidatesProducedIdenticalResults { get; init; }


    /// <summary>本层的产物目录（工作区里的 layer-XXX\output）。</summary>
    public string OutputPath { get; init; } = string.Empty;

    public bool Success { get; init; }

    /// <summary>引擎给的状态文案（如"解压成功"）。</summary>
    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 这一层是不是"产物越出本层产物目录"（不变量 4 的第二道防线）。
    /// 结构化事实：调用方据此写自己的措辞，⛔ 不用去比 Message 里的中文。
    /// </summary>
    public bool LandingViolation { get; init; }

    /// <summary>本层产物里探测到的内层归档（完整路径）。</summary>
    public IReadOnlyList<string> InnerArchives { get; init; } = Array.Empty<string>();

    public int OutputFileCount { get; init; }

    public long OutputSize { get; init; }

    /// <summary>
    /// **本层解压前从引擎拿到的清单**（条目数 + 解压后总字节），L3 的预期来源。
    ///
    /// <para>为什么必须留在报告里（用户 2026-09-30 真机）：检验等级 L3 要拿"产出最终内容物的那一层"
    /// 的清单去核对产物，而解压前那次列目录（<see cref="CheckEntriesBeforeExtractAsync"/>）
    /// 以前只用来做路径预检，`ArchiveListResult` **用完即弃** —— 于是链尾只剩第 0 层的清单，
    /// 而第 0 层清单对不上叶子层的产物，L4 只能判「判不出」，源包与其余物一个字节都不动。
    /// 现在每一层各留各的（⛔ 第 0 层的清单只对第 0 层用）。</para>
    /// </summary>
    public LayerManifest Manifest { get; init; } = LayerManifest.Unavailable("这一层没有列过清单");

    /// <summary>
    /// **本层清单的逐条形状**（相对路径 + 解压后字节），部分完成发布（<see cref="PartialPublishPlanner"/>）
    /// 要靠它逐条对账。
    ///
    /// <para>为什么要单独带一份：<see cref="Manifest"/> 只有两个总数（条目数 / 总字节），
    /// 而"哪些文件敢发布"必须逐条回答（缺的 / 大小不符的 / 被引擎点名的）。取的是
    /// **这一层列目录成功的那一次**（<see cref="CheckEntriesBeforeExtractAsync"/> 的原始结论）——
    /// 密码不对时候选连目录都列不出来，那一档自然就是空表 ⇒ 判"没有清单" ⇒ 什么都不发布。</para>
    /// </summary>
    public IReadOnlyList<(string Path, long Size)> ManifestEntries { get; init; } =
        Array.Empty<(string, long)>();

    /// <summary>这一层失败时**引擎点名**的坏条目（点不出名时是空集合，见 <c>ArchiveOperationResult.FailedEntryNames</c>）。</summary>
    public IReadOnlyList<string> FailedEntryNames { get; init; } = Array.Empty<string>();

    /// <summary>这一层失败时引擎**自报**的出错条目数（闸门：比点得出名的多 ⇒ 一个字节都不发布）。</summary>
    public int ReportedSubItemErrors { get; init; }

    /// <summary>
    /// 这一层失败时引擎给的**结构化错误类型**（<c>EngineErrorTypes</c> 那一套词表里的名字；
    /// 没有结果 / 手写报告时是空串）。
    ///
    /// <para><b>为什么要带它</b>（2026-10-16，「损坏压缩包 ⇒ 自动留下不完整内容物」那一档）：
    /// 那条判据只认**结构化事实**，⛔ 不许比中文文案（AGENTS：要写盘的判据只准读枚举/错误码）。
    /// 单层路那边手上直接有 <c>ArchiveOperationResult.DetectedErrorType</c>，递归路只有到这一层
    /// 才拿得到 ⇒ 带出来，两条路的判据才是同一个。</para>
    /// </summary>
    public string DetectedErrorType { get; init; } = string.Empty;

    /// <summary>只用 "空密码" 或 "******"；本层没试过密码时为空串。</summary>
    public string UsedPasswordMasked { get; init; } = string.Empty;
}
