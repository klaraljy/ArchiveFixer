using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 放行闸门（准入）：只回答"值不值得为这一组卷起一次 7z 试开"，不回答"它们是不是一组"。
///
/// <para><b>为什么闸门要独立成层</b>：试开一次是几十秒到几分钟的真引擎调用，而"内容级推断"每一步都是
/// 纯算术 —— 一旦让推断自己决定"直接解"，代价就失控。两张门票就是那道边界：满足其一才允许试开，
/// ⛔ 试开成不成立仍然只由 <c>VolumeProbeVerifier</c> 的硬链接试开回答。</para>
///
/// <para><b>两张门票</b>（调用方按 <c>||</c> 组合）：</para>
/// <list type="number">
/// <item><description><see cref="HasVolumeSizePattern"/>：同目录里有与第一卷**等长**的文件（满卷规律）；</description></item>
/// <item><description><see cref="HasTwoVolumeShapeEvidence"/>：两卷形状（名字尾巴接得上且兄弟卷形状自洽，或第一卷是整数 MiB）。</description></item>
/// </list>
///
/// <para><b>失败模型</b>：候选为空 / 拿不到第一卷大小 / 兄弟卷配不成一组 ⇒ 两张门票都给 <c>false</c>
/// （= 不放行），**绝不抛**；"判不出 ⇒ 不试开、不动"。</para>
///
/// <para>生命周期：纯静态、无状态；只读调用方已经收好的"路径 + 大小"清单，自己不枚举目录。</para>
/// </summary>
internal static class VolumeShapeAdmission
{
    /// <summary>
    /// 尺寸规律（三层证据里的第 2 层，与名字无关）：同目录里**至少有一个与第一卷等长的文件**。
    ///
    /// <para>为什么是"至少一个等长"而不是"所有候选都等长"：真实目录里除了这一组卷，常常还躺着
    /// 别的杂七杂八的小文件（说明、封面、另一个包……），要求"全部都等长"会把正常情形也否掉。
    /// 等长本身就是很强的证据（<c>-v</c> 切出来的卷除最后一卷外**必然**一模一样大），
    /// 足够当"值得试开一次"的门槛 —— 真正的结论仍然由试开给。</para>
    /// </summary>
    public static bool HasVolumeSizePattern(string? firstVolumePath, IReadOnlyList<VolumeCandidate>? candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return false;
        }

        long firstSize = VolumeFileFacts.SizeOf(firstVolumePath);

        return firstSize > 0 && candidates.Any(c => c.Size == firstSize);
    }

    /// <summary>
    /// **「两卷形状」这张门票**（用户 2026-09-29 现场：<c>amb909.7.01</c> 正好 2 GiB、
    /// <c>amb909.z.2</c> 1.89 GB —— 一组两卷的包，能救却救不回来）。
    ///
    /// <para><b>为什么必须放宽到这一档</b>：<see cref="HasVolumeSizePattern"/> 要的是"有与第一卷等长的文件"，
    /// 而**两卷**这一组里第二卷就是余量、必然更短 —— 那条规律**永远**给不出证据。
    /// 结果就是最常见的两卷切法反而被挡在门外（老行为：报「同目录里也没有找到像后续卷的文件」，
    /// 而兄弟卷就躺在同一个目录里）。</para>
    ///
    /// <para><b>两张门票，满足其一即可</b>（⛔ 都只是"值得试开一次"的门槛，成不成立仍然只由
    /// <c>VolumeProbeVerifier</c> 的硬链接试开回答；试不出就什么都不做）：</para>
    /// <list type="number">
    /// <item><description><b>名字里的短数字尾巴接得上</b>：第一卷有尾巴时要求"候选 = 它 + 1"
    /// （<c>.01</c> → <c>.2</c>），第一卷没尾巴时要求候选的号 ≥ 2（用户原话："可以靠后缀数字
    /// 2 的情况猜一猜是第二卷"）。⚠ 2026-10-03 阶段 B 起它**还要过形状闸门**
    /// （<see cref="SiblingVolumeShapeReader.ReadSiblingShape"/> 的
    /// <see cref="SiblingVolumeShape.SelfConsistent"/>）——
    /// 后缀段里的数字要参与"谁是首卷"就必须先有一组形状自洽的兄弟卷（方案 §1.3 第 3 层 / §4 阶段 B 的 (e)）；</description></item>
    /// <item><description><b>体积规律</b>：第一卷是**整数 MiB** —— 7z 的切分上限永远是整数 MiB
    /// （<c>-v1m</c> / <c>-v2g</c> / <c>-v100m</c>），所以"它是满片、这个更短的是末卷"在体积上说得通
    /// （用户原话："对不上再用体积规律（7z <c>-v</c> 的除末卷外都是满片：正好等于切分上限，例如 2 GiB）"）。
    /// 这只是**放行的第二张门票**，不是结论。</description></item>
    /// </list>
    /// </summary>
    public static bool HasTwoVolumeShapeEvidence(
        string? firstVolumePath,
        IReadOnlyList<VolumeCandidate>? candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return false;
        }

        long firstSize = VolumeFileFacts.SizeOf(firstVolumePath);

        if (firstSize <= 0)
        {
            return false;
        }

        // 有等长的 → 走的是"尺寸规律"那张门票，本张不必出手（调用方按 或 组合两张）。
        if (candidates.Any(c => c.Size == firstSize))
        {
            return false;
        }

        VolumeCandidate? partial = VolumeCandidateOrdering.LargestShorter(firstVolumePath, candidates);

        if (partial == null)
        {
            return false;
        }

        int? firstTail = VolumeCandidateOrdering.TryReadShortNumberTail(firstVolumePath);
        int? partialTail = VolumeCandidateOrdering.TryReadShortNumberTail(partial.Path);

        /*
         * ⛔ 这一条是「后缀段里的数字」在**定首卷**（"手上这一卷是第 1 卷、这个更短的接在它后面"）：
         * 方案 §4 阶段 B 的 (e) 规定它**必须先过形状闸门**（基名逐字相同 + 卷标记连续 + 除末片外等大），
         * 否则一个碰巧带数字的文件（`风景01` 那种）就能冒充首卷。
         * ⛔ 它不是被提到内容证据之前：闸门只在**没有内容证据**（没有等长满片）时才会被问到，
         * 而且放行之后仍然要由硬链接试开裁决。
         */
        if (partialTail is int number
            && number >= 2
            && (firstTail == null || firstTail.Value == number - 1)
            && SiblingVolumeShapeReader.ReadSiblingShape(
                firstVolumePath,
                candidates,
                VolumeContentSniffer.ExtensionFor(VolumeContentFormat.SevenZip)).SelfConsistent)
        {
            return true;
        }

        // 体积规律：切分上限永远是整数 MiB，"第一卷正好整 MiB"= 它在体积上像个满片。
        const long MiB = 1024 * 1024;

        return firstSize >= MiB && firstSize % MiB == 0;
    }
}
