using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「这一趟的产物到底在哪个目录」的唯一出口（从 `ExtractionCoordinator` 搬出的第 2 个方法，2026-10-23）。
    ///
    /// <para><b>口径</b>（用户 2026-10-14 / 2026-10-22）：链停在某一层时，上面已经成功那一层的产物
    /// 要按「部分完成」发布；停下那一层按③页「删除操作」档处理。
    /// ⛔ 只看结构化事实（`Completed` / `Success`），不比文案、不猜层号。</para>
    /// </summary>
    public static class ChainProductSource
    {
        /// <summary>
        /// 产物来源目录：链**没跑完**且上面有成功层 ⇒ **最后一个成功层**的产物目录；
        /// 单层路（<paramref name="recursion"/> 为空）与跑完的链 ⇒ 原样返回 <paramref name="stageDirectory"/>。
        /// </summary>
        public static string Resolve(RecursionResult? recursion, string stageDirectory)
        {
            if (recursion == null || recursion.Completed)
            {
                return stageDirectory;
            }

            RecursionLayerReport? lastSuccess = recursion.Layers?
                .LastOrDefault(layer => layer != null && layer.Success);

            return lastSuccess != null && !string.IsNullOrWhiteSpace(lastSuccess.OutputPath)
                ? lastSuccess.OutputPath
                : stageDirectory;
        }
    }
}
