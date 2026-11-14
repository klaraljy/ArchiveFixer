namespace ArchiveFixer.Extraction;

/// <summary>一次试开的结论（<see cref="Failure"/> 为 null = 成立）。</summary>
internal readonly struct VolumeProbeTrialResult
{
    internal VolumeProbeTrialResult(string? failure, bool encryptedArchive)
    {
        Failure = failure;
        EncryptedArchive = encryptedArchive;
    }

    /// <summary>失败原因；null = 这一次成立。</summary>
    internal string? Failure { get; }

    /// <summary>引擎的结论是"这一组是一份**文件名也加密**的归档（7z <c>-mhe</c> / RAR <c>-hp</c>）"。</summary>
    internal bool EncryptedArchive { get; }
}
