using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 定稿收尾的摘要与日志（**唯一出口**）：<c>StageCommitResult.Message</c>、日志里那一行
/// "定稿完成 —— …"、以及①页「错误信息」列读的是**同一份**字符串 —— 所以它只能拼一次。
///
/// <para>⛔ 移动决策点的日志（同名冲突 / 跳过 / 覆盖 / 改名 / 同一物理文件 / 接片已到位）
/// 留在 <see cref="PlacementMoveExecutor"/> 的执行现场：那些行依附当场的判据与局部变量，
/// 抄出来就会和判据漂移。这里只收"定稿结束"的一次性收尾（摘要 / 内容物点名 / 覆盖留痕）。</para>
/// </summary>
internal static class PlacementCommitLog
{
    /// <summary>
    /// 定稿摘要 —— **"没能搬运"那几条必须带上明细**（用户 2026-10-04 真机）。
    ///
    /// <para>现场：`老王.apk` 那一单写着「定稿搬运失败，产物没能写进输出目录：…；10 项没能搬运」，
    /// ①页「错误信息」列与日志里都只有这一个数字 —— 那 10 条 `文件名（原因原文）` 在收集处
    /// （执行器里的 `failures`）就已经拿到了，却在 CommitFailed 的提前 return 那条路上被丢掉，
    /// 用户手里没有任何可行动的线索（而批末诊断还指着他去看"引擎原话"，定稿类失败没有引擎原话）。</para>
    ///
    /// <para>为什么补在摘要里而不是另写一行：摘要这一份字符串**同时**是日志那一行
    /// （"定稿完成 —— …"）与①页「错误信息」列（`StageCommitResult.Message` →
    /// `CommitFailureMessage`）的来源 ⇒ 一处出口、两个消费点，不会各写一份、也不会对不上。
    /// ⛔ 判据一个都没动：这里只把**已经收集到的**事实说出来。</para>
    /// </summary>
    internal static string BuildSummary(
        FinalizePlan plan,
        string destinationDirectory,
        string processDirectory,
        int movedProcess,
        int renamed,
        int overwrittenCount,
        int skippedCount,
        int failedCount,
        IReadOnlyList<string> failures)
    {
        return
            $"内容物 {plan.ContentFileCount} 个文件 → {destinationDirectory}" +
            (movedProcess > 0
                ? $"；其余物 {movedProcess} 项（{plan.ProcessArtifactTotalSize} 字节）→ {processDirectory}"
                : string.Empty) +
            (renamed > 0 ? $"；{renamed} 项同名，已改名未覆盖" : string.Empty) +
            (overwrittenCount > 0 ? $"；{overwrittenCount} 项同名，按你的选择覆盖（已写日志）" : string.Empty) +
            (skippedCount > 0 ? $"；{skippedCount} 项同名，按你的选择跳过" : string.Empty) +
            (failedCount > 0 ? $"；{failedCount} 项没能搬运" : string.Empty) +
            (failedCount > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.FinalizeMoveFailureDetailFormat,
                    FinalizeMoveSupport.MaxFinalizeContentNameLines,
                    FinalizeMoveSupport.DescribeFinalizeMoveFailures(failures))
                : string.Empty);
    }

    /// <summary>
    /// 「定稿完成」这句话**不许在"没解出东西"时读成正常完成**（用户 2026-09-24 要求）。
    ///
    /// <para>现场误读：日志写着"定稿完成 —— 内容物 0 个文件 → …；其余物 2 项（0 字节）→ …"，
    /// 而实际上校验早已判否、那 2 项是 0 字节垃圾 —— 用户看到"定稿完成"就以为流程正常走完了。
    /// 现在两种情况各挂一句明确的结论：校验未通过 → "⚠ 校验未通过，产物视为无效"；
    /// 校验通过但一个内容物都没有 → 如实说"这次没有内容物"（那是合法的，例如只出过程物）。</para>
    /// </summary>
    internal static string BuildContentNote(int contentFileCount, bool verificationPassed)
    {
        return contentFileCount > 0
            ? string.Empty
            : verificationPassed
                ? "（⚠ 这次没有内容物，只有其余物）"
                : "（⚠ 校验未通过，产物视为无效；不定稿有效内容、不搬源包）";
    }

    /// <summary>
    /// 把定稿收尾的三类日志按既有顺序追加进 <paramref name="logEntries"/>：
    /// ①「定稿完成」那一条；②**产物点名**（用户 2026-09-25："出问题我看日志就能知道"）；
    /// ③ 覆盖留痕（用户事后追查"我原来那份去哪了"时有据可依）。
    ///
    /// <para>为什么必须列名字：续解那一步是按"这一轮新出现的文件"找下一层的，而名字被改坏的产物
    /// （真机上是 `222.ra删除r`）在日志里如果只报个数，用户就只能自己去翻目录 —— 那次就是这么绕远的。
    /// 上限 <see cref="FinalizeMoveSupport.MaxFinalizeContentNameLines"/> 条，超出只报个数
    /// （几千个文件时不能让日志爆掉），但"还有多少没列"必须写出来。</para>
    /// </summary>
    internal static void AppendCompletionEntries(
        List<(string Level, string Message)> logEntries,
        string logName,
        FinalizePlan plan,
        string summary,
        string contentNote,
        int failedCount,
        IReadOnlyList<string> overwritten)
    {
        logEntries.Add((failedCount == 0 ? "INFO" : "WARN", $"{logName}：定稿完成{contentNote} —— {summary}"));

        if (plan.Moves.Count > 0)
        {
            List<PlannedMove> contentMoves = plan.Moves
                .Where(move => !plan.ProcessArtifactSources.Contains(move.From))
                .ToList();

            List<string> contentNames = contentMoves
                .Select(move => Path.GetFileName(move.To))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(FinalizeMoveSupport.MaxFinalizeContentNameLines)
                .ToList();

            if (contentNames.Count > 0)
            {
                logEntries.Add((
                    "INFO",
                    $"{logName}：本次内容物 —— {string.Join("、", contentNames)}"
                    + (contentMoves.Count > contentNames.Count
                        ? $"（…还有 {contentMoves.Count - contentNames.Count} 项没列出来）"
                        : string.Empty)));
            }
        }

        // 覆盖留痕：被顶掉的落点单独再写一条，用户事后追查"我原来那份去哪了"时有据可依。
        foreach (string overwrittenPath in overwritten)
        {
            logEntries.Add(("WARN", $"{logName}：已覆盖（原文件按两阶段落位删除）：{overwrittenPath}"));
        }
    }
}
