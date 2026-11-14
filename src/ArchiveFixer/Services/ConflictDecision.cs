namespace ArchiveFixer.Services;

/// <summary>
/// 用户对同名冲突的一次决定。
///
/// <para>
/// <see cref="ApplyToAll"/> 是这套设计的**关键位**（决策 D-4：用户场景是 50–200+ 个包，
/// 逐个问等于不可用）：勾上"对后面所有同名冲突都照此办理"之后，本批内**不得再问第二次**，
/// 后续冲突一律照这个 Choice 办。
/// </para>
/// </summary>
public readonly struct ConflictDecision
{
    public ConflictDecision(ConflictChoice choice, bool applyToAll = false)
    {
        Choice = choice;
        ApplyToAll = applyToAll;
    }

    public ConflictChoice Choice { get; }

    /// <summary>true = 本批后续同名冲突一律照此办理（不再询问）。</summary>
    public bool ApplyToAll { get; }

    /// <summary>取消本批：调用方按既有取消语义处理，**不得**继续落位。</summary>
    public bool IsCancel => Choice == ConflictChoice.CancelBatch;

    public static ConflictDecision Once(ConflictChoice choice) => new(choice, applyToAll: false);

    public static ConflictDecision ForAll(ConflictChoice choice) => new(choice, applyToAll: true);

    /// <summary>
    /// 保守档：**没有界面宿主 / 没问到答案**时的兜底。
    ///
    /// 取"自动重命名"而不是"覆盖"：产物照样落地（不丢内容），旧文件一个字节都不动，
    /// 而且不需要谁来点确认 —— 这正是不变量 3 要的"默认不得覆盖"。
    /// </summary>
    public static ConflictDecision Conservative => Once(ConflictChoice.AutoRename);

    /// <summary>写日志用的一句话（必须能看出用户选了哪一档、是不是整批）。</summary>
    public string Describe() =>
        ApplyToAll ? $"全部{DescribeChoice(Choice)}（本批不再询问）" : DescribeChoice(Choice);

    public static string DescribeChoice(ConflictChoice choice) => choice switch
    {
        ConflictChoice.Skip => "跳过",
        ConflictChoice.Overwrite => "覆盖",
        ConflictChoice.AutoRename => "自动重命名",
        _ => "取消本批"
    };
}

