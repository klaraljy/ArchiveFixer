using System;

namespace ArchiveFixer.Services;

/// <summary>
/// **本次运行 / 本次操作的上下文**：运行 ID、运行目录、操作目录与操作身份、四份日志的当前路径、
/// 两个序号、文件日志开关，以及每一行都要抄进去的 <see cref="LogRunContext"/>。
///
/// <para>为什么单独一个类：这些字段以前散在 <see cref="LogService"/> 里，而**写它们的人**
/// （生命周期与诊断事件）和**读它们的人**（四份文件、JSONL、导出）本来就是两拨职责。
/// 拆开后只剩这一个可变状态的所有者，别的协作类全部按构造拿到**同一个实例** ——
/// "哪一份是当前这一份"仍然只有一个答案，也不会出现两个类各存一份路径、一处更新另一处没更新。</para>
///
/// <para>⛔ 它不写盘、不判任何业务。<see cref="SyncRoot"/> 就是拆分前 <c>LogService._lockObj</c> 的
/// **那同一个监视器**：屏幕集合、四份文件的追加、两个序号仍在同一把锁下串行 —— 锁对象换了位置，
/// 线程安全语义一个字都不能变（否则并发写出来的行会交错、`.log` 与 `.jsonl` 会不再一比一）。</para>
/// </summary>
internal sealed class LogOperationContext
{
    /// <summary>每次新建就是一次新运行（运行 ID 只在这里生成，拆分前是字段初始化器）。</summary>
    internal LogOperationContext()
    {
        RunId = Guid.NewGuid().ToString("N");
    }

    /// <summary>日志写入的唯一监视器（拆分前 <c>LogService._lockObj</c>；全仓只该有这一个）。</summary>
    internal object SyncRoot { get; } = new();

    /// <summary>本次进程运行 id（每次启动一个；日志只靠它就能区分"哪一次运行"）。</summary>
    internal string RunId { get; }

    /// <summary>本次运行目录（logs 根下唯一文件夹）。</summary>
    internal string RunDirectory { get; set; } = string.Empty;

    /// <summary>当前操作目录（每次操作一个子文件夹）。</summary>
    internal string OperationDirectory { get; set; } = string.Empty;

    /// <summary>本次运行里的操作 ID（运行 ID + 三次操作序号）。</summary>
    internal string OperationId { get; set; } = string.Empty;

    /// <summary>当前操作名（文件夹名与 manifest 用它）。</summary>
    internal string OperationName { get; set; } = string.Empty;

    /// <summary>本次运行里第几次操作（决定 <c>operations\NNN-名称-时间</c> 的名字）。</summary>
    internal int OperationIndex { get; set; }

    /// <summary>详细档 `.log`：人读、全量详细（内部排查用）。</summary>
    internal string MainLogPath { get; set; } = string.Empty;

    /// <summary>普通档 `.user.log`：只写普通行，不含详细探针。</summary>
    internal string UserLogPath { get; set; } = string.Empty;

    /// <summary>机器读 `.jsonl`：一行一个 JSON 对象，与人读 `.log` 一比一。</summary>
    internal string JsonlPath { get; set; } = string.Empty;

    /// <summary>诊断事件 `.trace.jsonl`：schema v2，一批一条 JSON，不占人读行。</summary>
    internal string TracePath { get; set; } = string.Empty;

    /// <summary>是否往文件里写日志（对应设置项「启用日志文件」，默认开）。</summary>
    internal bool EnableFileLog { get; set; } = true;

    /// <summary>本次操作内的日志序号（从 1 开始；人读/机器读同一条同号）。</summary>
    private long _sequence;

    /// <summary>诊断事件专用序号（不参与人读/JSONL 的一比一）。</summary>
    private long _traceSequence;

    /// <summary>
    /// **这一行的来源上下文**（批次 / 轮次 / 样本 / 阶段 / 任务 / 层号）—— 大测试的"只看日志排查"靠它。
    ///
    /// <para>写点：调用方开工时 <see cref="LogService.UpdateRunContext"/>；读点：JSONL 拼装把它抄进每一行。
    /// ⛔ 只影响**日志记录**，⛔ 不参与任何判据、⛔ 不改任何行为。</para>
    /// </summary>
    internal LogRunContext RunContext { get; set; } = new();

    /// <summary>
    /// 下一次写入用的日志序号。
    /// ⛔ 调用点必须与拆分前的 <c>++_sequence</c> 同一位置：只在 `.jsonl` 路径非空时才递增。
    /// </summary>
    internal long NextSequence() => ++_sequence;

    /// <summary>
    /// 下一条诊断事件用的序号。
    /// ⛔ 调用点必须与拆分前的 <c>++_traceSequence</c> 同一位置：只在 trace 路径非空时才递增。
    /// </summary>
    internal long NextTraceSequence() => ++_traceSequence;

    /// <summary>换一次操作：两个序号都从 0 重新开始（拆分前在 <c>BeginOperationFolder</c> 里直接赋值）。</summary>
    internal void ResetSequences()
    {
        _sequence = 0;
        _traceSequence = 0;
    }

    /// <summary>换一次上下文（只覆盖**显式给了**的那几格；判据一个字都不受影响）。</summary>
    internal void UpdateRunContext(
        string? batch,
        int? round,
        string? sample,
        string? stage,
        string? task,
        int? layer)
    {
        RunContext = RunContext with
        {
            Batch = batch ?? RunContext.Batch,
            Round = round ?? RunContext.Round,
            Sample = sample ?? RunContext.Sample,
            Stage = stage ?? RunContext.Stage,
            Task = task ?? RunContext.Task,
            Layer = layer ?? RunContext.Layer
        };
    }
}
