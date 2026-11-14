namespace ArchiveFixer.Services;

/// <summary>
/// 一次冲突的解析结论：**怎么处理** + **落到哪个路径**。
///
/// 把它和"显示用的字符串"分开，是因为旧 API 只返回一个路径字符串 —— 于是"跳过"和"覆盖"
/// 都只能靠"路径没变"来表达，调用方分不清两者，最终就退化成了"反正一样，都走自动改名"。
/// </summary>
public readonly struct ConflictResolution
{
    public ConflictResolution(ConflictChoice choice, string targetPath, bool conflicted, bool decidedByUser)
    {
        Choice = choice;
        TargetPath = targetPath;
        Conflicted = conflicted;
        DecidedByUser = decidedByUser;
    }

    public ConflictChoice Choice { get; }

    /// <summary>按结论该用的落点：Skip / Overwrite 时就是原路径，AutoRename 时是 <c>名字(1)</c>。</summary>
    public string TargetPath { get; }

    /// <summary>解析时目标确实已存在（false = 没有冲突，原样使用 <see cref="TargetPath"/>）。</summary>
    public bool Conflicted { get; }

    /// <summary>Ask 档是否真的拿到了用户的答案（false = 走了保守兜底）。</summary>
    public bool DecidedByUser { get; }
}

