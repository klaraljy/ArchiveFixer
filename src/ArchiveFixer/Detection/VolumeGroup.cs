using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 一组分卷（AGENTS.md §9.3：**一组分卷 = 一个任务**，只从 <see cref="FirstVolumePath"/> 启动）。
    /// </summary>
    public sealed class VolumeGroup
    {
        /// <summary>
        /// 归组键，形如 <c>目录|基名</c>（例：<c>C:\t|volume.7z</c>），语义上忽略大小写。
        ///
        /// 只有极少数情况下末尾会再加一段族标记（<c>|num</c> / <c>|z</c> / <c>|r</c> / <c>|part</c>）：
        /// 同一目录同一基名下**并存两套命名**时（例如 <c>x.rar</c>+<c>x.r00</c> 与 <c>x.part1.rar</c>+<c>x.part2.rar</c>，
        /// 这是两个真实存在、互不兼容的分卷集），否则两组会撞同一个键。下游拿它当字典键是安全的。
        /// </summary>
        public string GroupKey { get; init; } = string.Empty;

        /// <summary>所在目录（调用方给的原样路径片段，本模块不碰文件系统，不做全路径规范化）。</summary>
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>
        /// 基名。例：<c>volume.7z.001</c> → <c>volume.7z</c>、<c>archive.001</c> → <c>archive</c>、
        /// <c>movie.part1.rar</c> → <c>movie</c>、<c>x.z01</c>/<c>x.zip</c> → <c>x</c>。
        /// 保留首次出现时的大小写，比较时忽略大小写。
        /// </summary>
        public string BaseName { get; init; } = string.Empty;

        /// <summary>已按卷序升序排列（含 zip/rar 本体这一"第 1 卷"）。</summary>
        public IReadOnlyList<VolumeCandidate> Volumes { get; init; } = Array.Empty<VolumeCandidate>();

        /// <summary>
        /// 启动解压用的那一卷：**已找到**的分卷里卷序最小的那一卷。
        ///
        /// 注意两点：一是 <see cref="IsComplete"/> 为 false 时不得拿它去启动（不变量 7：缺卷不得开始不可完成的任务，
        /// 先把 <see cref="MissingVolumeNames"/> 报给用户）；二是只有 <c>.z01</c>/<c>.r00</c> 而没有本体时，
        /// 这里会是 <c>.z01</c>/<c>.r00</c>，同时缺卷清单里带着本体名。
        /// </summary>
        public string FirstVolumePath { get; init; } = string.Empty;

        /// <summary>已找到的卷数（含本体）。</summary>
        public int KnownVolumeCount { get; init; }

        /// <summary>
        /// 期望卷数；无法确定时为 0。
        ///
        /// 本模块认的五种命名（<c>.001</c> / <c>.z01</c> / <c>.r00</c> / <c>.partN.rar</c> / 本体）
        /// **没有一个带"总共几卷"这个信息**，所以这里恒为 0 —— 这是有意为之，
        /// 不是"没实现"：拿"找到的最大卷号"冒充总数，会让缺卷的组看起来是完整的。
        /// 字段保留是给将来出现真带总数的命名（例如某些打包器会在文件名里写 total）留位。
        /// </summary>
        public int ExpectedVolumeCount { get; init; }

        /// <summary>
        /// 卷序是否**从 1 开始连续、没有缺号**。
        ///
        /// 为什么不是"期望卷数 > 0 且无缺口"：期望卷数永远推不出来（见 <see cref="ExpectedVolumeCount"/>），
        /// 那条判据会恒为 false，等于把所有正常的分卷组都标成不完整。
        /// 所以这里取"能验证的那一半"：找到了第 1 卷、且 1..最大卷号之间没有洞。
        /// 反过来，只有 1 卷（例如只有 <c>volume.7z.001</c>）时这里是 true —— 后面有没有卷没人知道，
        /// 这种情况由 <see cref="Note"/> 提示"可能不完整"，不要用 false 冒充确定结论。
        /// </summary>
        public bool IsComplete { get; init; }

        /// <summary>
        /// 缺失的卷文件名（只有文件名，不含目录）。判据是卷号：<c>.001</c>+<c>.003</c> 缺的是
        /// <c>volume.7z.002</c>；<c>.z01</c>/<c>.r00</c> 缺本体时报 <c>x.zip</c>/<c>x.rar</c>。
        /// 超出"最大已找到卷号"的缺号无法得知，不会列在这里（总数未知，不猜）。
        /// </summary>
        public IReadOnlyList<string> MissingVolumeNames { get; init; } = Array.Empty<string>();

        /// <summary>给用户看的一句话，例如"无法确定分卷总数，按顺序找到 3 卷"。</summary>
        public string Note { get; init; } = string.Empty;
    }
}
