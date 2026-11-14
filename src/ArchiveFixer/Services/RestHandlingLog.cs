using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
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

namespace ArchiveFixer.Services;

/// <summary>
/// 其余物这一档**对用户怎么说**：结论、证据、原因的唯一措辞出口（生命周期：每个决策点的输出侧）。
///
/// <para>为什么单独一个类：这一档的措辞本身就是口径（"其余物为什么还在"必须说清是哪一条判据、
/// L4 证据必须带哪一层与原因、"其余物"范围必须跟着源包档位说），它不该散在判定/搬运的分支里跟着一起改。
/// ⛔ 本类只写日志与任务上的"为什么留着"字段，不判任何一件事，也不动一个字节。</para>
/// </summary>
internal sealed class RestHandlingLog
{
    private readonly IRestHandlingStepHost _host;

    internal RestHandlingLog(IRestHandlingStepHost host)
    {
        _host = host;
    }

    /// <summary>
    /// **其余物为什么原封不动地留着** —— 那一句话的唯一出口（用户 2026-10-04 真机）。
    ///
    /// <para>现场：日志 271/272 里写着「<c>Sociology.7z：链尾的其余物不处理（链上的「老王.apk」没有成功…）</c>」、
    /// 「<c>第6集.7z：链尾没有按「删除操作」处理其余物 / 源包 —— 任务没有成功…</c>」，
    /// 可①页、批末诊断、失败清单里**一个字都没有** —— 用户看到的是"解压成功、其余物还在"，
    /// 只能自己去猜。这一句把那条日志里的事实搬到他能看见的三个地方（同一份文字，⛔ 不各写一遍）。</para>
    /// </summary>
    internal static string DescribeRestKeptNote(ArchiveTask task, string reason)
    {
        if (task == null)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(task.RestDirectoryPath)
            ? string.Format(CultureInfo.CurrentCulture, StatusText.RestKeptNoteNoPathFormat, reason)
            : string.Format(CultureInfo.CurrentCulture, StatusText.RestKeptNoteFormat, task.RestDirectoryPath, reason);
    }

    /// <summary>
    /// 把"其余物没处理 + 为什么"记到任务上（①页「错误信息」列读 <c>ErrorMessage</c>，
    /// 批末诊断与失败清单读 <c>RestKeptReason</c> —— 三处同一份文字）。
    ///
    /// <para>⚠ 刻意**不改**任务的终态 / 状态：内容物是好的（这一单确实成功），
    /// 只是"其余物这一档没做" —— 改终态会连带改掉批末计数与四条删除裁决，那不是措辞的事。
    /// 幂等：同一句只写一次（链尾可能两条路都判到）。</para>
    /// </summary>
    internal static void RecordRestKept(ArchiveTask task, string reason)
    {
        if (task == null || string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        string note = DescribeRestKeptNote(task, reason);

        task.RestKeptReason = note;

        if (!task.ErrorMessage.Contains(note, StringComparison.Ordinal))
        {
            task.ErrorMessage = string.IsNullOrWhiteSpace(task.ErrorMessage)
                ? note
                : task.ErrorMessage + "；" + note;
        }
    }

    /// <summary>
    /// 链尾"按档处理其余物 / 源包"被某一条判据拦下时的**唯一**一行日志
    /// （用户 2026-09-30：源包被留下时必须说清是哪一条判据 —— ⛔ 不许静默 return）。
    ///
    /// <para>措辞与源包搬运动那几条 WARN 分开：这里说的是"链尾这一档没做"，
    /// 而 <paramref name="why"/> 里已经带了具体判据（含"哪一层 + 什么原因"）。</para>
    /// </summary>
    internal void AppendRemovalBlocked(ArchiveTask task, string why)
    {
        // 这条 WARN 就是"其余物为什么还在"的答案 ⇒ 同一句话也要落到①页与报告里（用户 2026-10-04 真机）。
        RecordRestKept(task, why);

        _host.AppendLog(
            "WARN",
            $"{task.LogName}：{StatusText.ChainRestBlockedPrefix} —— {why}；"
            + "一个字节都没动（失败 / 取消 / 判不出完整性一律不动）。");
    }

    /// <summary>
    /// L4（完整性分类）的**那一行证据** —— 全仓唯一一处写它。
    ///
    /// <para>为什么要写：这一条结论就是"敢不敢动用户源包"的唯一依据。
    /// 2026-09-30 的真机日志里，源包被留下了、可**为什么**只有一个笼统的"没有可用的归档清单可核对"，
    /// 用户根本不知道是哪一层、下一步该做什么。</para>
    ///
    /// <list type="bullet">
    /// <item><description><b>可证完整</b>：只在"这一次真的要按设置动源包"时才写（留在原地档没什么要交代的）。</description></item>
    /// <item><description><b>判不出 / 可证不完整</b>：**一律写**，而且写的是"为什么没删源包：判据 + 哪一层 + 原因"。</description></item>
    /// </list>
    /// </summary>
    internal void AppendCompletenessEvidence(
        ArchiveTask task,
        OutputVerificationResult verification,
        SourceHandlingMode sourceHandling)
    {
        ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(
            verification,
            task.ManifestExpectation);

        if (verdict.AllowsSourceRemoval)
        {
            if (sourceHandling == SourceHandlingMode.KeepInPlace)
            {
                return;
            }

            _host.AppendLog(
                "INFO",
                $"{task.LogName}：{verdict.Message}（L3 核对依据：{DescribeManifestBasis(task.ManifestExpectation)}；"
                + $"机器结论：{verdict.Evidence}）");

            return;
        }

        _host.AppendLog(
            "WARN",
            $"{task.LogName}：{verdict.Blocker}（机器结论：{verdict.Evidence}）");
    }

    /// <summary>L3 的预期清单是从哪一层来的（给人看的一句话；取不到时说清是哪一层、为什么）。</summary>
    internal static string DescribeManifestBasis(ManifestExpectation expectation)
    {
        return expectation.Source switch
        {
            ManifestExpectationSource.LeafLayer =>
                $"{expectation.LayerLabel}（产出最终内容物的那一层）的清单 {expectation.Expected!.FileCount} 个文件 / "
                + $"{expectation.Expected.TotalUncompressedSize} 字节",
            ManifestExpectationSource.OuterList =>
                $"归档自己的清单 {expectation.Expected!.FileCount} 个文件 / {expectation.Expected.TotalUncompressedSize} 字节",
            ManifestExpectationSource.QueryArchive =>
                $"引擎对这份归档现列出来的清单",
            _ => expectation.UnavailableReason
        };
    }

    /// <summary>
    /// 「其余物」这一档会动到什么 —— **其余物 = 过程物 + 原包**（用户 2026-09-27 定死的口径）。
    ///
    /// <para>为什么必须由档位决定这句话：源包那一档选「留在原地」时，其余物里**根本没有源包**。
    /// 2026-09-27 真机上就是这里出的洋相 —— 同一份日志一边写"源包处理：留在原地（一个字节都不搬）"，
    /// 一边写"其余物：任务成功后会彻底删除（源包 + 过程物）"，用户直接问"我都设置了原包不动，
    /// 你怎么还说原包在其余物里面"。</para>
    /// </summary>
    internal static string DescribeRestScope(SourceHandlingMode sourceHandling)
        => sourceHandling == SourceHandlingMode.KeepInPlace
            ? "解压过程中产生的过程物（源包留在原地：一个字节都不搬、不删）"
            : "源包 + 过程物";

    /// <summary>
    /// 逐层回收那一句里"**为什么没回收**"（只有 <c>SourcePackageMove != Done</c> 时才会问到这里）。
    ///
    /// <para>三档都只读**任务上已有的**事实（状态常量 / 机器终态），⛔ 不重推判据、不重算原因：</para>
    /// <list type="number">
    /// <item><description><b>部分完成</b>（删了一部分没删完，占用 / 只读 / 权限）——
    /// 这时候那一层已经写过一行 ERROR，指过去。</description></item>
    /// <item><description><b>终态成功</b> ⇒ 这一层跑成了，是回收判据没放行（完整性判不出 / 半套分卷闸门 /
    /// 源包清单是空的）—— 那一层也各写了一行 WARN，指过去。</description></item>
    /// <item><description><b>其余</b> ⇒ 这一层自己没跑成（定稿搬运失败 / 校验没过 / 已取消），
    /// 回收那一步**根本没走到** ⇒ 原因在①页「错误信息」列（用户 2026-10-04 真机正是这一档）。</description></item>
    /// </list>
    /// </summary>
    internal static string DescribeLayerReclaimBlocker(ArchiveTask task)
    {
        if (string.Equals(task.Status, StatusText.PartiallyCompleted, StringComparison.Ordinal))
        {
            return StatusText.LayerReclaimPartialBlockedReason;
        }

        if (task.Outcome == TaskOutcome.Succeeded)
        {
            return StatusText.LayerReclaimGateBlockedReason;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            StatusText.LayerReclaimLayerFailedReasonFormat,
            task.Status);
    }

    /// <summary>
    /// 其余物处理成功后那**一行**（措辞唯一出口，单独可测）。
    ///
    /// <para><b>两档措辞</b>：</para>
    /// <list type="bullet">
    /// <item><description><b>空壳档</b>（<see cref="RestPurgeOutcome.RemovedAsEmptyShell"/>）：
    /// 其余物里递归地一个文件都没有 ⇒ 就地删掉空目录、**没有进回收站**
    /// （<see cref="StatusText.RestPurgedEmptyShellFormat"/>）。</description></item>
    /// <item><description><b>普通档</b>：<see cref="StatusText.RestPurgedCompactFormat"/>
    /// —— 档名 + 短路径 + 条目数 + 释放大小（用户 2026-09-25 第 44 条："一次导出 713KB，这个多吓人"）。</description></item>
    /// </list>
    ///
    /// <para>⚠ <b>为什么必须按事实位选、不能按数字猜</b>（用户 2026-10-03 真机）：回收站档已下线，
    /// 但空壳仍然**没有走删除执行体**；判据只读 <see cref="RestPurgeOutcome.RemovedAsEmptyShell"/>
    /// （AGENTS.md §9.5：要删 / 要写盘的动作判据只准读事实；"0 项 0 B"证明不了"没进执行体"）。</para>
    /// </summary>
    internal static string DescribeRestPurgedLine(RestPurgeOutcome outcome)
    {
        if (outcome.RemovedAsEmptyShell)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RestPurgedEmptyShellFormat,
                LogLineText.DescribeShortTail(outcome.Directory, 2));
        }

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.RestPurgedCompactFormat,
            StatusText.RestActionDelete,
            LogLineText.DescribeShortTail(outcome.Directory, 2),
            outcome.EntryCount,
            WorkspaceCleanupService.FormatSize(outcome.FreedBytes));
    }
}
