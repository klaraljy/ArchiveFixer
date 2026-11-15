using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **试开候选层**：先回答"这一单能不能试开、要试的是哪一组卷/顺序"，再逐个候选真的试一次。
///
/// <para>准入判据（第一卷还在 / 顺序凑得出 / 引擎可用 / 有工作区根 / 同卷 / 试开目录可建）
/// 与卷候选的组装都只在这里；⛔ 试开顺序、判据、措辞与拆分前逐字一致。</para>
///
/// <para>职责分工（本类只做候选与驱动，落地动作都在协作类里）：</para>
/// <list type="bullet">
/// <item><description>硬链接 / 试开目录 / 文件大小 / 清场 → <see cref="VolumeProbeEvidence"/>；</description></item>
/// <item><description>引擎调用（列目录 / 完整性测试）→ <see cref="VolumeProbeEngineCalls"/>；</description></item>
/// <item><description>引擎结果判读 → <see cref="VolumeProbeJudgment"/>；</description></item>
/// <item><description>拒绝结论与「没试 ≠ 不成立」的包装 → <see cref="VolumeProbeFailures"/>。</description></item>
/// </list>
/// </summary>
internal sealed class VolumeProbeCandidates
{
    private readonly VolumeProbeEngineCalls _engineCalls;

    internal VolumeProbeCandidates(IArchiveEngine engine)
    {
        _engineCalls = new VolumeProbeEngineCalls(engine);
    }

    /// <summary>最后一次试开失败的原因（只为把话说清，不参与判定）。</summary>
    private string LastFailure { get; set; } = "没有可用信息";

    /// <summary>
    /// 先确认"第一卷自己打不开"（打得开 = 它本身就是个完整压缩包，不是分卷的第一卷，那就什么都别做），
    /// 再逐个假设顺序试开。
    /// </summary>
    internal async Task<VolumeProbeOutcome> VerifyAsync(
        string? firstVolumePath,
        IReadOnlyList<IReadOnlyList<VolumeCandidate>>? orderings,
        CancellationToken cancellationToken,
        string? preferredWorkRoot,
        VolumeContentFormat family,
        bool testVolumes,
        IReadOnlyList<string>? passwordCandidates,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled)
    {
        cancellationToken.ThrowIfCancellationRequested();

        VolumeProbeOutcome? refusal = AdmitGroupTrial(
            firstVolumePath,
            orderings,
            preferredWorkRoot,
            out string firstVolume,
            out IReadOnlyList<IReadOnlyList<VolumeCandidate>> admittedOrderings,
            out string probeRoot);

        if (refusal != null)
        {
            return refusal;
        }

        int attempts = 0;

        try
        {
            if (!VolumeProbeEvidence.TryCreateDirectory(probeRoot, out string directoryError))
            {
                return VolumeProbeFailures.Refuse(
                    0,
                    VolumeProbeFailures.ProbeDirectoryCreateFailed("试开", directoryError),
                    attempted: false,
                    VolumeTrialSkipReason.NoProbeRoot);
            }

            // ① 单独一卷能不能完整打开。能 → 它本来就是个完整包，改名只会把它弄坏。
            VolumeProbeOutcome? solo = await TrySoloAsync(firstVolume, probeRoot, cancellationToken, family)
                .ConfigureAwait(false);

            if (solo != null)
            {
                return VolumeProbeFailures.WithAttempted(solo, attempted: true);
            }

            // ② 逐个假设顺序试开：第一个"列得出来"的顺序就是它。
            foreach (IReadOnlyList<VolumeCandidate> ordering in admittedOrderings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string trialDirectory = Path.Combine(
                    probeRoot,
                    "try-" + attempts.ToString(CultureInfo.InvariantCulture));
                attempts++;

                var volumes = new List<VolumeCandidate>
                {
                    new() { Path = firstVolume, Size = VolumeProbeEvidence.SizeOf(firstVolume) }
                };
                volumes.AddRange(ordering);

                VolumeProbeTrialResult trial = await TryLinkAndListAsync(
                        volumes,
                        trialDirectory,
                        cancellationToken,
                        family,
                        testVolumes,
                        passwordCandidates,
                        progress,
                        stalled)
                    .ConfigureAwait(false);

                if (trial.Failure == null)
                {
                    return new VolumeProbeOutcome
                    {
                        Confirmed = true,
                        Attempted = true,
                        NeedsPassword = trial.EncryptedArchive,
                        OrderedVolumes = volumes,
                        Attempts = attempts,
                        Reason = trial.EncryptedArchive
                            ? $"按试开验证的顺序成立了（试了 {attempts} 种排列；引擎认出了这一组是一份"
                              + "「文件名也加密」的归档，给不出密码就读不出清单）"
                            : $"按试开验证的顺序成立了（试了 {attempts} 种排列）"
                    };
                }

                LastFailure = trial.Failure;
            }

            return VolumeProbeFailures.Refuse(
                attempts,
                VolumeProbeFailures.AllOrderingsFailed(attempts, LastFailure),
                attempted: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VolumeProbeFailures.Refuse(
                attempts,
                VolumeProbeFailures.TrialFailed("试开", ex.Message),
                attempted: attempts > 0);
        }
        finally
        {
            VolumeProbeEvidence.TryDelete(probeRoot);
        }
    }

    /// <summary>
    /// **跨盘 zip 的"试拼定序"**：末片的中央目录把大部分片钉住了，但有几位在字节上完全对称 ——
    /// 那几片的先后只能真的解一遍才回答（列目录不看中间片的数据，顺序错了照样列得出来）。
    /// </summary>
    internal async Task<VolumeProbeOutcome> VerifySpannedZipOrderAsync(
        IReadOnlyList<IReadOnlyList<string>>? candidateOrders,
        IReadOnlyList<string>? passwordCandidates,
        string? preferredWorkRoot,
        CancellationToken cancellationToken)
    {
        VolumeProbeOutcome? refusal = AdmitZipOrder(
            candidateOrders,
            preferredWorkRoot,
            "试拼",
            "试拼",
            out IReadOnlyList<IReadOnlyList<string>> admittedOrders,
            out string probeRoot);

        if (refusal != null)
        {
            return refusal;
        }

        var candidates = (passwordCandidates ?? Array.Empty<string>())
            .Where(p => p != null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            // 没给候选就按"空密码"试一次：不加密的包这一档就够（加密包会如实报密码不对）。
            candidates.Add(string.Empty);
        }

        int attempts = 0;

        try
        {
            if (!VolumeProbeEvidence.TryCreateDirectory(probeRoot, out string directoryError))
            {
                return VolumeProbeFailures.Refuse(
                    0,
                    VolumeProbeFailures.ProbeDirectoryCreateFailed("试拼", directoryError),
                    attempted: false,
                    VolumeTrialSkipReason.NoProbeRoot);
            }

            string trialDirectory = Path.Combine(probeRoot, "zip-order");
            string lastFailure = "没有可用信息";
            int wrongPasswordOnly = 0;

            /*
             * 密码只活在这个局部变量里（⛔ 不进日志、不进结论、不进 Reason）。
             *
             * 为什么要记它：加密的跨盘 zip 里，"顺序错"与"密码错"在引擎那儿是两句话 ——
             * 一旦某次失败**不是**密码错，就说明这个密码已经解开了数据（错的只是顺序），
             * 后面那几个顺序只要拿它试一次就够了，不必把密码本重跑一遍
             * （一组 675 MB 的包，每多试一个密码就是多解一遍）。
             */
            string? knownPassword = null;

            foreach (IReadOnlyList<string> order in admittedOrders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? tailLink = VolumeProbeEvidence.LinkAsSpannedZip(order, trialDirectory);

                if (tailLink == null)
                {
                    return VolumeProbeFailures.Refuse(
                        attempts,
                        VolumeProbeFailures.LinkNotPossibleForSpannedOrder,
                        attempted: true);
                }

                IReadOnlyList<string> toTry = knownPassword != null ? new[] { knownPassword } : candidates;

                foreach (string password in toTry)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    attempts++;

                    ArchiveOperationResult test = await _engineCalls
                        .TestAsync(tailLink, password, progress: null, stalled: null, cancellationToken)
                        .ConfigureAwait(false);

                    if (VolumeProbeJudgment.IsSuccess(test))
                    {
                        return new VolumeProbeOutcome
                        {
                            Confirmed = true,
                            Attempted = true,
                            OrderedVolumes = order
                                .Select(p => new VolumeCandidate { Path = p, Size = VolumeProbeEvidence.SizeOf(p) })
                                .ToList(),
                            Attempts = attempts,
                            Reason = $"按这个顺序真的解了一遍（引擎测过每个条目的 CRC，全对；试了 {attempts} 次）"
                        };
                    }

                    if (VolumeProbeJudgment.IsPasswordFailure(test))
                    {
                        wrongPasswordOnly++;
                    }
                    else
                    {
                        knownPassword ??= password;
                    }

                    lastFailure = VolumeProbeFailures.PreferLastFailure(lastFailure, test.Message);
                }
            }

            bool passwordBlocked = wrongPasswordOnly == attempts && knownPassword == null;

            return VolumeProbeFailures.Refuse(
                attempts,
                passwordBlocked
                    ? VolumeProbeFailures.PasswordBlockedForSpannedOrder
                    : VolumeProbeFailures.OrdersNotConfirmed(lastFailure),
                attempted: true,
                needsPassword: passwordBlocked);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VolumeProbeFailures.Refuse(
                attempts,
                VolumeProbeFailures.TrialFailed("试拼", ex.Message),
                attempted: attempts > 0);
        }
        finally
        {
            VolumeProbeEvidence.TryDelete(probeRoot);
        }
    }

    /// <summary>
    /// **通用分片（<c>X.zip.001..00N</c>）的顺序验证**：中央目录在末片、列目录不读中间片，
    /// 所以只能真的解一遍（引擎逐条目核对 CRC）。
    /// </summary>
    internal async Task<VolumeProbeOutcome> VerifyNumberedZipOrderAsync(
        IReadOnlyList<IReadOnlyList<string>>? candidateOrders,
        string? preferredWorkRoot,
        CancellationToken cancellationToken)
    {
        VolumeProbeOutcome? refusal = AdmitZipOrder(
            candidateOrders,
            preferredWorkRoot,
            "试开",
            "按顺序试开",
            out IReadOnlyList<IReadOnlyList<string>> admittedOrders,
            out string probeRoot);

        if (refusal != null)
        {
            return refusal;
        }

        int attempts = 0;
        string lastFailure = "没有可用信息";
        bool passwordBlocked = false;

        try
        {
            if (!VolumeProbeEvidence.TryCreateDirectory(probeRoot, out string directoryError))
            {
                return VolumeProbeFailures.Refuse(
                    0,
                    VolumeProbeFailures.ProbeDirectoryCreateFailed("试开", directoryError),
                    attempted: false,
                    VolumeTrialSkipReason.NoProbeRoot);
            }

            foreach (IReadOnlyList<string> order in admittedOrders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string trialDirectory = Path.Combine(
                    probeRoot,
                    "zip-numbered-" + attempts.ToString(CultureInfo.InvariantCulture));
                attempts++;

                string? entryLink = VolumeProbeEvidence.LinkAsNumberedZip(order, trialDirectory);

                if (entryLink == null)
                {
                    return VolumeProbeFailures.Refuse(
                        attempts,
                        VolumeProbeFailures.LinkNotPossibleForNumberedOrder,
                        attempted: true);
                }

                /*
                 * 通用分片这一组在样本里是**不加密**的（加密的是更里层的包）。空密码这一档就够；
                 * 真遇到加密的这一层，引擎会明确报密码错 —— 那一档**不拿来当"顺序不对"的证据**
                 * （顺序错了与密码错了在引擎那里是同一句话），直接如实报"没有可用密码"。
                 */
                ArchiveOperationResult test = await _engineCalls
                    .TestAsync(entryLink, string.Empty, progress: null, stalled: null, cancellationToken)
                    .ConfigureAwait(false);

                if (VolumeProbeJudgment.IsSuccess(test))
                {
                    return new VolumeProbeOutcome
                    {
                        Confirmed = true,
                        Attempted = true,
                        OrderedVolumes = order
                            .Select(path => new VolumeCandidate { Path = path, Size = VolumeProbeEvidence.SizeOf(path) })
                            .ToList(),
                        Attempts = attempts,
                        Reason = $"按这个顺序真的解了一遍（引擎测过每个条目的 CRC，全对；试了 {attempts} 次）"
                    };
                }

                lastFailure = VolumeProbeFailures.PreferLastFailure(lastFailure, test.Message);

                if (VolumeProbeJudgment.IsPasswordFailure(test))
                {
                    passwordBlocked = true;
                    break;
                }
            }

            return VolumeProbeFailures.Refuse(
                attempts,
                passwordBlocked
                    ? VolumeProbeFailures.PasswordBlockedForNumberedOrder
                    : VolumeProbeFailures.OrdersNotConfirmed(lastFailure),
                attempted: true,
                needsPassword: passwordBlocked);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return VolumeProbeFailures.Refuse(
                attempts,
                VolumeProbeFailures.TrialFailed("试开定序", ex.Message),
                attempted: attempts > 0);
        }
        finally
        {
            VolumeProbeEvidence.TryDelete(probeRoot);
        }
    }

    /// <summary>
    /// <see cref="VolumeGroupResolver"/> 要的那一个试开出口：把请求翻译成本层的硬链接试开，
    /// 再把结论折成 <see cref="VolumeTrialOutcome"/>。
    /// </summary>
    internal async Task<VolumeTrialOutcome> TryOpenAsync(
        VolumeTrialRequest? request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return new VolumeTrialOutcome { Attempted = false, Reason = "试开请求为空" };
        }

        VolumeProbeOutcome outcome = await VerifyAsync(
                request.FirstVolumePath,
                request.Orderings,
                cancellationToken,
                request.PreferredWorkRootDirectory,
                VolumeContentFormat.Unknown,
                testVolumes: false,
                passwordCandidates: null,
                progress: null,
                stalled: null)
            .ConfigureAwait(false);

        return VolumeProbeJudgment.ToTrialOutcome(outcome);
    }

    /// <summary>
    /// 整组试开的准入：第一卷 / 顺序 / 引擎 / 工作区根 / 同卷 / 试开目录。
    /// 返回非 null = 这一档不试（<see cref="VolumeProbeOutcome.Attempted"/> 为 false）。
    /// </summary>
    private VolumeProbeOutcome? AdmitGroupTrial(
        string? firstVolumePath,
        IReadOnlyList<IReadOnlyList<VolumeCandidate>>? orderings,
        string? preferredWorkRoot,
        out string firstVolume,
        out IReadOnlyList<IReadOnlyList<VolumeCandidate>> admittedOrderings,
        out string probeRoot)
    {
        firstVolume = string.Empty;
        admittedOrderings = orderings ?? Array.Empty<IReadOnlyList<VolumeCandidate>>();
        probeRoot = string.Empty;

        if (string.IsNullOrWhiteSpace(firstVolumePath) || !File.Exists(firstVolumePath))
        {
            return VolumeProbeFailures.FirstVolumeGone();
        }

        firstVolume = firstVolumePath;

        if (orderings == null || orderings.Count == 0)
        {
            return VolumeProbeFailures.NoGroupOrderings();
        }

        if (!_engineCalls.IsAvailable)
        {
            return VolumeProbeFailures.EngineUnavailable("试开");
        }

        /*
         * ⛔ 没有工作区根 ⇒ **一次都不试**（用户 2026-09-30 红线：「工作区就设在解压的地方，
         * 这就完全不存在跨盘的操作」）。
         *
         * 以前这一档会退到"第一卷所在卷根下的 .ArchiveFixer.work"—— 那就是"专门在用户盘上开一个
         * 工作区"，本机实测 C:\ 与 E:\ 根上那两个空壳就是它留下的。整条删掉，⛔ 也不许改退到
         * 程序目录 / 临时目录：证不出来就如实说"无法确认"，宁可什么都不做。
         */
        if (string.IsNullOrWhiteSpace(preferredWorkRoot))
        {
            return VolumeProbeFailures.NoWorkRoot("试开", "整组是齐的");
        }

        /*
         * ⛔ 跨盘 = 一次都不试（用户 2026-09-30 决定，见 VerifyAsync 的说明）。
         * 判据是"两个路径在不在同一个卷"，⛔ 不去比文案、也不试一次看看行不行 ——
         * 试开的第一步就是在那个目录里建东西，那正是被否决的动作。
         */
        if (!VolumeContentInference.IsSameVolumeRoot(firstVolumePath, preferredWorkRoot))
        {
            return VolumeProbeFailures.CrossVolume("试开", "整卷是否齐全");
        }

        probeRoot = VolumeContentInference.BuildProbeRoot(firstVolumePath, preferredWorkRoot);

        if (probeRoot.Length == 0)
        {
            return VolumeProbeFailures.NoProbeRoot("试开");
        }

        admittedOrderings = orderings;

        return null;
    }

    /// <summary>
    /// 顺序试开 / 试拼的准入：顺序合法 / 引擎 / 工作区根 / 同卷 / 试开目录。
    /// 返回非 null = 这一档不试；<paramref name="verb"/> 只影响措辞（试开 / 试拼），
    /// <paramref name="incompleteAction"/> = "还没凑齐"那句话的动词（通用分片是"按顺序试开"）。
    /// </summary>
    private VolumeProbeOutcome? AdmitZipOrder(
        IReadOnlyList<IReadOnlyList<string>>? candidateOrders,
        string? preferredWorkRoot,
        string verb,
        string incompleteAction,
        out IReadOnlyList<IReadOnlyList<string>> admittedOrders,
        out string probeRoot)
    {
        admittedOrders = candidateOrders ?? Array.Empty<IReadOnlyList<string>>();
        probeRoot = string.Empty;

        if (candidateOrders == null || candidateOrders.Count == 0)
        {
            return VolumeProbeFailures.NoCandidateOrders();
        }

        IReadOnlyList<string> firstOrder = candidateOrders[0];

        if (firstOrder.Count < 2)
        {
            return VolumeProbeFailures.IncompleteOrder(incompleteAction);
        }

        foreach (IReadOnlyList<string> order in candidateOrders)
        {
            if (order.Count != firstOrder.Count)
            {
                return VolumeProbeFailures.OrderCountMismatch(verb);
            }

            foreach (string volume in order)
            {
                if (string.IsNullOrWhiteSpace(volume) || !File.Exists(volume))
                {
                    return VolumeProbeFailures.OrderPieceMissing(verb);
                }
            }
        }

        if (!_engineCalls.IsAvailable)
        {
            return VolumeProbeFailures.EngineUnavailable(verb);
        }

        if (string.IsNullOrWhiteSpace(preferredWorkRoot))
        {
            return VolumeProbeFailures.NoWorkRoot(verb, "顺序");
        }

        if (!VolumeContentInference.IsSameVolumeRoot(firstOrder[0], preferredWorkRoot))
        {
            return VolumeProbeFailures.CrossVolume(verb, "顺序");
        }

        probeRoot = VolumeContentInference.BuildProbeRoot(firstOrder[0], preferredWorkRoot);

        if (probeRoot.Length == 0)
        {
            return VolumeProbeFailures.NoProbeRoot(verb);
        }

        admittedOrders = candidateOrders;

        return null;
    }

    /// <summary>单独一卷试开：返回非 null = **已经可以下结论了**（自己就是完整包 → 不该改名）。</summary>
    private async Task<VolumeProbeOutcome?> TrySoloAsync(
        string firstVolumePath,
        string probeRoot,
        CancellationToken cancellationToken,
        VolumeContentFormat family)
    {
        string soloDirectory = Path.Combine(probeRoot, "solo");
        var solo = new List<VolumeCandidate>
        {
            new() { Path = firstVolumePath, Size = VolumeProbeEvidence.SizeOf(firstVolumePath) }
        };

        VolumeProbeTrialResult trial = await TryLinkAndListAsync(solo, soloDirectory, cancellationToken, family)
            .ConfigureAwait(false);

        /*
         * ⛔ 「单卷就报加密归档」必须**拒绝**（用户 2026-09-29 真机副本取证）：
         * 7z 的"下一份头"写在**最后一卷**里，位置由第一张卷第 0 字节的 Start Header 给出；
         * 单卷试开就报"头加密"，说明那一份头**就在这个文件里面** —— 它本身就是个**完整**的加密包，
         * 不是分卷的第一卷（把它当第一卷改名只会把它弄坏）。
         * 反过来，"分卷的第一卷独自一人"读不到头（头在后面的卷里），报的是"打不开 / 通用分片"。
         */
        if (trial.EncryptedArchive)
        {
            return VolumeProbeFailures.Refuse(
                0,
                "第一卷自己就能完整打开（文件名也加密，没有密码读不出清单）"
                + " —— 它本身就是个完整的压缩包，不是分卷的第一卷（不动它）",
                attempted: true);
        }

        if (trial.Failure != null)
        {
            // 打不开 → 正是"后面还有卷"的样子，可以接着试排列。
            return null;
        }

        return VolumeProbeFailures.Refuse(
            0,
            "第一卷自己就能完整打开 —— 它本身就是个完整的压缩包，不是分卷的第一卷（不动它）",
            attempted: true);
    }

    /// <summary>
    /// 按假设名把这几卷硬链接进 <paramref name="directory"/> 并让引擎列一次（或完整测一遍）；
    /// 返回 <c>Failure == null</c> = 成立（顺序成立，或引擎认出了"这一组是加密归档"）。
    /// </summary>
    private async Task<VolumeProbeTrialResult> TryLinkAndListAsync(
        IReadOnlyList<VolumeCandidate> volumes,
        string directory,
        CancellationToken cancellationToken,
        VolumeContentFormat family = VolumeContentFormat.Unknown,
        bool testVolumes = false,
        IReadOnlyList<string>? passwordCandidates = null,
        IProgress<ArchiveProgress>? progress = null,
        Action<ArchiveStallNotice>? stalled = null)
    {
        if (!VolumeProbeEvidence.TryCreateDirectory(directory, out string directoryError))
        {
            return new VolumeProbeTrialResult(
                VolumeProbeFailures.ProbeDirectoryCreateFailed("试开", directoryError),
                false);
        }

        if (!VolumeProbeEvidence.TryLinkVolumes(volumes, directory, family, out string firstLink, out string linkFailure))
        {
            return new VolumeProbeTrialResult(linkFailure, false);
        }

        if (testVolumes)
        {
            IReadOnlyList<string> candidates = passwordCandidates is { Count: > 0 }
                ? passwordCandidates
                : new[] { string.Empty };
            string lastFailure = "试开不成立";

            foreach (string password in candidates)
            {
                ArchiveOperationResult test = await _engineCalls
                    .TestAsync(firstLink, password, progress, stalled, cancellationToken)
                    .ConfigureAwait(false);

                if (VolumeProbeJudgment.IsSuccess(test))
                {
                    return new VolumeProbeTrialResult(null, false);
                }

                lastFailure = VolumeProbeFailures.PreferLastFailure(lastFailure, test.Message);

                if (!VolumeProbeJudgment.MayRetryWithNextPassword(test))
                {
                    break;
                }
            }

            return new VolumeProbeTrialResult(lastFailure, false);
        }

        ArchiveListResult list = await _engineCalls
            .ListAsync(firstLink, progress, stalled, cancellationToken)
            .ConfigureAwait(false);

        return VolumeProbeJudgment.JudgeList(list);
    }
}
