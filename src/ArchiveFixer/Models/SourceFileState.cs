using System;
using System.IO;

namespace ArchiveFixer.Models;

/// <summary>
/// 一条"源文件当时长什么样"的记录：路径 + 大小 + 修改时间 + <b>记的时候还在不在</b>。
///
/// <para>
/// "当时在不在"必须记下来，不能靠"文件不见了"倒推：文件在上一次快照时就已经不在时，
/// 正确的结论是"两次都一样（都不在）"，而不是"它刚刚消失了"。
/// 少了这个字段，一个从来就不存在的路径会被每次都报成"源文件已变化"。
/// </para>
/// </summary>
public sealed class SourceFileState
{
    /// <summary>记录时的完整路径（<see cref="Path.GetFullPath(string)"/> 规范化过；取不到时原样保留）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>记录时文件是否存在。<b>false</b> 时后面两项没有意义。</summary>
    public bool Exists { get; init; }

    /// <summary>记录时的字节数（不存在时为 0）。</summary>
    public long Length { get; init; }

    /// <summary>记录时的最后写入时间（UTC；不存在时为 <see cref="DateTime.MinValue"/>）。</summary>
    public DateTime LastWriteTimeUtc { get; init; }
}
