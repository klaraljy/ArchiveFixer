namespace ArchiveFixer.Extraction
{
    /// <summary>逐层回收那一档的触发来源（从协调器嵌套类型提到 Extraction 顶层）。</summary>

    /// <summary>
    /// **这一层当场回收**的两个触发点（判据与执行体完全相同，只有措辞与"谁会走到这里"不同）。
    ///
    /// <para>⛔ 抽出来是为了让"同一件事只有一个出口"（AGENTS.md §9.5）：两个触发点共用
    /// <see cref="PurgeLayerSourcePackage"/> 那一份门槛与执行体，⛔ 不许各写一遍。</para>
    /// </summary>
    internal enum LayerPurgeTrigger
    {
        /// <summary>①页「空间不足」模式：每一层（含最外层）跑完就删**它自己那一层的源包**。</summary>
        SpaceTight,

        /// <summary>
        /// 普通档的「逐层回收」（用户 2026-10-03 拍板；2026-10-15 与③页档位解绑）：
        /// 续解层 / 递归层跑完就删掉**上一层交给它的那个内层包**（成功链的过程物一律当场永久删）；
        /// 最外层源包留到链尾按「源包操作」那两档处理。
        /// </summary>
        LayerReclaim,

        /// <summary>
        /// ③页「源包操作 = 彻底删除」（用户 2026-10-14：源包就两个选项，留下和彻底删除）：
        /// 成功（定稿 + 校验通过 + 未取消 + 可证完整）之后**就地删掉整组源包**。
        /// ⛔ 不搬进其余物 —— 执行体就是下面那个「当场原地删」的那一支。
        /// </summary>
        SourceHandlingDelete
    }
}
