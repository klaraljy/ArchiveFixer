using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **一片的证据**（第二大步发现层内部用）：这一片的内容事实 + 它是不是首片 + 大小/自述。
///
/// <para>已登记过交接单的片直接装交接单里的底层事实（⛔ 不重读盘）；只有盘上补进来的、
/// 没登记过的兄弟片才装一次 <see cref="FileNatureCard"/> 的结果。卷号/片数允许为空/0（判不出），
/// ⛔ 不在这里补。</para>
///
/// <para><b>生命周期</b>：纯数据、不可变；由 <see cref="VolumeCandidateEvidence.BuildFacts"/> 产出，
/// 在"定序 → 判完整性"这一轮里只读传递，不落盘、不缓存。</para>
/// </summary>
internal sealed record VolumeCandidatePieceFacts(
    string Path,
    VolumeContentFormat Format,
    int? VolumeNumber,
    bool IsFirstPiece,
    long Size,
    int DeclaredPieceCount,
    long DeclaredTotalBytes,
    VolumeHandoffEntry? Handoff,
    FileNatureCard Card);
