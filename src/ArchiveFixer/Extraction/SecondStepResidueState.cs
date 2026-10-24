namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 第二大步残留区的总开关（用户 2026-10-24 拍板：第一大步只登记信息、不做任何分卷操作）。
    ///
    /// <para>残留区里的执行体（接片 / 收拢 / 收片 / 组分析 / 点名 / 缺卷补判 / 搬片）都先问这个开关：
    /// 为 true（当前）⇒ 一律**什么都不做**。重建第二大步时，把要接回来的那一块改成 false
    /// （或改成读配置/任务位），再逐块验收。</para>
    ///
    /// <para>⛔ 这只是过渡期的停用开关；第二大步重建完、残留目录删掉时，本文件一起删。</para>
    /// </summary>
    public static class SecondStepResidueState
    {
        /// <summary>第二大步停摆中：残留区里所有执行体都必须直接返回。</summary>
        public static bool Parked { get; set; } = true;
    }
}
