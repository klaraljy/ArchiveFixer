using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 其余物删除的**结果汇总/日志**阶段：一次删除尝试 → <see cref="RestPurgeOutcome"/>
/// 与给人看的那几句话。⛔ 不碰盘、不做判据，只把事实（删成了没有 / 多少条 / 释放多少字节）
/// 翻成结论和日志行。
///
/// <para><b>为什么单独一层</b>：两档删除（整份 / 半份清理）的事实位集合完全一样
/// （<see cref="RestPurgeOutcome.Attempted"/> / <see cref="RestPurgeOutcome.Succeeded"/> /
/// <see cref="RestPurgeOutcome.RemovedAsEmptyShell"/> / <see cref="RestPurgeOutcome.LogLines"/>），
/// 只有措辞不同。事实位在这里收口，调用方就不会漏填某一格 ——
/// 尤其是"空壳就地删"必须留 <see cref="RestPurgeOutcome.RemovedAsEmptyShell"/>：
/// 调用方据此选文案，⛔ 不许按"0 项 0 字节"猜（那证明不了没进回收站）。</para>
/// </summary>
internal static class RestPurgeReporter
{
    /// <summary>被闸门挡下 / 没有可删内容：<b>一个字节都没动</b>（<see cref="RestPurgeOutcome.Attempted"/> = false）。</summary>
    internal static RestPurgeOutcome Skip(string message)
    {
        return new RestPurgeOutcome
        {
            Attempted = false,
            Succeeded = false,
            Message = message
        };
    }

    /// <summary>
    /// 删除日志（每条目标的完整一行：路径 + 理由 + 条目数 + 总大小），
    /// 调用方原样写进界面/文件日志（两处各写一份会出现两个时间戳，所以只在这里取一次）。
    /// </summary>
    internal static IReadOnlyList<string> ToLogLines(DeleteResult result)
    {
        return result.LogEntries.Select(entry => entry.ToDisplayText()).ToList();
    }

    /// <summary>"为什么没删掉"那半句：优先失败清单，其次整体消息（与原先逐字相同）。</summary>
    internal static string DescribeFailureDetail(DeleteResult result)
    {
        return result.FailureReasons.Count > 0
            ? string.Join("；", result.FailureReasons)
            : result.Message;
    }

    /// <summary>整份删除那一档：空壳就地删成功的那一行。</summary>
    internal static string DescribeDirectoryShellRemoved(string name)
    {
        return $"{name}：其余物目录里只剩下空壳（0 个文件），已就地删掉空目录，没有送进回收站";
    }

    /// <summary>半份清理那一档：剩下的全是空壳、按项就地删掉之后的那一行。</summary>
    internal static string DescribeKeptItemsShellRemoved(string name, int removedCount)
    {
        return $"{name}：其余物里除要保留的以外只剩下空壳（0 个文件），已就地删掉 {removedCount} 个空目录，"
               + "没有送进回收站";
    }

    /// <summary>"保留的那一份仍在盘上：…"那半句（半份清理的两条成功路径都用它）。</summary>
    internal static string DescribeKeptSuffix(IReadOnlyList<string> keep)
    {
        return $"，保留的那一份仍在盘上：{string.Join("、", keep)}";
    }

    /// <summary>整份删除失败的那一句。</summary>
    internal static string DescribeDirectoryPurgeFailed(string name, string failure)
    {
        return $"{name}：其余物没能删掉（{failure}）—— "
               + "内容物不受影响，但空间没有回来";
    }

    /// <summary>半份清理失败的那一句。</summary>
    internal static string DescribeKeptItemsPurgeFailed(string name, int targetCount, string failure)
    {
        return $"{name}：其余物里的 {targetCount} 个过程物没能删掉（{failure}）—— "
               + "内容物与源包都不受影响";
    }

    /// <summary>整份删除成功的那一句（条目数 + 真正释放的字节数 + "不进回收站"）。</summary>
    internal static string DescribeDirectoryPurged(string name, string directory, int entryCount, long freedBytes)
    {
        return $"{name}：其余物已彻底删除 {directory}"
               + $"（{DescribeFreed(entryCount, freedBytes)}）";
    }

    /// <summary>半份清理成功的那一句（删掉几项 / 共几项 + 保留的那一份仍在盘上）。</summary>
    internal static string DescribeKeptItemsPurged(
        string name,
        int successCount,
        int targetCount,
        int entryCount,
        long freedBytes,
        IReadOnlyList<string> keep)
    {
        return $"{name}：其余物里除要保留的以外已删掉 {successCount} / {targetCount} 个项"
               + $"（{DescribeFreed(entryCount, freedBytes)}）"
               + DescribeKeptSuffix(keep);
    }

    /// <summary>"N 个条目 / 释放 X，不进回收站"那半句（两档同一口径）。</summary>
    private static string DescribeFreed(int entryCount, long freedBytes)
    {
        return $"{entryCount} 个条目 / 释放 {TaskSpaceEstimate.FormatSize(freedBytes)}，不进回收站";
    }

    /// <summary>空壳就地删成功：释放 0 字节、0 个条目，并留下 <see cref="RestPurgeOutcome.RemovedAsEmptyShell"/> 事实位。</summary>
    internal static RestPurgeOutcome RemovedAsEmptyShell(
        string directory,
        string message,
        IReadOnlyList<string> logLines)
    {
        return new RestPurgeOutcome
        {
            Attempted = true,
            Succeeded = true,
            Directory = directory,
            FreedBytes = 0,
            EntryCount = 0,
            RemovedAsEmptyShell = true,
            Message = message,
            LogLines = logLines
        };
    }

    /// <summary>动过手但一个目标都没删成：空间没有回来（<see cref="RestPurgeOutcome.Attempted"/> = true）。</summary>
    internal static RestPurgeOutcome FailedNotRemoved(
        string directory,
        string message,
        IReadOnlyList<string> logLines)
    {
        return new RestPurgeOutcome
        {
            Attempted = true,
            Succeeded = false,
            Directory = directory,
            Message = message,
            LogLines = logLines
        };
    }

    /// <summary>至少删成一个目标。</summary>
    /// <param name="directory">其余物目录（整份删除时就是被删掉的那一个）。</param>
    /// <param name="entryCount">
    /// 条目数：整份删除那一档原先取**第一条结果**的 <c>EntryCount</c>，半份清理那一档取**全部结果之和** ——
    /// 两个口径都照原样保留（由调用方算好传进来），⛔ 不在这里改成统一算法。
    /// </param>
    /// <param name="freedBytes">真正释放的字节数。</param>
    /// <param name="message">一句话结论。</param>
    /// <param name="logLines">删除日志。</param>
    internal static RestPurgeOutcome Removed(
        string directory,
        int entryCount,
        long freedBytes,
        string message,
        IReadOnlyList<string> logLines)
    {
        return new RestPurgeOutcome
        {
            Attempted = true,
            Succeeded = true,
            Directory = directory,
            FreedBytes = freedBytes,
            EntryCount = entryCount,
            Message = message,
            LogLines = logLines
        };
    }
}
