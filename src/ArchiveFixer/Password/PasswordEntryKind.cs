namespace ArchiveFixer.Password
{
    /// <summary>
    /// 密码本条目的类型。
    /// </summary>
    public enum PasswordEntryKind
    {
        /// <summary>列表式：一行一个密码，没有"名称"这一说。</summary>
        List,

        /// <summary>映射式：<c>名称:密码</c>，可以用归档名去匹配"名称"。</summary>
        Mapped,

        /// <summary>显式的空密码行（文件里写了"空"/"空密码"这类标记）。</summary>
        Empty
    }
}
