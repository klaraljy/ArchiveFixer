namespace ArchiveFixer.Security
{
    /// <summary>
    /// 归档条目名的风险类别。
    ///
    /// ⚠ 这里的声明顺序**不是**危险程度顺序（枚举值一旦重排就会影响比较与持久化），
    /// 真正的优先级只写在 <see cref="ArchivePathGuard"/> 的 Severity 里，一处定义、一处可查。
    /// </summary>
    public enum PathRiskKind
    {
        /// <summary>没有发现问题。</summary>
        None,

        /// <summary>含 <c>..</c> 片段，试图跳出目标目录（Zip Slip 的经典形式）。</summary>
        ParentTraversal,

        /// <summary>绝对路径（<c>/etc/passwd</c> 或 <c>C:\Windows\x</c>）。</summary>
        AbsolutePath,

        /// <summary>盘符相对路径（<c>C:foo</c>，盘符后面没有斜杠），落点取决于该盘的"当前目录"。</summary>
        DriveRelative,

        /// <summary>网络共享路径（<c>\\server\share</c>）。</summary>
        UncPath,

        /// <summary>扩展 / 设备前缀（<c>\\?\</c> 或 <c>\\.\</c>），会绕过常规路径解析。</summary>
        ExtendedPrefix,

        /// <summary>Windows 保留设备名（<c>CON</c> / <c>NUL</c> / <c>COM1</c> …，含带扩展名的形式）。</summary>
        DeviceName,

        /// <summary>名称以点或空格结尾（<c>name.</c> / <c>name␠</c>），Windows 会静默改名，实际落点与预期不符。</summary>
        TrailingDotOrSpace,

        /// <summary>名称含冒号，可能是 NTFS 备用数据流（内容会写到主文件流之外）。</summary>
        AlternateDataStream,

        /// <summary>路径里含不可见控制字符。</summary>
        ControlCharacter,

        /// <summary>条目路径为空。</summary>
        EmptyEntry
    }
}
