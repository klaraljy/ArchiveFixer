namespace ArchiveFixer.Engines
{
    /// <summary>7-Zip 的原话关键字（三档；顺序 = 重要度，见 <see cref="EngineOutputKeywords"/>）。</summary>
    public static class SevenZipKeywords
    {
        /// <summary>引擎自己标出来的错行。</summary>
        public static readonly string[] ErrorLines = { "ERROR" };

        /// <summary>说出**原因**的行 —— 用户真正要看的就是这一档。</summary>
        public static readonly string[] ReasonLines =
        {
            "Wrong password",
            "Password is incorrect",
            "Enter password",
            "Can not get password",
            "Cannot get password",
            "CRC Failed",
            "CRC error",
            "Data Error",
            "Data error",
            "Headers Error",
            "Header Error",
            "Unexpected end",
            "Missing volume",
            "Can not open encrypted archive",
            "Cannot open encrypted archive",
            "Can not open",
            "Cannot open",
            "Is not archive",
            "is not archive",
            "Unsupported Method",
            "Unsupported method"
        };

        /// <summary>其它被识别的行（含警告、权限、取消、超时）。</summary>
        public static readonly string[] OtherLines =
        {
            "WARNING",
            "Warning",
            "Can not create",
            "Cannot create",
            "Access is denied",
            "Permission denied",
            "Command Line Error",
            "Incorrect command line",
            "Can not read",
            "Cannot read",
            "Break signaled",
            "User break",
            "User stopped",
            "Operation canceled",
            "Operation cancelled",
            "timed out",
            "timeout"
        };

        /// <summary>三档一次给全（挑行与排序的唯一顺序）。</summary>
        public static string[][] Buckets => new[] { ErrorLines, ReasonLines, OtherLines };
    }
}
