namespace ArchiveFixer.Detection;

public sealed record FileContentProbeResult(
    FileContentClass Class,
    string Format,
    string Evidence,
    bool IsContentContainer = false,
    long MediaEnd = 0)
{
    public static readonly FileContentProbeResult Unknown = new(
        FileContentClass.Unknown,
        string.Empty,
        string.Empty);
}
