using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 清理源包的结果。
/// 删除是**不可逆**操作，所以这里把"有没有真的动手"和"动了什么"分开记：
/// <see cref="Attempted"/> = false 时调用方可以明确告诉用户"源文件一个都没碰"。
/// </summary>
public sealed class SourceCleanupResult
{
    /// <summary>是否真的尝试删除（未开启 / 校验未通过 / 没有可删的路径时为 false）。</summary>
    public bool Attempted { get; init; }

    public IReadOnlyList<string> DeletedFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FailedFiles { get; init; } = Array.Empty<string>();

    public long FreedBytes { get; init; }

    public string Message { get; init; } = string.Empty;
}

