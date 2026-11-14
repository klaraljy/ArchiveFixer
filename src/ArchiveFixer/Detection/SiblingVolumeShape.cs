using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 同目录"兄弟卷"的**形状读数**：基名逐字相同 / 卷标记连续 / 除末片外等大（方案 §4 阶段 B 的 (e) 闸门）。
    ///
    /// <para><b>为什么要有这份读数</b>：一串文件里"有数字"从来不是证据（<c>风景01</c> 里也有 <c>01</c>）。
    /// 只有当同目录真能配出一组**形状自洽**的兄弟卷时，后缀段里的数字才允许参与"谁是首卷 / 怎么排"的判断；
    /// ⛔ 基名正文里的数字**永远不构成任何证据**（本类只看卷标记段，而卷标记段是
    /// <see cref="LegacyVolumeNames.TryGetIndex"/> 认出来的，基名里的 <c>01</c> 根本进不了那个判据）。</para>
    ///
    /// <para>⛔ <see cref="SelfConsistent"/> 只是**闸门**：它证明"这组名字自洽"，⛔ 不证明"这几片就是一组"——
    /// 成不成立仍然只由硬链接试开回答（<see cref="Extraction.VolumeProbeVerifier"/>）。</para>
    /// </summary>
    public sealed class SiblingVolumeShape
    {
        /// <summary>这一组的基名（唯一基名出口算出来的**包基名**，剥掉卷标记与归档后缀）。</summary>
        public string BaseName { get; init; } = string.Empty;

        /// <summary>成员总数（含锚点自己、含名字丢了的那几片）。</summary>
        public int MemberCount { get; init; }

        /// <summary>名字里**自报卷标记**的成员（升序）—— 「后缀段里的数字」只从这一份来。</summary>
        public IReadOnlyList<int> Markers { get; init; } = Array.Empty<int>();

        /// <summary>1..N 里没有任何成员自报的卷位（= 缺的那几卷，名字是已知的）。</summary>
        public IReadOnlyList<int> MissingMarkers { get; init; } = Array.Empty<int>();

        /// <summary>**名字整个丢了**的成员（基名逐字相同、但没有卷标记、也没有归档魔数）。</summary>
        public IReadOnlyList<VolumeCandidate> NamelessFillers { get; init; } = Array.Empty<VolumeCandidate>();

        /// <summary>三条同时成立：① 基名逐字相同（≥ 2 个成员）② 卷标记连续（1..N 无重复）③ 除末片外等大。</summary>
        public bool SelfConsistent { get; init; }

        /// <summary>判不出时的一句话（面向日志；成立时为空）。</summary>
        public string Blocker { get; init; } = string.Empty;
    }
}
