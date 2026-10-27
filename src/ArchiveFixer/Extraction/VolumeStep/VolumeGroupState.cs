namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **一组分卷的生命周期状态**（第二大步唯一写者 = <see cref="VolumeGroupService"/>）。
    /// ⛔ 交界处不许写这个状态；判不出 = <see cref="Unknown"/>，一个字节都不动。
    /// </summary>
    public enum VolumeGroupState
    {
        /// <summary>已有候选，但族/完整性还没定（不动作）。</summary>
        Candidate = 0,

        /// <summary>可证完整：族、槽位、片数都齐了。</summary>
        Complete = 1,

        /// <summary>明显缺片（能点名的才点名）。</summary>
        Missing = 2,

        /// <summary>判不出（证据不足/冲突）。</summary>
        Unknown = 3,

        /// <summary>已写死唯一 owner 任务。</summary>
        OwnerAssigned = 4,

        /// <summary>已按规范名计划建好链接。</summary>
        Linked = 5,

        /// <summary>正在解这一层。</summary>
        Extracting = 6,

        /// <summary>解开且校验通过。</summary>
        Succeeded = 7,

        /// <summary>没解开（密码错/缺卷/失败）。</summary>
        Failed = 8,

        /// <summary>
        /// **分卷结构异常**（数量/槽位/大小/混族矛盾）：任务列表可见，但不处理、不删源、不解压。
        /// ⛔ 不是失败；批末不算红。
        /// </summary>
        SizeAnomaly = 9
    }
}
