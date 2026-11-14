using ArchiveFixer.Extraction;
using ArchiveFixer.Storage;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 清理结果 → 界面结论（<see cref="CleanupOutcome"/>）的**唯一映射处**。
///
/// <para>
/// 两条执行路径（其余物删除、空文件夹清理）的底层结果类型完全不同
/// （<see cref="DeleteResult"/> 与 <see cref="EmptyFolderCleanResult"/>），但界面上要的东西是同一套：
/// 有没有动手、成功几个、失败几个、释放/回收了多少字节、每一步的日志、一句话结论。
/// 把翻译集中在这里，界面就不用认识两套结果类型，也不会出现"两处对失败的口径不一样"。
/// </para>
/// <para>
/// ⚠ "一项都没动手"与"动手了但全失败"是两件事：前者走 <see cref="NotAttempted"/>（
/// <see cref="CleanupOutcome.Attempted"/> = false），后者是 <see cref="FromDeleteResult"/> 里
/// 成功数为 0 的正常结论。界面据此分别用提示与警告。
/// </para>
/// </summary>
internal static class CleanupOutcomeMapper
{
    /// <summary>其余物删除的结论：成功/失败/释放字节数 + 每一条删除日志。</summary>
    public static CleanupOutcome FromDeleteResult(
        CleanupScope scope,
        DeleteMode mode,
        DeleteResult result)
    {
        return new CleanupOutcome
        {
            Scope = scope,
            Mode = mode,
            Attempted = true,
            SuccessCount = result.SuccessCount,
            FailureCount = result.FailureCount,
            FreedBytes = result.FreedBytes,
            RecycledBytes = result.RecycledBytes,
            FailureReasons = result.FailureReasons,
            LogLines = result.LogEntries.Select(entry => entry.ToDisplayText()).ToList(),
            Message = result.Message
        };
    }

    /// <summary>
    /// 空文件夹清理的结论。
    ///
    /// <para>
    /// **只有"删除动作失败"算失败**：其余跳过（任意层级有文件 / 是链接 / 读不到 / 未确认）
    /// 是判据让它保留的，不是这次清理出错，算进失败数会让用户以为程序坏了。
    /// </para>
    /// </summary>
    public static CleanupOutcome FromEmptyFolderResult(
        EmptyFolderCleanResult result,
        DeleteMode mode)
    {
        var logLines = new List<string>();

        if (result.DeleteResult != null)
        {
            logLines.AddRange(result.DeleteResult.LogEntries.Select(entry => entry.ToDisplayText()));
        }

        return new CleanupOutcome
        {
            Scope = CleanupScope.EmptyFolders,
            Mode = mode,
            Attempted = result.Attempted,
            SuccessCount = result.DeletedDirectories.Count,
            FailureCount = result.Skipped.Count(skip => skip.Reason == EmptyFolderSkipReason.DeleteFailed),
            FreedBytes = result.FreedBytes,
            RecycledBytes = result.RecycledBytes,
            FailureReasons = result.Skipped
                .Where(skip => skip.Reason == EmptyFolderSkipReason.DeleteFailed)
                .Select(skip => $"{skip.Path}：{skip.Message}")
                .ToList(),
            LogLines = logLines,
            Message = result.Message
        };
    }

    /// <summary>一项都没动手的结论：<see cref="CleanupOutcome.Attempted"/> 为 false，一个字节都没动。</summary>
    public static CleanupOutcome NotAttempted(
        CleanupScope scope,
        DeleteMode mode,
        string message)
    {
        return new CleanupOutcome
        {
            Scope = scope,
            Mode = mode,
            Attempted = false,
            Message = message
        };
    }
}
