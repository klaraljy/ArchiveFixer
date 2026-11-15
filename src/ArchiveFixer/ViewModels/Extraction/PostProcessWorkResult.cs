using ArchiveFixer.Extraction;
using System.Collections.Generic;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 收尾重活的产物：后台线程只负责碰文件系统，结论与日志都通过它回传给 UI 线程，
/// 免得后台线程去动界面集合 / 任务状态。
///
/// <para>⚠ 类型从 <c>ExtractionCoordinator</c> 的 <c>PostProcessSuccessStep</c> partial
/// 搬出来单独成文件（M5 类型总行数治理：那个 partial 已删除）。产出它的
/// <c>SourceHandler.RunPostProcessWork</c> 与消费它的 <c>PostProcessSuccessHandler</c>
/// 都在 <c>ArchiveFixer.ViewModels</c> 命名空间，直接引用本类型，⛔ 不再经协调器嵌套类型。</para>
/// </summary>
internal sealed class PostProcessWorkResult
{
    public OutputVerificationResult Verification { get; init; } = new();

    public List<(string Level, string Message)> LogEntries { get; init; } = new();

    public bool BudgetExceeded { get; init; }

    public string BudgetMessage { get; init; } = string.Empty;

    /// <summary>
    /// 产物越出目标根目录的说明（非 null = **不承认**这次解压：不归集、不清理源包）。
    /// 它必须走"结论"这条路回传，而不是像旧实现那样只往日志里塞一行 ERROR。
    /// </summary>
    public string? LandingViolation { get; init; }

    public CollectResult? Collected { get; init; }

    /// <summary>定稿（暂存区 → 最终目录）的结果；没走到定稿这一步时为 null。</summary>
    public StageCommitResult? Commit { get; init; }

    /// <summary>
    /// 定稿**整体失败**（一个内容物都没能搬进最终目录）：与越界同一口径 —— 结论不成立。
    /// 与 <see cref="LandingViolation"/> 分开写，是为了让状态与原因各自说自己的事，
    /// 别让"越界"这个词出现在一句讲搬运失败的提示里。
    /// </summary>
    public bool CommitFailed { get; init; }

    public string CommitFailureMessage { get; init; } = string.Empty;

    /// <summary>
    /// 源包没能移入其余物（非 null = **任务要标成"部分完成"**）。
    ///
    /// 与越界/预算同一口径地走"结论"这条路回传：内容物已经好了，这件事只影响任务的完成度，
    /// 所以它**不**顶掉"解压成功"，但也绝不允许被静默吞掉（D-12：移动失败要写明原因）。
    /// </summary>
    public string? SourceMoveFailure { get; init; }
}
