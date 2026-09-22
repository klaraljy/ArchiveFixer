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
        /// <summary>
        /// 「过程物」目录名（契约 §3.2）：为得到内容物而产生、用户不需要的东西 ——
        /// 内层归档、分卷、抠出来的中间 ZIP、纯壳文件夹。
        ///
        /// 名字的**唯一来源**是 <see cref="ProcessArtifactLayout.ArtifactDirectoryName"/>，
        /// 这里只是给本类内部一个短名字用（别在别处再写一遍字面量）。
        /// </summary>
        internal const string ProcessArtifactDirectoryName = ProcessArtifactLayout.ArtifactDirectoryName;

        private readonly MainViewModel _vm;
        private readonly IArchiveEngine _archiveEngine;
        private readonly PasswordService _passwordService;
        private readonly PathService _pathService;
        private readonly DialogService _dialogService;

        /// <summary>
        /// 递归解压（M4）。引擎、探测器、密码来源全部注入，递归层自己不碰密码本。
        ///
        /// 注意：**不在这里留一个构造时就固定上限的实例** ——
        /// <see cref="RecursiveExtractor"/> 的上限是构造参数，构造一次就再也改不了，
        /// 而这里是 ViewModel 层的单例：用户改完设置（层数 / 每层密码上限）不重启程序就不生效。
        /// 所以每次任务现建一个（见 <see cref="CreateRecursiveExtractor"/>），上限当场从设置里取。
        /// </summary>
        private RecursiveExtractor CreateRecursiveExtractor() =>
            new(_archiveEngine, new MagicArchiveProber(), BuildRecursionPasswordCandidates, BuildRecursionLimits());

        /// <summary>
        /// 递归的硬上限（不变量 8）：层数与每层密码尝试次数都取用户的设置项。
        ///
        /// 旧实现的坑：调用方 new 的时候没传 limits，递归核心一直用 <c>RecursionLimits.Default</c> ——
        /// 于是"最大嵌套层数"改成 5 也照样只解 3 层，而每层密码上限被悄悄压到 8
        /// （设置项缺省是 10，用户调大更是不生效）。设置项必须真的管用，不能只是界面上的数字。
        /// </summary>
        private RecursionLimits BuildRecursionLimits() => new()
        {
            MaxDepth = Math.Clamp(Settings.MaxRecursionDepth, 1, 10),
            MaxPasswordAttemptsPerLayer = MaxPasswordAttemptsPerLayer
        };

        /// <summary>解压前预检最多试几个密码候选去列表：试太多次会让"一键"变成等待。</summary>
        private const int MaxPreflightPasswordAttempts = 3;

        /// <summary>
        /// 每个归档（**每一层**）最多真的试几个密码候选（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
        ///
        /// 为什么必须有：解压模式下一个候选 = **一次完整解压**。加密分卷的第二层，
        /// 几百条密码本的包会被逐个候选整包重解一遍（几百 MB 起、磁盘一直在写），
        /// 表现就是"几十分钟不动"，最后还可能把"没试完"报成"密码错误"。
        ///
        /// 到上限时的状态是 <see cref="StatusText.PasswordAttemptLimitReached"/>，**不是**"密码错误"。
        ///
        /// 值取自设置项 <see cref="AppSettings.MaxPasswordAttemptsPerLayer"/>（界面可改、能落盘）。
        /// 这里再夹一次只是兜底：设置对象不一定走过 <see cref="AppSettings.Normalize"/>（例如测试里直接
        /// <c>new AppSettings()</c>），而 0 或负数会让"一个候选都不试"直接变成密码错误 —— 那是最误导人的结论。
        /// （旧写法：这里是个常量 10，用户在设置界面改了不生效。）
        /// </summary>
        private int MaxPasswordAttemptsPerLayer =>
            Math.Clamp(Settings.MaxPasswordAttemptsPerLayer, 1, 1000);

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

            /// <summary>
            /// 产物越出目标根目录的说明（非 null = **不承认**这次解压：不归集、不清理源包）。
            /// 它必须走"结论"这条路回传，而不是像旧实现那样只往日志里塞一行 ERROR。
            /// </summary>
            public string? LandingViolation { get; init; }

            public CollectResult? Collected { get; init; }

            /// <summary>定稿（暂存区 → 最终目录）的结果；没走到定稿这一步时为 null。</summary>
            public StageCommitResult? Commit { get; init; }

            /// <summary>
            /// 定稿**整体失败**（一个内容物都没能搬进最终目录）：与越界同一口径 —— 结论不成立。
            /// 与 <see cref="LandingViolation"/> 分开写，是为了让状态与原因各自说自己的事，
            /// 别让"越界"这个词出现在一句讲搬运失败的提示里。
            /// </summary>
            public bool CommitFailed { get; init; }

            public string CommitFailureMessage { get; init; } = string.Empty;
        }

        /// <summary>定稿搬运的一句话结论（给日志与任务字段用）。</summary>
        internal sealed class StageCommitResult
        {
            public bool Attempted { get; init; }

            /// <summary>搬运过程中被取消（已把搬过去的搬回来，最终目录不留半成品）。</summary>
            public bool Cancelled { get; init; }

            /// <summary>计划里的内容物搬运条数（一条可能是一整个目录）。</summary>
            public int PlannedContentCount { get; init; }

            /// <summary>真的搬过去的内容物条数。</summary>
            public int MovedContentCount { get; init; }

            /// <summary>真的搬过去的过程物条数。</summary>
            public int MovedProcessCount { get; init; }

            /// <summary>内容物文件数（来自定稿计划，不是"搬了几条"）。</summary>
            public int ContentFileCount { get; init; }

            public long ProcessArtifactBytes { get; init; }

            public int RenamedCount { get; init; }

            public int FailedCount { get; init; }

            public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

            public string Message { get; init; } = string.Empty;

            /// <summary>要带回 UI 线程写日志的行（后台线程不许直接写界面集合）。</summary>
            public List<(string Level, string Message)> LogEntries { get; init; } = new();
        }

        /// <summary>
        /// 定稿布局计划：直接包住 <see cref="ResultFinalizer"/> 的结论，只加三样执行阶段要用的东西 ——
        /// 过程物源路径的集合（执行时判断一条移动属于哪一类）与"这是不是一次失败的规划"。
        ///
        /// 为什么不再自己算一套相对路径：判定表（契约 §3.1：终端单文件直接放 / 多文件套一层 /
        /// 多重空目录提上来 / 单链塌缩）**只有一份实现**，就在 <see cref="ResultFinalizer"/> 里。
        /// 本类只负责"把暂存树描述给它、把它的计划执行掉"。
        /// </summary>
        internal sealed class FinalLayoutPlan
        {
            public bool Failed { get; init; }

            public string FailureReason { get; init; } = string.Empty;

            public FinalizeLayoutKind Layout { get; init; } = FinalizeLayoutKind.Empty;

            /// <summary>全部移动（**内容物在前、过程物在后**，按这个顺序执行）。</summary>
            public IReadOnlyList<PlannedMove> Moves { get; init; } = Array.Empty<PlannedMove>();

            /// <summary>计划里的内容物条数。</summary>
            public int PlannedContentCount { get; init; }

            /// <summary>哪几条是过程物（按 From 查，执行时用）。</summary>
            public HashSet<string> ProcessArtifactSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            public string ProcessArtifactDirectory { get; init; } = string.Empty;

            public long ProcessArtifactTotalSize { get; init; }

            public int ContentFileCount { get; init; }

            public string Summary { get; init; } = string.Empty;

            public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// 解压成功之后的收尾：**定稿搬运** → 校验 → 归集 → 视开关清理源包（M3/契约 §2）。
        ///
        /// 顺序不能换：
        /// 0. 先定稿 —— 暂存区里的产物只有搬到最终目录才算"结果"，校验也必须以搬完之后的落点为对象
        ///    （契约 §2.2：定稿是搬运，不是"原地解压"）；
        /// 1. 再校验 —— 没有校验就没有"删除源包"的资格（AGENTS.md §9.5）；
        /// 2. 再归集 —— 归集是移动；先归集再校验会算不准（文件已经不在原输出目录）；
        /// 3. 最后清理源包 —— 它依赖前两步的结论。
        ///
        /// 任何一步失败都**不改变**"解压成功"这个结论，只是把结论写进日志与任务字段；
        /// 不要因为归集或清理失败就把任务标成失败 —— 用户的文件确实解出来了。
        ///
        /// 三个例外（它们不是"收尾失败"，而是**解压结论本身不成立**，必须顶掉"解压成功"）：
        /// ① 产物越出目标根目录（不变量 4）—— 不归集、不清理，状态落成失败；
        /// ② 产物超出资源预算 —— 同样是失败，并停止后续任务；
        /// ③ 定稿时被取消 —— 已经搬过去的会搬回来，最终目录不留半成品，状态落成"已取消"。
        ///
        /// 返回值就是这个语义：<c>true</c> = "解压成功"仍然成立；<c>false</c> = 已被上面三条否掉，
        /// 调用方**不许**再补一句"解压成功：xxx"（那会与任务状态自相矛盾，等于骗人）。
        ///
        /// 线程规则（这是 P0 修复的关键）：
        /// 收尾里的"全目录枚举 / 二次遍历 / 定稿搬运 / 归集移动 / 删除源包"**全是同步磁盘活**，
        /// 之前整段跑在 UI 线程上（整条管线没有 ConfigureAwait(false)，await 的续体全回 Dispatcher），
        /// 780MB 的包解出几千个文件时窗口彻底无响应 —— 与"抠出内嵌归档"是同一个坑，
        /// 那边已经改成 Task.Run 并写了注释，收尾这段是漏改。
        /// 现在重活统一进 <see cref="RunPostProcessWork"/> 交给 Task.Run，
        /// await 回来（没有 ConfigureAwait(false)，续体仍在 UI 上下文）才写任务状态与日志。
        ///
        /// 取消规则（P1）：收尾每一步之前都查令牌。用户按了「取消当前」就不许再移动产物、更不许删源包
        /// （AGENTS.md §9.5：取消、部分完成、校验失败一律不删）。取消由上层 catch 落成"已取消"，不得显示成功。
        /// </summary>
        /// <param name="stageDirectory">
        /// 本任务的暂存目录（入仓阶段的产物在这里，最终目录此时还是干净的）。
        /// </param>
        private async Task<bool> PostProcessSuccessAsync(
            ArchiveTask task,
            string engineArchivePath,
            string password,
            string stageDirectory,
            string outputRedirectNote,
            OutputPlacementMode placementMode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1) 校验：拿到引擎声明的条目数与总大小，和落盘结果对一遍。
            // 清单同样取自**真正解开的那份归档**：内嵌归档要拿抠出来的文件去列，源文件 7z 根本打不开。
            // 密码照旧传进去：加密头（-mhe）的包不给密码根本列不出清单，校验会直接退化成"没法比"。
            ArchiveListResult expected = await _archiveEngine.ListAsync(
                ArchiveRequest.For(engineArchivePath, password),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // 开关在进后台之前读一次：设置是用户可改的，别让后台线程读到半路改掉的值。
            bool collectResults = Settings.CollectResultsToDirectory;
            string collectTargetDirectory = Settings.CollectTargetDirectory;
            bool deleteSource = Settings.DeleteSourceAfterExtract;

            /*
             * 终端落法（规格 §3.1 / 设置项 TerminalLayoutMode）同样在这里读一次并解析：
             * 解析口径唯一（OutputPlacement.ParseTerminalLayoutMode），非法值回落 KeepLastFolder，
             * 所以一个读不懂的配置只会退到"最不意外"的那一档，不会把落点算成别的东西。
             */
            TerminalLayoutMode terminalLayout = OutputPlacement.ParseTerminalLayoutMode(Settings.TerminalLayoutMode);

            PostProcessWorkResult work = await Task.Run(
                () => RunPostProcessWork(
                    task,
                    stageDirectory,
                    expected,
                    collectResults,
                    collectTargetDirectory,
                    deleteSource,
                    placementMode,
                    terminalLayout,
                    cancellationToken),
                cancellationToken);

            // 回到 UI 线程：只做状态与日志，不再碰大盘。
            foreach ((string level, string message) in work.LogEntries)
            {
                AppendLog(level, message);
            }

            /*
             * 定稿被取消：产物已经搬回暂存区，最终目录里没有半成品。
             * 这里必须抛出去让上层落成"已取消"（不变量 6：取消不得显示成功），
             * 而不是 return false —— 那会被上层当成"结论不成立"，任务变成"解压失败"，
             * 用户看到的原因就成了"失败"，但他的操作其实是取消。
             */
            if (work.Commit?.Cancelled == true)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (work.BudgetExceeded)
            {
                task.Status = StatusText.ExtractFailed;
                task.ErrorMessage = work.BudgetMessage;

                // 不发布、不清理：产物留在原地让用户自己判断，源文件更不能删。
                StopAfterCurrent();
                return false;
            }

            if (!string.IsNullOrWhiteSpace(work.LandingViolation))
            {
                /*
                 * 产物越界（不变量 4）：这不是"收尾出的小问题"，而是**这次解压的结果不能承认**。
                 *
                 * 旧实现只写了一行 ERROR 日志：任务照样是"解压成功"，产物照样被归集走、源包照样被删 ——
                 * 用户的文件已经写到目标根之外了，程序却报告一切正常。现在与递归路径同一口径：
                 * 状态落成失败、原因写进 ErrorMessage，归集与清理在 RunPostProcessWork 里就已经不做了
                 * （它在越界处直接 return，这里也**不允许**补做任何一步）。
                 */
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = work.LandingViolation;

                // 结论不成立时不许在详情里显示"输出校验通过"。
                task.IsOutputVerified = false;
                task.LastUpdatedTime = DateTime.Now;

                return false;
            }

            if (work.CommitFailed)
            {
                // 定稿一个内容物都没搬过去：产物还在暂存区，最终目录是空的 —— 不能报成功（不变量 6）。
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = work.CommitFailureMessage;
                task.IsOutputVerified = false;
                task.LastUpdatedTime = DateTime.Now;

                return false;
            }

            task.IsOutputVerified = work.Verification.Verified;

            // 把"实际输出到别处"这件事写进任务对象：输出目录被自动改名时，用户必须在任务上看得见，
            // 不能只在日志里留一句就过去（AGENTS.md 不变量 6 的同一精神：结论不能静默）。
            string verifyMessage = string.IsNullOrWhiteSpace(outputRedirectNote)
                ? work.Verification.Message
                : $"{work.Verification.Message}；{outputRedirectNote}";

            // 定稿里搬不动的文件也必须让用户看得见：产物还在暂存区，不在他打开的那个目录里。
            if (work.Commit is { FailedCount: > 0 })
            {
                verifyMessage += $"；定稿时有 {work.Commit.FailedCount} 个文件没能搬到最终目录" +
                                 $"（{string.Join("、", work.Commit.Failures.Take(5))}），它们仍在暂存区。";
            }

            task.VerifyMessage = verifyMessage;

            if (work.Collected != null && work.Collected.Success)
            {
                task.CollectedPath = work.Collected.DestinationPath;
            }

            /*
             * 中间工作区清理（P1，端到端验收实测一次成功就留下近 1 GB 垃圾）。
             *
             * 位置与条件都在这里定死：**解压成功 + 输出校验通过 + 没被取消**。
             * 越界 / 超预算在上面已经 return（结论不成立）；取消会让上面的 await 抛 OperationCanceledException；
             * 校验没过时产物可能不全，工作区里的中间件是用户唯一的线索 —— 一律不删。
             *
             * 入仓（stage）之后这里更要紧了：暂存目录就在本任务的工作区里，产物已经定稿搬走，
             * 剩下的全是可再生的中间件。
             */
            if (work.Verification.Verified && !cancellationToken.IsCancellationRequested)
            {
                CleanupTaskWorkspaceDirectory(task);
            }

            // 走到这里说明"解压成功"这个结论没有被越界 / 超预算顶掉。
            return true;
        }

        /// <summary>
        /// 收尾重活本体：落点校验 → 结果校验 → 预算事后判定 → **定稿搬运** → 归集 → 清理源包。
        ///
        /// 校验与预算量的都是**暂存区**：那才是引擎真正写盘的地方（契约 §2.1）。
        /// 最终目录此时还是干净的，只有定稿那一步会往里面写东西。
        ///
        /// **只允许在后台线程上跑**（见 <see cref="PostProcessSuccessAsync"/> 的线程规则）：
        /// 这里只碰文件系统，不写任务状态、不写界面日志集合，要说的都放进 LogEntries 回传。
        /// </summary>
        private PostProcessWorkResult RunPostProcessWork(
            ArchiveTask task,
            string stageDirectory,
            ArchiveListResult expected,
            bool collectResults,
            string collectTargetDirectory,
            bool deleteSource,
            OutputPlacementMode placementMode,
            TerminalLayoutMode terminalLayout,
            CancellationToken cancellationToken)
        {
            var logEntries = new List<(string Level, string Message)>();

            cancellationToken.ThrowIfCancellationRequested();

            OutputVerificationResult verification = OutputVerifier.Verify(
                stageDirectory,
                expected.Success ? expected : null);

            /*
             * 解压后落点校验（第二道防线，不变量 4）：产物必须都落在目标根目录之内。
             *
             * 量的是**暂存区**：引擎写盘的地方就是它（契约 §2.1）。落点校验必须在定稿**之前**做，
             * 否则越界的产物已经被搬进最终目录，再报"拒绝承认"就晚了 —— 用户目录里已经多了东西。
             *
             * 旧写法（本轮修掉）：越界时只写一行 ERROR 日志，**结论照样是"解压成功"**，
             * 而且照样归集产物、照样清理源包 —— 等于把不变量 4 降级成一条没人看的提示：
             * 越界的产物被搬进结果目录、源包被删掉，程序却报一切正常。
             *
             * 现在与递归路径（RecursiveExtractor 的第二道防线）同一口径：
             * 越界 = **失败结论**，不定稿、不归集、不清理，原因写回任务状态与错误信息。
             * 这里直接 return，让"不定稿不归集不清理"由控制流保证 —— 不依赖后面每一步都记得查这个标志。
             *
             * 复用 RecursiveExtractor.FindLandingViolation 而不是就地再枚举一遍：
             * 两条路径的口径必须一样，复制一份迟早分叉；而且它还能发现目录联接点 / 符号链接
             * （名字在产物目录里、内容却写到目录外）—— 旧写法只枚举文件，看不出这一类。
             */
            string? landingViolation = RecursiveExtractor.FindLandingViolation(stageDirectory);

            if (landingViolation != null)
            {
                string landingMessage = "产物越出目标根目录，已拒绝承认本次解压：" + landingViolation;

                logEntries.Add(("ERROR", $"{task.FileName}：{landingMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    LandingViolation = landingMessage
                };
            }

            /*
             * 运行时预算的事后判定。
             *
             * 7z 写盘的时候我们拦不住（外部进程，我们看不到它的写入），所以"超限就停"只能在事后做：
             * 量一遍产物，超了就**停止后续任务**并把这一单标出来 —— 至少不会让整盘被一个包吃掉。
             * 这条只在引擎给不出清单时才真正有意义，但事后量一遍很便宜，就一直做。
             */
            (int producedFiles, long producedSize) = OutputVerifier.Measure(stageDirectory);
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

            /*
             * 2) 定稿（契约 §2.2）：把暂存产物**一次性**搬到最终目录，中间件归入 <c>过程物</c>。
             *
             * 只在这一刻、只有这一条路径会往最终目录写东西。之前的所有动作（抠内嵌归档、解第一层、
             * 解内层分卷）都在私有暂存目录里完成 —— 这就是用户那句"分卷文件你居然又解压到外面来了"
             * 的根治点：源目录与最终目录在入仓阶段一个字节都不会被写。
             *
             * 搬运本身是**同步磁盘活**（几千个文件的 File.Move、跨盘还是拷贝），
             * 所以它跟着本方法一起跑在后台线程上（见 PostProcessSuccessAsync 的线程规则）。
             */
            StageCommitResult? commit = ExecuteFinalLayout(
                task,
                stageDirectory,
                task.OutputPath,
                placementMode,
                terminalLayout,
                cancellationToken);

            if (commit.Attempted)
            {
                foreach ((string level, string message) in commit.LogEntries)
                {
                    logEntries.Add((level, message));
                }
            }

            if (commit.Cancelled)
            {
                // 取消：搬过去的已经搬回暂存区，最终目录没有半成品。结论由上层落成"已取消"。
                logEntries.Add(("WARN", $"{task.FileName}：定稿被取消，产物已退回暂存目录，未写入最终目录。"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    Commit = commit
                };
            }

            if (commit.Attempted && commit.PlannedContentCount > 0 && commit.MovedContentCount == 0 && commit.FailedCount > 0)
            {
                /*
                 * 计划里的内容物一条都没搬过去、而且全是失败：这次"定稿"等于没发生。
                 * 不能再报成功（不变量 6 的反面：跑完了不许说成成功，跑失败了也不许）——
                 * 结论落成失败，产物留在暂存区供用户自己取。
                 */
                string failureMessage = "定稿搬运失败，产物没能写进输出目录：" + commit.Message;

                logEntries.Add(("ERROR", $"{task.FileName}：{failureMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    Commit = commit,
                    CommitFailed = true,
                    CommitFailureMessage = failureMessage
                };
            }

            // 归集是**移动**产物，动手之前再查一次令牌（取消就不再移动）。
            cancellationToken.ThrowIfCancellationRequested();

            // 3) 归集（可选）
            CollectResult? collected = null;

            /*
             * 续解出来的内层包**不参与归集**：它的落点就是父任务那一个最终目录，
             * 而归集会把那个目录整个搬走 —— 第二轮再搬一次，内容物就会被搬进 "名字(1)" 里，
             * 用户看到的还是"多了一个文件夹"。归集只由最外层源包做一次。
             */
            if (collectResults && task.IsContinuationTask)
            {
                logEntries.Add(("INFO", $"{task.FileName}：内层包，产物与父任务同一个目录，跳过结果归集。"));
            }
            else if (collectResults)
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

            /*
             * 4) 清理源包（默认关闭；未通过校验时服务内部会拒绝执行）。
             *
             * 只对**最外层源包**做：续解出来的内层包，它的"源文件"是我们自己产出的中间件
             * （现在就在 <c>过程物</c> 里），既不是用户给的包，也不该被这条开关删掉 ——
             * 用户按下"解压后删除源包"时想删的是他拖进来的那个包，不是过程物。
             */
            if (task.IsContinuationTask)
            {
                if (deleteSource)
                {
                    logEntries.Add(("INFO", $"{task.FileName}：内层包，源文件属于过程物，已跳过清理源包。"));
                }
            }
            else
            {
                SourceCleanupResult cleanup = new SourceCleanupService().Cleanup(
                    task,
                    verification,
                    deleteSource);

                if (cleanup.Attempted)
                {
                    logEntries.Add((cleanup.FailedFiles.Count == 0 ? "INFO" : "WARN", $"{task.FileName}：清理源包 —— {cleanup.Message}"));
                }
            }

            return new PostProcessWorkResult
            {
                Verification = verification,
                LogEntries = logEntries,
                Collected = collected,
                Commit = commit
            };
        }

        /// <summary>
        /// 抠出来的内嵌归档（双面文件尾部那一段）在定稿后是否搬进 <c>过程物</c>。
        ///
        /// 契约 §3.2 把"抠出来的中间 ZIP"列为过程物，本来是**该搬**的 —— 但实测下来不能搬，
        /// 因为它不是"解出来的产物"，而是**解压的输入**（用户那个源文件的一段副本）：
        ///
        /// · 双面文件 <c>user.mp4</c> 的 ZIP 里就是内层分卷 <c>inner.7z.001/.002</c>；
        ///   把它搬进 <c>过程物\user.zip</c> 之后，一键处理的续解扫描（它只看"本轮新出现的归档起点"）
        ///   会把这个 ZIP 也当成一个**新的内层包**，于是又解一遍、又产出 <c>inner.7z.001</c>、
        ///   再多续解一轮 —— 实测就是这个结果（回归测试"双面文件真实现场"从 2 轮变成 3 轮）。
        /// · 而且它往往很大（双面文件能到 760MB 量级），搬进用户目录等于凭空多一份大副本。
        ///
        /// 所以默认 **false**：它留在工作区（<c>&lt;work&gt;\&lt;taskId&gt;\</c>），
        /// 成功且校验通过后随工作区一起清掉，失败/取消时留着供排查。
        /// 如果将来判定表/ResultFinalizer 能把它标注成"输入而非产物"（续解扫描据此跳过），
        /// 把这里改成 true 即可，其余代码不用动。
        /// </summary>
        private const bool MoveCarvedArtifactIntoProcessDirectory = false;

        /// <summary>
        /// 定稿布局规划：把暂存产物树描述成 <see cref="StagedEntry"/>，交给
        /// <see cref="ResultFinalizer.Plan"/>（契约 §3.1 判定表的**唯一实现处**），
        /// 再把它的话翻译成本类执行阶段用的形状。
        ///
        /// 分工要说清楚：
        /// · 本方法负责"**哪些是过程物**" —— 这一步只有跑过暂存阶段的人知道。
        ///   判据用后缀（分卷段 + 归档本体）：解压出来的归档就是**待续解的内层包**，
        ///   按流水线语义它属于过程物（一键处理下一轮会去解它，成功的话它的内容物自会落进内容物一层）；
        ///   判定失败/到轮数上限时它留在 <c>过程物</c> 里可回收，比混在内容物里强。
        ///   （ResultFinalizer 的注释建议"由调用方显式标"，这里就是那个调用方。）
        /// · <see cref="ResultFinalizer"/> 负责"摆成什么样" —— 终端单文件直接放、多文件套一层、
        ///   多重空目录提上来、单链塌缩，以及 D-2 的 <c>&lt;源目录&gt;\过程物\&lt;包基名&gt;\</c>。
        ///
        /// 纯函数：只读目录、只返回计划，一个字节都不动，所以能脱离管线被测。
        /// </summary>
        /// <param name="stageDirectory">暂存目录（入仓阶段的产物树）。</param>
        /// <param name="destinationDirectory">最终目录（落点由 <see cref="PathService.BuildOutputPath"/> 算）。</param>
        /// <param name="placementMode">落点模式；只影响过程物集中到哪（D-2 依赖它）。</param>
        /// <param name="archiveBaseName">
        /// 终端归档基名（"没有最外层文件夹名"时给那一层取名用）。传空则退回 destDir 自己的末段名。
        /// </param>
        /// <param name="terminalLayout">
        /// 终端落法（规格 §3.1 的可选项，来自设置项 <see cref="AppSettings.TerminalLayoutMode"/>）。
        ///
        /// ⚠ 这里**必须由调用方传**：本方法以前把 <see cref="TerminalLayoutMode.KeepLastFolder"/> 写死，
        /// 于是用户在设置里选"用压缩包名当最后一层"完全不生效 —— 界面上的选项与真实行为不一致，
        /// 是本项目明令禁止的那一类（"改了没用"的开关）。
        /// </param>
        internal static FinalLayoutPlan PlanFinalLayout(
            string stageDirectory,
            string destinationDirectory,
            OutputPlacementMode placementMode,
            string? archiveBaseName,
            TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder)
        {
            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new FinalLayoutPlan();
            }

            string stageRoot = SafePathHelper.GetFullPathSafe(stageDirectory);
            var staged = new List<StagedEntry>();

            foreach (string file in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
            {
                long size = 0;

                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // 量不出大小只影响"过程物总共多大"这个数字，不影响布局。
                }

                staged.Add(new StagedEntry
                {
                    RelativePath = Path.GetRelativePath(stageRoot, file),
                    Size = size,
                    IsProcessArtifact = IsProcessArtifactFile(file)
                });
            }

            // 目录条目也要给：判定表要认"自带一层文件夹""多重空目录嵌套""单链"这些形态，
            // 光有文件路径推不出"这一层是不是空的"。
            foreach (string directory in Directory.EnumerateDirectories(stageRoot, "*", SearchOption.AllDirectories))
            {
                staged.Add(new StagedEntry
                {
                    RelativePath = Path.GetRelativePath(stageRoot, directory),
                    IsDirectory = true
                });
            }

            if (staged.Count == 0)
            {
                return new FinalLayoutPlan();
            }

            FinalizePlan finalize = ResultFinalizer.Plan(
                staged,
                destinationDirectory,
                terminalLayout,
                archiveBaseName,
                contentRoot: null,
                stagingRoot: stageRoot,
                placementMode: placementMode);

            var processSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PlannedMove move in finalize.ProcessArtifactMoves)
            {
                processSources.Add(move.From);
            }

            return new FinalLayoutPlan
            {
                Failed = finalize.Layout == FinalizeLayoutKind.Failed,
                FailureReason = finalize.FailureReason,
                Layout = finalize.Layout,
                Moves = finalize.Moves,
                PlannedContentCount = finalize.ContentMoves.Count,
                ProcessArtifactSources = processSources,
                ProcessArtifactDirectory = finalize.ProcessArtifactDirectory,
                ProcessArtifactTotalSize = finalize.ProcessArtifactTotalSize,
                ContentFileCount = finalize.ContentFileCount,
                Summary = finalize.Summary,
                Warnings = finalize.Warnings
            };
        }

        /// <summary>
        /// 这条路是不是"过程物"（契约 §3.2）：分卷段（<c>.001</c>/<c>.z01</c>/<c>.r00</c>/<c>.part1</c>）
        /// 与归档本体（<c>.7z</c>/<c>.zip</c>/<c>.rar</c>/…）。抠出来的中间 ZIP 也落在这一条里。
        /// </summary>
        internal static bool IsProcessArtifactFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return ExtensionHelper.IsVolumePartExtension(extension) ||
                   ExtensionHelper.IsKnownArchiveExtension(extension);
        }

        /// <summary>
        /// 执行定稿：按 <see cref="PlanFinalLayout"/> 的计划把暂存产物搬进最终目录。
        ///
        /// **只允许在后台线程上跑**（几千个 File.Move、跨盘还是拷贝）。
        /// 三条硬要求：
        /// ① 取消时把已经搬过去的**搬回来**（最终目录不许留半成品，契约 §6 第 2 条）；
        /// ② 同名绝不覆盖（AutoRename 改名，记进 <see cref="StageCommitResult.RenamedCount"/>）；
        /// ③ 单个文件搬不动只记一笔，不拖垮其余文件（与 <see cref="ExtractionWorkspace"/> 的搬运口径一致）。
        /// </summary>
        private StageCommitResult ExecuteFinalLayout(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            OutputPlacementMode placementMode,
            TerminalLayoutMode terminalLayout,
            CancellationToken cancellationToken)
        {
            var logEntries = new List<(string Level, string Message)>();

            // 给"没有最外层文件夹名"时那一层取名用（判定表 2）。取的是**终端归档**的基名：
            // 续解出来的内层包，它的 CurrentPath 就是最后解的那一卷，基名正是用户认得出的那个名字。
            string archiveBaseName = OutputPlacement.ResolveArchiveBaseName(task.CurrentPath);

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new StageCommitResult { Attempted = false };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new StageCommitResult
                {
                    Attempted = true,
                    Cancelled = true,
                    Message = "定稿开始前已取消"
                };
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayout(
                    stageDirectory,
                    destinationDirectory,
                    placementMode,
                    archiveBaseName,
                    terminalLayout);
            }
            catch (Exception ex)
            {
                string readFailure = $"读取暂存目录失败：{ex.Message}";

                logEntries.Add(("ERROR", $"{task.FileName}：{readFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { readFailure },
                    Message = readFailure,
                    LogEntries = logEntries
                };
            }

            if (plan.Failed)
            {
                string planFailure = "定稿布局规划失败：" + plan.FailureReason;

                logEntries.Add(("ERROR", $"{task.FileName}：{planFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { planFailure },
                    Message = planFailure,
                    LogEntries = logEntries
                };
            }

            foreach (string warning in plan.Warnings)
            {
                logEntries.Add(("WARN", $"{task.FileName}：定稿提醒 —— {warning}"));
            }

            if (plan.Moves.Count == 0)
            {
                // 引擎什么都没写出来（空包）：没有可定稿的东西，也不该凭空造一个空目录给用户。
                logEntries.Add(("WARN", $"{task.FileName}：暂存目录里没有产物，没有需要定稿的内容。"));

                return new StageCommitResult
                {
                    Attempted = true,
                    Message = "暂存目录里没有产物",
                    LogEntries = logEntries
                };
            }

            bool destinationExisted = Directory.Exists(destinationDirectory);

            if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
            {
                string createFailure = $"最终目录不存在且创建失败：{destinationDirectory}";

                logEntries.Add(("ERROR", $"{task.FileName}：{createFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = plan.Moves.Count,
                    Failures = new[] { createFailure },
                    Message = createFailure,
                    LogEntries = logEntries
                };
            }

            var moved = new List<(string From, string To)>();
            var failures = new List<string>();

            int movedContent = 0;
            int movedProcess = 0;
            int renamed = 0;

            // 计划里 **内容物在前、过程物在后**：先让用户要的东西落位，再收拾中间件。
            foreach (PlannedMove move in plan.Moves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    RollBackFinalLayout(moved, destinationDirectory, destinationExisted);

                    logEntries.Add(("WARN", $"{task.FileName}：定稿被取消，已把 {moved.Count} 项退回暂存目录。"));

                    return new StageCommitResult
                    {
                        Attempted = true,
                        Cancelled = true,
                        Message = "定稿被取消，产物已退回暂存目录",
                        LogEntries = logEntries
                    };
                }

                bool isProcessArtifact = plan.ProcessArtifactSources.Contains(move.From);

                try
                {
                    // 计划里的一条可能是**文件**，也可能是**整个目录**（判定表 2/3/4 会整棵搬）。
                    bool isDirectory = Directory.Exists(move.From) && !File.Exists(move.From);

                    if (!SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(move.To)))
                    {
                        failures.Add($"{Path.GetFileName(move.To)}（无法创建目标目录）");
                        continue;
                    }

                    string target = move.To;

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        // 目标同名：换成 名字(1)，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                        string renamedTarget = isDirectory
                            ? SafePathHelper.AutoRenameDirectoryPath(target)
                            : SafePathHelper.AutoRenameFilePath(target);

                        if (!string.Equals(renamedTarget, target, StringComparison.OrdinalIgnoreCase))
                        {
                            renamed++;
                        }

                        target = renamedTarget;
                    }

                    if (isDirectory)
                    {
                        Directory.Move(move.From, target);
                    }
                    else
                    {
                        File.Move(move.From, target);
                    }

                    moved.Add((move.From, target));

                    if (isProcessArtifact)
                    {
                        movedProcess++;
                    }
                    else
                    {
                        movedContent++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(move.From)}（{ex.Message}）");
                }
            }

            /*
             * 抠出来的内嵌归档：它是从源文件按偏移再生出来的派生数据，契约 §3.2 把它列为过程物。
             * 它**不在暂存区里**（在任务工作区根下，是解压的输入），所以不在上面的计划里，单独搬一次。
             * 为什么默认不搬：见 MoveCarvedArtifactIntoProcessDirectory 的说明。
             */
            if (MoveCarvedArtifactIntoProcessDirectory && task.EmbeddedArchiveOffset > 0)
            {
                string carvedPath = BuildEmbeddedArchivePath(task);

                if (File.Exists(carvedPath))
                {
                    try
                    {
                        string processRoot = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                            ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                            : plan.ProcessArtifactDirectory;

                        string carvedTarget = Path.Combine(processRoot, Path.GetFileName(carvedPath));

                        SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(carvedTarget));

                        if (File.Exists(carvedTarget))
                        {
                            carvedTarget = SafePathHelper.AutoRenameFilePath(carvedTarget);
                            renamed++;
                        }

                        File.Move(carvedPath, carvedTarget);

                        movedProcess++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(carvedPath)}（抠出的中间归档：{ex.Message}）");
                    }
                }
            }

            string processDirectory = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                : plan.ProcessArtifactDirectory;

            string summary =
                $"内容物 {plan.ContentFileCount} 个文件 → {destinationDirectory}" +
                (movedProcess > 0
                    ? $"；过程物 {movedProcess} 项（{plan.ProcessArtifactTotalSize} 字节）→ {processDirectory}"
                    : string.Empty) +
                (renamed > 0 ? $"；{renamed} 项同名，已改名未覆盖" : string.Empty) +
                (failures.Count > 0 ? $"；{failures.Count} 项没能搬运" : string.Empty);

            logEntries.Add((failures.Count == 0 ? "INFO" : "WARN", $"{task.FileName}：定稿完成 —— {summary}"));

            return new StageCommitResult
            {
                Attempted = true,
                PlannedContentCount = plan.PlannedContentCount,
                MovedContentCount = movedContent,
                MovedProcessCount = movedProcess,
                ContentFileCount = plan.ContentFileCount,
                ProcessArtifactBytes = plan.ProcessArtifactTotalSize,
                RenamedCount = renamed,
                FailedCount = failures.Count,
                Failures = failures,
                Message = summary,
                LogEntries = logEntries
            };
        }

        /// <summary>
        /// 定稿的回滚：把已经搬过去的条目按**相反顺序**搬回暂存区原路径。
        ///
        /// 为什么必须回滚（契约 §6 第 2 条"定稿前 destDir 不得出现半成品"）：
        /// 用户按下「取消当前」时，搬运可能已经做了一半 —— 只把最终目录里那一半留下，
        /// 用户看到的就是一个"解压到一半"的目录，正是这次改造要根治的观感问题。
        /// 回滚失败（文件被别的程序锁住等）只吞掉：那是更小的问题，而且日志里已经有取消记录。
        ///
        /// 条目可能是**整个目录**（判定表 2/3/4 会整棵搬），所以这里也要按类型分开搬回去。
        /// </summary>
        private static void RollBackFinalLayout(
            List<(string From, string To)> moved,
            string destinationDirectory,
            bool destinationExisted)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(moved[i].From));

                    if (Directory.Exists(moved[i].To) && !File.Exists(moved[i].To))
                    {
                        Directory.Move(moved[i].To, moved[i].From);
                    }
                    else
                    {
                        File.Move(moved[i].To, moved[i].From);
                    }
                }
                catch
                {
                    // 回滚不掉的单个条目不影响"已经尽量退回"这个结论。
                }
            }

            if (destinationExisted)
            {
                // 目录本来就在（续解内层包时它装着上一轮的内容物），绝不能删。
                return;
            }

            TryDeleteEmptyDirectoryTree(destinationDirectory);
        }

        /// <summary>由深到浅删掉空目录（只删空的；非空一律不碰）。定稿回滚后收拾自己造出来的目录壳。</summary>
        private static void TryDeleteEmptyDirectoryTree(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                string[] directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);

                foreach (string directory in directories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(directory).Length == 0)
                        {
                            Directory.Delete(directory, recursive: false);
                        }
                    }
                    catch
                    {
                        // 删不掉就留着，不影响结论。
                    }
                }

                if (Directory.GetFileSystemEntries(root).Length == 0)
                {
                    Directory.Delete(root, recursive: false);
                }
            }
            catch
            {
                // 见方法注释：收拾目录壳是尽力而为。
            }
        }

        /// <summary>
        /// 给递归层提供密码候选（只给值，不给来源说明）。
        /// 顺序与单层解压完全一致 —— 递归的内层包同样是"用户的包"，不该用另一套规则。
        ///
        /// 这里就截到每层上限（同 <see cref="MaxPasswordAttemptsPerLayer"/> 这个设置项）：
        /// 递归里每个候选同样是一次完整解压尝试，让几百个候选排着队进去
        /// 等于把"一键处理"变成没人看得懂的长时间等待（AGENTS.md §9.2）。
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
        /// <param name="stageDirectory">
        /// 暂存目录（入仓阶段）。递归核心把叶子层产物发布到**这里**，不直接写最终目录 ——
        /// 定稿那一步才把它们搬进 <see cref="ArchiveTask.OutputPath"/>。
        /// </param>
        private async Task RunRecursiveAsync(
            ArchiveTask task,
            string engineArchivePath,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            RecursionMode mode = string.Equals(Settings.RecursionMode, "AllBranches", StringComparison.OrdinalIgnoreCase)
                ? RecursionMode.AllBranches
                : RecursionMode.SingleChain;

            // 上限（层数 / 每层密码尝试次数）当场从设置里取，并交给**本次**的递归核心：
            // 构造时固定一份的话，用户改完设置不重启就不生效（见字段上的说明）。
            RecursionLimits limits = BuildRecursionLimits();
            RecursiveExtractor recursiveExtractor = CreateRecursiveExtractor();

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

            RecursionResult result = await recursiveExtractor.ExtractAsync(
                recursionTask, stageDirectory, mode, previousDecision: null, cancellationToken);

            if (result.StopReason == RecursionStopReason.NeedsDecision && result.Decision != null)
            {
                bool expandAll = await ShowConfirmOnUiThreadAsync(
                    result.Decision.Prompt + Environment.NewLine + Environment.NewLine +
                    "选“确定”：把这些内层归档也解开。选“取消”：只保留当前这一层的结果。");

                if (expandAll)
                {
                    // 同一个实例接着跑（上限不变）：续跑用的是用户刚确认的那批候选，不是重新扫一遍。
                    result = await recursiveExtractor.ExtractAsync(
                        recursionTask, stageDirectory, mode, result.Decision, cancellationToken);
                }
                else
                {
                    /*
                     * 用户只想保留当前这一层。
                     *
                     * 这里**不能**就这样返回 NeedsDecision：那会让用户以为"东西已经解到输出目录了"，
                     * 而按不变量 12，需要决定时产物还留在工作区里 —— 文案与实现不符等于骗人。
                     *
                     * 做法：把已经解好的第 0 层产物从递归工作区搬进**暂存目录**（它照样是入仓阶段的一部分），
                     * 并把它当成 Completed 继续走"校验 → 定稿 → 归集 → 清理"。
                     * 不重新解压一遍：大包重解代价太大，而产物本来就在工作区里。
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
                            () => MoveDirectoryContent(layerZeroOutput, stageDirectory),
                            cancellationToken);

                        result = new RecursionResult
                        {
                            StopReason = RecursionStopReason.Completed,
                            Completed = true,
                            PartiallyCompleted = false,
                            Layers = result.Layers,
                            FinalOutputPath = stageDirectory,
                            Summary = $"按你的选择只解开了当前这一层，已取回 {moved} 个文件（定稿后进输出目录）。"
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

            /*
             * task.OutputPath **不跟着递归结论走**。
             *
             * 它必须始终指向**最终目录**（一键处理的续解、界面"输出目录"列、清理源包都读它），
             * 而现在递归的产物先落在**暂存目录**里（调用方传的就是 stage），要等定稿那一步才搬出去。
             * 旧写法 `task.OutputPath = result.FinalOutputPath` 在这里会把任务指向暂存区：
             * 用户点"打开输出目录"会看到工作区里的中间件，续解也会去暂存区里找内层包。
             */
            if (!result.Completed)
            {
                // 没走完（上限 / 密码 / 需要决定）：产物留在暂存区，如实说清它在哪。
                AppendLog(
                    "WARN",
                    $"{task.FileName}：这次没有走完，产物仍在暂存目录，未搬进输出目录：{result.FinalOutputPath}");
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

            /*
             * 落点模式（契约 §1.1 的四种，由旧的三个设置项翻译过来）。
             * 定稿时要靠它决定过程物集中到哪：模式 B（解压到当前目录）下目标目录就是源目录本身，
             * 一个目录里几十上百个包会共用它，所以过程物再套一层包基名（决策 D-2，由 ResultFinalizer 实现）。
             */
            OutputPlacementMode placementMode = OutputPlacement.FromLegacyFlags(
                extractOptions.ExtractToOriginalDirectory,
                extractOptions.KeepArchiveNameFolder,
                extractOptions.CustomOutputDirectory);

            /*
             * 场景 B 塌缩（规格 §3.3）：111\222\名字\名字.rar → 产物落 111\222\名字\内容物。
             *
             * 「包基名 == 所在目录名」是纯字符串判断（不碰磁盘）；成立之后才需要**扫一次目录**，
             * 确认里面没有别的包。扫描是磁盘活：用户场景里一个目录可能有几百个条目、网络盘更慢，
             * 所以必须在后台线程上跑（AGENTS.md / 任务硬约束：UI 线程只更新状态）。
             *
             * 只对**最外层源包**做：续解出来的内层包落点由父任务给定（见 BuildOutputPath 的早退分支），
             * 这层规则对它没有意义。
             */
            bool collapseRepeatedFolderLayer = Settings.CollapseRepeatedFolderLayer;
            bool sourceDirectoryContainsOnlyThisArchive = false;

            if (collapseRepeatedFolderLayer
                && !task.IsContinuationTask
                && SourceFolderScanService.IsRepeatedFolderNameCandidate(task.CurrentPath))
            {
                SourceFolderScanResult scan = await Task.Run(
                    () => SourceFolderScanService.Inspect(task.CurrentPath),
                    cancellationToken);

                sourceDirectoryContainsOnlyThisArchive = scan.ContainsOnlyThisArchive;

                // 塌缩会少一层目录，这件事必须让用户看得见（日志里说清为什么）。
                AppendLog(scan.ContainsOnlyThisArchive ? "INFO" : "WARN", $"{task.FileName}：{scan.Message}");
            }

            // 解压前算出来的"打算输出到哪"。它只是一个提议：下面可能因为目录已存在被改名。
            // 续解出来的内层包由 PathService.BuildOutputPath 直接给出**父任务那一个**最终目录（不再套一层）。
            string requestedOutputPath = _pathService.BuildOutputPath(
                task,
                extractOptions,
                collapseRepeatedFolderLayer,
                sourceDirectoryContainsOnlyThisArchive);
            string outputPath = requestedOutputPath;
            string outputRedirectNote = string.Empty;

            /*
             * 落点算不出来时必须**当场停下**（必修项，2026-09-21）。
             *
             * 旧实现会把空的"自定义输出目录"回落成程序安装目录，于是用户的文件被解进 exe 所在的那个目录 ——
             * 既找不到、又可能污染程序自己的 data。现在 BuildOutputPath 返回空串，
             * 这里给出明确状态与一句话原因，让用户去设置里选一个输出目录。
             */
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = "输出目录无效（未设置或不可用），请在设置里选择输出目录后重试";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                return;
            }

            /*
             * 落点是不是"源包自己的目录"（模式 B「解压到压缩包所在目录」/ 场景 B 塌缩后的
             * 111\222\名字）。这条事实决定"目录已存在且非空就改名 xxx(1)"要不要让开，
             * 理由见下面那个分支上的说明；判定本身只有一处实现（OutputPlacement）。
             */
            bool landsInSourceDirectory = OutputPlacement.LandsInSourceDirectory(task.CurrentPath, outputPath);

            try
            {
                /*
                 * 「输出目录已存在且非空 → 自动改名成 xxx(1)」只对**最外层源包**做。
                 *
                 * 为什么续解出来的内层包必须跳过这一步：它的落点是父任务**正在用**的那个目录
                 * （内容物 + 过程物都在里面），对它改名等于把第二层解到旁边的 xxx(1) 去 ——
                 * 那正是用户抱怨的"多弄了四个文件夹、文件一多根本分不清"。
                 * 同名冲突也不会因此丢东西：内层产物进目录时由定稿搬运的 AutoRename 兜住（绝不覆盖）。
                 */
                if (task.IsContinuationTask)
                {
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：内层包，产物归入父任务输出目录 {outputPath}（不再另建目录）。");
                }
                else if (landsInSourceDirectory)
                {
                    /*
                     * 落点就是**源包所在目录**（模式 B「解压到压缩包所在目录」，或场景 B 塌缩后的
                     * 111\222\名字）→ 上面那条"已存在且非空就改名 xxx(1)"必须让开。
                     *
                     * 为什么：那个目录**必然非空** —— 源包自己就躺在里面。照旧规则一改名，
                     * 用户选的就地整理 / 塌缩当场被抵消，产物落到旁边的 名字(1)\，
                     * 正是这一轮要根治的"凭空多一层目录"。
                     *
                     * 让开之后会不会和既有文件混在一起：不会丢东西。定稿搬运对同名条目一律
                     * AutoRename（绝不覆盖），而且这条规则的本意是"别把产物倒进一个已有内容的目录"，
                     * 可这里那个目录**本来就是这次要整理的目标**。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：落点就是源包所在目录 {outputPath}（就地整理 / 已塌缩重复层），不套用“目录已存在就改名”的规则。");
                }
                else if (!string.IsNullOrWhiteSpace(outputPath) &&
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
             *
             * ⚠ 它**始终指向最终目录**，不指向暂存目录：暂存区只是过程，
             * 续解逻辑（OneClickCoordinator）读的必须是"内容物最终在哪"。
             */
            task.OutputPath = outputPath;

            // 每个任务都明确说一次实际落点：目录被改名时用户必须能立刻看出产物去了哪。
            AppendLog("INFO", $"{task.FileName}：本次实际输出目录 {outputPath}");

            /*
             * ===== 阶段一：入仓（stage，契约 §2.1）=====
             *
             * 从这一刻起，本任务**所有**中间动作（抠内嵌归档之后的解压、第一层、内层分卷、递归）
             * 都在私有暂存目录 <c>&lt;work&gt;\&lt;taskId&gt;\stage</c> 里完成，
             * 源目录与最终目录一个字节都不会被写。
             *
             * 这条就是用户那句"分卷文件你居然又解压到外面来了"的根治点：
             * 旧实现把引擎的输出目录直接设成 destDir，内层分卷于是和内容物一起躺在用户目录里；
             * 一旦失败，用户目录里还留着半成品。
             */
            string stageDirectory = _pathService.BuildTaskStageDirectory(task);

            if (string.IsNullOrWhiteSpace(stageDirectory))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = "无法确定暂存目录（工作区不可用）";
                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                return;
            }

            string? stageFailure = await PrepareStageDirectoryAsync(task, stageDirectory, cancellationToken);

            if (!string.IsNullOrWhiteSpace(stageFailure))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = stageFailure;
                AppendLog("ERROR", $"{task.FileName}：{stageFailure}");
                return;
            }

            // 引擎写盘的地方 = 暂存目录。**不是** outputPath —— 那是定稿的目标。
            string engineOutputPath = stageDirectory;

            AppendLog("INFO", $"{task.FileName}：入仓目录 {stageDirectory}（中间产物先落这里，定稿时才搬进 {outputPath}）");

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
                 * 临时空间预检：抠出来的中间文件落在工作区（<c>&lt;程序目录&gt;\data\work</c>，即程序所在的那个盘）。
                 * 一个 780MB 的双面文件要在那里占掉 760MB —— 空间不够会写到一半失败，
                 * 还会把那个盘挤满。取不到空间就不拦（宁可试也不误拒），取到了才判。
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
            // 上限读一次就定住：设置是用户随时可改的，同一单任务里不许"前半段按一个上限、后半段按另一个"。
            int attemptLimit = MaxPasswordAttemptsPerLayer;
            int maxPasswordAttempts = Math.Min(candidates.Count, attemptLimit);
            bool candidatesTruncated = candidates.Count > maxPasswordAttempts;

            if (candidatesTruncated)
            {
                AppendLog(
                    "WARN",
                    $"{task.FileName}：密码候选共 {candidates.Count} 个，超过单层上限 {attemptLimit} 个，" +
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

                // 预算里的"落点"用暂存目录：引擎真正写盘的地方是它，最终目录此时还不存在。
                BudgetCheckResult budget = new ResourceBudget().CheckBeforeExtract(preflightList, archiveSize, engineOutputPath);

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
             * 放在预检之后：预检已经把暂存目录算好，而且非归档 / 格式未知在前面已经分流走了。
             *
             * 落点传的是**暂存目录**：递归核心本来就在自己的工作区里逐层解、最后才 Publish，
             * 让它直接 Publish 到最终目录等于跳过了"入仓 → 定稿"这道门（失败时还会在用户目录里留下半成品）。
             * 现在它发布进暂存区，再由下面的定稿一次性搬进最终目录 —— 与单层路径同一口径。
             */
            if (!string.Equals(Settings.RecursionMode, "SingleLayer", StringComparison.OrdinalIgnoreCase))
            {
                await RunRecursiveAsync(task, engineArchivePath, engineOutputPath, cancellationToken);

                if (task.Status == StatusText.ExtractSuccess)
                {
                    // 递归产物同样要走"校验 → 定稿 → 归集 → 可选清理"，与单层路径一个字都不差。
                    bool recursionConclusionStands = await PostProcessSuccessAsync(
                        task, engineArchivePath, string.Empty, engineOutputPath, outputRedirectNote, placementMode, cancellationToken);

                    if (recursionConclusionStands)
                    {
                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                    }
                }

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
         OutputPath = engineOutputPath,
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

                    // 收尾可能把"解压成功"顶掉（产物越界 / 超预算 / 定稿失败）：只有结论仍然成立时才敢这么写日志。
                    bool conclusionStands = await PostProcessSuccessAsync(
                        task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, placementMode, cancellationToken);

                    if (conclusionStands)
                    {
                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                    }
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
         OutputPath = engineOutputPath,
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

                        // 同"先测试再解压"那条分支：收尾否掉结论（越界 / 超预算 / 定稿失败）时不许写"解压成功"。
                        bool conclusionStands = await PostProcessSuccessAsync(
                            task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, placementMode, cancellationToken);

                        if (conclusionStands)
                        {
                            AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                        }

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

            /*
             * 进度文案只在"这一单没有失败 / 取消结论"时才写"完成"。
             * 越界、超预算、递归部分完成这些分支自己写了 ProgressFailed，取消写的是"已取消"，
             * 无条件覆盖会让任务详情窗口出现"状态：解压失败 / 进度：完成"这种自相矛盾的显示。
             */
            if (task.ProgressText != StatusText.ProgressFailed &&
                task.ProgressText != StatusText.ProgressCancelled)
            {
                task.ProgressText = StatusText.ProgressCompleted;
            }

            task.LastUpdatedTime = DateTime.Now;
        }

        /// <summary>
        /// 准备本任务的暂存目录（入仓的第一步）：建出来，并把**上一次留下的残留**清掉。
        ///
        /// 为什么要清残留：暂存目录的 taskId 带源路径哈希，所以同一个包重试时落回同一个目录。
        /// 上一次失败/取消留下的产物如果不清，这一次的定稿会把它们一起当成"本次产物"搬进最终目录 ——
        /// 用户会看到上一轮的垃圾文件混进结果里（比"多几个文件夹"更难查）。
        ///
        /// 为什么必须在后台线程：<see cref="Directory.Delete(string, bool)"/> 删的是几十万个文件的树时
        /// 是秒级到分钟级的同步磁盘活，放在 UI 线程上就是"点了没反应"。
        ///
        /// 返回 null = 准备好了；返回非空字符串 = 失败原因（调用方落成失败状态并把原因写给用户）。
        /// </summary>
        private async Task<string?> PrepareStageDirectoryAsync(
            ArchiveTask task,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (Directory.Exists(stageDirectory))
                        {
                            bool hasLeftover = false;

                            try
                            {
                                hasLeftover = Directory.EnumerateFileSystemEntries(stageDirectory).Any();
                            }
                            catch
                            {
                                hasLeftover = true;
                            }

                            if (hasLeftover)
                            {
                                // 上一轮的残留：先清掉再解。清不掉就如实失败，绝不"接着往里解"。
                                Directory.Delete(stageDirectory, recursive: true);
                            }
                        }

                        return SafePathHelper.EnsureDirectoryExists(stageDirectory)
                            ? null
                            : $"暂存目录不存在且创建失败：{stageDirectory}";
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"准备暂存目录失败（{ex.Message}）：{stageDirectory}";
            }
        }

        /// <summary>
        /// 内嵌归档抠出来之后落在哪：<c>&lt;work&gt;\&lt;taskId&gt;\&lt;包基名&gt;.zip</c>。
        ///
        /// 三条刻意的选择：
        /// ① 放工作区（<see cref="PathService.WorkDirectory"/>）而不是源目录旁边 ——
        ///    中间产物不得写进源目录（AGENTS.md §6 第 12 条）。抠出来的这一段只是为了能解压，
        ///    不是用户要的东西；定稿时它会被归入 <c>过程物</c>（契约 §3.2），取消/失败时留在工作区；
        /// ② 文件名沿用**源文件的包基名**，不改成随机临时名 ——
        ///    密码本"名称:密码"的映射匹配用的就是包基名，随机名会让本来能命中的密码全部落空，
        ///    用户看到的现象会是"同一个包以前能解开，现在说密码错误"；
        /// ③ 它是**暂存目录的兄弟**（不在 stage 里面）：stage 里的东西会被整体定稿搬走，
        ///    而它是解压的输入，不该被自己搬走。
        /// </summary>
        private string BuildEmbeddedArchivePath(ArchiveTask task)
        {
            string baseName = FileNameHelper.SanitizeFileName(FileNameHelper.GetArchiveBaseName(task.CurrentPath));

            return Path.Combine(_pathService.BuildTaskWorkDirectory(task), baseName + ".zip");
        }

        /// <summary>
        /// 清理**本任务自己**的中间工作区目录：<c>&lt;work&gt;\&lt;taskId&gt;\</c>。
        ///
        /// 为什么必须有（端到端验收实测）：双面文件要先按偏移把它尾部那段真正的 ZIP 抠进工作区再解压，
        /// 一次**成功**的一键处理就在那个目录里留下近 1 GB 中间件（实测 733 MB + 167 MB 两个包）。
        /// 这些是从源文件按偏移可再生的派生数据，成功后留着纯属垃圾：跑几批就把盘吃掉，
        /// 而且 MainViewModel 启动时会把工作区根下的每个子目录都报成"未完成的工作区"，留着还会造成假警报。
        ///
        /// 入仓（stage）之后这一条覆盖的范围变大了：暂存目录也在这个工作区里，
        /// 所以**每个任务**（不只是抠过内嵌归档的）成功后都要清 —— 条件反而更简单：
        /// 定稿把内容物搬走之后，这里剩下的全是可再生的中间件。
        ///
        /// 只在这三件事同时成立时才删（与 AGENTS.md §9.5 的清理语义对齐）：
        /// ① 解压成功；② 输出校验通过；③ 没有被取消、没有越界结论、没有超预算、没有定稿失败。
        /// 取消 / 部分完成 / 校验失败 / 越界一律不删 —— 产物可能没落全，工作区里的中间件是用户唯一的线索。
        ///
        /// 安全边界（这是"删目录"，每一条都要有）：
        /// · 目录按 <see cref="PathService.BuildTaskWorkDirectory"/> 的同一套布局算出，
        ///   再规范化确认它确实在工作区根**之下**（容器内校验，越界就什么都不删）；
        /// · 目录里只允许出现 <c>stage</c> 这一个子目录（我们自己造的）与本任务的中间件文件；
        ///   出现别的子目录说明这不是我们造的那个目录（最典型：任务名撞上了递归工作区的 <c>recursive</c>），
        ///   为安全起见一个字节都不碰；
        /// · 删失败（被占用 / 权限不足）只写日志，绝不让已经成功的任务变成失败。
        /// </summary>
        private void CleanupTaskWorkspaceDirectory(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            string workRoot = _pathService.WorkDirectory;
            string taskDirectory = _pathService.BuildTaskWorkDirectory(task);

            if (string.IsNullOrWhiteSpace(taskDirectory))
            {
                return;
            }

            if (!ArchivePathGuard.IsInsideRoot(workRoot, taskDirectory, out string reason))
            {
                AppendLog("WARN", $"{task.FileName}：中间工作区目录不在工作区根目录之下，已跳过清理 —— {reason}");
                return;
            }

            if (!Directory.Exists(taskDirectory))
            {
                return;
            }

            try
            {
                string[] subdirectories = Directory.GetDirectories(taskDirectory);

                string? foreign = subdirectories.FirstOrDefault(
                    directory => !string.Equals(
                        Path.GetFileName(directory),
                        PathService.StageDirectoryName,
                        StringComparison.OrdinalIgnoreCase));

                if (foreign != null)
                {
                    AppendLog("WARN", $"{task.FileName}：中间工作区目录里有非本任务造的子目录（{foreign}），为安全起见不清理：{taskDirectory}");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.FileName}：读不了中间工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");
                return;
            }

            (int fileCount, long totalSize) = OutputVerifier.Measure(taskDirectory);

            try
            {
                // 删除是**不可逆**的：动手之前先把"删什么、为什么"写进日志（AGENTS.md §9.5 的同一要求）。
                AppendLog(
                    "INFO",
                    $"{task.FileName}：解压成功且输出校验通过，清理本任务的中间工作区（{fileCount} 个文件 / {totalSize} 字节）：{taskDirectory}");

                Directory.Delete(taskDirectory, recursive: true);

                AppendLog("INFO", $"{task.FileName}：中间工作区已清理：{taskDirectory}");
            }
            catch (Exception ex)
            {
                // 删不掉只是"垃圾多留一会儿"，不影响任务结论（同 ExtractionWorkspace.Cleanup 的口径）。
                AppendLog("WARN", $"{task.FileName}：清理中间工作区失败（{ex.Message}），目录保留：{taskDirectory}");
            }
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
