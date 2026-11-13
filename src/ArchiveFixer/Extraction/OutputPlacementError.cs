namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 落点解析的失败原因。**机器可判**（不要去比中文文案）。
    /// </summary>
    public enum OutputPlacementError
    {
        None = 0,

        /// <summary>源包路径为空。</summary>
        EmptySourcePath,

        /// <summary>源包路径里没有目录部分（例如只给了个文件名），推不出"当前目录"。</summary>
        SourceDirectoryUnavailable,

        /// <summary>自定义根为空。</summary>
        EmptyCustomRoot,

        /// <summary>自定义根是相对路径（<c>out\sub</c>）—— 会落到进程当前目录，用户完全没指定过那里。</summary>
        CustomRootNotAbsolute,

        /// <summary>自定义根就是盘符根本身（<c>D:\</c>）—— 等于把内容物直接摊在盘根上。</summary>
        CustomRootIsRootDirectory,

        /// <summary>自定义根的盘符不存在（U 盘拔了 / 盘符写错）。</summary>
        CustomRootDriveMissing,

        /// <summary>路径拼接失败（名字里带 Path.Combine 处理不了的字符）。</summary>
        InvalidDestinationPath
    }
}
