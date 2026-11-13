namespace ArchiveFixer.Detection
{
    /// <summary>
    /// "这一卷的内容告诉了我们什么"这一档的机器结论（**面向用户的中文不在这里** —— 文案统一由
    /// <c>VolumeNameRepair</c> 从 <c>StatusText</c> 取，见 AGENTS.md §7）。
    /// </summary>
    public enum VolumeNumberFail
    {
        /// <summary>内容认出来了，卷号也定了。</summary>
        None,

        /// <summary>魔数既不是 RAR 也不是跨盘 zip（7z 那条路走 <see cref="VolumeContentInference"/>）。</summary>
        NotAnArchive,

        /// <summary>认得出是归档，但内容说它是**单卷**（不是分卷组的一员）。</summary>
        NotVolumeMember,

        /// <summary>头被截断 / CRC 对不上 / 归档头被加密 —— 读不出卷号。</summary>
        HeadersUnreadable,

        /// <summary>RAR 1.5–4.x（RAR4）的老式编号族（<c>.rar</c>/<c>.r00</c>）：本程序只认 <c>partN.rar</c> 这一族，不猜。</summary>
        RarOldNumbering,

        /// <summary>RAR 1.5–4.x（RAR4）的"这是第 1 卷"标记与推出来的卷号对不上。</summary>
        RarFirstVolumeMismatch,

        /// <summary>同目录里凑不齐一组（认出来的成员少于 2 卷）。</summary>
        GroupIncomplete,

        /// <summary>整组卷号连不成 1..N（缺卷，或有一卷被改坏后认不出来了）。</summary>
        GroupNotContiguous,

        /// <summary>RAR 1.5–4.x（RAR4）卷号的基数（0 起还是 1 起）两种解释都成立 / 都不成立 —— 不猜。</summary>
        GroupBaseAmbiguous,

        /// <summary>EOCD 说这是**单盘** zip（盘号 0）—— 不是跨盘组。</summary>
        ZipSingleDisk,

        /// <summary>跨盘 zip 的片数 ≥ 3：除末片外内容里没有盘号，中间几片的先后定不下来。</summary>
        ZipTooManyDisks,

        /// <summary>末片说总共 N 片，但同目录里能认出来的片数对不上。</summary>
        ZipPartsMissing,

        /// <summary>当前这一卷不是这一组的第 1 卷（RAR 的入口就是第 1 卷，改它没用）。</summary>
        CurrentNotFirstVolume,

        /// <summary>当前这一卷根本不在这一组里（同目录里还有别的组）。</summary>
        CurrentNotInGroup
    }
}

