namespace ArchiveFixer.Storage;

/// <summary>一次"上次没跑完留下的空壳"的处置结论（只描述结论，不碰界面 —— <c>Storage</c> 不引用 WPF）。</summary>
public sealed class WorkspaceLeftoverShellOutcome
{
    /// <summary>这一次任务的目标目录（判据只在这一层看，⛔ 不往下递归、也不看别的目录）。</summary>
    public string TargetDirectory { get; init; } = string.Empty;

    /// <summary>那个工作区目录（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）；没找到时为空串。</summary>
    public string WorkspaceDirectory { get; init; } = string.Empty;

    /// <summary>怎么处置的。</summary>
    public WorkspaceLeftoverShellAction Action { get; init; }

    /// <summary>这一条要不要写进日志（没脚印那一档一律不写：没有事实可说，写了只是噪音）。</summary>
    public bool ShouldReport => Action != WorkspaceLeftoverShellAction.NoFootprint;

    /// <summary>
    /// 日志级别：**收掉了 = INFO**（顺手活，正常）；
    /// **发现了但没清理 = WARN**（用户可能想自己看一眼，不该埋进 INFO 里）。
    /// </summary>
    public string LogLevel => Action == WorkspaceLeftoverShellAction.LeftIntact ? "WARN" : "INFO";

    /// <summary>给日志的一句话（含真实路径与"为什么没清理"）。没脚印时为空串。</summary>
    public string Message { get; init; } = string.Empty;
}
