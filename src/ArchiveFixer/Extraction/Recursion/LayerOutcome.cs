using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>一层的执行结果：要么成功（带报告，可以继续），要么停下（带报告 + 停因）。</summary>
    internal sealed class LayerOutcome
    {
        internal RecursionLayerReport? Report { get; private init; }

        internal RecursionStopReason StopReason { get; private init; }

        internal static LayerOutcome Ok(RecursionLayerReport report)
        {
            return new LayerOutcome { Report = report, StopReason = RecursionStopReason.None };
        }

        internal static LayerOutcome Stop(RecursionLayerReport report, RecursionStopReason reason)
        {
            return new LayerOutcome { Report = report, StopReason = reason };
        }
    }
}
