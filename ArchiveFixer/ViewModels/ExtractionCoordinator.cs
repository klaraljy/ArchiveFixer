using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 批量解压流程协调器。
    /// 负责并发调度、单任务解压管线、停止后续/取消当前。
    /// </summary>
    internal sealed class ExtractionCoordinator
    {
        private readonly MainViewModel _vm;
        private readonly IArchiveEngine _archiveEngine;
        private readonly PasswordService _passwordService;
        private readonly PathService _pathService;
        private readonly DialogService _dialogService;

        /// <summary>递归解压（M4）。引擎、探测器、密码来源全部注入，递归层自己不碰密码本。</summary>
        private readonly RecursiveExtractor _recursiveExtractor;

        /// <summary>解压前预检最多试几个密码候选去列表：试太多次会让"一键"变成等待。</summary>
        private const int MaxPreflightPasswordAttempts = 3;

        /// <summary>
        /// 每个归档（**每一层**）最多真的试几个密码候选（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
        ///
        /// 为什么必须有：解压模式下一个候选 = **一次完整解压**。用户那两个 mp4 的第二层是加密分卷，
        /// 几百条密码本的包会被逐个候选整包重解一遍（几百 MB 起、磁盘一直在写），
        /// 表现就是"几十分钟不动"，最后还可能把"没试完"报成"密码错误"。
        ///
        /// 到上限时的状态是 <see cref="StatusText.PasswordAttemptLimitReached"/>，**不是**"密码错误"。
        ///
        /// 说明：这里目前是常量而不是设置项 —— 本轮改动不允许碰 <c>Models/AppSettings.cs</c> 与设置窗口，
        /// 把它做成 `MaxPasswordAttemptsPerLayer` 设置项是后续动作（见报告里的移交项）。
        /// </summary>
        internal const int MaxPasswordAttemptsPerLayer = 10;

        /// <summary>合并提示里最多列几个文件名（再多就让用户去看失败清单），别弹一个占满屏幕的框。</summary>
        private const int MaxPasswordFailureNamesInDialog = 10;

        private CancellationTokenSource? _operationCts;
        private readonly List<CancellationTokenSource> _runningTaskCts = new();
        private bool _isExtracting;

        /*
         * 本批因密码没通过而失败的任务（密码错误 / 达到密码尝试上限）。
         *
         * 旧逻辑在每个任务的循环体里各弹一次模态框，而且用的是**同步** Dispatcher.Invoke：
         * 50 个错包 = 50 次阻塞点击，批量跑不动。现在只在这里登记，
         * 批次结束后由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
         * 并发解压时多个任务会同时写它，所以必须加锁（而不是靠"反正都在 UI 线程上"）。
         */
        private readonly List<(string FileName, string Status)> _passwordFailures = new();
        private readonly object _passwordFailuresLock = new();

        public ExtractionCoordinator(
            MainViewModel vm,
            IArchiveEngine archiveEngine,
            PasswordService passwordService,
            PathService pathService,
            DialogService dialogService)
        {
            _vm = vm;
            _archiveEngine = archiveEngine;
            _passwordService = passwordService;
            _pathService = pathService;
            _dialogService = dialogService;

            _recursiveExtractor = new RecursiveExtractor(
                archiveEngine,
                new MagicArchiveProber(),
                BuildRecursionPasswordCandidates);
        }

        private AppSettings Settings => _vm.Settings;

        /// <summary>
        /// 收尾重活的产物：后台线程只负责碰文件系统，结论与日志都通过它回传给 UI 线程，
        /// 免得后台线程去动界面集合 / 任务状态。
        /// </summary>
        private sealed class PostProcessWorkResult
        {
            public OutputVerificationResult Verification { get; init; } = new();

            public List<(string Level, string Message)> LogEntries { get; init; } = new();

            public bool BudgetExceeded { get; init; }

            public string BudgetMessage { get; init; } = string.Empty;

            public CollectResult? Collected { get; init; }
        }

        /// <summary>
        /// 解压成功之后的收尾：校验落盘结果 → 归集 → 视开关清理源包（M3）。
        ///
        /// 顺序不能换：
        /// 1. 先校验 —— 没有校验就没有"删除源包"的资格（AGENTS.md §9.5）；
        /// 2. 再归集 —— 归集是移动；先归集再校验会算不准（文件已经不在原输出目录）；
        /// 3. 最后清理源包 —— 它依赖前两步的结论。
        ///
        /// 任何一步失败都**不改变**"解压成功"这个结论，只是把结论写进日志与任务字段；
        /// 不要因为归集或清理失败就把任务标成失败 —— 用户的文件确实解出来了。
        ///
        /// 线程规则（这是 P0 修复的关键）：
        /// 收尾里的"全目录枚举 / 二次遍历 / 归集移动 / 删除源包"**全是同步磁盘活**，
        /// 之前整段跑在 UI 线程上（整条管线没有 ConfigureAwait(false)，await 的续体全回 Dispatcher），
        /// 780MB 的包解出几千个文件时窗口彻底无响应 —— 与"抠出内嵌归档"是同一个坑，
        /// 那边已经改成 Task.Run 并写了注释，收尾这段是漏改。
        /// 现在重活统一进 <see cref="RunPostProcessWork"/> 交给 Task.Run，
        /// await 回来（没有 ConfigureAwait(false)，续体仍在 UI 上下文）才写任务状态与日志。
        ///
        /// 取消规则（P1）：收尾每一步之前都查令牌。用户按了「取消当前」就不许再移动产物、更不许删源包
        /// （AGENTS.md §9.5：取消、部分完成、校验失败一律不删）。取消由上层 catch 落成"已取消"，不得显示成功。
        /// </summary>
        private async Task PostProcessSuccessAsync(
            ArchiveTask task,
            string engineArchivePath,
            string password,
            string outputRedirectNote,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1) 校验：拿到引擎声明的条目数与总大小，和落盘结果对一遍。
            // 清单同样取自**真正解开的那份归档**：内嵌归档要拿抠出来的文件去列，源文件 7z 根本打不开。
            ArchiveListResult expected = await _archiveEngine.ListAsync(
                ArchiveRequest.For(engineArchivePath, password),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // 开关在进后台之前读一次：设置是用户可改的，别让后台线程读到半路改掉的值。
            bool collectResults = Settings.CollectResultsToDirectory;
            string collectTargetDirectory = Settings.CollectTargetDirectory;
            bool deleteSource = Settings.DeleteSourceAfterExtract;

            PostProcessWorkResult work = await Task.Run(
                () => RunPostProcessWork(
                    task,
                    expected,
                    collectResults,
                    collectTargetDirectory,
                    deleteSource,
                    cancellationToken),
                cancellationToken);

            // 回到 UI 线程：只做状态与日志，不再碰大盘。
            foreach ((string level, string message) in work.LogEntries)
            {
                AppendLog(level, message);
            }

            if (work.BudgetExceeded)
            {
                task.Status = StatusText.ExtractFailed;
                task.ErrorMessage = work.BudgetMessage;

                // 不发布、不清理：产物留在原地让用户自己判断，源文件更不能删。
                StopAfterCurrent();
                return;
            }

            task.IsOutputVerified = work.Verification.Verified;

            // 把"实际输出到别处"这件事写进任务对象：输出目录被自动改名时，用户必须在任务上看得见，
            // 不能只在日志里留一句就过去（AGENTS.md 不变量 6 的同一精神：结论不能静默）。
            task.VerifyMessage = string.IsNullOrWhiteSpace(outputRedirectNote)
                ? work.Verification.Message
                : $"{work.Verification.Message}；{outputRedirectNote}";

            if (work.Collected != null && work.Collected.Success)
            {
                task.CollectedPath = work.Collected.DestinationPath;
            }
        }

        /// <summary>
        /// 收尾重活本体：落点校验 → 结果校验 → 预算事后判定 → 归集 → 清理源包。
        ///
        /// **只允许在后台线程上跑**（见 <see cref="PostProcessSuccessAsync"/> 的线程规则）：
        /// 这里只碰文件系统，不写任务状态、不写界面日志集合，要说的都放进 LogEntries 回传。
        /// </summary>
        private PostProcessWorkResult RunPostProcessWork(
            ArchiveTask task,
            ArchiveListResult expected,
            bool collectResults,
            string collectTargetDirectory,
            bool deleteSource,
            CancellationToken cancellationToken)
        {
            var logEntries = new List<(string Level, string Message)>();

            cancellationToken.ThrowIfCancellationRequested();

            OutputVerificationResult verification = OutputVerifier.Verify(
                task.OutputPath,
                expected.Success ? expected : null);

            // 解压后落点校验（第二道防线）：产物必须都在目标根目录之内。
            if (Directory.Exists(task.OutputPath))
            {
                foreach (string produced in Directory.EnumerateFiles(task.OutputPath, "*", SearchOption.AllDirectories))
                {
                    if (!ArchivePathGuard.IsInsideRoot(task.OutputPath, produced, out string landReason))
                    {
                        logEntries.Add(("ERROR", $"{task.FileName}：产物落点异常 —— {landReason}"));
                    }
                }
            }

            /*
             * 运行时预算的事后判定。
             *
             * 7z 写盘的时候我们拦不住（外部进程，我们看不到它的写入），所以"超限就停"只能在事后做：
             * 量一遍产物，超了就**停止后续任务**并把这一单标出来 —— 至少不会让整盘被一个包吃掉。
             * 这条只在引擎给不出清单时才真正有意义，但事后量一遍很便宜，就一直做。
             */
            (int producedFiles, long producedSize) = OutputVerifier.Measure(task.OutputPath);
            ResourceBudgetOptions budgetLimits = ResourceBudgetOptions.Default;

            if (producedSize > budgetLimits.MaxTotalSize || producedFiles > budgetLimits.MaxFileCount)
            {
                string budgetMessage =
                    $"解压产物超出资源预算：{producedFiles} 个文件 / {producedSize} 字节" +
                    $"（上限 {budgetLimits.MaxFileCount} 个 / {budgetLimits.MaxTotalSize} 字节）。已停止后续任务。";

                logEntries.Add(("ERROR", $"{task.FileName}：{budgetMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    BudgetExceeded = true,
                    BudgetMessage = budgetMessage
                };
            }

            logEntries.Add((verification.Verified ? "INFO" : "WARN", $"{task.FileName}：结果校验 —— {verification.Message}"));

            // 归集是**移动**产物，动手之前再查一次令牌（取消就不再移动）。
            cancellationToken.ThrowIfCancellationRequested();

            // 2) 归集（可选）
            CollectResult? collected = null;

            if (collectResults)
            {
                if (!verification.Verified)
                {
                    logEntries.Add(("WARN", $"{task.FileName}：校验未通过，已跳过结果归集。"));
                }
                else
                {
                    collected = new ResultCollector().Collect(task, collectTargetDirectory);

                    logEntries.Add((collected.Success ? "INFO" : "WARN", $"{task.FileName}：结果归集 —— {collected.Message}"));
                }
            }

            /*
             * 删除是**不可逆**的，AGENTS.md §9.5 明确要求"取消、部分完成、校验失败时一律不删"，
             * 所以清理源包之前单独再查一次令牌 —— 用户刚按下「取消当前」时最不该发生的就是删源包。
             */
            cancellationToken.ThrowIfCancellationRequested();

            // 3) 清理源包（默认关闭；未通过校验时服务内部会拒绝执行）
            SourceCleanupResult cleanup = new SourceCleanupService().Cleanup(
                task,
                verification,
                deleteSource);

            if (cleanup.Attempted)
            {
                logEntries.Add((cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN", $"{task.FileName}：清理源包 —— {cleanup.Message}"));
            }

            return new PostProcessWorkResult
            {
                Verification = verification,
                LogEntries = logEntries,
                Collected = collected
            };
        }

        /// <summary>
        /// 给递归层提供密码候选（只给值，不给来源说明）。
        /// 顺序与单层解压完全一致 —— 递归的内层包同样是"用户的包"，不该用另一套规则。
        ///
        /// 这里就截到每层上限：递归里每个候选同样是一次完整解压尝试，
        /// 让几百个候选排着队进去等于把"一键处理"变成没人看得懂的长时间等待（AGENTS.md §9.2）。
        /// </summary>
        private IReadOnlyList<string> BuildRecursionPasswordCandidates(string archivePath)
        {
            var probe = new ArchiveTask(archivePath);

            return _passwordService
                .GetPasswordCandidates(
                    probe,
                    Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                    _passwordService.Passwords,
                    Settings.TryEmptyPasswordFirst,
                    Settings.EnableSidecarPassword)
                .Take(MaxPasswordAttemptsPerLayer)
                .Select(p => p.Value ?? string.Empty)
                .ToList();
        }

        /// <summary>
        /// 递归解压一个任务，并把结果落到任务状态上。
        /// 多分支时**必须问用户**（不变量 8），问完只处理用户确认的那批候选。
        /// </summary>
        /// <param name="engineArchivePath">
        /// 真正交给引擎的归档路径。内嵌归档时它是工作区里抠出来的那个文件（不是 <c>task.CurrentPath</c>）。
        /// </param>
        private async Task RunRecursiveAsync(
            ArchiveTask task,
            string engineArchivePath,
            string outputPath,
            CancellationToken cancellationToken)
        {
            RecursionMode mode = string.Equals(Settings.RecursionMode, "AllBranches", StringComparison.OrdinalIgnoreCase)
                ? RecursionMode.AllBranches
                : RecursionMode.SingleChain;

            var limits = new RecursionLimits
            {
                MaxDepth = Math.Max(1, Settings.MaxRecursionDepth)
            };

            task.Status = StatusText.Extracting;
            task.ProgressText = StatusText.ProgressProcessing;
            task.LastUpdatedTime = DateTime.Now;

            /*
             * 递归核心（RecursiveExtractor）从 task.CurrentPath 取"第 0 层要解哪个归档"。
             * 内嵌归档真正的归档是抠出来的那个文件，所以这里给它一个只带正确路径的**探针任务**，
             * 而不是把 task.CurrentPath 临时改掉 —— 源文件路径是改名 / 清理 / 统计的依据，
             * 任何"改了再改回来"的写法都会在这些地方留下一个窗口期（异常时更是永远改不回来）。
             * 递归结论仍然应用在真正的 task 上（ApplyRecursionResult(task, result)）。
             */
            ArchiveTask recursionTask = string.Equals(
                    engineArchivePath,
                    task.CurrentPath,
                    StringComparison.OrdinalIgnoreCase)
                ? task
                : new ArchiveTask(engineArchivePath);

            AppendLog("INFO", $"{task.FileName}：开始递归解压（模式 {mode}，最大 {limits.MaxDepth} 层）。");

            RecursionResult result = await _recursiveExtractor.ExtractAsync(
                recursionTask, outputPath, mode, previousDecision: null, cancellationToken);

            if (result.StopReason == RecursionStopReason.NeedsDecision && result.Decision != null)
            {
                bool expandAll = await ShowConfirmOnUiThreadAsync(
                    result.Decision.Prompt + Environment.NewLine + Environment.NewLine +
                    "选“确定”：把这些内层归档也解开。选“取消”：只保留当前这一层的结果。");

                if (expandAll)
                {
                    result = await _recursiveExtractor.ExtractAsync(
                        recursionTask, outputPath, mode, result.Decision, cancellationToken);
                }
                else
                {
                    /*
                     * 用户只想保留当前这一层。
                     *
                     * 这里**不能**就这样返回 NeedsDecision：那会让用户以为"东西已经解到输出目录了"，
                     * 而按不变量 12，需要决定时产物还留在工作区里 —— 文案与实现不符等于骗人。
                     *
                     * 做法：把已经解好的第 0 层产物从工作区搬到输出目录，并把它当成 Completed 继续走
                     * "校验 → 归集 → 清理"。不重新解压一遍：大包重解代价太大，而产物本来就在工作区里。
                     */
                    string? layerZeroOutput = result.Layers
                        .FirstOrDefault(l => l.Depth == 0 && l.Success)?.OutputPath;

                    if (!string.IsNullOrWhiteSpace(layerZeroOutput) && Directory.Exists(layerZeroOutput))
                    {
                        /*
                         * 搬运是同步磁盘活（递归枚举 + File.Move），同样必须离开 UI 线程 ——
                         * 理由与收尾那一段完全相同（见 PostProcessSuccessAsync 的线程规则）。
                         */
                        int moved = await Task.Run(
                            () => MoveDirectoryContent(layerZeroOutput, outputPath),
                            cancellationToken);

                        result = new RecursionResult
                        {
                            StopReason = RecursionStopReason.Completed,
                            Completed = true,
                            PartiallyCompleted = false,
                            Layers = result.Layers,
                            FinalOutputPath = outputPath,
                            Summary = $"按你的选择只解开了当前这一层，已输出 {moved} 个文件。"
                        };
                    }
                    else
                    {
                        // 第 0 层本身就没成功：保持原结论（部分完成），让它如实报告。
                        AppendLog("WARN", $"{task.FileName}：当前这一层没有可用产物，保留原结论。");
                    }
                }
            }

            ApplyRecursionResult(task, result);
        }

        /// <summary>
        /// 把递归结论翻译成任务状态。
        /// 规则：完成 = 成功；部分完成 = **绝不显示成功**；需要用户决定 = 等用户；
        /// 其余（上限 / 密码 / 损坏 / 取消）都要说清停在哪一层、为什么。
        /// </summary>
        private void ApplyRecursionResult(ArchiveTask task, RecursionResult result)
        {
            foreach (RecursionLayerReport layer in result.Layers)
            {
                AppendLog(
                    layer.Success ? "INFO" : "WARN",
                    $"  ├ 第 {layer.Depth} 层：{Path.GetFileName(layer.ArchivePath)} → {layer.Status}" +
                    (string.IsNullOrWhiteSpace(layer.Message) ? string.Empty : $"，{layer.Message}"));
            }

            task.ErrorMessage = result.Summary;

            switch (result.StopReason)
            {
                case RecursionStopReason.Completed:
                    task.Status = StatusText.ExtractSuccess;
                    task.ProgressText = StatusText.ProgressCompleted;
                    break;

                case RecursionStopReason.NeedsDecision:
                    task.Status = StatusText.PartiallyCompleted;
                    task.ProgressText = StatusText.ProgressCompleted;
                    break;

                case RecursionStopReason.UserCancelled:
                    task.Status = StatusText.Cancelled;
                    task.ProgressText = StatusText.Cancelled;
                    break;

                default:
                    task.Status = StatusText.PartiallyCompleted;
                    task.ProgressText = StatusText.ProgressFailed;
                    break;
            }

            task.LastUpdatedTime = DateTime.Now;

            AppendLog(result.Completed ? "INFO" : "WARN", $"{task.FileName}：{result.Summary}");

            // 递归产物同样要走"校验 → 归集 → 可选清理"；这里传 recursive=true，
            // 因为外层归档的条目数和最终产物根本不是一回事，不能拿它当预期值。
            if (result.Completed)
            {
                task.OutputPath = string.IsNullOrWhiteSpace(result.FinalOutputPath) ? task.OutputPath : result.FinalOutputPath;
            }
        }

        /// <summary>
        /// 把目录内容（保持子目录结构）移动到目标目录，同名自动改名**绝不覆盖**，返回移动的文件数。
        /// 只在"用户选择只保留当前一层"这条路径上用。
        /// </summary>
        private static int MoveDirectoryContent(string sourceDirectory, string targetDirectory)
        {
            int moved = 0;

            SafePathHelper.EnsureDirectoryExists(targetDirectory);

            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string relative = Path.GetRelativePath(sourceDirectory, file);
                    string destination = Path.Combine(targetDirectory, relative);

                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(destination) ?? targetDirectory);

                    if (File.Exists(destination))
                    {
                        destination = SafePathHelper.AutoRenameFilePath(destination);
                    }

                    File.Move(file, destination);
                    moved++;
                }
                catch
                {
                    // 单个文件搬不动不该让整件事失败：日志里已经能看出解压本身是成功的。
                }
            }

            return moved;
        }

        /// <summary>
        /// 在 **UI 线程** 上弹确认框。
        ///
        /// 为什么必须显式调度（这是实测出来的卡死原因）：
        /// 引擎调用与抠出分别用了 ConfigureAwait(false) 和 Task.Run，管线拿到递归结论时
        /// **已经不在 UI 线程上**。此时直接 MessageBox.Show 会创建出一个没人泵消息的窗口 ——
        /// 用户看到的现象是"点了按钮之后什么都动不了、没有弹窗、连 X 都点不掉"，
        /// 而进程既不占 CPU、也没有子进程、磁盘也没在忙，看着像死锁。
        /// 命令行直接调 RecursiveExtractor 会秒回 NeedsDecision，卡的就是这一步弹窗。
        /// </summary>
        private Task<bool> ShowConfirmOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return Task.FromResult(_dialogService.ShowConfirm(message));
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowConfirm(message)).Task;
        }

        /// <summary>
        /// 在 UI 线程上弹警告（**异步**，不阻塞调用线程）。
        ///
        /// 与 <see cref="ShowConfirmOnUiThreadAsync"/> 同一套做法，取代原先任务循环里的
        /// 同步 <c>Application.Current.Dispatcher.Invoke</c>：
        /// ① 同步 Invoke 会让调用线程一直等模态框关掉（将来从后台线程调用时就是死等）；
        /// ② <c>Application.Current</c> 为 null 时（单元测试 / 无界面宿主）它会直接 NRE。
        /// </summary>
        private Task ShowWarningOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                // 没有 WPF 应用：只留日志（调用方已经写过），绝不在这里弹模态框 —— 那会阻塞调用方且没人点得掉。
                return Task.CompletedTask;
            }

            if (dispatcher.CheckAccess())
            {
                _dialogService.ShowWarning(message);
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowWarning(message)).Task;
        }

        /// <summary>把"密码没通过"的任务登记到本批（只登记，不弹窗）。</summary>
        private void RecordPasswordFailure(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            if (task.Status != StatusText.WrongPassword &&
                task.Status != StatusText.PasswordAttemptLimitReached)
            {
                return;
            }

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            lock (_passwordFailuresLock)
            {
                _passwordFailures.Add((fileName, task.Status));
            }
        }

        private void ClearPasswordFailures()
        {
            lock (_passwordFailuresLock)
            {
                _passwordFailures.Clear();
            }
        }

        /// <summary>
        /// 把本批"密码错误 / 达到密码尝试上限"的任务**合并成一次**提示。
        ///
        /// 旧做法：在每个任务内部各弹一次模态框，50 个错包 = 50 次阻塞点击 ——
        /// 批量处理根本跑不动，用户看到的就是"点了没反应"。
        /// 新做法：循环里只登记（<see cref="RecordPasswordFailure"/>），批次结束后在这里一次说清：
        /// 有几个、都是谁、其中几个是"到上限"（不是密码错误）。
        /// 日志与弹窗是同一句话，所以没有界面时（无头宿主）也留得下证据。
        /// </summary>
        private async Task ShowPasswordFailuresSummaryAsync()
        {
            List<(string FileName, string Status)> failures;

            lock (_passwordFailuresLock)
            {
                if (_passwordFailures.Count == 0)
                {
                    return;
                }

                failures = _passwordFailures.ToList();
                _passwordFailures.Clear();
            }

            int wrongPasswordCount = failures.Count(f => f.Status == StatusText.WrongPassword);
            int attemptLimitCount = failures.Count - wrongPasswordCount;

            /*
             * 措辞有个坑：日志会被 PasswordMasker 兜底脱敏，规则是"密码 + 冒号 → 本行剩余全部打码"。
             * 所以这里**不要在"密码"后面直接跟冒号**，否则用户看到的是"本批 3 个包…密码：******"，
             * 文件名与原因全被吃掉（实测踩过）。原因写成"原因：密码错误…"这种形态是安全的。
             */
            string reason = attemptLimitCount == 0
                ? "密码错误或缺少正确密码"
                : wrongPasswordCount == 0
                    ? $"{StatusText.PasswordAttemptLimitReached}（候选还没试完就按上限停了，不等于密码错误）"
                    : $"密码错误 {wrongPasswordCount} 个 / {StatusText.PasswordAttemptLimitReached} {attemptLimitCount} 个" +
                      "（后者只是候选没试完，不等于密码错误）";

            var builder = new StringBuilder();

            builder.Append(failures.Count == 1
                ? "有 1 个包没能解开"
                : $"本批 {failures.Count} 个包没能解开");

            builder.Append("，原因：");
            builder.Append(reason);
            builder.AppendLine();

            foreach (string name in failures.Select(f => f.FileName).Take(MaxPasswordFailureNamesInDialog))
            {
                builder.AppendLine(name);
            }

            if (failures.Count > MaxPasswordFailureNamesInDialog)
            {
                builder.AppendLine($"…等共 {failures.Count} 个（完整清单见「复制失败列表 / 导出失败清单」）。");
            }

            string message = builder.ToString().TrimEnd();

            // 日志与弹窗同源：日志一定写（无界面宿主也留得下证据），弹窗只在真的有界面时弹。
            // 这里把换行换成"；"是为了让日志一行看完 —— 换行后的内容不会被脱敏规则吃掉。
            AppendLog("WARN", message.Replace(Environment.NewLine, "；"));

            await ShowWarningOnUiThreadAsync(message);
        }

        private string GlobalPassword => _vm.GlobalPassword;
        private string SelectedOutputDirectory => _vm.SelectedOutputDirectory;
        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
        private bool IsStopping { get => _vm.IsStopping; set => _vm.IsStopping = value; }
        private bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }
        private void AppendLog(string message) => _vm.AppendLog(message);
        private void AppendLog(string level, string message) => _vm.AppendLog(level, message);
        private void UpdateSummary() => _vm.UpdateSummary();

        public async Task StartExtractAsync()
        {
            if (_isExtracting)
            {
                AppendLog("WARN", "当前已经在批量解压中，忽略重复启动。");
                return;
            }

            var selectedTasks = Tasks.Where(x => x.IsSelected).ToList();

            if (selectedTasks.Count == 0)
            {
                _dialogService.ShowWarning("请先选择需要解压的任务。");
                return;
            }

            if (!_archiveEngine.IsAvailable)
            {
                _dialogService.ShowError("未找到 tools\\7zip\\7z.exe，无法解压。");
                AppendLog("ERROR", StatusText.SevenZipMissing);
                return;
            }

            IsBusy = true;
            IsStopping = false;
            _isExtracting = true;

            // 本批的密码失败登记从零开始：上一批的残留不能让这一批多弹一次提示。
            ClearPasswordFailures();

            try
            {
                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = new CancellationTokenSource();

                AppendLog("INFO", "开始批量解压");

                int maxParallel = Math.Clamp(Settings.MaxParallelExtractCount, 1, 8);

                var runningTasks = new List<Task>();

                foreach (ArchiveTask task in selectedTasks)
                {
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        AppendLog("WARN", "已停止后续任务，不再启动新的解压任务。");
                        break;
                    }

                    // 等待有空闲并发位；期间若用户点了“停止后续”，不再为后面的任务等位。
                    while (runningTasks.Count >= maxParallel)
                    {
                        Task finished = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(finished);
                        await finished;

                        if (IsStopping || _operationCts.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    /*
                     * 关键修复（实测复现）：上面的 break 只跳出了"等并发位"的 while，
                     * 落到这里仍会把当前任务 Add 进去 —— 表现为"点了停止后续，另一个包照样跑成功了"，
                     * 违反不变量 9（停止后续只阻止**启动后续**，绝不能再启动新任务）。
                     * 所以在真正启动之前必须再查一次。
                     */
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        AppendLog("WARN", "已停止后续任务，不再启动新的解压任务。");
                        break;
                    }

                    runningTasks.Add(ProcessExtractTaskAsync(task));
                }

                // 等待所有已启动的任务结束（包括“停止后续”后仍在运行的任务）。
                while (runningTasks.Count > 0)
                {
                    Task finished = await Task.WhenAny(runningTasks);
                    runningTasks.Remove(finished);
                    await finished;
                }

                AppendLog("INFO", "批量解压完成");

                /*
                 * 密码错误**合并成一次提示**（P0）。
                 * 逐个任务弹模态框时，50 个错包就是 50 次阻塞点击 —— 批量处理根本跑不动。
                 * 放在批次结束后：此时才谈得上"本批 N 个包"，也不会挡住正在跑的任务。
                 */
                await ShowPasswordFailuresSummaryAsync();
            }
            finally
            {
                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = null;

                _isExtracting = false;
                IsBusy = false;
                IsStopping = false;

                UpdateSummary();
            }
        }

        private async Task ProcessExtractTaskAsync(ArchiveTask task)
        {
            var taskCts = new CancellationTokenSource();
            _runningTaskCts.Add(taskCts);

            try
            {
                await ExtractSingleTaskAsync(task, taskCts.Token);
            }
            catch (OperationCanceledException)
            {
                task.Status = StatusText.Cancelled;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.Cancelled;
                task.ErrorMessage = "任务已取消";
                task.EndTime = DateTime.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("WARN", $"任务已取消：{task.FileName}");
            }
            catch (Exception ex)
            {
                task.Status = StatusText.UnknownError;
                task.ErrorMessage = ex.Message;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressFailed;
                task.EndTime = DateTime.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"任务失败：{task.FileName}，{ex.Message}");
            }
            finally
            {
                _runningTaskCts.Remove(taskCts);

                try
                {
                    taskCts.Dispose();
                }
                catch
                {
                }

                UpdateSummary();
            }
        }

        private async Task ExtractSingleTaskAsync(ArchiveTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (task == null)
            {
                return;
            }

            /*
             * 分卷缺失时**不许开始**（AGENTS.md §6 第 7 条）。
             * 理由：分卷包里每一卷都是必需的数据片，缺一卷 7z 必然失败，
             * 让它跑一遍只会浪费用户时间、还可能留下半截输出目录；
             * 直接说清"缺哪几个卷"才是用户能行动的信息。
             */
            if (task.IsVolumeGroup && !task.IsVolumeComplete)
            {
                task.Status = StatusText.VolumeMissing;
                task.ErrorMessage = string.IsNullOrWhiteSpace(task.VolumeInfoText)
                    ? "分卷不完整，缺少分卷"
                    : task.VolumeInfoText;

                AppendLog("ERROR", $"分卷缺失，未开始解压：{task.FileName}，{task.ErrorMessage}");
                return;
            }

            bool tryExtractUnknown = Settings.UnknownFormatAction == "TryExtract";

            if (!task.IsArchive || task.DetectedFormat == "Unknown")
            {
                if (tryExtractUnknown)
                {
                    AppendLog("WARN", $"格式未知：{task.FileName}，已开启“尝试解压未知格式”，继续尝试。");
                }
                else
                {
                    /*
                     * 魔数说不认识，**不等于"它不是压缩包"** —— 只说明文件头不在我的签名表里。
                     * 基线里早就写下了正确原则：最终以 7-Zip 的结论为准。
                     *
                     * 所以这里让引擎亲自试一次列目录（只对用户勾选的任务做，代价是一次进程调用）：
                     *   · 列得出来 → 就是能解的容器（自解压安装器、签名表里没有的格式都算），继续处理；
                     *   · 列不出来 → 才跳过，并把"7-Zip 也打不开"写进原因，而不是含糊的"格式未知"。
                     */
                    ArchiveListResult probe = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(task.CurrentPath, string.Empty),
                        cancellationToken);

                    if (probe.Success && probe.FileCount > 0)
                    {
                        task.IsArchive = true;
                        task.EngineVerdict = $"{_archiveEngine.DisplayName} {_archiveEngine.Version} 能打开：{probe.FileCount} 个文件";

                        AppendLog("INFO", $"{task.FileName}：文件头不在签名表里，但 7-Zip 能打开（{probe.FileCount} 个文件），继续处理。");
                    }
                    else
                    {
                        task.Status = StatusText.Skipped;
                        task.Operation = StatusText.OpSkip;
                        task.ProgressText = StatusText.ProgressCompleted;
                        task.ErrorMessage = string.IsNullOrWhiteSpace(probe.Message)
                            ? "不是压缩包：7-Zip 也打不开"
                            : $"不是压缩包：7-Zip 打不开（{probe.Message}）";

                        AppendLog("WARN", $"跳过：{task.FileName} —— {task.ErrorMessage}");
                        return;
                    }
                }
            }

            if (!File.Exists(task.CurrentPath))
            {
                task.Status = StatusText.ExtractFailed;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressCompleted;
                task.ErrorMessage = "文件不存在";
                AppendLog("ERROR", $"文件不存在：{task.CurrentPath}");
                return;
            }

            task.StartTime = DateTime.Now;
            task.EndTime = null;
            task.ElapsedText = "-";
            task.Operation = StatusText.OpPrepare;
            task.Status = StatusText.WaitingExtract;
            task.ProgressText = StatusText.ProgressProcessing;
            task.ErrorMessage = string.Empty;
            task.LastUpdatedTime = DateTime.Now;

            var extractOptions = new ExtractOptions
            {
                ExtractToOriginalDirectory = Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = SelectedOutputDirectory,
                KeepArchiveNameFolder = Settings.KeepArchiveNameFolder,
                TestBeforeExtract = Settings.TestBeforeExtract,
                OverwriteMode = Settings.OverwriteMode,
                UseGlobalPassword = Settings.UseGlobalPasswordForAllTasks,
                GlobalPassword = GlobalPassword,
                TryPasswordList = true,
                TryEmptyPasswordFirst = Settings.TryEmptyPasswordFirst,
                CancelOnFirstSuccess = true,
                TryExtractUnknownFormat = tryExtractUnknown,
                MaxParallelExtractCount = Settings.MaxParallelExtractCount
            };

            extractOptions.Normalize();

            // 解压前算出来的"打算输出到哪"。它只是一个提议：下面可能因为目录已存在被改名。
            string requestedOutputPath = _pathService.BuildOutputPath(task, extractOptions);
            string outputPath = requestedOutputPath;
            string outputRedirectNote = string.Empty;

            try
            {
                if (!string.IsNullOrWhiteSpace(outputPath) &&
                    Directory.Exists(outputPath) &&
                    Directory.EnumerateFileSystemEntries(outputPath).Any())
                {
                    string newOutputPath = _pathService.AutoRenameDirectoryPath(outputPath);

                    AppendLog("WARN", $"输出目录已存在且非空，为避免混入旧文件，自动改用新目录：{newOutputPath}");

                    /*
                     * 实际落点与"打算的落点"不一致这件事**不许静默**：
                     * ① 下面会把实际落点写回 task.OutputPath（界面"输出目录"列看得见）；
                     * ② 校验结论里也带一句（task.VerifyMessage）；
                     * ③ 日志里额外提醒续解的影响 —— 一键处理的续解是按**解压前**的目录快照找内层包的，
                     *    内层包落进新目录时它找不到，用户看到的现象就是"第二层没解"。
                     *    这里的提示是让人一眼知道该去哪找，而不是让程序假装没发生。
                     */
                    outputRedirectNote = $"原定输出目录 {requestedOutputPath} 已存在且非空，本次实际输出到 {newOutputPath}";

                    AppendLog("WARN", $"{task.FileName}：{outputRedirectNote}。自动续解按解压前的目录查找内层包，若内层包落在新目录里可能不会被继续解开。");

                    outputPath = newOutputPath;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"检查输出目录失败，将继续使用原输出目录：{ex.Message}");
            }

            /*
             * 回写**实际最终输出目录**（P1）。
             *
             * 这是"产物到底在哪"的唯一权威来源：上面可能刚把目录从 xxx 改成了 xxx(1)，
             * 解压后校验、结果归集、清理源包、界面"输出目录"列、以及一键处理的续解全都以它为准
             * （续解那边要读的就是这个值，见报告里的移交项）。
             * 绝不允许只把改名写进日志、却让 task.OutputPath 停在"打算输出到哪"。
             */
            task.OutputPath = outputPath;

            // 每个任务都明确说一次实际落点：目录被改名时用户必须能立刻看出产物去了哪。
            AppendLog("INFO", $"{task.FileName}：本次实际输出目录 {outputPath}");

            /*
             * 内嵌归档（双面文件）：先按偏移把尾部那段真正的 ZIP 抠出来，
             * 再拿它去列目录 / 预检 / 解压 / 解压后校验。
             *
             * 为什么不能直接把源文件交给 7z：这种文件的 ZIP 内部偏移是**相对 ZIP 自己**的。
             * 7-Zip 会尝试用"文件末尾 EOCD 反推的基准偏移"容忍这种错位，但只容忍 8 MiB 以内
             * （本机 26.01 实测：前置 8,388,608 字节可以，8,388,609 字节就报
             * "Cannot open the file as archive"）；用户那个文件垫了 17,031,321 字节，必然被拒。
             * 改后缀也没用（资源管理器能打开，只是因为它的 ZIP 读取器容忍这种整体错位）。
             * 把 [偏移, EOF) 原样复制出来是唯一可靠、且完全不改源文件的做法。
             *
             * 换成局部变量 engineArchivePath，而**不去改 task.CurrentPath**：
             * 后者是源文件路径，改名、清理源包、结果统计、报告全都依赖它 ——
             * 一旦被换成工作区里的临时文件，"清理源包"会去删我们自己的中间产物，
             * 而真正的源文件永远得不到处理，报告里指向的也不再是用户给的那个文件。
             */
            string engineArchivePath = task.CurrentPath;

            if (task.EmbeddedArchiveOffset > 0)
            {
                string carveTarget = BuildEmbeddedArchivePath(task);

                long carveBytes = 0;

                try
                {
                    carveBytes = new FileInfo(task.CurrentPath).Length - task.EmbeddedArchiveOffset;
                }
                catch
                {
                    // 量不出大小不影响能不能抠，只是日志与空间预检里少个数字。
                }

                /*
                 * 临时空间预检：抠出来的中间文件落在 %AppData%（系统盘）。
                 * 一个 780MB 的双面文件要在系统盘上占掉 760MB —— 空间不够会写到一半失败，
                 * 还会把系统盘挤满。取不到空间就不拦（宁可试也不误拒），取到了才判。
                 */
                long? carveFree = SpaceChecker.GetAvailableFreeSpace(Path.GetDirectoryName(carveTarget));

                if (carveFree.HasValue && carveBytes > 0 && carveFree.Value < carveBytes + (256L * 1024 * 1024))
                {
                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage =
                        $"取出内嵌归档需要约 {carveBytes / 1024 / 1024} MB 临时空间，" +
                        $"但系统盘只剩 {carveFree.Value / 1024 / 1024} MB。请先清理空间再试。";
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                    return;
                }

                AppendLog(
                    "INFO",
                    $"{task.FileName}：正在取出内嵌归档（约 {carveBytes / 1024 / 1024} MB），这一步在后台做，界面不会卡住。");

                /*
                 * 关键修复：抠出是**同步磁盘拷贝**（几百 MB 量级），必须扔到后台线程。
                 * 之前直接在这里同步调用，拷贝期间整个界面线程被占住 ——
                 * 表现就是"点了一键处理之后程序卡死、窗口未响应"，只能强杀进程
                 * （实测：780MB 的双面文件拷 763MB，界面全程无响应）。
                 */
                int lastPercent = -1;

                // 每前进 20% 报一次：够密到能看出在动，又不至于把日志刷爆。
                var carveProgress = new Progress<int>(percent =>
                {
                    if (percent >= lastPercent + 20 || percent >= 100)
                    {
                        lastPercent = percent;
                        AppendLog("INFO", $"{task.FileName}：取出内嵌归档 {percent}%");
                    }
                });

                CarveResult carve = await Task.Run(
                    () => EmbeddedArchiveCarver.Carve(
                        task.CurrentPath,
                        task.EmbeddedArchiveOffset,
                        carveTarget),
                    cancellationToken);

                if (!carve.Success || !File.Exists(carve.OutputPath))
                {
                    task.Status = StatusText.ExtractFailed;
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressFailed;
                    task.ErrorMessage = string.IsNullOrWhiteSpace(carve.Message)
                        ? "取出内嵌归档失败"
                        : "取出内嵌归档失败：" + carve.Message;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                    return;
                }

                engineArchivePath = carve.OutputPath;

                AppendLog(
                    "INFO",
                    $"检测到内嵌归档：已从偏移 {task.EmbeddedArchiveOffset} 处取出 {carve.BytesWritten} 字节，" +
                    $"实际使用 {engineArchivePath} 解压。");
            }

            List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                task,
                Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                _passwordService.Passwords,
                Settings.TryEmptyPasswordFirst,
                Settings.EnableSidecarPassword);

            if (candidates.Count == 0)
            {
                candidates.Add(new PasswordItem
                {
                    Value = string.Empty,
                    Source = "Empty",
                    IsEnabled = true,
                    Remark = "空密码"
                });
            }

            /*
             * 密码候选的**硬上限**（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
             *
             * 解压模式下一个候选 = 一次完整解压：几百条密码本的包会被逐个候选整包重解一遍，
             * 用户看到的是"几十分钟不动、磁盘一直在写"。所以本层只试前 N 个，
             * 到上限时状态说"达到密码尝试上限"（**不是**"密码错误"）——
             * 包可能完全没问题，只是密码不在这批候选里。
             */
            int maxPasswordAttempts = Math.Min(candidates.Count, MaxPasswordAttemptsPerLayer);
            bool candidatesTruncated = candidates.Count > maxPasswordAttempts;

            if (candidatesTruncated)
            {
                AppendLog(
                    "WARN",
                    $"{task.FileName}：密码候选共 {candidates.Count} 个，超过单层上限 {MaxPasswordAttemptsPerLayer} 个，" +
                    $"本层只试前 {maxPasswordAttempts} 个（到上限会明确报“{StatusText.PasswordAttemptLimitReached}”，不会报成密码错误）。");
            }

            /*
             * M5 解压前预检：路径安全 + 资源预算。
             *
             * 为什么必须先"列目录"再解压：真正写盘的是外部 7z.exe，进程外拦不住 Zip Slip。
             * 能做的只有两件事 —— ①解压前把危险条目挑出来，拒绝这一单；
             * ②解压后再校验落点（见 PostProcessSuccessAsync）。这里做的是 ①。
             *
             * 加密头（-mhe）的包不给密码是列不出目录的，所以先拿密码候选去试列表：
             * 试成功的那一个同时也是最可能的解压密码，后面解压循环还会再用一遍。
             */
            ArchiveListResult? preflightList = null;

            foreach (PasswordItem candidate in candidates.Take(MaxPreflightPasswordAttempts))
            {
                ArchiveListResult attempt = await _archiveEngine.ListAsync(
                    ArchiveRequest.For(engineArchivePath, candidate.Value),
                    cancellationToken);

                if (attempt.Success)
                {
                    preflightList = attempt;
                    break;
                }
            }

            if (preflightList == null)
            {
                AppendLog("WARN", $"{task.FileName}：没能列出归档内容（可能是加密头或文件损坏），本次跳过路径预检与资源预算，解压后仍会校验落点。");
            }
            else
            {
                PathSafetyReport pathReport = ArchivePathGuard.CheckEntries(preflightList.Entries);

                if (!pathReport.IsSafe)
                {
                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage = "归档里有不安全的条目，已拒绝解压：" + pathReport.Summary;

                    AppendLog("ERROR", $"{task.FileName}：路径预检未通过 —— {pathReport.Summary}");
                    return;
                }

                long archiveSize = 0;

                try
                {
                    // 量的是**真正交给引擎的那个文件**：内嵌归档抠出来之后它比源文件小得多，
                    // 拿源文件大小当分母会把展开比算小，压缩炸弹就更容易蒙混过关。
                    archiveSize = new FileInfo(engineArchivePath).Length;
                }
                catch
                {
                    // 取不到大小就不做展开比判断，其余预算照常。
                }

                BudgetCheckResult budget = new ResourceBudget().CheckBeforeExtract(preflightList, archiveSize, outputPath);

                if (!budget.Allowed)
                {
                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage = "资源预算未通过：" + budget.Reason;

                    AppendLog("ERROR", $"{task.FileName}：资源预算未通过 —— {budget.Reason}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(budget.Reason))
                {
                    AppendLog("WARN", $"{task.FileName}：资源预算提示 —— {budget.Reason}");
                }
            }

            /*
             * 递归模式（M4）：不是"只解当前层"时，整条解压交给 RecursiveExtractor。
             * 它自己会解第 0 层、探测内层、按模式决定继续还是问用户，并受硬上限约束。
             * 放在预检之后：预检已经把 outputPath 算好，而且非归档 / 格式未知在前面已经分流走了。
             */
            if (!string.Equals(Settings.RecursionMode, "SingleLayer", StringComparison.OrdinalIgnoreCase))
            {
                await RunRecursiveAsync(task, engineArchivePath, outputPath, cancellationToken);
                return;
            }

            ArchiveOperationResult? lastResult = null;
            string selectedPassword = string.Empty;
            bool passwordConfirmed = false;

            /*
             * 关键修复：
             *
             * 旧逻辑：
             * candidates.Count > 1 就强制先执行 7z t。
             *
             * 问题：
             * 7z t 只是测试，不会产生解压文件。
             * 所以用户会看到 7-Zip Console 一直运行，但输出目录没有产物。
             *
             * 新逻辑：
             * 只有用户明确开启 TestBeforeExtract，或者任务明确标记 IsEncrypted，
             * 才先测试密码。
             */
            bool shouldTestPasswordBeforeExtract =
                Settings.TestBeforeExtract;

            if (shouldTestPasswordBeforeExtract)
            {
                task.Operation = StatusText.OpTest;
                task.Status = StatusText.Testing;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.FileName}：开始解压前测试。");

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    AppendLog("INFO", $"{task.FileName}：测试密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult testResult = await _archiveEngine.TestAsync(
                        ArchiveRequest.For(engineArchivePath, password),
                        cancellationToken);

                    lastResult = testResult;

                    if (testResult.DetectedErrorType == "Cancelled" ||
                        testResult.Status == StatusText.Cancelled ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (testResult.Success)
                    {
                        selectedPassword = password;
                        passwordConfirmed = true;

                        task.Status = StatusText.TestPassed;
                        task.PasswordStatus = string.IsNullOrEmpty(password) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                        task.ErrorMessage = string.Empty;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("INFO", $"{task.FileName}：测试通过。");
                        break;
                    }

                    if (testResult.DetectedErrorType == "WrongPassword")
                    {
                        task.PasswordStatus = StatusText.WrongPassword;
                        AppendLog("WARN", $"{task.FileName}：密码错误，继续尝试下一个候选密码。");
                        continue;
                    }

                    task.Status = testResult.Status;
                    task.ErrorMessage = testResult.Message;
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.FileName}：测试失败，原因：{testResult.Message}");
                    break;
                }

                if (!passwordConfirmed)
                {
                    /*
                     * "试到上限停了"和"候选全都试过、都说密码错"是两件事（AGENTS.md §9.2）：
                     * 前者的状态是"达到密码尝试上限"，不能让用户以为密码本里就一定没有正确密码。
                     * lastResult 不是 WrongPassword 时说明停因另有其人（损坏/权限…），照旧原样上报。
                     */
                    bool stoppedByAttemptLimit = candidatesTruncated &&
                        (lastResult == null || lastResult.DetectedErrorType == "WrongPassword");

                    if (stoppedByAttemptLimit)
                    {
                        task.Status = StatusText.PasswordAttemptLimitReached;
                        task.PasswordStatus = StatusText.PasswordNeed;
                        task.ErrorMessage =
                            $"已达到密码尝试上限：本层试了 {maxPasswordAttempts} 个候选（共 {candidates.Count} 个），未能确认密码。";
                    }
                    else if (lastResult != null && lastResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }
                    else if (lastResult != null)
                    {
                        task.Status = lastResult.Status;
                        task.ErrorMessage = lastResult.Message;
                    }
                    else
                    {
                        task.Status = StatusText.TestFailed;
                        task.ErrorMessage = "没有可用密码或测试失败";
                    }

                    task.EndTime = DateTime.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";

                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"解压前测试失败：{task.FileName}，原因：{task.ErrorMessage}");

                    /*
                     * 密码类失败**登记到批次**，由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
                     * 原来这里（以及下面的直接解压分支）各弹一次模态框、而且用的是同步 Dispatcher.Invoke：
                     * 一批几十个错包就要点几十次，还会阻塞调用线程。
                     */
                    RecordPasswordFailure(task);

                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                task.Operation = StatusText.OpExtract;
                task.Status = StatusText.Extracting;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.FileName}：测试通过，开始正式解压。");

                ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     new ArchiveRequest
     {
         ArchivePath = engineArchivePath,
         OutputPath = outputPath,
         Password = selectedPassword
     },
     extractOptions,
     cancellationToken);

                lastResult = extractResult;

                if (extractResult.DetectedErrorType == "Cancelled" ||
                    extractResult.Status == StatusText.Cancelled ||
                    cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (extractResult.Success)
                {
                    task.Status = StatusText.ExtractSuccess;
                    task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                    task.ErrorMessage = string.Empty;

                    // 密码成功记录仍然挂在**源文件**上：下次用户再导入这个文件时要能直接命中，
                    // 而工作区里抠出来的临时文件活不过这次任务。
                    _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);

                    await PostProcessSuccessAsync(task, engineArchivePath, selectedPassword, outputRedirectNote, cancellationToken);



                    AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                }
                else
                {
                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                }
            }
            else
            {
                /*
                 * 普通模式：
                 * 不先测试，直接解压。
                 *
                 * 好处：
                 * 1. 普通无密码压缩包会立刻产生解压产物。
                 * 2. 不会让用户误以为卡住。
                 * 3. 如果遇到密码错误，再尝试下一个密码。
                 */
                bool extractSuccess = false;
                bool hasWrongPassword = false;

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    selectedPassword = password;

                    task.Operation = StatusText.OpExtract;
                    task.Status = StatusText.Extracting;
                    task.ProgressText = StatusText.ProgressProcessing;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("INFO", $"{task.FileName}：开始解压，密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     new ArchiveRequest
     {
         ArchivePath = engineArchivePath,
         OutputPath = outputPath,
         Password = selectedPassword
     },
     extractOptions,
     cancellationToken);

                    lastResult = extractResult;

                    if (extractResult.DetectedErrorType == "Cancelled" ||
                        extractResult.Status == StatusText.Cancelled ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (extractResult.Success)
                    {
                        extractSuccess = true;

                        task.Status = StatusText.ExtractSuccess;
                        task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                        task.ErrorMessage = string.Empty;

                        // 同"先测试再解压"那条分支：密码成功记录挂源文件，不挂工作区里的临时文件。
                        _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);

                        await PostProcessSuccessAsync(task, engineArchivePath, selectedPassword, outputRedirectNote, cancellationToken);



                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                        break;
                    }

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        hasWrongPassword = true;
                        task.PasswordStatus = StatusText.WrongPassword;

                        AppendLog("WARN", $"{task.FileName}：密码错误，继续尝试下一个候选密码。");
                        continue;
                    }

                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");

                    break;
                }

                if (!extractSuccess)
                {
                    // 与"先测试再解压"分支同一条规则：试到上限 ≠ 候选全都试过、更 ≠ 密码错误（AGENTS.md §9.2）。
                    bool stoppedByAttemptLimit = candidatesTruncated && hasWrongPassword;

                    if (stoppedByAttemptLimit)
                    {
                        task.Status = StatusText.PasswordAttemptLimitReached;
                        task.PasswordStatus = StatusText.PasswordNeed;
                        task.ErrorMessage =
                            $"已达到密码尝试上限：本层试了 {maxPasswordAttempts} 个候选（共 {candidates.Count} 个），未能确认密码。";
                    }
                    else if (hasWrongPassword)
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }
                    else if (lastResult != null)
                    {
                        task.Status = lastResult.Status;
                        task.ErrorMessage = lastResult.Message;
                    }
                    else
                    {
                        task.Status = StatusText.ExtractFailed;
                        task.ErrorMessage = "未知解压失败";
                    }

                    if (task.Status == StatusText.WrongPassword || task.Status == StatusText.PasswordAttemptLimitReached)
                    {
                        AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                    }
                }
            }

            // 密码类失败登记到本批，批次结束后合并成一次提示（不再在任务循环里逐个弹模态框）。
            RecordPasswordFailure(task);

            task.EndTime = DateTime.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";

            task.Operation = StatusText.OpWaiting;
            task.ProgressText = StatusText.ProgressCompleted;
            task.LastUpdatedTime = DateTime.Now;
        }

        /// <summary>
        /// 内嵌归档抠出来之后落在哪：<c>%AppData%\ArchiveFixer\work\&lt;任务名&gt;\&lt;包基名&gt;.zip</c>。
        ///
        /// 两条刻意的选择：
        /// ① 放工作区（<see cref="PathService.WorkDirectory"/>）而不是源目录旁边 ——
        ///    中间产物不得写进源目录（AGENTS.md §6 第 12 条）。抠出来的这一段只是为了能解压，
        ///    不是用户要的东西，任务结束留在工作区由用户自己清；
        /// ② 文件名沿用**源文件的包基名**，不改成随机临时名 ——
        ///    密码本"名称:密码"的映射匹配用的就是包基名，随机名会让本来能命中的密码全部落空，
        ///    用户看到的现象会是"同一个包以前能解开，现在说密码错误"。
        /// </summary>
        private string BuildEmbeddedArchivePath(ArchiveTask task)
        {
            string taskId = FileNameHelper.SanitizeFileName(task.FileName);
            string baseName = FileNameHelper.SanitizeFileName(FileNameHelper.GetArchiveBaseName(task.CurrentPath));

            return Path.Combine(_pathService.WorkDirectory, taskId, baseName + ".zip");
        }

        public void StopAfterCurrent()
        {
            try
            {
                IsStopping = true;

                /*
                 * 说明：
                 * 这里取消的是批量操作 token，用来阻止启动后续任务。
                 *
                 * 正在运行的任务各自持有独立的取消源，
                 * 所以这里只停止后续，不强制结束当前任务。
                 *
                 * 如果用户想立即取消正在运行的任务，需要再点“取消当前任务”。
                 */
                _operationCts?.Cancel();

                AppendLog("WARN", "已请求停止后续任务，不再启动新的解压任务。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "停止后续任务失败：" + ex.Message);
            }
        }


        public void CancelCurrentTask()
        {
            try
            {
                if (_runningTaskCts.Count == 0)
                {
                    AppendLog("WARN", "当前没有正在执行的任务可取消。");
                    return;
                }

                foreach (CancellationTokenSource cts in _runningTaskCts.ToList())
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                    }
                }

                AppendLog("WARN", "已请求取消当前正在执行的任务，正在结束 7-Zip 进程。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "取消当前任务失败：" + ex.Message);
            }
        }
    }
}
