using System;
using System.Threading.Tasks;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 启动期的磁盘读取兜底：**有界等待 + 失败吃默认值**。
    ///
    /// <para>
    /// 背景（2026-11-10 真机）：外接盘坏掉时，程序在启动期读一个很小的 json 就卡在内核 I/O 里，
    /// 整个 UI 线程被拖死 —— X 点不动、托盘退出无效、任务管理器也结束不掉。
    /// </para>
    ///
    /// <para>
    /// ⚠ 这里**不能**承诺"中止读盘"：线程一旦进入内核等待，`CancellationToken` 也停不掉它
    /// （见 dotnet/runtime 里 Process/File 阻塞读的老问题）。这里能做的是**不再等**：
    /// 超时就把默认值交出去，让窗口照常显示、照常关闭。
    /// </para>
    ///
    /// <para>
    /// 后台任务走 <see cref="Task.Run(Func{TResult})"/>，用的是线程池线程（background），
    /// 所以主窗口关掉后进程不会被它拖住退不出去。
    /// </para>
    /// </summary>
    internal static class SafeIo
    {
        /// <summary>默认只等这么久。启动期的小文件读取正常都在几毫秒内完成。</summary>
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// 在后台线程执行 <paramref name="work"/>，最多等 <paramref name="timeout"/>；
        /// 超时或抛异常一律返回 <paramref name="fallback"/>。
        /// </summary>
        internal static T Run<T>(Func<T> work, T fallback, TimeSpan? timeout = null, Action<string>? onProblem = null)
        {
            if (work == null)
            {
                return fallback;
            }

            try
            {
                Task<T> task = Task.Run(work);
                if (!task.Wait(timeout ?? DefaultTimeout))
                {
                    onProblem?.Invoke("读盘超时，按默认值继续");
                    return fallback;
                }

                return task.Result;
            }
            catch (Exception ex)
            {
                onProblem?.Invoke("读盘失败，按默认值继续：" + ex.GetType().Name);
                return fallback;
            }
        }

        /// <summary>不关心返回值的版本。</summary>
        internal static void Run(Action work, TimeSpan? timeout = null, Action<string>? onProblem = null)
        {
            if (work == null)
            {
                return;
            }

            Run<bool>(
                () =>
                {
                    work();
                    return true;
                },
                false,
                timeout,
                onProblem);
        }
    }
}
