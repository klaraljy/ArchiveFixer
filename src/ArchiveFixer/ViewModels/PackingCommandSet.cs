using System.Windows.Input;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// ⑤打包页的命令目录：命令对象在这里创建，<see cref="PackingViewModel"/> 只保留同名薄转发。
///
/// <para>可用性判据仍读 ViewModel 自己的状态（<c>IsRunning</c>）——这里收的只是命令实例与
/// "重问一次"那一处：跑批开始 / 结束时 <see cref="RaiseRunStateCommands"/> 发一遍，
/// 免得界面停在上一轮的灰 / 亮状态上。</para>
/// </summary>
internal sealed class PackingCommandSet
{
    internal PackingCommandSet(PackingViewModel viewModel)
    {
        BrowseFolderCommand = new RelayCommand(viewModel.BrowseSourceFolder);
        BrowseFileCommand = new RelayCommand(viewModel.BrowseSourceFile);
        PickPlacementDirectoryCommand = new RelayCommand(viewModel.PickPlacementDirectory);
        RefreshSummaryCommand = new RelayCommand(viewModel.RefreshSummary);
        StartCommand = new AsyncRelayCommand(viewModel.StartAsync, () => !viewModel.IsRunning);
        CancelCommand = new RelayCommand(viewModel.Cancel, () => viewModel.IsRunning);
        CopyPasswordCommand = new RelayCommand(viewModel.CopyPassword);
        OpenOutputFolderCommand = new RelayCommand(viewModel.OpenOutputFolder);
    }

    public ICommand BrowseFolderCommand { get; }

    public ICommand BrowseFileCommand { get; }

    public ICommand PickPlacementDirectoryCommand { get; }

    public ICommand RefreshSummaryCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand CopyPasswordCommand { get; }

    public ICommand OpenOutputFolderCommand { get; }

    /// <summary>跑批开始 / 结束时重问「开始」与「取消」的可用性。</summary>
    internal void RaiseRunStateCommands()
    {
        (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
