using System;
using System.Linq;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>L3 的预期清单**从哪来**（机器可判；日志/证据只读它，⛔ 不比中文）。</summary>
    public enum ManifestExpectationSource
    {
        /// <summary>取不到清单 ⇒ L4 只能判「判不出」（兜底那一档）。</summary>
        None = 0,

        /// <summary>让调用方**现问引擎**列一份（真包 / 内嵌归档抠出来的副本都走这条）。</summary>
        QueryArchive = 1,

        /// <summary>手上已经有第 0 层的清单（内嵌 ZIP 直读那条路带上来的）。</summary>
        OuterList = 2,

        /// <summary>**叶子层**（产出最终内容物的那一层）的清单 —— 展开 &gt; 1 层时用这一档。</summary>
        LeafLayer = 3
    }

    /// <summary>
    /// 一次 L3 裁决：这一单拿**哪一层**的清单当预期，或者**为什么取不到**。
    ///
    /// <para>它是 <c>ExtractionCoordinator</c> 与各删除闸门之间唯一的"预期清单"接口 ——
    /// 谁都不许自己再判一遍"展开了几层、该拿哪一层的清单"。</para>
    /// </summary>
    public sealed class ManifestExpectation
    {
        /// <summary>兜底值 = **取不到清单**（⛔ 漏赋值只会变成"不核对、不删源"，不会变成"敢删"）。</summary>
        public static ManifestExpectation None { get; } = new()
        {
            Source = ManifestExpectationSource.None,
            UnavailableReason = "没有列过归档清单，没法核对最终产物"
        };

        public ManifestExpectationSource Source { get; init; } = ManifestExpectationSource.None;

        /// <summary>能直接用的清单（<see cref="ManifestExpectationSource.LeafLayer"/> 时是本层清单折出来的那一份）。</summary>
        public ArchiveListResult? Expected { get; init; }

        /// <summary>清单来自哪一层（-1 = 不是某一层的清单）。</summary>
        public int LayerDepth { get; init; } = ChainManifestResolver.NoLayer;

        /// <summary>那一层的说法（`第 1 层：Sociology.7z`）。</summary>
        public string LayerLabel { get; init; } = string.Empty;

        /// <summary>取不到时的原因（**点名哪一层 + 为什么** + 用户能做什么）；取得到时为空串。</summary>
        public string UnavailableReason { get; init; } = string.Empty;

        /// <summary>有没有可用的清单。</summary>
        public bool HasManifest => Source != ManifestExpectationSource.None && Expected is { Success: true };

        /// <summary>排障用的一行（机器可判的字段，⛔ 不是给人看的文案）。</summary>
        public string Evidence => Source == ManifestExpectationSource.None
            ? $"ManifestExpectation=None;Reason={UnavailableReason}"
            : $"ManifestExpectation={Source};Layer={LayerDepth}";
    }

    /// <summary>
    /// **L3 该拿哪一层的清单当预期 —— 唯一出口**（用户 2026-09-30 真机报的 bug）。
    ///
    /// <para><b>真机现场</b>：`（T244）管理1.mp4`（伪装后缀，真身是从偏移处抠出来的内嵌 ZIP）
    /// → 第 0 层解出 `Sociology.7z`，第 1 层解出 8 个文件。两层**都**列得出清单
    /// （日志第 43 行"清单 2 个文件 / 解压后 5.2 GiB"就是第 0 层那份），
    /// 可老口径 `recursionOutranOuterManifest` 一见"展开 &gt; 1 层"就把预期整份丢掉 ⇒
    /// `ManifestCrossChecked = false` ⇒ L4「判不出」⇒ 用户设的"移入其余物 + 彻底删除"整条失效，
    /// 源包与其余物一个字节都没动。</para>
    ///
    /// <para><b>规则（两层含义分开，⛔ 别再混成一个布尔）</b>：</para>
    /// <list type="number">
    /// <item><description><b>第 0 层的清单只对第 0 层</b>：展开 &gt; 1 层时它**不再**描述最终产物
    /// （拿它核对叶子层的产物必然对不上 —— §11.5 那条口径本身是对的，不改）。</description></item>
    /// <item><description>但这**不等于"没有清单"**：最终产物由**叶子层**产出，所以换用叶子层的清单
    /// （引擎在解压前就列过它，见 <see cref="RecursionLayerReport.Manifest"/>）。
    /// 只有叶子层**真的**列不出来时，才落回"取不到"。</description></item>
    /// </list>
    ///
    /// <para>⛔ 兜底永远落在"取不到"那一档（= L4 判不出 = 什么都不做）。</para>
    /// </summary>
    public static class ChainManifestResolver
    {
        /// <summary>"不是某一层的清单"。</summary>
        public const int NoLayer = -1;

        /// <summary>
        /// 决定这一单的预期清单。
        /// </summary>
        /// <param name="recursion">递归展开结论；<c>null</c> = 不是递归路径（单层 / 手动「只解压」）。</param>
        /// <param name="knownList">调用方已经拿在手上的第 0 层清单（内嵌 ZIP 直读那条路带进来的）。</param>
        public static ManifestExpectation Resolve(RecursionResult? recursion, ArchiveListResult? knownList)
        {
            int expandedLayers = recursion?.Layers.Count(layer => layer.Success) ?? 0;

            /*
             * 展开 0~1 层：**老口径一个字不改** ——
             * 第 0 层的清单就是最终产物的清单（只有一层，两者本来就是同一回事）。
             * 直读路线把清单直接带进来了就用它（它必须与真正解出来的东西是同一份），
             * 否则让调用方现问引擎列一份。
             */
            if (expandedLayers <= 1)
            {
                if (knownList is { Success: true })
                {
                    return new ManifestExpectation
                    {
                        Source = ManifestExpectationSource.OuterList,
                        Expected = knownList,
                        LayerDepth = expandedLayers == 1 ? 0 : NoLayer
                    };
                }

                return new ManifestExpectation
                {
                    Source = ManifestExpectationSource.QueryArchive,
                    LayerDepth = expandedLayers == 1 ? 0 : NoLayer
                };
            }

            /*
             * 展开 > 1 层：第 0 层清单不再描述最终产物 ⇒ 换成**叶子层**（层号最大的那个成功层）的清单。
             * 只认"层号最大且自己列出了清单"的那一层：中间层的产物里那个内层包已经被
             * 同名目录就地替换（ExtractionWorkspace.TryDeleteConsumedPackage），
             * 拿中间层的清单去核对最终产物一样对不上 —— 那会造出**假失败**。
             */
            RecursionLayerReport? deepest = recursion!.Layers
                .Where(layer => layer.Success)
                .OrderByDescending(layer => layer.Depth)
                .FirstOrDefault();

            if (deepest?.Manifest is { Available: true } manifest)
            {
                return new ManifestExpectation
                {
                    Source = ManifestExpectationSource.LeafLayer,
                    Expected = manifest.ToExpected(),
                    LayerDepth = deepest.Depth,
                    LayerLabel = DescribeLayer(deepest)
                };
            }

            return new ManifestExpectation
            {
                Source = ManifestExpectationSource.None,
                LayerDepth = deepest?.Depth ?? NoLayer,
                LayerLabel = deepest == null ? string.Empty : DescribeLayer(deepest),
                UnavailableReason = DescribeUnavailable(deepest, expandedLayers)
            };
        }

        /// <summary>一层的说法：`第 1 层：Sociology.7z`（只写文件名 —— ⛔ 不把完整路径写进日志，§8）。</summary>
        public static string DescribeLayer(RecursionLayerReport? layer)
        {
            if (layer == null)
            {
                return string.Empty;
            }

            string name;

            try
            {
                name = System.IO.Path.GetFileName(layer.ArchivePath);
            }
            catch
            {
                name = string.Empty;
            }

            return name.Length == 0
                ? $"第 {layer.Depth} 层"
                : $"第 {layer.Depth} 层：{name}";
        }

        /// <summary>
        /// 单层 / 手动路径上"现问引擎列一份清单"也失败时的那份结论（点名这一份归档 + 为什么 + 用户能做什么）。
        ///
        /// <para>没有这一句会怎样：那两条路本来连"层"的概念都没有，L4 只能说"没有可用的归档清单可核对"——
        /// 用户不知道是包坏了、要密码，还是程序的问题。</para>
        /// </summary>
        public static ManifestExpectation DescribeOuterUnavailable(string archivePath, ArchiveListResult? queried)
        {
            string name;

            try
            {
                name = System.IO.Path.GetFileName(archivePath);
            }
            catch
            {
                name = string.Empty;
            }

            string label = name.Length == 0 ? "第 0 层这一份归档" : $"第 0 层：{name}";

            return new ManifestExpectation
            {
                Source = ManifestExpectationSource.None,
                LayerDepth = 0,
                LayerLabel = label,
                UnavailableReason = $"{label}没能取得清单：{LayerManifest.DescribeListFailure(queried)}；"
                                    + "产物照常保留、源包一个字节都不动 —— "
                                    + "若是加密包（文件名也加密那一档），请先在「密码」页配上密码再跑一次；"
                                    + "若是包本身损坏或没下完，请换一份完整副本"
            };
        }

        /// <summary>
        /// 取不到清单时的一句原因：**点名是哪一层、为什么、用户能做什么**
        /// （用户 2026-09-30：日志不许再让人猜）。
        /// </summary>
        private static string DescribeUnavailable(RecursionLayerReport? deepest, int expandedLayers)
        {
            if (deepest == null)
            {
                return $"本次展开了 {expandedLayers} 层，但没有任何一层留下了可用的清单，"
                       + "没法核对最终产物；请把这几个包分别单独解开确认（或先看下面的失败原因）";
            }

            string why = string.IsNullOrWhiteSpace(deepest.Manifest.Reason)
                ? "引擎没能列出这一层的目录"
                : deepest.Manifest.Reason;

            return $"{DescribeLayer(deepest)}（产出最终内容物的那一层）没能取得清单：{why}；"
                   + "产物照常保留、源包一个字节都不动 —— "
                   + "若是加密包，请先在「密码」页配上密码再跑一次；若是包本身损坏，请换一份完整副本";
        }
    }
}
