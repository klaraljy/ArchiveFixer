using System;
using System.Threading;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 第二大步"当前工作"的生存期句柄（解析 / 试开 / 命名）。
    /// Dispose 时解绑并释放取消源；<see cref="Token"/> 交给 ResolveAllAsync / 引擎试开。
    /// </summary>
    internal sealed class SecondStepWorkScope : IDisposable
    {
        private readonly Action _onDispose;

        private int _disposed;

        public SecondStepWorkScope(CancellationToken token, Action onDispose)
        {
            Token = token;
            _onDispose = onDispose ?? throw new ArgumentNullException(nameof(onDispose));
        }

        public CancellationToken Token { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _onDispose();
        }
    }
}
