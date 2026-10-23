using System;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// UI 线程投递与确认/警告框（第 14 轮③：从 <c>ExtractionCoordinator.UiThreadStep.cs</c> 搬成独立类；
    /// 它碰 WPF，所以落在 GUI 层的 Services，⛔ 不进 Extraction）。
    ///
    /// <para>两条纪律（都对应实测踩过的坑）：① 投递一律用 <c>BeginInvoke</c>，同步 <c>Invoke</c> 会让
    /// "读进程输出"等"UI 处理完"，UI 忙的时候互相拖死；② <c>Application.Current</c> 为 null
    /// （单元测试 / 控制台宿主）时就地执行，既不抛也不死等。</para>
    /// </summary>
    internal sealed class UiThreadStep
    {
        private readonly DialogService _dialogs;

        internal UiThreadStep(DialogService dialogs)
        {
            _dialogs = dialogs;
        }

        /// <summary>
        /// 在 UI 线程上弹确认框（引擎调用用了 <c>ConfigureAwait(false)</c> / <c>Task.Run</c>，
        /// 管线拿到结论时已经不在 UI 线程上；直接 MessageBox.Show 会弹出一个没人泵消息的窗）。
        /// </summary>
        internal Task<bool> ShowConfirmAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return Task.FromResult(_dialogs.ShowConfirm(message));
            }

            return dispatcher.InvokeAsync(() => _dialogs.ShowConfirm(message)).Task;
        }

        /// <summary>在 UI 线程上弹警告（异步、不阻塞调用线程；没有 WPF 应用时只留日志）。</summary>
        internal Task ShowWarningAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return Task.CompletedTask;
            }

            if (dispatcher.CheckAccess())
            {
                _dialogs.ShowWarning(message);
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => _dialogs.ShowWarning(message)).Task;
        }

        /// <summary>把一段动作投递到 UI 线程执行（异步、不阻塞、无界面宿主也不抛）。</summary>
        internal static void PostToUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                TryRunSafely(action);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch
            {
                // 调度器正在关闭（关窗口时）：丢弃这一次进度，绝不影响引擎调用。
            }
        }

        /// <summary>动作本身抛异常一律吞掉：界面侧的进度显示失败与解压结论无关。</summary>
        internal static void TryRunSafely(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // 界面侧的进度显示失败与解压结论无关。
            }
        }
    }
}
