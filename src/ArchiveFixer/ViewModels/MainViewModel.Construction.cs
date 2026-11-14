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

        Commands.Initialize(this);

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
        MarkSettingsBaseline();
    }
}

