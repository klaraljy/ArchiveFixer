using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **试开验证**：把候选按假设名做成硬链接，让既有引擎真的列一次目录 —— 列出来才算数。
///
/// <para><b>为什么必须真的试开</b>：7z 的分卷内容里没有卷号（见
/// <see cref="VolumeContentInference"/> 的注释），"按尺寸猜顺序"只是**假设**；
/// 假设错了去改名，就是把一组本来能解的包改成解不开的（改名不可逆、用户还得自己改回来）。
/// 引擎列一次目录的证据强度，比任何名字/尺寸规律都高。</para>
///
/// <para><b>⛔ 绝不复制文件</b>：一卷 2 GiB，复制一组要几十 GiB 和几分钟。
/// 这里一律用 <c>CreateHardLinkW</c> 在**同一卷**上建第二个名字（零字节、瞬时）。
/// 由此推出两条硬规矩（用户 2026-09-30 红线：「工作区就设在解压的地方，这就完全不存在跨盘的操作」）：
/// <b>① 没有工作区根（拿不到这一单的目标目录）⇒ 一次都不试</b>；<b>② 跨卷 ⇒ 一次都不试</b>。
/// ⛔ 不许退到源卷根 / 程序目录 / 临时目录另开一个工作区 —— 判不出就如实报"无法确认"。</para>
///
/// <para><b>职责分布</b>（按职责拆分；⛔ 试开顺序与判据一条没动，本类只留公开 API 与转发）：</para>
/// <list type="bullet">
/// <item><description><see cref="VolumeProbeCandidates"/>：试开候选 —— 准入、卷/顺序候选、逐个候选试开；</description></item>
/// <item><description><see cref="VolumeProbeEvidence"/>：证据 —— 硬链接、试开目录、文件大小与清场；</description></item>
/// <item><description><see cref="VolumeProbeEngineCalls"/>：引擎调用 —— 列目录 / 完整性测试；</description></item>
/// <item><description><see cref="VolumeProbeJudgment"/>：结果判定 —— 引擎原话 → 成立 / 不成立 / 加密头；</description></item>
/// <item><description><see cref="VolumeProbeFailures"/>：失败模型 —— 拒绝结论与「没试 ≠ 不成立」的包装。</description></item>
/// </list>
///
/// <para><b>本类不引用 WPF</b>，只依赖 <see cref="IArchiveEngine"/> 与文件系统。</para>
/// </summary>
public sealed class VolumeProbeVerifier
{
    private readonly VolumeProbeCandidates _candidates;

    public VolumeProbeVerifier(IArchiveEngine engine)
    {
        _candidates = new VolumeProbeCandidates(engine);
    }

    /// <summary>
    /// 先确认"第一卷自己打不开"（打得开 = 它本身就是个完整压缩包，不是分卷的第一卷，那就什么都别做），
    /// 再逐个假设顺序试开。
    /// </summary>
    /// <param name="firstVolumePath">第一卷。</param>
    /// <param name="orderings">后续卷的候选排列（每一组都不含第一卷）。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <param name="preferredWorkRoot">
    /// 试开临时物的落点（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，用户 2026-09-30 口径）。
    /// <b>没有它 / 它跨卷 ⇒ 一次都不试</b>：⛔ 不退到"源卷根下的 <c>.ArchiveFixer.work</c>"
    /// （工作区只准设在解压的地方，在源盘开目录正是不变量 12 禁止的形态；本机 C:\ 与 E:\ 根上
    /// 那两个空壳就是这条老路留下的），⛔ 也不改用程序目录 / 临时目录。
    /// 两档都如实报"无法确认"（<see cref="VolumeProbeOutcome.SkipReason"/> =
    /// <see cref="VolumeTrialSkipReason.NoProbeRoot"/> / <see cref="VolumeTrialSkipReason.CrossVolume"/>）。
    /// </param>
    public Task<VolumeProbeOutcome> VerifyAsync(
        string? firstVolumePath,
        IReadOnlyList<IReadOnlyList<VolumeCandidate>>? orderings,
        CancellationToken cancellationToken = default,
        string? preferredWorkRoot = null,
        VolumeContentFormat family = VolumeContentFormat.Unknown,
        bool testVolumes = false,
        IReadOnlyList<string>? passwordCandidates = null,
        IProgress<ArchiveProgress>? progress = null,
        Action<ArchiveStallNotice>? stalled = null) =>
        _candidates.VerifyAsync(
            firstVolumePath,
            orderings,
            cancellationToken,
            preferredWorkRoot,
            family,
            testVolumes,
            passwordCandidates,
            progress,
            stalled);

    /// <summary>
    /// **跨盘 zip 的"试拼定序"**（用户 2026-10-01 的专属算法里，最后一小块拼图）。
    ///
    /// <para>什么时候需要它：末片的中央目录把**大部分**片都钉住了（<c>SpannedZipIndex.Pin</c>：
    /// 每一片都有自己的"身份证"—— 中央目录写着它在哪个偏移上该有一个本地头），
    /// 但**有几片里一个条目都没开始**（整段落在某个文件的数据中间）⇒ 那几片的先后在字节上完全对称，
    /// 内容里没有任何信息。剩下的可能性只有几种排列 —— 那就**真的试一遍**。</para>
    ///
    /// <para><b>为什么必须"测试"（<c>t</c>）而不是"列目录"</b>：跨盘 zip 的中央目录在**末片**里、
    /// 而且不加密 —— 列目录**根本不看中间几片的数据**，顺序错了照样列得出来（实测）。
    /// 只有真的解码一遍才会碰到 CRC：顺序错了必然报数据错 / CRC 错。</para>
    ///
    /// <para>⛔ 与 <see cref="VerifyAsync"/> 同一套红线：**没有工作区根 / 跨盘 ⇒ 一次都不试**；
    /// ⛔ 绝不复制（硬链接零字节、瞬时）；⛔ 密码只作为引擎参数传下去，**绝不进日志 / 绝不进结论**。</para>
    /// </summary>
    /// <param name="candidateOrders">
    /// 待验证的**整组顺序**（每个元素 = 一份完整顺序，第 1 片在最前面、末片在最后）。
    /// ⛔ 调用方只许把"内容已经钉住的部分 + 剩下那几片的排列"放进来，**不许**拿它当暴力搜索。
    /// </param>
    /// <param name="passwordCandidates">密码候选（AES 加密的跨盘 zip：只有对的密码才验得出顺序）。</param>
    /// <param name="preferredWorkRoot">本单的目标工作区根（同 <see cref="VerifyAsync"/>）。</param>
    /// <param name="cancellationToken">取消。</param>
    public Task<VolumeProbeOutcome> VerifySpannedZipOrderAsync(
        IReadOnlyList<IReadOnlyList<string>>? candidateOrders,
        IReadOnlyList<string>? passwordCandidates,
        string? preferredWorkRoot,
        CancellationToken cancellationToken = default) =>
        _candidates.VerifySpannedZipOrderAsync(candidateOrders, passwordCandidates, preferredWorkRoot, cancellationToken);

    /// <summary>
    /// **通用分片（<c>X.zip.001..00N</c>）的顺序验证**（用户 2026-10-18 口径）。
    ///
    /// <para>为什么必须用 <c>TestAsync</c> 而不是 <c>ListAsync</c>：通用分片的中央目录在**末片**里，
    /// 列目录根本不读中间片的数据 —— 顺序错了照样列得出来（实测）。只有真的解一遍（引擎逐条目核对 CRC）
    /// 才能回答"这个顺序对不对"。</para>
    ///
    /// <para>⛔ 与 <see cref="VerifySpannedZipOrderAsync"/> 同一套红线：没有工作区根 / 跨盘 ⇒ 一次都不试；
    /// 只在试开目录里建**硬链接**（零字节），收工把整棵目录删掉；⛔ 绝不复制、⛔ 绝不改源文件。</para>
    /// </summary>
    /// <param name="candidateOrders">待验证的整组顺序（每个元素 = 第 1 片 … 末片）。</param>
    /// <param name="preferredWorkRoot">本单的目标工作区根（同 <see cref="VerifyAsync"/>）。</param>
    /// <param name="cancellationToken">取消。</param>
    public Task<VolumeProbeOutcome> VerifyNumberedZipOrderAsync(
        IReadOnlyList<IReadOnlyList<string>>? candidateOrders,
        string? preferredWorkRoot,
        CancellationToken cancellationToken = default) =>
        _candidates.VerifyNumberedZipOrderAsync(candidateOrders, preferredWorkRoot, cancellationToken);

    /// <summary>
    /// <see cref="VolumeGroupResolver"/> 要的那一个试开出口：把请求翻译成本类的硬链接试开，
    /// 再把结论折成 <see cref="VolumeTrialOutcome"/>。
    ///
    /// <para>⛔ <b>只读</b>：源文件一个字节都不动 —— 只在试开目录里建**硬链接**（零字节、瞬时），
    /// 收工把整棵试开目录删掉。这是"试开确认"能当最高权重证据的前提。</para>
    /// </summary>
    public Task<VolumeTrialOutcome> TryOpenAsync(
        VolumeTrialRequest request,
        CancellationToken cancellationToken = default) =>
        _candidates.TryOpenAsync(request, cancellationToken);
}
