using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// UnRAR 的**超时取消**这一块：两道上限（连续无输出 / 总运行时长）与"这次到底是超时还是用户取消"。
///
/// <para>
/// 30 分钟是**连续无输出**的硬上限，不是"总运行时间"上限：大包在慢盘上可以跑很久，
/// 只要 UnRAR 还在吐进度，计时就被 <see cref="EngineOutputActivityMonitor.MarkActivity"/> 刷新。
/// 6 小时是另一道**绝对**上限：没有它的话，"卡在某个极慢的 I/O 上但偶尔有输出"可以无限拖住任务
/// （7-Zip 侧没有这一道，见 <c>SevenZipIdleWatch</c> 的说明）。
/// </para>
///
/// <para>本类**只取消令牌源、只做判定**，不碰进程：杀进程只在
/// <see cref="UnRarProcessSession"/> 的取消/超时分支里做
/// （经 <see cref="UnRarProcessTerminator"/>，且只针对自己启动的 PID）。</para>
/// </summary>
internal static class UnRarIdleWatch
{
    /// <summary>连续无引擎输出的硬上限（不是总运行时间；进度会刷新它）。</summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 绝对上限：即使一直有进度，也不允许一个进程跑超过 6 小时。
    /// 没有它的话，"卡在某个极慢的 I/O 上但偶尔有输出"可以无限拖住任务。
    /// </summary>
    internal static readonly TimeSpan MaxTotalTimeout = TimeSpan.FromHours(6);

    /// <summary>
    /// 30 分钟的**无输出**超时：只在连续 <paramref name="idleTimeout"/> 没有任何引擎输出时取消进程。
    /// 只要 UnRAR 还在吐进度，计时就被 <see cref="EngineOutputActivityMonitor.MarkActivity"/> 刷新。
    /// </summary>
    internal static async Task WatchAsync(
        EngineOutputActivityMonitor monitor,
        TimeSpan idleTimeout,
        CancellationTokenSource timeoutCts)
    {
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), timeoutCts.Token).ConfigureAwait(false);

                if (monitor.IdleFor >= idleTimeout)
                {
                    timeoutCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常收口 / 超时。
        }
        catch
        {
            // 看门狗自身出问题不允许影响解压结论。
        }
    }

    /// <summary>
    /// 取消是不是**超时**引起的：两道超时任一触发、且用户自己没取消。
    ///
    /// 这条判据决定结果里 <c>ExitCode</c> 是 -3（超时）还是 -2（用户取消），
    /// 也决定错误类型是 <see cref="EngineErrorTypes.TimedOut"/> 还是
    /// <see cref="EngineErrorTypes.Cancelled"/> —— 顺序与旧实现一致，不要改成先判用户取消。
    /// </summary>
    internal static bool IsTimeout(
        CancellationTokenSource timeoutCts,
        CancellationTokenSource absoluteTimeoutCts,
        CancellationToken cancellationToken)
    {
        bool timedOut = timeoutCts.IsCancellationRequested || absoluteTimeoutCts.IsCancellationRequested;

        return timedOut && !cancellationToken.IsCancellationRequested;
    }
}
