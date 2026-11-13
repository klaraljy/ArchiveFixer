using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Extraction
{
    /// <summary>层诊断事实的回传口；只决定"这一层是否已报过"，不写日志。</summary>
    internal static class RecursiveLayerDiagnostics
    {
        internal static bool ReportDangerousEntriesOnce(
            bool alreadyReported,
            bool reportDangerousEntries,
            Action<string>? dangerousEntriesReported,
            ArchiveListResult listed)
        {
            if (alreadyReported)
            {
                return true;
            }

            dangerousEntriesReported?.Invoke(
                ViewModels.ExtractionCoordinator.AnalyzeDangerousEntries(
                    listed.Entries,
                    reportDangerousEntries));

            return true;
        }
    }
}
