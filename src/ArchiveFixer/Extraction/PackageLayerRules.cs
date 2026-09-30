using System;
using System.Collections.Generic;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「**哪一层留、那一层叫什么**」的**唯一出口**（用户 2026-09-30 中午定的三条规则）。
    ///
    /// <para>
    /// 三条规则（原话要点）：
    /// </para>
    /// <list type="number">
    /// <item><description><b>层的来源是"内层压缩包"</b>，不是"文件夹看着像不像多余的壳"：
    /// <c>AAA.rar</c> 解出 <c>AAA\内容物</c> ⇒ <c>AAA</c> 这一层。⛔ 绝不许拿"这一层只有一个子文件夹"
    /// 当理由摊平 —— 用户原话："如果是一个文件夹 <c>1111</c> 里面包裹真正的内容物，这个时候你就会把
    /// <c>1111</c> 省略，这是非常大忌"。普通文件夹层一律保留，判据只认"这一层是不是内层包产出的"。</description></item>
    /// <item><description><b>第一层（源包包名目录）与最后一层（最后一个内层包那一层）永不可省</b>；
    /// 中间层按用户的选择（②页「续解时省略中间层」<c>OmitMiddleContinuationLayers</c>）决定。</description></item>
    /// <item><description><b>就地替换</b>：任何"按内容认出是压缩包"的文件（不管后缀多假）在**原来的位置**
    /// 解开，留下一个**以它命名的文件夹**（去掉假后缀的基名）放内容物；真文件原地不动。
    /// 例：<c>AAA\DDDD.mp4</c> ⇒ <c>AAA\DDDD\内容物</c>、<c>AAA\BBBB\CCCCC.mp4</c> ⇒
    /// <c>AAA\BBBB\CCCCC\内容物</c>。⛔ 不是"把它们都搬到顶层再各建一层"。</description></item>
    /// </list>
    ///
    /// <para>
    /// 这个类里的每一条都是**事实**，两个消费方（递归发布侧 <see cref="ExtractionWorkspace.Publish"/>、
    /// 定稿侧 <see cref="ResultFinalizer.Plan"/>、续解侧 <c>OneClickCoordinator</c>）读的是**同一份**实现，
    /// ⛔ 不许任何一处自己数层数、自己拼包基名、自己判"该不该加一层"（AGENTS.md §9.5 一条真值一个出口）。
    /// </para>
    /// </summary>
    internal static class PackageLayerRules
    {
        /// <summary>
        /// 最后一个**真的解开了**的内层包的归档路径；没有内层包（只解了一层）时是空串。
        ///
        /// <para>
        /// 判据只看**递归真的解开了几个包**：只有第 0 层（任务自己那个包）成功 ⇒ 没有内层包 ⇒ 空串，
        /// 所有消费方一律按"与加这条红线之前**逐字相同**"的口径走（那一份口径有很多既有用例钉着）。
        /// 失败的那一层不算"被展开的内层包"—— 它没产出任何东西。
        /// </para>
        /// </summary>
        public static string ResolveArchivePath(IReadOnlyList<RecursionLayerReport>? layers)
        {
            if (layers == null)
            {
                return string.Empty;
            }

            int expandedCount = 0;
            string lastArchivePath = string.Empty;

            foreach (RecursionLayerReport layer in layers)
            {
                if (!layer.Success)
                {
                    continue;
                }

                expandedCount++;
                lastArchivePath = layer.ArchivePath;
            }

            // 第 0 层就是任务自己那个包：只成功了一层 = 没有内层包，最里层无从谈起。
            return expandedCount > 1 ? lastArchivePath : string.Empty;
        }

        /// <summary>这一次递归到底展开了内层包没有（只解了一层 → false）。</summary>
        public static bool ExpandedInnerPackage(IReadOnlyList<RecursionLayerReport>? layers)
        {
            return ResolveArchivePath(layers).Length > 0;
        }

        /// <summary>
        /// 最里层那一层的名字来源 = 最后一个内层包的**包基名**。
        ///
        /// <para>
        /// 包名取法沿用**唯一**那一个实现 <see cref="OutputPlacement.ResolveArchiveBaseName"/>
        /// （分卷组取整组基名、伪装后缀接着剥、内嵌归档按可见名字取），这里只做转发，
        /// ⛔ 不再写第二份"怎么从路径取包名"。
        /// </para>
        /// </summary>
        public static string ResolveBaseName(IReadOnlyList<RecursionLayerReport>? layers)
        {
            string archivePath = ResolveArchivePath(layers);

            return archivePath.Length == 0
                ? string.Empty
                : OutputPlacement.ResolveArchiveBaseName(archivePath);
        }

        /// <summary>
        /// **就地替换**留给那一层文件夹的名字 = 内层包**去掉假后缀的基名**
        /// （用户原话："在它原来的位置留下一个以它命名的文件夹（去掉假后缀的基名）"）。
        ///
        /// <list type="bullet">
        /// <item><description>正常包：<c>DDDD.mp4</c> → <c>DDDD</c>、<c>小小绘 推特大合集.7z</c> →
        /// <c>小小绘 推特大合集</c>；</description></item>
        /// <item><description><b>分卷组一律返回空串</b>（"过程物名不成层"，用户 2026-09-27 红线）：
        /// 真机现场 <c>59768866.001/.002</c> 的包基名是**过程物**的名字、不是用户认得的包名 ——
        /// 拿它当一层就是 <c>…\包名\59768866\真内容</c> 那个怪目录；这一档的内容归上一层；</description></item>
        /// <item><description>名字被改坏、剥不出后缀（<c>user.jp删除g</c>）：基名就等于文件名本身 ——
        /// 调用方必须自己处理"这个名字被文件自己占着"这一物理约束
        /// （见 <c>OneClickCoordinator.IsOwnLayerOccupiedByPackageFile</c>）。</description></item>
        /// </list>
        ///
        /// <para>返回空串表示**不另建层**，内容归上一层 —— ⛔ 绝不硬造一个名字。</para>
        /// </summary>
        public static string ResolveInPlaceLayerName(string? archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return string.Empty;
            }

            string fileName = FileNameHelper.GetFileName(archivePath);

            if (fileName.Length == 0 || FileNameHelper.IsVolumePartFileName(fileName))
            {
                return string.Empty;
            }

            string name = OutputPlacement.ResolveArchiveBaseName(archivePath);

            return FileNameHelper.SanitizeFileName(name);
        }

        /// <summary>
        /// **就地替换**的落点：被解开的内层包在它**原来所在的那个目录**里留下一个
        /// <c>&lt;去掉假后缀的基名&gt;\</c>，内容物放进去（用户给的形状：<c>AAA\DDDD\内容物</c>、
        /// <c>AAA\BBBB\CCCCC\内容物</c>）。
        ///
        /// <para>
        /// ⛔ 与 <see cref="ResolveInPlaceLayerName"/> 的分工：这里只做"拼在哪"，
        /// "叫什么"由那一个方法给 —— 两处各写一份取名字规则必然漂移。
        /// </para>
        /// </summary>
        /// <param name="parentDirectory">内层包**现在**所在的那个目录（不是"顶层"，也不是父任务的落点）。</param>
        /// <param name="archivePath">内层包自己的路径。</param>
        /// <returns>取不出名字时返回 <paramref name="parentDirectory"/> 本身（不另建层，内容归上一层）。</returns>
        public static string ResolveInPlaceDirectory(string? parentDirectory, string? archivePath)
        {
            string directory = (parentDirectory ?? string.Empty).TrimEnd('\\', '/');

            if (directory.Length == 0)
            {
                return string.Empty;
            }

            string layerName = ResolveInPlaceLayerName(archivePath);

            return layerName.Length == 0
                ? directory
                : SafePathHelper.Combine(directory, layerName);
        }

        /// <summary>
        /// 内层包那一层**要不要建**（用户 2026-09-27 拍板、2026-09-30 收口到这里）。
        ///
        /// <para>规则按优先级三条（先命中先返回）：</para>
        /// <list type="number">
        /// <item><description><b>同一个父层这一轮认出多个内层包</b>（<paramref name="siblingCount"/> &gt; 1）→
        /// **必须建**：几个包各自一层，否则它们的内容物会倒进同一层里互相撞名
        /// （⛔ 不许"多个分支共用一个名字"）。</description></item>
        /// <item><description><b>父层自己产出了内容物</b>（<paramref name="parentProducedContent"/>）→
        /// **不建**：内层包解出来的东西**并进父层那一层**，与父层自己的内容物放在同一个目录里
        /// （2026-09-27 真机口径：`（T250）经济2\国考资料.txt` 旁边本该就是 `老王宣传\…`，
        /// 再套一层 `Sociology\` 会把真内容埋深一层）。</description></item>
        /// <item><description>剩下就是"**干净的单链过路层**"：忠实档建
        /// （<c>111\2222\33333\444444\5555555\内容物</c> 那条链就是它）、
        /// 简洁档省掉（<c>111\5555555\内容物</c>）。</description></item>
        /// </list>
        ///
        /// <para>
        /// ⚠ <b>末层永不省</b>不在这里判：那个事实只有在"链条走到头"之后才知道
        /// （见 <see cref="ShouldKeepLayerFolder"/>），判据仍只有这一处。
        /// </para>
        /// </summary>
        public static bool ShouldAddInnerPackageLayer(
            bool omitMiddleLayers,
            bool parentProducedContent,
            int siblingCount)
        {
            if (siblingCount > 1)
            {
                return true;
            }

            if (parentProducedContent)
            {
                return false;
            }

            return !omitMiddleLayers;
        }

        /// <summary>
        /// **末层永不省**（用户 2026-09-30 红线：第一层与最后一层绝对不能省，只能省中间层）。
        ///
        /// <para>
        /// "这一层还是中间层吗"这一个事实决定了它能不能被简洁档省掉：**只有还往下解出了内层包的层
        /// 才算中间层**。<paramref name="hasChildLayer"/> 为 false ⇒ 链到头了 ⇒ 它就是**最后一层**
        /// ⇒ 无条件保留自己的包名目录（这也是"内层包名 == 源包名"时照样两层的那条口径）。
        /// </para>
        ///
        /// <para>
        /// ⚠ 判据刻意收在这里、而不是让调用方各写一句 `if (!hasChild)`：三个消费方
        /// （工作区发布 / 定稿 / 续解）都读这一个方法，改口径只改一处。
        /// </para>
        /// </summary>
        public static bool ShouldKeepLayerFolder(
            bool omitMiddleLayers,
            bool parentProducedContent,
            int siblingCount,
            bool hasChildLayer)
        {
            // 末层（链上没有再往下的层）：无条件保留 —— 首尾必留里的"尾"。
            if (!hasChildLayer)
            {
                return true;
            }

            return ShouldAddInnerPackageLayer(omitMiddleLayers, parentProducedContent, siblingCount);
        }

        /// <summary>
        /// 这一层的产物里"除了交给下一层去解的那些内层包，还有没有别的东西" ——
        /// 也就是 <see cref="ShouldAddInnerPackageLayer"/> 里 <c>parentProducedContent</c> 那一个事实。
        ///
        /// <para>
        /// 判据只认**条目本身**：分层目录下有多少个条目、其中有几个正是本层认出来的内层包。
        /// ⛔ 不猜名字、不看后缀。读不了目录时按"出了内容物"处理（保守一侧：宁可多留一层，
        /// 也不要把用户的东西并进别人的目录里）。
        /// </para>
        /// </summary>
        /// <param name="layerOutputDirectory">这一层的产物目录。</param>
        /// <param name="innerArchiveCount">本层认出来、要交给下一层去解的内层包个数。</param>
        public static bool ProducedOwnContent(string? layerOutputDirectory, int innerArchiveCount)
        {
            if (string.IsNullOrWhiteSpace(layerOutputDirectory))
            {
                return false;
            }

            string[] entries;

            try
            {
                entries = System.IO.Directory.GetFileSystemEntries(layerOutputDirectory);
            }
            catch
            {
                return true;
            }

            return entries.Length > innerArchiveCount;
        }
    }
}
