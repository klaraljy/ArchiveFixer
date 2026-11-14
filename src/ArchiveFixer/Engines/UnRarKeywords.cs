namespace ArchiveFixer.Engines;

/// <summary>UnRAR 的原话关键字（三档，与 7-Zip 侧同一份规则）。</summary>
public static class UnRarKeywords
{
    public static readonly string[] ErrorLines = { "ERROR" };

    public static readonly string[] ReasonLines =
    {
        "Incorrect password",
        "Wrong password",
        "Enter password",
        "Cannot find volume",
        "Can not find volume",
        "Missing volume",
        "checksum error",
        "Checksum error",
        "CRC error",
        "CRC failed",
        "Unexpected end",
        "Cannot open",
        "Can not open"
    };

    public static readonly string[] OtherLines =
    {
        "WARNING",
        "Warning",
        "Cannot create",
        "Can not create",
        "Access is denied",
        "Permission denied",
        "is not RAR archive",
        "Total errors",
        "Unknown option",
        "timed out",
        "timeout"
    };

    public static string[][] Buckets => new[] { ErrorLines, ReasonLines, OtherLines };
}
