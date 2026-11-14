namespace ArchiveFixer.Detection;

/// <summary>
/// 改名时用的**名字族**（用户 2026-09-29 要求：卷名拼法只留**一个**出口）。
///
/// <para>⛔ 任何地方要拼"这一组卷该叫什么"，都必须走
/// <see cref="VolumeNumberFromContent.BuildStandardNames"/>，不许在别处再写一套 ——
/// 计划里说的名字与真正改成的名字不是同一个，就会出现"按它说的改完还是解不开"。</para>
/// </summary>
public enum VolumeNamingFamily
{
    /// <summary>7z 的数字族：<c>基名.7z.001</c> / <c>.002</c>……（与 <c>VolumeGroupDetector</c> 的数字族口径一致）。</summary>
    SevenZipNumbered,

    /// <summary>RAR 族：<c>基名.part1.rar</c> / <c>part2.rar</c>……（RAR5 与 RAR 1.5–4.x/RAR4 的新编号都认这一族）。</summary>
    RarPart,

    /// <summary>真·跨盘 zip 族：<c>基名.z01</c>……<c>基名.z99</c> + 末片 <c>基名.zip</c>（PKZIP 跨盘族的拼法）。</summary>
    ZipSpanned,

    /// <summary>
    /// 通用分片族（**对归档再分片**）：<c>基名.zip.001</c> / <c>基名.zip.002</c>……
    /// （7-Zip `-tzip -v` 切出来的形状；设计文档 §6.6 A / §9 工单 #10）。
    /// ⛔ 与 <see cref="ZipSpanned"/> 是**两族**：那一族的末片叫 <c>.zip</c>、靠 EOCD 盘号认；
    /// 这一族没有盘号，靠"末片尾部有 EOCD"+"序号连续"认。
    /// </summary>
    ZipNumbered
}

