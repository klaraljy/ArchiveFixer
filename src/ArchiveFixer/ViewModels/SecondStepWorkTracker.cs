using System;
using System.Threading;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 第二大步「当前工作」（解析 / 试开 / 命名）的取消源容器。
    ///
    /// <para>抽成独立类型只有一个理由：这条取消语义必须能被**单独钉住**（开始 → 取消 → 再开始），
    /// 不依赖构造整个 <see cref="ExtractionCoordinator"/>（它要 MainViewModel / 路径服务 / 全局静态）。
    /// 「停止当前任务 / 暂停所有操作」取消的是这里；「停止后续」不碰它（只停后续，不停当前）。</para>
    /// </summary>
    internal sealed class SecondStepWorkTracker
    {
        private CancellationTokenSource? _cts;

        internal SecondStepWorkScope Begin()
        {
            Cancel();

            var cts = new CancellationTokenSource();
            _cts = cts;

            return new SecondStepWorkScope(cts.Token, () =>
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                }

                try
                {
                    cts.Dispose();
                }
                catch
                {
                    // 释放失败不影响收尾。
                }
            });
        }

        /// <summary>取消当前这一档工作；返回"这次是否真的从运行态翻到取消态"。</summary>
        internal bool Cancel()
        {
            CancellationTokenSource? cts = _cts;

            if (cts == null || cts.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
