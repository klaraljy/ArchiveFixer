using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>一条清单条目与盘上实际的比对结论（**逐条**，这就是"敢不敢发布"的最小单位）。</summary>
    public enum PartialEntryState
    {
        /// <summary>清单里有、盘上也在、大小对得上、引擎也没点它的名 ⇒ **可以发布**。</summary>
        Publishable = 0,

        /// <summary>清单里有、盘上没有 ⇒ 缺（引擎没解出来 / 解到一半就断了）。</summary>
        Missing = 1,

        /// <summary>盘上有，但大小与清单对不上（半截 / 被截断）⇒ 坏。</summary>
        SizeMismatch = 2,

        /// <summary>盘上有、大小也对，但**引擎点了它的名**（CRC/Data Error）⇒ 坏。</summary>
        EngineReported = 3
    }

    /// <summary>一条条目的比对结果。</summary>
    public sealed class PartialEntryVerdict
    {
        public string Path { get; init; } = string.Empty;

        public long ExpectedSize { get; init; }

        /// <summary>盘上实际大小；<c>-1</c> = 不在盘上。</summary>
        public long ActualSize { get; init; } = -1;

        public PartialEntryState State { get; init; } = PartialEntryState.Publishable;
    }

    /// <summary>要不要按「部分完成」发布、发哪些、为什么 —— **机器可判的唯一结论**。</summary>
    public sealed class PartialPublishVerdict
    {
        /// <summary>能不能发布。</summary>
        public bool CanPublish { get; init; }

        /// <summary>逐条结论（顺序 = 清单顺序）。</summary>
        public IReadOnlyList<PartialEntryVerdict> Entries { get; init; } = Array.Empty<PartialEntryVerdict>();

        /// <summary>可发布的文件（相对路径 + 清单上的字节数）。</summary>
        public IReadOnlyList<(string Path, long Size)> Publishable { get; init; } = Array.Empty<(string, long)>();

        /// <summary>不发布的条目（相对路径 + 为什么）—— 报告里要点名的那一份。</summary>
        public IReadOnlyList<(string Path, PartialEntryState State)> Rejected { get; init; } =
            Array.Empty<(string, PartialEntryState)>();

        /// <summary>机器可判的原因码（日志与文案都读它，⛔ 不比中文）。</summary>
        public string ReasonCode { get; init; } = string.Empty;

        /// <summary>给人看的一句话（点名原因；文案常量在 <c>StatusText</c>）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>可发布的字节数 / 清单总字节数（报告里用）。</summary>
        public long PublishableBytes { get; init; }

        public long ExpectedBytes { get; init; }
    }

    /// <summary>
    /// **「部分完成」能不能发布、发哪些** —— 唯一出口（纯函数，⛔ 自己不碰盘、不调引擎）。
    ///
    /// <para><b>它回答的问题</b>：解压中途失败了（个别条目坏 / 断了），已经解出来的那些**敢不敢**放进目标目录？
    /// 判据只有"**逐条对账**"这一条路（用户 2026-10-01 真机 `giu910` 之后定的方向，见
    /// `docs/部分完成发布方案.md`）：清单里每一条与盘上实际比 —— 缺的、大小不符的、被引擎点名的，一律**不发**；
    /// 其余的发。⛔ 绝不拿"引擎没报错"单独当依据（中文版 UnRAR 报错是中文，点不出名）。</para>
    ///
    /// <para><b>闸门（判不出 ⇒ 什么都不做）</b>：</para>
    /// <list type="number">
    /// <item><description>清单拿不到 / 没有条目 ⇒ <see cref="ReasonCode"/> = <c>NoManifest</c>，不发；</description></item>
    /// <item><description>引擎自报的坏条目数 &gt; 我们数出来的坏条目数 ⇒ 有点不出名的坏东西 ⇒ 不发（<c>UnnamedEngineErrors</c>）；</description></item>
    /// <item><description>可发布的一个都没有 ⇒ 不发（<c>NothingPublishable</c>）；</description></item>
    /// <item><description>阈值不过（缺得太多）⇒ 不发（<c>TooMuchMissing</c>）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么阈值要卡</b>（用户口径）：557/558 值得发；1/558 发出去只会污染目标目录 ——
    /// 缺太多通常说明是密码 / 格式层面的问题，不是个别条目坏。</para>
    /// </summary>
    public static class PartialPublishPlanner
    {
        /// <summary>原因码（机器可判；日志与 UI 文案都从这里挑，⛔ 不手写中文比对）。</summary>
        public const string ReasonOk = "Ok";

        public const string ReasonNoManifest = "NoManifest";

        public const string ReasonUnnamedEngineErrors = "UnnamedEngineErrors";

        public const string ReasonNothingPublishable = "NothingPublishable";

        public const string ReasonTooMuchMissing = "TooMuchMissing";

        /// <summary>
        /// 引擎明明说"这一趟失败了"，可我们在盘上**一条缺口都对不上**（没缺、没截断、没点名）
        /// ⇒ 那个坏东西在盘上"看着是对的"（典型：本地化 UnRAR 的报错既读不出名字也读不出计数）
        /// ⇒ 一律不发布。
        /// </summary>
        public const string ReasonUnreconciledEngineFailure = "UnreconciledEngineFailure";

        /// <summary>缺多少条以内还值得发布（用户口径：个位数）。</summary>
        public const int MaxMissingEntries = 5;

        /// <summary>或者：已解出的字节占比不低于这个数（两条满足其一即可）。</summary>
        public const double MinPublishableByteRatio = 0.95;

        /// <summary>
        /// 盘上那条路径的字节数；不在盘上返回 <c>-1</c>。
        ///
        /// <para>抽成参数是为了让本类是**纯函数**（用例不必造真文件），也为了调用方能复用同一次目录遍历。</para>
        /// </summary>
        public delegate long SizeProbe(string relativePath);

        /// <summary>
        /// 逐条对账 + 过闸门，给出结论。
        /// </summary>
        /// <param name="manifestEntries">
        /// 清单里的**文件**条目（相对路径 + 解压后字节数）。目录条目由调用方先滤掉。
        /// </param>
        /// <param name="sizeProbe">盘上实际字节数（见 <see cref="SizeProbe"/>）。</param>
        /// <param name="engineFailedEntries">引擎点名的坏条目（<c>ArchiveOperationResult.FailedEntryNames</c>）。</param>
        /// <param name="engineReportedErrorCount">
        /// 引擎自报的坏条目数（<c>Sub items Errors</c> / <c>Total errors</c>）。它是**闸门**：
        /// 比我们数出来的坏条目还多，就说明有点不出名的坏东西 ⇒ 不发。
        /// </param>
        public static PartialPublishVerdict Plan(
            IReadOnlyList<(string Path, long Size)>? manifestEntries,
            SizeProbe sizeProbe,
            IReadOnlyList<string>? engineFailedEntries = null,
            int engineReportedErrorCount = 0,
            bool engineReportedFailure = false)
        {
            if (sizeProbe == null)
            {
                throw new ArgumentNullException(nameof(sizeProbe));
            }

            if (manifestEntries == null || manifestEntries.Count == 0)
            {
                return Fail(ReasonNoManifest, "清单里没有可核对的条目，没法逐条判断哪些能发布（判不出 ⇒ 一个字节都不发布）");
            }

            var named = new HashSet<string>(
                (engineFailedEntries ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(Normalize),
                StringComparer.OrdinalIgnoreCase);

            var verdicts = new List<PartialEntryVerdict>(manifestEntries.Count);

            foreach ((string path, long size) in manifestEntries)
            {
                long actual = sizeProbe(path);
                PartialEntryState state;

                if (actual < 0)
                {
                    state = PartialEntryState.Missing;
                }
                else if (actual != size)
                {
                    state = PartialEntryState.SizeMismatch;
                }
                else if (named.Contains(Normalize(path)))
                {
                    state = PartialEntryState.EngineReported;
                }
                else
                {
                    state = PartialEntryState.Publishable;
                }

                verdicts.Add(new PartialEntryVerdict
                {
                    Path = path,
                    ExpectedSize = size,
                    ActualSize = actual,
                    State = state
                });
            }

            var publishable = verdicts
                .Where(v => v.State == PartialEntryState.Publishable)
                .Select(v => (v.Path, v.ExpectedSize))
                .ToList();

            var rejected = verdicts
                .Where(v => v.State != PartialEntryState.Publishable)
                .Select(v => (v.Path, v.State))
                .ToList();

            long expectedBytes = verdicts.Sum(v => v.ExpectedSize);
            long publishableBytes = publishable.Sum(v => v.Item2);

            /*
             * ⛔ 闸门 2（最关键的那一条）：引擎自己说"有 N 个条目出错"，而我们只数出 M < N 个
             * ⇒ 有点不出名的坏东西（中文版 UnRAR 的报错就是中文，一行都认不出来）⇒ **不发**。
             * 宁可退回"什么都不发布"，也不能把一个"看起来大小对得上、其实内容坏了"的文件放出去。
             */
            if (engineReportedErrorCount > rejected.Count)
            {
                return Fail(
                    ReasonUnnamedEngineErrors,
                    $"引擎自报有 {engineReportedErrorCount} 个条目出错，但只点得出 {rejected.Count} 个的名字"
                    + "（本地化界面的引擎报错是本地语言）—— 点不出名的坏条目一律按「可能还在里面」处理，"
                    + "所以这一份一个字节都不发布");
            }

            if (publishable.Count == 0)
            {
                return Fail(ReasonNothingPublishable, "清单里的条目一个都没能完整解出来，没有可发布的内容");
            }

            /*
             * ⛔ 闸门 5（补 2026-10-02，专治**本地化引擎**那一档）：引擎说"这一趟失败了"，可我们在盘上
             * **一条缺口都对不上** —— 没缺条目、没大小不符、也没点名。那只剩一种解释：坏的那个东西
             * 在盘上"看着是对的"（CRC 坏、长度没变），而本地化 UnRAR 的报错既读不出名字也读不出计数，
             * 所以闸门 2 也拦不住它。这一档一律不发布 —— 判不出 ⇒ 什么都不做。
             */
            if (engineReportedFailure && rejected.Count == 0)
            {
                return Fail(
                    ReasonUnreconciledEngineFailure,
                    "引擎报告这一趟解压失败，但清单里的条目在盘上一条缺口都对不上"
                    + "（既没缺、也没截断、引擎又点不出名）—— 那个坏东西可能「看着是对的」，所以一个字节都不发布");
            }

            bool withinCount = rejected.Count <= MaxMissingEntries;
            bool withinRatio = expectedBytes > 0 && (double)publishableBytes / expectedBytes >= MinPublishableByteRatio;

            if (!withinCount && !withinRatio)
            {
                return Fail(
                    ReasonTooMuchMissing,
                    $"缺得太多（{rejected.Count} 个条目没解出来，只出了 {publishableBytes} / {expectedBytes} 字节，"
                    + $"不到 {MinPublishableByteRatio:P0}）—— 这一档不发布，先查密码或换一份完整副本");
            }

            return new PartialPublishVerdict
            {
                CanPublish = true,
                Entries = verdicts,
                Publishable = publishable,
                Rejected = rejected,
                ReasonCode = ReasonOk,
                Reason = $"可发布 {publishable.Count} 个文件 / {publishableBytes} 字节，"
                         + $"缺或坏 {rejected.Count} 个（清单共 {verdicts.Count} 个 / {expectedBytes} 字节）",
                PublishableBytes = publishableBytes,
                ExpectedBytes = expectedBytes
            };
        }

        /// <summary>
        /// 把引擎的清单结论折成 <see cref="Plan"/> 要的那份"逐条预期"（**纯函数**）。
        ///
        /// <para>两条口径：</para>
        /// <list type="number">
        /// <item><description>**目录条目不算**：清单里的目录解压后不产生文件，拿它去比盘上必然"缺"；</description></item>
        /// <item><description>**仅大小写不同的重复条目只留第一条**：Windows 上物理放不下两个同名文件
        /// （与 <c>OutputVerifier.CollapseCaseOnlyDuplicates</c> 同一精神，但那边只回两个计数，
        /// 这里要的是逐条清单，所以按同一个口径各算各的 —— 折算口径本身没有第二套定义）。</description></item>
        /// </list>
        ///
        /// <para>清单取不到（列不出来）时返回**空表**：`Plan` 会把空表判成"没有清单"⇒ 什么都不发布。</para>
        /// </summary>
        public static IReadOnlyList<(string Path, long Size)> ToManifestEntries(ArchiveListResult? list)
        {
            if (list == null || !list.Success || list.Entries == null || list.Entries.Count == 0)
            {
                return Array.Empty<(string, long)>();
            }

            var entries = new List<(string Path, long Size)>(list.Entries.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveEntry entry in list.Entries)
            {
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                string path = Normalize(entry.Path);

                if (path.Length == 0 || !seen.Add(path))
                {
                    continue;
                }

                entries.Add((path, entry.Size));
            }

            return entries;
        }

        /// <summary>路径归一（去掉开头的 <c>./</c>、把 <c>/</c> 统一成 <c>\</c>）：清单与盘上两侧的写法可能不同。</summary>
        private static string Normalize(string path)
        {
            string value = (path ?? string.Empty).Trim().Replace('/', '\\');

            while (value.StartsWith(@".\", StringComparison.Ordinal))
            {
                value = value[2..];
            }

            return value;
        }

        private static PartialPublishVerdict Fail(string reasonCode, string reason)
        {
            return new PartialPublishVerdict
            {
                CanPublish = false,
                ReasonCode = reasonCode,
                Reason = reason
            };
        }
    }
}
