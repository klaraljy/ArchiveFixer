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
    /// <summary>整组试开（数字 ZIP / 跨盘 ZIP 尾巴 + 排序试开；第 10 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        private async Task<GroupOrderTrialResult> ProbeGatherGroupAsync(
            IReadOnlyList<string> pieces,
            IReadOnlyList<string> candidatePool,
            string entryLayer,
            string? cardEntryPath,
            string referenceName,
            bool allowFallback,
            CancellationToken cancellationToken)
        {
            if (_archiveEngine == null || !_archiveEngine.IsAvailable)
            {
                return GroupOrderTrialResult.Fail("硬链接试开没做成：当前没有可用的解压引擎 ⇒ 整组不动");
            }

            string referenceBase = FileNameHelper.GetArchiveBaseName(referenceName);

            if (referenceBase.Length == 0
                && !string.IsNullOrWhiteSpace(cardEntryPath)
                && File.Exists(cardEntryPath))
            {
                try
                {
                    referenceBase = FileNature.Inspect(cardEntryPath).ArchiveBaseName;
                }
                catch
                {
                    // 读不出参照基名 ⇒ 下面按名字那把尺子兜底。
                }
            }

            /*
             * ===== 通用分片（`X.zip.001..00N`）的**内容试开定序**（用户 2026-10-18 口径）=====
             *
             * 只有这一族需要"猜中间片"：RAR / 7z / 跨盘 ZIP 的卷号由内容或名字明确给出；通用分片的中间片
             * 内容里没有卷号，名字被伪装时也读不出次序。做法（用户原话）：先按后缀相似度排一个高概率顺序，
             * 试开；试不出再把剩余排列全试一遍；全不成立 ⇒ 判不出（这一趟先不收）。
             *
             * ⛔ 候选只从"内容确认是这一组"的卡里来，并按**盘上物理身份**去重（同一份文件的多个名字只算一片）；
             * ⛔ 片数上限 5（用户口径：超过就不猜）。
             */
            List<FileNatureCard> numberedCandidates = CollectNumberedZipTrialPieces(
                pieces,
                candidatePool,
                referenceBase,
                referenceName);

            if (numberedCandidates.Count >= 2)
            {
                AppendLog(
                    "INFO",
                    "试开定序候选 —— "
                    + string.Join(
                        "、",
                        numberedCandidates.Select(card =>
                            $"{FileNameHelper.GetFileName(card.Path)}〔F={card.Format} EOCD={card.ZipEndRecordMeansSplit} "
                            + $"LH={card.HasZipLocalHeader} 成员={card.Membership} 基名={card.ArchiveBaseName}〕")));
            }

            /*
             * ⛔ 只有**内容判成通用分片**（`ZipNumbered`）才走这一档：
             * 跨盘 ZIP 的入口是末片 `.zip`、片名 `.z01/.z02`，它的顺序本来就由名字与内容盘号明确给出
             * —— 拿通用分片的 `.zip.00N` 去试开，会在真实跨盘组旁边再多造一套名字（15151515 那类回归）。
             */
            VolumeNamingFamily? contentFamily = numberedCandidates.Count >= 2
                ? VolumePiecesFromCards.Read(numberedCandidates).NamingFamily
                : null;

            if (!allowFallback
                && contentFamily == VolumeNamingFamily.ZipNumbered
                && numberedCandidates.Count > VolumeOrderTrial.MaxPieces)
            {
                /*
                 * 超过 5 片：按用户口径**不猜顺序**；但"不猜"不等于"把整组判死"——
                 * 名字/内容卡已经能归出完整组的，照旧走原来的收拢/解压路，⛔ 不置
                 * `contentOrderUndetermined`（2026-10-18 全量批 `19191919` 现场：
                 * 同一组在别的任务上已经可证完整，被这一条的"适用失败"压住了）。
                 */
                return new GroupOrderTrialResult
                {
                    Applicable = false,
                    Reason = $"这一组有 {numberedCandidates.Count} 份不同的片（超过 {VolumeOrderTrial.MaxPieces} 片）⇒ 不试开顺序，照旧按名字/内容收拢"
                };
            }

            if (!allowFallback
                && contentFamily == VolumeNamingFamily.ZipNumbered
                && numberedCandidates.Count >= 2)
            {
                string? tailPath = ResolveNumberedZipTail(numberedCandidates);
                string stem = ResolveOrderTrialStem(numberedCandidates, tailPath, referenceBase);

                /*
                 * 末片自述"共 N 片"而候选只有 M&lt;N 份 ⇒ 这一趟**不是"顺序试不出"，是候选还没到齐**
                 * （2026-10-18 新批 `29292929`：一个任务只看见 2 片，按"顺序失败"把整组压住，后面
                 * 另一个任务已经看见 3 片并试开成功 ⇒ 不能再让少片那一次形成判决）。
                 * 返回"不适用" ⇒ 调用方照旧走名字/收拢，不置 `contentOrderUndetermined`。
                 */
                int declaredPieces = numberedCandidates
                    .FirstOrDefault(card => string.Equals(card.Path, tailPath, StringComparison.OrdinalIgnoreCase))
                    ?.DeclaredVolumeCount ?? 0;

                if (declaredPieces > numberedCandidates.Count)
                {
                    return new GroupOrderTrialResult
                    {
                        Applicable = false,
                        Reason = $"只看见 {numberedCandidates.Count} 片，末片自述 {declaredPieces} 片 ⇒ 候选还没到齐，不做顺序判决"
                    };
                }

                if (tailPath == null || stem.Length == 0)
                {
                    return GroupOrderTrialResult.Fail(
                        "硬链接试开定序没做成：末片（内容带 EOCD 的那一片）或基名判不出 ⇒ 整组不动",
                        applicable: true);
                }

                IReadOnlyList<IReadOnlyList<string>> orders = VolumeOrderTrial.BuildCandidateOrders(
                    numberedCandidates.Select(card => card.Path).ToList(),
                    tailPath,
                    VolumeNamingFamily.ZipNumbered,
                    stem);

                if (orders.Count == 0)
                {
                    return GroupOrderTrialResult.Fail(
                        "硬链接试开定序没做成：排不出可试的顺序 ⇒ 整组不动",
                        applicable: true);
                }

                AppendLog(
                    "INFO",
                    $"试开定序 —— 内容确认的候选 {numberedCandidates.Count} 份"
                    + "（同一份文件的多个名字只算一份）"
                    + $"｜末片=「{FileNameHelper.GetFileName(tailPath)}」"
                    + $"｜族=通用分片｜基名=「{stem}」｜可试顺序 {orders.Count} 种");

                var verifier = new VolumeProbeVerifier(_archiveEngine);
                VolumeProbeOutcome outcome = await verifier.VerifyNumberedZipOrderAsync(
                        orders,
                        Path.Combine(entryLayer, ".ArchiveFixer.work"),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!outcome.Confirmed)
                {
                    /*
                     * ⛔ **加密组不能拿"试开没通过"当"顺序判不出"**（2026-10-18 `EncryptedVolumePipelineTests`
                     * 回归）：空密码试开必然报密码错，而那是"没有密码"，不是"顺序不对"。
                     * ⇒ 这一档退回原来的密码/名字流程（照常报「密码错误」），⛔ 不把整组判死。
                     */
                    if (outcome.NeedsPassword)
                    {
                        return new GroupOrderTrialResult
                        {
                            Applicable = false,
                            Reason = "这一组是加密的，试开定序没有可用密码 ⇒ 退回原来的密码/名字流程"
                        };
                    }

                    return GroupOrderTrialResult.Fail(
                        "硬链接试开定序没通过（引擎真的解了一遍，每个条目还是对不上）⇒ 整组不动："
                        + outcome.Reason,
                        applicable: true);
                }

                List<string> winning = outcome.OrderedVolumes
                    .Select(volume => volume.Path)
                    .ToList();

                /*
                 * ⛔ 这里**只交顺序、不自己建链接**：接片仍走既有出口 `TryGatherGroupPiecesInto`
                 * （它会记"谁吐出的片、谁借的片"，批末收场才能把产出方那一份按档收干净）。
                 * 自己建链接会绕过那本账 ⇒ 产出方那一份变成残留（2026-10-18 `19191919` / `6666` 的 c3 现场）。
                 */
                return new GroupOrderTrialResult
                {
                    Applicable = true,
                    Confirmed = true,
                    OrderedPieces = winning,
                    Stem = stem,
                    Reason = $"内容试开定序成立（第 {outcome.Attempts} 种顺序）—— 按这个顺序把这一组接进入口那一层"
                        + "（零字节硬链接，原件一个字节不动）"
                };
            }

            /*
             * ===== 跨盘 ZIP：末片 + 中间片的内容试开定序 =====
             * 新批 `24242424`（2026-10-18 运行期逐字）：末片自述"共 3 片"，而两个中段片的名字是
             * `.pdf` / `.zip`、内容一个判不出；名字族给不出次序 ⇒ 与通用分片同一套高概率顺序，
             * 只是硬链接名换成跨盘族 `.z01/.z02/.zip`，验证仍是真的解一遍（逐条目 CRC）。
             */
            if (contentFamily == VolumeNamingFamily.ZipSpanned
                && numberedCandidates.Count >= 2
                && !allowFallback)
            {
                if (numberedCandidates.Count > VolumeOrderTrial.MaxPieces)
                {
                    return new GroupOrderTrialResult
                    {
                        Applicable = false,
                        Reason = $"这一组有 {numberedCandidates.Count} 份不同的片（超过 {VolumeOrderTrial.MaxPieces} 片）⇒ 不试开顺序，照旧按名字/内容收拢"
                    };
                }

                string? spannedTail = ResolveSpannedZipTail(numberedCandidates);
                string spannedStem = ResolveOrderTrialStem(numberedCandidates, spannedTail, referenceBase);

                int spannedDeclaredPieces = numberedCandidates
                    .FirstOrDefault(card => string.Equals(card.Path, spannedTail, StringComparison.OrdinalIgnoreCase))
                    ?.DeclaredVolumeCount ?? 0;

                if (spannedDeclaredPieces > numberedCandidates.Count)
                {
                    return new GroupOrderTrialResult
                    {
                        Applicable = false,
                        Reason = $"只看见 {numberedCandidates.Count} 片，末片自述 {spannedDeclaredPieces} 片 ⇒ 候选还没到齐，不做顺序判决"
                    };
                }

                if (spannedTail == null || spannedStem.Length == 0)
                {
                    return GroupOrderTrialResult.Fail(
                        "跨盘 ZIP 试开定序没做成：末片（内容自证最后一片）或基名判不出 ⇒ 整组不动",
                        applicable: true);
                }

                IReadOnlyList<IReadOnlyList<string>> spannedOrders = VolumeOrderTrial.BuildCandidateOrders(
                    numberedCandidates.Select(card => card.Path).ToList(),
                    spannedTail,
                    VolumeNamingFamily.ZipSpanned,
                    spannedStem);

                if (spannedOrders.Count == 0)
                {
                    return GroupOrderTrialResult.Fail(
                        "跨盘 ZIP 试开定序没做成：排不出可试的顺序 ⇒ 整组不动",
                        applicable: true);
                }

                AppendLog(
                    "INFO",
                    $"跨盘试开定序 —— 内容确认的候选 {numberedCandidates.Count} 份"
                    + $"｜末片=「{FileNameHelper.GetFileName(spannedTail)}」"
                    + $"｜族=跨盘 ZIP｜基名=「{spannedStem}」｜可试顺序 {spannedOrders.Count} 种");

                var spannedVerifier = new VolumeProbeVerifier(_archiveEngine);
                /*
                 * 先用**空密码**试：这一族很多组本来就没加密码；拿密码本去试反会被引擎报
                 * "Wrong password"（2026-10-18 `15151515` 回归现场）。
                 * 空密码不成立时，再用本批候选试一遍 —— 有些加密组的引擎原话是「缺卷 / 数据错」
                 * 而不是「密码错」，不能靠 `NeedsPassword` 单条判据决定要不要补密码。
                 */
                VolumeProbeOutcome spannedOutcome = await spannedVerifier.VerifySpannedZipOrderAsync(
                        spannedOrders,
                        passwordCandidates: null,
                        Path.Combine(entryLayer, ".ArchiveFixer.work"),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!spannedOutcome.Confirmed)
                {
                    IReadOnlyList<string> spannedPasswords = BuildVolumeProbePasswordCandidates(spannedTail);

                    if (spannedPasswords.Count > 0)
                    {
                        spannedOutcome = await spannedVerifier.VerifySpannedZipOrderAsync(
                                spannedOrders,
                                spannedPasswords,
                                Path.Combine(entryLayer, ".ArchiveFixer.work"),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (!spannedOutcome.Confirmed)
                {
                    if (spannedOutcome.NeedsPassword)
                    {
                        return new GroupOrderTrialResult
                        {
                            Applicable = false,
                            Reason = "这一组跨盘 ZIP 是加密的，试开定序没有可用密码 ⇒ 退回原来的密码/名字流程"
                        };
                    }

                    return GroupOrderTrialResult.Fail(
                        "跨盘 ZIP 硬链接试开定序没通过（引擎真的解了一遍，每个条目还是对不上）⇒ 整组不动："
                        + spannedOutcome.Reason,
                        applicable: true);
                }

                List<string> winningSpanned = spannedOutcome.OrderedVolumes
                    .Select(volume => volume.Path)
                    .ToList();

                return new GroupOrderTrialResult
                {
                    Applicable = true,
                    Confirmed = true,
                    OrderedPieces = winningSpanned,
                    Stem = spannedStem,
                    Family = VolumeNamingFamily.ZipSpanned,
                    Reason = $"跨盘 ZIP 内容试开定序成立（第 {spannedOutcome.Attempts} 种顺序）—— 按这个顺序把这一组接进入口那一层"
                        + "（零字节硬链接，原件一个字节不动）"
                };
            }

            /*
             * 不是"通用分片、片数不多于 5、末片可定"那一档 ⇒ 照旧只按名字给的这一种顺序列一次目录
             * （RAR / 7z / 跨盘 ZIP 的卷序本来就由内容或名字明确给出；这一档一个字没改）。
             * ⛔ 决策器说 Leave 时**不跑这一遍**（没要搬，就别多调一次引擎）。
             */
            if (!allowFallback)
            {
                return new GroupOrderTrialResult { Applicable = false };
            }
            string? firstVolume = !string.IsNullOrWhiteSpace(cardEntryPath) && File.Exists(cardEntryPath)
                ? cardEntryPath
                : pieces.FirstOrDefault(piece => File.Exists(piece)
                    && VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(piece)) == 1);

            if (string.IsNullOrWhiteSpace(firstVolume))
            {
                return GroupOrderTrialResult.Fail("硬链接试开没做成：这一组里认不出第 1 卷 ⇒ 整组不动");
            }

            var rest = pieces
                .Where(piece => File.Exists(piece)
                    && !string.Equals(piece, firstVolume, StringComparison.OrdinalIgnoreCase))
                .OrderBy(piece => VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(piece)) ?? int.MaxValue)
                .Select(piece => new VolumeCandidate { Path = piece, Size = FileSizeOrZero(piece) })
                .ToList();

            var verifierFallback = new VolumeProbeVerifier(_archiveEngine);

            /*
             * 7z / RAR 的名字也可能被伪装成 `.png` / 裸 `.7z`（2026-10-19 `3333`）：
             * 名字只给出一种顺序时，`.002` 与 `.003` 会被排反 ⇒ 引擎必然报缺卷。
             * 按第 ④ 条：名字给不出确定顺序时，先用高概率顺序、再逐种排列试开；
             * 试开只决定顺序，不把整组判死。⛔ 只在 ≤5 片时排列（用户口径）。
             */
            VolumeContentFormat trialProbeFamily;

            try
            {
                trialProbeFamily = FileNature.Inspect(firstVolume).Format;
            }
            catch
            {
                trialProbeFamily = VolumeContentFormat.Unknown;
            }

            VolumeNamingFamily? fallbackOrderFamily = trialProbeFamily switch
            {
                VolumeContentFormat.SevenZip => VolumeNamingFamily.SevenZipNumbered,
                _ => null
            };

            if (fallbackOrderFamily is VolumeNamingFamily orderFamily
                && pieces.Count >= 2
                && pieces.Count <= VolumeOrderTrial.MaxPieces)
            {
                IReadOnlyList<IReadOnlyList<string>> fallbackOrders = VolumeOrderTrial.BuildCandidateOrders(
                    pieces,
                    null,
                    orderFamily,
                    referenceBase);

                if (fallbackOrders.Count > 1)
                {
                    AppendLog(
                        "INFO",
                        $"试开定序（{orderFamily}）—— 片数 {pieces.Count}｜可试顺序 {fallbackOrders.Count} 种");

                    foreach (IReadOnlyList<string> order in fallbackOrders)
                    {
                        string first = order[0];
                        var restOrder = order
                            .Skip(1)
                            .Where(piece => File.Exists(piece))
                            .Select(piece => new VolumeCandidate { Path = piece, Size = FileSizeOrZero(piece) })
                            .ToList();

                        var outcomeOrder = await verifierFallback.VerifyAsync(
                                first,
                                new List<IReadOnlyList<VolumeCandidate>> { restOrder },
                                cancellationToken,
                                Path.Combine(entryLayer, ".ArchiveFixer.work"),
                                trialProbeFamily)
                            .ConfigureAwait(false);

                        if (outcomeOrder.Confirmed)
                        {
                            return new GroupOrderTrialResult
                            {
                                Applicable = true,
                                Confirmed = true,
                                OrderedPieces = order,
                                Stem = referenceBase,
                                Family = orderFamily,
                                Reason = $"按内容/名字的高概率顺序试开通过（第 {outcomeOrder.Attempts} 种顺序）"
                            };
                        }
                    }
                }
            }

            /*
             * 试开用的硬链接名要**按卡上的族**拼（用户 2026-10-17：「底层识别先，名字配合行动」）：
             * RAR 组若仍按 7z 的 `.001` 建链接，引擎必然报「分卷压缩包缺少必要分卷」⇒ 整组永远"试开没通过"。
             */
            VolumeContentFormat probeFamily;

            try
            {
                probeFamily = FileNature.Inspect(firstVolume).Format;
            }
            catch
            {
                probeFamily = VolumeContentFormat.Unknown;
            }

            var outcomeFallback = await verifierFallback.VerifyAsync(
                    firstVolume,
                    new List<IReadOnlyList<VolumeCandidate>> { rest },
                    cancellationToken,
                    Path.Combine(entryLayer, ".ArchiveFixer.work"),
                    probeFamily)
                .ConfigureAwait(false);

            if (outcomeFallback.Confirmed)
            {
                return new GroupOrderTrialResult
                {
                    Reason = "按名字/内容给出的顺序试开通过（没有走通用分片的内容定序那一档）"
                };
            }

            return GroupOrderTrialResult.Fail(
                "硬链接试开没通过（引擎打不开这一组）⇒ 整组不动：" + outcomeFallback.Reason);
        }

        /// <summary>
        /// 通用分片试开定序的候选：**内容确认是 ZIP 分片 + 基名与本组相关**，并按盘上物理身份去重。
        /// ⛔ 不读名字的卷号来排次序（名字只拿来算相似度、给高概率顺序）；⛔ 内容可证不是分卷成员的一律剔除。
        /// </summary>
        private List<FileNatureCard> CollectNumberedZipTrialPieces(
            IReadOnlyList<string>? pieces,
            IReadOnlyList<string>? candidatePool,
            string referenceBase,
            string referenceName)
        {
            var cards = new List<FileNatureCard>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexByIdentity = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string path in (pieces ?? Array.Empty<string>())
                         .Concat(candidatePool ?? Array.Empty<string>()))
            {
                if (string.IsNullOrWhiteSpace(path)
                    || !seenPaths.Add(path)
                    || !File.Exists(path))
                {
                    continue;
                }

                FileNatureCard card;

                try
                {
                    card = FileNature.Inspect(path);
                }
                catch
                {
                    continue;
                }

                if (VolumePieceFacts.ContentProvesNotAVolumePiece(card))
                {
                    continue;
                }

                bool contentSaysPiece = card.ZipEndRecordMeansSplit
                    || card.HasZipLocalHeader
                    || card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    || card.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece;

                /*
                 * **中间片可能连格式都认不出**（2026-10-18 `30303030` 组合 6 运行期定案）：
                 * 通用分片从数据中间切开 ⇒ 第 2 片开头既不是本地文件头、也没有 EOCD，
                 * `FileNature` 只能答 `Unknown`；而它确实是这一组的一片（它的字节数、与相邻片的关系
                 * 都已经由收拢候选池的准入判据 `AcceptsAsGroupPiece` 过了）。
                 * ⇒ 这一档**允许 `Unknown` 进试开候选**（基名相关是调用方已过的弱证据；
                 * 最终顺序仍由引擎真的解一遍回答，⛔ 不是靠这条放行它就当真）。
                 * ⛔ 内容可证"不是分卷成员"的仍然一律剔除（上面那一条）。
                 */
                bool unknownMiddleStillPossible = card.Format == VolumeContentFormat.Unknown
                    && card.BodyKind == ArchiveBodyKind.Unknown;

                if (!contentSaysPiece && !unknownMiddleStillPossible)
                {
                    continue;
                }

                bool related = referenceBase.Length > 0
                    && VolumeGatherStep.BaseNameRelatedToGroup(card.ArchiveBaseName, referenceBase);

                if (!related && referenceName.Length > 0)
                {
                    related = VolumeGroupDetector.BelongsToSameGroup(
                        FileNameHelper.GetFileName(path),
                        referenceName);
                }

                if (!related)
                {
                    continue;
                }

                string identityKey = card.HasIdentity
                    ? "I:"
                      + card.VolumeSerial.ToString(System.Globalization.CultureInfo.InvariantCulture)
                      + ":"
                      + card.FileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "P:" + path;

                if (indexByIdentity.TryGetValue(identityKey, out int existing))
                {
                    /*
                     * 同一份物理文件的第二条名字：留**卡上自证规范名**的那一条（用户口径「同一份不出现两条名字」）；
                     * 两条都不是规范名 ⇒ 留先出现的那条（判不出就不动）。
                     */
                    if (!VolumePieceFacts.IsNameItsOwnCanonicalName(cards[existing])
                        && VolumePieceFacts.IsNameItsOwnCanonicalName(card))
                    {
                        cards[existing] = card;
                    }

                    continue;
                }

                indexByIdentity[identityKey] = cards.Count;
                cards.Add(card);
            }

            return cards;
        }

        /// <summary>
        /// 通用分片的末片：**只认内容**（带 EOCD 的那一片）。用户口径：通用分片只有末卷能从内容确认。
        /// 多个 EOCD / 一个都没有 ⇒ 判不出（⛔ 不猜）。
        /// </summary>
        private static string? ResolveNumberedZipTail(IReadOnlyList<FileNatureCard> cards)
        {
            var endPieces = cards.Where(card => card.ZipEndRecordMeansSplit).ToList();

            if (endPieces.Count == 1)
            {
                return endPieces[0].Path;
            }

            if (endPieces.Count > 1)
            {
                var byName = endPieces
                    .Select(card => new
                    {
                        card,
                        number = VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(card.Path))
                    })
                    .Where(item => item.number != null)
                    .OrderByDescending(item => item.number)
                    .ToList();

                return byName.Count > 0 ? byName[0].card.Path : null;
            }

            return null;
        }

        /// <summary>
        /// 跨盘 ZIP 的末片：**只认内容**（`VolumePieceFacts.IsSpannedZipTailPiece`：格式 ZIP + 内容成员位 +
        /// 卷号 == 自述总片数）。判不出 / 多个互不相同 ⇒ null（⛔ 不猜）。
        /// </summary>
        private static string? ResolveSpannedZipTail(IReadOnlyList<FileNatureCard> cards)
        {
            var tails = cards.Where(card => VolumePieceFacts.IsSpannedZipTailPiece(card)).ToList();

            if (tails.Count == 0)
            {
                tails = cards
                    .Where(card => card.ZipEndRecordMeansSplit
                        && card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
                    .ToList();
            }

            if (tails.Count == 1)
            {
                return tails[0].Path;
            }

            if (tails.Count > 1)
            {
                var byNumber = tails
                    .Where(card => card.VolumeNumber != null)
                    .OrderByDescending(card => card.VolumeNumber)
                    .ToList();

                return byNumber.Count > 0 ? byNumber[0].Path : null;
            }

            return null;
        }

        /// <summary>试开定序用的基名：优先取末片卡上的归档基名（唯一出口），判不出才用参照基名。</summary>
        private static string ResolveOrderTrialStem(
            IReadOnlyList<FileNatureCard> cards,
            string? tailPath,
            string referenceBase)
        {
            if (!string.IsNullOrWhiteSpace(tailPath))
            {
                FileNatureCard? tail = cards.FirstOrDefault(card =>
                    string.Equals(card.Path, tailPath, StringComparison.OrdinalIgnoreCase));

                if (tail != null && tail.ArchiveBaseName.Length > 0)
                {
                    return tail.ArchiveBaseName;
                }
            }

            return referenceBase;
        }

        /// <summary>拿不到大小就按 0（<see cref="FileInfo"/> 对正被别的进程占着的文件会抛）。</summary>
        private static long FileSizeOrZero(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }

    }
}