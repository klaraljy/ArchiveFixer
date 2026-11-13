namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 输出的落点模式（规格 <c>docs/输出与整理模型.md</c> §1.1）。**只剩两档**。
    ///
    /// <para>
    /// <b>用户 2026-09-24 第 13 条亲自拍板删掉两档</b>（原话："我们之前不是还有解压到当前目录吗，
    /// 现在那条删掉了，因为我怕如果 222 里面有很多的子文件夹，我们 111 里面就彻底乱掉了"）：
    /// </para>
    /// <list type="bullet">
    /// <item><description>删除 <c>SourceDirectoryFlat</c>（解压到压缩包所在目录 / 摊平到当前目录）→ 迁移到
    /// <see cref="PerArchiveSubfolder"/>；</description></item>
    /// <item><description>删除 <c>CustomRootFlat</c>（直接解到指定目录、不建子文件夹）→ 迁移到
    /// <see cref="CustomRootPerArchive"/>。</description></item>
    /// </list>
    ///
    /// <para>
    /// 剩下的两档 + "选中对象是文件还是文件夹"（<see cref="SourceSelectionKind"/>）一起给出用户要的四条规则：
    /// </para>
    /// <list type="table">
    /// <item><description>文件 <c>111\222.rar</c> + 未指定 → <c>111\222\</c>；</description></item>
    /// <item><description>文件夹 <c>111\222\</c> + 未指定 → 就在 <c>222\</c> 里面，不新建同名层、不往外扔；</description></item>
    /// <item><description>文件 <c>222.rar</c> + 指定 <c>BBB</c> → <c>BBB\222\</c>（名字 = 去掉后缀的包名）；</description></item>
    /// <item><description>文件夹 <c>222\</c> + 指定 <c>BBB</c> → <c>BBB\222\</c>（名字 = 选中的文件夹名）。</description></item>
    /// </list>
    ///
    /// <para>
    /// ⚠ 枚举值**刻意保留 0 与 2**（不重新编号成 0/1）：旧版本、旧配置、序列化过的整数里
    /// 1 = 摊平到当前目录、3 = 直接解到指定目录 —— 编号一变，那些值会**静默**对应到别的落点。
    /// 迁移由 <see cref="OutputPlacement.NormalizeLegacyMode"/> 负责。
    /// </para>
    /// </summary>
    public enum OutputPlacementMode
    {
        /// <summary>同名子文件夹（默认，未指定位置那一档）：<c>111\222.rar</c> → <c>111\222\内容物</c>。</summary>
        PerArchiveSubfolder = 0,

        /// <summary>指定位置 + 同名子文件夹：<c>&lt;指定位置&gt;\222\内容物</c>。</summary>
        CustomRootPerArchive = 2
    }
}
