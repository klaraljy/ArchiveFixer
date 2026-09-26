using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Models;

namespace ArchiveFixer.Storage
{
    /// <summary>自测里**一个文件**的结果（"哪个文件、通过/失败、为什么"）。</summary>
    public sealed class DangerModeSelfTestStep
    {
        /// <summary>用户看得懂的名字（文件名）。</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>任务路径（排障用）。</summary>
        public string TaskPath { get; init; } = string.Empty;

        /// <summary>终态是不是「解压成功」。</summary>
        public bool ExtractSucceeded { get; init; }

        /// <summary>输出校验是否通过（条目数 / 总大小）。</summary>
        public bool Verified { get; init; }

        /// <summary>其余物是不是被**彻底删除**了（危险模式的关键动作）。</summary>
        public bool RestPurged { get; init; }

        /// <summary>彻底删除实际释放的字节数。</summary>
        public long PurgedBytes { get; init; }

        /// <summary>这个任务开始前的可用空间（-1 = 取不到）。</summary>
        public long AvailableBeforeStart { get; init; } = -1;

        /// <summary>这个任务收尾后的可用空间（-1 = 取不到）。</summary>
        public long AvailableAfterFinish { get; init; } = -1;

        /// <summary>这个任务需要的峰值字节数（估算）。</summary>
        public long RequiredBytes { get; init; }

        /// <summary>
        /// 这个任务内容物的估算字节数（有清单时是真实解压后大小）。
        ///
        /// <para>它是空间曲线判据里的"允许净消耗"那一项：危险模式跑完之后，
        /// 净消耗应当约等于 <c>内容物 − 已回收</c>（源包与中间件被删掉了，所以那一份不该还算在消耗里）。</para>
        /// </summary>
        public long ContentBytes { get; init; }

        /// <summary>不通过时的原因（通过时为空）。</summary>
        public string FailureReason { get; init; } = string.Empty;

        /// <summary>一行结论（写进日志与界面）。</summary>
        public string Describe()
        {
            string verdict = string.IsNullOrWhiteSpace(FailureReason)
                ? (ExtractSucceeded && Verified && RestPurged ? "通过" : "未通过")
                : "未通过";

            string detail =
                $"解压{(ExtractSucceeded ? "成功" : "未成功")}"
                + $"，校验{(Verified ? "通过" : "未通过")}"
                + $"，其余物{(RestPurged ? $"已彻底删除（释放 {TaskSpaceEstimate.FormatSize(PurgedBytes)}）" : "未删除")}";

            if (AvailableBeforeStart >= 0 && AvailableAfterFinish >= 0)
            {
                long delta = AvailableAfterFinish - AvailableBeforeStart;
                detail += $"，可用空间变化 {(delta >= 0 ? "+" : "-")}{TaskSpaceEstimate.FormatSize(Math.Abs(delta))}";
            }

            return $"{DisplayName}：{verdict}（{detail}）"
                   + (string.IsNullOrWhiteSpace(FailureReason) ? string.Empty : $" —— {FailureReason}");
        }
    }

    /// <summary>自测的全部证据（由协调器在真跑一遍的过程中收集）。</summary>
    public sealed class DangerModeSelfTestEvidence
    {
        /// <summary>本次自测用的并发档。</summary>
        public int ParallelCount { get; init; } = 1;

        /// <summary>协议要求的样本数（= 2 × 并发数）。</summary>
        public int RequiredSampleSize { get; init; }

        /// <summary>逐个文件的结果。</summary>
        public IReadOnlyList<DangerModeSelfTestStep> Steps { get; init; } = Array.Empty<DangerModeSelfTestStep>();

        /// <summary>跑之前就被挡下的原因（非空时直接判不通过，<see cref="Steps"/> 为空）。</summary>
        public string BlockedReason { get; init; } = string.Empty;
    }

    /// <summary>自测结论。</summary>
    public sealed class DangerModeSelfTestVerdict
    {
        /// <summary>**只有它为 true 才允许开启危险模式。**</summary>
        public bool Passed { get; init; }

        /// <summary>一句话总结（进日志 + 弹给用户）。</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>逐个文件的结果行（"哪个文件、通过/失败、为什么"）。</summary>
        public IReadOnlyList<string> StepLines { get; init; } = Array.Empty<string>();

        /// <summary>不通过的原因清单（逐条说清）。</summary>
        public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

        /// <summary>本次自测的样本数 / 并发档（写进"自测通过"的凭证）。</summary>
        public int SampleSize { get; init; }

        /// <summary>本次自测用的并发档。</summary>
        public int ParallelCount { get; init; }
    }

    /// <summary>
    /// **危险模式的自测协议**（用户 2026-09-22 明确要求）。
    ///
    /// <para>用户原话：「要拿几个文件先测试一遍，比如并行解压是五个，你就要拿十个文件做这样的测试，
    /// 测试成功才能告知用户可以一试，但风险还是有的」。</para>
    ///
    /// <para><b>协议（四条判据，全部成立才算通过）</b>：</para>
    /// <list type="number">
    /// <item><description><b>样本量</b>：并发数 × 2（并发 5 → 10 个文件）。凑不够就**不测**，
    /// 绝不"拿 3 个文件假装测过了"。</description></item>
    /// <item><description><b>逐个产物校验通过</b>：每个文件终态必须是「解压成功」**且**输出校验通过
    /// （不变量 6：部分完成不算，取消不算）。</description></item>
    /// <item><description><b>其余物按预期被删</b>：每个文件的其余物都真的被彻底删除，并报出释放的字节数。</description></item>
    /// <item><description><b>空间曲线符合预期</b>：每个文件收尾后的可用空间 ≥ 它开始前的可用空间 − 它自己内容物的估算值。
    /// 这就是"净占用基本不变"的形式化说法：如果源包与中间件没有被删掉，可用空间会掉下大约一整个源包的体积，
    /// 这条判据立刻会红。取不到可用空间时**判不通过**（验不了就是验不了，不能默认通过）。</description></item>
    /// </list>
    ///
    /// <para>本类是纯判定（不跑流程、不碰磁盘）：跑流程由 <c>ExtractionCoordinator</c> 负责，
    /// 它只是把真跑一遍的证据喂进来。这样"通过与否"这件事可以被单测精确摆布 ——
    /// 包括"故意让某个文件校验不通过"这种真机上很难复现的现场。</para>
    /// </summary>
    public static class DangerModeSelfTestProtocol
    {
        /// <summary>样本量倍数：并发数 × 它。用户原话"并行解压是五个，你就要拿十个文件"。</summary>
        public const int SampleMultiplier = 2;

        /// <summary>
        /// 空间曲线的容差：日志、临时文件、文件系统簇对齐都会让"释放了多少"与"可用空间涨了多少"
        /// 对不上一点点。取 `max(16 MiB, 释放量的 10%)` —— 超过这个数就说明"边解边删"没起作用。
        /// </summary>
        public static long SpaceCurveSlackBytes(long purgedBytes)
        {
            const long floor = 16L * 1024 * 1024;
            long tenth = purgedBytes > 0 ? purgedBytes / 10 : 0L;

            return Math.Max(floor, tenth);
        }

        /// <summary>
        /// 协议要求的样本数（并发 1 → 2 个，并发 5 → 10 个）。
        ///
        /// <para>⚠ 这里**不做档位归一化**（<see cref="ExtractionScheduler.NormalizeParallelCount"/>
        /// 会把 5 归到 8）：用户原话就是"并行解压是五个，你就要拿十个文件"。
        /// 设置里的「最大并发解压数」允许 1~8 任意整数，所以 5 是真实可能出现的档位，
        /// 归到 8 会变成要 16 个文件 —— 那是把用户的规则改掉了。</para>
        /// </summary>
        public static int RequiredSampleSize(int parallelCount)
        {
            int parallel = Math.Clamp(parallelCount, 1, ExtractionScheduler.ParallelCeiling);

            return parallel * SampleMultiplier;
        }

        /// <summary>
        /// 挑样本：**从需求最小的开始**（"先拿小的试"是唯一能让人放心的顺序），
        /// 且必须真的可解 —— 格式未知、分卷不全的包会在自测里失败，但那是**别的原因**，
        /// 拿它们当样本只会让自测报出一个与危险模式无关的"不通过"。
        /// </summary>
        public static IReadOnlyList<ArchiveTask> SelectSamples(
            IEnumerable<ArchiveTask>? candidates,
            int requiredSampleSize)
        {
            var usable = new List<ArchiveTask>();

            foreach (ArchiveTask task in candidates ?? Enumerable.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    continue;
                }

                if (!task.IsArchive || string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (task.IsVolumeGroup && !task.IsVolumeComplete)
                {
                    continue;
                }

                usable.Add(task);
            }

            /*
             * 排序用文件大小（粗估源包体积）：自测**故意不列目录**——列目录要跑引擎、要试密码，
             * 在"还没决定要不要开这个模式"的阶段做这些事代价太高，而"挑小的先试"只需要一个大致体积。
             */
            return usable
                .OrderBy(EstimateSourceBytes)
                .ThenBy(task => task.Index)
                .Take(Math.Max(0, requiredSampleSize))
                .ToList();
        }

        /// <summary>
        /// 开测之前的**前置条件**：源包处理档必须是"会离开原位"的那两档。
        ///
        /// <para>理由：危险模式省下来的空间**全部**来自"源包与中间件被真的删掉"。
        /// <c>KeepInPlace</c> 档下源包一个字节都不动，其余物里只有中间件 ——
        /// 那种配置下开启这个模式只会白白承担"永久删除"的风险，却省不出最主要的那一份。</para>
        /// </summary>
        public static bool CheckPreconditions(AppSettings? settings, out string reason)
        {
            reason = string.Empty;

            if (settings == null)
            {
                return true;
            }

            SourceHandlingMode handling = AppSettings.ParseSourceHandling(settings.SourceHandling);

            if (handling == SourceHandlingMode.KeepInPlace)
            {
                reason =
                    "当前「源包处理」是「留在原地（不动源包）」，其余物里没有源包 —— "
                    + "危险模式省不出源包那一份空间，先把它改成「移入其余物」或「校验通过后删除」再自测。"
                    + "（改设置：设置 → 解压与整理 → 源包处理）";

                return false;
            }

            return true;
        }

        /// <summary>判定。</summary>
        public static DangerModeSelfTestVerdict Evaluate(DangerModeSelfTestEvidence? evidence)
        {
            if (evidence == null)
            {
                return Fail("没有自测证据", Array.Empty<string>(), 0, 0, "自测没有产生任何证据");
            }

            int sampleSize = evidence.RequiredSampleSize;
            int parallel = evidence.ParallelCount;

            var stepLines = evidence.Steps.Select(step => step.Describe()).ToList();

            if (!string.IsNullOrWhiteSpace(evidence.BlockedReason))
            {
                return Fail(evidence.BlockedReason, stepLines, evidence.Steps.Count, parallel, evidence.BlockedReason);
            }

            var reasons = new List<string>();

            if (evidence.Steps.Count < sampleSize)
            {
                reasons.Add(
                    $"协议要求跑 {sampleSize} 个文件（并发 {parallel} × {SampleMultiplier}），实际只跑了 {evidence.Steps.Count} 个");
            }

            foreach (DangerModeSelfTestStep step in evidence.Steps)
            {
                string name = string.IsNullOrWhiteSpace(step.DisplayName) ? step.TaskPath : step.DisplayName;

                if (!step.ExtractSucceeded)
                {
                    reasons.Add($"{name}：没有解压成功，不变量 6（部分完成不得显示为成功）在这一步就不成立");
                    continue;
                }

                if (!step.Verified)
                {
                    reasons.Add($"{name}：输出校验没有通过");
                    continue;
                }

                if (!step.RestPurged)
                {
                    reasons.Add($"{name}：其余物没有被彻底删除（危险模式的关键动作没生效）");
                    continue;
                }

                if (step.AvailableBeforeStart < 0 || step.AvailableAfterFinish < 0)
                {
                    reasons.Add($"{name}：取不到目标盘可用空间，空间曲线无法验证 —— 验不了就不算通过");
                    continue;
                }

                /*
                 * 空间曲线：**净消耗必须约等于"内容物 − 已回收"**。
                 *
                 * 这条怎么证明"边解边删"生效（推演一遍，别改错）：
                 * · 源包在任务开始前就已经在盘上了，所以它**不进**这次的前后差值；
                 * · 普通档：源包只是被搬进其余物（同盘移动不释放空间）→ 净消耗 ≈ 内容物（整份都留在盘上）；
                 * · 危险模式：源包与中间件被真的删掉 → 净消耗 ≈ 内容物 − 回收量 ≈ 0（"空间没有改变"）。
                 * 所以判据是 `净消耗 ≤ 内容物 − 回收量 + 容差`：没有真的回收时，右边会小掉一整个源包的量，
                 * 这条立刻会红。用峰值（源包 + 内容物 + 过程物）当右边是**错的** —— 那等于把源包也算成允许消耗，
                 * 无论删没删都能过。
                 */
                long allowedDrop = TaskSpaceEstimate.SaturatingSum(
                    step.ContentBytes > step.PurgedBytes ? step.ContentBytes - step.PurgedBytes : 0L,
                    SpaceCurveSlackBytes(step.PurgedBytes));

                long actualDrop = step.AvailableBeforeStart - step.AvailableAfterFinish;

                if (actualDrop > allowedDrop)
                {
                    reasons.Add(
                        $"{name}：空间曲线不符合预期 —— 开始前可用 {TaskSpaceEstimate.FormatSize(step.AvailableBeforeStart)}，"
                        + $"收尾后只剩 {TaskSpaceEstimate.FormatSize(step.AvailableAfterFinish)}，"
                        + $"净消耗 {TaskSpaceEstimate.FormatSize(actualDrop)}，超过允许的 {TaskSpaceEstimate.FormatSize(allowedDrop)}"
                        + "（说明源包 / 中间件并没有被真正回收）");
                }
            }

            if (reasons.Count > 0)
            {
                return Fail(
                    $"自测未通过：{reasons.Count} 个问题（共测 {evidence.Steps.Count} 个文件）",
                    stepLines,
                    evidence.Steps.Count,
                    parallel,
                    reasons.ToArray());
            }

            return new DangerModeSelfTestVerdict
            {
                Passed = true,
                Summary =
                    $"自测通过（并发 {parallel}，共测 {evidence.Steps.Count} 个文件）："
                    + "每个文件都解压成功 + 输出校验通过 + 其余物已彻底删除 + 空间曲线符合预期。"
                    + "可以一试，但风险还是有的：源包已被永久删除，无法还原。",
                StepLines = stepLines,
                FailureReasons = Array.Empty<string>(),
                SampleSize = evidence.Steps.Count,
                ParallelCount = parallel
            };
        }

        /// <summary>估一个任务的源包字节数（只 stat，不跑引擎；读不到按 0 计 —— 它只用于排序）。</summary>
        private static long EstimateSourceBytes(ArchiveTask task)
        {
            long total = 0;

            foreach (string path in new[] { task.CurrentPath }.Concat(task.VolumePaths))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    total = TaskSpaceEstimate.SaturatingSum(total, new System.IO.FileInfo(path).Length);
                }
                catch
                {
                    // 量不到就当 0：排序用的估计值，不值得为它抛异常打断整个自测。
                }
            }

            return total;
        }

        private static DangerModeSelfTestVerdict Fail(
            string summary,
            IReadOnlyList<string> stepLines,
            int sampleSize,
            int parallel,
            params string[] reasons)
        {
            return new DangerModeSelfTestVerdict
            {
                Passed = false,
                Summary = summary,
                StepLines = stepLines,
                FailureReasons = reasons,
                SampleSize = sampleSize,
                ParallelCount = parallel
            };
        }
    }

    /// <summary>
    /// "自测通过"的**凭证**（落盘在 <c>AppSettings.DangerModeSelfTestStamp</c>）。
    ///
    /// <para>为什么要有凭证，而不是"自测通过就当场打开"：设置是会被保存、程序是会被重启的。
    /// 没有凭证时，用户手改 <c>appsettings.json</c> 把开关改成 <c>true</c> 就能绕开整条协议
    /// （那份协议是**不可逆操作唯一的闸门**）。有了凭证，<c>AppSettings.Normalize</c> 就有一件事可判：
    /// 开关是 true 而凭证是空的 → 强制关掉。</para>
    ///
    /// <para>凭证里记下并发数与样本数（不只是"通过"两个字）：用户后来把并发从 2 调到 8 时，
    /// 日志与设置界面能如实说出"自测是在并发 2 下做的"，而不是给一个含糊的"已通过"。</para>
    /// </summary>
    public static class DangerModeSelfTestStamp
    {
        /// <summary>字段分隔符（凭证是一行文本，落盘可读、可人工核对）。</summary>
        private const char Separator = '|';

        /// <summary>凭证前缀（认凭证时先认它，免得把一句手写的话当成凭证）。</summary>
        public const string Prefix = "自测通过";

        /// <summary>按结论造一条凭证。</summary>
        public static string Create(DangerModeSelfTestVerdict verdict, DateTime timestamp)
        {
            int parallel = verdict?.ParallelCount ?? 0;
            int sample = verdict?.SampleSize ?? 0;

            return string.Join(
                Separator,
                Prefix,
                timestamp.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                $"并发={parallel}",
                $"样本={sample}");
        }

        /// <summary>这条凭证算不算数（空 / 手写乱码一律不算）。</summary>
        public static bool IsValid(string? stamp)
        {
            if (string.IsNullOrWhiteSpace(stamp))
            {
                return false;
            }

            return stamp.TrimStart().StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>给用户看的一句话；没有凭证时返回空串（调用方据此决定要不要提示"还没自测"）。</summary>
        public static string Describe(string? stamp)
        {
            return IsValid(stamp) ? stamp!.Trim() : string.Empty;
        }

        /// <summary>
        /// 这条凭证**盖不盖得住**某个并发档：凭证里记的并发数 ≥ 这一档。
        ///
        /// <para>自测是"拿并发数 × 2 个文件真跑一遍"，所以它的结论只对它跑过的那一档成立。
        /// 用户后来把并发从 4 调到 8 时，旧凭证不能替 8 背书 —— 那等于**没测过就用了**，
        /// 而这条路上的代价是不可逆的。所以这里判"盖不住"时调用方必须**本批不生效**（一个字节都不删），
        /// 并如实说明怎么恢复（调回档位 / 重新自测）。</para>
        ///
        /// <para>⚠ 单调方向是刻意的：**调低**并发（8 → 4）仍然算盖得住 ——
        /// 少并行只会更安全，逼用户为"调小"再删一批样本源包是没道理的。</para>
        /// </summary>
        public static bool Covers(string? stamp, int parallelCount)
        {
            if (!IsValid(stamp))
            {
                return false;
            }

            int tested = ParseParallelCount(stamp);
            int required = DangerModeSelfTestProtocol.RequiredSampleSize(parallelCount);

            // 认不出并发数（手写的凭证）→ 盖不住；样本数不足 2×档位 → 也盖不住。
            return tested >= parallelCount && ParseSampleSize(stamp) >= required;
        }

        /// <summary>盖不住时给一句"为什么 + 怎么恢复"；盖得住返回空串。</summary>
        public static string DescribeCoverage(string? stamp, int parallelCount)
        {
            if (Covers(stamp, parallelCount))
            {
                return string.Empty;
            }

            if (!IsValid(stamp))
            {
                return "还没有自测凭证，危险模式不会生效。";
            }

            return $"自测是在并发 {ParseParallelCount(stamp)}（{ParseSampleSize(stamp)} 个文件）下做的，"
                   + $"现在这一档是并发 {parallelCount} —— 凭证盖不住，危险模式本批不生效。"
                   + $"做法：把「最大并发解压数」调到不超过 {ParseParallelCount(stamp)}，或在新档位下重新自测。";
        }

        /// <summary>凭证里记下的并发数；认不出来返回 0。</summary>
        public static int ParseParallelCount(string? stamp)
        {
            return ParseField(stamp, "并发=");
        }

        /// <summary>凭证里记下的样本数；认不出来返回 0。</summary>
        public static int ParseSampleSize(string? stamp)
        {
            return ParseField(stamp, "样本=");
        }

        private static int ParseField(string? stamp, string key)
        {
            if (!IsValid(stamp))
            {
                return 0;
            }

            foreach (string part in stamp!.Split(Separator))
            {
                string trimmed = part.Trim();

                if (trimmed.StartsWith(key, StringComparison.Ordinal) &&
                    int.TryParse(trimmed.Substring(key.Length), out int value))
                {
                    return value;
                }
            }

            return 0;
        }
    }
}
