using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 损坏压缩包的「部分完成」发布（第 21 轮③：从 <c>ExtractionCoordinator.PartialPublishStep.cs</c>
/// 搬成独立类）。触发判据 = 引擎明说包损坏这一个结构化事实（<see cref="IsDamagedArchive"/> 读
/// <c>EngineErrorTypes.CorruptedArchive</c>）；⛔ 不看任何开关、绝不覆盖同名文件。
///
/// <para><b>本类是薄门面</b>（职责拆分）：对外的 API 名与候选契约留在这里
/// （<see cref="PartialPublishCandidate"/> 被协调器与任务收尾两处按
/// <c>PartialPublishStep.PartialPublishCandidate</c> 引用 ⇒ 类型必须留在这一步上），
/// 每一件事各自转发给一个协作类：</para>
/// <list type="number">
/// <item><description><see cref="PartialPublishFacts"/> —— 两个**唯一事实出口**（算不算损坏？链停在哪层？）。</description></item>
/// <item><description><see cref="PartialPublishCandidateRegistry"/> —— **失败那一刻**把"盘上还剩什么"记下来。</description></item>
/// <item><description><see cref="PartialPublishAdmission"/> —— **收尾那一刻**"这一单该不该发"。</description></item>
/// <item><description><see cref="PartialPublishPublisher"/> —— **真发布与结算**（搬运 / 记账 / 终态 / 其余物与工作区收尾）。</description></item>
/// <item><description><see cref="ProcessArtifactPreserver"/> —— 失败路上的"**留现场**"（只加一个名字，不搬不删）。</description></item>
/// <item><description><see cref="PartialPublishLog"/> —— 这一档**全部日志措辞与级别**。</description></item>
/// </list>
///
/// <para>⚠ 拆分的依据是**生命周期与失败模型**，不是行数：登记（内存里记事实，永不失败）→ 判定
/// （五道门槛，任何一道不过就一个字节都不发）→ 搬运与结算（全有或全无，失败时工作区原样留着）→
/// 保留（非破坏性，判不出就不动）。日志单独一类是因为**措辞本身就是口径**，不该跟着判定分支一起改。</para>
/// </summary>
internal sealed class PartialPublishStep
{
    private readonly PartialPublishCandidateRegistry _candidates;
    private readonly PartialPublishAdmission _admission;
    private readonly PartialPublishPublisher _publisher;
    private readonly ProcessArtifactPreserver _preserver;

    internal PartialPublishStep(IPartialPublishStepHost host)
    {
        PartialPublishLog log = new(host);

        _candidates = new PartialPublishCandidateRegistry();
        _admission = new PartialPublishAdmission(host, log);
        _publisher = new PartialPublishPublisher(host, log, _candidates, _admission);
        _preserver = new ProcessArtifactPreserver(host);
    }

    /// <summary>
    /// **「这一单算不算『损坏压缩包』」的唯一出口**（转发 <see cref="PartialPublishFacts"/>）。
    ///
    /// <para>判据只看**结构化事实**：引擎明说 <c>EngineErrorTypes.CorruptedArchive</c>
    /// （"归档损坏 / 校验和不符 / 数据不全"）。⛔ 不比中文文案、⛔ 不看体积/名字一类推断。</para>
    /// </summary>
    internal static bool IsDamagedArchive(string? detectedErrorType) =>
        PartialPublishFacts.IsDamagedArchive(detectedErrorType);

    /// <summary>
    /// **「链停在哪一层」的唯一出口**（转发 <see cref="PartialPublishFacts"/>）：报告里**最后一个没跑成的层**
    /// （都在跑成 / 没跑过任何一层 ⇒ null）。两个消费方（候选登记 / 最后一层过程物）读的是同一个判据，
    /// ⛔ 不各判一遍。
    /// </summary>
    internal static RecursionLayerReport? ResolveStoppedLayer(RecursionResult? result) =>
        PartialPublishFacts.ResolveStoppedLayer(result);

    /// <summary>
    /// 本批「部分完成发布」生不生效（**唯一判据**，转发 <see cref="PartialPublishAdmission.IsActive"/>）：
    /// = ③页那个开关打开 **且** 本批不是「空间不足」模式。⛔ 只让行为失效，**绝不去改用户的设置**。
    /// </summary>
    internal bool PartialPublishActive => _admission.IsActive;

    /// <summary>
    /// 部分完成发布的候选（**失败路径**填、收尾处发布；见 <see cref="TryPublishPartialProductsAsync"/>）。
    ///
    /// <para>键用任务对象本身；条目在收尾时取走 —— 不清掉的话同一个任务对象重跑时会把上一轮的清单当成
    /// 这一轮的（那是"拿旧识别结果解新文件"的翻版）。</para>
    /// </summary>
    internal ConcurrentDictionary<ArchiveTask, PartialPublishCandidate> Candidates => _candidates.Candidates;

    /// <summary>
    /// 记下"这一单失败时盘上还剩什么"（部分完成发布的输入，转发
    /// <see cref="PartialPublishCandidateRegistry.Record"/>）。**失败路径调用**，成功路径一次都不调。
    ///
    /// <para>⛔ 这里刻意**不看③页那个开关**：开关在收尾那一刻才读（见 <see cref="PartialPublishActive"/>），
    /// 否则"开着空间不足模式时本开关不生效"这句说明就没机会写出来了
    /// （一个候选都没记下来 ⇒ 收尾时连"为什么没发布"都答不上）。记的都是引用，没有额外的磁盘开销。</para>
    /// </summary>
    /// <param name="stagingRoot">已解出来的东西在哪（暂存目录 / 这一层的产物目录）。</param>
    /// <param name="manifestEntries">逐条清单（递归那条路直接带过来；没有就传 null，收尾时懒取一次）。</param>
    internal void RecordPartialPublishCandidate(
        ArchiveTask task,
        string stagingRoot,
        string archivePath,
        string password,
        IReadOnlyList<(string Path, long Size)>? manifestEntries,
        IReadOnlyList<string>? engineFailedEntries,
        int engineReportedErrorCount,
        bool engineReportedFailure,
        string? detectedErrorType = null,
        bool chainProducedContent = false) =>
        _candidates.Record(
            task,
            stagingRoot,
            archivePath,
            password,
            manifestEntries,
            engineFailedEntries,
            engineReportedErrorCount,
            engineReportedFailure,
            detectedErrorType,
            chainProducedContent);

    /// <summary>
    /// 递归那条路的登记（转发 <see cref="PartialPublishCandidateRegistry.RecordFromRecursion"/>）：
    /// 只认"最深的那一层失败了"这一档，那一层的产物才是"解了一半的现场"。
    /// </summary>
    internal void RecordRecursivePartialPublishCandidate(ArchiveTask task, RecursionResult? result) =>
        _candidates.RecordFromRecursion(task, result);

    /// <summary>
    /// 把“这一层正在解的当前过程物”按其它层同一口径留下/任其清理（转发
    /// <see cref="ProcessArtifactPreserver.PreserveCurrentProcessArtifacts"/>）：
    /// 只接受工作区里的产物；源包/判不出位置一律不动。
    /// </summary>
    internal void PreserveCurrentProcessArtifacts(ArchiveTask task, IEnumerable<string>? artifactPaths, string? entryPath) =>
        _preserver.PreserveCurrentProcessArtifacts(task, artifactPaths, entryPath);

    /// <summary>
    /// 留下**一份**当前过程物（转发 <see cref="ProcessArtifactPreserver.PreserveCurrentProcessArtifact"/>）：
    /// 只加一个名字，原件原地不动；判不出 ⇒ 一个字节都不动。
    /// </summary>
    internal bool PreserveCurrentProcessArtifact(ArchiveTask task, string? artifactPath, bool setLastPointer = true) =>
        _preserver.PreserveCurrentProcessArtifact(task, artifactPath, setLastPointer);

    /// <summary>
    /// **「最后一层过程物」按③页「删除操作」那一档处置**（转发
    /// <see cref="ProcessArtifactPreserver.PreserveStoppedLayerProcessArtifact"/>）：
    /// 默认档 = 在其余物里给它另起一个名字（硬链接，原件不搬不删）；「彻底删除」档 = 什么都不做、只写一行说清。
    /// </summary>
    internal void PreserveStoppedLayerProcessArtifact(ArchiveTask task, RecursionResult? result) =>
        _preserver.PreserveStoppedLayerProcessArtifact(task, result);

    /// <summary>
    /// **部分完成发布的唯一入口**（收尾处调用，转发 <see cref="PartialPublishPublisher.TryPublishAsync"/>）：
    /// 取走候选 → 过准入 → 懒取清单 → 真搬 → 其余物/工作区收尾。
    /// </summary>
    /// <returns>
    /// true = 这一单的工作区**不能清**（有内容没能发布，它们只在那里）；false = 按既有口径清。
    /// </returns>
    /// <param name="cancelled">
    /// 这一单是不是被用户取消的（判据 = **任务自己的取消令牌**，⛔ 不是比对中文文案）。
    ///
    /// <para>为什么必须单独传这一位：单层那条取消路径（<c>ProcessExtractAsync</c> 的
    /// <c>catch (OperationCanceledException)</c>）只落了中文状态、没落机器终态，而收尾那一步会把
    /// <c>Pending</c> 兜底改成 <c>Failed</c> ⇒ "取消"在终态上与"失败"分不开。拿终态当唯一判据的话，
    /// 用户按下取消之后，半成品照样会被摆进目标目录 —— 那正是他要停下来的东西。</para>
    /// </param>
    internal Task<bool> TryPublishPartialProductsAsync(ArchiveTask task, bool cancelled) =>
        _publisher.TryPublishAsync(task, cancelled);

    /// <summary>
    /// 部分完成发布的候选（**失败路径**填、收尾处发布）。
    ///
    /// <para>为什么用"候选"而不是就地发布：发布要读到三样只有收尾那一刻才齐的东西 ——
    /// 机器终态（失败 / 部分完成才算数）、最终输出目录（同名冲突可能刚改过名）、
    /// 以及"这一单到底还剩哪些产物"（候选循环里每个候选都会清一次产物目录）。</para>
    ///
    /// <para>⚠ 这个嵌套类型**不能搬走**：协调器与任务收尾两处按
    /// <c>PartialPublishStep.PartialPublishCandidate</c> 引用它（前者给候选表定类型，后者在收尾之前
    /// 读走 <see cref="PartialPublishCandidate.ChainProducedContent"/> 那一位）。</para>
    /// </summary>
    internal sealed class PartialPublishCandidate
    {
        /// <summary>已解出来的东西在哪（暂存目录 / 这一层的产物目录）。</summary>
        public string StagingRoot { get; init; } = string.Empty;

        /// <summary>交给引擎的那个归档（懒取清单时要用；内嵌归档这一档是抠出来的临时文件）。</summary>
        public string ArchivePath { get; init; } = string.Empty;

        /// <summary>这一层最后用的密码（懒取清单时用；只在内存里，⛔ 永不落日志）。</summary>
        public string Password { get; init; } = string.Empty;

        /// <summary>引擎点名的坏条目 / 自报的出错数 / 这一趟是不是报了失败（三道闸门）。</summary>
        public IReadOnlyList<string> EngineFailedEntries { get; init; } = Array.Empty<string>();

        public int EngineReportedErrorCount { get; init; }

        public bool EngineReportedFailure { get; init; }

        /// <summary>
        /// **这一单的失败是"引擎明说包损坏"吗**（<see cref="PartialPublishFacts.IsDamagedArchive"/> 是唯一判据出口）。
        ///
        /// <para>它是这一档的**触发事实**：登记那一刻从结构化结果里读一次、之后不再变
        /// （收尾时才决定发不发，但"坏没坏"这件事在失败那一刻就已经定了）。</para>
        /// </summary>
        public bool Damaged { get; init; }

        /// <summary>
        /// **这一单整体到底有没有内容物解出来**（用户 2026-10-16 口径：「部分完成 = 一部分成功 + 一部分失败」：
        /// 「密码错了但是其他的地方有内容物这个**肯定是判部分完成**，但是如果只有一个压缩包，而且密码错误，
        /// 那么就不可能有内容物，因为密码都没有试出来」）。
        ///
        /// <para>它是**终态分类**那一档的判据输入（与"要不要发布"是两件事：发布只认"损坏"，见
        /// <see cref="Damaged"/>）—— 单层路 = 产物目录里真解出来的非空文件够不够
        /// （唯一出口 <c>ProducedContentGate</c>，与"密码已被证实"用同一把尺子，⛔ 不另定阈值）；
        /// 递归路 = 链上**任何一层**产出过文件（<c>RecursionLayerReport.OutputFileCount</c>）。</para>
        /// </summary>
        public bool ChainProducedContent { get; init; }

        /// <summary>
        /// 引擎给的结构化错误类型（<c>EngineErrorTypes</c> 那一套词表里的名字；空串 = 没给）。
        /// 只为**如实写清"为什么没发布"**：那一行要点名这一单到底属于哪一档。
        /// </summary>
        public string DetectedErrorType { get; init; } = string.Empty;

        /// <summary>
        /// 逐条清单（相对路径 + 解压后字节）。**空表 = 手上没有清单** ⇒ 收尾时用这一层的密码
        /// 现问引擎一次（见 <see cref="PartialPublishPublisher"/> 的懒取清单）；再拿不到就什么都不发布。
        /// </summary>
        public IReadOnlyList<(string Path, long Size)> ManifestEntries { get; init; } =
            Array.Empty<(string, long)>();
    }
}
