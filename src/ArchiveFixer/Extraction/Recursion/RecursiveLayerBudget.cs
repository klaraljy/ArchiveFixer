using ArchiveFixer.Engines;
using ArchiveFixer.Security;

namespace ArchiveFixer.Extraction;

/// <summary>层内预算判据；只返回原因，不写日志、不构造结论。</summary>
internal static class RecursiveLayerBudget
{
    internal static string? FindSingleFileViolation(ArchiveListResult listed, long maxSingleFileSize)
    {
        (string path, long size) = ResourceBudget.FindLargestFileEntry(listed);

        return size > maxSingleFileSize
            ? ResourceBudget.DescribeSingleFileOverLimit(size, path, maxSingleFileSize)
            : null;
    }
}
