using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 递归展开策略（设计.md §十–§十四、AGENTS.md §6 第 8 条：多分支默认不展开，必须问）。
    /// </summary>
    public enum RecursionMode
    {
        /// <summary>只解当前这一层，不看里面还有什么。</summary>
        SingleLayer,

        /// <summary>
        /// 默认：只有一个"主要内层归档"（同层其它文件都是说明类文件）时才自动继续。
        /// 出现多个内层归档就停下来问用户，绝不替他决定。
        /// </summary>
        SingleChain,

        /// <summary>展开所有内层归档（必须由用户显式选择），仍受全部上限约束。</summary>
        AllBranches
    }

    /// <summary>
    /// 递归的硬上限（AGENTS.md §6 第 8 条：层数 / 文件数 / 总大小 / 展开比 / 密码尝试次数）。
    ///
    /// 这些值是"安全阀"而不是"性能参数"：命中任何一个都必须**停下并报告**，
    /// 不允许"再试一层看看"。压缩炸弹靠的就是把其中某一条顶穿。
    /// </summary>
    public sealed class RecursionLimits
    {
        /// <summary>最多解几层（含第 0 层）。3 层足够覆盖 外层.zip → 内层.7z → data.tar 这种真实包。</summary>
        public int MaxDepth { get; init; } = 3;

        /// <summary>所有层累计的输出文件数上限。</summary>
        public int MaxTotalFiles { get; init; } = 100_000;

        /// <summary>所有层累计的输出总字节数上限（默认 50 GiB）。</summary>
        public long MaxTotalSize { get; init; } = 50L * 1024 * 1024 * 1024;

        /// <summary>
        /// 单层展开比上限：该层解压后总大小 / 该层归档文件大小。
        /// 500 倍对正常资源包足够宽松（视频、图片再压也到不了），超了就是"疑似压缩炸弹"。
        /// </summary>
        public double MaxExpansionRatio { get; init; } = 500d;

        /// <summary>单层里最多允许出现多少个内层归档；超了说明这不是"套娃"而是"归档集合"，应当让用户单独处理。</summary>
        public int MaxInnerArchivesPerLayer { get; init; } = 50;

        /// <summary>
        /// 每层最多试几个密码候选。
        ///
        /// 为什么和"密码错误"必须分开（AGENTS.md §9.2）：
        /// 密码错误 = "试遍了所有候选都不对"，用户该去补密码本；
        /// 达到上限 = "候选还有，只是我们不再试了"，用户该做的是缩小候选或调高上限。
        /// 两种情况给用户的下一步动作完全不同，混成一个会让人白折腾。
        /// </summary>
        public int MaxPasswordAttemptsPerLayer { get; init; } = 8;

        /// <summary>默认上限。写成静态单例是为了让"默认值"只有一处，便于核对与调整。</summary>
        public static RecursionLimits Default { get; } = new();
    }

    /// <summary>
    /// 一层的执行报告。"失败定位到层"（AGENTS.md §6 第 14 条）靠的就是它。
    /// 注意：本类**不得**出现明文密码，只有 <see cref="UsedPasswordMasked"/> 这个脱敏占位符。
    /// </summary>
    public sealed class RecursionLayerReport
    {
        public int Depth { get; init; }

        /// <summary>本层解的那个归档。</summary>
        public string ArchivePath { get; init; } = string.Empty;

        /// <summary>本层的产物目录（工作区里的 layer-XXX\output）。</summary>
        public string OutputPath { get; init; } = string.Empty;

        public bool Success { get; init; }

        /// <summary>引擎给的状态文案（如"解压成功"）。</summary>
        public string Status { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        /// <summary>本层产物里探测到的内层归档（完整路径）。</summary>
        public IReadOnlyList<string> InnerArchives { get; init; } = Array.Empty<string>();

        public int OutputFileCount { get; init; }

        public long OutputSize { get; init; }

        /// <summary>只用 "空密码" 或 "******"；本层没试过密码时为空串。</summary>
        public string UsedPasswordMasked { get; init; } = string.Empty;
    }

    /// <summary>
    /// 需要用户拍板的多分支询问。字段是给 GUI 直接用的：<see cref="Prompt"/> 就是弹窗正文。
    /// </summary>
    public sealed class RecursionDecisionRequest
    {
        public string CurrentArchivePath { get; init; } = string.Empty;

        public int Depth { get; init; }

        public IReadOnlyList<string> CandidateArchives { get; init; } = Array.Empty<string>();

        /// <summary>中文，直接弹给用户。</summary>
        public string Prompt { get; init; } = string.Empty;
    }

    /// <summary>为什么会停。<b>不要</b>把它压成"成功/失败"两态：这里的每一项都对应不同的用户动作。</summary>
    public enum RecursionStopReason
    {
        None,

        /// <summary>一路解到底（或按用户选择的模式正常收尾）。</summary>
        Completed,

        /// <summary>出现多个内层归档，等用户拍板。</summary>
        NeedsDecision,

        MaxDepthReached,

        MaxTotalFilesReached,

        MaxTotalSizeReached,

        /// <summary>单层展开比超限，疑似压缩炸弹。</summary>
        ExpansionRatioExceeded,

        TooManyInnerArchives,

        /// <summary>候选还有，但已经试满 MaxPasswordAttemptsPerLayer。**不是**密码错误。</summary>
        PasswordAttemptsExceeded,

        /// <summary>候选全试完了都不对。</summary>
        WrongPassword,

        Corrupted,

        /// <summary>条目路径不安全（预检拦下），本层不落盘。</summary>
        UnsafeEntry,

        UserCancelled,

        EngineFailed
    }

    /// <summary>递归展开的最终结论。</summary>
    public sealed class RecursionResult
    {
        public RecursionStopReason StopReason { get; init; }

        /// <summary>真的走完了（含 SingleLayer 这种"按模式正常收尾"）。</summary>
        public bool Completed { get; init; }

        /// <summary>解出了一些层但没走完（含 NeedsDecision，以及产物留在工作区的情形）。</summary>
        public bool PartiallyCompleted { get; init; }

        /// <summary>非 null 表示需要用户就多分支做选择。</summary>
        public RecursionDecisionRequest? Decision { get; init; }

        public IReadOnlyList<RecursionLayerReport> Layers { get; init; } = Array.Empty<RecursionLayerReport>();

        /// <summary>指向**已完成的最深一层**产物；部分完成时这里是工作区里的路径。</summary>
        public string FinalOutputPath { get; init; } = string.Empty;

        /// <summary>一行中文结论，直接显示给用户。</summary>
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>
    /// 递归解压核心（M4）。
    ///
    /// 定位与边界：
    /// 1. 引擎、探测器、密码来源**全部从构造函数注入** —— 递归核心不许自己 new 引擎、
    ///    不许直接拼 7z 参数、不许认识 GUI（AGENTS.md §3.1 四条禁止项）。这样才能脱离 GUI 被测。
    /// 2. 它只**解压**：不删源文件（那是 SourceCleanupService 的事）、不改名、不写最终目录
    ///    （中间产物一律先落工作区，完成后再发布）。
    /// 3. 密码候选由 <c>passwordProvider</c> 给，本类不碰密码本、不记明文；
    ///    对外只暴露 "空密码" / "******"。
    ///
    /// 主循环是**一趟宽度优先**的：队列里每一项 = "一个待解的归档 + 它在第几层"，
    /// 解一层、探一层、再决定要不要把探测到的内层归档入队。
    /// 之所以不用递归写法：层数上限、累计预算、以及"多分支要问用户"这三件事，
    /// 在循环里都只在**入队前**检查一次，比散在递归调用里更好审。
    /// </summary>
    public sealed class RecursiveExtractor
    {
        /// <summary>说明类文件后缀：它们和内层归档同级出现时，不构成"多分支"。</summary>
        private static readonly string[] InformationalExtensions =
        {
            ".txt", ".nfo", ".url", ".md", ".sfv", ".jpg", ".png"
        };

        /// <summary>展开比检查的上限保护：只对最大若干层做（防止有人把上限配得极大时白算）。</summary>
        private const int MaxExpansionRatioChecks = 20;

        private readonly IArchiveEngine _engine;
        private readonly IArchiveProber _prober;
        private readonly Func<string, IReadOnlyList<string>> _passwordProvider;
        private readonly RecursionLimits _limits;

        /// <summary>
        /// passwordProvider：给定归档路径，返回按优先级排好的密码候选（**空字符串代表试空密码**）。
        /// 它由调用方注入，递归层自己不碰密码本，也不记明文。
        /// </summary>
        public RecursiveExtractor(
            IArchiveEngine engine,
            IArchiveProber prober,
            Func<string, IReadOnlyList<string>> passwordProvider,
            RecursionLimits? limits = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _prober = prober ?? throw new ArgumentNullException(nameof(prober));
            _passwordProvider = passwordProvider ?? (_ => Array.Empty<string>());
            _limits = limits ?? RecursionLimits.Default;
        }

        /// <summary>
        /// 从 <see cref="ArchiveTask.CurrentPath"/> 开始递归展开。
        /// </summary>
        /// <param name="previousDecision">
        /// 非 null 表示"用户已经就上一次的多分支询问做出了选择：继续"。
        /// 这时只处理 <see cref="RecursionDecisionRequest.CandidateArchives"/> 里那几个归档，
        /// **不重新全盘扫描** —— 用户看到的候选清单可能已经被别的东西改过，重扫等于偷偷扩大范围。
        /// </param>
        public async Task<RecursionResult> ExtractAsync(
            ArchiveTask task,
            string finalOutputDirectory,
            RecursionMode mode,
            RecursionDecisionRequest? previousDecision = null,
            CancellationToken cancellationToken = default)
        {
            var layers = new List<RecursionLayerReport>();
            var stopReason = RecursionStopReason.None;

            /*
             * 这个字段只表示"**这一次运行**有没有产生新的、还没回答的询问"。
             * 刻意不从 previousDecision 起手：传了 previousDecision 就说明用户已经回答过了，
             * 把它原样塞回结果会让调用方以为"还得再问一次"（RecursionResult.Decision 的约定是
             * "非 null = 需要用户就多分支做选择"）。
             */
            RecursionDecisionRequest? decision = null;

            int totalFiles = 0;
            long totalSize = 0;

            // 参数校验放在建工作区之前：路径都没有就没什么可展开的，
            // 这里也**不抛异常**（递归核心对外的约定是"用返回值说清楚"，不是"炸给调用方"）。
            if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                return new RecursionResult
                {
                    StopReason = RecursionStopReason.EngineFailed,
                    Completed = false,
                    PartiallyCompleted = false,
                    Decision = decision,
                    Layers = Array.Empty<RecursionLayerReport>(),
                    FinalOutputPath = string.Empty,
                    Summary = "任务没有可处理的归档路径，未开始递归解压"
                };
            }

            // 工作区必须在循环之前就建出来：取消 / 立刻失败时也要留下"产物在哪"的线索。
            ExtractionWorkspace workspace = CreateWorkspace(task);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pending = new Queue<WorkItem>();

                /*
                 * previousDecision 的语义（规则 4）：用户已经就上次的多分支询问选了"继续"。
                 * 把候选清单压成**文件名**挂在第 0 层的执行项上 —— 后面每一层都拿它做过滤，
                 * 于是"只处理用户点名的这几个归档、不重新全盘扫描"这件事在代码里只有一处实现。
                 *
                 * 为什么不能直接比完整路径：决策里带的是**上一次运行的工作区**里的路径，
                 * 而本次续跑会新建一个工作区（taskId 带时间戳 + 随机码），绝对前缀必然不同 ——
                 * 拿完整路径比对会把用户点过的每一个分支都判成"这一层里没有"，选择被静默丢掉。
                 * 层内部的文件名是两次运行之间唯一稳定的定位键（同一个归档、同一个引擎解出来）。
                 */
                IReadOnlyList<string>? decidedNames = previousDecision == null
                    ? null
                    : previousDecision.CandidateArchives
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .ToList();

                pending.Enqueue(new WorkItem
                {
                    Depth = 0,
                    ArchivePath = task.CurrentPath,
                    Layer = workspace.CreateNextLayer(task.CurrentPath),
                    IsRoot = true,
                    DecisionNames = decidedNames
                });

                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    WorkItem item = pending.Dequeue();

                    /*
                     * 上限检查必须在**真正动手之前**做（规则 5），而且顺序固定：
                     * 层数 → 累计文件数 → 累计大小 → 展开比。
                     * 顺序固定是为了让"命中哪一条"可预期、可复现、可写进测试；
                     * 密码尝试上限不在这里查，它是**每层内部**的事（见 ExtractLayerAsync）。
                     */
                    RecursionStopReason blocked = CheckLimitsBeforeLayer(item, layers, totalFiles, totalSize);

                    if (blocked != RecursionStopReason.None)
                    {
                        stopReason = blocked;
                        break;
                    }

                    LayerOutcome outcome = await ExtractLayerAsync(item, cancellationToken)
                        .ConfigureAwait(false);

                    if (outcome.Report != null)
                    {
                        layers.Add(outcome.Report);
                    }

                    if (outcome.StopReason != RecursionStopReason.None)
                    {
                        stopReason = outcome.StopReason;
                        break;
                    }

                    RecursionLayerReport report = outcome.Report!;

                    totalFiles += report.OutputFileCount;
                    totalSize += report.OutputSize;

                    if (!TryEnqueueNextLayers(
                            item,
                            report,
                            workspace,
                            pending,
                            mode,
                            out RecursionDecisionRequest? askUser,
                            out RecursionStopReason enqueueStopReason))
                    {
                        if (askUser != null)
                        {
                            decision = askUser;
                            stopReason = RecursionStopReason.NeedsDecision;
                        }
                        else
                        {
                            stopReason = enqueueStopReason;
                        }

                        break;
                    }
                }

                if (stopReason == RecursionStopReason.None)
                {
                    // 队列空了 = 没有更多可展开的内层归档。
                    stopReason = RecursionStopReason.Completed;
                }

                bool publish = stopReason == RecursionStopReason.Completed;

                return BuildResult(
                    stopReason,
                    layers,
                    decision,
                    workspace,
                    finalOutputDirectory,
                    string.Empty,
                    publish);
            }
            catch (OperationCanceledException)
            {
                /*
                 * 取消不是失败（AGENTS.md §6 第 6 条）：保留工作区，让用户能看到已经解出来的部分，
                 * 也能从那里接着处理。绝不在这里抛异常给上层。
                 */
                return BuildResult(
                    RecursionStopReason.UserCancelled,
                    layers,
                    decision,
                    workspace,
                    finalOutputDirectory,
                    string.Empty,
                    published: false);
            }
            catch (Exception ex)
            {
                // 兜底：一个包炸了不能把整批任务带下水（AGENTS.md §6 第 9 条）。
                return BuildResult(
                    RecursionStopReason.EngineFailed,
                    layers,
                    decision,
                    workspace,
                    finalOutputDirectory,
                    PasswordMasker.Sanitize(ex.Message),
                    published: false);
            }
        }

        /// <summary>
        /// 解一层：逐个密码候选试，直到成功或确定停因。
        /// </summary>
        private async Task<LayerOutcome> ExtractLayerAsync(
            WorkItem item,
            CancellationToken cancellationToken)
        {
            var options = new ExtractOptions
            {
                // 每层都是全新目录，正常情况下不会撞名；万一撞上（归档内有重复条目）
                // 一律"跳过 + 保留先落地的那个"，绝不覆盖：改名属于改名模块的事，递归核心不越权。
                OverwriteMode = "SkipExisting",
                KeepArchiveNameFolder = false
            };

            options.Normalize();

            IReadOnlyList<string> candidates = BuildPasswordCandidates(item.ArchivePath);

            int attempts = 0;
            bool triedAny = false;
            ArchiveOperationResult? lastFailure = null;
            ArchiveOperationResult? success = null;
            string? succeededPassword = null;

            foreach (string candidate in candidates)
            {
                if (attempts >= _limits.MaxPasswordAttemptsPerLayer)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                attempts++;
                triedAny = true;

                ArchiveOperationResult result = await _engine.ExtractAsync(
                        new ArchiveRequest
                        {
                            ArchivePath = item.ArchivePath,
                            OutputPath = item.Layer.OutputPath,
                            Password = candidate
                        },
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.Success)
                {
                    succeededPassword = candidate;
                    success = result;
                    lastFailure = null;
                    break;
                }

                lastFailure = result;

                /*
                 * 损坏不再换候选重试（规则 1）：换密码对损坏的归档没有任何帮助，
                 * 只会把同一个损坏包重试 N 遍，既浪费时间又掩盖真正的问题。
                 */
                if (result.IsCorrupted)
                {
                    return LayerOutcome.Stop(
                        BuildLayerReport(item, result, succeededPassword: null),
                        RecursionStopReason.Corrupted);
                }

                // 需要密码 / 密码错误 → 这个候选没用了，试下一个。
                if (result.IsWrongPassword || result.IsNeedPassword)
                {
                    continue;
                }

                // 其余（引擎不可用、路径问题、输出冲突…）换密码也解决不了，直接停。
                return LayerOutcome.Stop(
                    BuildLayerReport(item, result, succeededPassword: null),
                    MapEngineErrorToStopReason(result));
            }

            if (succeededPassword == null)
            {
                RecursionLayerReport failureReport = BuildLayerReport(item, lastFailure, succeededPassword: null);

                RecursionStopReason reason = ResolvePasswordStopReason(
                    candidates.Count,
                    attempts,
                    lastFailure,
                    triedAny);

                return LayerOutcome.Stop(failureReport, reason);
            }

            (int fileCount, long outputSize) = OutputVerifier.Measure(item.Layer.OutputPath);

            IReadOnlyList<string> innerArchives = await ProbeInnerArchivesAsync(
                    item.Layer.OutputPath,
                    cancellationToken)
                .ConfigureAwait(false);

            var report = new RecursionLayerReport
            {
                Depth = item.Depth,
                ArchivePath = item.ArchivePath,
                OutputPath = item.Layer.OutputPath,
                Success = true,

                // 状态一律取引擎给的（成功时就是"解压成功"），不在这里另造一套口径。
                Status = string.IsNullOrWhiteSpace(success?.Status)
                    ? StatusText.ExtractSuccess
                    : success!.Status,
                Message = string.Empty,
                InnerArchives = innerArchives,
                OutputFileCount = fileCount,
                OutputSize = outputSize,
                UsedPasswordMasked = PasswordMasker.Mask(succeededPassword)
            };

            return LayerOutcome.Ok(report);
        }

        /// <summary>
        /// 引擎给的错误分类 → 递归停因。
        ///
        /// 这里刻意**按引擎的分类枚举**（DetectedErrorType）而不是按中文状态串来判：
        /// 中文文案是给用户看的，随时可能改；用它做控制流会让"改一句提示"变成"改行为"。
        /// </summary>
        private static RecursionStopReason MapEngineErrorToStopReason(ArchiveOperationResult result)
        {
            return result.DetectedErrorType switch
            {
                "CorruptedArchive" => RecursionStopReason.Corrupted,
                "WrongPassword" => RecursionStopReason.WrongPassword,
                "NeedPassword" => RecursionStopReason.WrongPassword,
                "Cancelled" => RecursionStopReason.UserCancelled,
                "UnsafePath" => RecursionStopReason.UnsafeEntry,
                _ => RecursionStopReason.EngineFailed
            };
        }

        /// <summary>
        /// 判定"全都没解开"时到底是密码错误还是达到尝试上限（规则 5 与规则 6 的分界）。
        /// </summary>
        private static RecursionStopReason ResolvePasswordStopReason(
            int candidateCount,
            int attempts,
            ArchiveOperationResult? lastFailure,
            bool triedAny)
        {
            if (lastFailure != null && lastFailure.IsCorrupted)
            {
                return RecursionStopReason.Corrupted;
            }

            if (!triedAny)
            {
                /*
                 * 一个候选都没有：调用方的密码来源没给出任何东西。
                 * 这里归到 WrongPassword（"没有可用密码"），而不是 EngineFailed ——
                 * 对用户来说要做的动作是一样的（去补一个密码），而 EngineFailed 会把他引向查引擎，
                 * 那是错的方向。
                 */
                return RecursionStopReason.WrongPassword;
            }

            // 候选还有剩、却因为上限停手 —— 这才是"达到密码尝试上限"。
            if (candidateCount > attempts)
            {
                return RecursionStopReason.PasswordAttemptsExceeded;
            }

            // 候选全试完了都不对 —— 常规的"密码错误"。
            return RecursionStopReason.WrongPassword;
        }

        /// <summary>
        /// 探测本层产物里的内层归档（**递归**找所有文件，跳过 0 字节与工作区自身）。
        /// </summary>
        private async Task<IReadOnlyList<string>> ProbeInnerArchivesAsync(
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            var found = new List<string>();
            var pending = new Stack<string>();

            pending.Push(outputDirectory);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    // 单个目录读不了就跳过；漏判只是少展开一层，不该让整次递归失败。
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        continue;
                    }

                    // 不跟随符号链接 / 联接点：跟着走既可能绕圈，也会把工作区外的文件算成"内层归档"。
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (IsZeroByteFile(entry))
                    {
                        // 0 字节文件不可能是归档，却能被某些"看后缀"的探测器误判，直接排除。
                        continue;
                    }

                    bool isArchive = await _prober
                        .IsArchiveAsync(entry, cancellationToken)
                        .ConfigureAwait(false);

                    if (isArchive)
                    {
                        found.Add(entry);
                    }
                }
            }

            // 排序让候选清单在 UI 与测试里都是稳定顺序（不同文件系统返回顺序不一致）。
            found.Sort(StringComparer.OrdinalIgnoreCase);

            return found;
        }

        /// <summary>
        /// 一层成功之后判断是否还要继续，以及继续哪些。
        /// 返回 false 表示停止，并通过 out 参数说明是"要问用户"还是"命中上限"。
        ///
        /// 判断顺序是固定的：模式（SingleLayer 直接收尾）→ 用户点名的清单过滤 → 数量上限 →
        /// 单链启发式 / 要不要问用户 → 层数上限后入队。
        /// 这个顺序本身就是规则（规则 4 与规则 5 的交界），改任何一步之前先读那一步的注释。
        /// </summary>
        private bool TryEnqueueNextLayers(
            WorkItem item,
            RecursionLayerReport report,
            ExtractionWorkspace workspace,
            Queue<WorkItem> pending,
            RecursionMode mode,
            out RecursionDecisionRequest? decision,
            out RecursionStopReason stopReason)
        {
            decision = null;
            stopReason = RecursionStopReason.None;

            IReadOnlyList<string> innerArchives = report.InnerArchives;

            if (innerArchives.Count == 0)
            {
                // 这一层里没有可继续的东西，队列自然消耗干净；停因在循环结束时统一判定为 Completed。
                return true;
            }

            if (mode == RecursionMode.SingleLayer)
            {
                /*
                 * 只解当前这一层：里面的归档原样留着，用户想展开再单独发起。
                 *
                 * 这一条必须排在数量上限之前 —— 单层模式根本不继续，"里面有几个内层归档"对结论
                 * 没有任何影响，在这里报 TooManyInnerArchives 只会给出一个与用户选择无关的停因。
                 */
                return true;
            }

            List<string> toProcess = innerArchives.ToList();

            /*
             * previousDecision 的语义（规则 4）：只处理用户点名的那几个归档。
             * 按**文件名**比对（大小写不敏感，见 DecisionNames 的注释），**不重新全盘扫描** ——
             * 用户点头的是他看到的那个清单，不是"这一层里现在有什么"。
             */
            bool decidedByUser = item.DecisionNames != null;

            if (decidedByUser)
            {
                var chosen = new HashSet<string>(item.DecisionNames!, StringComparer.OrdinalIgnoreCase);

                toProcess = toProcess
                    .Where(path => chosen.Contains(Path.GetFileName(path)))
                    .ToList();
            }

            /*
             * 数量上限的位置是刻意的（规则 4 与规则 5 的交界）：
             * ① 放在"用户点名的清单"过滤**之后**：他点的就是他要的，先查上限会让"只选 3 个"
             *    也被拦下，与 previousDecision 的语义直接冲突；
             * ② 放在"要不要问用户"**之前**：一层里冒出几十个内层归档，说明这不是套娃包，
             *    而是一个归档集合 —— 该给的结论是 TooManyInnerArchives（去逐个单独处理），
             *    而不是把同一个事实包装成"要不要继续展开"再问一遍，那个问题对用户没有意义。
             */
            if (toProcess.Count > _limits.MaxInnerArchivesPerLayer)
            {
                stopReason = RecursionStopReason.TooManyInnerArchives;
                return false;
            }

            if (!decidedByUser && mode == RecursionMode.SingleChain)
            {
                bool isSingleChain = toProcess.Count == 1 && HasOnlyInformationalSiblings(report, toProcess[0]);

                if (!isSingleChain)
                {
                    /*
                     * 多分支询问只在第 0 层发生。
                     * 理由：用户要回答的是"这个包里的分叉要不要都展开"，那是**源包层面**的问题；
                     * 一旦他选了"继续"，后面每一层再问一遍就成了骚扰（而且他也没法逐层判断）。
                     * 后续每层都仍受全部上限约束，安全阀并没有因此失效。
                     */
                    if (item.Depth > 0)
                    {
                        /*
                         * 更深的层里出现多分支：不再问第二遍，就停在这一层。
                         * 这不算失败 —— 该层的产物已经解出来了，只是没有继续往里钻。
                         */
                        return true;
                    }

                    decision = BuildDecision(item, innerArchives);
                    return false;
                }
            }

            foreach (string innerArchivePath in toProcess)
            {
                /*
                 * 层数上限在入队前查（规则 5）：下一层已经到 MaxDepth 就不再入队，
                 * 已经排在前面的项照常解完 —— 那些产物是有效的，扔掉它们没有意义。
                 *
                 * 判据是**层号** item.Depth + 1，不是"已经解了几层"（或者队列长度）：
                 * 分支模式下同一层会排好几项，拿已解层数当层号会把合法的宽树误拦
                 * （两个分支、MaxDepth = 3 时，第二个分支会被算成"第 3 层"而拦掉）。
                 */
                if (item.Depth + 1 >= _limits.MaxDepth)
                {
                    stopReason = RecursionStopReason.MaxDepthReached;
                    return false;
                }

                pending.Enqueue(new WorkItem
                {
                    Depth = item.Depth + 1,
                    ArchivePath = innerArchivePath,
                    Layer = workspace.CreateNextLayer(innerArchivePath),
                    IsRoot = false
                });
            }

            return true;
        }

        /// <summary>
        /// 单链判定：同层除内层归档外，其余文件必须都是说明类文件（.txt/.nfo/.url/.md/.sfv/.jpg/.png）。
        /// 只要冒出一个别的类型的文件，就说明这层产物本身就是"用户要的东西"，
        /// 里面那个归档未必是主角 —— 这种情况必须问，不能替用户决定。
        /// </summary>
        private static bool HasOnlyInformationalSiblings(RecursionLayerReport report, string innerArchivePath)
        {
            string outputRoot = SafePathHelper.GetFullPathSafe(report.OutputPath);
            string archivePath = SafePathHelper.GetFullPathSafe(innerArchivePath);

            if (outputRoot.Length == 0 || archivePath.Length == 0)
            {
                return false;
            }

            var pending = new Stack<string>();
            pending.Push(outputRoot);

            while (pending.Count > 0)
            {
                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    // 读不了就当作"有疑问"，走需要询问的一侧更安全。
                    return false;
                }

                foreach (string entry in entries)
                {
                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        return false;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // 链接指到哪我们不知道；算作"非说明类"，宁可问用户。
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (string.Equals(
                            SafePathHelper.GetFullPathSafe(entry),
                            archivePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsInformationalFile(entry))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsInformationalFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);

            return InformationalExtensions.Any(
                known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 每次要继续之前检查的硬上限（规则 5）。这里的每一项命中都必须**停止**，没有例外。
        /// </summary>
        private RecursionStopReason CheckLimitsBeforeLayer(
            WorkItem item,
            List<RecursionLayerReport> layers,
            int totalFiles,
            long totalSize)
        {
            /*
             * 层数：只解层号 < MaxDepth 的层（0 起算，所以 MaxDepth = 1 时第 0 层照解、
             * 第 1 层被拦下）—— 这与 AGENTS.md §10 M4 的"达到上限安全停下并报告"一致。
             *
             * 判据是**层号**（item.Depth），不是"已经解了几层"（layers.Count）：分支模式下同一层
             * 会排好几项，拿已解层数当层号会在合法的宽树上假报超限（AllBranches 展开 3 个以上分支时，
             * 第 3 项就会被当成"第 3 层"拦掉）。
             *
             * 这一关也是"入队判据写错"的兜底：每一项都要先过这里才会被解，所以入队处万一漏判，
             * 多出来的层只会停在队列里、以 MaxDepthReached 收场，不会被真的解开。
             */
            if (item.Depth >= _limits.MaxDepth)
            {
                return RecursionStopReason.MaxDepthReached;
            }

            if (totalFiles >= _limits.MaxTotalFiles)
            {
                return RecursionStopReason.MaxTotalFilesReached;
            }

            if (totalSize >= _limits.MaxTotalSize)
            {
                return RecursionStopReason.MaxTotalSizeReached;
            }

            if (layers.Count < MaxExpansionRatioChecks && IsExpansionRatioExceeded(item, layers))
            {
                return RecursionStopReason.ExpansionRatioExceeded;
            }

            return RecursionStopReason.None;
        }

        /// <summary>
        /// 单层展开比检查：本层归档解压后的总大小 / 该归档文件大小，超限即"疑似压缩炸弹"。
        /// 取不到可信数值（文件不在、list 失败、被压缩成 0 字节）时**不拦**：
        /// 拿不准的时候拦下来会把正常包也误伤，而真正的兜底是 MaxTotalSize / MaxTotalFiles 这两条硬线。
        /// </summary>
        private bool IsExpansionRatioExceeded(WorkItem item, List<RecursionLayerReport> layers)
        {
            long archiveSize = SafeFileLength(item.ArchivePath);

            if (archiveSize <= 0)
            {
                return false;
            }

            long uncompressedSize;

            if (item.IsRoot)
            {
                ArchiveListResult list;

                try
                {
                    /*
                     * 只有第 0 层需要问引擎"解压后多大"：那时的归档还是用户给的原始文件，
                     * 磁盘上还没有任何产物可量。
                     *
                     * 这里是**同步等待**一个本该异步的引擎调用，属于刻意取舍：
                     * ① 本次调用是即时返回的元数据查询（7z l），不是长任务；
                     * ② 它挂在一个同步的"上限判定"函数上，判定结果决定后面整条分支，
                     *    写成异步会把 async 渗透到主循环的每一处上限检查里，得不偿失。
                     * 用 GetAwaiter().GetResult() 而不是 .Result：前者保留原始异常类型，
                     * 不会被包成 AggregateException；异常本身由外层 catch 兜住，不会外泄。
                     */
                    list = _engine
                        .ListAsync(ArchiveRequest.For(item.ArchivePath), CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                    // 引擎抛异常时按"拿不准"处理，不拦。
                    return false;
                }

                if (!list.Success)
                {
                    return false;
                }

                uncompressedSize = list.TotalUncompressedSize;
            }
            else
            {
                // 内层归档已经在磁盘上了：它所在那一层的产物实测大小就是它的解压后大小，比 list 更可信。
                uncompressedSize = layers.Count > 0 ? layers[^1].OutputSize : 0;
            }

            if (uncompressedSize <= 0)
            {
                return false;
            }

            return uncompressedSize / (double)archiveSize > _limits.MaxExpansionRatio;
        }

        /// <summary>
        /// 把上次的多分支选择变成"只处理这几个路径"的执行项。
        /// 单独一个方法，是为了让"用户选了什么"这件事在代码里只有一处解释。
        /// </summary>
        private static RecursionDecisionRequest BuildDecision(WorkItem item, IReadOnlyList<string> innerArchives)
        {
            return new RecursionDecisionRequest
            {
                CurrentArchivePath = item.ArchivePath,
                Depth = item.Depth,
                CandidateArchives = innerArchives,
                Prompt =
                    $"检测到 {innerArchives.Count} 个内层归档，预计最多产生 {innerArchives.Count} 个子任务，" +
                    "可能产生大量输出，是否继续？"
            };
        }

        private IReadOnlyList<string> BuildPasswordCandidates(string archivePath)
        {
            IReadOnlyList<string> provided;

            try
            {
                provided = _passwordProvider(archivePath) ?? Array.Empty<string>();
            }
            catch
            {
                // 密码来源出问题（密码本损坏等）不该让递归核心炸掉：当成"没有候选"处理，
                // 后面会以 WrongPassword 收场并如实报告。
                return Array.Empty<string>();
            }

            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string? candidate in provided)
            {
                if (candidate == null)
                {
                    continue;
                }

                // 去重：调用方的候选顺序里常常"最近成功的密码"和"密码本命中"是同一个值，
                // 重复试同一个密码只会白烧一次密码尝试次数。
                if (seen.Add(candidate))
                {
                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        private RecursionLayerReport BuildLayerReport(
            WorkItem item,
            ArchiveOperationResult? result,
            string? succeededPassword)
        {
            string message = result == null
                ? "引擎没有返回结果"
                : PasswordMasker.Sanitize(result.Message);

            return new RecursionLayerReport
            {
                Depth = item.Depth,
                ArchivePath = item.ArchivePath,
                OutputPath = item.Layer.OutputPath,
                Success = false,
                Status = result?.Status ?? StatusText.ExtractFailed,
                Message = message,
                InnerArchives = Array.Empty<string>(),
                OutputFileCount = 0,
                OutputSize = 0,
                UsedPasswordMasked = succeededPassword == null ? string.Empty : PasswordMasker.Mask(succeededPassword)
            };
        }

        private static ExtractionWorkspace CreateWorkspace(ArchiveTask task)
        {
            string sourceName = string.IsNullOrWhiteSpace(task.CurrentPath)
                ? "未命名"
                : FileNameHelper.GetArchiveBaseName(task.CurrentPath);

            /*
             * taskId = 包基名 + 时间戳 + 随机码。
             * 为什么不能只用包名：同一批任务里常有两个同名包（C:\t\a\pack.zip 与 C:\t\b\pack.zip），
             * 共用一个工作区目录会让第二次解压撞上第一次的产物。
             * 为什么不能只用时间戳：并发解压时两个任务可能落在同一毫秒。
             *
             * 这里**不需要**自己做文件名清洗：ExtractionWorkspace 构造时会再清洗一遍，
             * 而且它才是"路径安全"的唯一责任方（清洗两次不冲突，但不能靠这里兜底）。
             */
            string taskId = $"{sourceName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";

            return new ExtractionWorkspace(WorkspaceRootDirectory, taskId);
        }

        /// <summary>
        /// 工作区根目录。
        /// 用系统临时目录而不是源目录旁边（AGENTS.md §6 第 12 条：中间产物不得写进源目录），
        /// 也不写死在某个盘符上（非目标：不写死盘符）。
        /// </summary>
        private static string WorkspaceRootDirectory
        {
            get
            {
                string root = Path.Combine(Path.GetTempPath(), "ArchiveFixer", "recursive");

                SafePathHelper.EnsureDirectoryExists(root);

                return root;
            }
        }

        /// <summary>
        /// 组装最终结论。
        ///
        /// 这里集中处理三件容易搞混的事（规则 6/7/8）：
        /// ① <see cref="RecursionResult.Completed"/> 只在真正走完时为 true；
        /// ② 部分完成时产物**留在工作区**、Summary 里必须写清工作区在哪；
        /// ③ 全完成时才发布，且发布失败要如实说出来，不能因为"层都解完了"就假装成功。
        /// </summary>
        private RecursionResult BuildResult(
            RecursionStopReason stopReason,
            List<RecursionLayerReport> layers,
            RecursionDecisionRequest? decision,
            ExtractionWorkspace workspace,
            string finalOutputDirectory,
            string extraMessage,
            bool published)
        {
            bool completed = stopReason == RecursionStopReason.Completed;
            bool partiallyCompleted = !completed && layers.Any(layer => layer.Success);

            /*
             * 一层都没解出来时指向工作区目录本身：那些情形（取消、第一次就失败）下产物还没有，
             * 但"工作区在哪"正是排查与重试最需要的信息，而这个目录在工作区建出来时就一定存在。
             */
            string finalOutputPath = workspace.Layers.Count > 0
                ? workspace.Layers[^1].OutputPath
                : workspace.TaskDirectory;

            string publishMessage = string.Empty;

            if (published)
            {
                WorkspacePublishResult publishResult = workspace.Publish(finalOutputDirectory);

                publishMessage = publishResult.Message;

                if (publishResult.Success)
                {
                    // 只有真的发出去了才改指向：发布失败时 FinalOutputPath 仍指向工作区里的产物，
                    // 用户按这个路径还能找到东西。
                    finalOutputPath = publishResult.DestinationPath;
                }
                else
                {
                    completed = false;
                    partiallyCompleted = true;
                }
            }

            WriteRecoveryReport(stopReason, layers, workspace, finalOutputPath, completed, partiallyCompleted);

            return new RecursionResult
            {
                StopReason = stopReason,
                Completed = completed,
                PartiallyCompleted = partiallyCompleted,
                Decision = decision,
                Layers = layers.ToList(),
                FinalOutputPath = finalOutputPath,
                Summary = BuildSummary(
                    stopReason,
                    layers,
                    workspace,
                    finalOutputPath,
                    completed,
                    publishMessage,
                    extraMessage)
            };
        }

        private static string BuildSummary(
            RecursionStopReason stopReason,
            List<RecursionLayerReport> layers,
            ExtractionWorkspace workspace,
            string finalOutputPath,
            bool completed,
            string publishMessage,
            string extraMessage)
        {
            int done = layers.Count(layer => layer.Success);

            string reason = DescribeStopReason(stopReason);

            string head = stopReason switch
            {
                RecursionStopReason.Completed => $"已完成 {done} 层递归解压（{reason}）",
                RecursionStopReason.NeedsDecision => $"已完成 {done} 层，停在第 {done + 1} 层，原因：{reason}",
                _ => done > 0
                    ? $"已完成 {done} 层，停在第 {done + 1} 层，原因：{reason}"
                    : $"第 1 层就没能解开，原因：{reason}"
            };

            var parts = new List<string> { head };

            if (completed && !string.IsNullOrWhiteSpace(finalOutputPath))
            {
                parts.Add($"产物：{finalOutputPath}");
            }

            if (!string.IsNullOrWhiteSpace(publishMessage))
            {
                parts.Add(publishMessage);
            }

            /*
             * 没有发布就一定要写明工作区位置（规则 7）：部分完成、取消、等待用户决定这三种情形下
             * 产物都还留在工作区里，用户按这个路径就能看到已经解出来的部分（需求书 §十二：
             * 递归停止后必须保存"工作区位置"）。
             *
             * 判据用 !completed 而不是 partiallyCompleted：取消时可能一层都还没解出来
             * （partiallyCompleted 为 false），但"工作区在哪"同样是用户最需要的信息。
             */
            if (!completed)
            {
                parts.Add($"未发布的产物留在工作区：{workspace.TaskDirectory}");
            }

            if (!string.IsNullOrWhiteSpace(extraMessage))
            {
                parts.Add(extraMessage);
            }

            return string.Join("；", parts);
        }

        private static string DescribeStopReason(RecursionStopReason stopReason)
        {
            return stopReason switch
            {
                RecursionStopReason.Completed => "没有更多内层归档",
                RecursionStopReason.NeedsDecision => "检测到多个内层归档，等待用户决定是否继续展开",
                RecursionStopReason.MaxDepthReached => "已达到最大递归层数",
                RecursionStopReason.MaxTotalFilesReached => "已达到累计输出文件数上限",
                RecursionStopReason.MaxTotalSizeReached => "已达到累计输出总大小上限",
                RecursionStopReason.ExpansionRatioExceeded => "单层展开比超限，疑似压缩炸弹",
                RecursionStopReason.TooManyInnerArchives => "本层内层归档数量超过上限",
                RecursionStopReason.PasswordAttemptsExceeded => "已达到密码尝试次数上限（候选还有剩余）",
                RecursionStopReason.WrongPassword => "密码错误：所有候选都试过了",
                RecursionStopReason.Corrupted => "压缩包损坏",
                RecursionStopReason.UnsafeEntry => "归档内存在不安全路径，已拒绝解压",
                RecursionStopReason.UserCancelled => "用户取消",
                RecursionStopReason.EngineFailed => "引擎操作失败",
                _ => "未知原因"
            };
        }

        /// <summary>
        /// 写 report.json（给"崩溃后可恢复"用）。
        /// 报告里**只有**结构和数量：路径、层号、状态、计数、脱敏后的密码标记。
        /// 密码策略在这里只是**声明**（"每层最多试 N 个候选"），不含任何明文。
        /// </summary>
        private void WriteRecoveryReport(
            RecursionStopReason stopReason,
            List<RecursionLayerReport> layers,
            ExtractionWorkspace workspace,
            string finalOutputPath,
            bool completed,
            bool partiallyCompleted)
        {
            var report = new
            {
                SchemaVersion = 1,
                TaskDirectory = workspace.TaskDirectory,
                RootDirectory = workspace.RootDirectory,
                StopReason = stopReason.ToString(),
                Completed = completed,
                PartiallyCompleted = partiallyCompleted,
                FinalOutputPath = finalOutputPath,
                Engine = new
                {
                    Id = _engine.Id,
                    Version = _engine.Version
                },
                Limits = new
                {
                    _limits.MaxDepth,
                    _limits.MaxTotalFiles,
                    _limits.MaxTotalSize,
                    _limits.MaxExpansionRatio,
                    _limits.MaxInnerArchivesPerLayer,
                    _limits.MaxPasswordAttemptsPerLayer
                },
                PasswordPolicy = "每层按调用方给的候选顺序逐个尝试；报告不记录任何密码内容",
                Layers = layers.Select(layer => new
                {
                    layer.Depth,
                    layer.ArchivePath,
                    layer.OutputPath,
                    layer.Success,
                    layer.Status,
                    layer.Message,
                    layer.OutputFileCount,
                    layer.OutputSize,
                    layer.UsedPasswordMasked,
                    InnerArchives = layer.InnerArchives.ToArray()
                }).ToArray()
            };

            workspace.WriteReport(report);
        }

        private static bool IsZeroByteFile(string path)
        {
            try
            {
                return new FileInfo(path).Length == 0;
            }
            catch
            {
                // 读不到大小就当作非 0，交给探测器去判断（宁可多问一次，也别漏掉真的归档）。
                return false;
            }
        }

        private static long SafeFileLength(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return 0;
                }

                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }

        /// <summary>队列里的一项：一个待解的归档 + 它在第几层 + 它的工作区目录。</summary>
        private sealed class WorkItem
        {
            public int Depth { get; init; }

            public string ArchivePath { get; init; } = string.Empty;

            public WorkspaceLayer Layer { get; init; } = new WorkspaceLayer();

            /// <summary>是否是任务自己的那个包（第 0 层）。</summary>
            public bool IsRoot { get; init; }

            /// <summary>
            /// 非 null 表示"只处理用户点名的这几个归档"。
            ///
            /// 存的是**文件名**而不是完整路径：决策里带的是上一次运行的工作区路径，本次续跑新建工作区后
            /// 前缀必然不同，按完整路径比对会把用户的选择全部判成"没了"。层内部的文件名是唯一稳定的定位键；
            /// 同一层里出现同名归档时两个都会被处理 —— 这是刻意的取舍：宁可多解一个同名分支，
            /// 也不能因为工作区换了就把用户点过的分支静默丢掉。
            ///
            /// 仅在 previousDecision 非 null 时落在第 0 层，后续层不继承（用户回答的是源包层面的问题）。
            /// </summary>
            public IReadOnlyList<string>? DecisionNames { get; init; }
        }

        /// <summary>一层的执行结果：要么成功（带报告，可以继续），要么停下（带报告 + 停因）。</summary>
        private sealed class LayerOutcome
        {
            public RecursionLayerReport? Report { get; private init; }

            public RecursionStopReason StopReason { get; private init; }

            public static LayerOutcome Ok(RecursionLayerReport report)
            {
                return new LayerOutcome { Report = report, StopReason = RecursionStopReason.None };
            }

            public static LayerOutcome Stop(RecursionLayerReport report, RecursionStopReason reason)
            {
                return new LayerOutcome { Report = report, StopReason = reason };
            }
        }
    }
}
