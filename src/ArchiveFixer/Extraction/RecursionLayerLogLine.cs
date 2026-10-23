using System.IO;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 递归**逐层一行日志**的唯一格式出口（从 `ExtractionCoordinator` 搬出的第 7 个功能族，2026-10-23）。
    ///
    /// <para>格式：`  ├ 第 N 层：包名 → 状态`，有细节时接「，细节」。⛔ 判据只用结构化字段
    /// （<see cref="RecursionLayerReport.Success"/> 决定级别由调用方给，这里只拼字）。</para>
    /// </summary>
    public static class RecursionLayerLogLine
    {
        public static string Format(RecursionLayerReport layer)
        {
            string name = Path.GetFileName(layer.ArchivePath ?? string.Empty);
            string detail = string.IsNullOrWhiteSpace(layer.Message) ? string.Empty : $"，{layer.Message}";

            return $"  ├ 第 {layer.Depth} 层：{name} → {layer.Status}{detail}";
        }
    }
}
