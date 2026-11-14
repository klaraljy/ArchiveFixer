using ArchiveFixer.Storage;

namespace ArchiveFixer.Packing;

/// <summary>
/// 一个分卷（7-Zip 自己切出来的那一个文件）。
///
/// <para>名字由 7-Zip 产生（<c>-v</c> → <c>名字.7z.001/.002/…</c>），
/// 这里只**认**磁盘上的结果，不自己拼 —— 自己拼会在"内容物恰好是分卷大小的整数倍"
/// 这种边界上多切一卷出来（docs/打包功能.md §2）。</para>
/// </summary>
public sealed class PackingVolume
{
    /// <summary>文件名（含 <c>.7z.001</c> 这一段，不带目录）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>全路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>分卷序号（<c>.001</c> → 1）；认不出来时是 0。</summary>
    public int Index { get; init; }

    /// <summary>字节数（读不到时是 0）。</summary>
    public long Bytes { get; init; }

    public override string ToString()
    {
        return $"{FileName}（{TaskSpaceEstimate.FormatSize(Bytes)}）";
    }
}
