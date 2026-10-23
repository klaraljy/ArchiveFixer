using System;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「产物越出目标根目录」这条结论的**唯一措辞出口**（从 `ExtractionCoordinator` 搬出的第一个方法，2026-10-23）。
    ///
    /// <para>判据只读结构化事实 <see cref="RecursionLayerReport.LandingViolation"/>（⛔ 不比中文文案）；
    /// 措辞与单层路那一句逐字对齐（两条路同一句，用户才看得出是同一件事）。</para>
    /// </summary>
    public static class LandingViolationNote
    {
        /// <summary>这一趟递归里有没有任何一层是"产物越出本层产物目录"。</summary>
        public static bool HasLandingViolation(RecursionResult? result) =>
            result?.Layers != null
            && result.Layers.Any(layer => layer != null && layer.LandingViolation);

        /// <summary>结论正文：越界那条事实放在最前面，后面接原有原因（可为空）。</summary>
        public static string Compose(string? existingMessage) =>
            "产物越出目标根目录 —— 按既定口径整包判定失败（不归集产物、不处理源包、其余物不生成）："
            + (existingMessage ?? string.Empty);
    }
}
