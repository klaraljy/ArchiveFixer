namespace ArchiveFixer.Models
{
    /// <summary>
    /// 解压选项。
    /// 由 AppSettings 和界面选项转换而来，传给归档引擎（IArchiveEngine.ExtractAsync）使用。
    /// </summary>
    public class ExtractOptions
    {
        /// <summary>
        /// 是否解压到压缩包所在目录。
        /// true：使用压缩包当前所在目录。
        /// false：使用 CustomOutputDirectory。
        /// </summary>
        public bool ExtractToOriginalDirectory { get; set; } = true;

        /// <summary>
        /// 自定义输出目录。
        /// ExtractToOriginalDirectory = false 时使用。
        /// </summary>
        public string CustomOutputDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 是否保留压缩包同名文件夹。
        /// 
        /// 例如：
        /// test.7z -> test\
        /// </summary>
        public bool KeepArchiveNameFolder { get; set; } = true;

        /// <summary>
        /// 解压前是否先执行 7z t 测试。
        /// </summary>
        public bool TestBeforeExtract { get; set; } = false;

        /// <summary>
        /// 覆盖策略。
        /// 
        /// SkipExisting        -> -aos
        /// OverwriteAll        -> -aoa
        /// AutoRenameExtracted -> -aou
        /// AutoRenameExisting  -> -aot
        /// </summary>
        public string OverwriteMode { get; set; } = "SkipExisting";

        /// <summary>
        /// 是否使用统一密码。
        /// </summary>
        public bool UseGlobalPassword { get; set; } = true;

        /// <summary>
        /// 统一密码。
        /// 仅内存使用，不能写入日志。
        /// </summary>
        public string GlobalPassword { get; set; } = string.Empty;

        /// <summary>
        /// 是否尝试密码列表。
        /// </summary>
        public bool TryPasswordList { get; set; } = true;

        /// <summary>
        /// 是否优先尝试空密码。
        /// </summary>
        public bool TryEmptyPasswordFirst { get; set; } = true;

        /// <summary>
        /// 密码成功后是否停止继续尝试。
        /// 默认 true。
        /// </summary>
        public bool CancelOnFirstSuccess { get; set; } = true;

        /// <summary>
        /// 遇到 Unknown 格式时是否尝试解压。
        /// 默认 false，更安全。
        /// </summary>
        public bool TryExtractUnknownFormat { get; set; } = false;

        /// <summary>
        /// 最大并发解压数。
        /// 第十一版默认串行，值为 1。
        /// </summary>
        public int MaxParallelExtractCount { get; set; } = 1;

        /// <summary>
        /// 从 AppSettings 创建解压选项。
        /// </summary>
        public static ExtractOptions FromSettings(AppSettings settings, string globalPassword = "")
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            return new ExtractOptions
            {
                ExtractToOriginalDirectory = settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = settings.CustomOutputDirectory,
                KeepArchiveNameFolder = settings.KeepArchiveNameFolder,
                TestBeforeExtract = settings.TestBeforeExtract,
                OverwriteMode = settings.OverwriteMode,
                UseGlobalPassword = settings.UseGlobalPasswordForAllTasks,
                GlobalPassword = globalPassword ?? string.Empty,
                TryPasswordList = true,
                TryEmptyPasswordFirst = settings.TryEmptyPasswordFirst,
                CancelOnFirstSuccess = true,
                TryExtractUnknownFormat = settings.UnknownFormatAction == "TryExtract",
                MaxParallelExtractCount = settings.MaxParallelExtractCount < 1
                    ? 1
                    : settings.MaxParallelExtractCount
            };
        }

        /// <summary>
        /// 获取 7-Zip 覆盖参数。
        /// </summary>
        public string GetOverwriteArgument()
        {
            return OverwriteMode switch
            {
                "OverwriteAll" => "-aoa",
                "AutoRenameExtracted" => "-aou",
                "AutoRenameExisting" => "-aot",
                "SkipExisting" => "-aos",
                _ => "-aos"
            };
        }

        /// <summary>
        /// 修正非法配置。
        /// </summary>
        public void Normalize()
        {
            CustomOutputDirectory ??= string.Empty;
            GlobalPassword ??= string.Empty;

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
        }
    }
}
