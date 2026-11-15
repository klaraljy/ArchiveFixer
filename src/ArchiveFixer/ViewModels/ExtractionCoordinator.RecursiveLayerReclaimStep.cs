using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System.Collections.Generic;
using System.Threading.Tasks;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 递归器构建 / 逐层回收 / 递归上限（原 <c>ExtractionCoordinator.RecursiveLayerReclaimStep.cs</c> 的全部实现）——
/// 实现已整块搬到协作类 <see cref="RecursiveLayerReclaimHandler"/>，这一分片只留同签名薄转发 + 最小 internal 访问器。
///
/// <para>⛔ 判据、日志顺序、调用顺序一个字都没改；调用点（<c>ExtractionCoordinator.Recursion.cs</c> 的
/// 访问器、<c>RecursionHandler</c>、<c>RecursionGateAndHintFixesTests</c> / <c>RecursionLogAndProbePathFixesTests</c>）
/// 一处未动。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>搬出去的那一份实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private RecursiveLayerReclaimHandler? _recursiveLayerReclaimHandler;

    private RecursiveLayerReclaimHandler Reclaim => _recursiveLayerReclaimHandler ??= new RecursiveLayerReclaimHandler(this);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerReclaimHandler.CreateRecursiveExtractor"/>。</summary>
    private RecursiveExtractor CreateRecursiveExtractor(
        int? maxDepth = null,
        bool manualSingleLayer = false,
        bool spaceTight = false) =>
        Reclaim.CreateRecursiveExtractor(maxDepth, manualSingleLayer, spaceTight);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerReclaimHandler.ResolveCandidateSourceForLog"/>。</summary>
    internal string ResolveCandidateSourceForLog(
        string value,
        IReadOnlyDictionary<string, string>? candidateSources = null) =>
        Reclaim.ResolveCandidateSourceForLog(value, candidateSources);

    /// <summary>薄转发：实现见 <see cref="RecursiveLayerReclaimHandler.BuildRecursionLimits"/>。</summary>
    internal RecursionLimits BuildRecursionLimits(
        int? maxDepth = null,
        bool manualSingleLayer = false,
        bool spaceTight = false) =>
        Reclaim.BuildRecursionLimits(maxDepth, manualSingleLayer, spaceTight);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那几个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     *
     * 已经在 ServiceForwarding / Source / PieceAdoptionStep / LogCaptureStep / VolumeHandoffStep
     * 里 internal 的那些直接复用：_archiveEngine / Settings / AppendLogPair / AppendLog /
     * BuildRecursionPasswordCandidates / IsUserImportedSourcePath / _extractionContentGraph /
     * _passwordService / _vm / _cleanupLedger / PurgeLayerSourcePackage / IsStopping / BudgetLimits。
     */

    /// <summary>访问器：给 <see cref="RecursiveLayerReclaimHandler"/> 判"本批走不走逐层回收"（唯一写入点 = 批首）。</summary>
    internal bool LayerReclaimThisBatchForRecursiveLayerReclaim => _layerReclaimThisBatch;

    /// <summary>访问器：给 <see cref="RecursiveLayerReclaimHandler"/> 报候选来源时读全局密码（唯一实现 = 本类）。</summary>
    internal string GlobalPasswordForRecursiveLayerReclaim => GlobalPassword;

    /// <summary>访问器：给 <see cref="RecursiveLayerReclaimHandler"/> 取每层密码尝试上限（唯一实现 = 密码候选步）。</summary>
    internal int MaxPasswordAttemptsPerLayerForRecursiveLayerReclaim => MaxPasswordAttemptsPerLayer;

    /// <summary>访问器：给 <see cref="RecursiveLayerReclaimHandler"/> 在解第 0 层前问"源还是原来那份吗"。</summary>
    internal Task<string?> CheckRootSourceUnchangedForRecursiveLayerReclaim(ArchiveTask task) =>
        CheckRootSourceUnchangedAsync(task);

    /// <summary>访问器：给 <see cref="RecursiveLayerReclaimHandler"/> 判"这条路径是某单的原始源包吗"。</summary>
    internal bool IsAnyTaskOriginalPathForRecursiveLayerReclaim(string path) => IsAnyTaskOriginalPath(path);

    /// <summary>访问器：给 <see cref="RecursiveLayerReclaimHandler"/> 判"这条路径的物理身份是某单的原始源包吗"。</summary>
    internal bool IsAnyTaskOriginalIdentityForRecursiveLayerReclaim(string path) =>
        IsAnyTaskOriginalIdentity(path);

    /// <summary>访问器：给 <see cref="RecursiveLayerReclaimHandler"/> 记账"这一层用过哪些内层包"（唯一账本 = 本类）。</summary>
    internal void AddChainInnerLayerInputForRecursiveLayerReclaim(string path) => _chainInnerLayerInputs.Add(path);

    /// <summary>
    /// 本次运行里每个任务用过的**递归核心**（<see cref="RecursiveExtractor"/> 实例）。
    ///
    /// <para>为什么要在收尾处留着它：递归核心的逐层工作区不在任务工作区里，而是它自己的
    /// <c>&lt;work&gt;\recursive\&lt;taskId&gt;</c>（<see cref="RecursiveExtractor.ConfiguredWorkspaceRoot"/>
    /// 之下再挂一层 <c>recursive</c>）—— 那才是双层包里最占地方的一份（用户原话：
    /// "如果解压 40G，两层，解压失败有 80G 的卸载残留"）。它按结论清理的时机有两处：
    /// 成功那一支由递归核心自己在 <c>FinalizeRun</c> 里清；**失败 / 取消 / 部分完成**那一支
    /// 原本一律保留，现在按 <see cref="AppSettings.KeepFailedWorkspace"/> 决定清不清 ——
    /// 而那个决定只能在任务收尾时做（要等"这一批到底算不算成功"落定），
    /// 取消那一条路更是连 <c>ExtractAsync</c> 都没正常返回（抛 OperationCanceledException）。</para>
    ///
    /// <para>⚠ 一个任务一个实例（递归核心把"本次任务的工作区"记在实例上），键用任务对象本身
    /// （ArchiveTask 没重写 Equals，字典默认引用相等）；条目在收尾时移除。</para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ArchiveTask, RecursiveExtractor> _taskRecursiveExtractors = new();

    /// <summary>
    /// 本批走不走**「逐层回收」**（用户 2026-10-03 拍板；批首定一次）。
    ///
    /// <para><b>⛔ 2026-10-15 口径变更：与③页「删除操作」档位彻底解绑</b> —— 用户 2026-10-14 口径逐字
    /// 「删除操作只针对**最后一层过程物**……**其他的过程物统一彻底删除**（因为他们都是成功的情况下），
    /// 统一彻底删除」（见 <c>docs\大改动-2026-10-14-口径与冲突.md</c> §2.1 / §2.2 / §18.1）。
    /// 老口径（"只有彻底删除档才逐层回收"）已废：**成功链上的过程物一律当场永久删**，
    /// 不管用户选的是留下还是彻底删除 —— 它们是"成功过的那几层"，可以从留下的那一层重做
    /// （用户 2026-10-14 的论证）。</para>
    ///
    /// <para><b>⛔ 只在这里写一次</b>（AGENTS.md §9.5：同一件事的真值只允许有一个出口）——
    /// 续解层的每层回收（<c>PostProcessSuccessAsync</c>）、递归路的钩子
    /// （<see cref="RecursiveLayerReclaimHandler"/> 的 <c>OnRecursiveLayerReclaim</c>，挂载点读它）、链尾"只处理源包"
    /// （<c>CompleteRootSourcePackagesAfterChainAsync</c>）都只读这一个字段，
    /// 别处⛔ 不许再拿设置 / <see cref="RunOptions"/> 判一遍。</para>
    ///
    /// <para>⚠ 唯一的例外仍是**「空间不足」模式**：那一档有它自己的一支（连源包都在每层当场删），
    /// 批首把它置 false 避免抢活（见 <c>PrepareRestHandlingForBatch</c> 里那一支）。</para>
    ///
    /// <para>⚠ 与 <see cref="RestHandlingThisBatch"/> 同样的寿命：批尾**不清**（链尾钩子在批次返回之后
    /// 才跑，清掉会让链尾以为"这一批没有逐层回收"而再去搬已经不存在的内层包）；
    /// 下一批开工时 <c>PrepareRestHandlingForBatch</c> 会重新写一次。</para>
    /// </summary>
    private bool _layerReclaimThisBatch;

}
