using System;
using System.Threading;

namespace ArchiveFixer.Models;

/// <summary>
/// 一次运行的停止令牌：UI 按停止只把这一位从 0 翻到 1；跑批循环只读它，
/// 不再依赖会被协调器中途复位的 <c>IsStopping</c>。
///
/// <para>2026-11-11：这里同时提供 <see cref="CancellationToken"/>。
/// 理由是可取消的工作不只是"任务"——第二大步的解析/试开/命名也属于"当前正在执行"，
/// 只给一个 bool 的结果就是"暂停对所有操作"在这三分钟里完全无效（真机日志 00:00:45/00:00:48）。</para>
/// </summary>
public sealed class RunStopToken : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private int _requested;

    /// <summary>给引擎/解析管线用的取消令牌（试开里的 ThrowIfCancellationRequested / TestAsync 都认它）。</summary>
    public CancellationToken Token => _cts.Token;

    public bool IsStopRequested => Volatile.Read(ref _requested) != 0;

    public void RequestStop()
    {
        Interlocked.Exchange(ref _requested, 1);

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 运行已经收尾：停止位留着，取消不再需要。
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Dispose();
        }
        catch
        {
            // 释放失败不影响收尾。
        }
    }
}
