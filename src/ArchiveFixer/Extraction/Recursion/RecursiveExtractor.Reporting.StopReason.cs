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

public sealed partial class RecursiveExtractor
{        /// <summary>停因判定观察口：⛔ 不改判据；观察者自己抛异常也不影响解压。</summary>
    internal void ReportStopReason(
        RecursionStopReason reason,
        string message,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        if (StopReasonObserved == null || reason == RecursionStopReason.None)
        {
            return;
        }

        try
        {
            StopReasonObserved(reason, sourceFile, sourceLine, caller, message);
        }
        catch
        {
            // 诊断观察失败不得影响解压。
        }
    }

    /// <summary>
    /// 这个路径在不在**本批工作区根**里面（= 中间产物 / 入仓目录，⛔ 不可能是用户看得见的落点）。
    ///
    /// <para>依据是项目红线：落点永远不许落在工作区里（发布侧另有一道闸门
    /// <c>ExtractionWorkspace.Publish</c> 会当场停手）⇒ "在工作区根里面"就等于"这是内部目录"。</para>
    ///
    /// <para>用途只有一个（2026-10-07 真机 EEEE）：结论句据此决定把发布目标说成"产物"还是
    /// "中间产物入仓"，⛔ 不参与任何写盘判据。</para>
    /// </summary>
    private static bool IsInsideWorkspaceRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string? root = ConfiguredWorkspaceRoot;

        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string full = SafePathHelper.GetFullPathSafe(path);
        string rootFull = SafePathHelper.GetFullPathSafe(root);

        if (full.Length == 0 || rootFull.Length == 0)
        {
            return false;
        }

        rootFull = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSummary(
        RecursionStopReason stopReason,
        List<RecursionLayerReport> layers,
        ExtractionWorkspace workspace,
        string finalOutputPath,
        bool completed,
        string publishMessage,
        string extraMessage,
        IReadOnlyList<string> unexpandedNames,
        string passwordFallbackNote = "")
    {
        int done = layers.Count(layer => layer.Success);

        string reason = DescribeStopReason(stopReason, unexpandedNames.Count);

        string head = stopReason switch
        {
            RecursionStopReason.Completed => $"已完成 {done} 层递归解压（{reason}）",
            RecursionStopReason.NeedsDecision => $"已完成 {done} 层，停在第 {done + 1} 层，原因：{reason}",
            // "源文件已变化"时一层都没解是**常态**（拦在第 0 层开工之前），
            // 所以它不走下面那句"第 1 层就没能解开" —— 那句话会让人以为解压失败。
            RecursionStopReason.SourceChanged => $"未开始解压，原因：{reason}",

            /*
             * 两义那一档（引擎既说密码不对、又说数据坏了）：结论里**同时**保留两种可能
             * 与**引擎原话**（用户 2026-09-30 真机；与 7-Zip 侧同一条口径）。
             * 原话取那一层自己记下来的 Message —— BuildLayerReport 写进去的就是引擎原话。
             */
            RecursionStopReason.PasswordOrCorrupted => (done > 0
                    ? $"已完成 {done} 层，停在第 {done + 1} 层，原因："
                    : "第 1 层就没能解开，原因：")
                + DescribeAmbiguousPasswordFailure(layers),

            /*
             * 分卷缺失（免试那一档）：结论里必须带上**缺哪几片**（不变量 7 要的就是"缺哪几个"），
             * 而那段证据在那一层的 Message 里 —— 与两义那一档同一个写法：取那一层自己记下来的正文。
             */
            RecursionStopReason.MissingVolume => (done > 0
                    ? $"已完成 {done} 层，停在第 {done + 1} 层，原因："
                    : "第 1 层就没能解开，原因：")
                + DescribeMissingVolumeFailure(layers),

            _ => done > 0
                ? $"已完成 {done} 层，停在第 {done + 1} 层，原因：{reason}"
                : $"第 1 层就没能解开，原因：{reason}"
        };

        var parts = new List<string> { head };

        /*
         * "还有哪几个内层包没展开"必须写出来（不变量 8）。
         * 只报"已完成"而把没展开的分支咽下去，用户会以为这就是最终数据 ——
         * 这与"部分成功不得显示为成功"是同一类问题。
         *
         * ⚠ 2026-10-04（真机）：过去只写**个数**（"该层还有 1 个内层包未展开"），
         * 用户看不出是哪一个 —— 他只能自己去工作区里翻。现在**点名**（前
         * <see cref="MaxUnexpandedNameLines"/> 个，多出来的折成"…还有 K 个"）。
         */
        if (unexpandedNames.Count > 0)
        {
            parts.Add(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RecursionUnexpandedListFormat,
                unexpandedNames.Count,
                DescribeUnexpandedNames(unexpandedNames)));
        }

        /*
         * 2026-10-07 真机 EEEE：递归跑在任务里时，发布目标是**这一单的入仓目录**
         * （`<工作区>\<任务>-hash\stage`）—— 老写法把它印成「产物：…」，而下一句又是
         * 「已发布 1 个文件到 <同一个 stage>」，用户读成"报了解压成功、产物却留在工作区没发布"
         * （日志下一行其实已是"定稿完成 → 落点"）。
         * ⇒ 入仓那一档**不再重复印内部路径**，只留下面 publishMessage 里那句如实的话
         * （"已入仓 N 个文件（中间产物，定稿时才搬进落点）"）；真的发布到落点时照旧印。
         */
        bool intoIntakeStaging = IsInsideWorkspaceRoot(finalOutputPath);

        if (completed && !string.IsNullOrWhiteSpace(finalOutputPath) && !intoIntakeStaging)
        {
            parts.Add($"产物：{finalOutputPath}");
        }

        if (!string.IsNullOrWhiteSpace(publishMessage))
        {
            parts.Add(publishMessage);
        }

        /*
         * 没有发布就一定要写明工作区位置（规则 7）：部分完成、取消、等待用户决定这三种情形下
         * 产物都还留在工作区里，用户按这个路径就能看到已经解出来的部分（需求书 §十二：
         * 递归停止后必须保存"工作区位置"）。
         *
         * 判据用 !completed 而不是 partiallyCompleted：取消时可能一层都还没解出来
         * （partiallyCompleted 为 false），但"工作区在哪"同样是用户最需要的信息。
         */
        if (!completed)
        {
            parts.Add($"未发布的产物留在工作区：{workspace.TaskDirectory}");
        }

        if (!string.IsNullOrWhiteSpace(extraMessage))
        {
            parts.Add(extraMessage);
        }

        /*
         * ===== 换引擎兜底那句必须进结论（用户 2026-10-05 口径）=====
         *
         * 跑过 ⇒ 写清"是哪个引擎报的密码错、已经换 WinRAR 把同一批候选再试过一遍"；
         * 没跑成（本机没找到 WinRAR）⇒ 写清"所以没换引擎再试"。
         * ⛔ 不许静默跳过：不写这一句，用户会以为程序压根没换过引擎（而 7-Zip 那句"密码错"
         * 会被读成"密码真的不对"）。
         */
        if (!string.IsNullOrWhiteSpace(passwordFallbackNote))
        {
            parts.Add(passwordFallbackNote);
        }

        return string.Join("；", parts);
    }

    /// <summary>
    /// 「这一层还有哪几个内层包没展开」的名单（前 <see cref="MaxUnexpandedNameLines"/> 个，
    /// 多出来的折成"…还有 K 个"）。
    ///
    /// <para><c>internal</c>（2026-10-04）：一键档停在这一层的那一行也要点名（用户原话
    /// 「停因里要点名那个未展开的包」），而那一支会把递归结论改写成"只解了当前这一层"、
    /// 正文里那份名单到不了用户眼前 ⇒ 由 <c>ExtractionCoordinator</c> 转调**这一个**出口，
    /// ⛔ 不另写一份"前 5 个 + …还有 K 个"。</para>
    ///
    /// <para>只写**文件名**（§8 隐私红线），而且用**包基名之外的原文文件名**：
    /// 用户拿着它才能在目录里对上号（这正是"看不出是哪个"要治的那件事）。</para>
    /// </summary>
    internal static string DescribeUnexpandedNames(IReadOnlyList<string> names)
    {
        var shown = new List<string>();

        foreach (string name in names.Take(MaxUnexpandedNameLines))
        {
            string fileName = Path.GetFileName(name);

            shown.Add(fileName.Length == 0 ? name : fileName);
        }

        string text = string.Join("、", shown);

        if (names.Count <= shown.Count)
        {
            return text;
        }

        return text + string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.RecursionUnexpandedMoreFormat,
            names.Count - shown.Count);
    }

    /// <summary>
    /// 停因那句话。<paramref name="unexpandedCount"/> 只用于"多分支"那两档的**真实数量**。
    ///
    /// <para>⚠ 2026-10-04（真机）：这两档过去写死"多个"，而判据其实是
    /// <see cref="HasOnlyInformationalSiblings"/> —— **1 个内层归档 + 它旁边还有别的文件**
    /// 也会停在这一档，于是日志里出现"多个内层归档未展开；该层还有 **1 个**内层包未展开"
    /// 这种自相矛盾。现在按真实数量说，并且把 1 个那一档的**真实理由**说出来。</para>
    /// </summary>
    private static string DescribeStopReason(RecursionStopReason stopReason, int unexpandedCount = 0)
    {
        return stopReason switch
        {
            RecursionStopReason.Completed => "没有更多内层归档",
            RecursionStopReason.NeedsDecision => unexpandedCount > 1
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RecursionNeedsDecisionReasonMultipleFormat,
                    unexpandedCount)
                : StatusText.RecursionNeedsDecisionReasonSingle,
            RecursionStopReason.BranchNotExpanded => unexpandedCount > 1
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RecursionBranchStopReasonMultipleFormat,
                    unexpandedCount)
                : StatusText.RecursionBranchStopReasonSingle,
            RecursionStopReason.MaxDepthReached => "已达到最大递归层数",
            RecursionStopReason.MaxTotalFilesReached => "已达到累计输出文件数上限",
            RecursionStopReason.MaxTotalSizeReached => "已达到累计输出总大小上限",
            // 与上一条同档（都是"撞上程序的安全上限，不是包坏了"）：措辞统一走 StatusText。
            RecursionStopReason.MaxSingleFileSizeReached => StatusText.RecursionSingleFileSizeReachedReason,
            // 文件名已加密（-mhe / -hp）：与自己"密码错误"那一档分开说（措辞与单层路径同一句）。
            RecursionStopReason.EncryptedHeaders => StatusText.RecursionEncryptedHeadersReason,

            /*
             * 分卷缺失（免试那一档）：详细证据（缺哪几片 / 还差多少字节）在那一层的 Message 里，
             * 由 `BuildSummary` 的同一个分支取出来（与两义那一档同一种写法）。
             */
            RecursionStopReason.MissingVolume => StatusText.RecursionMissingVolumeReason,
            RecursionStopReason.ExpansionRatioExceeded => "本层展开比超限，疑似压缩炸弹",
            RecursionStopReason.TooManyInnerArchives => "本层内层归档数量超过上限",
            RecursionStopReason.PasswordAttemptsExceeded => "已达到密码尝试次数上限（候选还有剩余）",
            RecursionStopReason.WrongPassword => "密码错误：所有候选都试过了",
            RecursionStopReason.Corrupted => "压缩包损坏",

            // 两义那一档：两种可能都必须留着（⛔ 不许压成"损坏"或"密码错误"）。
            // 任务级结论那一条会再带上引擎原话（见 BuildSummary 的同名分支）。
            RecursionStopReason.PasswordOrCorrupted => "密码可能不对，也可能这个包的数据坏了",

            RecursionStopReason.UnsafeEntry => "归档内存在不安全路径，已拒绝解压",
            // 不变量 11：这一条**不是"解不开"**，而是"手上这份识别结果已经不对应这个文件了"。
            // 措辞必须让用户知道该做什么（重新扫描），而不是去怀疑包坏了或换个引擎。
            RecursionStopReason.SourceChanged => "源文件已变化：识别结果作废，本层没有开始解压（引擎未被调用）",
            RecursionStopReason.UserCancelled => "用户取消",
            RecursionStopReason.EngineFailed => "引擎操作失败",
            _ => "未知原因"
        };
    }

    /// <summary>
    /// 分卷缺失（免试那一档）的结论正文：**缺哪几片 + 字节数证据**（取那一层自己记下来的 Message）。
    /// 拿不到正文时退回停因那句话 —— ⛔ 绝不用一句"未知原因"把它盖掉，也⛔ 不许说成"引擎解不开"
    /// （引擎一次都没被调用过）。
    /// </summary>
    private static string DescribeMissingVolumeFailure(List<RecursionLayerReport> layers)
    {
        RecursionLayerReport? failed = layers.LastOrDefault(
            layer => layer != null && !layer.Success && !string.IsNullOrWhiteSpace(layer.Message));

        return failed == null
            ? StatusText.RecursionMissingVolumeReason
            : failed.Message;
    }

    /// <summary>
    /// 两义那一档的结论正文：**两种可能 + 引擎原话**（原话取那一层自己记下来的 Message）。
    /// 拿不到原话时只写两种可能 —— ⛔ 绝不用一句"未知原因"把它盖掉。
    /// </summary>
    private static string DescribeAmbiguousPasswordFailure(List<RecursionLayerReport> layers)
    {
        RecursionLayerReport? failed = layers.LastOrDefault(
            layer => layer != null && !layer.Success && !string.IsNullOrWhiteSpace(layer.Message));

        return failed == null
            ? "密码可能不对，也可能这个包的数据坏了"
            : $"密码可能不对，也可能这个包的数据坏了（引擎原话：{failed.Message}）";
    }

}
