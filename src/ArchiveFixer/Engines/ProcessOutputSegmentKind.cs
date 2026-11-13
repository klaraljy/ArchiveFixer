namespace ArchiveFixer.Engines
{
    /// <summary>输出片段的种类。</summary>
    public enum ProcessOutputSegmentKind
    {
        /// <summary>一整行：以 <c>\n</c>（含 <c>\r\n</c>）结束。空行也是行（<c>-slt</c> 靠空行分隔条目）。</summary>
        Line = 0,

        /// <summary>
        /// 行内片段：以单独一个 <c>\r</c> 或退格 <c>\b</c> 结束 —— 引擎"就地重写"的进度用的就是它。
        /// </summary>
        Fragment = 1
    }
}
