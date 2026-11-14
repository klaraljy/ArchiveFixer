namespace ArchiveFixer.Services;

/// <summary>一处判据的四个结构化字段（唯一出口 <see cref="Criterion"/>）。</summary>
public sealed record CriterionFields(
    string Criterion,
    string Inputs,
    string Verdict,
    string Action,
    string File,
    string Identity);

