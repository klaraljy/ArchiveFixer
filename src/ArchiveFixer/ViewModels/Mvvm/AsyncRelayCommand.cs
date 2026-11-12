using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 异步命令。
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute)
        {
            _execute = _ => execute();
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
        {
            _execute = _ => execute();
            _canExecute = _ => canExecute();
        }

        public AsyncRelayCommand(Func<object?, Task> execute)
        {
            _execute = execute;
        }

        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (_isExecuting)
            {
                return false;
            }

            return _canExecute?.Invoke(parameter) ?? true;
        }

        /// <summary>
        /// ICommand 的入口。`Execute` 的签名只能是 <c>void</c>，所以这里**不能**写成 <c>async Task</c>；
        /// 按微软对 async void 的口径，真正的异步体放进 <see cref="RunAsync"/>，并且**自己兜住异常**——
        /// 否则异常只能靠 <c>DispatcherUnhandledException</c> 兜底（用户看到的是崩溃提示）。
        /// </summary>
        public void Execute(object? parameter)
        {
            _ = RunAsync(parameter);
        }

        private async Task RunAsync(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                // 命令里抛出来的异常进日志，不再往 UI 线程上冒。
                ArchiveFixer.Helpers.AppDiagnostics.Problem("命令", "执行业务命令", ex);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
