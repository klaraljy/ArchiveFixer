namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 终端落法（规格 §3.1）：内容物最里面那一层文件夹叫什么。
    ///
    /// <para>
    /// <see cref="KeepLastFolder"/> 保留归档内部最后一层文件夹名（<c>111\222\666\…</c>）；
    /// <see cref="UseArchiveName"/> 改用归档基名（<c>111\222\…</c>，"少一层点击"）。
    /// </para>
    /// <para>
    /// ⚠️ <b>2026-09-27 起它不再是一项用户设置</b>：用户把界面上那一档整个删掉了，
    /// 生产路径一律传 <see cref="KeepLastFolder"/>（"保留包内那层文件夹 + 外面套一层包名文件夹"）。
    /// 枚举留着是因为它是 <see cref="ResultFinalizer.Plan"/> 的**既有契约参数**
    /// （其余物布局等路径也读它），删掉只会白动一大片与本次需求无关的代码 ——
    /// ⛔ 但**不许**再把它接回设置或界面（那就是"改了没用的开关"）。
    /// </para>
    /// <para>
    /// ⚠️ 本选项只管**套出来的那一层文件夹叫什么**。判定表情形 1（终端就是单个文件）本来就没有
    /// 文件夹层，两种取值结果相同 —— 见 <see cref="ResultFinalizer"/>。
    /// </para>
    /// </summary>
    public enum TerminalLayoutMode
    {
        /// <summary>保留归档内最后一层文件夹名（生产路径固定用这一档；更保守，不丢信息）。</summary>
        KeepLastFolder = 0,

        /// <summary>用归档基名当最后那一层文件夹名（内部契约仍有这一支，生产路径不会传）。</summary>
        UseArchiveName = 1
    }
}
