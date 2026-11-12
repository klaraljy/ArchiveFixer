using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{    /// <summary>递归展开的最终结论。</summary>
    public sealed class RecursionResult
    {
        public RecursionStopReason StopReason { get; init; }

        /// <summary>真的走完了（含 SingleLayer 这种"按模式正常收尾"）。</summary>
        public bool Completed { get; init; }

        /// <summary>解出了一些层但没走完（含 NeedsDecision，以及产物留在工作区的情形）。</summary>
        public bool PartiallyCompleted { get; init; }

        /// <summary>非 null 表示需要用户就多分支做选择。</summary>
        public RecursionDecisionRequest? Decision { get; init; }

        public IReadOnlyList<RecursionLayerReport> Layers { get; init; } = Array.Empty<RecursionLayerReport>();

        /// <summary>
        /// 停下来时**这一层还有哪几个内层归档没展开**（完整路径；空 = 没有未展开的分支）。
        ///
        /// <para>为什么要带上名单（用户 2026-10-04 真机）：过去只有个数，Summary 里也只写个数 ——
        /// 用户看不出是哪个包没展开，只能自己去工作区里翻。调用方（协调器的「两条出路」那一条）
        /// 也读这一份，⛔ 不许自己再数一遍。</para>
        /// </summary>
        public IReadOnlyList<string> UnexpandedNames { get; init; } = Array.Empty<string>();

        /// <summary>指向**已完成的最深一层**产物；部分完成时这里是工作区里的路径。</summary>
        public string FinalOutputPath { get; init; } = string.Empty;

        /// <summary>
        /// 这一趟**借来用过的用户源片**（绝对路径；空 = 没用过）。
        ///
        /// <para>唯一用途是给协调器记账：跨盘 ZIP 的末片压在包里、其余几片散在用户目录里时，
        /// 那几片是**被这条链凑齐并解开**的 —— 它们自己那一单就不该再报「分卷缺失」，
        /// 而应按跟班卷处理（真机第八批：内容已经解出来了，三片却全报失败）。</para>
        /// </summary>
        public IReadOnlyList<string> ConsumedVolumeSources { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 这一趟停下时**打不开的那几片**（工作区里的绝对路径；空 = 没有）。
        ///
        /// <para>唯一用途：协调器把它们按规范卷名**接到"这一组还缺卷"的那一单旁边**
        /// （跨链收卷，真机第九批 CCCC）—— 不然这条链一收尾，工作区连同刚解出来的那一片一起被删，
        /// 而它可能正是同批另一组缺的那一片。</para>
        /// </summary>
        public IReadOnlyList<string> UnresolvedVolumePieces { get; init; } = Array.Empty<string>();

        /// <summary>一行中文结论，直接显示给用户。</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>
        /// 递归核心自身抛出的异常（只给诊断日志用；⛔ 不参与状态/文案判断）。
        /// 普通引擎失败、密码错误、缺卷等业务停因不会填这里。
        /// </summary>
        public Exception? FailureException { get; init; }

        /// <summary>
        /// **这一趟有没有真的解出过东西**（任意一层成功、或任意一层的产物目录里留下了文件）。
        ///
        /// <para>为什么要单列一条（用户 2026-09-27 真机 `giu.7z.001`）：调用方要据此区分
        /// "一半做完了"（<c>部分完成</c>）与"什么都没产出"（<c>失败</c>）——
        /// 后者说成"部分完成"会让用户去工作区里找根本不存在的产物；</para>
        ///
        /// <para>判据只看**事实**（层的成功标记 + 产物文件数 / 字节数 + <see cref="AnyLayerProductsOnDisk"/>），
        /// ⛔ 不比对任何中文文案（AGENTS.md §7）。</para>
        ///
        /// <para>⚠ 2026-10-16 补的第二个来源（用户口径：「密码错了但是**其他的地方有内容物**这个肯定判部分完成」）：
        /// 失败层的报告里 <c>OutputFileCount</c> **按定义是 0**（`BuildLayerReport` 写的），
        /// 可它的产物目录里**可能真有东西** —— 真机的错密码候选就会在报错前留下文件
        /// （`ProducedContentGate` 的注释里那条实测："真机那 10 个错候选里就有留下一个文件的"）。
        /// 第一版只看报告 ⇒ 把这种"解出来一半再失败"判成「失败」，与口径相反（守门用例当场红）。</para>
        /// </summary>
        public bool ProducedAnyLayerOutput =>
            AnyLayerProductsOnDisk
            || Layers.Any(layer => layer.Success || layer.OutputFileCount > 0 || layer.OutputSize > 0);

        /// <summary>
        /// **盘上那一半事实**：任意一层的产物目录里真的留下了文件（由收尾那一刻数一次、写进来）。
        /// 它只在**失败层**上才有独立意义（成功层的文件数已经在 <see cref="Layers"/> 的报告里）。
        /// </summary>
        public bool AnyLayerProductsOnDisk { get; init; }
    }
}
