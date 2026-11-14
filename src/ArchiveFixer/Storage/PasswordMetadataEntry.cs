namespace ArchiveFixer.Storage;

public sealed class PasswordMetadataEntry
{
    public string Value { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public string Remark { get; set; } = string.Empty;
}
