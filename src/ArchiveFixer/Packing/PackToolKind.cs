namespace ArchiveFixer.Packing
{
    /// <summary>打包用到的两个外部工具。</summary>
    public enum PackToolKind
    {
        /// <summary>
        /// 内置的 <c>tools\7zip\7z.exe</c>：建加密分卷、建**7z 外层容器**、以及列出 7z 外层容器的条目。
        /// </summary>
        SevenZip = 0,

        /// <summary>本机已装的 <c>Rar.exe</c>（或退一档的 <c>WinRAR.exe</c>）—— **绝不分发**。</summary>
        Rar = 1
    }
}
