namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 「手上这几卷加起来够不够整包」的三档（方案《分卷族系统化识别》§4 阶段 B 的 (d)）。
    ///
    /// <para>⛔ <see cref="Unknown"/> 是**默认档**：读不出起始头就什么都不断言（⛔ 不拿"差不多"当结论）。</para>
    /// </summary>
    public enum SevenZipByteBudgetVerdict
    {
        /// <summary>读不出起始头（没有魔数 / 读不到 / 布局不符）⇒ 这一条证据今天用不上。</summary>
        Unknown,

        /// <summary>手上的字节数**少于**整包自述的字节数 ⇒ 缺卷，缺多少也说得出来。</summary>
        Short,

        /// <summary>**正好相等** ⇒ 字节数这一条硬证据成立（与顺序无关），可以当"整组齐了"。</summary>
        Exact,

        /// <summary>手上的字节数**多于**整包自述的字节数 ⇒ 候选池里混进了不该算的文件。</summary>
        Excess
    }
}
