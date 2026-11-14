namespace ArchiveFixer.ViewModels;

/// <summary>忙闲嵌套计数与标志。</summary>
internal sealed class BusyStateCoordinator
{
    private readonly MainViewModel _vm;
    private int _busyNesting;

    public BusyStateCoordinator(MainViewModel vm)
    {
        _vm = vm;
    }

    internal void Enter()
    {
        _busyNesting++;

        if (_busyNesting == 1)
        {
            SetBusyFlag(true);
        }
    }

    internal void Exit()
    {
        if (_busyNesting <= 0)
        {
            _busyNesting = 0;
            return;
        }

        _busyNesting--;

        if (_busyNesting == 0)
        {
            SetBusyFlag(false);
        }
    }

    private void SetBusyFlag(bool value)
    {
        if (_vm.SetIsBusyRaw(value))
        {
            /*
             * 忙闲一变就重问所有命令的可用性：动磁盘的那一批要跟着灰 / 亮。
             * ⚠ 「移除勾选的」**不看忙闲**（判据 = 只看有没有勾选的，见 CanRemoveSelectedTasks），
             * 所以这里不再单独通知它的 ToolTip —— 那句话是常量，跟忙闲一个字都不差。
             */
            _vm.RaiseAllCommandCanExecuteChanged();
        }
    }
}

