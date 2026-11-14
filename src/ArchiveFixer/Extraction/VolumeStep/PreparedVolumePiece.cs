using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction.VolumeStep;

/// <summary>
/// **标准名 -> 原始片**的一条映射。
///
/// <para>标准名只在临时组入口目录里存在；原始片是用户源文件或本链产出的片。
/// 源包删除/落点/接片一律读 <see cref="OriginalPath"/>，⛔ 不碰临时链接。</para>
/// </summary>
public sealed class PreparedVolumePiece
{
    public string CanonicalName { get; init; } = string.Empty;

    public string OriginalPath { get; init; } = string.Empty;

    public long Size { get; init; }

    public VolumeHandoffSource Source { get; init; }
}
