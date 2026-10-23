using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>批前提醒/确认框/并行与输出目录（第 11 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 在 UI 线程上弹一个"是 / 否"确认框（**无 UI 宿主返回 false**，不弹窗口也不死等）。
        ///
        /// 与 <see cref="ShowConfirmOnUiThreadAsync"/> 的区别只有降级行为：那个走
        /// <see cref="DialogService.ShowConfirm(string)"/>，无界面宿主时返回 false 已是保守档，
        /// 但它仍然要求调用方在 UI 上下文里；这里显式调度，后台线程调用也安全。
        /// </summary>
        private Task<bool> DispatcherSafeConfirmAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return Task.FromResult(false);
            }

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(_dialogService.ShowConfirm(message));
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowConfirm(message)).Task;
        }

        /// <summary>
        /// 在 UI 线程上打开"选择文件夹"对话框（**无 UI 宿主返回空串**）。
        ///
        /// 只挑目录、不扫盘：真正的枚举在调用方丢给后台线程做（UI 线程纪律）。
        /// </summary>
        private string ShowFolderBrowserOnUiThread()
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return string.Empty;
            }

            if (dispatcher.CheckAccess())
            {
                return _dialogService.ShowFolderBrowserDialog();
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowFolderBrowserDialog()).Result;
        }

        /// <summary>
        /// 本批允许同时跑几个任务（并发上限）。
        ///
        /// <para>
        /// 「全速」开着时返回本批的任务数（= 不节流）—— 这是"自动等待/节流必须配一个立即继续的出口"
        /// 那条采纳项的落地（<c>docs/WinRAR功能参考.md</c> §2 D 组）。它有意的取舍：
        /// **不管磁盘、不管别的程序**，跑成什么样由用户自己负责；界面上的勾选框文案把这一点写明了。
        /// </para>
        /// <para>
        /// 关着时读设置项 <see cref="AppSettings.MaxParallelExtractCount"/>（1–8，默认 4 ——
        /// 2026-09-22 的并行实测：并发 1→4 是 3.08×，4→8 只再快 1.9% 却把单包耗时拉长一倍）。
        /// 夹一次是兜底：设置对象不一定走过 <c>AppSettings.Normalize</c>（测试里直接 new），
        /// 0 或负数会让"等并发位"的循环条件永远成立 —— 那个任务会永远等下去。
        /// </para>
        /// </summary>
        /// <summary>
        /// 排队等空位**超过这个秒数**才单独写一行（否则只进批末汇总）。
        ///
        /// <para>用户 2026-09-26 第 45 条：原来每个被节流挡住的包都写两行同义反复的话，真机上 76 个任务
        /// 出了 74 条 —— 而绝大多数等待只有一两秒，根本不值得占一行。</para>
        /// </summary>
        private const int ThrottleNoticeThresholdSeconds = 30;

        private int ResolveMaxParallel(IReadOnlyList<ArchiveTask> tasks, out bool fullSpeed)
        {
            fullSpeed = _vm.RunAtFullSpeed;

            if (fullSpeed)
            {
                return Math.Max(1, tasks?.Count ?? 1);
            }

            return Math.Clamp(Settings.MaxParallelExtractCount, 1, MaxParallelExtractCountCeiling);
        }

        /// <summary>
        /// 「空间不足」模式下**本批开几个**（用户 2026-09-27 拍板：忽略设置里的并发档与「全速」）。
        ///
        /// <para>判据全部在 <see cref="ExtractionScheduler.ResolveSpaceTightParallelCount"/> 里
        /// （"很多个体积相近的小包"→5，其余→3；再与"按空间算得出的建议并行数"取小）。
        /// 这里只做两件事：把那个数夹进"不超过任务数"，以及把**为什么是这个数**写进日志 ——
        /// 空间不足模式下用户最需要知道的正是"你凭什么只开 3 个"。</para>
        /// </summary>
        private int ResolveSpaceTightParallelForBatch(ExtractionSchedulePlan plan)
        {
            int count = ExtractionScheduler.ResolveSpaceTightParallelCount(
                plan.Ordered,
                plan.BudgetBytes,
                plan.RecommendedParallelCount,
                out string basis);

            count = Math.Clamp(count, 1, Math.Max(1, plan.Ordered.Count));

            /*
             * ⚠ 用户档只用来**往下压**（用户 2026-09-27 第二次追加："反正空间是弄好了，时间就是速度和卡顿
             * 你想怎么解决"，而这一批在 USB 外置盘上 3 个并发把磁头抢满了、界面也卡）。
             *
             * 口径：模式自己的上限（5 / 3）与「全速」照旧不生效，但**用户的并发档允许更低** ——
             * 慢盘上 1~2 个并发不但不卡，总时间常常还更短（磁头不用来回抢）。
             * ⛔ 只取小、绝不取大：拿用户档把并发**抬高**到模式上限之上是不允许的（那就把这个模式的意义拆掉了）。
             */
            int userCap = Math.Clamp(Settings.MaxParallelExtractCount, 1, MaxParallelExtractCountCeiling);

            if (userCap < count)
            {
                AppendLog(
                    "WARN",
                    $"「空间不足」模式：模式自己算出最多跑 {count} 个，但②页的「最大并发解压数」是 {userCap} —— "
                    + $"按更保守的 {userCap} 个跑（慢盘上并发少反而更快、也更不卡）。");

                count = userCap;
            }

            AppendLog(
                "WARN",
                $"「空间不足」模式：本批同时最多跑 {count} 个（忽略「全速」，不写设置）。{basis}。");

            return count;
        }

        /// <summary>并发上限的硬顶（与设置界面的 1–8 一致；这里只做兜底夹取）。</summary>
        private const int MaxParallelExtractCountCeiling = 8;

        /// <summary>
        /// 本批的"完成后打开输出目录"记账清零（设置项 <see cref="AppSettings.OpenOutputFolderWhenDone"/>）。
        /// </summary>
        private void ResetBatchOutputFolderState()
        {
            lock (_outputFolderLock)
            {
                _outputFolderOpenedThisBatch = false;
            }
        }

        /// <summary>
        /// 试着打开"这一批的"输出目录，**整批只成功打开一次**。
        ///
        /// 只允许在"最外层任务 + 内容物已定稿 + 输出校验通过 + 未取消"的收尾处调用
        /// （判定在 <see cref="PostProcessSuccessAsync"/> 里，那里才知道结论是否成立）。
        /// 内层包（续解出来的）一律不触发：它们与父任务共用同一个目录，反复打开就是刷屏。
        /// </summary>
        /// <returns>真的打开了返回 true；关着开关 / 已经开过 / 目录不存在返回 false。</returns>
        private bool TryOpenOutputFolderOnce(ArchiveTask task)
        {
            if (!Settings.OpenOutputFolderWhenDone)
            {
                return false;
            }

            if (task.IsContinuationTask)
            {
                return false;
            }

            lock (_outputFolderLock)
            {
                if (_outputFolderOpenedThisBatch)
                {
                    return false;
                }

                _outputFolderOpenedThisBatch = true;
            }

            /*
             * ⚠ 顺序有意：先"占位"再打开。
             *
             * 并发下两个任务可能同时走到这里，先占位保证只有一个真的去开资源管理器；
             * 万一打开失败（目录被删了之类），这一批也不会再补开一次 —— 用户看到一句 WARN 就够，
             * 反复尝试开一个不存在的目录只会刷日志。
             *
             * 调用**只打开文件夹**的实现（不置前、不最大化、不抢焦点，见 MainViewModel 的说明）。
             */
            return _vm.OpenCompletedOutputDirectory(task.OutputPath);
        }

        /// <summary>
        /// 本次运行内不再弹"解压前的提醒"（用户在弹窗里勾了那个勾选项）。
        ///
        /// <para>它是**实例字段**，不是静态、更不是设置项：用户勾的是"这次运行别再烦我"，
        /// 关掉程序重开就该再提醒一次（用户点名："只在本次运行内记忆，绝不写设置文件"）。
        /// 也因此**没有**任何把它写盘 / 读盘的代码 —— 测试直接断言设置文件一个字节都没变。</para>
        /// </summary>
        private bool _remindersSuppressedThisRun;

        /// <summary>
        /// "无用物"魔数体检用的识别器：**复用现有那一套**
        /// （<see cref="MagicArchiveProber"/> → <c>ArchiveDetectService</c>：文件头魔数 + 认不出来时再看尾部内嵌归档），
        /// 绝不另写一份魔数表 —— 两份必然会漂移，而漂移的后果是把用户的真包（伪装后缀 / 双面文件）
        /// 叫成"无用物"，正好砸在本程序最拿手的场景上。
        /// </summary>
        private readonly IArchiveProber _reminderProber = new MagicArchiveProber();

        /// <summary>
        /// 解压前提醒的"用户答案"（可注入；单测用它模拟"点了继续 / 点了先不处理 / 勾了不再提示"）。
        ///
        /// <para>为什么需要它：无界面宿主里那个框根本不会显示，而"勾了不再提示之后到底还会不会再弹"
        /// 只有让调用方答一次才测得出来。正式路径永远是 null（走真弹窗）。</para>
        /// </summary>
        internal Func<string, ReminderAnswer>? ReminderAnswerOverride { get; set; }

        /// <summary>一次"解压前的提醒"的用户答案。</summary>
        internal sealed class ReminderAnswer
        {
            /// <summary>true = 继续处理；false = 先不处理（这一批不开始，一个字节都不动）。</summary>
            public bool Confirmed { get; init; }

            /// <summary>是否勾了「本次运行不再提示这类提醒」。</summary>
            public bool OptionChecked { get; init; }
        }

        /// <summary>
        /// 一次「解压前的提醒」扫描的结论（A 段无用物 + B 段无可用密码）。
        ///
        /// <para>为什么把"扫出事实"和"弹框问人"拆开（用户 2026-09-24 第 17 条）：一键处理那条路
        /// 只允许有**一个**弹窗，所以它得在**动手之前**先拿到这两段事实、写进自己的确认框；
        /// 手动「只解压」那条路仍然用同一个扫描结果去弹原来那个提醒框。两段判据因此只有一份实现
        /// （<see cref="SourceJunkScanner"/> + <see cref="FindTasksWithoutUsablePassword"/>），
        /// 不会出现"确认框里说没有、解压时又提醒有"这种自相矛盾。</para>
        /// </summary>
        internal sealed class BatchReminderFacts
        {
            /// <summary>A 段：源目录里疑似打包者附带的文件。</summary>
            public SourceJunkScanResult Junk { get; init; } = new();

            /// <summary>B 段：需要密码但当前一个可用候选都没有的包。</summary>
            public IReadOnlyList<ArchiveTask> NoUsablePassword { get; init; } = Array.Empty<ArchiveTask>();

            /// <summary>两段都空 = 没什么可提醒的（既不弹框、也不写日志）。</summary>
            public bool HasAnything => Junk.HasAnything || NoUsablePassword.Count > 0;
        }

        /// <summary>
        /// 这一批的「解压前提醒」已经并进调用方的确认框了（用户 2026-09-24 第 17 条）。
        ///
        /// <para>置上之后 <see cref="ConfirmBatchRemindersAsync"/> **只写日志、不再弹第二个框**。
        /// 由 <c>OneClickCoordinator</c> 在问它那一个确认框前后设置，整条一键处理**全程有效**
        /// （续解的第 2/3 轮也不再弹 —— 那正是"弹窗太多"的来源之一），跑完由它复位
        /// （<see cref="EndCallerHandledReminderBatch"/>）。手动「只解压」不设它 —— 那条路本来就只有这一个框。</para>
        /// </summary>
        private bool _callerHandlesBatchReminderDialog;

        /// <summary>
        /// 已经替调用方扫好的那一次结论（见 <see cref="PrepareBatchReminderForCallerAsync"/>）。
        ///
        /// <para>为什么不直接让它再扫一遍：扫描要枚举源目录 + 做魔数体检（最坏 2000 次读文件），
        /// 同一批扫两遍纯属白花时间。**只用一次**（用完即清），第 2/3 轮的内层包重新扫 ——
        /// 那是新出现的包，本来就该重判。</para>
        /// </summary>
        private BatchReminderFacts? _prefetchedReminderFacts;

        /// <summary>
        /// 一键处理那条路在动手前调它：把两段事实拿走写进确认框，并声明"提醒已经并进去了"。
        /// </summary>
        internal async Task<BatchReminderFacts> PrepareBatchReminderForCallerAsync(
            IReadOnlyList<ArchiveTask> selectedTasks)
        {
            _callerHandlesBatchReminderDialog = true;

            BatchReminderFacts facts = await ScanBatchRemindersAsync(selectedTasks).ConfigureAwait(false);

            _prefetchedReminderFacts = facts;

            return facts;
        }

        /// <summary>
        /// 调用方**不打算弹任何框**（用户勾过「以后不再询问」）：提醒也一并不弹。
        ///
        /// <para>为什么不是"退回原来的提醒框"：用户勾的意思是"直接开始"，再弹一个提醒等于没听他的。
        /// 判定与日志一条都没少 —— 少的只是弹窗，而日志里照样写清"无用物 N 个 / 无可用密码 M 个"。</para>
        /// </summary>
        internal void SuppressBatchReminderDialogForThisRun()
        {
            _callerHandlesBatchReminderDialog = true;
            _prefetchedReminderFacts = null;
        }

        /// <summary>这一批跑完了：把"由调用方负责弹框"的状态收回去（下一批回到默认口径）。</summary>
        internal void EndCallerHandledReminderBatch()
        {
            _callerHandlesBatchReminderDialog = false;
            _prefetchedReminderFacts = null;
        }

        /// <summary>扫一遍"解压前的提醒"的两段（**只读事实、不弹框**）。</summary>
        internal async Task<BatchReminderFacts> ScanBatchRemindersAsync(
            IReadOnlyList<ArchiveTask> selectedTasks)
        {
            SourceJunkScanResult junk = await SourceJunkScanner
                .ScanAsync(selectedTasks, _reminderProber, CancellationToken.None)
                .ConfigureAwait(false);

            return new BatchReminderFacts
            {
                Junk = junk,
                NoUsablePassword = FindTasksWithoutUsablePassword(selectedTasks)
            };
        }

        /// <summary>
        /// 一键处理确认框的正文（用户 2026-09-24 第 17 条）：把三个结论汇总成四行文本。
        ///
        /// <para>三个结论各有**唯一来源**，这里只做汇总，绝不另算一遍：</para>
        /// <list type="bullet">
        /// <item><description>内容物落点 → <see cref="PathService.ResolveOutputPlacement"/>（与真正开跑同一条推导链）；</description></item>
        /// <item><description>其余物会怎么处理 → ③页「删除操作」那一档（<see cref="AppSettings.RestHandlingAfterVerify"/>，用户 2026-09-25 第 32 条定的三档）；</description></item>
        /// <item><description>两行提醒 → <see cref="BatchReminderFacts"/>（同一个无用物扫描器 + 同一套密码候选判据）。</description></item>
        /// </list>
        /// </summary>
        internal async Task<OneClickConfirmFacts> BuildConfirmFactsAsync(
            IReadOnlyList<ArchiveTask> targets,
            OneClickRunOptions? pendingOptions,
            BatchReminderFacts? reminders,
            CancellationToken cancellationToken = default)
        {
            string destination = await DescribePlannedDestinationAsync(targets, pendingOptions, cancellationToken)
                .ConfigureAwait(false);

            /*
             * 「其余物会怎么处理」= 「删除操作」那一档（用户 2026-09-25 第 32 条：三档
             * 不动 / 移入回收站 / 彻底删除，判据只有一处实现 RestHandlingModes.Normalize）。
             * 这里只是把它**原样说给用户听**，一个字都不另算。
             *
             * ⚠ 第 33 条：**「本次选项」优先** —— 确认框里的折叠区现在也能改这一档，
             * 用户改完再点「开始处理」时，正文那一行必须跟着说这一次将会怎么处理，
             * 不能仍念设置里的旧值（那正是"弹窗没跟着设置/选择更新"的另一半）。
             */
            string restHandling = pendingOptions != null
                ? RestHandlingModes.Normalize(pendingOptions.RestHandling)
                : RestHandlingModes.Normalize(Settings.RestHandlingAfterVerify);

            SourceHandlingMode sourceHandling = pendingOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

            /*
             * 「特定解压」那一行（用户 2026-09-24）：开关开着**且**至少有一条规则会真的跑时才写 ——
             * 用户最恨"我以为它按默认跑的"，所以动手前的那个框里必须写明这一次用了哪条规则。
             * 开关开着但一条规则都没勾时**不写**（那条路上行为与默认完全一样，写一行反而误导）。
             */
            SpecialExtractionPlan specialExtraction = SpecialExtractionPlan.FromSettings(Settings);

            /*
             * 「空间不足」模式：**动手前就把话说清**（用户 2026-09-27 要求确认框里有一条红字）——
             * 这个模式会覆盖上面那两档，而且会永久删除源包，属于"必须在点开始之前看见"的事。
             *
             * ⚠ 正文那两行也换成**覆盖后**的值：只加一条红字、却让"源包：留在原地"那一行照旧显示
             * 设置里的值，等于让用户在同一个框里读到两句互相矛盾的话。
             */
            bool spaceTight = _vm.SpaceTightMode;

            /*
             * 安全档（「不删原包」）：源包那一档要按"一个字节都不动"说，
             * 而不是按上面那样被覆盖成「彻底删除」—— 那会与安全档的真实行为相反。
             * ⚠ 2026-10-15：空间不足模式（非安全档）的真实动作 = **定稿 + 校验通过后当场就地删源包**
             * ⇒ 那一档的措辞对应 `DeleteInPlace`（原来是 `MoveToRest`，已随用户口径删除）。
             */
            bool spaceTightKeepSource = spaceTight && _vm.SpaceTightKeepSource;

            if (spaceTight)
            {
                sourceHandling = spaceTightKeepSource
                    ? SourceHandlingMode.KeepInPlace
                    : SourceHandlingMode.DeleteInPlace;

                restHandling = RestHandlingModes.Delete;
            }

            /*
             * 「多层 + 空间小 ⇒ 中途才报空间不足」这句话必须在**动手之前**就看得见
             * （用户 2026-09-29 第 2 条："开工前就要看得见……在批首的日志与确认框里写明"）。
             *
             * 判据与批首那条 WARN **是同一处**（EvaluateMultiLayerSpaceRisk），空间口径也是同一个
             * BuildSpaceAdvice（导入体检用的就是它）—— ⛔ 这里绝不另算一套。
             * 花的是 stat 文件 + 探一次盘，几十到几百个包也只是毫秒级，所以放后台线程跑。
             */
            MultiLayerSpaceRisk spaceRisk = await Task.Run(
                () => EvaluateMultiLayerSpaceRisk(BuildSpaceAdvice(targets)),
                cancellationToken).ConfigureAwait(false);

            string spaceRiskEcho = spaceRisk.Applies ? DescribeMultiLayerSpaceRisk(spaceRisk) : string.Empty;

            return new OneClickConfirmFacts
            {
                DestinationEcho = destination,
                RestEcho = StatusText.OneClickConfirmRestLabel + restHandling switch
                {
                    // ⚠ 2026-10-15：回收站那一档整条下线 ⇒ 这里只剩两档。
                    RestHandlingModes.Delete => StatusText.OneClickConfirmRestAutoDelete,
                    _ => StatusText.OneClickConfirmRestKeep
                },
                SourceEcho = StatusText.OneClickConfirmSourceLabel
                    + OneClickRunOptions.DescribeSourceHandling(sourceHandling),

                /*
                 * 空间不足模式下把「源包：」那一行**锁住**（用户 2026-09-27 真机逮到）：
                 * 折叠区里的单选框显示的是**设置**里的档（源包=留在原地），而这一批真实行为是覆盖后的 ——
                 * 不锁的话同一个框里会出现"源包：留在原地"与红字"会自动覆盖为「源包 → 放入其余物」"两句矛盾的话。
                 */
                SourceEchoLocked = spaceTight,
                SpaceTightEcho = spaceTight
                    ? spaceTightKeepSource
                        ? StatusText.SpaceTightKeepSourceConfirmText
                        : StatusText.SpaceTightConfirmText
                    : string.Empty,
                SpecialExtractionEcho = specialExtraction.IsActive
                    ? StatusText.SpecialExtractionConfirmLabel + specialExtraction.RuleNames
                    : string.Empty,
                NoticeEcho = BuildConfirmNotice(reminders, spaceRiskEcho)
            };
        }

        /// <summary>
        /// 确认框里那段"要说一声"的提醒（无用物 / 没有可用密码 / 多层可能中途空间不足）。
        ///
        /// <para>措辞与判定都沿用既有那一套（<c>StatusText.JunkReminder*</c> 的同源事实），
        /// 这里只是**变短**：确认框正文要能一眼看完，所以各只给一行、最多举三个例子。
        /// 详细清单仍然在日志里（<see cref="LogReminderFindings"/> 一字未改）。</para>
        ///
        /// <para>⚠ 空间那条排在最前（它关系"这一批能不能跑完"），而且它**不依赖**
        /// <paramref name="reminders"/> —— 没有无用物、没有密码问题时它照样要出现。</para>
        /// </summary>
        private static string BuildConfirmNotice(BatchReminderFacts? reminders, string spaceRiskEcho)
        {
            var builder = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(spaceRiskEcho))
            {
                builder.AppendLine(spaceRiskEcho);
            }

            if (reminders == null || !reminders.HasAnything)
            {
                return builder.ToString().TrimEnd();
            }

            if (reminders.Junk.HasAnything)
            {
                builder.AppendLine(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.OneClickConfirmJunkFormat,
                    reminders.Junk.TotalCount,
                    string.Join("、", reminders.Junk.SampleNames.Take(3))));
            }

            if (reminders.NoUsablePassword.Count > 0)
            {
                builder.AppendLine(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.OneClickConfirmNoPasswordFormat,
                    reminders.NoUsablePassword.Count,
                    string.Join(
                        "、",
                        reminders.NoUsablePassword.Take(3).Select(DisplayNameOf))));
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 「内容物会生成在…」那一行（用户点名要看的第一件事）。
        ///
        /// <para>走**落点唯一实现**（<c>PathService.ResolveOutputPlacement</c> → <c>OutputPlacement</c>），
        /// 与真正开跑时同一个 <c>ExtractOptions</c> 构造（含"续解各占一层 / 一律不塌缩"那套口径）——
        /// 所以这一行说的就是"真会落到哪"，不是另写一套公式算出来的近似值。
        /// 算不出来时**如实说明原因**（绝不编一个假路径让用户放心）。</para>
        ///
        /// <para>多包时补一句后缀：现在只剩"每个包各建一层（含相对子路径）"这一种形状 ——
        /// 旧的"所有包落进同一层"（场景 B / 共用根）已随落点模型 v2 退役。</para>
        /// </summary>
        internal async Task<string> DescribePlannedDestinationAsync(
            IReadOnlyList<ArchiveTask> targets,
            OneClickRunOptions? pendingOptions,
            CancellationToken cancellationToken = default)
        {
            if (targets == null || targets.Count == 0)
            {
                return string.Empty;
            }

            ExtractOptions extractOptions = BuildExtractOptions(tryExtractUnknownFormat: false);
            pendingOptions?.ApplyTo(extractOptions);

            ArchiveTask first = targets[0];

            OutputPlacementResult placement = _pathService.ResolveOutputPlacement(first, extractOptions);

            if (!placement.Success || string.IsNullOrWhiteSpace(placement.DestinationDirectory))
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.OneClickConfirmDestinationUnknownFormat,
                    placement.Message);
            }

            string head = placement.DestinationDirectory;

            if (targets.Count <= 1)
            {
                return head;
            }

            ArchiveTask? second = targets.FirstOrDefault(
                task => !ReferenceEquals(task, first)
                        && !string.Equals(task.CurrentPath, first.CurrentPath, StringComparison.OrdinalIgnoreCase));

            if (second != null)
            {
                OutputPlacementResult other = _pathService.ResolveOutputPlacement(second, extractOptions);

                if (other.Success
                    && SafePathHelper.PathEquals(other.DestinationDirectory, placement.DestinationDirectory))
                {
                    return head + string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.OneClickConfirmMultiSharedFormat,
                        targets.Count);
                }
            }

            return head + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.OneClickConfirmMultiPerArchiveFormat,
                targets.Count);
        }

        /// <summary>
        /// 造一份这一批用的解压选项（**唯一构造处**）。
        ///
        /// <para>为什么必须是一个方法：一键处理确认框要预告"内容物会落到哪"，而它必须与真正开跑时
        /// 用**完全相同的字段**去算 —— 两处各抄一份字段清单，迟早会漂移成"预告说 A、实际落 B"。</para>
        /// </summary>
        private ExtractOptions BuildExtractOptions(bool tryExtractUnknownFormat)
        {
            var extractOptions = new ExtractOptions
            {
                ExtractToOriginalDirectory = Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = SelectedOutputDirectory,
                KeepArchiveNameFolder = Settings.KeepArchiveNameFolder,
                TestBeforeExtract = Settings.TestBeforeExtract,

                /*
                 * ⚠ 引擎的"覆盖档"**刻意不跟着设置走**（用户 2026-09-25 真机铁证）。
                 *
                 * 设置里那一档（默认 SkipExisting → `-aos`）是给"最终落位的同名冲突"用的，
                 * 而我们自己的定稿逻辑（SafePathHelper 自动改名 / 冲突档）**根本不看引擎这个参数** ——
                 * 引擎写的永远是**我们的暂存目录**（`engineOutputPath = stageDirectory`），
                 * 里面的东西每一轮都是垃圾。
                 *
                 * 跟着设置走的代价实测过一次：`-aos` 让"上一次错密码留下的 0 字节桩文件"把
                 * **下一个候选（可能就是正确密码）**直接跳过、退出码 0 → 正确密码被当成废票扔掉
                 * （详见下面候选循环里那段说明）。所以暂存提取一律**覆盖写**。
                 */
                OverwriteMode = "OverwriteAll",
                UseGlobalPassword = Settings.UseGlobalPasswordForAllTasks,
                GlobalPassword = GlobalPassword,
                TryPasswordList = true,
                TryEmptyPasswordFirst = Settings.TryEmptyPasswordFirst,
                CancelOnFirstSuccess = true,
                TryExtractUnknownFormat = tryExtractUnknownFormat,
                MaxParallelExtractCount = Settings.MaxParallelExtractCount,

                /*
                 * 「解压到当前文件夹」（用户 2026-09-27）：本批是不是摊平只看这一个运行期字段。
                 * 落点由 OutputPlacement 唯一实现推导（destDir = 源包自己那一层），
                 * "不套包名那一层"由定稿那一侧按同一字段决定（见 PlanFinalLayoutForTask）——
                 * 两处读的是同一个字段，不会出现"落点摊平了、定稿又套了一层"。
                 */
                ExtractIntoSourceFolder = _extractIntoSourceFolderThisRun
            };

            extractOptions.Normalize();

            return extractOptions;
        }

        /// <summary>
        /// 解压前的合并提醒。
        /// </summary>
        /// <returns><c>false</c> = 用户在提醒里选了「先不处理」，调用方**直接返回**（这一批不开始）。</returns>
        private async Task<bool> ConfirmBatchRemindersAsync(IReadOnlyList<ArchiveTask> selectedTasks)
        {
            if (_remindersSuppressedThisRun)
            {
                return true;
            }

            /*
             * 设置里关掉了「解压前的提醒」= **不扫、不弹**，直接开始（用户 2026-09-24 第 15 条）。
             *
             * ⚠ 判在**扫描之前**：那条扫描会枚举整批任务的源目录（最坏约十万条目录项），
             * 关掉开关的人不该付这份代价。这里只读设置、只写一行日志，不碰任何文件。
             */
            if (!Settings.RemindBeforeExtract)
            {
                AppendLog("INFO", StatusText.RemindBeforeExtractDisabledLog);
                return true;
            }

            // 调用方刚为确认框扫过一次就复用它（用完即清）；否则现在扫。
            BatchReminderFacts? prefetched = _prefetchedReminderFacts;
            _prefetchedReminderFacts = null;

            BatchReminderFacts facts = prefetched
                ?? await ScanBatchRemindersAsync(selectedTasks).ConfigureAwait(false);

            if (!facts.HasAnything)
            {
                // 两段都为空：不弹、也不写日志（没有结论可写）。空对话框是纯噪声。
                return true;
            }

            LogReminderFindings(facts.Junk, facts.NoUsablePassword);
            /*
             * ⛔ **"无用物移出任务列表"也挂在这里**（真机 2026-10-07 07:49）：
             * 判据与结论**这一处已经算好了**（上面那句提醒用的就是 `facts.Junk`）；
             * 而 `ScanCoordinator.RemoveJunkTasksFromListAsync` 那两个调用点（导入 / 一键处理）
             * 在真机那一批里**一次都没进到扫描**（我加的排障行没出现，列表里那 4 个 `.txt` 也没被移走）
             * ⇒ 与其去猜它们为什么没跑，不如在"结论已经在手上"的这一刻**直接落一步**：
             * 复用既有出口 `RemoveTasksBySourcePaths`（⛔ 只动列表、⛔ 不删/不改名/不搬磁盘上的文件），
             * 移了谁、移了几个**逐条写日志**。⛔ 不在这里另写一套扫描判据（唯一实现仍是 SourceJunkScanner）。
             */
            /*
             * ⛔ 判据用**唯一出口** `AllHits`（= 全部命中），⛔ 不是那份"最多列 10 条"的提示名单（`Items`）：
             * 真机 CCCC 2026-10-07 实测扫到 17 条、`Items` 只有前 10 条 ⇒ 按 `Items` 移就会剩下几条
             * 留在列表里（用户当场问「为什么一开始只清理 10 个」）。
             */
            IReadOnlyList<SourceJunkItem> junkHits = facts.Junk.AllHits;

            if (junkHits.Count > 0)
            {
                int junkRemoved = _vm.RemoveTasksBySourcePaths(junkHits.Select(item => item.FullPath));

                if (junkRemoved > 0)
                {
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.OneClickJunkRemovedLogFormat,
                            junkRemoved,
                            string.Join("、", junkHits.Take(5).Select(item => item.FileName))));
                }
                else
                {
                    /*
                     * 列表里一条都没对上 ⇒ **中性地说一声**（INFO）：正常情形就是"导入那一步已经把它们
                     * 移走了"（用户看到的"两波"正是这两条路各移一部分）。⛔ 只写日志，⛔ 不动磁盘上任何文件。
                     */
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.JunkAlreadyOffListLogFormat,
                            junkHits.Count,
                            string.Join("、", junkHits.Take(5).Select(item => item.FileName))));
                }
            }

            if (_callerHandlesBatchReminderDialog)
            {
                /*
                 * 用户 2026-09-24 第 17 条："点击完一键处理，就只能有一个弹窗提醒"。
                 * 这一批的确认框（OneClickOptionsWindow）已经把这两段写在正文里了 ——
                 * 再弹一个就是把同一件事说两遍，正是他嫌啰嗦的那种。
                 * 判定与日志一个都没少，少的只是第二个弹窗。
                 */
                AppendLog("INFO", StatusText.BatchReminderMergedLog);

                return true;
            }

            ReminderAnswer answer = await AskReminderAsync(BuildReminderMessage(facts.Junk, facts.NoUsablePassword))
                .ConfigureAwait(false);

            if (answer.OptionChecked)
            {
                _remindersSuppressedThisRun = true;
                AppendLog("INFO", StatusText.JunkReminderOptionLog);
            }

            if (!answer.Confirmed)
            {
                AppendLog("INFO", StatusText.JunkReminderDeclinedLog);
            }

            return answer.Confirmed;
        }

        /// <summary>批首写一条 WARN，把两段的结论都落进日志（数量 + 前几条名字）。</summary>
        private void LogReminderFindings(SourceJunkScanResult junk, IReadOnlyList<ArchiveTask> noPassword)
        {
            string junkPart = junk.HasAnything
                ? string.Format(
                    StatusText.JunkReminderLogFoundFormat,
                    junk.TotalCount,
                    string.Join("、", junk.SampleNames))
                : StatusText.JunkReminderLogNoneText;

            string passwordPart = noPassword.Count > 0
                ? string.Format(
                    StatusText.JunkReminderLogFoundFormat,
                    noPassword.Count,
                    string.Join(
                        "、",
                        noPassword.Take(MaxPasswordFailureNamesInDialog).Select(DisplayNameOf)))
                : StatusText.JunkReminderLogNoneText;

            AppendLog("WARN", string.Format(StatusText.JunkReminderLogFormat, junkPart, passwordPart));
        }

        /// <summary>
        /// 拼提醒正文。三段话的顺序见 <see cref="StatusText"/> 里那一组的说明（**不要重排**）：
        /// ① 这些文件是什么 → ② 本程序不会动它们 → ③ 解压完你自己判断要不要删。
        /// </summary>
        private static string BuildReminderMessage(SourceJunkScanResult junk, IReadOnlyList<ArchiveTask> noPassword)
        {
            var builder = new StringBuilder();

            builder.AppendLine(StatusText.JunkReminderIntro);

            if (junk.HasAnything)
            {
                builder.AppendLine();
                builder.AppendLine(StatusText.JunkReminderJunkHeader);
                builder.AppendLine(string.Format(StatusText.JunkReminderJunkCountFormat, junk.TotalCount));

                foreach (IGrouping<string, SourceJunkItem> group in
                         junk.Items.GroupBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine("  " + group.Key);

                    foreach (SourceJunkItem item in group)
                    {
                        // 目录要标出来（第 32 条）：真实资源包把密码提示做成一个文件夹，
                        // 与"说明.txt"混在一起不标就分不清哪个是文件夹。
                        builder.AppendLine("    " + item.FileName + (item.IsDirectory ? "（文件夹）" : string.Empty));
                    }
                }

                if (junk.ExtraCount > 0)
                {
                    builder.AppendLine(string.Format(StatusText.JunkReminderJunkMoreFormat, junk.ExtraCount));
                }

                if (junk.Truncated)
                {
                    builder.AppendLine(StatusText.JunkReminderTruncatedNote);
                }

                builder.AppendLine(StatusText.JunkReminderJunkFooter);
            }

            if (noPassword.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine(string.Format(StatusText.JunkReminderPasswordHeaderFormat, noPassword.Count));

                foreach (ArchiveTask task in noPassword.Take(MaxPasswordFailureNamesInDialog))
                {
                    builder.AppendLine("    " + DisplayNameOf(task));
                }

                if (noPassword.Count > MaxPasswordFailureNamesInDialog)
                {
                    builder.AppendLine(string.Format(
                        StatusText.JunkReminderPasswordMoreFormat,
                        noPassword.Count - MaxPasswordFailureNamesInDialog));
                }

                builder.AppendLine(StatusText.JunkReminderPasswordOutcome);
                builder.AppendLine(StatusText.JunkReminderPasswordExit);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>任务在提示里的显示名（文件名优先，退回完整路径）。</summary>
        private static string DisplayNameOf(ArchiveTask task) =>
            string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName;

        /// <summary>
        /// 拿用户的答案。
        ///
        /// <para><b>无界面宿主（单测 / 控制台宿主）在这里按「继续处理」返回</b>，而且**连弹窗都不尝试**：
        /// 既不产生 DialogService 的降级记录（那会让"这个宿主没有界面"的既有断言被这条纯提示污染），
        /// 也不会有任何等待。整批解压因此照常跑完 —— 这一点由管线测试证明。</para>
        /// </summary>
        private async Task<ReminderAnswer> AskReminderAsync(string message)
        {
            if (ReminderAnswerOverride != null)
            {
                return ReminderAnswerOverride(message);
            }

            if (System.Windows.Application.Current == null)
            {
                AppendLog("WARN", StatusText.JunkReminderNoHostLog);

                return new ReminderAnswer { Confirmed = true, OptionChecked = false };
            }

            return await ShowReminderConfirmOnUiThreadAsync(message).ConfigureAwait(false);
        }

        /// <summary>
        /// 在 UI 线程上弹提醒（与 <see cref="ShowConfirmOnUiThreadAsync"/> 同一套调度纪律：
        /// 管线可能已经不在 UI 线程上，直接弹会造出一个没人泵消息的窗口）。
        ///
        /// <para>降级方向由 <see cref="DialogService.ShowReminderConfirm"/> 定义成"继续"，
        /// 所以这里即使调度器已经关掉也只是放行，不会把整批拦下。</para>
        /// </summary>
        private async Task<ReminderAnswer> ShowReminderConfirmOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            bool confirmed;
            bool optionChecked = false;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                confirmed = _dialogService.ShowReminderConfirm(
                    StatusText.JunkReminderTitle,
                    message,
                    StatusText.JunkReminderYesText,
                    StatusText.JunkReminderNoText,
                    StatusText.JunkReminderOptionText,
                    optionCheckedByDefault: false,
                    out optionChecked);
            }
            else
            {
                confirmed = await dispatcher.InvokeAsync(() =>
                {
                    bool checkedNow;

                    bool result = _dialogService.ShowReminderConfirm(
                        StatusText.JunkReminderTitle,
                        message,
                        StatusText.JunkReminderYesText,
                        StatusText.JunkReminderNoText,
                        StatusText.JunkReminderOptionText,
                        optionCheckedByDefault: false,
                        out checkedNow);

                    optionChecked = checkedNow;

                    return result;
                }).Task.ConfigureAwait(false);
            }

            return new ReminderAnswer { Confirmed = confirmed, OptionChecked = optionChecked };
        }

    }
}