namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 7z 起始头自述的"整包应当有多少字节"与"手上这几份加起来有多少字节"的一次比对读数。
    ///
    /// <para>它只描述事实，**不含任何动作**：消费方最多拿它决定"值不值得试开一次 / 怎么排序 / 这句话怎么说"。</para>
    /// </summary>
    public readonly struct SevenZipByteBudget
    {
        /// <summary>读出了起始头（<see cref="ExpectedBytes"/> 可信）。</summary>
        public bool Known { get; init; }

        /// <summary>整包自述的字节数 = <c>32 + NextHeaderOffset + NextHeaderSize</c>。</summary>
        public long ExpectedBytes { get; init; }

        /// <summary>手上这几份加起来的字节数。</summary>
        public long ActualBytes { get; init; }

        /// <summary>三档结论。</summary>
        public SevenZipByteBudgetVerdict Verdict { get; init; }

        /// <summary>差的字节数（<see cref="SevenZipByteBudgetVerdict.Short"/> = 缺多少、<see cref="SevenZipByteBudgetVerdict.Excess"/> = 多多少；其余档为 0）。</summary>
        public long DifferenceBytes { get; init; }

        /// <summary>给人看的一句话（判不出时为空 —— 没有可说的结构化事实）。</summary>
        public string Describe()
        {
            if (!Known)
            {
                return string.Empty;
            }

            return Verdict switch
            {
                SevenZipByteBudgetVerdict.Short =>
                    $"7z 起始头自述这一包应当有 {ExpectedBytes} 字节，手上这几卷加起来只有 {ActualBytes} 字节 —— 还差 {DifferenceBytes} 字节",
                SevenZipByteBudgetVerdict.Exact =>
                    $"7z 起始头自述这一包应当有 {ExpectedBytes} 字节，手上这几卷加起来正好 {ActualBytes} 字节（字节数这一条证据成立）",
                SevenZipByteBudgetVerdict.Excess =>
                    $"7z 起始头自述这一包应当有 {ExpectedBytes} 字节，手上这几卷加起来却有 {ActualBytes} 字节 —— 多出 {DifferenceBytes} 字节",
                _ => string.Empty
            };
        }
    }
}
