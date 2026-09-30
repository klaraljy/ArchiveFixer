using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「**最后一个被展开的内层包**」—— 落点模型里"**最里层**"那一层的名字来源
    /// （用户 2026-09-30 真机红线：落点最少两层文件夹；最外层 = 以源包包名命名的目录、
    /// 最里层 = 以最后一个压缩包那一层命名的目录，**两层都不许省**）。
    ///
    /// <para>
    /// 这个事实**只在这里算一次**，两个消费方读的是同一个方法，⛔ 不许任何一处自己数层数、
    /// 自己拼包基名（§9.5 一条真值一个出口）：
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>递归发布侧</b>（<see cref="ExtractionWorkspace.Publish"/> 的
    /// <c>stripLeafWrapper</c>）：展开了内层包 ⇒ 叶子层产物里那**一个**文件夹就是内层包自己产出的
    /// 内容物那一层，⛔ 发布时不许把它当"解压器自动加的壳"摊掉
    /// （真机现场：<c>T 小小绘 推特大合集 330P+454V-9.31G</c> 就是在这一步丢的）。</description></item>
    /// <item><description><b>定稿侧</b>（<see cref="ResultFinalizer.Plan"/> 的
    /// <c>innermostPackageBaseName</c>）：展开了内层包 ⇒ <c>destDir</c> 里面**必须**还有一层，
    /// 判定表里任何"塌缩 / 不套层"的分支都不许吃掉它。</description></item>
    /// </list>
    ///
    /// <para>
    /// 判据只看**递归真的解开了几个包**：只有第 0 层（任务自己那个包）成功 ⇒ 没有内层包 ⇒ 空串，
    /// 两个消费方一律按"与加这条红线之前**逐字相同**"的口径走（那一份口径有很多既有用例钉着）。
    /// 失败的那一层不算"被展开的内层包"—— 它没产出任何东西。
    /// </para>
    /// </summary>
    internal static class InnermostPackageLayer
    {
        /// <summary>
        /// 最后一个**真的解开了**的内层包的归档路径；没有内层包（只解了一层）时是空串。
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
    }
}
