using System;
using System.Collections.Generic;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 解压产物校验结果（M3「一批包一键搞定」）。
    /// </summary>
    public sealed class OutputVerificationResult
    {
        public bool Verified { get; init; }

        /// <summary>
        /// 这次校验到底有没有拿到可判的事实（区别于“没校验过”）。判不出时下游必须按不可发布/不可删处理。
        /// </summary>
        public bool Known { get; init; } = true;

        /// <summary>
        /// 删源/不可逆动作的唯一放行位：只有“校验事实已知 + 底线校验通过 + 清单逐条核对过”才为 true。
        /// ⛔ 空清单/引擎没给清单时 <see cref="ManifestCrossChecked"/> 为 false，这里必须为 false。
        /// </summary>
        public bool AllowsSourceRemoval => Known && Verified && ManifestCrossChecked;

        /// <summary>机器可判的结论（<see cref="Verified"/> 的枚举形态；二者恒一致）。</summary>
        public OutputVerificationOutcome Outcome =>
            Verified ? OutputVerificationOutcome.Passed : OutputVerificationOutcome.Failed;

        /// <summary>
        /// 用户可读的失败结论（**只在判否时用**）：一句话说清"产物校验未通过 + 预期与实际"。
        /// 任务状态与失败清单都写它 —— 用户 2026-09-24 要求错误信息里必须带这三个数字。
        /// </summary>
        public string FailureMessage =>
            Message.StartsWith("校验未通过", StringComparison.Ordinal)
                ? "产物" + Message
                : "产物校验未通过：" + Message;

        public int ExpectedFileCount { get; init; }

        public int ActualFileCount { get; init; }

        public long ExpectedTotalSize { get; init; }

        public long ActualTotalSize { get; init; }

        /// <summary>
        /// 归档里**仅大小写不同**的同名条目组数（2026-09-27 真机：`rar-android-722.132.apk`）。
        ///
        /// <para>Windows 的盘不区分大小写，`res\9N.9.png` 与 `res\9n.9.png` 物理上只能是**同一个文件**，
        /// 所以这类条目**注定落不全** —— 预期数必须先按"仅大小写去重"折算，否则每一个这种包都会被判成
        /// "产物不完整"（真机后果：把 10 个密码候选白试一遍，最后还报「密码错误」）。</para>
        /// </summary>
        public int CaseOnlyDuplicateGroups { get; init; }

        /// <summary>被大小写折叠**吃掉**的条目数（预期折算时扣掉的份数）。</summary>
        public int CaseOnlyDroppedEntries { get; init; }

        /// <summary>
        /// **只缺 0 字节条目**：逐条核对时缺的条目全是 0 字节，且产物总字节不少于清单。
        /// 这一档内容字节可发布，但完整性**不升到 Complete**（不能删源包）。
        /// </summary>
        public bool OnlyZeroByteEntriesMissing { get; init; }

        /// <summary>逐条核对时缺的条目（路径 + 清单字节数；没有逐条清单时为空）。</summary>
        public IReadOnlyList<ArchiveEntry> MissingEntries { get; init; } = Array.Empty<ArchiveEntry>();

        /// <summary>
        /// 这一次校验**到底有没有拿可信清单逐条核对过**（L4 的判据，用户 2026-09-30 的检验等级）。
        ///
        /// <para>为什么必须有这个字段：<see cref="Verified"/> 为 true 有**两种完全不同的含义** ——
        /// ① "拿清单核对过了，产物对得上"；② "没拿到清单，只做了非空 + 非全 0 字节的底线校验"。
        /// 老口径下两者在数据上长得一模一样，于是"删源包"这道不可逆的闸门分不开它们
        /// （见 <see cref="ResultCompletenessClassifier"/>）。这里把事实如实记下来，判定交给那一个出口。</para>
        /// </summary>
        public bool ManifestCrossChecked { get; init; }

        /// <summary>给人看的一句话（直接进汇总报告与日志，不要在这里拼密码等敏感内容）。</summary>
        public string Message { get; init; } = string.Empty;
    }
}
