namespace ArchiveFixer.Detection
{
    /// <summary>文件头能直接回答的那几种格式（只读前几个字节，不解析归档）。</summary>
    public enum VolumeContentFormat
    {
        Unknown,

        /// <summary>7z：魔数 <c>37 7A BC AF 27 1C</c>（**只有第一卷**有；后续卷是裸字节流）。</summary>
        SevenZip,

        /// <summary>RAR：<c>Rar!\x1A\x07\x00</c>（RAR4）/ <c>Rar!\x1A\x07\x01\x00</c>（RAR5）。</summary>
        Rar,

        /// <summary>ZIP 家族本地头 <c>PK\x03\x04</c>（空归档是 <c>PK\x05\x06</c>）。</summary>
        Zip
    }
}
