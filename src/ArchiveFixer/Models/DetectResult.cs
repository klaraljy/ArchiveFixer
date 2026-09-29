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
        /// 内嵌归档（"双面文件"）在源文件中的起始偏移，**0 表示没有内嵌归档**。
        ///
        /// 什么情况下会有值：文件头是不认识的格式，但尾部藏着一个完整的 ZIP
        /// （典型：网盘下载的 .mp4 前面是视频、后面拼了一整个 ZIP）。此时
        /// <see cref="Format"/> 为 ZIP、<see cref="SuggestedExtension"/> 为 .zip，
        /// 而 <see cref="Confidence"/> 只有 80 —— 因为它不是"文件开头就是 ZIP"，而是尾部推断出来的。
        ///
        /// 用途（两处，缺一不可）：
        /// 1. 后缀状态判成"内嵌归档"：这种文件**不该改名**（改成 .zip 之后 7z 照样打不开 ——
        ///    前置数据超过了它的容忍上限，改名只会让用户以为问题解决了）；
        /// 2. 解压管线按这个偏移把 [offset, EOF) 抠出来当真正的归档用
        ///    （ZIP 内部偏移相对它自己，抠出来偏移才对得齐）。
        /// </summary>
        public long EmbeddedArchiveOffset { get; init; }

        /// <summary>
        /// 内嵌归档的**结束位置（不含）**：<c>EOCD + 22 + 注释长度</c>；0 = 不知道，按文件末尾算。
        ///
        /// 与 <see cref="EmbeddedArchiveOffset"/> 成对使用：抠取范围是
        /// <c>[EmbeddedArchiveOffset, EmbeddedArchiveEnd)</c>。真实资源包在 EOCD 之后还有
        /// 十几 KB 正常数据（实测 14,350–17,424 字节），所以终点必须显式记下来，
        /// 不能再拿"文件末尾"当终点（那会把尾巴上的别的数据混进产物）。
        /// </summary>
        public long EmbeddedArchiveEnd { get; init; }

        /// <summary>
        /// 这个内嵌归档能不能**直读**（<c>Extraction/EmbeddedZipStreamExtractor</c> 的只读探查结论）。
        ///
        /// <para>识别阶段顺手探一次（只读、不写、不改源文件），结论供**空间核算**用：
        /// 直读不产生那份等大的临时副本，账面上就不该预留它（用户 2026-09-24 需求第 7 条）。</para>
        ///
        /// <para>⚠ 它只是**估算依据**，不是解压时的判据：真正解压前还会用同一个探查再问一次
        /// （源文件在两次动作之间可能变过），以那一刻的结论为准。</para>
        /// </summary>
        public bool EmbeddedDirectReadSupported { get; init; }

        /// <summary>直读不支持时的原因（<see cref="EmbeddedDirectReadSupported"/> 为 true 时为空）。</summary>
        public string EmbeddedDirectReadReason { get; init; } = string.Empty;

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

        /// <summary>
        /// 复制一份。
        ///
        /// <para>为什么必须有它：识别结论会进**进程内缓存**（同一份内容的第二个拷贝不再重算），
        /// 而缓存里那一份**绝不能被调用方改到** —— 后处理会往识别结果上叠东西
        /// （例如 RAR 加密判读改写 <see cref="Message"/> 与 <see cref="IsProbablyEncrypted"/>），
        /// 直接交出缓存对象等于让"下一次命中"读到被改过的那一份。</para>
        /// </summary>
        public DetectResult Clone() => new()
        {
            Format = Format,
            SuggestedExtension = SuggestedExtension,
            IsArchive = IsArchive,
            IsKnownFormat = IsKnownFormat,
            IsProbablyEncrypted = IsProbablyEncrypted,
            Message = Message,
            HeaderHex = HeaderHex,
            Confidence = Confidence,
            EmbeddedArchiveOffset = EmbeddedArchiveOffset,
            EmbeddedArchiveEnd = EmbeddedArchiveEnd,
            EmbeddedDirectReadSupported = EmbeddedDirectReadSupported,
            EmbeddedDirectReadReason = EmbeddedDirectReadReason
        };

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
