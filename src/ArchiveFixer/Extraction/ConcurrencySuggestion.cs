namespace ArchiveFixer.Extraction;

/// <summary>并发建议：只描述设备情况，不修改用户选择。</summary>
public readonly record struct ConcurrencySuggestion(int Suggested, string Text);
