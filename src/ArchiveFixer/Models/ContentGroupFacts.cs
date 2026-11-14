namespace ArchiveFixer.Models;

/// <summary>
/// 这一单所属**内容组的最终结果事实**（组身份 / 组判决 / 组内角色 / 组最终落点）。
///
/// <para>从 <see cref="ArchiveTask"/> 抽出的协作类（「结果」那一块）：组级字段只由
/// <c>ExtractionCoordinator.FinalizeContentGroups</c> 写，界面那几列的派生判断
/// （是否成组 / 组内容是否已证 / 输出目录显示哪一层）收在这里，⛔ 不另判一遍。
/// </para>
///
/// <para><b>失败模型</b>：全部是空串 / 枚举判空，没有 IO、没有异常路径；组身份为空 ⇒ 一切派生判断
/// 按"普通单包"说老口径（与拆分前逐字相同）。</para>
///
/// <para>生命周期：随任务创建；<see cref="Apply"/> 是唯一一处"一次写全组"的入口（协调器组判决回写）；
/// 单个字段也可能被直接赋值（拆分前的公开可写属性语义不变，⛔ 这里不做归一化），没有清空出口 ——
/// 任务对象复用时由协调器整组重写。</para>
/// </summary>
internal sealed class ContentGroupFacts
{
    /// <summary>这一单属于哪个内容组（空 = 普通单包，不参与组级最终内容口径）。</summary>
    internal string Id { get; set; } = string.Empty;

    internal string BaseName { get; set; } = string.Empty;

    /// <summary>组最终内容三态；空组/未判决 = Unknown。</summary>
    internal FinalContentState FinalState { get; set; } = FinalContentState.Unknown;

    /// <summary>这一单是组内的过程任务（入口/中间层），不计最终成功。</summary>
    internal bool IsProcessTask { get; set; }

    /// <summary>只有组最终内容任务才有值；过程任务为空。</summary>
    internal string FinalContentPath { get; set; } = string.Empty;

    /// <summary>组唯一最终输出根；只有最终内容任务才有值。</summary>
    internal string FinalOutputPath { get; set; } = string.Empty;

    internal bool HasGroup => !string.IsNullOrWhiteSpace(Id);

    internal bool Verified =>
        HasGroup
        && !IsProcessTask
        && FinalState == FinalContentState.Verified;

    /// <summary>列表「输出目录」列：组内过程任务不再显示自己的中间落点。</summary>
    internal string ResolveOutputDisplayPath(string outputPath) =>
        !HasGroup
            ? outputPath
            : IsProcessTask
                ? "—（组内过程任务）"
                : !string.IsNullOrWhiteSpace(FinalOutputPath)
                    ? FinalOutputPath
                    : outputPath;

    /// <summary>组判决回写（一次写全六个字段；原公开方法 <c>ArchiveTask.ApplyContentGroupFacts</c> 的唯一实现）。</summary>
    internal void Apply(
        string? groupId,
        string? baseName,
        FinalContentState state,
        bool isProcessTask,
        string? finalContentPath,
        string? finalOutputPath)
    {
        Id = groupId ?? string.Empty;
        BaseName = baseName ?? string.Empty;
        FinalState = state;
        IsProcessTask = isProcessTask;
        FinalContentPath = finalContentPath ?? string.Empty;
        FinalOutputPath = finalOutputPath ?? string.Empty;
    }
}
