using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 一键处理的结果。
    ///
    /// 为什么要一个对象而不是只返回那行汇总：测试要能直接断言"跑了几轮、停在哪"，
    /// 拿中文汇总去 <c>Contains</c> 只是把文案当接口，文案一改测试就假红/假绿。
    /// </summary>
    internal sealed class OneClickOutcome
    {
        /// <summary>实际跑了几轮（1 = 只有第一层，也就是最常见的情况）。</summary>
        public int Rounds { get; init; }

        /// <summary>自动续解了几层（正常跑完时 = 轮数 - 1）。</summary>
        public int ContinuationLayers { get; init; }

        /// <summary>是不是被「停止后续」打断的（被打断就不许显示成成功）。</summary>
        public bool Stopped { get; init; }

        /// <summary>是不是撞到轮数硬上限、还有更深的内层包没解。</summary>
        public bool HitRoundLimit { get; init; }

        /// <summary>一行汇总（已经写进日志，GUI 用它弹提示）。</summary>
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>
    /// 本轮找到的一个内层归档，以及**它属于哪个父任务**。
    ///
    /// 为什么要带父任务（本轮改造的核心之一）：内层包不再被当成"另一个独立的任务"，
    /// 而是父任务这条流水线的一部分 —— 它的产物最终必须归到**父任务那一个**最终目录里。
    /// 只传文件路径的话，内层包会按自己的路径算落点（<c>&lt;id&gt;.7z\内容物</c>），
    /// 源目录旁边就又多一个平级目录 —— 正是用户抱怨的那个现象。
    /// </summary>
    internal sealed class InnerArchiveCandidate
    {
        /// <summary>内层归档的起点（分卷组的第一卷，或者单文件归档）。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>父任务的最终目录（归集之后的落点）；内层任务的产物就落在这里。</summary>
        public string ParentOutputDirectory { get; init; } = string.Empty;

        /// <summary>父任务的名字（只用于日志与报告）。</summary>
        public string ParentTaskName { get; init; } = string.Empty;
    }

    /// <summary>
    /// 「一个包一次搞定」：把 识别 → 修正伪装后缀 → 按密码本试密码解压 → 一行汇总 串成一次操作
    /// （AGENTS.md §10 的 M2 验收）。
    ///
    /// 关于"一键"的准确含义：
    /// 不变量 3 要求**任何改名都必须先预览**，所以这里不会跳过确认。
    /// 一键 = "一次确认之后全自动"；如果这批任务里没有任何需要改名的（后缀本来就对、或者是分卷），
    /// 改名这一步会整个跳过，那就真的零确认。
    ///
    /// 关于自动续解（第 2 层起）：
    /// 真实的"双面文件"（<c>xxx.mp4</c> = 视频头 + 尾部一个完整 ZIP）解出来往往**不是最终数据**，
    /// 而是 <c>&lt;id&gt;.7z.001</c> + <c>.002</c> 这样的加密分卷，还要再解一层。
    /// 以前一键处理只做第一层就停手（<see cref="AppSettings.RecursionMode"/> 默认 SingleLayer），
    /// 用户看到的现象就是"只有第一层能解出来"。
    ///
    /// 这里**刻意不碰** <c>Extraction/RecursiveExtractor</c>：那条递归路径在用户的真实文件上会卡死
    /// （历史事故见 docs/需求变更.md），所以续解用的是**已经跑通的单层解压**重复跑 ——
    /// 一轮就是一次正常的"识别 → 改名 → 解压"，只是把上一轮产出的内层包当成本轮输入，
    /// 轮数卡死在 <see cref="MaxRounds"/> 轮（不变量 8）。RecursionMode 的默认值不动。
    ///
    /// 刻意不做的事：
    /// - 不自动删源包（那是 M3 的独立开关，默认关闭，且要校验通过才删）；
    /// - 不改用户没勾选的任务（沿用既有"只处理选中项"的约定）——
    ///   续解只处理**本轮产物里新出现**的归档，产物目录里本来就有的包一个都不碰。
    /// </summary>
    internal sealed class OneClickCoordinator
    {
        /// <summary>
        /// 轮数硬上限（不变量 8）：含第一层在内一共 3 轮。
        /// 真实资源包极少超过两层；再深通常意味着包里套了不该自动展开的东西，
        /// 继续往下解只会把时间和磁盘烧在一个可能失控的展开上 —— 到顶就停并如实报告。
        /// </summary>
        internal const int MaxRounds = 3;

        private readonly MainViewModel _vm;
        private readonly ScanCoordinator _scanCoordinator;
        private readonly RenameCoordinator _renameCoordinator;
        private readonly ExtractionCoordinator _extractionCoordinator;
        private readonly DialogService _dialogService;

        public OneClickCoordinator(
            MainViewModel vm,
            ScanCoordinator scanCoordinator,
            RenameCoordinator renameCoordinator,
            ExtractionCoordinator extractionCoordinator,
            DialogService dialogService)
        {
            _vm = vm;
            _scanCoordinator = scanCoordinator;
            _renameCoordinator = renameCoordinator;
            _extractionCoordinator = extractionCoordinator;
            _dialogService = dialogService;
        }

        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
        private AppSettings Settings => _vm.Settings;
        private bool IsBusy => _vm.IsBusy;

        /// <summary>
        /// 内层包的落点 = **父任务的最终目录**（归集之后就是归集目录，没归集就是输出目录）。
        ///
        /// 归集是"把产物目录整个搬走"，所以开了归集时父任务的 OutputPath 已经不存在了，
        /// 权威落点是 CollectedPath。内层包必须跟着搬过去的那一个目录走，否则第二层会落到
        /// 一个空目录里，用户看到的还是"东西散在两个地方"。
        /// </summary>
        internal static string ResolveContinuationOutputDirectory(ArchiveTask parentTask)
        {
            if (parentTask == null)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(parentTask.CollectedPath)
                ? parentTask.OutputPath
                : parentTask.CollectedPath;
        }

        /*
         * 忙碌标志必须**成对进出**（MainViewModel.EnterBusy/ExitBusy 是嵌套计数）。
         *
         * 旧写法是直接 `IsBusy = true` / `finally IsBusy = false`：一键处理跑到一半会去调
         * RenameCoordinator / ExtractionCoordinator，而它们各自的 finally 也会把标志置回 false ——
         * 于是外层还在跑的时候「停止后续 / 取消当前」按钮就变灰了（它们只看 IsBusy），
         * 守卫跟着失效，用户能在间隙里再点一次解压，同一个包被解两遍。
         * 计数之后只有最外层退出才真正变成"不忙"，见 MainViewModel.EnterBusy。
         */
        private void EnterBusy() => _vm.EnterBusy();
        private void ExitBusy() => _vm.ExitBusy();

        private void AppendLog(string level, string message) => _vm.AppendLog(level, message);
        private void UpdateSummary() => _vm.UpdateSummary();

        public async Task RunAsync()
        {
            if (IsBusy)
            {
                return;
            }

            /*
             * 只处理**勾选**的任务。
             * 以前是"选了就处理选中的、没选就处理全部"，看起来贴心，实际是灾难：
             * 用户以为只动自己挑的那几个，结果整列表都被解压；日志里的任务数还会前后对不上。
             * 明确一点更好：没勾就提示去勾。
             */
            List<ArchiveTask> targets = Tasks.Where(t => t.IsSelected).ToList();

            if (targets.Count == 0)
            {
                _dialogService.ShowInfo(Tasks.Count == 0
                    ? "任务列表是空的。先把文件或文件夹拖进来，或点“添加文件夹”。"
                    : $"请先勾选要处理的任务。{Environment.NewLine}{Environment.NewLine}" +
                      $"列表里有 {Tasks.Count} 个任务，当前一个都没勾。在列表上按 Ctrl+A 可以全选。");
                return;
            }

            EnterBusy();

            try
            {
                OneClickOutcome outcome = await RunPipelineAsync(targets);

                _dialogService.ShowInfo(outcome.Summary);
            }
            catch (OperationCanceledException)
            {
                AppendLog("WARN", "一键处理被取消。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "一键处理失败：" + ex.Message);
                _dialogService.ShowException(ex, "一键处理失败");
            }
            finally
            {
                ExitBusy();
            }
        }

        /// <summary>
        /// 真正干活的部分：**不带任何对话框**，返回结果对象。
        ///
        /// 为什么要单独一个入口：GUI 的 MessageBox 会真的弹出来（自动测试里没人点，就挂在那里了），
        /// 把"跑流程"和"弹窗"分开，流程本身才测得动。
        /// </summary>
        internal async Task<OneClickOutcome> RunPipelineAsync(IReadOnlyList<ArchiveTask> firstRoundTargets)
        {
            AppendLog("INFO", $"一键处理开始，共 {firstRoundTargets.Count} 个任务。");

            // 处理过的任务按轮累加：汇总要算"本次一共处理了多少个"，只算第一轮会和实际不符。
            var processed = new List<ArchiveTask>(firstRoundTargets);

            // 源文件（含分卷各卷）不算内层包：不能把用户最初给的那个 .mp4 / .7z.001 又加一遍。
            HashSet<string> sourcePaths = BuildSourcePathSet(firstRoundTargets);

            List<ArchiveTask> roundTargets = firstRoundTargets.ToList();

            int round = 0;
            int continuationLayers = 0;
            bool stopped = false;
            bool hitRoundLimit = false;

            /*
             * 「用户按过停止后续」这件事必须单独记一笔。
             *
             * 只看 _vm.IsStopping 不行：ExtractionCoordinator 在批次开始和收尾时都会把它复位成 false，
             * 等我们拿回控制权时它已经是 false 了。只看"有没有任务没被处理"也不完全可靠：
             * 并发位被占着时批量循环还会再启动一个任务，未必留下未处理的任务。
             * 订阅属性变更拿到的是"用户确实按过"这个事实，不依赖上面两条时序。
             */
            bool stopRequested = false;

            void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(MainViewModel.IsStopping) && _vm.IsStopping)
                {
                    stopRequested = true;
                }
            }

            _vm.PropertyChanged += OnViewModelPropertyChanged;

            try
            {
                while (round < MaxRounds)
                {
                    round++;

                    // 第一步：识别（第 2 轮起只扫本轮新加进来的包）。
                    await ScanRoundAsync(round, roundTargets);

                    // 第二步：修正伪装后缀。只处理确实需要改的，且必须经过预览确认（不变量 3）。
                    int needRename = Tasks.Count(t => t.IsSelected && NeedsRename(t));

                    if (needRename > 0)
                    {
                        AppendLog("INFO", $"一键处理：{needRename} 个任务需要修正后缀，先出预览。");
                        await _renameCoordinator.SmartRenameAsync();
                    }
                    else if (round == 1)
                    {
                        AppendLog("INFO", "一键处理：没有需要修正的后缀，跳过改名。");
                    }

                    /*
                     * 解压**之前**先记下候选目录里已有的文件。
                     *
                     * 续解只认"这一轮新出现的"归档起点，不能把目录里本来就有的压缩包当成产物：
                     * 输出目录可以就是源目录（设置里的"解压到原目录 + 不建包名文件夹"），
                     * 那样一来用户自己放在旁边的包会被当成内层包重新解一遍 —— 等于偷偷处理了没勾选的文件。
                     *
                     * 目录枚举（可能几万个文件）在后台线程上做：一键处理跑在 UI 线程上，
                     * 收尾阶段任何一次全目录遍历都会让窗口卡住（本项目最容易出卡死的一块）。
                     */
                    (HashSet<string> existingFiles, HashSet<string> unreadableDirectories) =
                        await SnapshotCandidateDirectoriesAsync(roundTargets);

                    // 第三步：解压（密码本、旁路说明文件、分卷守卫都在解压流程里生效）。
                    // 走**整理路径**的入口：定稿 + 校验通过 + 未取消之后按设置处理源包
                    // （默认移入其余物，决策 D-9/D-11/D-12）。手动「只解压」按钮走的
                    // StartExtractAsync 是地基路径，永远不动源包 —— 两者只在这一件事上不同。
                    AppendLog("INFO", round == 1 ? "一键处理：开始解压。" : $"一键处理：第 {round} 层开始解压。");
                    await _extractionCoordinator.StartExtractForOneClickAsync();

                    UpdateSummary();

                    // 「停止后续」之后不许再续解：按下过停止，或者这一轮有任务根本没轮到。
                    if (stopRequested || roundTargets.Any(t => !IsHandled(t)))
                    {
                        stopped = true;
                        break;
                    }

                    // 从本轮成功任务的产物里找内层包。找不到就结束，但**不许静默**（见下面的日志）。
                    List<InnerArchiveCandidate> innerArchives =
                        await CollectInnerLayersAsync(roundTargets, existingFiles, unreadableDirectories, sourcePaths);

                    if (innerArchives.Count == 0)
                    {
                        /*
                         * 这一句是必须的，不是噪音。
                         *
                         * 以前这里一声不吭地 break：用户完全分不清"本来就没有内层包"和"续解功能坏了、
                         * 或者内层包落在了没被看到的目录里"。真实踩坑就是后者 —— 输出目录已存在时
                         * 解压实际落到 xxx(1)，而续解只按解压**前**的目录快照找包，
                         * 结果第二层根本没跑，汇总却照样打印"一键处理完成：成功 N"。
                         * 有这一行日志，用户（和我们）才有一条能对照的线索。
                         */
                        AppendLog("INFO", $"一键处理：第 {round} 层没有发现可继续解压的内层包，到此结束。");
                        break;
                    }

                    if (round >= MaxRounds)
                    {
                        hitRoundLimit = true;
                        AppendLog("WARN", $"一键处理：第 {round + 1} 层还有 {innerArchives.Count} 个内层包，但已达到 {MaxRounds} 轮上限，停止续解。");
                        break;
                    }

                    List<ArchiveTask> nextRound;

                    try
                    {
                        /*
                         * 已经处理过的任务全部取消勾选。
                         * 不这样做，下一轮会把它们**重新解压一遍**，产出 "(1)" 这样的垃圾副本 ——
                         * 解压流程只认勾选状态，这正是它该有的样子（不偷偷处理没勾的）。
                         */
                        foreach (ArchiveTask task in processed)
                        {
                            task.IsSelected = false;
                        }

                        nextRound = await AddInnerTasksAsync(innerArchives);
                    }
                    catch (Exception ex)
                    {
                        AppendLog("ERROR", $"一键处理：把内层包加进任务列表失败，停止续解 —— {ex.Message}");
                        break;
                    }

                    if (nextRound.Count == 0)
                    {
                        // 内层包全都已经在任务列表里了（路径重复）：再跑一轮只会空转。
                        AppendLog("WARN", $"一键处理：第 {round + 1} 层的 {innerArchives.Count} 个内层包没能加进任务列表，停止续解。");
                        break;
                    }

                    continuationLayers++;

                    /*
                     * 说清"第二层解到哪去"：内层包**不另建目录**，
                     * 它的产物与父任务归到同一个最终目录（用户诉求：一个源包 = 一个目录）。
                     */
                    string continuationTarget = innerArchives
                        .Select(candidate => candidate.ParentOutputDirectory)
                        .FirstOrDefault(directory => !string.IsNullOrWhiteSpace(directory)) ?? string.Empty;

                    AppendLog(
                        "INFO",
                        $"一键处理：第 {round + 1} 层发现 {innerArchives.Count} 个内层包，继续解" +
                        (string.IsNullOrWhiteSpace(continuationTarget)
                            ? "。"
                            : $"（产物归入同一个输出目录：{continuationTarget}，不再另建文件夹）。"));

                    processed.AddRange(nextRound);
                    sourcePaths.UnionWith(BuildSourcePathSet(nextRound));
                    roundTargets = nextRound;
                }
            }
            finally
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            string summary = BuildSummaryLine(processed, stopped, continuationLayers, hitRoundLimit);

            AppendLog("INFO", summary);

            return new OneClickOutcome
            {
                Rounds = round,
                ContinuationLayers = continuationLayers,
                Stopped = stopped,
                HitRoundLimit = hitRoundLimit,
                Summary = summary
            };
        }

        /// <summary>
        /// 一轮的"识别"步骤。
        /// 第 1 轮沿用原来的整体扫描；第 2 轮起**只扫本轮新加进来的包**。
        /// </summary>
        private async Task ScanRoundAsync(int round, IReadOnlyList<ArchiveTask> roundTargets)
        {
            List<ArchiveTask> pending = roundTargets.Where(NeedsScan).ToList();

            if (pending.Count == 0)
            {
                return;
            }

            if (round == 1)
            {
                AppendLog("INFO", "一键处理：先识别格式。");
                await _scanCoordinator.ScanTasksAsync();
                return;
            }

            /*
             * 为什么不直接再调一次 ScanTasksAsync：
             * 它会遍历**整个任务列表**，把已经解压完的任务状态从"解压成功"冲回"已识别" ——
             * 用户会看到第 1 层的结果在列表里突然没了，汇总里的成功数也跟着归零。
             * 新加进来的包只需要识别它自己，按任务扫没有这个副作用。
             */
            foreach (ArchiveTask task in pending)
            {
                await _scanCoordinator.RescanTaskAsync(task);
            }
        }

        /// <summary>
        /// 把内层包加进任务列表，返回新加进来的任务（已显式勾选），并给每个内层任务挂上
        /// **父任务的最终目录**（<see cref="ArchiveTask.ParentOutputDirectory"/>）。
        ///
        /// 挂父目录这一步是"一个源包 = 一个最终目录"的接线点：
        /// <see cref="PathService.BuildOutputPath"/> 见到这个字段就直接返回它，
        /// 于是内层包不会再算出 <c>&lt;id&gt;.7z\内容物</c> 这种自己的目录。
        ///
        /// 为什么临时关掉 <see cref="AppSettings.AutoScanAfterDrop"/>：
        /// AddPathsAsync 在它开启时会顺手对整个列表做一次重新识别，同样会把已经解压完的任务状态冲回
        /// "已识别"（第 1 层的结果在界面上就没了）。这里只要"把新文件变成任务"，
        /// 识别由本轮自己按任务做（见 <see cref="ScanRoundAsync"/>），所以临时关掉、用完立刻还原。
        /// </summary>
        private async Task<List<ArchiveTask>> AddInnerTasksAsync(IReadOnlyList<InnerArchiveCandidate> candidates)
        {
            int before = Tasks.Count;
            bool autoScanAfterDrop = Settings.AutoScanAfterDrop;

            Settings.AutoScanAfterDrop = false;

            try
            {
                await _scanCoordinator.AddPathsAsync(candidates.Select(c => c.Path).ToList());
            }
            finally
            {
                Settings.AutoScanAfterDrop = autoScanAfterDrop;
            }

            // 按完整路径找回"这个任务对应哪个内层候选"：AddPathsAsync 可能改写路径大小写，
            // 也可能因为重复而少加几个，所以用查表而不是按下标一一对应。
            var byPath = new Dictionary<string, InnerArchiveCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach (InnerArchiveCandidate candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate.Path))
                {
                    byPath[NormalizePath(candidate.Path)] = candidate;
                }
            }

            var added = new List<ArchiveTask>();

            for (int i = before; i < Tasks.Count; i++)
            {
                /*
                 * 新任务必须处于勾选状态才会被下一轮处理。
                 * FileScanService 建任务时就是 IsSelected = true（也确认过 ArchiveTask 的字段默认值），
                 * 但这条是"续解能不能继续"的命门，显式写一遍，免得以后有人改默认值时静默失效。
                 */
                Tasks[i].IsSelected = true;

                if (byPath.TryGetValue(NormalizePath(Tasks[i].CurrentPath), out InnerArchiveCandidate? candidate))
                {
                    Tasks[i].ParentOutputDirectory = candidate.ParentOutputDirectory;
                    Tasks[i].ParentTaskName = candidate.ParentTaskName;

                    /*
                     * 落点当场写一遍，别等界面刷新：续解下一轮要读 task.OutputPath 找内层包，
                     * 中间任何一次时序错位都会让第二层静默不跑（历史事故）。
                     */
                    if (!string.IsNullOrWhiteSpace(candidate.ParentOutputDirectory))
                    {
                        Tasks[i].OutputPath = candidate.ParentOutputDirectory;
                    }
                }

                added.Add(Tasks[i]);
            }

            return added;
        }

        /// <summary>路径比较用的规范化形式（全路径 + 统一分隔符；大小写在 Windows 上不敏感）。</summary>
        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// 只对"还没识别"的任务重扫：
        /// 一律重扫会把已经识别好的、甚至已经解压完的任务重新洗一遍，白费时间还冲掉结果。
        /// </summary>
        private static bool NeedsScan(ArchiveTask task)
        {
            return task.ExtensionStatus == StatusText.NotChecked ||
                string.IsNullOrWhiteSpace(task.ExtensionStatus) ||
                string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                task.Status == StatusText.WaitingScan;
        }

        /// <summary>
        /// 需要改名的三种后缀状态：缺失、不匹配、多重伪装。
        /// "后缀正常"不用动，"分卷后缀"**绝对不能动**（改了会切断分卷链），"格式未知"交给设置里的策略。
        /// </summary>
        private static bool NeedsRename(ArchiveTask task)
        {
            /*
             * 内嵌归档（文件尾部藏着 ZIP 的双面文件）**不改名**。
             *
             * 它看起来最像"该改名"的那一类（`xxx.mp4` 里明明有 ZIP），但改名是纯粹的误导：
             * ZIP 的内部偏移相对它自己，而前置数据远超 7-Zip 的容忍上限（实测 8 MiB），
             * 所以 `xxx.zip` 交到 7z 手里仍然打不开。
             * 该做的事是解压管线里按偏移把尾部那段取出来，不是动后缀。
             */
            if (task.ExtensionStatus == StatusText.ExtensionEmbedded)
            {
                return false;
            }

            return task.ExtensionStatus == StatusText.ExtensionMissing ||
                task.ExtensionStatus == StatusText.ExtensionMismatch ||
                task.ExtensionStatus == StatusText.ExtensionMultiFake;
        }

        /// <summary>源文件路径集合（当前路径 + 最初路径 + 该分卷组的每一卷）。</summary>
        private static HashSet<string> BuildSourcePathSet(IEnumerable<ArchiveTask> tasks)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in tasks)
            {
                if (task == null)
                {
                    continue;
                }

                foreach (string path in new[] { task.CurrentPath, task.OriginalPath }.Concat(task.VolumePaths))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            return paths;
        }

        /// <summary>
        /// 记录候选产物目录在**解压前**已有的文件，返回（已有文件集合, 解压前读不到内容的目录集合）。
        ///
        /// 返回"读不到内容的目录"的用途：这种目录里的老包和新产物混在一起、分不出新旧，
        /// 续解时整个不看它 —— 宁可漏掉一层，也不能把用户本来就放在那里的包当成新产物去解压。
        ///
        /// ⚠ 这里**刻意不再**返回"快照成功的目录集合"。
        /// 旧实现要求"当前目录必须在快照集合里"，于是漏掉了最要命的一种情况：
        /// 输出目录已存在且非空时，解压实际落到自动改名的 <c>xxx(1)</c>，而快照是解压**之前**做的，
        /// 里面只有原定的 <c>xxx</c> —— 内层包所在的 <c>xxx(1)</c> 被整段跳过，
        /// 第二层静默不跑，汇总仍然打印"一键处理完成：成功 N"。
        /// 换成"黑名单"之后，<c>xxx(1)</c> 这种本轮新建的目录天然被放行，
        /// 新旧之分仍然由 <c>existingFiles</c> 把关。
        ///
        /// 线程规则：目录集合与设置读取在调用线程（UI）上做，**枚举（可能几万个文件）放后台**。
        /// 一键处理整条流程跑在 UI 线程上，任何一次全目录遍历都会让窗口卡住。
        /// </summary>
        private async Task<(HashSet<string> ExistingFiles, HashSet<string> UnreadableDirectories)> SnapshotCandidateDirectoriesAsync(
            IReadOnlyList<ArchiveTask> roundTargets)
        {
            // 没有输出目录就没法做"解压前后对比"。补算一次（与界面刷新输出目录用的是同一套规则）。
            if (roundTargets.Any(t => string.IsNullOrWhiteSpace(t.OutputPath)))
            {
                _vm.RefreshOutputPaths();
            }

            var directories = new List<string>();

            foreach (ArchiveTask task in roundTargets)
            {
                directories.AddRange(CandidateDirectories(task));
            }

            // 归集目标目录在解压前就能确定：不先记下来，它里面的老文件会被当成这一轮的新产物。
            if (Settings.CollectResultsToDirectory && !string.IsNullOrWhiteSpace(Settings.CollectTargetDirectory))
            {
                directories.Add(Settings.CollectTargetDirectory);
            }

            List<string> targets = directories
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            (HashSet<string> existingFiles, List<(string Directory, string Message)> unreadable) = await Task.Run(() =>
            {
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var failures = new List<(string, string)>();

                foreach (string directory in targets)
                {
                    if (!Directory.Exists(directory))
                    {
                        // 目录还不存在：解压之后出现在里面的都算新产物，不需要记任何东西。
                        continue;
                    }

                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                        {
                            files.Add(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add((directory, ex.Message));
                    }
                }

                return (files, failures);
            });

            var unreadableDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 日志回到 UI 线程再写（后台线程不许碰界面集合）。
            foreach ((string directory, string message) in unreadable)
            {
                unreadableDirectories.Add(directory);
                AppendLog("WARN", $"一键处理：读不了产物目录 {directory}（{message}），本轮不看这个目录。");
            }

            return (existingFiles, unreadableDirectories);
        }

        /// <summary>
        /// 从本轮**成功任务**的产物目录里找内层包，只取"归档的起点"，并带上**它属于哪个父任务**。
        ///
        /// 只取起点：把 <c>.002</c> / <c>.z01</c> / <c>.r00</c> / <c>part2</c> 这些后续段也加进任务列表，
        /// 只会造出一批假任务（它们自己不是完整归档，也不是组的开头）。
        ///
        /// 为什么每个候选都要带父任务：内层包不是独立任务，它的产物要归到**父任务那一个**最终目录里
        /// （用户诉求：一个源包 = 一个最终目录）。父目录由
        /// <see cref="ResolveContinuationOutputDirectory"/> 给出（归集开了就是归集目录）。
        ///
        /// 线程规则：目录枚举在后台（见 <see cref="SnapshotCandidateDirectoriesAsync"/> 的说明），
        /// 过滤是纯内存哈希查表，留在调用线程上做。
        /// </summary>
        private async Task<List<InnerArchiveCandidate>> CollectInnerLayersAsync(
            IReadOnlyList<ArchiveTask> roundTargets,
            HashSet<string> existingFiles,
            HashSet<string> unreadableDirectories,
            HashSet<string> sourcePaths)
        {
            // 已经在任务列表里的文件不重复加（去重）。
            var knownTaskPaths = new HashSet<string>(
                Tasks.Select(t => t.CurrentPath).Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);

            /*
             * task.OutputPath / task.CollectedPath 是解压管线回写的**实际落点**（唯一权威来源），
             * 不是"打算输出到哪"。所以这里无条件按它去找内层包：
             * 目录已经存在时它可能是自动改名后的 xxx(1)，绝不是快照里那个原定目录。
             * 是不是"这一轮新产物"由下面的 existingFiles 继续把关 ——
             * 解压前就有的文件会被过滤掉，不会把用户自己放在旁边的包重新解一遍。
             */
            var parents = new List<(ArchiveTask Task, string OutputDirectory)>();

            foreach (ArchiveTask task in roundTargets)
            {
                // 只有解压成功的任务才有"产物"可言；失败/跳过/取消的目录里没有可信的东西。
                if (!IsSuccessStatus(task))
                {
                    continue;
                }

                foreach (string directory in CandidateDirectories(task))
                {
                    if (!string.IsNullOrWhiteSpace(directory) && !unreadableDirectories.Contains(directory))
                    {
                        parents.Add((task, directory));
                    }
                }
            }

            if (parents.Count == 0)
            {
                return new List<InnerArchiveCandidate>();
            }

            List<(string OutputDirectory, List<string> Files)> scanned = await Task.Run(() =>
            {
                var results = new List<(string, List<string>)>();

                foreach ((ArchiveTask _, string outputDirectory) in parents)
                {
                    if (!Directory.Exists(outputDirectory))
                    {
                        continue;
                    }

                    try
                    {
                        results.Add((
                            outputDirectory,
                            Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).ToList()));
                    }
                    catch
                    {
                        // 读不了这个目录：跳过（快照阶段已经写过一条 WARN，不重复打扰用户）。
                    }
                }

                return results;
            });

            var found = new Dictionary<string, InnerArchiveCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach ((ArchiveTask task, string outputDirectory) in parents)
            {
                List<string>? files = scanned
                    .Where(entry => string.Equals(entry.OutputDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Files)
                    .FirstOrDefault();

                if (files == null)
                {
                    continue;
                }

                string parentName = string.IsNullOrWhiteSpace(task.FileName)
                    ? Path.GetFileName(task.CurrentPath)
                    : task.FileName;

                string continuationOutput = ResolveContinuationOutputDirectory(task);

                foreach (string file in files)
                {
                    // 只认"这一轮新出现"的：解压前就在那里的文件不是这一轮的产物。
                    if (existingFiles.Contains(file))
                    {
                        continue;
                    }

                    // 源文件本身（含分卷各卷）不算内层包。
                    if (sourcePaths.Contains(file))
                    {
                        continue;
                    }

                    if (knownTaskPaths.Contains(file))
                    {
                        continue;
                    }

                    if (!IsArchiveStartPoint(file))
                    {
                        continue;
                    }

                    if (!found.ContainsKey(file))
                    {
                        found[file] = new InnerArchiveCandidate
                        {
                            Path = file,
                            ParentOutputDirectory = continuationOutput,
                            ParentTaskName = parentName
                        };
                    }
                }
            }

            return found.Values.OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 一个任务的产物可能落在哪几个目录。
        ///
        /// 除了输出目录，还要看归集目录：开了"结果归集"时产物会被**移动**到归集目标目录，
        /// 内层包也跟着跑了 —— 只看 OutputPath 的话这种配置下永远找不到内层包（看起来就像功能没生效）。
        /// </summary>
        private static IEnumerable<string> CandidateDirectories(ArchiveTask task)
        {
            if (!string.IsNullOrWhiteSpace(task.OutputPath))
            {
                yield return task.OutputPath;
            }

            if (!string.IsNullOrWhiteSpace(task.CollectedPath))
            {
                yield return task.CollectedPath;
            }
        }

        /// <summary>
        /// 判断一个文件是不是"归档的起点"（分卷组的第一卷，或者单文件归档）。
        ///
        /// 排除项：<c>.002</c> 及更大编号、<c>.z01/.z02…</c>、<c>.r00/.r01…</c>、
        /// <c>.part2</c> 及更大的分卷段 —— 它们既不是完整归档，也不是组的开头。
        /// </summary>
        internal static bool IsArchiveStartPoint(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string fileName = Path.GetFileName(filePath);
            string extension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            if (ExtensionHelper.IsVolumePartExtension(extension))
            {
                /*
                 * 三位数字分卷里只有 .001 是起点。
                 * .z01 / .r00 / .part1 都不是：zip 分卷的起点叫 xxx.zip，rar 老式分卷的起点叫 xxx.rar，
                 * .part1 那种命名的起点在 xxx.part1.rar 上（由下面那段处理）。
                 */
                return extension.Equals(".001", StringComparison.OrdinalIgnoreCase);
            }

            // xxx.part2.rar 的最后后缀是 .rar，光看后缀分不出来，编号在倒数第二个后缀上。
            if (GetPartSegmentNumber(fileName) > 1)
            {
                return false;
            }

            return ExtensionHelper.IsKnownArchiveExtension(extension);
        }

        /// <summary>取 <c>xxx.part01.rar</c> 里的 1；不是 part 命名返回 0。</summary>
        private static int GetPartSegmentNumber(string fileName)
        {
            string partSegment = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));

            if (string.IsNullOrWhiteSpace(partSegment))
            {
                return 0;
            }

            string digits = partSegment.TrimStart('.');

            if (digits.Length < 5 ||
                !digits.StartsWith("part", StringComparison.OrdinalIgnoreCase) ||
                !digits.Skip(4).All(char.IsDigit))
            {
                return 0;
            }

            return int.TryParse(digits.Substring(4), out int number) ? number : 0;
        }

        /// <summary>
        /// 一行汇总。
        ///
        /// 硬要求：各分项加起来**必须等于本次处理的任务数**。
        /// 以前这里统计整个列表，还把"扫描过但从没被处理"的任务算成失败 ——
        /// 于是出现过 `成功 0 / 失败 1 / 跳过 2（共 4 个任务）`：0+1+2=3≠4，
        /// 用户根本没法判断到底发生了什么。汇总如果自己都对不上，还不如不显示。
        ///
        /// 续解加进来的任务同样计入"本次处理的任务数"（它们确实被处理了），口径写在 scope 里。
        /// </summary>
        private string BuildSummaryLine(
            IReadOnlyList<ArchiveTask> targets,
            bool stopped = false,
            int continuationLayers = 0,
            bool hitRoundLimit = false)
        {
            int success = targets.Count(IsSuccessStatus);
            int partial = targets.Count(t => t.Status == StatusText.PartiallyCompleted);
            int cancelled = targets.Count(t => t.Status == StatusText.Cancelled);
            int skipped = targets.Count(t => t.Status == StatusText.Skipped);
            int failed = targets.Count(IsFailureStatus);

            // 剩下的就是"既没成功也没失败、也没跳过"的：没轮到它（例如格式未知却没被处理）。
            int untouched = targets.Count - success - partial - cancelled - skipped - failed;

            var parts = new List<string> { $"成功 {success}", $"失败 {failed}", $"跳过 {skipped}" };

            if (partial > 0)
            {
                parts.Add($"部分完成 {partial}");
            }

            if (cancelled > 0)
            {
                parts.Add($"取消 {cancelled}");
            }

            if (untouched > 0)
            {
                parts.Add($"未处理 {untouched}");
            }

            string scope = targets.Count == Tasks.Count
                ? $"本次 {targets.Count} 个任务"
                : $"本次 {targets.Count} 个 / 列表共 {Tasks.Count} 个";

            /*
             * 被「停止后续」打断时不许写成"完成"：失败/取消/部分完成不得显示成成功（不变量 6）。
             * 分项数字照旧自洽，只是把话说准。
             */
            string line = $"{(stopped ? "一键处理已停止" : "一键处理完成")}：{string.Join(" / ", parts)}（{scope}）。";

            int renameSuccess = targets.Count(t => t.Status == StatusText.RenameSuccess);

            if (renameSuccess > 0)
            {
                line += $" 已修正后缀 {renameSuccess} 个。";
            }

            // 跳过必须说清为什么，否则"跳过 2"等于没说
            int notArchive = targets.Count(t => t.Status == StatusText.Skipped && !t.IsArchive);

            if (notArchive > 0)
            {
                line += $" 跳过的 {notArchive} 个已由 7-Zip 确认不是压缩包。";
            }

            int passwordError = targets.Count(t => t.Status == StatusText.WrongPassword);

            if (passwordError > 0)
            {
                line += $" 密码错误 {passwordError} 个：检查密码本里是否包含这些包的密码。";
            }

            int corrupted = targets.Count(t => t.Status == StatusText.Corrupted);

            if (corrupted > 0)
            {
                line += $" 文件损坏 {corrupted} 个：这类只能重新下载。";
            }

            if (stopped)
            {
                line += continuationLayers > 0
                    ? $" 已按「停止后续」中断（已完成续解 {continuationLayers} 层）。"
                    : " 已按「停止后续」中断，没有继续解内层包。";
            }
            else if (continuationLayers > 0)
            {
                // 续解出来的内层包**不另建目录**：产物全部归到源包那一个输出目录里
                // （用户诉求："一个源包 = 一个最终目录"）。这句话就是给用户对账用的。
                line += $" 自动续解 {continuationLayers} 层（产物归入同一个输出目录，不再另建文件夹）。";
            }

            if (hitRoundLimit)
            {
                line += $" 还有更深的内层包，但已达到 {MaxRounds} 轮上限，没有继续。";
            }

            return line;
        }

        /// <summary>解压成功（含"已覆盖"）。</summary>
        private static bool IsSuccessStatus(ArchiveTask task)
        {
            return task.Status == StatusText.ExtractSuccess ||
                   task.Status == StatusText.Overwritten;
        }

        /// <summary>
        /// 这个任务"这一轮处理过了"吗 —— 有终态的都算处理过。
        /// 没有终态的（已识别 / 等待解压 / 解压中…）说明它**根本没轮到**，那就是被「停止后续」截断的信号。
        ///
        /// internal 是为了让测试直接钉住"新增状态有没有被认账"（同类先例：<see cref="IsArchiveStartPoint"/>）：
        /// 漏一个状态不会编译失败，只会让汇总把"跑完了"说成"已停止 / 未处理 N"。
        /// </summary>
        internal static bool IsHandled(ArchiveTask task)
        {
            return IsSuccessStatus(task) ||
                   task.Status == StatusText.PartiallyCompleted ||
                   task.Status == StatusText.Cancelled ||
                   task.Status == StatusText.Skipped ||
                   IsFailureStatus(task);
        }

        /// <summary>
        /// 真正"处理过并且失败了"的状态。
        ///
        /// 「达到密码尝试上限」也算（不变量 6 的反面同样成立：跑完了不许说成"被停止"）：
        /// 候选试到上限就停了，这是一次**正常的失败终态**。漏掉它会同时坏两件事：
        /// ① 汇总的"失败"里少算一个，分项之和与任务数对不上；
        /// ② <see cref="IsHandled"/> 跟着返回 false → 被当成"没轮到" → 汇总打印
        ///    "一键处理已停止 / 未处理 N"，把正常结束误报成被用户停止。
        /// 实测量到的错话（负向对照）：
        /// "一键处理已停止：成功 0 / 失败 0 / 跳过 0 / 未处理 1（本次 1 个任务）。已按「停止后续」中断…"
        /// </summary>
        internal static bool IsFailureStatus(ArchiveTask task)
        {
            return task.Status is
                StatusText.ExtractFailed or
                StatusText.WrongPassword or
                StatusText.Corrupted or
                StatusText.AccessDenied or
                StatusText.OutputConflict or
                StatusText.VolumeMissing or
                StatusText.PathTooLong or
                StatusText.SevenZipMissing or
                StatusText.UnknownError or
                StatusText.RenameFailed or
                StatusText.TestFailed or
                StatusText.PasswordAttemptLimitReached;
        }
    }
}
