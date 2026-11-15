using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;


/// <summary>
/// 其余物（过程残留）的处理（第 23 轮③：从 <c>ExtractionCoordinator.RestHandlingStep.cs</c> 搬成独立类）。
/// ⛔ 删除前的六道门槛一条不绕过；判不出 ⇒ 一个字节都不动。
///
/// <para><b>本类是薄门面</b>（职责拆分：不是 partial，也不按行数切）：对外的 API 名与
/// <see cref="RestHandlingTrigger"/> 留在这里（协调器按 <c>RestHandlingStep.XXX</c> 引用），
/// 每一件事各自转发给一个协作类：</para>
/// <list type="number">
/// <item><description><see cref="ChainInnerPackageMovePlanner"/> —— 链尾归集的**纯计划**（搬哪些内层包、收到哪个名字；一个字节都不动）。</description></item>
/// <item><description><see cref="RestArtifactMover"/> —— **单个文件搬进其余物**的唯一动作出口（让位 + 同卷改名 / 跨卷复制 + 绝不覆盖）。</description></item>
/// <item><description><see cref="ChainRestArtifactCollector"/> —— 链尾**归集执行**与"还留着什么"的如实点名（逐项失败、整体继续）。</description></item>
/// <item><description><see cref="RestDirectoryResolver"/> —— 其余物目录解析（判不出 ⇒ 保留原值，⛔ 不猜路径）。</description></item>
/// <item><description><see cref="ChainRestSettlement"/> —— 链尾**结算**（按档动源包/其余物的准入 + 源包补做 + 内容物事实）。</description></item>
/// <item><description><see cref="RestPurgeStep"/> —— 「删除操作」的**执行体**（任务/链尾/成功链/部分完成四种收尾；删不成也落成结论）。</description></item>
/// <item><description><see cref="PassThroughRestSweepStep"/> —— **过路层**的其余物按档清掉（有任何一个文件判不出归属 ⇒ 什么都不做）。</description></item>
/// <item><description><see cref="RestHandlingBatchPolicy"/> —— 批首准入与口径（本次选项优先、空间不足覆盖、批内一致）。</description></item>
/// <item><description><see cref="RestHandlingLog"/> —— 这一档**全部结论/证据/原因措辞**的唯一出口。</description></item>
/// <item><description><see cref="BrokenVolumeChainNameRepairStep"/> —— 解压前的**分卷名修复**（只摆正自己的产物）。</description></item>
/// <item><description><see cref="ExtractionThroughputText"/> —— 直读速度那一句措辞（与其余物无生命周期关系，单独一类）。</description></item>
/// </list>
///
/// <para>⚠ 拆分的依据是**生命周期与失败模型**，不是行数：准入（批首定档）→ 规划（纯计划 / 目录解析）→
/// 执行（搬运 / 删除）→ 结算（链尾按档、成功后清空、部分完成只留源包）→ 输出（日志与证据）。
/// 每个协作类只在**它那一站**失败：归集逐项失败不影响结论；删除失败落成"没删成"；结算判不出 ⇒ 一个字节都不动；
/// 批策与措辞从不碰盘。</para>
/// </summary>
internal sealed class RestHandlingStep
{
    private readonly RestHandlingBatchPolicy _batchPolicy;
    private readonly ChainRestArtifactCollector _collector;
    private readonly ChainRestSettlement _settlement;
    private readonly RestPurgeStep _purge;
    private readonly PassThroughRestSweepStep _passThrough;
    private readonly RestHandlingLog _log;
    private readonly BrokenVolumeChainNameRepairStep _volumeNameRepair;

    internal RestHandlingStep(IRestHandlingStepHost host)
    {
        _log = new RestHandlingLog(host);
        _batchPolicy = new RestHandlingBatchPolicy(host);
        _purge = new RestPurgeStep(host, _batchPolicy);
        _collector = new ChainRestArtifactCollector(host, _purge);
        _settlement = new ChainRestSettlement(host, _batchPolicy, _log, _purge);
        _passThrough = new PassThroughRestSweepStep(host, _batchPolicy, _settlement);
        _volumeNameRepair = new BrokenVolumeChainNameRepairStep(host);
    }

    /// <summary>
    /// 把一条续解链里**被真正解过的内层包**搬进根任务的「其余物」（第 35 条；转发
    /// <see cref="ChainRestArtifactCollector.CollectAsync"/>）。
    ///
    /// <para>只搬"事实成立"的那些，任何一步失败只写 WARN（内层包留在原地，不影响任何结论）；
    /// 只有真的搬进去了才把其余物目录记到根任务上（否则删除档会去删一个不存在的目录）。
    /// 整条链全部成功时这里直接清空其余物；失败 / 部分完成 / 有未展开内层包时原样留着。⛔ 绝不覆盖。</para>
    /// </summary>
    internal async Task CollectChainInnerPackagesIntoRestAsync(
        ArchiveTask rootTask,
        IReadOnlyList<ArchiveTask>? chainTasks,
        CancellationToken cancellationToken,
        string? planAllowedRoot = null) =>
        await _collector.CollectAsync(rootTask, chainTasks, cancellationToken, planAllowedRoot);

    /// <summary>
    /// 把链内"用过的那几个文件"集中进其余物（转发
    /// <see cref="ChainRestArtifactCollector.CollectLayerInputsIntoRest"/>）：只搬还在盘上、落在根任务
    /// **输出范围之内**、且不在其余物目录里的那些；判不出 / 搬不动 ⇒ 写一行、不动。
    /// </summary>
    internal void CollectChainInnerLayerInputsIntoRest(ArchiveTask rootTask, string restDirectory, string outputRoot) =>
        _collector.CollectLayerInputsIntoRest(rootTask, restDirectory, outputRoot);

    /// <summary>
    /// **如实点名"成品目录里还留着的内层包"**（转发
    /// <see cref="ChainRestArtifactCollector.ReportLeftBehindInnerPackages"/>）：内容还没解出来的内层包
    /// **不能**当过程物清掉（清掉就是删内容），链尾那一站**无条件**调它。
    /// </summary>
    internal void ReportLeftBehindInnerPackages(ArchiveTask rootTask, IReadOnlyList<ArchiveTask>? chainTasks) =>
        _collector.ReportLeftBehindInnerPackages(rootTask, chainTasks);

    /// <summary>
    /// 链尾归集的**纯计划**（转发 <see cref="ChainInnerPackageMovePlanner.Plan"/>）：只算"哪些内层包、收到哪个名字"，
    /// 一个字节都不动。⛔ 分卷组整组一起搬；命中「内容物保留关键词」/ 默认内容容器 ⇒ 碰都不碰；撞名让位绝不覆盖。
    /// </summary>
    internal static List<(string From, string To)> PlanChainInnerPackageMoves(
        ArchiveTask? rootTask,
        IReadOnlyList<ArchiveTask>? chainTasks,
        string? restDirectory,
        string? outputRoot,
        ContentKeepRules? keepRules,
        out List<string> warnings) =>
        ChainInnerPackageMovePlanner.Plan(
            rootTask,
            chainTasks,
            restDirectory,
            outputRoot,
            keepRules,
            out warnings);

    /// <summary>
    /// 把"这段活干了多快"说成人话（转发 <see cref="ExtractionThroughputText.Describe"/>）：直读那一条日志用它
    /// （用户 2026-09-25 第 38 条）。
    /// </summary>
    internal static string DescribeThroughput(long writtenBytes, TimeSpan elapsed) =>
        ExtractionThroughputText.Describe(writtenBytes, elapsed);

    /// <summary>
    /// 「一组分卷的第一卷名字被改坏」时，把**我们自己产出的**那一卷改回标准命名（转发
    /// <see cref="BrokenVolumeChainNameRepairStep.TryRepair"/>）：只对续解任务；改不动 ⇒ 保持原样并 WARN。
    /// </summary>
    internal string TryRepairBrokenVolumeChainName(ArchiveTask task, string engineArchivePath) =>
        _volumeNameRepair.TryRepair(task, engineArchivePath);

    /// <summary>
    /// 列一个目录里的**文件名**（转发 <see cref="BrokenVolumeChainNameRepairStep.TryListFileNames"/>）：
    /// 读不了就返回空集合，调用方按"没有线索"处理。
    /// </summary>
    internal static IReadOnlyList<string?> TryListFileNames(string? directory) =>
        BrokenVolumeChainNameRepairStep.TryListFileNames(directory);

    /// <summary>
    /// 链尾**结算**（转发 <see cref="ChainRestSettlement.ApplyAfterChainAsync"/>）：只服务失败 / 部分完成路径 ——
    /// 单一物最终成功时由成功清理出口处理。判据任一条不成立 ⇒ 一个字节都不动，但**必须写清是哪一条**。
    /// </summary>
    internal async Task ApplyRestHandlingAfterChainAsync(
        ArchiveTask task,
        IReadOnlyList<ArchiveTask>? chainTasks,
        CancellationToken cancellationToken) =>
        await _settlement.ApplyAfterChainAsync(task, chainTasks, cancellationToken);

    /// <summary>
    /// **其余物为什么原封不动地留着** —— 那一句话的唯一出口（转发
    /// <see cref="RestHandlingLog.DescribeRestKeptNote"/>）：把日志里的事实搬到①页、批末诊断与失败清单三处。
    /// </summary>
    internal static string DescribeRestKeptNote(ArchiveTask task, string reason) =>
        RestHandlingLog.DescribeRestKeptNote(task, reason);

    /// <summary>
    /// 把"其余物没处理 + 为什么"记到任务上（转发 <see cref="RestHandlingLog.RecordRestKept"/>）：
    /// ①页「错误信息」列、批末诊断与失败清单读同一份文字；幂等，且**刻意不改**任务终态 / 状态。
    /// </summary>
    internal static void RecordRestKept(ArchiveTask task, string reason) =>
        RestHandlingLog.RecordRestKept(task, reason);

    /// <summary>
    /// 链尾"按档处理其余物 / 源包"被某一条判据拦下时的**唯一一行**日志（转发
    /// <see cref="RestHandlingLog.AppendRemovalBlocked"/>）：⛔ 不许静默 return，同一句话也落到①页与报告里。
    /// </summary>
    internal void AppendRemovalBlocked(ArchiveTask task, string why) =>
        _log.AppendRemovalBlocked(task, why);

    /// <summary>
    /// L4（完整性分类）的**那一行证据**（转发 <see cref="RestHandlingLog.AppendCompletenessEvidence"/>）：
    /// 可证完整只在"这一次真的要按设置动源包"时才写；判不出 / 可证不完整 ⇒ **一律写**，并点名哪一层、为什么。
    /// </summary>
    internal void AppendCompletenessEvidence(
        ArchiveTask task,
        OutputVerificationResult verification,
        SourceHandlingMode sourceHandling) =>
        _log.AppendCompletenessEvidence(task, verification, sourceHandling);

    /// <summary>L3 的预期清单是从哪一层来的（转发 <see cref="RestHandlingLog.DescribeManifestBasis"/>）：给人看的一句话。</summary>
    internal static string DescribeManifestBasis(ManifestExpectation expectation) =>
        RestHandlingLog.DescribeManifestBasis(expectation);

    /// <summary>
    /// 链结束后补搬源包的后台本体（转发 <see cref="ChainRestSettlement.RunDeferredSourceMoveWorkAsync"/>）：
    /// 判据一律读**事实**（L4 可证完整 / 机器终态 / 分卷完整性 / 内容物是否存在 / 取消令牌）；
    /// 只「彻底删除」档有动作，且走与别处同一条闸门链。**只允许在后台线程上跑**。
    /// </summary>
    internal async Task<DeferredSourceMoveWork> RunDeferredSourceMoveWorkAsync(
        ArchiveTask rootTask,
        IReadOnlyList<ArchiveTask>? chainTasks,
        SourceHandlingMode sourceHandling,
        CancellationToken cancellationToken) =>
        await _settlement.RunDeferredSourceMoveWorkAsync(rootTask, chainTasks, sourceHandling, cancellationToken);

    /// <summary>
    /// 链上的任务集合（转发 <see cref="ChainRestSettlement.EnumerateChainTasks"/>）：调用方传进来的续解子任务
    /// ＋ 根任务自己，去重后逐个产出（"根任务也是链上的一员"这件事不能漏）。
    /// </summary>
    internal static IEnumerable<ArchiveTask> EnumerateChainTasks(ArchiveTask rootTask, IReadOnlyList<ArchiveTask>? chainTasks) =>
        ChainRestSettlement.EnumerateChainTasks(rootTask, chainTasks);

    /// <summary>
    /// 数一个最终目录里**真正的内容物文件**（转发 <see cref="ChainRestSettlement.CountContentFiles"/>）：
    /// 排除其余物、归档/分卷这类待续解的过程物，**并排除工作区自己那棵树**；读不了目录 ⇒ 返回 0
    /// （宁可判定"没有内容物"也不动源包）。
    /// </summary>
    internal int CountContentFiles(string destinationDirectory) =>
        _settlement.CountContentFiles(destinationDirectory);

    /// <summary>
    /// 归集之后其余物目录到底在哪（转发 <see cref="RestDirectoryResolver.ResolveRestDirectoryAfterCollect"/>）：
    /// 归集后的位置优先，其次计划路径；两边都不存在 ⇒ 跟着内容物走（⛔ 不退回那个已经不存在的旧位置）。
    /// </summary>
    internal static string ResolveRestDirectoryAfterCollect(StageCommitResult commit, CollectResult? collected) =>
        RestDirectoryResolver.ResolveRestDirectoryAfterCollect(commit, collected);

    /// <summary>
    /// **过路层那一单的其余物按档处理**（转发 <see cref="PassThroughRestSweepStep.PurgeSettledProducer"/>）：
    /// 六道门槛一条不绕过，唯一差别是证据来源是**接手它的那一单**；其余物里只要有一个文件不属于这一组
    /// ⇒ 什么都不做（判不出就不动）。
    /// </summary>
    internal void PurgePassThroughRestOfSettledProducer(ArchiveTask producer, ArchiveTask consumer, string groupBaseName) =>
        _passThrough.PurgeSettledProducer(producer, consumer, groupBaseName);

    /// <summary>
    /// 「其余物」这一档会动到什么（转发 <see cref="RestHandlingLog.DescribeRestScope"/>）：
    /// 源包那一档选「留在原地」时，其余物里**根本没有源包**，这句话必须跟着档位说。
    /// </summary>
    internal static string DescribeRestScope(SourceHandlingMode sourceHandling) =>
        RestHandlingLog.DescribeRestScope(sourceHandling);

    /// <summary>
    /// **"过路层的其余物"该不该放行**（转发 <see cref="PassThroughRestSweepStep.CanSweep"/>）：这一单没失败 / 没取消、
    /// 其余物在盘上且非空、其余物里**每一个文件**都已被下游接手 —— 一条不成立 ⇒ 判不出 ⇒ 什么都不做。
    /// </summary>
    internal static bool CanSweepPassThroughRest(
        ArchiveTask? task,
        IReadOnlyDictionary<string, string>? consumedVolumeSources) =>
        PassThroughRestSweepStep.CanSweep(task, consumedVolumeSources);

    /// <summary>
    /// 把**根任务那条链里过路层的其余物**按档处理掉（转发 <see cref="PassThroughRestSweepStep.SweepAsync"/>）：
    /// ⛔ 只碰这一棵树、只碰放行判据成立的那几单（别的链、链中途要留现场的那几单一律不动）。
    /// </summary>
    internal async Task SweepPassThroughRestDirectoriesAsync(ArchiveTask rootTask, CancellationToken cancellationToken) =>
        await _passThrough.SweepAsync(rootTask, cancellationToken);

    /// <summary>
    /// **这一份其余物是不是落在"根任务那棵树"里**（转发 <see cref="PassThroughRestSweepStep.IsRestInsideRootTree"/>）：
    /// 参数顺序按 <see cref="ArchivePathGuard.IsInsideRoot"/> 的定义 —— 第一个是根、第二个是待查路径。
    /// </summary>
    internal static bool IsRestInsideRootTree(string? rootDirectory, string? restDirectory) =>
        PassThroughRestSweepStep.IsRestInsideRootTree(rootDirectory, restDirectory);

    /// <summary>
    /// **把一个过程物文件搬进其余物**（转发 <see cref="RestArtifactMover.TryMoveIntoRest"/>）：
    /// 让位 + 同卷改名 / 跨卷复制 + 绝不覆盖；复制失败 ⇒ 一个字节都不动。三处消费点共用这一个出口。
    /// </summary>
    internal static bool TryMoveIntoRest(
        string from,
        string to,
        HashSet<string> movedTargets,
        out string target,
        out string? failure) =>
        RestArtifactMover.TryMoveIntoRest(from, to, movedTargets, out target, out failure);

    /// <summary>这次其余物处理的触发点：任务收尾 vs 链尾补做（⛔ 不用 bool force）。</summary>
    internal enum RestHandlingTrigger
    {
        TaskEnd = 0,
        ChainEnd = 1
    }

    /// <summary>
    /// 「删除操作」那一档的**唯一触发点**（转发 <see cref="RestPurgeStep.RunAsync"/>）：任务收尾之后，
    /// 按用户选的档处理它自己的其余物。删除门槛全在 <see cref="RestItemPurger"/> 里。
    ///
    /// <para>⚠ 一键处理里**不能在这里动手**（这一刻其余物里躺着的正是下一层要解的内层包），
    /// 统一记成"留到链尾"；手动「只解压」是单层路径，当场按档处理。</para>
    ///
    /// <para>**失败 / 部分完成 / 取消的任务一个字节都不会被删**（不变量 1 的红线）。</para>
    /// </summary>
    internal async Task RunRestHandlingAsync(
        ArchiveTask task,
        ScheduledTaskRuntime runtime,
        bool oneClickRun,
        RestHandlingTrigger trigger = RestHandlingTrigger.TaskEnd) =>
        await _purge.RunAsync(task, runtime, oneClickRun, trigger);

    /// <summary>
    /// 逐层回收那一句里"**为什么没回收**"（转发 <see cref="RestHandlingLog.DescribeLayerReclaimBlocker"/>）：
    /// 只读任务上已有的事实（状态常量 / 机器终态），⛔ 不重推判据、不重算原因。
    /// </summary>
    internal static string DescribeLayerReclaimBlocker(ArchiveTask task) =>
        RestHandlingLog.DescribeLayerReclaimBlocker(task);

    /// <summary>
    /// 其余物处理成功后那**一行**（转发 <see cref="RestHandlingLog.DescribeRestPurgedLine"/>）：
    /// 空壳档与普通档两套措辞，判据只读 <see cref="RestPurgeOutcome.RemovedAsEmptyShell"/> 事实位。
    /// </summary>
    internal static string DescribeRestPurgedLine(RestPurgeOutcome outcome) =>
        RestHandlingLog.DescribeRestPurgedLine(outcome);

    /// <summary>
    /// 批首定一次"本批的删除操作按哪一档走"（转发 <see cref="RestHandlingBatchPolicy.Prepare"/>）：
    /// **「本次选项」优先**，没有才读设置；「空间不足」模式覆盖这一档（运行期覆盖、⛔ 不写设置）。
    /// </summary>
    internal void PrepareRestHandlingForBatch() => _batchPolicy.Prepare();

    /// <summary>
    /// 本批「其余物」的范围那一句话（转发 <see cref="RestHandlingBatchPolicy.DescribeScopeForBatch"/>）：
    /// 「空间不足」模式必须另说一句（那种模式下源包根本不进其余物）。
    /// </summary>
    internal string DescribeRestScopeForBatch() => _batchPolicy.DescribeScopeForBatch();

    /// <summary>
    /// 批首写一条"其余物这一批会怎么处理"的日志（转发 <see cref="RestHandlingBatchPolicy.LogBatchPolicy"/>）：
    /// 用户要的是**当场看得懂**，而不是跑完发现"其余物还在"却不知道为什么。
    /// </summary>
    internal void LogRestHandlingForBatch() => _batchPolicy.LogBatchPolicy();

    /// <summary>
    /// 整条链成功后的其余物清空（转发 <see cref="RestPurgeStep.PurgeRestAfterSuccess"/>）：
    /// **不检查最外层源包是否还在盘上**（成功链按用户档位删源包是允许的）；失败 / 部分完成仍然只能走
    /// <see cref="PurgeRestItemsExceptChainRootSource"/>，那条路继续要求源包在盘上。
    /// </summary>
    internal void PurgeRestAfterSuccess(
        ArchiveTask task,
        string? planAllowedRoot = null,
        CancellationToken cancellationToken = default) =>
        _purge.PurgeRestAfterSuccess(task, planAllowedRoot, cancellationToken);

    /// <summary>
    /// 部分完成收尾：把其余物里**除这条链最外层源包以外**的项删掉（转发
    /// <see cref="RestPurgeStep.PurgeRestItemsExceptChainRootSource"/>）；源包不在盘上 ⇒ **一个字节都不删**。
    /// </summary>
    internal void PurgeRestItemsExceptChainRootSource(ArchiveTask task) =>
        _purge.PurgeRestItemsExceptChainRootSource(task);

    /// <summary>
    /// 本批生效的「删除操作」档（<see cref="RestHandlingModes"/>；批首定一次，转发
    /// <see cref="RestHandlingBatchPolicy.Mode"/>）。
    ///
    /// <para>⚠ 批首定一次而不是每任务现读设置：同一批里不许"前半段按一个档、后半段按另一个"。
    /// <see cref="RestHandlingModes.Keep"/> 是默认档 —— 没经过批首的路径（例如单独解压一个包）
    /// 永远按 Keep 走，不会顺手删东西。</para>
    /// </summary>
    internal string RestHandlingThisBatch
    {
        get => _batchPolicy.Mode;
        set => _batchPolicy.Mode = value;
    }
}
