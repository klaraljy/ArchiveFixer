namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 交接证据强度（用户 2026-10-24 口径）：内容可证 / 名字槽位 / 判不出。
    /// ⛔ 这只是**登记理由**，不是"它一定是分卷"的结论。
    /// </summary>
    public enum VolumeHandoffEvidence
    {
        Unknown = 0,
        ContentProven = 1,
        NameSlot = 2
    }
}