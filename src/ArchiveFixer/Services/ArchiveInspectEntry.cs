namespace ArchiveFixer.Services
{
    /// <summary>「看内容」里的一条（只给名字 / 大小 / 是不是目录 —— 不读内容）。</summary>
    public sealed record ArchiveInspectEntry(string Name, long Size, bool IsDirectory);
}
