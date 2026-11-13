namespace ArchiveFixer.Services
{
    /// <summary>
    /// 同名冲突的目标类型：文件还是目录。
    ///
    /// 为什么要分开：覆盖一个**文件**是"挪开 → 落位 → 删旧的"；覆盖一棵**目录树**是同一套动作，
    /// 但代价与风险差一个量级（整棵树进回收站都没有的"临时名"）。类型也对不上时（文件撞目录）
    /// 一律不覆盖 —— 删掉一棵目录树只为给一个文件让位，收益与风险不成比例。
    /// </summary>
    public enum ConflictTargetKind
    {
        File = 0,
        Directory = 1
    }
}

