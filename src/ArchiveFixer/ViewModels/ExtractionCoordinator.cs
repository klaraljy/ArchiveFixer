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
    internal sealed partial class ExtractionCoordinator
    {

        /// <summary>
        /// 「其余物」目录名（契约 §3.2，2026-09-22 由「过程物」改名）：为得到内容物而产生、
        /// 用户不需要的东西 —— 内层归档、分卷、抠出来的中间 ZIP、纯壳文件夹；**以及源包本身**
        /// （决策 D-9：一键处理默认把源包也移进来，用户在那个目录里一次删掉就干净了）。
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
        /// 源包搬运要碰的文件系统（决策 D-12）。
        ///
        /// 默认走真实文件系统；单测注入假实现来验证**跨盘那条路**（真机上很难临时造第二个卷）：
        /// "复制成功后再删原件、复制失败源包一个字节都不动"是红线，不能只靠读代码。
        /// </summary>
        private readonly ISourceMoveFileSystem _sourceMoveFileSystem;


        /// <summary>解压前预检最多试几个密码候选去列表：试太多次会让"一键"变成等待。</summary>
        private const int MaxPreflightPasswordAttempts = 3;

        // ================================================================
        // 不变量 11：源文件变化（AGENTS.md §6 第 11 条）
        // ================================================================


        private CancellationTokenSource? _operationCts;
        /// <summary>
        /// 正在跑的任务各自的取消源。
        ///
        /// <para>⚠ <b>必须加锁访问</b>（2026-09-22：默认并发由 1 提到 4 之后暴露的真缺陷）：
        /// 每个任务在**自己的线程**上加/删这个列表，而 <see cref="CancelCurrentTask"/> 在 UI 线程上读它。
        /// 旧的裸 <c>List</c> 写法在并发下会直接把内部数组搞坏 ——
        /// 实测到的现象是收尾时抛 <c>ArgumentOutOfRangeException</c>（<c>List.RemoveAt</c> 越界），
        /// 一个任务因此变成「未知错误」，而不是它本来的结论。</para>
        /// </summary>
        private readonly List<CancellationTokenSource> _runningTaskCts = new();

        private readonly object _runningTaskCtsLock = new();

        private void TrackRunningTask(CancellationTokenSource cts)
        {
            lock (_runningTaskCtsLock)
            {
                _runningTaskCts.Add(cts);
            }
        }

        private void UntrackRunningTask(CancellationTokenSource cts)
        {
            lock (_runningTaskCtsLock)
            {
                _runningTaskCts.Remove(cts);
            }
        }

        /// <summary>取一份快照（取消时**在锁外**逐个 Cancel：Cancel 会触发回调，不能拿着锁做）。</summary>
        private List<CancellationTokenSource> SnapshotRunningTasks()
        {
            lock (_runningTaskCtsLock)
            {
                return _runningTaskCts.ToList();
            }
        }

        private bool _isExtracting;

        /*
         * 本批因密码没通过而失败的任务（密码错误 / 达到密码尝试上限）。
         *
         * 旧逻辑在每个任务的循环体里各弹一次模态框，而且用的是**同步** Dispatcher.Invoke：
         * 50 个错包 = 50 次阻塞点击，批量跑不动。现在只在这里登记，
         * 批次结束后由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
         * 并发解压时多个任务会同时写它，所以必须加锁（而不是靠"反正都在 UI 线程上"）。
         */

        /*
         * ===== 同名冲突（ConflictAction = Ask）的批内状态 =====
         *
         * 决策 D-4：用户的真实场景是 50–200+ 个包，逐个弹窗等于不可用。所以"询问"这一档必须做到
         * **本批只问一次**：用户答了「覆盖全部 / 跳过全部 / 全部自动重命名」之后，后面的冲突一律照办，
         * 一个字都不许再问。这三条状态就是这件事的全部记账（批内重置、并发下加锁）。
         *
         * · _batchConflictDecision —— "全部 X"：本批后续所有冲突的答案；
         * · _taskConflictDecision  —— "这一个"：同一个任务内后面的冲突沿用（一次聚合询问管整任务）；
         * · _conflictPromptUnavailable —— 无界面宿主（单元测试 / 控制台宿主）或问不出答案：
         *   记下来，后面的冲突不再尝试弹窗，直接走保守档并只写一次日志。
         */

        /*
         * ===== 本批的"完成后打开输出目录"记账（设置项 OpenOutputFolderWhenDone，默认关）=====
         *
         * 为什么必须记账：设置开着的时候，如果每个任务成功都开一次资源管理器，
         * 一批 50–200 个包就会在用户桌面上炸出几十个窗口 —— 那不是"看一眼结果"，是骚扰。
         * 所以整批**只开一次**（第一个成功收尾的最外层任务），批开始时清零。
         *
         * 并发下多个任务会同时走到判断处，所以必须加锁（与 _passwordFailures 同一个理由）。
         */

        /*
         * ===== 手动输入的密码（WinRAR 参考 §3 附注 / §2 H 组采纳项）=====
         *
         * 不变量 5 的红线：**只对本次运行有效、绝不落盘**。所以它只是一个字段 ——
         * 不进 AppSettings、不进密码列表、不进日志、不进报告，进程一退就没了。
         *
         * 为什么需要它：密码本没命中、统一密码也不对时，用户以前只能"改设置再重跑整批"。
         * 现在整批**一次性**问一次，输入的值当成本批所有任务的候选（排在空密码之后、
         * 密码本之前 —— 它比密码本更"新"，是用户刚给出的信息）。
         *
         * ⚠ <c>internal</c>（2026-10-05）：单测要钉住"它必须同时进**递归**那条路的候选表"——
         * 只靠公开入口造不出"本批手动输过密码"这个状态（无界面宿主不会弹那个框）。
         */

        /*
         * ===== 「一键处理 · 本次选项」的运行期覆盖（规格 docs/输出与整理模型.md §9）=====
         *
         * 面板选的落点 / 终端落法 / 源包处理**只对这一次一键处理有效**：
         * · 它是一个**每批一份、批结束就置空**的字段（不是静态、不进设置、不落盘）——
         *   「不勾存为默认就不许改设置文件」这条硬要求靠的就是"这里根本没有写设置的路径"；
         * · 同时只有一批能在跑（_isExtracting 守着），所以字段不会被两批同时用到；
         * · 手动「只解压」（地基路径）**不带**它：那条路径照旧完全按设置走。
         */
        private OneClickRunOptions? _runOptions;

        /// <summary>本批的"本次选项"（没有就是 null —— 地基路径、或没弹面板时的降级）。</summary>
        private OneClickRunOptions? RunOptions => _runOptions;

        /*
         * ===== 手动档「解压到当前文件夹」的运行期开关（用户 2026-09-27）=====
         *
         * 语义 = WinRAR 右键的「解压到当前文件夹」：`111\222.rar` → `111\内容物`（**不建包名那一层**）。
         * 三条边界写死在这里，别的路径一律不许打开它：
         * ① 只有①页那个显式按钮会传 true（一键处理 / 批量 / 续解一律 false）—— 用户红线：批量绝不摊平；
         * ② 与「本次选项」同寿：批首置入、`finally` 里连同 _runOptions 一起清掉，不进设置、不落盘；
         * ③ 同名冲突照走既定冲突档（用户拍板接受这个代价：摊平本来就更容易撞名）。
         */
        private bool _extractIntoSourceFolderThisRun;

        /*
         * ⛔ 原来这里有一个 `SourceDeleteFileSystemOverride`（"清理源包"的删除执行体，只给测试注入）。
         * 2026-09-25 第 32 条之后**管线里再没有任何一处调用 `SourceCleanupService`**
         * （删除统一走 `Storage/RestItemPurger`，它自己的执行体接缝在 `RecycleBinService` 那一层），
         * 所以这个接缝成了"看起来能影响删除、其实什么都不影响"的假接口 —— 一并删掉，
         * 免得以后有人拿它当"删除没发生"的证据。
         */

        /*
         * ===== 空间规划 + 危险模式（用户 2026-09-22 需求）=====
         *
         * 三件互相咬合的东西，都在这一批里活着：
         * · _ledger         —— 空间预留账本：并发时"已经许出去多少空间"的唯一记账处。
         *                      没有它，5G 与 6G 两个包会各自看到"还剩 10G"然后一起开跑。
         * · _runtime        —— 逐任务的运行期记录（预留了多少、开工前的可用空间、危险模式删了多少）。
         *                      自测证据与"跳过报告"都从这里取数；key 是任务路径。
         * · _refinedEstimates —— 解压前那一遍 list 算出来的**精确**空间需求（按任务路径存）。
         *                      自测的空间曲线要用它的"内容物"那一项，而任务对象上没有这个字段
         *                      （Models\ArchiveTask.cs 不在本批授权范围内，也就不去动它）。
         */

        /*
         * ⚠ 两张表都用**任务对象本身**做键，不用路径 —— 这一点有实际后果：
         * 源包搬进其余物之后，协调器会把 task.CurrentPath 改写到新位置（决策 D-12 要求的回写）。
         * 用路径当键的话，同一个任务在"搬之前"与"搬之后"会落进两个不同的格子：
         * 自测读证据时拿到的是全新的一份（RestPurged=false、可用空间 -1），于是"明明删掉了却报没删"。
         * ArchiveTask 没有重写 Equals，字典默认就是引用相等 —— 正是这里要的语义。
         */

        /// <summary>
        /// 本批是不是**一键处理**（批首钉一次；<c>oneClickRun</c> 只在批首那个参数里，
        /// 而"中途撞上空间不足该不该弹提示"这件事发生在很深的地方，需要它当判据）。
        ///
        /// <para>为什么需要：一键档的批中间是**零弹窗**红线，而那条"空间不足"的纯提示恰恰是
        /// 唯一被允许的例外之一（纯提示 / 一个按钮 / 非模态 / 同一批一次）——
        /// 手动档保持原样（只写日志），所以要能把两档分开。</para>
        /// </summary>
        private bool _oneClickThisBatch;

        /// <summary>
        /// 本批是不是按「空间不足」的**安全档**跑（「不删原包」勾着时；见
        /// <see cref="MainViewModel.SpaceTightKeepSource"/>）。
        ///
        /// <para>安全档与默认档的差别**只有一条**：源包一个字节都不动（不搬进其余物、也不删除）。
        /// 并发、排序、其余物删除这三件事两档完全一致 —— 用户要的正是"先看清空间怎么变，
        /// 再决定要不要开那个会删源包的档"（他的原话：怕"没成功而且原包也没有了"）。</para>
        /// </summary>
        private bool _spaceTightKeepSourceThisBatch;

        public ExtractionCoordinator(
            MainViewModel vm,
            IArchiveEngine archiveEngine,
            PasswordService passwordService,
            PathService pathService,
            DialogService dialogService,
            ISourceMoveFileSystem? sourceMoveFileSystem = null)
        {
            _workspaceStep = new WorkspaceStep(this);

            _vm = vm;
            _archiveEngine = archiveEngine;
            _passwordService = passwordService;
            _pathService = pathService;
            _dialogService = dialogService;

            _uiThread = new UiThreadStep(_dialogService);
            _progressHeartbeat = new ProgressHeartbeatStep(this);
            _conflictStep = new ConflictStep(this);
            _passwordCandidatesStep = new PasswordCandidatesStep(this);
            _placementStep = new PlacementStep(this);
            _partialPublishStep = new PartialPublishStep(this);
            _sourceMoveFileSystem = sourceMoveFileSystem ?? FileSystemSourceMoveFileSystem.Instance;
        }

        /// <summary>源包搬运用的文件系统（真实实现或测试注入的假实现）。</summary>
        private ISourceMoveFileSystem SourceMoveFileSystem => _sourceMoveFileSystem;

        private AppSettings Settings => _vm.Settings;


        private string GlobalPassword => _vm.GlobalPassword;

        private string SelectedOutputDirectory => _vm.SelectedOutputDirectory;

        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

        private bool IsStopping { get => _vm.IsStopping; set => _vm.IsStopping = value; }

        private bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }


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

                LogStopRequestOnce();
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
                // 先取快照再逐个 Cancel：Cancel 会同步触发回调，不能拿着列表的锁做这件事。
                List<CancellationTokenSource> running = SnapshotRunningTasks();

                if (running.Count == 0)
                {
                    AppendLog("WARN", "当前没有正在执行的任务可取消。");
                    return;
                }

                foreach (CancellationTokenSource cts in running)
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