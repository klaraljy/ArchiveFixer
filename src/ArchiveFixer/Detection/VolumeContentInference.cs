using System.Collections.Generic;

namespace ArchiveFixer.Detection;

/// <summary>
/// **内容级**分卷推断（模块公开入口）：名字靠不住时，只拿"文件内容 + 同目录尺寸"这两条与名字无关的事实，
/// 推出一组卷的候选与假设顺序。
///
/// <para><b>为什么需要它</b>（用户 2026-09-28 真机）：他把三个 7z 卷的名字改成了
/// <c>amb909.7.01</c> / <c>amb909.z.2</c> / <c>amb909..3</c> —— 卷号被改烂、后缀也不对，
/// 任何**按名字**的判据都认不出它们是一组（第一卷既不叫 <c>.7z</c> 也不叫 <c>.001</c>）。
/// 但内容没有骗人：第一卷开头就是 7z 魔数，且它自己单独打不开（说明后面还有卷）。</para>
///
/// <para><b>物理事实（⛔ 别指望更多）</b>：7z 的 <c>-v</c> 分卷**内容里没有任何卷号标记** ——
/// <c>.001</c> 是正常 7z 头，之后是裸字节流，所以"从内容直接读出第几卷"在 7z 上**不可能**。
/// 模块只做能做的四件事：① 用内容认出"这是 7z 的第一卷"；② 用同目录**尺寸**排出候选顺序；
/// ③ 名字里那个"短数字尾巴"（<c>.2</c>）只用来**排序 / 当放行的第二张门票**；
/// ④ 把顺序交给 <c>VolumeProbeVerifier</c> 用引擎**试开**验证（验证通过才算数）。</para>
///
/// <para><b>两卷形状（用户 2026-09-29 第二次真机）</b>：现场是 <c>amb909.7.01</c>（正好 2 GiB，
/// 带 7z 魔数）+ <c>amb909.z.2</c>（1.89 GB，认不出格式 = 裸的续卷），程序却报
/// 「同目录里也没有找到像后续卷的文件」—— 因为"尺寸规律"要的是"有与第一卷等长的文件"，
/// 而两卷时第二卷就是余量、必然更短。这一档现在由 <see cref="HasTwoVolumeShapeEvidence"/>
/// 放过闸门（名字的尾巴接得上 / 第一卷是整数 MiB），是不是真的一组仍旧只由试开回答。</para>
///
/// <para><b>职责分工（本类只留公开 API 薄转发，判据都在协作类里，⛔ 不在这里加分支）</b>：</para>
/// <list type="bullet">
/// <item><description>内容解码 → <see cref="VolumeContentSniffer"/>：魔数 → 格式 / 标准后缀。</description></item>
/// <item><description>卷号推断（收目录 → 候选池 → 短数字尾巴 → 假设顺序）→ <see cref="VolumeCandidateOrdering"/>。</description></item>
/// <item><description>证据建模 → <see cref="SiblingVolumeShapeReader"/>（兄弟卷形状读数）与
/// <see cref="VolumeShapeAdmission"/>（两张放行门票）。</description></item>
/// <item><description>试开落点 → <see cref="VolumeProbeWorkspace"/>：同卷判据 / 工作区根 / 假设名。</description></item>
/// <item><description>文件事实 → <see cref="VolumeFileFacts"/>：大小与文件名，读不到给哨兵值、绝不抛。</description></item>
/// </list>
///
/// <para>模块整体是**纯逻辑**：只读文件头（8 字节）、只做算术与排序，不建目录、不改名字、不调引擎；
/// 拿不到事实时一律"判不出 ⇒ 不试开、不动"。</para>
/// </summary>
public static class VolumeContentInference
{
    /// <summary>
    /// 试开用的工作目录名（与工作区根同名）：⛔ 必须与第一卷**同卷**，
    /// 跨卷建目录就没法做硬链接（硬链接不能跨卷），那就只能复制 GB 级文件 —— 绝对不行。
    /// </summary>
    public const string WorkDirectoryName = ".ArchiveFixer.work";

    /// <summary>
    /// 排列数量上限（6 个候选的全排列是 720，每个排列都要真的起一次 7z，全试完要几分钟）。
    /// 唯一实现在 <see cref="VolumeCandidateOrdering.MaxOrderings"/>。
    /// </summary>
    public const int MaxOrderings = VolumeCandidateOrdering.MaxOrderings;

    /// <summary>
    /// 内容解码：读文件头判断格式。读不出来（不存在 / 被占 / 权限）一律
    /// <see cref="VolumeContentFormat.Unknown"/>，**绝不抛**。实现见 <see cref="VolumeContentSniffer"/>。
    /// </summary>
    public static VolumeContentFormat SniffFormat(string? filePath) =>
        VolumeContentSniffer.SniffFormat(filePath);

    /// <summary>内容对应的标准后缀（不含点）；认不出来返回空串。实现见 <see cref="VolumeContentSniffer.ExtensionFor"/>。</summary>
    public static string ExtensionFor(VolumeContentFormat format) =>
        VolumeContentSniffer.ExtensionFor(format);

    /// <summary>
    /// 候选 = 同目录里除第一卷外"大小 ≤ 第一卷"、且**认不出归档魔数**的文件，
    /// 按大小降序、同大小按名字升序。⛔ 只是候选，是不是真的一组由试开决定。
    /// 实现见 <see cref="VolumeCandidateOrdering.BuildCandidates"/>。
    /// </summary>
    public static IReadOnlyList<VolumeCandidate> BuildCandidates(
        string? firstVolumePath,
        IEnumerable<VolumeCandidate>? filesInDirectory) =>
        VolumeCandidateOrdering.BuildCandidates(firstVolumePath, filesInDirectory);

    /// <summary>
    /// 名字里的**短数字尾巴**（最后一段是 1~3 位纯数字）：<c>amb909.7.01</c> → 1、
    /// <c>amb909.z.2</c> → 2；认不出来返回 null。⛔ 只用来排序 / 当第二张门票，不用来判"是不是分卷"。
    /// 实现见 <see cref="VolumeCandidateOrdering.TryReadShortNumberTail"/>。
    /// </summary>
    public static int? TryReadShortNumberTail(string? path) =>
        VolumeCandidateOrdering.TryReadShortNumberTail(path);

    /// <summary>
    /// 尺寸规律（第一张门票）：同目录里**至少有一个与第一卷等长的文件**。
    /// ⛔ 只是"值得试开一次"的门槛，结论仍由试开给。实现见 <see cref="VolumeShapeAdmission.HasVolumeSizePattern"/>。
    /// </summary>
    public static bool HasVolumeSizePattern(
        string? firstVolumePath,
        IReadOnlyList<VolumeCandidate>? candidates) =>
        VolumeShapeAdmission.HasVolumeSizePattern(firstVolumePath, candidates);

    /// <summary>
    /// 读同目录这一组**兄弟卷的形状**（基名逐字相同 / 卷标记连续 / 除末片外等大）：
    /// 它是一条**闸门**，不是结论。实现见 <see cref="SiblingVolumeShapeReader.ReadSiblingShape"/>。
    /// </summary>
    public static SiblingVolumeShape ReadSiblingShape(
        string? anchorPath,
        IEnumerable<VolumeCandidate>? filesInDirectory,
        string archiveExtension) =>
        SiblingVolumeShapeReader.ReadSiblingShape(anchorPath, filesInDirectory, archiveExtension);

    /// <summary>
    /// **「两卷形状」这张门票**（第二张门票）：名字里的短数字尾巴接得上（且兄弟卷形状自洽），
    /// 或第一卷是整数 MiB。⛔ 只是"值得试开一次"的门槛，成不成立仍然只由试开回答。
    /// 实现见 <see cref="VolumeShapeAdmission.HasTwoVolumeShapeEvidence"/>。
    /// </summary>
    public static bool HasTwoVolumeShapeEvidence(
        string? firstVolumePath,
        IReadOnlyList<VolumeCandidate>? candidates) =>
        VolumeShapeAdmission.HasTwoVolumeShapeEvidence(firstVolumePath, candidates);

    /// <summary>
    /// 候选的**假设顺序**（第一个元素是第 2 卷，依此类推），最多 <see cref="MaxOrderings"/> 个。
    /// ⛔ 只是假设，成不成立由试开回答。实现见 <see cref="VolumeCandidateOrdering.BuildOrderings"/>。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<VolumeCandidate>> BuildOrderings(
        string? firstVolumePath,
        IReadOnlyList<VolumeCandidate>? candidates) =>
        VolumeCandidateOrdering.BuildOrderings(firstVolumePath, candidates);

    /// <summary>
    /// 试开目录：**只认调用方给的工作区根**（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）；
    /// 没有工作区根 / 跨卷 ⇒ 空串（= 不试开，⛔ 不另找落点、⛔ 不复制）。
    /// 实现见 <see cref="VolumeProbeWorkspace.BuildProbeRoot"/>。
    /// </summary>
    public static string BuildProbeRoot(string? firstVolumePath, string? preferredWorkRoot) =>
        VolumeProbeWorkspace.BuildProbeRoot(firstVolumePath, preferredWorkRoot);

    /// <summary>
    /// 两个路径是不是在**同一个卷**上（= 硬链接唯一的成立条件）；判不出按"跨卷"处理。
    /// 实现见 <see cref="VolumeProbeWorkspace.IsSameVolumeRoot"/>。
    /// </summary>
    public static bool IsSameVolumeRoot(string? pathA, string? pathB) =>
        VolumeProbeWorkspace.IsSameVolumeRoot(pathA, pathB);

    /// <summary>试开时第 <paramref name="index"/>（1 起）卷的假设名。实现见 <see cref="VolumeProbeWorkspace.ProbeFileName"/>。</summary>
    public static string ProbeFileName(int index) => VolumeProbeWorkspace.ProbeFileName(index);

    /// <summary>
    /// 枚举**一个目录**里的"路径 + 大小"（读不了返回空，绝不抛）：同目录与子目录两处必须走同一份实现。
    /// 实现见 <see cref="VolumeCandidateOrdering.EnumerateCandidatesIn"/>。
    /// </summary>
    public static IReadOnlyList<VolumeCandidate> EnumerateCandidatesIn(string? directory) =>
        VolumeCandidateOrdering.EnumerateCandidatesIn(directory);
}
