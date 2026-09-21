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

        private CancellationTokenSource? _operationCts;
        private readonly List<CancellationTokenSource> _runningTaskCts = new();
        private bool _isExtracting;

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
        /// 解压成功之后的收尾：校验落盘结果 → 归集 → 视开关清理源包（M3）。
        ///
        /// 顺序不能换：
        /// 1. 先校验 —— 没有校验就没有"删除源包"的资格（AGENTS.md §9.5）；
        /// 2. 再归集 —— 归集是移动；先归集再校验会算不准（文件已经不在原输出目录）；
        /// 3. 最后清理源包 —— 它依赖前两步的结论。
        ///
        /// 任何一步失败都**不改变**"解压成功"这个结论，只是把结论写进日志与任务字段；
        /// 不要因为归集或清理失败就把任务标成失败 —— 用户的文件确实解出来了。
        /// </summary>
        /// <summary>
        /// 给递归层提供密码候选（只给值，不给来源说明）。
        /// 顺序与单层解压完全一致 —— 递归的内层包同样是"用户的包"，不该用另一套规则。
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
                .Select(p => p.Value ?? string.Empty)
                .ToList();
        }

        /// <summary>
        /// 递归解压一个任务，并把结果落到任务状态上。
        /// 多分支时**必须问用户**（不变量 8），问完只处理用户确认的那批候选。
        /// </summary>
        private async Task RunRecursiveAsync(ArchiveTask task, string outputPath, CancellationToken cancellationToken)
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

            AppendLog("INFO", $"{task.FileName}：开始递归解压（模式 {mode}，最大 {limits.MaxDepth} 层）。");

            RecursionResult result = await _recursiveExtractor.ExtractAsync(
                task, outputPath, mode, previousDecision: null, cancellationToken);

            if (result.StopReason == RecursionStopReason.NeedsDecision && result.Decision != null)
            {
                bool expandAll = _dialogService.ShowConfirm(
                    result.Decision.Prompt + Environment.NewLine + Environment.NewLine +
                    "选“确定”：把这些内层归档也解开。选“取消”：只保留当前这一层的结果。");

                if (expandAll)
                {
                    result = await _recursiveExtractor.ExtractAsync(
                        task, outputPath, mode, result.Decision, cancellationToken);
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
                        int moved = MoveDirectoryContent(layerZeroOutput, outputPath);

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

        private async Task PostProcessSuccessAsync(ArchiveTask task, string password, CancellationToken cancellationToken)
        {
            // 1) 校验：拿到引擎声明的条目数与总大小，和落盘结果对一遍。
            ArchiveListResult expected = await _archiveEngine.ListAsync(
                ArchiveRequest.For(task.CurrentPath, password),
                cancellationToken);

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
                        AppendLog("ERROR", $"{task.FileName}：产物落点异常 —— {landReason}");
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
                task.Status = StatusText.ExtractFailed;
                task.ErrorMessage =
                    $"解压产物超出资源预算：{producedFiles} 个文件 / {producedSize} 字节" +
                    $"（上限 {budgetLimits.MaxFileCount} 个 / {budgetLimits.MaxTotalSize} 字节）。已停止后续任务。";

                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");

                // 不发布、不清理：产物留在原地让用户自己判断，源文件更不能删。
                StopAfterCurrent();
                return;
            }

            task.IsOutputVerified = verification.Verified;
            task.VerifyMessage = verification.Message;

            AppendLog(verification.Verified ? "INFO" : "WARN", $"{task.FileName}：结果校验 —— {verification.Message}");

            // 2) 归集（可选）
            if (Settings.CollectResultsToDirectory)
            {
                if (!verification.Verified)
                {
                    AppendLog("WARN", $"{task.FileName}：校验未通过，已跳过结果归集。");
                }
                else
                {
                    CollectResult collected = new ResultCollector().Collect(task, Settings.CollectTargetDirectory);

                    AppendLog(collected.Success ? "INFO" : "WARN", $"{task.FileName}：结果归集 —— {collected.Message}");

                    if (collected.Success)
                    {
                        task.CollectedPath = collected.DestinationPath;
                    }
                }
            }

            // 3) 清理源包（默认关闭；未通过校验时服务内部会拒绝执行）
            SourceCleanupResult cleanup = new SourceCleanupService().Cleanup(
                task,
                verification,
                Settings.DeleteSourceAfterExtract);

            if (cleanup.Attempted)
            {
                AppendLog(cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN", $"{task.FileName}：清理源包 —— {cleanup.Message}");
            }
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

            string outputPath = _pathService.BuildOutputPath(task, extractOptions);

            try
            {
                if (!string.IsNullOrWhiteSpace(outputPath) &&
                    Directory.Exists(outputPath) &&
                    Directory.EnumerateFileSystemEntries(outputPath).Any())
                {
                    string newOutputPath = _pathService.AutoRenameDirectoryPath(outputPath);

                    AppendLog("WARN", $"输出目录已存在且非空，为避免混入旧文件，自动改用新目录：{newOutputPath}");

                    outputPath = newOutputPath;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"检查输出目录失败，将继续使用原输出目录：{ex.Message}");
            }

            task.OutputPath = outputPath;

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
                    ArchiveRequest.For(task.CurrentPath, candidate.Value),
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
                    archiveSize = new FileInfo(task.CurrentPath).Length;
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
                await RunRecursiveAsync(task, outputPath, cancellationToken);
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

                for (int i = 0; i < candidates.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    AppendLog("INFO", $"{task.FileName}：测试密码候选 {i + 1}/{candidates.Count}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult testResult = await _archiveEngine.TestAsync(
                        ArchiveRequest.For(task.CurrentPath, password),
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
                    if (lastResult != null && lastResult.DetectedErrorType == "WrongPassword")
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

                    if (task.Status == StatusText.WrongPassword)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _dialogService.ShowWarning($"文件密码错误或缺少正确密码：\n{task.FileName}");
                        });
                    }

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
         ArchivePath = task.CurrentPath,
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

                    _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);

                    await PostProcessSuccessAsync(task, selectedPassword, cancellationToken);



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

                for (int i = 0; i < candidates.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    selectedPassword = password;

                    task.Operation = StatusText.OpExtract;
                    task.Status = StatusText.Extracting;
                    task.ProgressText = StatusText.ProgressProcessing;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("INFO", $"{task.FileName}：开始解压，密码候选 {i + 1}/{candidates.Count}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     new ArchiveRequest
     {
         ArchivePath = task.CurrentPath,
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

                        _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);

                        await PostProcessSuccessAsync(task, selectedPassword, cancellationToken);



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
                    if (hasWrongPassword)
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

                    if (task.Status == StatusText.WrongPassword)
                    {
                        AppendLog("ERROR", $"解压失败：{task.FileName}，原因：密码错误或缺少正确密码");

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _dialogService.ShowWarning($"文件密码错误或缺少正确密码：\n{task.FileName}");
                        });
                    }
                }
            }

            task.EndTime = DateTime.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";

            task.Operation = StatusText.OpWaiting;
            task.ProgressText = StatusText.ProgressCompleted;
            task.LastUpdatedTime = DateTime.Now;
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
