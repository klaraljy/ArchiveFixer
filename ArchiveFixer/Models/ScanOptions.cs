namespace ArchiveFixer.Models
{
    /// <summary>
    /// 扫描选项。
    /// 传给 FileScanService 使用。
    /// </summary>
    public class ScanOptions
    {
        /// <summary>
        /// 是否递归扫描文件夹。
        /// </summary>
        public bool RecursiveScan { get; set; } = true;

        /// <summary>
        /// 扫描模式。
        /// 
        /// ScanAllFiles
        /// ScanKnownArchiveExtensions
        /// ScanSuspiciousFiles
        /// </summary>
        public string ScanMode { get; set; } = "ScanAllFiles";

        /// <summary>
        /// 是否包含隐藏文件。
        /// 默认 false。
        /// </summary>
        public bool IncludeHiddenFiles { get; set; } = false;

        /// <summary>
        /// 是否包含系统文件。
        /// 默认 false。
        /// </summary>
        public bool IncludeSystemFiles { get; set; } = false;

        /// <summary>
        /// 最大文件大小限制，单位 MB。
        /// 0 表示不限制。
        /// </summary>
        public long MaxFileSizeLimit { get; set; } = 0;

        /// <summary>
        /// 从 AppSettings 创建扫描选项。
        /// </summary>
        public static ScanOptions FromSettings(AppSettings settings)
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            return new ScanOptions
            {
                RecursiveScan = settings.RecursiveScan,
                ScanMode = settings.ScanMode,
                IncludeHiddenFiles = settings.IncludeHiddenFiles,
                IncludeSystemFiles = settings.IncludeSystemFiles,
                MaxFileSizeLimit = settings.MaxFileSizeLimit
            };
        }

        /// <summary>
        /// 修正非法配置。
        /// </summary>
        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(ScanMode))
            {
                ScanMode = "ScanAllFiles";
            }

            if (ScanMode != "ScanAllFiles" &&
                ScanMode != "ScanKnownArchiveExtensions" &&
                ScanMode != "ScanSuspiciousFiles")
            {
                ScanMode = "ScanAllFiles";
            }

            if (MaxFileSizeLimit < 0)
            {
                MaxFileSizeLimit = 0;
            }
        }
    }
}
