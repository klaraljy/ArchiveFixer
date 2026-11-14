namespace ArchiveFixer.Extraction
{
    /// <summary>目标卷的物理介质类型（只用于并发初值；判不出按 HDD 保守）。</summary>
    public enum StorageDeviceKind
    {
        Unknown = 0,
        Hdd = 1,
        Ssd = 2
    }
}
