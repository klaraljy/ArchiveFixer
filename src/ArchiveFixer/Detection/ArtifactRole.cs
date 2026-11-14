namespace ArchiveFixer.Detection;

/// <summary>
/// 账本项在“源根 → 产出物 → 内容组 → 最终内容”链路里的角色。
/// <para>⛔ 这是归属事实，不是“处理状态”；判不出就保持 <see cref="Unknown"/>。</para>
/// </summary>
public enum ArtifactRole
{
    Unknown = 0,

    /// <summary>用户导入的源文件（第一大步的一个源根）。</summary>
    SourceRoot = 1,

    /// <summary>可能是某一组分卷、交界前不许回收的候选片。</summary>
    CandidateVolumePiece = 2,

    /// <summary>解出来的内层归档；可能继续递归，也可能成为内容组入口。</summary>
    InnerArchive = 3,

    /// <summary>已确认只用于中间处理、不会成为最终内容的过程物。</summary>
    ProcessArtifact = 4,

    /// <summary>可进入最终内容审判的内容物。</summary>
    Content = 5
}
