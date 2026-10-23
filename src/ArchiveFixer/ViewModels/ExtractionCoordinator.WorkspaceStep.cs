using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 工作区这一步的接线（第 14 轮：实现已搬进 <c>Extraction/WorkspaceStep.cs</c> 独立类，
    /// 这里只留一行调用 + 把协调器的能力喂给 <see cref="IWorkspaceStepHost"/>）。
    /// </summary>
    internal sealed partial class ExtractionCoordinator : IWorkspaceStepHost
    {
        private readonly WorkspaceStep _workspaceStep;

        private WorkspaceRootResolution ApplyBatchWorkspaceRoot(IReadOnlyList<ArchiveTask> tasks) =>
            _workspaceStep.ApplyBatchWorkspaceRoot(tasks);

        private void RemoveEmptyWorkspaceShells() => _workspaceStep.RemoveEmptyWorkspaceShells();

        private void RemoveEmptyTargetShellIfWeCreatedIt() => _workspaceStep.RemoveEmptyTargetShellIfWeCreatedIt();

        private void CleanupTaskWorkspaceDirectory(ArchiveTask task, string stageDirectory) =>
            _workspaceStep.CleanupTaskWorkspaceDirectory(task, stageDirectory);

        private void CleanupFailedTaskWorkspace(ArchiveTask task, bool keepWorkspace = false) =>
            _workspaceStep.CleanupFailedTaskWorkspace(task, keepWorkspace);

        private bool HasProductEntriesOutsideWorkspace(string directory) =>
            _workspaceStep.HasProductEntriesOutsideWorkspace(directory);

        /// <summary>单测注入：目标目录 → 假盘根（默认 = <see cref="System.IO.Path.GetPathRoot(string)"/>）。</summary>
        internal Func<string, string>? WorkspaceDriveOverride
        {
            get => _workspaceStep.WorkspaceDriveOverride;
            set => _workspaceStep.WorkspaceDriveOverride = value;
        }

        /// <summary>本次运行每个任务真正用的工作区目录（键 = 任务对象）。</summary>
        internal ConcurrentDictionary<ArchiveTask, string> TaskWorkspaceDirectories =>
            _workspaceStep.TaskWorkspaceDirectories;

        /// <summary>本批我们自己建出来的那个目标目录（空串 = 这一批没建过）。</summary>
        internal string BatchCreatedTargetDirectory
        {
            get => _workspaceStep.BatchCreatedTargetDirectory;
            set => _workspaceStep.BatchCreatedTargetDirectory = value;
        }

        PathService IWorkspaceStepHost.PathService => _pathService;

        AppSettings? IWorkspaceStepHost.Settings => Settings;

        OneClickRunOptions? IWorkspaceStepHost.RunOptions => RunOptions;

        ExtractOptions IWorkspaceStepHost.BuildExtractOptions(bool tryExtractUnknownFormat) =>
            BuildExtractOptions(tryExtractUnknownFormat);

        bool IWorkspaceStepHost.IsUnresolvedVolumeGroupMember(ArchiveTask task) =>
            IsUnresolvedVolumeGroupMember(task);

        Func<string, bool> IWorkspaceStepHost.IsOurWorkspaceSubdirectory => IsOurWorkspaceSubdirectory;

        void IWorkspaceStepHost.AppendLog(string level, string message) => AppendLog(level, message);

        RecursiveExtractor? IWorkspaceStepHost.TakeTaskRecursiveExtractor(ArchiveTask task) =>
            _taskRecursiveExtractors.TryRemove(task, out RecursiveExtractor? extractor) ? extractor : null;
    }
}
