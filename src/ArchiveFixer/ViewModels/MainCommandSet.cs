using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
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

    internal void Initialize(MainViewModel _vm)
    {
        AddFilesCommand = new AsyncRelayCommand(_vm.ScanCoordinator.AddFilesAsync, _vm.CanRunNormalCommand);
        AddFolderCommand = new AsyncRelayCommand(_vm.ScanCoordinator.AddFolderAsync, _vm.CanRunNormalCommand);
        AppendFilesCommand = new AsyncRelayCommand(_vm.ScanCoordinator.AppendFilesAsync, _vm.CanRunNormalCommand);
        AppendFolderCommand = new AsyncRelayCommand(_vm.ScanCoordinator.AppendFolderAsync, _vm.CanRunNormalCommand);
        ScanCommand = new AsyncRelayCommand(_vm.ScanCoordinator.ScanTasksAsync, _vm.CanRunNormalCommand);
        ClearCommand = new RelayCommand(_vm.ClearTasks, _vm.CanRunNormalCommand);
        RemoveSelectedCommand = new RelayCommand(_vm.RemoveSelectedTasks, _vm.CanRemoveSelectedTasks);

        SmartRenameCommand = new AsyncRelayCommand(
            () => _vm.RenameCoordinator.SmartRenameAsync(),
            _vm.CanRunNormalCommand);
        ChangeNameCommand = new AsyncRelayCommand(
            () => _vm.RenameCoordinator.ChangeNameAsync(),
            _vm.CanRunNormalCommand);

        /*
         * 四个"开始干活"的入口都打一个操作标记（用户 2026-09-25 第 38 条）：
         * 日志里会出现一行 `======== 本次操作开始：<名字> ========`，
         * 「导出日志」默认就从那一行开始导 —— 用户拿到的是**最近一次操作**的日志，不是一堆历史。
         */
        OneClickProcessCommand = new AsyncRelayCommand(
            async () =>
            {
                _vm.LogService.MarkOperationStart("一键处理");
                await _vm.OneClickCoordinator.RunAsync().ConfigureAwait(true);
            },
            _vm.CanRunNormalCommand);

        /*
         * 「继续解」（第 16 条追加）：只在"上一批撞到层数上限、还剩内层包没解"时可点。
         * 与一键处理同一道忙碌守卫（_vm.CanRunNormalCommand）——两者不能同时跑。
         */
        ContinueOneClickCommand = new AsyncRelayCommand(
            async () =>
            {
                _vm.LogService.MarkOperationStart("继续解");
                await _vm.OneClickCoordinator.RunAsync().ConfigureAwait(true);
            },
            _vm.CanContinueOneClick);

        StartExtractCommand = new AsyncRelayCommand(
            async () =>
            {
                _vm.LogService.MarkOperationStart("解压");
                await _vm.ExtractionCoordinator.StartExtractAsync().ConfigureAwait(true);
            },
            _vm.CanStartExtract);

        /*
         * 「解压到当前文件夹」（用户 2026-09-27）：与上面那条**同一个本体**，
         * 只把"这次摊平"这一个运行期标记传下去 —— 落点推导、定稿、校验、其余物、清理
         * 全都还是同一条管线（⛔ 不许为它另写一条解压路径）。
         *
         * 可用性守卫与「只解压」逐字相同（闲着 + 至少勾了一个任务）：
         * 它同样按勾选办事，不勾就点不动，免得用户以为"点了没反应"。
         */
        ExtractIntoSourceFolderCommand = new AsyncRelayCommand(
            async () =>
            {
                _vm.LogService.MarkOperationStart(StatusText.ExtractIntoSourceFolderText);
                await _vm.ExtractionCoordinator.StartExtractAsync(extractIntoSourceFolder: true).ConfigureAwait(true);
            },
            _vm.CanStartExtract);
        StopCommand = new RelayCommand(_vm.ExtractionCoordinator.StopAfterCurrent, () => _vm.IsBusy);
        CancelCurrentCommand = new RelayCommand(() => _vm.ExtractionCoordinator.CancelCurrentTask(), () => _vm.IsBusy);
        PauseAllOperationsCommand = new RelayCommand(
            () =>
            {
                // 暂停 = 同一秒内掐断当前所有任务 + 不再启动后续；恢复只能重新按一键处理。
                _vm.ExtractionCoordinator.StopAfterCurrent();
                _vm.ExtractionCoordinator.CancelCurrentTask("暂停所有操作");
            },
            () => _vm.IsBusy);

        OpenSettingsCommand = new RelayCommand(_vm.OpenSettings, _vm.CanRunNormalCommand);
        OpenPasswordListCommand = new RelayCommand(_vm.OpenPasswordList, _vm.CanRunNormalCommand);
        OpenPackingCommand = new RelayCommand(_vm.OpenPacking, _vm.CanRunNormalCommand);

        ImportPasswordBookCommand = new RelayCommand(_vm.PasswordBook.Import, _vm.CanRunNormalCommand);
        ExportLogCommand = new RelayCommand(_vm.LogExport.ExportLog);
        ExportDiagnosticBundleCommand = new RelayCommand(_vm.LogExport.ExportDiagnosticBundle);
        ExportAllLogsCommand = new RelayCommand(_vm.LogExport.ExportAllLogs);
        CopyFailedListCommand = new RelayCommand(_vm.LogExport.CopyFailedList);
        ExportFailedListCommand = new RelayCommand(_vm.LogExport.ExportFailedList);
        OpenCheckedSourceDirectoryCommand = new RelayCommand(_vm.OpenCheckedSourceDirectory, _vm.CanRunNormalCommand);
        InspectArchiveCommand = new AsyncRelayCommand(_vm.InspectCheckedArchivesAsync, _vm.CanRunNormalCommand);
        TryPasswordsCommand = new AsyncRelayCommand(_vm.TryPasswordsForCheckedArchivesAsync, _vm.CanRunNormalCommand);

        InspectCurrentTaskCommand = new AsyncRelayCommand(
            () => _vm.RunInspectAsync(tryPasswords: false, singleTask: _vm.SelectedTask),
            _vm.CanRunCurrentTaskManual);
        TryPasswordsForCurrentTaskCommand = new AsyncRelayCommand(
            () => _vm.RunInspectAsync(tryPasswords: true, singleTask: _vm.SelectedTask),
            _vm.CanRunCurrentTaskManual);
        OpenCurrentSourceDirectoryCommand = new RelayCommand(
            () => { if (_vm.SelectedTask != null) _vm.OpenTaskDirectory(_vm.SelectedTask); },
            _vm.CanRunCurrentTaskManual);
        SmartRenameCurrentTaskCommand = new AsyncRelayCommand(
            () => _vm.RenameCoordinator.SmartRenameAsync(_vm.SelectedTask == null ? null : new[] { _vm.SelectedTask }),
            _vm.CanRunCurrentTaskManual);
        ChangeNameCurrentTaskCommand = new AsyncRelayCommand(
            () => _vm.RenameCoordinator.ChangeNameAsync(_vm.SelectedTask == null ? null : new[] { _vm.SelectedTask }),
            _vm.CanRunCurrentTaskManual);
        RepairVolumeNameCurrentTaskCommand = new AsyncRelayCommand(
            () => _vm.RenameBySuggestionAndRetryAsync(_vm.SelectedTask == null ? null : new[] { _vm.SelectedTask }),
            () => _vm.CanRunCurrentTaskManual() && _vm.SelectedTask != null
                && _vm.BuildVolumeRepairCandidates(new[] { _vm.SelectedTask }).Count > 0);
        StartExtractCurrentTaskCommand = new AsyncRelayCommand(
            async () =>
            {
                ArchiveTask? target = _vm.ResolveManualExtractionTarget();
                if (target == null) return;
                _vm.LogService.MarkOperationStart("只解压当前任务");
                await _vm.ExtractionCoordinator.StartExtractAsync(targetTasks: new[] { target }).ConfigureAwait(true);
            },
            _vm.CanRunCurrentTaskManual);
        ExtractIntoSourceFolderCurrentTaskCommand = new AsyncRelayCommand(
            async () =>
            {
                ArchiveTask? target = _vm.ResolveManualExtractionTarget();
                if (target == null) return;
                _vm.LogService.MarkOperationStart(StatusText.ExtractIntoSourceFolderText);
                await _vm.ExtractionCoordinator.StartExtractAsync(
                    extractIntoSourceFolder: true,
                    targetTasks: new[] { target }).ConfigureAwait(true);
            },
            _vm.CanRunCurrentTaskManual);
        FindVolumeSiblingsCommand = new AsyncRelayCommand(
            _vm.FindVolumeSiblingsAsync,
            () => _vm.CanRunCurrentTaskManual());
        OpenCurrentTaskOutputDirectoryCommand = new RelayCommand(
            () => { if (_vm.SelectedTask != null) _vm.OpenTaskOutputDirectory(_vm.SelectedTask); },
            _vm.CanRunCurrentTaskManual);
        CopyCurrentVolumeGroupPathsCommand = new RelayCommand(
            _vm.CopyCurrentVolumeGroupPaths,
            _vm.CanRunCurrentTaskManual);
        AddCurrentOutputFolderToTasksCommand = new AsyncRelayCommand(
            _vm.AddCurrentOutputFolderToTasksAsync,
            _vm.CanRunCurrentTaskManual);
        CleanCurrentTaskArtifactsCommand = new AsyncRelayCommand(
            () => _vm.Cleanup.RunAsync(
                CleanupScope.Artifacts,
                ArtifactDeleteScope.SelectedTask,
                _vm.SelectedTask == null ? null : new[] { _vm.SelectedTask }),
            _vm.CanRunCurrentTaskManual);
        RecycleCurrentTaskArtifactsCommand = new AsyncRelayCommand(
            () => _vm.Cleanup.RunAsync(
                CleanupScope.Artifacts,
                ArtifactDeleteScope.SelectedTask,
                _vm.SelectedTask == null ? null : new[] { _vm.SelectedTask },
                DeleteMode.RecycleBin),
            _vm.CanRunCurrentTaskManual);
        CleanCurrentTaskEmptyFoldersCommand = new AsyncRelayCommand(
            () => _vm.Cleanup.RunAsync(
                CleanupScope.EmptyFolders,
                ArtifactDeleteScope.SelectedTask,
                _vm.SelectedTask == null ? null : new[] { _vm.SelectedTask }),
            _vm.CanRunCurrentTaskManual);
        OpenOutputDirectoryCommand = new RelayCommand(_vm.OpenOutputDirectory);
        SelectOutputDirectoryCommand = new RelayCommand(_vm.SelectOutputDirectory, _vm.CanRunNormalCommand);
        CopyOutputLocationCommand = new RelayCommand(_vm.CopyOutputLocation, _vm.CanRunNormalCommand);

        /*
         * ②③④⑥四页共用的设置编辑器改了落点之后，把①页「输出位置」那一行拉回同一个真值。
         *
         * 为什么必须挂这条线：AppSettings 是普通 POCO（不发通知），而②页的「选择 / 单选」
         * 走的是 SettingsViewModel（直接写 Settings.CustomOutputDirectory / ExtractToOriginalDirectory），
         * 不经过 MainViewModel.SelectedOutputDirectory 的 setter —— 不挂线的话，
         * 用户在②页改完回到①页会看到旧路径（"改了没反应"那类 bug 的典型形态）。
         * 只在这些属性上搬运，别的属性一律不碰（避免 SaveSettings 之后的整片刷新）。
         */
        _vm.SettingsEditor.PropertyChanged += _vm.OnSettingsEditorPropertyChanged;
        OpenLogDirectoryCommand = new RelayCommand(_vm.OpenLogDirectory);
        OpenWorkDirectoryCommand = new RelayCommand(_vm.OpenWorkDirectory);

        /*
         * 工作区清理：只在"确实有残留"且当前没有跑批时可点。
         * 跑批中清理会在 UI 线程同步递归删目录，既卡界面又可能删到正在用的工作区。
         */
        ClearWorkspaceLeftoversCommand = new RelayCommand(_vm.Workspace.Clear, () => _vm.HasWorkspaceLeftovers && !_vm.IsBusy);

        // 其余物删除入口（③「清理与删除」页）：预览 → 确认 → 后台执行，全程不阻塞界面。
        // 第一个只作用于**当前勾选任务自己那一份**；第二个是显式的"整个目录"入口（默认不选）。
        CleanProcessArtifactsCommand = new AsyncRelayCommand(
            () => _vm.Cleanup.RunAsync(CleanupScope.Artifacts, ArtifactDeleteScope.SelectedTask),
            _vm.CanRunNormalCommand);

        CleanAllProcessArtifactsCommand = new AsyncRelayCommand(
            () => _vm.Cleanup.RunAsync(CleanupScope.Artifacts, ArtifactDeleteScope.EverythingInDirectory),
            _vm.CanRunNormalCommand);

        CleanEmptyFoldersCommand = new AsyncRelayCommand(
            () => _vm.Cleanup.RunAsync(CleanupScope.EmptyFolders, ArtifactDeleteScope.SelectedTask),
            _vm.CanRunNormalCommand);

        /*
         * ⛔ 行级命令**一率补可用性判据**（用户 2026-10-10：「右键点打开输出目录没有，打开文件夹目录没用，
         * 什么东西都是没有用的」＋「所有的都要检查…要么就功能弄好来，要么就不要弄这个功能」）。
         *
         * 现场：这几条原来是"永远可点"，而实现在参数不是任务时**直接 return** ⇒ 右键落在空白处 / 列头、
         * 或列表空着时点下去**什么都不发生、不弹窗、不写日志**（与 2026-09-29 用户骂过的
         * 「移除勾选的按钮一直亮着没用啊」同一个病）。判据 = 参数必须是**列表里真的存在的那一行**
         * （`_vm.IsRowInList`）⇒ 该灰就灰；实现里那一支也补一句说明（⛔ 不许静默）。
         */
        RemoveTaskCommand = new RelayCommand(_vm.RemoveTask, _vm.IsRowInList);

        /*
         * 「移除勾选的」—— 可用性判据与右键菜单那条 `RemoveSelectedCommand` **共用同一个**
         * `_vm.CanRemoveSelectedTasks`（只问"有没有勾选的"），⛔ 不许各写一套。
         *
         * 这条口径被用户改过两次，两半**都**要留着（只照一半改就是错的）：
         *
         * ① 2026-09-27：它以前绑 `!_vm.IsBusy`，跑批中途是灰的；而它只是**纯列表操作**（只动内存里那几行，
         *    磁盘一个字节都不碰）—— 用户原话"我就是修改列表删除东西，和正在处理有什么关系"，
         *    于是 `!_vm.IsBusy` 这一半被否掉：**跑批中途照样能点**。
         * ② 2026-09-29："永远可点"的结果是**列表空着 / 一个都没勾时它也亮着**，点下去什么都不发生 ——
         *    用户原话："这个勾选行里面，移除勾选的按钮一直亮着没用啊，我都没有导入文件你亮着干什么"，
         *    紧接着又指出"这怎么还是和前面的按钮样式不一样"。于是补上"至少有一条勾着的"这一半。
         *
         * ⛔ 合成口径 = **只看有没有勾选的**（与旁边「全选 / 全不选 / 反选」在"没有可作用的对象就不亮"
         * 这点上同形，但**不看忙闲**，那一点上与它们不同形）—— ⛔ 不许把 `!_vm.IsBusy` 加回来。
         *
         * 通知那一条同样关键：判据对而界面不重问 = 用户读成"没生效"。它现在进了
         * `RaiseAllCommandCanExecuteChanged`（`UpdateSummary` 收尾统一发一遍），
         * 于是勾选变化 / 任务增删都会立刻反映到按钮上（忙闲切换也照发一遍，只是这颗按钮的判据不看它）
         * （§9.5「值 + 通知」两条都要钉住）。
         */
        RemoveCheckedTasksCommand = new RelayCommand(_vm.RemoveCheckedTasks, _vm.CanRemoveSelectedTasks);

        RenameBySuggestionAndRetryCommand = new AsyncRelayCommand(
            () => _vm.RenameBySuggestionAndRetryAsync(),
            _vm.CanRenameBySuggestionAndRetry);
        RescanTaskCommand = new AsyncRelayCommand(_vm.ScanCoordinator.RescanTaskAsync, _vm.IsRowInList);
        CopyTaskInfoCommand = new RelayCommand(_vm.CopyTaskInfo, _vm.IsRowInList);
        CopyTaskPathCommand = new RelayCommand(_vm.CopyTaskPath, _vm.IsRowInList);
        CopyTaskErrorCommand = new RelayCommand(_vm.CopyTaskError, _vm.IsRowInList);
        /*
         * ⚠ 这两条命令的实体返回 bool（"到底打开了没有"）—— 命令层要的是 Action ⇒ 这里用 lambda 转一手。
         * ⛔ 别改回方法组：那会让"打不开时静默"重新变成可能（用户 2026-10-10 实测的死点）。
         */
        OpenTaskDirectoryCommand = new RelayCommand(parameter => _vm.OpenTaskDirectory(parameter));
        OpenTaskOutputDirectoryCommand = new RelayCommand(parameter => _vm.OpenTaskOutputDirectory(parameter));
        OpenTaskArtifactDirectoryCommand = new RelayCommand(_vm.OpenTaskArtifactDirectory, _vm.IsRowInList);
        ToggleShowPasswordCommand = new RelayCommand(() =>
        {
            _vm.ShowPassword = !_vm.ShowPassword;
        });

        ResetSettingsCommand = new RelayCommand(_vm.ResetSettings, _vm.CanRunNormalCommand);

        /*
         * ===== 空间规划（用户 2026-09-22 需求）=====
         *
         * 一个入口：RefreshParallelAdviceCommand —— 按当前可用空间算出"建议并行几个"
         * （纯计算 + stat，放后台）。
         * ⛔ 原来这里还有一个红色按钮命令（危险模式 + 自测 + 凭证）：那整套已按
         * 用户 2026-09-25 第 32 条整块退役 —— 那条需求现在是③页「2 删除操作」的第三档。
         */
        RefreshParallelAdviceCommand = new AsyncRelayCommand(_vm.RefreshParallelAdviceAsync, _vm.CanRunNormalCommand);

        SelectAllTasksCommand = new RelayCommand(_vm.SelectAllTasks, _vm.CanChangeTaskSelection);
        SelectNoneTasksCommand = new RelayCommand(_vm.SelectNoneTasks, _vm.CanChangeTaskSelection);
        InvertTaskSelectionCommand = new RelayCommand(_vm.InvertTaskSelection, _vm.CanChangeTaskSelection);
        SelectSoleTaskCommand = new RelayCommand(_vm.SelectSoleTask, _vm.IsRowInList);
    }
}







