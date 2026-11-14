using System.Collections.Generic;

namespace ArchiveFixer.Helpers;

/// <summary>
/// 文件后缀相关工具 —— **公开门面**（只做薄转发，判据本体在协作类里）。
///
/// <para><b>拆分后的协作类（依赖单向，⛔ 不许反向引用，也不许在此复制第二份判据）</b>：</para>
/// <list type="bullet">
/// <item><description>后缀词表与解析（取/加/删/替换后缀、建议后缀、可疑判定）=
/// <see cref="ExtensionNameRules"/>；</description></item>
/// <item><description>卷标记的基础尺子（什么算合法卷标记、什么算塞进去的垃圾）=
/// <see cref="VolumeMarkerRules"/>；</description></item>
/// <item><description>通用骨架命中与归档后缀去杂质还原 = <see cref="ExtensionSkeletonMatcher"/>；</description></item>
/// <item><description>卷标记骨架（<c>z01</c> / <c>r00</c> / <c>001</c> / <c>partN</c> / 拉长卷号）=
/// <see cref="VolumeMarkerSkeletonMatcher"/>；</description></item>
/// <item><description>卷段拆分（整段标记 / 容错 / 未知档）= <see cref="VolumeSegmentSplitter"/>；</description></item>
/// <item><description>卷名重建（名字前缀/名字干、族后缀段归一、partN 族解析）=
/// <see cref="VolumeNameRebuilder"/>；</description></item>
/// <item><description>"这一格是靠猜才读出来的"三个谓词 = <see cref="VolumeNameRebuildGate"/>。</description></item>
/// </list>
///
/// <para>⚠ 后缀词表是**纯字符串**层，卷标记/骨架/卷名重建是**分发与还原判据**层：
/// 两层原先挤在同一个 2352 行文件里却互不依赖，所以按职责拆开，依赖方向
/// 后缀词表 → 卷标记尺子 → 骨架 → 卷段拆分 → 卷名重建/闸门。</para>
/// </summary>
public static class ExtensionHelper
{
    /// <summary>常见压缩包后缀（词表唯一一份在 <see cref="ExtensionNameRules"/>，⛔ 别在此处再抄一张）。</summary>
    public static readonly HashSet<string> KnownArchiveExtensions = ExtensionNameRules.KnownArchiveExtensions;

    /// <summary>常见伪装后缀（词表唯一一份在 <see cref="ExtensionNameRules"/>）。</summary>
    public static readonly HashSet<string> SuspiciousFakeExtensions = ExtensionNameRules.SuspiciousFakeExtensions;

    /// <summary>"本身就是 ZIP 容器"的合法扩展名（词表唯一一份在 <see cref="ExtensionNameRules"/>）。</summary>
    public static readonly HashSet<string> ZipContainerExtensions = ExtensionNameRules.ZipContainerExtensions;

    /// <summary>是不是"本身就是 ZIP 容器"的合法扩展名（后缀不该被改）。判据在 <see cref="ExtensionNameRules.IsZipContainerExtension"/>。</summary>
    public static bool IsZipContainerExtension(string? extension)
        => ExtensionNameRules.IsZipContainerExtension(extension);

    /// <summary>是不是常见归档后缀。判据在 <see cref="ExtensionNameRules.IsKnownArchiveExtension"/>。</summary>
    public static bool IsKnownArchiveExtension(string? extension)
        => ExtensionNameRules.IsKnownArchiveExtension(extension);

    /// <summary>是不是常见伪装后缀。判据在 <see cref="ExtensionNameRules.IsSuspiciousFakeExtension"/>。</summary>
    public static bool IsSuspiciousFakeExtension(string? extension)
        => ExtensionNameRules.IsSuspiciousFakeExtension(extension);

    /// <summary>文件名是否无后缀。判据在 <see cref="ExtensionNameRules.HasNoExtension"/>。</summary>
    public static bool HasNoExtension(string? fileNameOrPath)
        => ExtensionNameRules.HasNoExtension(fileNameOrPath);

    /// <summary>取最后一个后缀（无后缀 ⇒ 空串）。判据在 <see cref="ExtensionNameRules.GetLastExtension"/>。</summary>
    public static string GetLastExtension(string? fileNameOrPath)
        => ExtensionNameRules.GetLastExtension(fileNameOrPath);

    /// <summary>取最后一个后缀的显示文本（无后缀 ⇒ "无"）。判据在 <see cref="ExtensionNameRules.GetLastExtensionDisplay"/>。</summary>
    public static string GetLastExtensionDisplay(string? fileNameOrPath)
        => ExtensionNameRules.GetLastExtensionDisplay(fileNameOrPath);

    /// <summary>取文件名里所有后缀段（<c>test.rar.pdf.jpg</c> ⇒ <c>.rar</c> <c>.pdf</c> <c>.jpg</c>）。
    /// 判据在 <see cref="ExtensionNameRules.GetAllExtensions"/>。</summary>
    public static List<string> GetAllExtensions(string? fileNameOrPath)
        => ExtensionNameRules.GetAllExtensions(fileNameOrPath);

    /// <summary>是不是多个后缀。判据在 <see cref="ExtensionNameRules.HasMultipleExtensions"/>。</summary>
    public static bool HasMultipleExtensions(string? fileNameOrPath)
        => ExtensionNameRules.HasMultipleExtensions(fileNameOrPath);

    /// <summary>文件名里是否含指定后缀段。判据在 <see cref="ExtensionNameRules.ContainsExtension"/>。</summary>
    public static bool ContainsExtension(string? fileNameOrPath, string? extension)
        => ExtensionNameRules.ContainsExtension(fileNameOrPath, extension);

    /// <summary>标准化后缀（<c>7z</c> ⇒ <c>.7z</c>）。判据在 <see cref="ExtensionNameRules.NormalizeExtension"/>。</summary>
    public static string NormalizeExtension(string? extension)
        => ExtensionNameRules.NormalizeExtension(extension);

    /// <summary>替换最后一个后缀（无后缀 ⇒ 添加）。判据在 <see cref="ExtensionNameRules.ReplaceLastExtension"/>。</summary>
    public static string ReplaceLastExtension(string filePath, string targetExtension)
        => ExtensionNameRules.ReplaceLastExtension(filePath, targetExtension);

    /// <summary>添加后缀。判据在 <see cref="ExtensionNameRules.AddExtension"/>。</summary>
    public static string AddExtension(string filePath, string targetExtension)
        => ExtensionNameRules.AddExtension(filePath, targetExtension);

    /// <summary>删除最后一个后缀。判据在 <see cref="ExtensionNameRules.DeleteLastExtension"/>。</summary>
    public static string DeleteLastExtension(string filePath)
        => ExtensionNameRules.DeleteLastExtension(filePath);

    /// <summary>删除多个后缀。判据在 <see cref="ExtensionNameRules.DeleteMultipleExtensions"/>。</summary>
    public static string DeleteMultipleExtensions(string filePath, int count)
        => ExtensionNameRules.DeleteMultipleExtensions(filePath, count);

    /// <summary>按真实格式给建议后缀。判据在 <see cref="ExtensionNameRules.GetSuggestedExtensionByFormat"/>。</summary>
    public static string GetSuggestedExtensionByFormat(string? format)
        => ExtensionNameRules.GetSuggestedExtensionByFormat(format);

    /// <summary>扫描模式里是不是疑似文件。判据在 <see cref="ExtensionNameRules.IsSuspiciousFile"/>。</summary>
    public static bool IsSuspiciousFile(string filePath)
        => ExtensionNameRules.IsSuspiciousFile(filePath);

    /// <summary>这一段是不是"分卷标记"（<c>.001</c> / <c>.z01</c> / <c>.r00</c> / <c>.partN</c>）。
    /// 判据在 <see cref="VolumeMarkerRules.IsVolumePartExtension"/>。</summary>
    public static bool IsVolumePartExtension(string? extension)
        => VolumeMarkerRules.IsVolumePartExtension(extension);

    /// <summary>把一段拆成「分卷标记 + 粘在后面的垃圾」（只认前缀档）。判据在 <see cref="VolumeSegmentSplitter.TrySplitVolumeSegment"/>。</summary>
    public static bool TrySplitVolumeSegment(string? segment, out string volumeSegment, out string junkTail)
        => VolumeSegmentSplitter.TrySplitVolumeSegment(segment, out volumeSegment, out junkTail);

    /// <summary>宽松版拆段（杂质夹在中间也认）。判据在 <see cref="VolumeSegmentSplitter.TrySplitVolumeSegmentLoose"/>。</summary>
    public static bool TrySplitVolumeSegmentLoose(string? segment, out string canonicalSegment, out string junk)
        => VolumeSegmentSplitter.TrySplitVolumeSegmentLoose(segment, out canonicalSegment, out junk);

    /// <summary>卷标记之前那一段（名字重建用，⛔ 不是基名）。判据在 <see cref="VolumeNameRebuilder.ResolveNamePrefix"/>。</summary>
    public static string ResolveNamePrefix(string? fileName)
        => VolumeNameRebuilder.ResolveNamePrefix(fileName);

    /// <summary>从最标准的一卷推名字干（名字重建用，⛔ 不是基名）。判据在 <see cref="VolumeNameRebuilder.ResolveNameStem"/>。</summary>
    public static string ResolveNameStem(string? fileName, string? expectedArchiveExtension)
        => VolumeNameRebuilder.ResolveNameStem(fileName, expectedArchiveExtension);

    /// <summary>归一名字前缀的族后缀段（<c>7sz</c> ⇒ <c>7z</c>）。判据在 <see cref="VolumeNameRebuilder.NormalizeFamilySegment"/>。</summary>
    public static string NormalizeFamilySegment(string prefix)
        => VolumeNameRebuilder.NormalizeFamilySegment(prefix);

    /// <summary>拆开 <c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c> 这一族（含"脏标记/脏尾巴"档）。
    /// 判据在 <see cref="VolumeNameRebuilder.TrySplitPartNumberedVolume"/>。</summary>
    public static bool TrySplitPartNumberedVolume(
        string? fileName,
        out string baseName,
        out string canonicalMark,
        out int index,
        out string tailSegment,
        out bool disguised)
        => VolumeNameRebuilder.TrySplitPartNumberedVolume(
            fileName,
            out baseName,
            out canonicalMark,
            out index,
            out tailSegment,
            out disguised);

    /// <summary>这一段去掉杂质后是不是某个归档本体后缀。判据在 <see cref="ExtensionSkeletonMatcher.TryRecoverDisguisedArchiveBody"/>。</summary>
    public static bool TryRecoverDisguisedArchiveBody(
        string? segment,
        out string canonicalSegment,
        out string removedCharacters)
        => ExtensionSkeletonMatcher.TryRecoverDisguisedArchiveBody(segment, out canonicalSegment, out removedCharacters);

    /// <summary>"去掉杂质后是合法卷标记"的谓词。判据在 <see cref="VolumeSegmentSplitter.IsVolumeSegment"/>。</summary>
    public static bool IsVolumeSegment(string? segment)
        => VolumeSegmentSplitter.IsVolumeSegment(segment);

    /// <summary>通用骨架命中（顺序出现 + 首尾对齐 + 结果唯一）。判据在 <see cref="ExtensionSkeletonMatcher.TryMatchKnownSkeleton"/>。</summary>
    public static bool TryMatchKnownSkeleton(
        string? segment,
        IEnumerable<string>? knownSkeletons,
        out string canonicalSegment,
        out string junk)
        => ExtensionSkeletonMatcher.TryMatchKnownSkeleton(segment, knownSkeletons, out canonicalSegment, out junk);

    /// <summary>partN 的卷标记段是不是靠骨架档才认出来的。判据在 <see cref="VolumeNameRebuildGate.IsPartNumberedMarkBySkeleton"/>。</summary>
    public static bool IsPartNumberedMarkBySkeleton(string? fileName, out string canonicalMark)
        => VolumeNameRebuildGate.IsPartNumberedMarkBySkeleton(fileName, out canonicalMark);

    /// <summary>卷标记段是不是"要重建才读得出来"。判据在 <see cref="VolumeNameRebuildGate.IsVolumeMarkByDisguise"/>。</summary>
    public static bool IsVolumeMarkByDisguise(string? fileName, out string canonicalMark)
        => VolumeNameRebuildGate.IsVolumeMarkByDisguise(fileName, out canonicalMark);

    /// <summary>归档后缀段是不是靠容错/骨架档才读出来的。判据在 <see cref="VolumeNameRebuildGate.IsArchiveSegmentByDisguise"/>。</summary>
    public static bool IsArchiveSegmentByDisguise(string? fileName, out string canonicalExtension)
        => VolumeNameRebuildGate.IsArchiveSegmentByDisguise(fileName, out canonicalExtension);

    /// <summary>partN 骨架（<c>pa8rt1</c> ⇒ <c>part1</c>）。
    /// 判据在 <see cref="VolumeMarkerSkeletonMatcher.TryMatchPartNumberedSkeleton"/>。</summary>
    public static bool TryMatchPartNumberedSkeleton(string s, out string canonicalSegment, out string junk)
        => VolumeMarkerSkeletonMatcher.TryMatchPartNumberedSkeleton(s, out canonicalSegment, out junk);

    /// <summary>容错档拆段（删 ≤2 字符 / 骨架 / 任意垃圾 / 拉长卷号）。判据在 <see cref="VolumeSegmentSplitter.TrySplitVolumeSegmentTolerant"/>。</summary>
    public static bool TrySplitVolumeSegmentTolerant(
        string? segment,
        out string canonicalSegment,
        out string junk,
        bool allowPartNumberedSkeleton = false)
        => VolumeSegmentSplitter.TrySplitVolumeSegmentTolerant(segment, out canonicalSegment, out junk, allowPartNumberedSkeleton);
}
