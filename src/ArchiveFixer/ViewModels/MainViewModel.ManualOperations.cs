using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels;

public partial class MainViewModel
{
    public bool HasSelectedTask => _selectedTask != null;

    /// <summary>当前行有任务，或只勾选了一个任务时，手动操作入口可用。</summary>
    public bool CanOpenManualOperation =>
        SelectedTask != null || Tasks.Count(task => task != null && task.IsSelected) == 1;

    private ArchiveTask? _manualDistributionTask;

    private void SubscribeManualDistributionTask(ArchiveTask? task)
    {
        if (_manualDistributionTask != null)
        {
            _manualDistributionTask.PropertyChanged -= ManualDistributionTask_PropertyChanged;
        }

        _manualDistributionTask = task;

        if (task != null)
        {
            task.PropertyChanged += ManualDistributionTask_PropertyChanged;
        }
    }

    private void ManualDistributionTask_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArchiveTask.OutputPath)
            or nameof(ArchiveTask.CurrentPath)
            or nameof(ArchiveTask.Status)
            or nameof(ArchiveTask.StatusDisplayText)
            or nameof(ArchiveTask.Outcome))
        {
            RefreshManualDistribution();
        }
    }

    /// <summary>
    /// 手动弹窗顶部提示：单包告诉用户按步骤走；分卷组说明入口包与落点。
    /// </summary>
    public string ManualGuideText
    {
        get
        {
            ArchiveTask? task = SelectedTask;

            if (task == null)
            {
                return "先在主界面选中一个任务。";
            }

            if (task.VolumePaths.Count > 1 || task.IsVolumeGroup || task.IsVolumeGroupFollower)
            {
                string entry = task.VolumePaths.Count > 0
                    ? task.VolumePaths[0]
                    : task.CurrentPath;

                return "分卷组：入口包是 "
                    + Path.GetFileName(entry)
                    + "。先看内容 / 试密码，再只解压这一层；解压会落到入口包所在目录。";
            }

            return "单一压缩包：建议按 1 看内容 → 2 试密码 → 3 只解压这一层 → 4 打开输出目录 的顺序操作。";
        }
    }

    // 手动操作弹窗里的“目录分布”：只读参考，不参与操作目标判定。
    public string ManualDistributionTitle { get; private set; } = "文件分布";

    public string ManualDistributionPath { get; private set; } = string.Empty;

    public string ManualDistributionEmptyText { get; private set; } = string.Empty;

    public IReadOnlyList<ManualDistributionEntry> ManualDistributionEntries { get; private set; }
        = Array.Empty<ManualDistributionEntry>();

    /// <summary>手动弹窗打开时按基名推测的同组文件（只读候选，不直接移动/删除）。</summary>
    public IReadOnlyList<ManualDistributionEntry> ManualVolumeCandidates { get; private set; }
        = Array.Empty<ManualDistributionEntry>();

    public string ManualVolumeSearchText { get; private set; } = "尚未查找同组文件。";

    public ICommand FindVolumeSiblingsCommand => Commands.FindVolumeSiblingsCommand;
    public ICommand AddFilesCommand => Commands.AddFilesCommand;
    public ICommand AddFolderCommand => Commands.AddFolderCommand;
    public ICommand AppendFilesCommand => Commands.AppendFilesCommand;
    public ICommand AppendFolderCommand => Commands.AppendFolderCommand;
    public ICommand ScanCommand => Commands.ScanCommand;
    public ICommand ClearCommand => Commands.ClearCommand;
    public ICommand RemoveSelectedCommand => Commands.RemoveSelectedCommand;
    public ICommand SmartRenameCommand => Commands.SmartRenameCommand;
    public ICommand ChangeNameCommand => Commands.ChangeNameCommand;
    public ICommand StartExtractCommand => Commands.StartExtractCommand;
    public ICommand ExtractIntoSourceFolderCommand => Commands.ExtractIntoSourceFolderCommand;
    public ICommand StopCommand => Commands.StopCommand;
    public ICommand CancelCurrentCommand => Commands.CancelCurrentCommand;
    public ICommand PauseAllOperationsCommand => Commands.PauseAllOperationsCommand;
    public ICommand OpenSettingsCommand => Commands.OpenSettingsCommand;
    public ICommand OpenPasswordListCommand => Commands.OpenPasswordListCommand;
    public ICommand OpenPackingCommand => Commands.OpenPackingCommand;
    public ICommand ImportPasswordBookCommand => Commands.ImportPasswordBookCommand;
    public ICommand ExportLogCommand => Commands.ExportLogCommand;
    public ICommand ExportDiagnosticBundleCommand => Commands.ExportDiagnosticBundleCommand;
    public ICommand ExportAllLogsCommand => Commands.ExportAllLogsCommand;
    public ICommand CopyFailedListCommand => Commands.CopyFailedListCommand;
    public ICommand ExportFailedListCommand => Commands.ExportFailedListCommand;
    public ICommand OpenOutputDirectoryCommand => Commands.OpenOutputDirectoryCommand;
    public ICommand SelectOutputDirectoryCommand => Commands.SelectOutputDirectoryCommand;
    public ICommand CopyOutputLocationCommand => Commands.CopyOutputLocationCommand;
    public ICommand OpenLogDirectoryCommand => Commands.OpenLogDirectoryCommand;
    public ICommand OpenWorkDirectoryCommand => Commands.OpenWorkDirectoryCommand;
    public ICommand ClearWorkspaceLeftoversCommand => Commands.ClearWorkspaceLeftoversCommand;
    public ICommand CleanProcessArtifactsCommand => Commands.CleanProcessArtifactsCommand;
    public ICommand CleanAllProcessArtifactsCommand => Commands.CleanAllProcessArtifactsCommand;
    public ICommand CleanEmptyFoldersCommand => Commands.CleanEmptyFoldersCommand;
    public ICommand RemoveTaskCommand => Commands.RemoveTaskCommand;
    public ICommand RemoveCheckedTasksCommand => Commands.RemoveCheckedTasksCommand;
    public ICommand RenameBySuggestionAndRetryCommand => Commands.RenameBySuggestionAndRetryCommand;
    public ICommand RescanTaskCommand => Commands.RescanTaskCommand;
    public ICommand CopyTaskInfoCommand => Commands.CopyTaskInfoCommand;
    public ICommand CopyTaskPathCommand => Commands.CopyTaskPathCommand;
    public ICommand CopyTaskErrorCommand => Commands.CopyTaskErrorCommand;
    public ICommand OpenTaskDirectoryCommand => Commands.OpenTaskDirectoryCommand;
    public ICommand OpenCheckedSourceDirectoryCommand => Commands.OpenCheckedSourceDirectoryCommand;
    public ICommand InspectArchiveCommand => Commands.InspectArchiveCommand;
    public ICommand TryPasswordsCommand => Commands.TryPasswordsCommand;
    public ICommand InspectCurrentTaskCommand => Commands.InspectCurrentTaskCommand;
    public ICommand TryPasswordsForCurrentTaskCommand => Commands.TryPasswordsForCurrentTaskCommand;
    public ICommand OpenCurrentSourceDirectoryCommand => Commands.OpenCurrentSourceDirectoryCommand;
    public ICommand SmartRenameCurrentTaskCommand => Commands.SmartRenameCurrentTaskCommand;
    public ICommand ChangeNameCurrentTaskCommand => Commands.ChangeNameCurrentTaskCommand;
    public ICommand RepairVolumeNameCurrentTaskCommand => Commands.RepairVolumeNameCurrentTaskCommand;
    public ICommand StartExtractCurrentTaskCommand => Commands.StartExtractCurrentTaskCommand;
    public ICommand ExtractIntoSourceFolderCurrentTaskCommand => Commands.ExtractIntoSourceFolderCurrentTaskCommand;
    public ICommand OpenCurrentTaskOutputDirectoryCommand => Commands.OpenCurrentTaskOutputDirectoryCommand;
    public ICommand CopyCurrentVolumeGroupPathsCommand => Commands.CopyCurrentVolumeGroupPathsCommand;
    public ICommand AddCurrentOutputFolderToTasksCommand => Commands.AddCurrentOutputFolderToTasksCommand;
    public ICommand CleanCurrentTaskArtifactsCommand => Commands.CleanCurrentTaskArtifactsCommand;
    public ICommand RecycleCurrentTaskArtifactsCommand => Commands.RecycleCurrentTaskArtifactsCommand;
    public ICommand CleanCurrentTaskEmptyFoldersCommand => Commands.CleanCurrentTaskEmptyFoldersCommand;
    public ICommand OpenTaskOutputDirectoryCommand => Commands.OpenTaskOutputDirectoryCommand;
    public ICommand OpenTaskArtifactDirectoryCommand => Commands.OpenTaskArtifactDirectoryCommand;
    public ICommand ToggleShowPasswordCommand => Commands.ToggleShowPasswordCommand;
    public ICommand OneClickProcessCommand => Commands.OneClickProcessCommand;
    public ICommand ContinueOneClickCommand => Commands.ContinueOneClickCommand;
    public ICommand ResetSettingsCommand => Commands.ResetSettingsCommand;
    public ICommand RefreshParallelAdviceCommand => Commands.RefreshParallelAdviceCommand;

    private void RefreshManualDistribution() => ManualOperation.RefreshManualDistribution();

    private Task FindVolumeSiblingsAsync() => ManualOperation.FindVolumeSiblingsAsync();

    internal void SetManualDistributionTitle(string value) => ManualDistributionTitle = value;

    internal void SetManualDistributionPath(string value) => ManualDistributionPath = value;

    internal void SetManualDistributionEmptyText(string value) => ManualDistributionEmptyText = value;

    internal void SetManualDistributionEntries(IReadOnlyList<ManualDistributionEntry> value) => ManualDistributionEntries = value;

    internal void SetManualVolumeCandidates(IReadOnlyList<ManualDistributionEntry> value) => ManualVolumeCandidates = value;

    internal void SetManualVolumeSearchText(string value) => ManualVolumeSearchText = value;

    internal void NotifyManualVolumeChanged()
    {
        OnPropertyChanged(nameof(ManualVolumeCandidates));
        OnPropertyChanged(nameof(ManualVolumeSearchText));
    }

    internal void NotifyManualDistributionChanged()
    {
        OnPropertyChanged(nameof(ManualDistributionTitle));
        OnPropertyChanged(nameof(ManualDistributionPath));
        OnPropertyChanged(nameof(ManualDistributionEmptyText));
        OnPropertyChanged(nameof(ManualDistributionEntries));
        OnPropertyChanged(nameof(ManualGuideText));
        OnPropertyChanged(nameof(ManualVolumeCandidates));
        OnPropertyChanged(nameof(ManualVolumeSearchText));
    }

    internal static string FormatFileSize(FileSystemInfo info)
    {
        try
        {
            if (info is not FileInfo file)
            {
                return "-";
            }

            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double value = file.Length;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
        catch
        {
            return "?";
        }
    }

}
