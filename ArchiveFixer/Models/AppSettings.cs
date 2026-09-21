using System;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 应用程序设置。
    /// 对应 appsettings.json。
    /// </summary>
    public class AppSettings
    {
        public bool RecursiveScan { get; set; } = true;

        public string ScanMode { get; set; } = "ScanAllFiles";

        public string UnknownFormatAction { get; set; } = "MarkUnknown";

        public string DefaultExtension { get; set; } = ".7z";

        public bool ExtractToOriginalDirectory { get; set; } = true;

        public string CustomOutputDirectory { get; set; } = string.Empty;

        public bool KeepArchiveNameFolder { get; set; } = true;

        public string ConflictAction { get; set; } = "AutoRename";

        public bool TestBeforeExtract { get; set; } = false;

        public bool EnableLog { get; set; } = true;

        public bool AutoScanAfterDrop { get; set; } = true;

        public bool PreviewBeforeRename { get; set; } = true;

        public string OverwriteMode { get; set; } = "SkipExisting";

        public bool TryEmptyPasswordFirst { get; set; } = true;

        public bool UseGlobalPasswordForAllTasks { get; set; } = true;

        public int MaxParallelExtractCount { get; set; } = 1;

        public bool RememberLastOutputDirectory { get; set; } = true;

        public bool IncludeHiddenFiles { get; set; } = false;

        public bool IncludeSystemFiles { get; set; } = false;

        public long MaxFileSizeLimit { get; set; } = 0;

        public bool PreservePasswordLeadingTrailingSpaces { get; set; } = true;

        public static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                RecursiveScan = true,
                ScanMode = "ScanAllFiles",
                UnknownFormatAction = "MarkUnknown",
                DefaultExtension = ".7z",
                ExtractToOriginalDirectory = true,
                CustomOutputDirectory = string.Empty,
                KeepArchiveNameFolder = true,
                ConflictAction = "AutoRename",
                TestBeforeExtract = false,
                EnableLog = true,
                AutoScanAfterDrop = true,
                PreviewBeforeRename = true,
                OverwriteMode = "SkipExisting",
                TryEmptyPasswordFirst = true,
                UseGlobalPasswordForAllTasks = true,
                MaxParallelExtractCount = 1,
                RememberLastOutputDirectory = true,
                IncludeHiddenFiles = false,
                IncludeSystemFiles = false,
                MaxFileSizeLimit = 0,
                PreservePasswordLeadingTrailingSpaces = true
            };
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(ScanMode))
            {
                ScanMode = "ScanAllFiles";
            }

            if (string.IsNullOrWhiteSpace(UnknownFormatAction))
            {
                UnknownFormatAction = "MarkUnknown";
            }

            if (string.IsNullOrWhiteSpace(DefaultExtension))
            {
                DefaultExtension = ".7z";
            }

            if (!DefaultExtension.StartsWith(".", StringComparison.Ordinal))
            {
                DefaultExtension = "." + DefaultExtension;
            }

            if (string.IsNullOrWhiteSpace(ConflictAction))
            {
                ConflictAction = "AutoRename";
            }

            if (string.IsNullOrWhiteSpace(OverwriteMode))
            {
                OverwriteMode = "SkipExisting";
            }

            if (MaxParallelExtractCount < 1)
            {
                MaxParallelExtractCount = 1;
            }

            if (MaxParallelExtractCount > 8)
            {
                MaxParallelExtractCount = 8;
            }

            if (MaxFileSizeLimit < 0)
            {
                MaxFileSizeLimit = 0;
            }

            CustomOutputDirectory ??= string.Empty;
        }
    }
}
