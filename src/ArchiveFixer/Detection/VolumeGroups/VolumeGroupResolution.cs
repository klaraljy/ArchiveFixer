using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Detection
{    /// <summary>
     /// 一次分卷组装判定的**全部输出**。对外只读它一个对象 —— 三处消费方（解前预检 / 删除闸门 /
     /// 可删残留分类）拿到的都是同一份结论，⛔ 不许各自再判一遍。
     /// </summary>
    public sealed class VolumeGroupResolution
    {
        /// <summary>唯一结论。</summary>
        public VolumeGroupVerdict Verdict { get; init; }

        /// <summary>这次是围着哪个文件问的（原样回填，方便调用方对上号）。</summary>
        public string AnchorPath { get; init; } = string.Empty;

        /// <summary>组基名（<c>111.7z</c> / <c>一只顶美</c> / <c>x</c>）；判不出时为空串。</summary>
        public string BaseName { get; init; } = string.Empty;

        /// <summary>已定位的卷（按卷序升序；含靠推定/试开认进来的那些）。</summary>
        public IReadOnlyList<ResolvedVolume> Volumes { get; init; } = Array.Empty<ResolvedVolume>();

        /// <summary>**有证据**的缺卷名（只有文件名）。弱证据的疑缺不写在这里。</summary>
        public IReadOnlyList<string> MissingVolumeNames { get; init; } = Array.Empty<string>();

        /// <summary>**按位置推定**的候选（弱证据，只报不采信），形如「<c>111</c> 推定是第 2 卷」。</summary>
        public IReadOnlyList<string> PositionInferredNotes { get; init; } = Array.Empty<string>();

        /// <summary>证据表（每条证据一行；判不出时也要有，用来说清"依据是什么"）。</summary>
        public IReadOnlyList<VolumeEvidence> Evidence { get; init; } = Array.Empty<VolumeEvidence>();

        /// <summary>给人看的一句话结论 + 依据。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>
        /// **属于这一组的全部文件**（含推定进来的那些）。
        ///
        /// <para>⛔ 这条清单就是"永远不许进可删残留名单"的那个名单 —— 25 GB 那次事故的根因正是
        /// 剩下 5 卷每一卷单看都像"待续解的过程物"。</para>
        /// </summary>
        public IReadOnlyList<string> GroupFilePaths { get; init; } = Array.Empty<string>();

        /// <summary>
        /// **同一卷的名字变体**（`222.zip` 与 `222.zi删除p` 是同一个逻辑卷的两个名字）。
        ///
        /// <para>它们也算组成员（<see cref="IsGroupMember"/> 认它们），但**不占独立卷号** ——
        /// 所以它们既不进 <see cref="Volumes"/>、也不构成"缺了一卷"的证据。
        /// 判据见 <c>ResolveGroup</c> 里"名字变体"那一段。</para>
        /// </summary>
        public IReadOnlyList<string> VariantFilePaths { get; init; } = Array.Empty<string>();

        /// <summary>组里有没有"名字不标准"的成员（靠推定/试开才认进来的，或卷名里夹了垃圾）。</summary>
        public bool HasRenamedVolume { get; init; }

        /// <summary>
        /// **这一组可不可以进"可删的其余物"**（= 允许删除 / 搬走的唯一资格）。
        ///
        /// <para>只有**两条都成立**才行：① 结论是 <see cref="VolumeGroupVerdict.Complete"/>；
        /// ② 每一卷的名字都是 7-Zip 按原名认得的标准卷名（名字不标准 ⇒ 按原名根本打不开 ⇒ 删了就没了）。</para>
        ///
        /// <para>⛔ 其余一切情形（含"判不出"与"弱证据"）都在这里返回 false ——
        /// 兜底一律落在"什么都不做"那一档（AGENTS.md §9.5）。</para>
        /// </summary>
        public bool CanEnterDeletableRestItems { get; init; }

        /// <summary>这个文件是不是这一组的成员（同一份结论，供调用方逐个文件问）。</summary>
        public bool IsGroupMember(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string full = SafePathHelper.GetFullPathSafe(filePath);

            if (full.Length == 0)
            {
                return false;
            }

            foreach (string member in GroupFilePaths)
            {
                if (string.Equals(member, full, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

}
