namespace ArchiveFixer.Detection;

/// <summary>
/// 组键 —— 目录 + 基名（+ 必要时加族标记）的唯一拼法，桶键与组键共用这一处。
///
/// <para><b>为什么独立成层</b>：桶键（收集时按"目录 + 基名 + 族"分桶）与组键（输出给下游当字典键）
/// 必须是同一套拼法；两处各拼一份，迟早出现"归组键与桶键不是一个形状"，下游按组键找不回来。</para>
///
/// <para><b>什么时候加族标记</b>：同一目录同一基名下**并存两套命名**时（例如 <c>x.rar</c>+<c>x.r00</c>
/// 与 <c>x.part1.rar</c>+<c>x.part2.rar</c>，这是两个真实存在、互不兼容的分卷集），
/// 否则两组会撞同一个键。只有一套命名时不加，键保持 <c>目录|基名</c> 的老形状。</para>
///
/// <para><b>失败模型</b>：目录为空 ⇒ 只有基名；族判不出时由解析端落 <see cref="VolumeNameFamily.Numeric"/>
/// （见 <see cref="VolumeNameEvidence"/>），这里不另造兜底、不抛。</para>
///
/// <para>生命周期：纯静态、无状态；只做字符串拼接，Windows 文件名不含 <c>|</c>，不会歧义。</para>
/// </summary>
internal static class VolumeGroupKey
{
    /// <summary>收集时的桶键：目录 + 基名 + 族（族必须进键，否则两套命名会挤进同一个桶）。</summary>
    public static string BucketKey(string directoryPath, string baseName, VolumeNameFamily family) =>
        BaseKey(directoryPath, baseName) + "|" + FamilyToken(family);

    /// <summary>组键的前半段：目录 + 基名（目录为空时只有基名）。</summary>
    public static string BaseKey(string directoryPath, string baseName) =>
        string.IsNullOrEmpty(directoryPath) ? baseName : directoryPath + "|" + baseName;

    /// <summary>族标记（只在同基名下有多套命名时追加到组键末尾）。</summary>
    public static string FamilyToken(VolumeNameFamily family)
    {
        switch (family)
        {
            case VolumeNameFamily.Numeric:
                return "num";
            case VolumeNameFamily.ZipSpanned:
                return "z";
            case VolumeNameFamily.RarOld:
                return "r";
            default:
                return "part";
        }
    }

    /// <summary>输出给下游的组键：在同一基名（<paramref name="baseKey"/>）末尾追加族标记，保证键唯一。</summary>
    public static string GroupKeyWithFamily(string baseKey, VolumeNameFamily family) =>
        baseKey + "|" + FamilyToken(family);
}
