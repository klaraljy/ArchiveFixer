using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 「部分完成」这一档**怎么对用户说**：全部日志文案、级别与参数顺序的唯一出口
/// （判定在 <see cref="PartialPublishAdmission"/>、搬运与记账在
/// <see cref="PartialPublishPublisher"/>、过程物保留在 <see cref="ProcessArtifactPreserver"/>）。
///
/// <para>为什么单独一个类：这一档的措辞本身就是口径（"压缩包损坏"必须明说、
/// "源包原地不动"必须写、没发布时必须点名原因），它不该散在判定/搬运的分支里跟着一起改。
/// ⛔ 本类只**写日志**（<see cref="IPartialPublishStepHost.AppendLog"/>），不判任何一件事，
/// 也不动一个字节。</para>
/// </summary>
internal sealed class PartialPublishLog
{
    private readonly IPartialPublishStepHost _host;

    internal PartialPublishLog(IPartialPublishStepHost host)
    {
        _host = host;
    }

    /// <summary>
    /// 不是"损坏压缩包"那一档 ⇒ 一个字节都不发布，并点名引擎给的结构化错误类型
    /// （空串要说成"引擎没给出结构化错误类型"，⛔ 不许留个空位让人猜）。
    /// </summary>
    internal void NotDamaged(ArchiveTask task, PartialPublishStep.PartialPublishCandidate candidate) =>
        _host.AppendLog(
            "INFO",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishNotDamagedFormat,
                task.FileName,
                string.IsNullOrWhiteSpace(candidate.DetectedErrorType)
                    ? "引擎没给出结构化错误类型"
                    : candidate.DetectedErrorType));

    /// <summary>与「空间不足」模式硬互斥：写一行 INFO 说清为什么没生效，⛔ 设置一个字都不改。</summary>
    internal void SpaceTightBlocked(ArchiveTask task) =>
        _host.AppendLog(
            "INFO",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishSpaceTightBlockedFormat,
                task.FileName));

    /// <summary>这一单没有可用的成品目录（拿不到落点）⇒ 按「判不出 ⇒ 什么都不做」处理。</summary>
    internal void NoOutputRoot(ArchiveTask task) =>
        _host.AppendLog(
            "INFO",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishSkippedFormat,
                task.FileName,
                "这一单没有可用的成品目录（拿不到落点），按「判不出 ⇒ 什么都不做」处理"));

    /// <summary>
    /// 这一份不发布：原因原样带出来。空间体检没过与"判不出该不该发"是两句话
    /// （前者说"现在不该发"，后者说"判不出"），⛔ 不许合成一句。
    /// </summary>
    internal void NotPublished(ArchiveTask task, PartialPublishOutcome outcome) =>
        _host.AppendLog(
            "INFO",
            string.Format(
                CultureInfo.CurrentCulture,
                outcome.BlockedBySpace
                    ? StatusText.PartialPublishSpaceBlockedFormat
                    : StatusText.PartialPublishSkippedFormat,
                task.FileName,
                outcome.Reason));

    /// <summary>真的发出去了：点名发了几个 / 多少字节 / 发到哪 / 清单共几个 / 没发出去几个。</summary>
    internal void Published(ArchiveTask task, PartialPublishOutcome outcome) =>
        _host.AppendLog(
            "INFO",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishedFormat,
                task.FileName,
                outcome.PublishedCount,
                TaskSpaceEstimate.FormatSize(outcome.PublishedBytes),
                outcome.DestinationDirectory,
                outcome.Verdict.Entries.Count,
                TaskSpaceEstimate.FormatSize(outcome.Verdict.ExpectedBytes),
                outcome.Verdict.Rejected.Count));

    /// <summary>
    /// ⛔ **必须明说"压缩包损坏"**（用户 2026-10-14 口径：「这个损坏的压缩包我们肯定是要在日志里面
    /// 去报一个压缩包损坏的情况，然后不完整的内容物就留下来」）。
    ///
    /// <para>为什么写成 WARN 而不是 INFO：成功任务只留一行摘要（第 44 条），而这一单是"失败 + 救回一部分"
    /// ⇒ 只写 INFO 的话用户按"失败"去查，看不到"哪些是坏的、哪些已经救出来了"这件事。</para>
    /// </summary>
    internal void DamagedArchive(ArchiveTask task, PartialPublishOutcome outcome) =>
        _host.AppendLog(
            "WARN",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishDamagedArchiveFormat,
                task.FileName,
                outcome.Verdict.Rejected.Count));

    /// <summary>同名让位（<c>名字(1).ext</c>）时补一句：一个字节都没有覆盖。</summary>
    internal void Renamed(ArchiveTask task, PartialPublishOutcome outcome) =>
        _host.AppendLog(
            "INFO",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishedRenamedFormat,
                task.FileName,
                outcome.Publish.RenamedCount));

    /// <summary>这一档对源包的动作（一行 INFO，让人一眼看到"源包刻意没动"）。</summary>
    internal void SourceKept(ArchiveTask task) =>
        _host.AppendLog(
            "INFO",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishSourceKeptFormat,
                task.FileName));

    /// <summary>
    /// 计划里还有文件没能发布 ⇒ 其余物与工作区**原样留着**（那是这一份唯一的线索），
    /// 并把头几条失败原因拼进来（详情在日志里，⛔ 不刷屏）。
    /// </summary>
    internal void RestKept(ArchiveTask task, PartialPublishOutcome outcome) =>
        _host.AppendLog(
            "WARN",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishRestKeptFormat,
                task.FileName,
                $"计划里有 {outcome.Publish.Failures.Count} 个文件没能发布（{DescribeFirstFailures(outcome.Publish.Failures)}），"
                + "所以其余物与工作区都原样留着"));

    /// <summary>懒取清单这一次列目录出错了 ⇒ 判不出 ⇒ 什么都不发布（原因原样带出来）。</summary>
    internal void ListFailed(ArchiveTask task, Exception ex) =>
        _host.AppendLog(
            "WARN",
            string.Format(
                CultureInfo.CurrentCulture,
                StatusText.PartialPublishSkippedFormat,
                task.FileName,
                $"为了逐条核对清单又列了一次目录，这次列目录出错了（{ex.Message}）—— 判不出 ⇒ 什么都不发布"));

    /// <summary>把发布失败的头几条拼成一句话（最多 3 条，详情在日志里，⛔ 不刷屏）。</summary>
    private static string DescribeFirstFailures(IReadOnlyList<(string Path, string Reason)> failures)
    {
        if (failures == null || failures.Count == 0)
        {
            return "原因没有记下来";
        }

        var parts = new List<string>();

        foreach ((string path, string reason) in failures.Take(3))
        {
            parts.Add($"{path}：{reason}");
        }

        if (failures.Count > 3)
        {
            parts.Add($"还有 {failures.Count - 3} 个");
        }

        return string.Join("；", parts);
    }
}
