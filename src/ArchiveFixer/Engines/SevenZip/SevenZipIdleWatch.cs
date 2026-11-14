using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 7-Zip 的**无输出超时**这一块：连续一段时间没有任何引擎输出就取消进程。
///
/// <para>30 分钟是**连续无输出**的硬上限，不是“总运行时间”上限：
/// 大包在慢盘上可以跑很久，只要 7-Zip 还在吐进度，就不该在第 30 分钟被当成卡死杀掉。
/// 真机 2026-10-26：8 个大包并发、盘被抢满，几个包 30 分钟只到 1%–9%，全被总时长超时误杀。</para>
///
/// <para>本类**只取消一个令牌源**，不碰进程：杀进程只在运行器的取消/超时分支里做
/// （经 <see cref="SevenZipProcessTerminator"/>，且只针对自己启动的 PID）。</para>
/// </summary>
internal static class SevenZipIdleWatch
{
    /// <summary>连续无引擎输出的硬上限（不是总运行时间；进度会刷新它）。</summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 只在连续 <paramref name="idleTimeout"/> 没有任何引擎输出时取消进程。
    /// 只要 7-Zip 还在吐进度，计时就被 <see cref="EngineOutputActivityMonitor.MarkActivity"/> 刷新。
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
}
