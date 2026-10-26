using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 整机级的外部引擎并发闸门（用户 2026-10-26：多个项目/多个实例一起跑时，
    /// 每个实例各开 8 个 7z 会把 USB 盘抢死，要求全机合计最多 2–4 个）。
    ///
    /// <para>用**命名信号量**跨进程共享：同一台机器上所有 ArchiveFixer 进程都抢同一个名额。
    /// 当前固定 4（在用户接受的 2–4 档内）；⛔ 这个数字不是“每实例并发”，而是**整机同时跑
    /// 7z/UnRAR/WinRAR 进程的总数上限**。</para>
    ///
    /// <para>只包外部引擎进程；不包内置 ZIP 直读、不包 UI 操作。等名额时用轮询 + 取消，
    /// 不阻塞 UI 线程；拿不到名额就是排队，不是失败。</para>
    /// </summary>
    internal static class GlobalExtractionThrottle
    {
        /// <summary>整机最多同时跑几个外部引擎进程。</summary>
        internal const int MaxConcurrentEngines = 4;

        private const string SemaphoreName = "ArchiveFixer.ExtractionThrottle";

        private static readonly Semaphore Gate = CreateGate();

        private static Semaphore CreateGate()
        {
            try
            {
                return new Semaphore(MaxConcurrentEngines, MaxConcurrentEngines, SemaphoreName);
            }
            catch
            {
                // 命名信号量创建失败（权限/会话限制）时至少保证本进程内限流，不能因此让解压起不来。
                return new Semaphore(MaxConcurrentEngines, MaxConcurrentEngines);
            }
        }

        /// <summary>
        /// 等一个外部引擎名额。取消时不抛异常：调用方（引擎运行器）的契约是"取消返回失败结果"，
        /// 直接放行一个不占名额的租约，让引擎自己按既有取消路径收口。
        /// </summary>
        internal static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new Lease(ownsSlot: false);
            }

            while (true)
            {
                if (Gate.WaitOne(0))
                {
                    return new Lease(ownsSlot: true);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new Lease(ownsSlot: false);
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class Lease : IDisposable
        {
            private readonly bool _ownsSlot;
            private int _released;

            public Lease(bool ownsSlot)
            {
                _ownsSlot = ownsSlot;
            }

            public void Dispose()
            {
                if (!_ownsSlot || Interlocked.Exchange(ref _released, 1) != 0)
                {
                    return;
                }

                try
                {
                    Gate.Release();
                }
                catch
                {
                    // 信号量已经失效（进程收尾/句柄被关）时不让收尾路径再炸一次。
                }
            }
        }
    }
}
