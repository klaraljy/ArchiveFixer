namespace ArchiveFixer.Models
{
    /// <summary>
    /// 文件真实格式识别结果。
    /// 
    /// ArchiveDetectService 读取文件头后返回此对象。
    /// </summary>
    public class DetectResult
    {
        /// <summary>
        /// 检测到的格式。
        /// 
        /// 可选值：
        /// ZIP
        /// ZIP_EMPTY
        /// ZIP_SPANNED
        /// 7Z
        /// RAR4
        /// RAR5
        /// GZIP
        /// BZIP2
        /// XZ
        /// TAR
        /// Unknown
        /// </summary>
        public string Format { get; set; } = "Unknown";

        /// <summary>
        /// 建议后缀。
        /// 
        /// 例如：
        /// .zip
        /// .7z
        /// .rar
        /// .gz
        /// .bz2
        /// .xz
        /// .tar
        /// </summary>
        public string SuggestedExtension { get; set; } = string.Empty;

        /// <summary>
        /// 是否是支持的压缩包。
        /// </summary>
        public bool IsArchive { get; set; }

        /// <summary>
        /// 是否是已知格式。
        /// Unknown 时为 false。
        /// </summary>
        public bool IsKnownFormat { get; set; }

        /// <summary>
        /// 是否可能加密。
        /// 
        /// 注意：
        /// 仅靠文件头通常不能准确判断所有格式是否加密。
        /// 更准确的判断需要调用 7-Zip 测试输出。
        /// </summary>
        public bool IsProbablyEncrypted { get; set; }

        /// <summary>
        /// 识别消息。
        /// 可用于调试或显示。
        /// 不能包含明文密码。
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 文件头十六进制字符串。
        /// 可用于排查格式识别问题。
        /// 例如：
        /// 37 7A BC AF 27 1C
        /// </summary>
        public string HeaderHex { get; set; } = string.Empty;

        /// <summary>
        /// 识别置信度。
        /// 明确魔数识别一般为 100。
        /// 未知格式为 0。
        /// TAR 等弱特征可为 80 或 90。
        /// </summary>
        public int Confidence { get; set; }

        /// <summary>
        /// 创建 Unknown 结果。
        /// </summary>
        public static DetectResult Unknown(string message = "未知格式", string headerHex = "")
        {
            return new DetectResult
            {
                Format = "Unknown",
                SuggestedExtension = string.Empty,
                IsArchive = false,
                IsKnownFormat = false,
                IsProbablyEncrypted = false,
                Message = message,
                HeaderHex = headerHex,
                Confidence = 0
            };
        }

        /// <summary>
        /// 创建已知压缩格式结果。
        /// </summary>
        public static DetectResult Archive(
            string format,
            string suggestedExtension,
            string message = "",
            string headerHex = "",
            int confidence = 100,
            bool isProbablyEncrypted = false)
        {
            return new DetectResult
            {
                Format = string.IsNullOrWhiteSpace(format) ? "Unknown" : format,
                SuggestedExtension = suggestedExtension ?? string.Empty,
                IsArchive = true,
                IsKnownFormat = true,
                IsProbablyEncrypted = isProbablyEncrypted,
                Message = message ?? string.Empty,
                HeaderHex = headerHex ?? string.Empty,
                Confidence = confidence
            };
        }

        /// <summary>
        /// 创建已知但不一定作为压缩包处理的结果。
        /// 目前保留给后续扩展。
        /// </summary>
        public static DetectResult Known(
            string format,
            string suggestedExtension,
            bool isArchive,
            string message = "",
            string headerHex = "",
            int confidence = 90,
            bool isProbablyEncrypted = false)
        {
            return new DetectResult
            {
                Format = string.IsNullOrWhiteSpace(format) ? "Unknown" : format,
                SuggestedExtension = suggestedExtension ?? string.Empty,
                IsArchive = isArchive,
                IsKnownFormat = true,
                IsProbablyEncrypted = isProbablyEncrypted,
                Message = message ?? string.Empty,
                HeaderHex = headerHex ?? string.Empty,
                Confidence = confidence
            };
        }

        public override string ToString()
        {
            if (string.IsNullOrWhiteSpace(SuggestedExtension))
            {
                return $"{Format}，置信度 {Confidence}";
            }

            return $"{Format}，建议后缀 {SuggestedExtension}，置信度 {Confidence}";
        }
    }
}
