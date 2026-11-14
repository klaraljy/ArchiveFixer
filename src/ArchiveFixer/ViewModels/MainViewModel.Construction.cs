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
    public MainViewModel()
        : this(
              new FileScanService(),
              new ArchiveDetectService(),
              new RenameService(),
              new SevenZipEngine(),
              new PasswordService(),
              new LogService(),
              new SettingsService(),
              new PathService(),
              new TaskSummaryService(),
              new ClipboardService(),
              new DialogService(),
              // 生产入口：内置 passwords.txt 是主存储（测试用带参构造，保持旧 DPAPI 夹具语义）。
              // ⛔ 必须走构造参数：在构造体里补设就太晚了 —— AutoLoadPasswordBook() 在
              // 显式构造的末尾已经读过一遍记忆（真机 2026-11-01：13 条被 1 条旧记忆顶替）。
              useTextStore: true,
              // 生产密码本 DPAPI 加密；测试仍可按旧明文夹具运行。
              encryptTextStore: true)
    {
    }

    /// <summary>
    /// <paramref name="engineRegistry"/> 不给时按 <paramref name="archiveEngine"/> 推一套（见 <see cref="BuildEngineRegistry"/>）；
    /// 给了就以它为准（测试可以塞一套全是替身的注册表，端到端验证"按格式分派"）。
    /// </summary>
    public MainViewModel(
        FileScanService fileScanService,
        ArchiveDetectService archiveDetectService,
        RenameService renameService,
        IArchiveEngine archiveEngine,
        PasswordService passwordService,
        LogService logService,
        SettingsService settingsService,
        PathService pathService,
        TaskSummaryService taskSummaryService,
        ClipboardService clipboardService,
        DialogService dialogService,
        MaintenanceCleanupService? cleanupService = null,
        EngineRegistry? engineRegistry = null,
        bool useTextStore = false,
        bool encryptTextStore = false)
    {
        _fileScanService = fileScanService;
        _archiveDetectService = archiveDetectService;
        _renameService = renameService;

        /*
         * 流水线拿到的**不是**某一个引擎，而是"按格式分派 + 按优先级决定先用谁"的门面。
         *
         * 为什么必须换掉（这批修的就是这个）：端口本来就是 IArchiveEngine，
         * 但注册的只有 7-Zip —— 于是「先是 winrar、再是 7z」这条设置、
         * 以及辛苦做出来的 UnRarEngine，在界面上**完全没有生效**：
         * 不管优先级怎么排、UnRAR 装没装，干活的永远是 7-Zip。
         *
         * 分派规则一条都不在这里重写：能力筛、优先级 tiebreaker、不可用跳过、回退判据
         * 全在 EngineRegistry / EngineSelector 里（AGENTS.md §3.1），这里只做转发。
         */
        _archiveEngine = new EngineRouter(engineRegistry ?? BuildEngineRegistry(archiveEngine));

        _passwordService = passwordService;

        /*
         * ⛔ 2026-11-01：主存储必须在**任何读取之前**定下来。
         * 以前是在无参构造的构造体里补设 `UseTextStore = true` —— 那时显式构造体末尾的
         * `AutoLoadPasswordBook()` 已经先读过一遍，读的是旧的 DPAPI 记忆（不是 passwords.txt），
         * 随后又把那份旧列表写回 txt（真机：用户 13 条被 1 条测试密码顶替）。
         */
        _passwordService.UseTextStore = useTextStore;

        if (useTextStore)
        {
            _passwordService.TextStore.EncryptAtRest = encryptTextStore;
        }

        _logService = logService;
        _settingsService = settingsService;
        _pathService = pathService;

        /*
         * ⛔ 2026-11-01：PasswordService 默认数据根改成"未装配 = 空，不落盘"。
         * 生产入口由这里装配（PathService 的根就是 %LOCALAPPDATA%\ArchiveFixer）；
         * 测试传临时 PathService 就自动落到临时根，绝不会再写用户真实数据。
         */
        _passwordService.DataRootDirectory = pathService.DataRootDirectory;
        _taskSummaryService = taskSummaryService;
        _clipboardService = clipboardService;
        _dialogService = dialogService;

        // 清理服务可注入（测试里换掉执行器，绝不碰真实回收站）；不传就用真实实现。
        _cleanupService = cleanupService ?? new MaintenanceCleanupService();

        _scanCoordinator = new ScanCoordinator(this, fileScanService, archiveDetectService, dialogService);
        _renameCoordinator = new RenameCoordinator(this, _scanCoordinator, renameService, dialogService);
        _extractionCoordinator = new ExtractionCoordinator(this, _archiveEngine, passwordService, pathService, dialogService);
        _oneClickCoordinator = new OneClickCoordinator(this, _scanCoordinator, _renameCoordinator, _extractionCoordinator, dialogService);

        /*
         * 把"解压前那一遍 list 顺带算出来的提示"接进失败清单的第二级（逐归档缩进行）。
         *
         * 为什么走这个注入口而不是改 TaskSummaryService：那个文件不在本次授权范围内
         * （任务书明确要求"需要什么就写进报告"）。TaskSummaryService 早就留了这个可选钩子
         * （EntryDetailProvider），这里挂上就等价于改了它的输出，而且不改它的代码。
         *
         * 两条提示都是"只提示不阻断"的结论，所以措辞上不写成错误，也不影响任何状态判定：
         * · 可疑条目（可执行 / 脚本类）—— 设置项 ReportDangerousEntries，默认开；
         * · 路径过长预警 —— 7z 报的是英文错，这里给一句中文。
         */
        _taskSummaryService.EntryDetailProvider = task =>
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(task.DangerousEntriesWarning))
            {
                lines.Add(task.DangerousEntriesWarning);
            }

            if (!string.IsNullOrWhiteSpace(task.PathLengthWarning))
            {
                lines.Add(task.PathLengthWarning);
            }

            return lines.Count == 0 ? null : lines;
        };

        /*
         * "这个包实际是哪个引擎解的"接进报告（不变量 14）。
         *
         * 报告层默认只能报"通用引擎"（它不知道每个包走了哪条分派路，而且报告生成那一刻
         * 用户可能已经改了优先级、卸载了 WinRAR —— 那时再去问注册表问的是"现在"，
         * 不是"当时"）。所以由**真正执行的那个引擎**在结果上盖戳（ArchiveOperationResult
         * 的 EngineId / EngineVersion），门面按归档路径记下来，报告逐任务取用；
         * 取不到（比如报告是手搓的、或这一次根本没跑过引擎）就退回通用引擎身份。
         */
        _taskSummaryService.EngineIdentityProvider = task => ResolveEngineIdentity(task);

        /*
         * 读设置只有一步（数据根固定 = %LOCALAPPDATA%\ArchiveFixer，设置文件只有一处）。
         *
         * 老实现要读三步：① 先读程序目录那一份 → ② 按「缓存根目录」这个设置项把数据根定下来 →
         * ③ 数据根换了地方就再读一次。那个设置项已经删除，②③ 随之消失；
         * 少了 ③ 也不会再出现"启动读旧的、保存写新的"（两者本来就是同一个文件了）。
         */
        _settings = _settingsService.Load();
        ApplyEngineSettings();

        StartSettingsAutoSaveTimer();

        _uiResponsivenessMonitor = UiThread.StartMonitor?.Invoke(
            message => AppendDiagnosticLog(
                "WARN",
                message,
                DiagnosticCodes.UiStall,
                "UiResponsivenessMonitor"));

        /*
         * 设置编辑器（选项卡②③④⑥绑它）与打包编辑器（⑤绑它）。
         *
         * ⚠ AttachSharedSettings 是**必须**的：SettingsViewModel 的构造函数会克隆一份设置
         * （设置窗口的「取消」靠它回退），而选项卡上同一项可能在多处出现
         * （并发档既在②的输入框里、又决定危险模式凭证的覆盖判定），
         * 两份值一定会打架 —— 共享同一个对象才不会有"界面显示 A、保存写回 B"。
         */
        _settingsEditor = new SettingsViewModel(_settings, _settingsService);

        /*
         * ②页「指定位置 → 选择…」也要做一次空间体检（用户 2026-09-27 第 2 条）：
         * 那一颗按钮与①页的「选择…」是**两个入口、同一件事**（换了一块输出盘），
         * 只挂一边的话，用户在②页挑完盘什么都不会发生。
         * ⚠ 不 await（回调是同步的）：体检自己吞掉所有异常，不存在没人接的异常。
         */
        _settingsEditor.OutputDirectoryPicked = pickedFolder =>
            _ = CheckSpaceForTasksAsync(Tasks.ToList(), "换了输出位置");
        _settingsEditor.AttachSharedSettings(_settings);

        PackingEditor = new PackingViewModel
        {
            LogSink = line => AppendLog("INFO", line),

            /*
             * ⛔ 打包也要在操作日志里**打一条"本次操作开始"**（第 38 条那套口径）：
             * 「导出日志（本次操作）」是从最后一条标记开始导的 —— 打包以前不打标记，
             * 于是用户"刚打完包就导出"，导出来的头一行还是**上一次解压**的"本次操作开始：解压"，
             * 或者干脆整份运行日志全倒出来。时机同理：**用户确认之后、调服务之前**（第 46 条）。
             */
            OperationStarted = () => _logService.MarkOperationStart("打包")
        };

        /*
         * 打包那几档（落点 / 原包操作 / 其余物操作）存在**同一份 AppSettings** 上，
         * 但字段与解压侧完全分开（第 46 条：⛔ 两者绝不同步）；改了要落盘（"保存记忆操作"）。
         * 密码列表由④页那份喂进来（「从密码列表里选」按钮用）。
         */
        PackingEditor.Settings = Settings;
        PackingEditor.SettingsChanged = SavePackingSettings; PackingEditor.PasswordListProvider = () => _passwordService.Passwords
            .Where(item => item != null && item.IsEnabled && !string.IsNullOrEmpty(item.Value))
            .Select(item => item.Value)
            .ToList();

        /*
         * 「记住上次输出目录」真正生效的地方（不是留着好看的开关）：
         * 关掉之后，启动时**不**把上次的输出目录填回 SelectedOutputDirectory，
         * 这一次运行按"输出位置"那一档的规则算落点（默认 = 压缩包同目录）。
         * 之前这个开关只存在于界面上，改了什么都不会发生。
         *
         * ⛔ 两条边界（2026-10-01 修；现场见 SyncOutputLocationFromSettings 的说明）：
         * ① **不许走 SelectedOutputDirectory 的 setter** —— 它的语义是"用户指定了一个位置"，
         *    会把用户上回选的「未指定位置」在启动时悄悄改成"指定位置"，于是"下次点开没有记忆"；
         * ② 关掉这个开关只是"这一格空着"，⛔ 不许顺手去动设置里的 CustomOutputDirectory
         *    （搬值的那条路只碰界面字段，绝不写设置）。
         */
        SyncOutputLocationFromSettings(
            _settings.RememberLastOutputDirectory
                ? _settings.CustomOutputDirectory ?? string.Empty
                : string.Empty);

        AddFilesCommand = new AsyncRelayCommand(_scanCoordinator.AddFilesAsync, CanRunNormalCommand);
        AddFolderCommand = new AsyncRelayCommand(_scanCoordinator.AddFolderAsync, CanRunNormalCommand);
        AppendFilesCommand = new AsyncRelayCommand(_scanCoordinator.AppendFilesAsync, CanRunNormalCommand);
        AppendFolderCommand = new AsyncRelayCommand(_scanCoordinator.AppendFolderAsync, CanRunNormalCommand);
        ScanCommand = new AsyncRelayCommand(_scanCoordinator.ScanTasksAsync, CanRunNormalCommand);
        ClearCommand = new RelayCommand(ClearTasks, CanRunNormalCommand);
        RemoveSelectedCommand = new RelayCommand(RemoveSelectedTasks, CanRemoveSelectedTasks);

        SmartRenameCommand = new AsyncRelayCommand(
            () => _renameCoordinator.SmartRenameAsync(),
            CanRunNormalCommand);
        ChangeNameCommand = new AsyncRelayCommand(
            () => _renameCoordinator.ChangeNameAsync(),
            CanRunNormalCommand);

        /*
         * 四个"开始干活"的入口都打一个操作标记（用户 2026-09-25 第 38 条）：
         * 日志里会出现一行 `======== 本次操作开始：<名字> ========`，
         * 「导出日志」默认就从那一行开始导 —— 用户拿到的是**最近一次操作**的日志，不是一堆历史。
         */
        OneClickProcessCommand = new AsyncRelayCommand(
            async () =>
            {
                _logService.MarkOperationStart("一键处理");
                await _oneClickCoordinator.RunAsync().ConfigureAwait(true);
            },
            CanRunNormalCommand);

        /*
         * 「继续解」（第 16 条追加）：只在"上一批撞到层数上限、还剩内层包没解"时可点。
         * 与一键处理同一道忙碌守卫（CanRunNormalCommand）——两者不能同时跑。
         */
        ContinueOneClickCommand = new AsyncRelayCommand(
            async () =>
            {
                _logService.MarkOperationStart("继续解");
                await _oneClickCoordinator.RunAsync().ConfigureAwait(true);
            },
            CanContinueOneClick);

        StartExtractCommand = new AsyncRelayCommand(
            async () =>
            {
                _logService.MarkOperationStart("解压");
                await _extractionCoordinator.StartExtractAsync().ConfigureAwait(true);
            },
            CanStartExtract);

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
                _logService.MarkOperationStart(StatusText.ExtractIntoSourceFolderText);
                await _extractionCoordinator.StartExtractAsync(extractIntoSourceFolder: true).ConfigureAwait(true);
            },
            CanStartExtract);
        StopCommand = new RelayCommand(_extractionCoordinator.StopAfterCurrent, () => IsBusy);
        CancelCurrentCommand = new RelayCommand(() => _extractionCoordinator.CancelCurrentTask(), () => IsBusy);
        PauseAllOperationsCommand = new RelayCommand(
            () =>
            {
                // 暂停 = 同一秒内掐断当前所有任务 + 不再启动后续；恢复只能重新按一键处理。
                _extractionCoordinator.StopAfterCurrent();
                _extractionCoordinator.CancelCurrentTask("暂停所有操作");
            },
            () => IsBusy);

        OpenSettingsCommand = new RelayCommand(OpenSettings, CanRunNormalCommand);
        OpenPasswordListCommand = new RelayCommand(OpenPasswordList, CanRunNormalCommand);
        OpenPackingCommand = new RelayCommand(OpenPacking, CanRunNormalCommand);

        ImportPasswordBookCommand = new RelayCommand(PasswordBook.Import, CanRunNormalCommand);
        ExportLogCommand = new RelayCommand(LogExport.ExportLog);
        ExportDiagnosticBundleCommand = new RelayCommand(LogExport.ExportDiagnosticBundle);
        ExportAllLogsCommand = new RelayCommand(LogExport.ExportAllLogs);
        CopyFailedListCommand = new RelayCommand(LogExport.CopyFailedList);
        ExportFailedListCommand = new RelayCommand(LogExport.ExportFailedList);
        OpenCheckedSourceDirectoryCommand = new RelayCommand(OpenCheckedSourceDirectory, CanRunNormalCommand);
        InspectArchiveCommand = new AsyncRelayCommand(InspectCheckedArchivesAsync, CanRunNormalCommand);
        TryPasswordsCommand = new AsyncRelayCommand(TryPasswordsForCheckedArchivesAsync, CanRunNormalCommand);

        InspectCurrentTaskCommand = new AsyncRelayCommand(
            () => RunInspectAsync(tryPasswords: false, singleTask: SelectedTask),
            CanRunCurrentTaskManual);
        TryPasswordsForCurrentTaskCommand = new AsyncRelayCommand(
            () => RunInspectAsync(tryPasswords: true, singleTask: SelectedTask),
            CanRunCurrentTaskManual);
        OpenCurrentSourceDirectoryCommand = new RelayCommand(
            () => { if (SelectedTask != null) OpenTaskDirectory(SelectedTask); },
            CanRunCurrentTaskManual);
        SmartRenameCurrentTaskCommand = new AsyncRelayCommand(
            () => _renameCoordinator.SmartRenameAsync(SelectedTask == null ? null : new[] { SelectedTask }),
            CanRunCurrentTaskManual);
        ChangeNameCurrentTaskCommand = new AsyncRelayCommand(
            () => _renameCoordinator.ChangeNameAsync(SelectedTask == null ? null : new[] { SelectedTask }),
            CanRunCurrentTaskManual);
        RepairVolumeNameCurrentTaskCommand = new AsyncRelayCommand(
            () => RenameBySuggestionAndRetryAsync(SelectedTask == null ? null : new[] { SelectedTask }),
            () => CanRunCurrentTaskManual() && SelectedTask != null
                && BuildVolumeRepairCandidates(new[] { SelectedTask }).Count > 0);
        StartExtractCurrentTaskCommand = new AsyncRelayCommand(
            async () =>
            {
                ArchiveTask? target = ResolveManualExtractionTarget();
                if (target == null) return;
                _logService.MarkOperationStart("只解压当前任务");
                await _extractionCoordinator.StartExtractAsync(targetTasks: new[] { target }).ConfigureAwait(true);
            },
            CanRunCurrentTaskManual);
        ExtractIntoSourceFolderCurrentTaskCommand = new AsyncRelayCommand(
            async () =>
            {
                ArchiveTask? target = ResolveManualExtractionTarget();
                if (target == null) return;
                _logService.MarkOperationStart(StatusText.ExtractIntoSourceFolderText);
                await _extractionCoordinator.StartExtractAsync(
                    extractIntoSourceFolder: true,
                    targetTasks: new[] { target }).ConfigureAwait(true);
            },
            CanRunCurrentTaskManual);
        FindVolumeSiblingsCommand = new AsyncRelayCommand(
            FindVolumeSiblingsAsync,
            () => CanRunCurrentTaskManual());
        OpenCurrentTaskOutputDirectoryCommand = new RelayCommand(
            () => { if (SelectedTask != null) OpenTaskOutputDirectory(SelectedTask); },
            CanRunCurrentTaskManual);
        CopyCurrentVolumeGroupPathsCommand = new RelayCommand(
            CopyCurrentVolumeGroupPaths,
            CanRunCurrentTaskManual);
        AddCurrentOutputFolderToTasksCommand = new AsyncRelayCommand(
            AddCurrentOutputFolderToTasksAsync,
            CanRunCurrentTaskManual);
        CleanCurrentTaskArtifactsCommand = new AsyncRelayCommand(
            () => Cleanup.RunAsync(
                CleanupScope.Artifacts,
                ArtifactDeleteScope.SelectedTask,
                SelectedTask == null ? null : new[] { SelectedTask }),
            CanRunCurrentTaskManual);
        RecycleCurrentTaskArtifactsCommand = new AsyncRelayCommand(
            () => Cleanup.RunAsync(
                CleanupScope.Artifacts,
                ArtifactDeleteScope.SelectedTask,
                SelectedTask == null ? null : new[] { SelectedTask },
                DeleteMode.RecycleBin),
            CanRunCurrentTaskManual);
        CleanCurrentTaskEmptyFoldersCommand = new AsyncRelayCommand(
            () => Cleanup.RunAsync(
                CleanupScope.EmptyFolders,
                ArtifactDeleteScope.SelectedTask,
                SelectedTask == null ? null : new[] { SelectedTask }),
            CanRunCurrentTaskManual);
        OpenOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory);
        SelectOutputDirectoryCommand = new RelayCommand(SelectOutputDirectory, CanRunNormalCommand);
        CopyOutputLocationCommand = new RelayCommand(CopyOutputLocation, CanRunNormalCommand);

        /*
         * ②③④⑥四页共用的设置编辑器改了落点之后，把①页「输出位置」那一行拉回同一个真值。
         *
         * 为什么必须挂这条线：AppSettings 是普通 POCO（不发通知），而②页的「选择 / 单选」
         * 走的是 SettingsViewModel（直接写 Settings.CustomOutputDirectory / ExtractToOriginalDirectory），
         * 不经过 MainViewModel.SelectedOutputDirectory 的 setter —— 不挂线的话，
         * 用户在②页改完回到①页会看到旧路径（"改了没反应"那类 bug 的典型形态）。
         * 只在这些属性上搬运，别的属性一律不碰（避免 SaveSettings 之后的整片刷新）。
         */
        _settingsEditor.PropertyChanged += OnSettingsEditorPropertyChanged;
        OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
        OpenWorkDirectoryCommand = new RelayCommand(OpenWorkDirectory);

        /*
         * 工作区清理：只在"确实有残留"且当前没有跑批时可点。
         * 跑批中清理会在 UI 线程同步递归删目录，既卡界面又可能删到正在用的工作区。
         */
        ClearWorkspaceLeftoversCommand = new RelayCommand(Workspace.Clear, () => HasWorkspaceLeftovers && !IsBusy);

        // 其余物删除入口（③「清理与删除」页）：预览 → 确认 → 后台执行，全程不阻塞界面。
        // 第一个只作用于**当前勾选任务自己那一份**；第二个是显式的"整个目录"入口（默认不选）。
        CleanProcessArtifactsCommand = new AsyncRelayCommand(
            () => Cleanup.RunAsync(CleanupScope.Artifacts, ArtifactDeleteScope.SelectedTask),
            CanRunNormalCommand);

        CleanAllProcessArtifactsCommand = new AsyncRelayCommand(
            () => Cleanup.RunAsync(CleanupScope.Artifacts, ArtifactDeleteScope.EverythingInDirectory),
            CanRunNormalCommand);

        CleanEmptyFoldersCommand = new AsyncRelayCommand(
            () => Cleanup.RunAsync(CleanupScope.EmptyFolders, ArtifactDeleteScope.SelectedTask),
            CanRunNormalCommand);

        /*
         * ⛔ 行级命令**一率补可用性判据**（用户 2026-10-10：「右键点打开输出目录没有，打开文件夹目录没用，
         * 什么东西都是没有用的」＋「所有的都要检查…要么就功能弄好来，要么就不要弄这个功能」）。
         *
         * 现场：这几条原来是"永远可点"，而实现在参数不是任务时**直接 return** ⇒ 右键落在空白处 / 列头、
         * 或列表空着时点下去**什么都不发生、不弹窗、不写日志**（与 2026-09-29 用户骂过的
         * 「移除勾选的按钮一直亮着没用啊」同一个病）。判据 = 参数必须是**列表里真的存在的那一行**
         * （`IsRowInList`）⇒ 该灰就灰；实现里那一支也补一句说明（⛔ 不许静默）。
         */
        RemoveTaskCommand = new RelayCommand(RemoveTask, IsRowInList);

        /*
         * 「移除勾选的」—— 可用性判据与右键菜单那条 `RemoveSelectedCommand` **共用同一个**
         * `CanRemoveSelectedTasks`（只问"有没有勾选的"），⛔ 不许各写一套。
         *
         * 这条口径被用户改过两次，两半**都**要留着（只照一半改就是错的）：
         *
         * ① 2026-09-27：它以前绑 `!IsBusy`，跑批中途是灰的；而它只是**纯列表操作**（只动内存里那几行，
         *    磁盘一个字节都不碰）—— 用户原话"我就是修改列表删除东西，和正在处理有什么关系"，
         *    于是 `!IsBusy` 这一半被否掉：**跑批中途照样能点**。
         * ② 2026-09-29："永远可点"的结果是**列表空着 / 一个都没勾时它也亮着**，点下去什么都不发生 ——
         *    用户原话："这个勾选行里面，移除勾选的按钮一直亮着没用啊，我都没有导入文件你亮着干什么"，
         *    紧接着又指出"这怎么还是和前面的按钮样式不一样"。于是补上"至少有一条勾着的"这一半。
         *
         * ⛔ 合成口径 = **只看有没有勾选的**（与旁边「全选 / 全不选 / 反选」在"没有可作用的对象就不亮"
         * 这点上同形，但**不看忙闲**，那一点上与它们不同形）—— ⛔ 不许把 `!IsBusy` 加回来。
         *
         * 通知那一条同样关键：判据对而界面不重问 = 用户读成"没生效"。它现在进了
         * `RaiseAllCommandCanExecuteChanged`（`UpdateSummary` 收尾统一发一遍），
         * 于是勾选变化 / 任务增删都会立刻反映到按钮上（忙闲切换也照发一遍，只是这颗按钮的判据不看它）
         * （§9.5「值 + 通知」两条都要钉住）。
         */
        RemoveCheckedTasksCommand = new RelayCommand(RemoveCheckedTasks, CanRemoveSelectedTasks);

        RenameBySuggestionAndRetryCommand = new AsyncRelayCommand(
            () => RenameBySuggestionAndRetryAsync(),
            CanRenameBySuggestionAndRetry);
        RescanTaskCommand = new AsyncRelayCommand(_scanCoordinator.RescanTaskAsync, IsRowInList);
        CopyTaskInfoCommand = new RelayCommand(CopyTaskInfo, IsRowInList);
        CopyTaskPathCommand = new RelayCommand(CopyTaskPath, IsRowInList);
        CopyTaskErrorCommand = new RelayCommand(CopyTaskError, IsRowInList);
        /*
         * ⚠ 这两条命令的实体返回 bool（"到底打开了没有"）—— 命令层要的是 Action ⇒ 这里用 lambda 转一手。
         * ⛔ 别改回方法组：那会让"打不开时静默"重新变成可能（用户 2026-10-10 实测的死点）。
         */
        OpenTaskDirectoryCommand = new RelayCommand(parameter => OpenTaskDirectory(parameter));
        OpenTaskOutputDirectoryCommand = new RelayCommand(parameter => OpenTaskOutputDirectory(parameter));
        OpenTaskArtifactDirectoryCommand = new RelayCommand(OpenTaskArtifactDirectory, IsRowInList);
        ToggleShowPasswordCommand = new RelayCommand(() =>
        {
            ShowPassword = !ShowPassword;
            OnPropertyChanged(nameof(GlobalPassword));
        });

        ResetSettingsCommand = new RelayCommand(ResetSettings, CanRunNormalCommand);

        /*
         * ===== 空间规划（用户 2026-09-22 需求）=====
         *
         * 一个入口：RefreshParallelAdviceCommand —— 按当前可用空间算出"建议并行几个"
         * （纯计算 + stat，放后台）。
         * ⛔ 原来这里还有一个红色按钮命令（危险模式 + 自测 + 凭证）：那整套已按
         * 用户 2026-09-25 第 32 条整块退役 —— 那条需求现在是③页「2 删除操作」的第三档。
         */
        RefreshParallelAdviceCommand = new AsyncRelayCommand(RefreshParallelAdviceAsync, CanRunNormalCommand);

        SelectAllTasksCommand = new RelayCommand(SelectAllTasks, CanChangeTaskSelection);
        SelectNoneTasksCommand = new RelayCommand(SelectNoneTasks, CanChangeTaskSelection);
        InvertTaskSelectionCommand = new RelayCommand(InvertTaskSelection, CanChangeTaskSelection);
        SelectSoleTaskCommand = new RelayCommand(SelectSoleTask, IsRowInList);

        HookTaskSelectionTracking();

        Initialize();
    }

    private void Initialize()
    {
        try
        {
            // 文件日志开关跟着设置走（默认开）。用户关掉它时不该再产生新日志文件。
            //
            // ⚠ 启动期这一句会建三个文件并扫日志目录 —— 在坏盘上会卡在内核 I/O 里把窗口拖死
            // （2026-11-10 真机）。改成有界等待：超时就**本次会话关掉文件日志**，
            // 这样后面每条 AppendLog 都会被 `_currentLogFilePath` 为空挡掉，不会反复卡。
            bool logReady = ArchiveFixer.Helpers.SafeIo.Run(
                () =>
                {
                    _logService.Initialize(Settings.EnableLog);
                    return true;
                },
                false,
                TimeSpan.FromMilliseconds(2000));

            if (!logReady)
            {
                _logService.EnableFileLog = false;
            }
            // 危险路径上被降级处理的失败，至少要在日志里留一句（⛔ 不改控制流）。
            ArchiveFixer.Helpers.AppDiagnostics.ProblemSink = (where, ex) =>
                AppendLog("WARN", "已降级：" + where + " 失败（" + ex.GetType().Name + "：" + ex.Message + "）");
        }
        catch
        {
            // 日志初始化失败不能导致程序无法启动。
        }

        AppendLog("INFO", "软件启动");

        /*
         * 诊断日志 v2：启动即写本次设置与工具状态（脱敏，⛔ 不写密码）。
         * 以后用户只发日志时，不必再问“他是什么设置、工具在哪一版”。
         */
        DiagnosticLog.WriteSettings(
            _logService,
            Settings,
            ArchiveFixer.Engines.ToolLocator.Default.DescribeAvailability());

        /*
         * 弹窗"要不要响铃闪任务栏"的决定写进日志（用户 2026-09-26）：
         * 现在的口径是**只有窗口没能出现在最前面时才提醒**，所以"这次为什么响 / 为什么没响"
         * 必须答得出来 —— 用户问过一次"这个提醒的作用是什么"，日志就是那个答案。
         * 静默档（已在前台）只记 INFO，提醒档记 WARN（一眼能看见）。
         */
        ArchiveFixer.Helpers.WindowAttention.Note = line => AppendLog(
            line.Contains("安静显示", StringComparison.Ordinal) ? "INFO" : "WARN",
            "窗口提醒：" + line);

        /*
         * "一个引擎都用不了"的提示必须现算，**不许写死 7z 的路径**。
         *
         * 判据是"任一引擎可用"（EngineRouter.IsAvailable），而默认优先级是 UnRAR → 7-Zip：
         * 旧文案写死"未找到 tools\7zip\7z.exe"，于是真正缺 UnRAR 的人被引去修一个本来没问题的
         * 7z 目录，自己配了外部 7z 的人更是被指向一个他根本没在用的路径。
         * 现在由 ToolLocator 现算两条期望路径 + "任装其一即可" + 当前优先级（路径只有它一个来源）。
         */
        if (!_archiveEngine.IsAvailable)
        {
            AppendLog("WARN", "软件可启动，但解压时会失败。" + ToolLocator.Default.DescribeNoEngineAvailable());
        }

        if (!ToolLocator.Default.SevenZipDllExists)
        {
            // 这一条判定的就是 7z.dll，所以路径要写**真实的那一个**（不再拼一个可能过期的字面量）。
            AppendLog(
                "WARN",
                $"未找到 {ToolLocator.Default.SevenZipDllPath}（7-Zip 命令行解压需要它），请确认 7-Zip 的文件完整；" +
                "只解 RAR 的话，装一个 UnRAR 也能用。");
        }

        /*
         * 把"这一次会按什么顺序分派"写进日志：优先级设置到底生效没有，事后能从这里看到。
         * 以前这件事在界面上完全看不出来 —— 用户改了顺序、跑完一批，
         * 日志里既没有"用了哪个引擎"，也没有"为什么是它"。
         */
        if (_archiveEngine is EngineRouter router)
        {
            AppendLog("INFO", router.DescribeDispatch());
        }

        Workspace.LogLeftover();
        PasswordBook.AutoLoad();

        UpdateSummary();

        /*
         * 自动保存的基线：把"整个构造过程全部走完之后的那一份设置"记成已存指纹 ——
         * 于是启动本身不写盘，只有**用户真的改了东西**才会落盘（见 AutoSaveSettingsIfChanged）。
         *
         * ⚠ 基线必须在**最后**取：②③④⑥四页的编辑器与⑤打包页在构造时会把若干默认档补齐，
         * 挂在构造开头取的话，这些"程序自己的初始化"会被当成用户改动 —— 开机 800ms 后
         * 白白写一次盘（真机上表现为"什么都没动，设置文件的时间戳却变了"）。
         */
        _lastSavedSettingsJson = _settingsService.Serialize(_settings);
    }
}


