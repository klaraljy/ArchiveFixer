namespace ArchiveFixer.Extraction
{
    public sealed class SpecificExtractionRunResult
    {
        public bool Success { get; init; }

        public string OutputPath { get; init; } = string.Empty;

        public int Depth { get; init; }

        public string RestDirectory { get; init; } = string.Empty;

        /// <summary>本次实际密码结论（密码正确 / 不需要密码 / 需要密码 / 密码错误）；空 = 判不出。</summary>
        public string PasswordStatus { get; init; } = string.Empty;

        /// <summary>失败时的结构化诊断（层号、入口、卷数、分类），只进日志，不参与判据。</summary>
        public string DiagnosticDetail { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;
    }
}
