using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Extraction
{
    /// <summary>候选之间的停止判据；只回答“停不停”和日志，不碰账本。</summary>
    internal static class RecursiveStopPolicy
    {
        internal static bool ShouldStop(int tried, Func<bool>? stopRequested)
            => tried > 0 && stopRequested?.Invoke() == true;

        internal static string BuildMessage(string layerLabel, int remaining)
            => string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RecursionStoppedByStopRequestLogFormat,
                layerLabel,
                Math.Max(0, remaining));
    }
}
