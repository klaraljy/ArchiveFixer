using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// RAR 分卷建组：内容卷号 + 首卷标记定序，试开确认后暂存 .partN.rar。
    /// </summary>
    public sealed class RarVolumeGroupBuilder
    {
        private readonly VolumeProbeVerifier _probe;
        private readonly IArchiveEngine? _engine;

        /// <summary>可选：把"判据输入"写进用户日志（不成立时点名，⛔ 不静默）。</summary>
        public Action<string>? AppendLog { get; set; }

        public RarVolumeGroupBuilder(VolumeProbeVerifier probe, IArchiveEngine? engine = null)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _engine = engine;
        }

        public async Task<VolumeGroupUnit?> TryBuildAsync(
            IReadOnlyList<VolumePieceFact> facts,
            string workRoot,
            CancellationToken cancellationToken = default)
        {
            if (facts == null || facts.Count < 2 || string.IsNullOrWhiteSpace(workRoot))
            {
                return null;
            }

            VolumePieceFact? first = facts.FirstOrDefault(fact =>
                fact.Card.BodyKind == ArchiveBodyKind.VolumeFirstPiece
                || fact.Card.VolumeNumber == 1);

            if (first == null)
            {
                AppendLog?.Invoke($"{facts.Count} 片里找不到「第 1 卷」（内容卷号=1 或首卷标志都没有）⇒ 不建组");
                return null;
            }

            var rest = facts
                .Where(fact => !ReferenceEquals(fact, first))
                .ToList();

            var orderings = new List<IReadOnlyList<VolumeCandidate>>();

            if (rest.All(fact => fact.Card.VolumeNumber is > 0))
            {
                orderings.Add(rest
                    .OrderBy(fact => fact.Card.VolumeNumber)
                    .Select(fact => new VolumeCandidate { Path = fact.Path, Size = fact.Size })
                    .ToList());
            }
            else
            {
                foreach (IReadOnlyList<VolumePieceFact> permutation in Permute(rest))
                {
                    orderings.Add(permutation
                        .Select(fact => new VolumeCandidate { Path = fact.Path, Size = fact.Size })
                        .ToList());
                }
            }

            if (orderings.Count == 0)
            {
                return null;
            }

            VolumeProbeOutcome probe = await _probe.VerifyAsync(
                    first.Path,
                    orderings,
                    cancellationToken,
                    workRoot,
                    VolumeContentFormat.Rar)
                .ConfigureAwait(false);

            /*
             * ===== 「这一组是谁、按什么顺序」= **内容事实**；试开（要密码）只做 tie-breaker =====
             *
             * 用户口径（2026-10-22）：「我们找出过同一组，但是输入密码解压那肯定是改回标准名之后的行为了」
             * 「既然只是单纯的密码错误而不是真的缺少卷和没匹配到一起，这种情况下，用户就可以自行添加新密码
             * 然后自己试开」⇒ **组身份与密码无关**：卷头里写死的卷号把 1..N 摆齐、而且没有缺口，
             * 这一组就是成立的；⛔ 不拿"试开能不能成功"当建组门槛（那一步本来就在改回标准名之后）。
             *
             * ⚠ 放宽是有条件的（内容自证完整）：**每一片都读到内容卷号、序号正好是 1..N 且不重不漏**。
             * 只要有一片读不出号 / 有缺口 ⇒ 退回老口径（试开确认才算），⛔ 不猜顺序。
             * （2026-10-22 第一次放宽写成"只要卷号都有就放行"，随后 `EncryptedVolumePipelineTests` 两条红；
             *  复查定案：那两条的真凶是**搬片**那一处的判据，不是这里 —— 修好搬运之后这里按条件放宽。）
             */
            bool numbersKnown = rest.All(fact => fact.Card.VolumeNumber is > 0);

            var contentNumbers = facts
                .Select(fact => fact.Card.VolumeNumber ?? 0)
                .Where(number => number > 0)
                .OrderBy(number => number)
                .ToArray();

            bool contentProvesComplete = numbersKnown
                && contentNumbers.Length == facts.Count
                && contentNumbers.SequenceEqual(Enumerable.Range(1, facts.Count));

            IReadOnlyList<VolumeCandidate> ordered;

            if (probe.Confirmed && probe.OrderedVolumes.Count == facts.Count)
            {
                // 试开确认过 ⇒ 用试开排出来的那一版（tie-breaker 赢）。
                ordered = probe.OrderedVolumes;
            }
            else if (contentProvesComplete && orderings.Count > 0)
            {
                /*
                 * 顺序由内容的卷号定下（1..N 无缺口）⇒ 照建。带密码的组就走这一支：
                 * 组事实成立、片会被搬到入口层；解压那一步如实报「密码错误」，片留在入口层等用户补密码。
                 */
                ordered = orderings[0];
            }
            else
            {
                return null;
            }

            var byPath = facts.ToDictionary(fact => fact.Path, StringComparer.OrdinalIgnoreCase);
            var confirmed = new List<VolumePieceFact> { first };

            foreach (VolumeCandidate candidate in ordered)
            {
                if (byPath.TryGetValue(candidate.Path, out VolumePieceFact? fact)
                    && !ReferenceEquals(fact, first))
                {
                    confirmed.Add(fact);
                }
            }

            if (confirmed.Count != facts.Count)
            {
                AppendLog?.Invoke($"顺序对不齐：facts={facts.Count} 片、按顺序只认回 {confirmed.Count} 片 ⇒ 不建组");
                return null;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(first.Card.ArchiveBaseName);

            if (string.IsNullOrWhiteSpace(baseName))
            {
                AppendLog?.Invoke("基名判不出 ⇒ 不建组");
                return null;
            }

            IReadOnlyList<string> names = VolumeNumberFromContent.BuildStandardNames(
                baseName,
                VolumeNamingFamily.RarPart,
                confirmed.Count);

            if (names.Count != confirmed.Count)
            {
                AppendLog?.Invoke($"规范名个数对不上：名字 {names.Count} 个、确认顺序 {confirmed.Count} 片 ⇒ 不建组");
                return null;
            }

            string staging = Path.Combine(workRoot, "rar-group-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var staged = new List<string>();

            for (int index = 0; index < confirmed.Count; index++)
            {
                string target = Path.Combine(staging, names[index]);

                if (!HardLinkHelper.CanHardLink(confirmed[index].Path, staging)
                    || !HardLinkHelper.TryCreateHardLink(target, confirmed[index].Path))
                {
                    AppendLog?.Invoke($"暂存链接建不出来（第 {index + 1} 片）⇒ 不建组");
                    return null;
                }

                staged.Add(target);
            }

            /*
             * ⛔ **空密码试开失败 ⇒ 不建组**（这是基线口径，2026-10-22 用 30 份真机批次定案）。
             *
             * 试过把它放宽成"只做 tie-breaker、失败也照建"（为了落实"密码错也把片留在入口包那一层"），
             * 代价是四份真机样本当场红：`7777` / `3333` / `21212121` / `24242424`。
             * 运行期日志逐字（`7777`，同一份样本、同一批事实 `facts=3`）：
             * · 基线：这之后**没有**建组，走 `批末补判：这一组现在齐了（共 3 卷）` ⇒ 先把片收拢到产品层
             *   `…\7777\bbb\bbb\bbb` 再开 ⇒ 成品出得来；
             * · 放宽后：这之后立刻 `RAR 内容事实建组成功 —— 3 片，源层「…\7777\bbb(3)\bbb(3)\bbb-L2」`
             *   ⇒ 组在**用户源层**里就地建、就地开 ⇒ 成品落到源层，判据 ① 红。
             * ⇒ 顺序（哪一版排列对）**必须**由试开回答；"试开失败也建组"等于让组在错误的那一层开工。
             */
            if (_engine != null)
            {
                ArchiveOperationResult test = await _engine.TestAsync(
                        ArchiveRequest.For(staged[0], string.Empty),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!test.Success)
                {
                    /*
                     * 空密码试开失败：**内容自证完整**（卷号 1..N 不重不漏）时照旧建组 ——
                     * 这一组只是带密码，片该进入口层、解压那一步如实报「密码错误」，等用户补密码。
                     * ⛔ 内容证明不了完整 ⇒ 照旧不建（兜底落在"什么都不做"）。
                     */
                    if (!contentProvesComplete)
                    {
                        return null;
                    }

                    AppendLog?.Invoke(
                        $"{baseName}：空密码试开没通过（这一组带密码）—— 卷号 1..{facts.Count} 已由内容证明完整，"
                        + "照旧在入口层建组；解压那一步会如实报「密码错误」");
                }
            }

            var unit = new VolumeGroupUnit(
                baseName,
                VolumeNamingFamily.RarPart,
                confirmed[0].SourceLayer,
                staged[0])
            {
                ConfirmedOrder = staged,
                PieceOrigins = confirmed
            };

            /*
             * ⛔ 归属判不出（空）⇒ **不写 owner**（保持 null）：那个事实位的语义是"这一组归谁"，
             * 拿一个空串占位会让"还没有 owner"那几条判据（`OwnerTaskFileName == null`）全部失效。
             */
            if (!string.IsNullOrWhiteSpace(confirmed[0].ProducerTaskFileName))
            {
                unit.OwnerTaskFileName = confirmed[0].ProducerTaskFileName;
            }
            return unit;
        }

        private static IEnumerable<IReadOnlyList<VolumePieceFact>> Permute(IReadOnlyList<VolumePieceFact> items)
        {
            if (items.Count <= 1)
            {
                yield return items;
                yield break;
            }

            for (int index = 0; index < items.Count; index++)
            {
                var rest = items.Where((_, position) => position != index).ToList();

                foreach (IReadOnlyList<VolumePieceFact> tail in Permute(rest))
                {
                    var result = new List<VolumePieceFact> { items[index] };
                    result.AddRange(tail);
                    yield return result;
                }
            }
        }
    }
}
