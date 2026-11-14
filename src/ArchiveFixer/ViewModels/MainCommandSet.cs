using System.Windows.Input;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// MainViewModel 的命令目录；命令对象在此声明，VM 只保留同名薄转发。
/// </summary>
internal sealed class MainCommandSet
{
    public ICommand FindVolumeSiblingsCommand { get; set; } = null!;
    public ICommand AddFilesCommand { get; set; } = null!;
    public ICommand AddFolderCommand { get; set; } = null!;
    public ICommand AppendFilesCommand { get; set; } = null!;
    public ICommand AppendFolderCommand { get; set; } = null!;
    public ICommand ScanCommand { get; set; } = null!;
    public ICommand ClearCommand { get; set; } = null!;
    public ICommand RemoveSelectedCommand { get; set; } = null!;
    public ICommand SmartRenameCommand { get; set; } = null!;
    public ICommand ChangeNameCommand { get; set; } = null!;
    public ICommand StartExtractCommand { get; set; } = null!;
    public ICommand ExtractIntoSourceFolderCommand { get; set; } = null!;
    public ICommand StopCommand { get; set; } = null!;
    public ICommand CancelCurrentCommand { get; set; } = null!;
    public ICommand PauseAllOperationsCommand { get; set; } = null!;
    public ICommand OpenSettingsCommand { get; set; } = null!;
    public ICommand OpenPasswordListCommand { get; set; } = null!;
    public ICommand OpenPackingCommand { get; set; } = null!;
    public ICommand ImportPasswordBookCommand { get; set; } = null!;
    public ICommand ExportLogCommand { get; set; } = null!;
    public ICommand ExportDiagnosticBundleCommand { get; set; } = null!;
    public ICommand ExportAllLogsCommand { get; set; } = null!;
    public ICommand CopyFailedListCommand { get; set; } = null!;
    public ICommand ExportFailedListCommand { get; set; } = null!;
    public ICommand OpenOutputDirectoryCommand { get; set; } = null!;
    public ICommand SelectOutputDirectoryCommand { get; set; } = null!;
    public ICommand CopyOutputLocationCommand { get; set; } = null!;
    public ICommand OpenLogDirectoryCommand { get; set; } = null!;
    public ICommand OpenWorkDirectoryCommand { get; set; } = null!;
    public ICommand ClearWorkspaceLeftoversCommand { get; set; } = null!;
    public ICommand CleanProcessArtifactsCommand { get; set; } = null!;
    public ICommand CleanAllProcessArtifactsCommand { get; set; } = null!;
    public ICommand CleanEmptyFoldersCommand { get; set; } = null!;
    public ICommand RemoveTaskCommand { get; set; } = null!;
    public ICommand RemoveCheckedTasksCommand { get; set; } = null!;
    public ICommand RenameBySuggestionAndRetryCommand { get; set; } = null!;
    public ICommand RescanTaskCommand { get; set; } = null!;
    public ICommand CopyTaskInfoCommand { get; set; } = null!;
    public ICommand CopyTaskPathCommand { get; set; } = null!;
    public ICommand CopyTaskErrorCommand { get; set; } = null!;
    public ICommand OpenTaskDirectoryCommand { get; set; } = null!;
    public ICommand OpenCheckedSourceDirectoryCommand { get; set; } = null!;
    public ICommand InspectArchiveCommand { get; set; } = null!;
    public ICommand TryPasswordsCommand { get; set; } = null!;
    public ICommand InspectCurrentTaskCommand { get; set; } = null!;
    public ICommand TryPasswordsForCurrentTaskCommand { get; set; } = null!;
    public ICommand OpenCurrentSourceDirectoryCommand { get; set; } = null!;
    public ICommand SmartRenameCurrentTaskCommand { get; set; } = null!;
    public ICommand ChangeNameCurrentTaskCommand { get; set; } = null!;
    public ICommand RepairVolumeNameCurrentTaskCommand { get; set; } = null!;
    public ICommand StartExtractCurrentTaskCommand { get; set; } = null!;
    public ICommand ExtractIntoSourceFolderCurrentTaskCommand { get; set; } = null!;
    public ICommand OpenCurrentTaskOutputDirectoryCommand { get; set; } = null!;
    public ICommand CopyCurrentVolumeGroupPathsCommand { get; set; } = null!;
    public ICommand AddCurrentOutputFolderToTasksCommand { get; set; } = null!;
    public ICommand CleanCurrentTaskArtifactsCommand { get; set; } = null!;
    public ICommand RecycleCurrentTaskArtifactsCommand { get; set; } = null!;
    public ICommand CleanCurrentTaskEmptyFoldersCommand { get; set; } = null!;
    public ICommand OpenTaskOutputDirectoryCommand { get; set; } = null!;
    public ICommand OpenTaskArtifactDirectoryCommand { get; set; } = null!;
    public ICommand ToggleShowPasswordCommand { get; set; } = null!;
    public ICommand OneClickProcessCommand { get; set; } = null!;
    public ICommand ContinueOneClickCommand { get; set; } = null!;
    public ICommand ResetSettingsCommand { get; set; } = null!;
    public ICommand RefreshParallelAdviceCommand { get; set; } = null!;
    public ICommand SelectAllTasksCommand { get; set; } = null!;
    public ICommand SelectNoneTasksCommand { get; set; } = null!;
    public ICommand InvertTaskSelectionCommand { get; set; } = null!;
    public ICommand SelectSoleTaskCommand { get; set; } = null!;
    public void RaiseAll()
    {
        foreach (ICommand command in new[]
                 {
             AddFilesCommand,
             AddFolderCommand,
             AppendFilesCommand,
             AppendFolderCommand,
             ScanCommand,
             ClearCommand,
             RemoveSelectedCommand,

             SmartRenameCommand,
            OneClickProcessCommand,
             ChangeNameCommand,

             StartExtractCommand,
             ExtractIntoSourceFolderCommand,
             StopCommand,
             CancelCurrentCommand,
             PauseAllOperationsCommand,

             SelectAllTasksCommand,
             SelectNoneTasksCommand,
             InvertTaskSelectionCommand,
             SelectSoleTaskCommand,

             /*
              * 「移除勾选的」（用户 2026-09-29 第二次反馈：判据对了界面也不动）。
              * 它以前**不在**这份名单里，而 RelayCommand 自己不发 CanExecuteChanged ——
              * WPF 只在 CommandManager.RequerySuggested（焦点 / 输入变化）时才重问，
              * 于是"勾上一个任务"之后按钮要等下一次焦点变化才亮，用户看到的就是"先亮着不动"。
              * 这份名单由 UpdateSummary 统一发一遍（勾选变化 / 任务增删 / 忙闲切换都会走到它），
              * ⛔ 不许在别处再写一套通知。
              */
             RemoveCheckedTasksCommand,

             OpenSettingsCommand,
             OpenPasswordListCommand,
             OpenPackingCommand,
             ExportLogCommand,
             ExportAllLogsCommand,
             CopyFailedListCommand,
             OpenOutputDirectoryCommand,
             SelectOutputDirectoryCommand,
             CopyOutputLocationCommand,
             OpenLogDirectoryCommand,
             ResetSettingsCommand,
             CleanProcessArtifactsCommand,
             CleanAllProcessArtifactsCommand,
             CleanEmptyFoldersCommand,

             RemoveTaskCommand,
             RescanTaskCommand,
             RenameBySuggestionAndRetryCommand,
             InspectCurrentTaskCommand,
             TryPasswordsForCurrentTaskCommand,
             OpenCurrentSourceDirectoryCommand,
             SmartRenameCurrentTaskCommand,
             ChangeNameCurrentTaskCommand,
             RepairVolumeNameCurrentTaskCommand,
             StartExtractCurrentTaskCommand,
             ExtractIntoSourceFolderCurrentTaskCommand,
             OpenCurrentTaskOutputDirectoryCommand,
             CopyCurrentVolumeGroupPathsCommand,
             AddCurrentOutputFolderToTasksCommand,
             CleanCurrentTaskArtifactsCommand,
             RecycleCurrentTaskArtifactsCommand,
             CleanCurrentTaskEmptyFoldersCommand,
             CopyTaskInfoCommand,
             CopyTaskPathCommand,
             CopyTaskErrorCommand,
             OpenTaskDirectoryCommand,
             OpenTaskOutputDirectoryCommand,
             ToggleShowPasswordCommand
         })
        {
            if (command is RelayCommand relay)
            {
                relay.RaiseCanExecuteChanged();
            }
            else if (command is AsyncRelayCommand asyncRelay)
            {
                asyncRelay.RaiseCanExecuteChanged();
            }
        }
    }
}

