using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>一次「部分完成发布」的完整结论：判据 → 二次空间体检 → 真搬。</summary>
    public sealed class PartialPublishOutcome
    {
        /// <summary>真的发布了（至少一个文件搬进了 <c>部分完成\</c>）。</summary>
        public bool Published { get; init; }

        /// <summary>逐条对账的结论（没发布时也带着"为什么"）。</summary>
        public PartialPublishVerdict Verdict { get; init; } = new();

        /// <summary>发布的执行结论（没走到那一步时是"一个字节都没动"）。</summary>
        public PartialPublisher.Result Publish { get; init; } = new();

        /// <summary>发布前的二次空间体检没过（不是"判不出该不该发"，而是"现在不该发"）。</summary>
        public bool BlockedBySpace { get; init; }

        /// <summary>给人看的一句话（走 <c>StatusText</c> 常量，调用方原样写日志）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>发布落点（<c>&lt;包名&gt;\部分完成</c>）；没发布时为空串。</summary>
        public string DestinationDirectory => Publish.DestinationDirectory;

        public int PublishedCount => Publish.MovedCount;

        public long PublishedBytes => Publish.MovedBytes;
    }

    /// <summary>
    /// **「部分完成」发布的入口**（唯一一处把三件事串起来的地方）：逐条对账
    /// （<see cref="PartialPublishPlanner"/>）→ **发布前二次空间体检** → 真搬
    /// （<see cref="PartialPublisher"/>）。
    ///
    /// <para><b>为什么这三步必须在一起</b>（用户 2026-10-02 顾虑 3：「这个的操作最少差不多就占了两份原包的空间
    /// 要在开启界面详细写清楚」）：部分完成这一档跑完，盘上是"源包 + 已解出的内容物"**两份**，
    /// 而这一刻盘上往往本来就不宽裕（常常正是因为空间紧才失败）。所以"要不要发"不能只看清单对不对得上，
    /// 还要**在真搬之前再探一次空间** —— 不够就一个字节都不搬（兜底永远落在"什么都不做"那一档）。</para>
    ///
    /// <para><b>⛔ 它自己不碰源包、不动源文件</b>：只读暂存目录、只写 <c>&lt;包名&gt;\部分完成\</c>。
    /// 清理（其余物里的过程物 / 工作区）是调用方的事，判据也由调用方守。</para>
    /// </summary>
    public static class PartialPublishRunner
    {
        /// <summary>
        /// 发布前的二次空间体检。返回 null = 放行；非 null = 一句原因（**不发布**）。
        ///
        /// <para><paramref name="requiredBytes"/> 取"这次要搬进去的字节数"：同盘时那次 <c>File.Move</c>
        /// 其实不新写字节（只改目录项），跨盘时才是真复制 —— 判据刻意按**更保守**的那一档算
        /// （宁可少发一份，也不在紧巴巴的盘上再压一份进去）。</para>
        /// </summary>
        public delegate string? SpaceProbe(string packageOutputRoot, long requiredBytes);

        /// <summary>
        /// 生产用的空间体检：唯一出口仍然是 <see cref="SpaceChecker"/>（与空间门同一处取数口径），
        /// 保留余量取 <see cref="SpaceGate.DefaultReserveBytes"/>（与空间门同一个数，⛔ 不另写一个）。
        /// </summary>
        public static string? ProbeSpaceViaSpaceChecker(string packageOutputRoot, long requiredBytes)
        {
            return SpaceChecker.HasEnoughSpace(
                packageOutputRoot,
                requiredBytes,
                SpaceGate.DefaultReserveBytes,
                out string reason)
                ? null
                : reason;
        }

        /// <summary>
        /// 走一遍：对账 → 体检 → 搬。**任何一步判不出都返回"没发布"**，一个字节都不动。
        /// </summary>
        /// <param name="stagingRoot">已解出来的东西在哪（本任务的暂存目录 / 这一层的产物目录）。</param>
        /// <param name="packageOutputRoot">这一份包的输出根（<c>&lt;目标&gt;\&lt;包名&gt;</c>）。</param>
        /// <param name="manifestEntries">清单里的文件条目（相对路径 + 解压后字节数）。</param>
        /// <param name="engineFailedEntries">引擎点名的坏条目。</param>
        /// <param name="engineReportedErrorCount">引擎自报的出错条目数（闸门）。</param>
        /// <param name="engineReportedFailure">这一趟引擎是不是报了失败（闸门 5）。</param>
        /// <param name="probe">二次空间体检（用例注入；生产走 <see cref="ProbeSpaceViaSpaceChecker"/>）。</param>
        /// <param name="artifactProbe">"目标名被占了没有"的探测（用例注入用；生产走真实文件系统）。</param>
        public static PartialPublishOutcome Run(
            string? stagingRoot,
            string? packageOutputRoot,
            IReadOnlyList<(string Path, long Size)>? manifestEntries,
            IReadOnlyList<string>? engineFailedEntries = null,
            int engineReportedErrorCount = 0,
            bool engineReportedFailure = false,
            SpaceProbe? probe = null,
            IArtifactTargetProbe? artifactProbe = null)
        {
            PartialPublishVerdict verdict;

            try
            {
                /*
                 * 盘上那条路径的字节数从**暂存目录**读（清单里的相对路径 → 文件）。
                 * 读不到（不存在 / 读不了）一律当"不在盘上"（-1）⇒ 那一条判"缺"、不发布。
                 */
                string staging = stagingRoot ?? string.Empty;

                verdict = PartialPublishPlanner.Plan(
                    manifestEntries,
                    relative => MeasureInStaging(staging, relative),
                    engineFailedEntries,
                    engineReportedErrorCount,
                    engineReportedFailure);
            }
            catch (Exception ex)
            {
                // 对账本身出意外 ⇒ 什么都不发布（判不出 ⇒ 什么都不做），如实带出原因。
                return new PartialPublishOutcome
                {
                    Reason = $"逐条对账时出现意外错误（{ex.Message}），一个字节都不发布"
                };
            }

            if (!verdict.CanPublish)
            {
                return new PartialPublishOutcome { Verdict = verdict, Reason = verdict.Reason };
            }

            if (string.IsNullOrWhiteSpace(packageOutputRoot))
            {
                return new PartialPublishOutcome
                {
                    Verdict = verdict,
                    Reason = "这一单没有可用的输出根（拿不到成品目录），不发布"
                };
            }

            string? spaceReason = (probe ?? ProbeSpaceViaSpaceChecker)(
                packageOutputRoot,
                verdict.PublishableBytes);

            if (spaceReason != null)
            {
                return new PartialPublishOutcome
                {
                    Verdict = verdict,
                    BlockedBySpace = true,
                    Reason = spaceReason
                };
            }

            PartialPublisher.Result publish = PartialPublisher.Publish(
                stagingRoot,
                verdict.Publishable,
                packageOutputRoot,
                artifactProbe);

            return new PartialPublishOutcome
            {
                Published = publish.MovedCount > 0,
                Verdict = verdict,
                Publish = publish,
                Reason = verdict.Reason
            };
        }

        /// <summary>暂存目录里这条相对路径的字节数；不在 / 读不了返回 <c>-1</c>（= "缺"）。</summary>
        private static long MeasureInStaging(string stagingRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(stagingRoot) || string.IsNullOrWhiteSpace(relativePath))
            {
                return -1;
            }

            try
            {
                string combined = SafePathHelper.Combine(stagingRoot, relativePath);
                string full = SafePathHelper.GetFullPathSafe(combined);

                if (full.Length == 0 || !SafePathHelper.FileExists(full))
                {
                    return -1;
                }

                return new System.IO.FileInfo(full).Length;
            }
            catch
            {
                return -1;
            }
        }
    }
}
