namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一次 L4 裁决（三态 + 证据 + 一句给人看的话）。
    ///
    /// <para>它只描述结论，不做任何动作 —— 删源 / 搬源 / 保留的**判据**读它，
    /// 但动作本身仍归各自的唯一出口（<c>SourceCleanupService</c> / <c>RestItemPurger</c> /
    /// <c>源包移入其余物旧入口</c>）。</para>
    /// </summary>
    public sealed class ResultCompletenessVerdict
    {
        /// <summary>三态结论。</summary>
        public ResultCompleteness State { get; init; } = ResultCompleteness.Undeterminable;

        /// <summary>
        /// 证据的来源字段名（机器可判：<c>OutputVerification.Outcome</c> / <c>ManifestCrossChecked</c> /
        /// <c>MissingVolumeNames</c> 之类）—— 排障时要能一眼看出"这条结论是拿什么判的"。
        /// </summary>
        public string Evidence { get; init; } = string.Empty;

        /// <summary>给人看的一句话（进日志与报告；⛔ 不许拿它当判据）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// **为什么没删 / 没搬源包** —— 一句话说清"哪条判据 + 哪一层 + 什么原因"
        /// （用户 2026-09-30：日志不许再让人猜）。
        ///
        /// <para>它是**唯一一处**拼装这个说法的出口：所有读 L4 的删除闸门（其余物删除 / 源包搬运 /
        /// 空间不足模式删源）写日志时都读它，⛔ 不许各自再拼一遍措辞。可证完整时它是空串
        /// （那时本来就不该有"为什么没删"这句话）。</para>
        /// </summary>
        public string Blocker { get; init; } = string.Empty;

        /// <summary>可证完整 —— **唯一**允许删除 / 搬走源包的档。</summary>
        public bool AllowsSourceRemoval => State == ResultCompleteness.Complete;
    }
}
