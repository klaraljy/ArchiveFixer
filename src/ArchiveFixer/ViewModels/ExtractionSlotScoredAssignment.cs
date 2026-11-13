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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 缺片补位的打分分配档（从 `ExtractionCoordinator.VolumeSlotFillStep.cs` 搬出）：
    /// 7z 自述字节数给不出证据时，按“缺哪几个槽位”与“名字读不出卷号的候选”三条既有证据投票分配；
    /// 先试最高分那一版、再来一趟换边，两版都不成 ⇒ 回滚本趟建的链接。
    /// </summary>
    internal sealed class ExtractionSlotScoredAssignment
    {
        private readonly ExtractionCoordinator _host;

        internal ExtractionSlotScoredAssignment(ExtractionCoordinator host)
        {
            _host = host;
        }


        /// <summary>
        /// **打分分配那一档**（用户 2026-10-14 口径 ①③；唯一执行体）：7z 自述字节数给不出证据时，
        /// 由"缺哪几个槽位"与"名字读不出卷号的候选"按三条既有证据投票分配，先试最高分那一版，
        /// 再来一趟就换边；两版都不成 ⇒ 回滚（删掉我们建的链接）。
        ///
        /// <para>三条证据（都是**既有**判据，⛔ 不新写命名规则）：
        /// ① 基名匹配 <see cref="FileNameHelper.GetArchiveBaseName"/>；
        /// ② 后缀相似（<see cref="VolumeSlotScoring.Similarity"/>，治"卷标记夹垃圾"）；
        /// ③ 空间计算 —— "除末卷外等大"（<see cref="VolumeGroupDetector.HasRegularVolumeSizes"/> 同一条规律）。
        /// 规范槽位名仍由 <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/> 给。</para>
        ///
        /// <para>⛔ 三条红线：数目不等 / 判不出 ⇒ **一份都不补**；只**硬链接**（零字节、原件一个字节不动）；
        /// 失败把这一趟建的链接**删掉**（= 换回原来的样子）。</para>
        /// </summary>
        internal void TryLinkMissingPiecesByScoredAssignment(
            ArchiveTask task,
            string entryLayer,
            string firstVolume,
            FileNatureCard entryCard,
            SortedDictionary<int, string> present,
            long largest)
        {
            string entryName = FileNameHelper.GetFileName(firstVolume);
            string groupBaseName = FileNameHelper.GetArchiveBaseName(entryName);

            if (groupBaseName.Length == 0 || present.Count == 0 || largest <= 0)
            {

                return;
            }

            string attemptKey = groupBaseName + "|" + SafePathHelper.GetFullPathSafe(entryLayer);

            /*
             * ⚠ 2026-10-14（实测 `28282828`，断点逐字）：**自述片数是硬上限** —— 名字读出"越界卷号"的那一片
             * 要从 present 摘掉：它其实就是缺口里的那一片，只是成员名被写坏了
             * （真第 2 片 `111-L2.7z.002` 套壳时被写成 `111-L2.7z.004pv2` ⇒ 名字尺子读成 4 号，
             * 而 7z 起始头自述只有 3 片）。摘掉之后它照常被候选池扫到、由打分分配挑槽位。
             * ⛔ 盘上一个字节都不动（摘掉的是"名字的解释"，不是文件）。
             */
            ExtractionCoordinator.DetachImpossibleSlots(present, entryCard.DeclaredVolumeCount);

            if (present.Count == 0)
            {
                return;
            }

            int maxIndex = present.Keys.Max();

            // 候选池：各单那几棵树 + 这一层（⛔ 不扫全盘、⛔ 不碰用户源目录）
            var candidates = new List<VolumeSlotScoring.Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string presentPath in present.Values)
            {
                seen.Add(SafePathHelper.GetFullPathSafe(presentPath));
            }

            /*
             * ⚠ 2026-10-14（实测 `3333`）：**已经在层里的那几片的硬链接不算候选**。
             * 收片那一档是"零字节硬链接"，同一份字节常常在别的树里还有第二个名字
             * （实测逐字：`111(2)\…\111-W1-0\111-L2.zip.002` 与层里那份 `盘上身份` 逐字相同
             * = `7A7EB341:A00000002E572`）⇒ 只比路径会把**同一个文件的两个名字**当成两份片，
             * 于是缺口被算成 `[3,4]`、还拿 `.002` 去补第 3 槽（日志逐字那种错法）。
             * ⇒ 按**盘上物理身份**（卷序列号 + 文件索引，既有出口 `FileIdentity`）排除。
             */
            var presentIdentities = new HashSet<(ulong VolumeSerial, ulong FileIndex)>();

            foreach (string presentPath in present.Values)
            {
                if (FileIdentity.TryGetIdentity(presentPath, out ulong serial, out ulong index))
                {
                    presentIdentities.Add((serial, index));
                }
            }

            /*
             * ⚠ 2026-10-14（实测 `28282828`，断点逐字）：候选池那几棵树改由唯一出口
             * <see cref="_host.CollectCandidateRoots"/> 给 —— 它比老写法多收"**每一单自己所在的那一层**"
             * （真片常常就躺在它旁边：那一份的真第 3 片在 `…\111(3)\111(3)\`，
             * 而老写法的 `roots` 七条里一条都没覆盖到它 ⇒ `candidates.Count == 0` ⇒ 判不出 ⇒ 组永远缺 2、3）。
             * 从**用户源树**里选出来的候选额外要求"基名与这一组有关系"（那道弱证据闸）。
             */
            foreach ((string root, ArchiveTask? rootOwner, bool requiresBaseNameRelation)
                     in _host.CollectCandidateRoots(ExtractionCoordinator.SnapshotTaskTable(_host.Tasks), entryLayer))
            {
                _ = rootOwner;

                string[] found;

                try
                {
                    found = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string path in found)
                {
                    string full = SafePathHelper.GetFullPathSafe(path);

                    if (!seen.Add(full))
                    {
                        continue;
                    }

                    long size;

                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    if (size <= 0 || size > largest)
                    {
                        continue;
                    }

                    if (FileIdentity.TryGetIdentity(path, out ulong candidateSerial, out ulong candidateIndex)
                        && presentIdentities.Contains((candidateSerial, candidateIndex)))
                    {
                        continue;   // 这是层里某一已有的片的第二个名字（硬链接）⇒ 不是候选
                    }

                    FileNatureCard card = FileNature.Inspect(path);

                    if (card.BodyKind == ArchiveBodyKind.SingleComplete)
                    {
                        continue;   // 内容自证是完整单卷 ⇒ 绝不当补片
                    }

                    /*
                     * 谁有资格当候选：
                     * ① 内容自证"我是分卷组的一员"（RAR 的 Volume 位 / RAR4 首卷标记 / 跨盘 ZIP 盘号）；
                     * ② 或者"判不出"那一档（7z 中间片按定义读不出格式）—— ⛔ 只收这两档。
                     * 另外：名字的基名与这一组相同也算一条弱证据（走打分，不在这里硬卡）。
                     */
                    bool member = card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember;
                    bool undecided = card.Format == VolumeContentFormat.Unknown
                        && card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;

                    if (!member && !undecided)
                    {
                        continue;
                    }

                    /*
                     * 谁能进候选（⚠ 2026-10-14 实测 `3333` 那一份后放宽了这一格，判据仍是事实）：
                     * 名字**读不出卷号**的 ⇒ 当然进；
                     * 名字**读得出卷号、可那个槽位已经有人占着**的 ⇒ 也进 —— 那正是"同名两份"的歧义
                     * （收片那一档按定义收不进去），必须靠这一档按体积/后缀/基名去补。
                     *
                     * 现场逐字（`3333` 组合 1）：第 3 片被伪装成"单一包名" `111-L2.zip`（原该是 `111-L2.zip.003`），
                     * 名字尺子把它读成"卷号 1" ⇒ 与已经在层的 `.001` **同号冲突** ⇒ 收片收不进、
                     * 这一档又被"读得出卷号就跳过"挡住 ⇒ 候选 0 个 ⇒ 整组永远缺第 3 片 ⇒ ①红。
                     * 而**空间规律**早就能认出它：`.001/.002` 都是 26 214 400，它是 10 490 534 ⇒
                     * "除末卷外等大" ⇒ 它就是**末片**。
                     */
                    /*
                     * ⚠ 2026-10-14（用户口径「专属算法不就是靠识别的吗」）：这里也**先问卡**
                     * （同一个唯一出口 `ExtractionCoordinator.ResolveVolumeSlotIndexForPiece`）——
                     * 卡说"我不是第一片"而名字读出 1 的那一份，**不当成"已占住 1 号槽"**，
                     * 而是按"判不出号"进候选池，由"缺口 + 体积"去定（⛔ 不再拿假锚点补片）。
                     */
                    /*
                     * ⚠ 2026-10-16（用户拍板「RAR4 用主头 MHD_FIRSTVOLUME 认入口 + RawVolumeField 锚定基数」）：
                     * RAR 1.5–4.x 的头里**没有绝对卷号**，只有卷尾结尾块的原始字段 —— 卡上的 `VolumeNumber`
                     * 因此恒为空、名字尺子也读不出，于是这一档永远把它算成"名字读不出卷号"的候选，
                     * 再跟"缺口数"对不上就整档判不出（真机 `4444` 逐字：这一组缺 5 个槽位，而名字读不出
                     * 卷号的候选有 2 份 ⇒ 数目对不上 ⇒ 判不出）。
                     * ⇒ 用**入口那一份**（主头自报第 1 卷、它的原始字段就是基数）锚定，把绝对卷号翻出来；
                     * 唯一出口 = `VolumePieceFacts.ResolveRar4SlotIndex`（判不出返回 null，照旧什么都不做）。
                     */
                    int? nameIndex = ExtractionCoordinator.ResolveVolumeSlotIndexForPiece(path, card)
                        ?? VolumePieceFacts.ResolveRar4SlotIndex(card, entryCard);

                    /*
                     * ===== 族不同 ⇒ 不许进这一组（用户 2026-10-17：「一切看底层识别的来」）=====
                     * 现场（新样本 `1111` 日志逐字）：`222-L1.zip.001 → 222-L2.part4.rar`
                     * （`基名 0/4，后缀相似 2/3（0.50），体积 3/3`）—— **内层 zip 组**的片被"打分分配"
                     * 塞进**外层 RAR 组**，多出 `part4/part5.rar` 两个名字 ⇒ "除末卷外等大"判据被带偏
                     * ⇒ 整组判成缺卷 ⇒ ①红。
                     * 判据只用卡上的族（`Format`）：两边都认得出且不同 ⇒ 不参选；判不出则照旧（不拦）。
                     */
                    if (card.Format != VolumeContentFormat.Unknown
                        && entryCard.Format != VolumeContentFormat.Unknown
                        && card.Format != entryCard.Format)
                    {
                        continue;
                    }

                    /*
                     * ⚠ 2026-10-14（实测 `28282828`，断点逐字）：**名字读出越界卷号 ⇒ 名字在撒谎**。
                     * 真第 2 片的成员名被写成 `111-L2.7z.004pv2` ⇒ 读成 4 号，而 7z 起始头自述只有 3 片
                     * （`entryCard.DeclaredVolumeCount == 3`）⇒ 它其实就是缺口里的那一片 ⇒ 必须进候选池。
                     */
                    bool impossibleSlot = nameIndex is int claimedIndex
                        && entryCard.DeclaredVolumeCount > 0
                        && claimedIndex > entryCard.DeclaredVolumeCount;

                    if (requiresBaseNameRelation
                        && !VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetFileName(path), groupBaseName))
                    {
                        continue;   // 用户源树里选出来的候选：基名得跟这一组有关系（弱证据那道闸）
                    }

                    if (impossibleSlot)
                    {
                        // 越界号 ⇒ 名字不可信 ⇒ 收（占哪个槽位由打分分配说了算）
                    }
                    else if (nameIndex is int claimed
                        && present.TryGetValue(claimed, out string? occupant)
                        && !SafePathHelper.PathEquals(occupant, path))
                    {
                        // 同号冲突 ⇒ 收片收不进去，正是这一档要处理的
                    }
                    else if (nameIndex != null)
                    {
                        continue;   // 名字读得出卷号、槽位也没冲突 ⇒ 归收片那一档
                    }

                    candidates.Add(new VolumeSlotScoring.Candidate
                    {
                        Path = path,
                        FileName = FileNameHelper.GetFileName(path),
                        Size = size,
                        ContentNumber = card.VolumeNumber
                    });
                }
            }

            /*
             * ⚠ 槽位推导分两步（用户口径 ①："除末卷等大" + "缺什么补什么"）：
             * ① 1..max 里的**洞**（按名字读得出卷号的那几片留下的缺口）；
             * ② 洞填完还不够（缺的那几片在"末片之后"，名字根本读不出来）⇒ 往 max 后面**续**，
             *    续几个由**候选数**定；内容算得出片数时（7z 的自述值）照旧封顶，⛔ 不越界。
             */
            var slots = new List<int>();

            for (int index = 1; index <= maxIndex; index++)
            {
                if (!present.ContainsKey(index))
                {
                    slots.Add(index);
                }
            }

            while (slots.Count < candidates.Count)
            {
                int next = ++maxIndex;

                if (entryCard.DeclaredVolumeCount > 0 && next > entryCard.DeclaredVolumeCount)
                {
                    break;
                }

                slots.Add(next);
            }

            _host.AppendLog(
                "WARN",
                $"{task.LogName}：这一组的缺口 = [{string.Join(",", slots)}]，名字读不出卷号的候选 = "
                + $"[{string.Join(",", candidates.Select(item => item.FileName))}]");

            /*
             * ⚠ 实测 `24242424`：同一个名字的两份拷贝（不同树里）被当成两份片 ⇒ 缺口被算成 `[2,3]`、
             * 还拿**同一片**去补两个槽。⇒ 按"名字 + 大小"去重（同名的同一片只算一份）。
             */
            candidates = candidates
                .GroupBy(
                    item => item.FileName + "|" + item.Size.ToString(CultureInfo.InvariantCulture),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (slots.Count != candidates.Count)
            {
                _host.AppendLog(
                    "WARN",
                    $"{task.LogName}：这一组缺 {slots.Count} 个槽位，而名字读不出卷号的候选有 {candidates.Count} 份 —— "
                    + "数目对不上 ⇒ 判不出，一份都不补（⛔ 不猜）。");

                return;
            }

            IReadOnlyList<VolumeSlotScoring.Assignment> ranked = VolumeSlotScoring.Rank(
                slots,
                candidates,
                groupBaseName,
                largest,
                maxIndex,
                slot => VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(entryName, slot, out string canonical)
                    ? canonical
                    : string.Empty);

            if (ranked.Count == 0)
            {
                return;
            }

            int attempt = _host._scoredSlotAttempts.TryGetValue(attemptKey, out int done) ? done : 0;

            if (attempt >= ranked.Count)
            {
                _host.AppendLog(
                    "WARN",
                    $"{task.LogName}：这一组的打分分配两版都试过了、仍然不成 ⇒ 不再动手、「保持原样」"
                    + "（我们建的槽位链接已经删掉；原件一个字节都没动）。");

                return;
            }

            // 回滚上一版：只删**我们自己建的**那几个槽位链接（零字节硬链接 ⇒ 删掉 = 回到原样）
            if (_host._scoredSlotLinks.TryGetValue(attemptKey, out List<string>? previous))
            {
                foreach (string link in previous)
                {
                    try
                    {
                        if (File.Exists(link))
                        {
                            FileOperations.Delete(link);
                        }
                    }
                    catch
                    {
                        // 删不掉就留着（它只是我们建的链接）。
                    }
                }

                previous.Clear();
            }

            VolumeSlotScoring.Assignment picked = ranked[attempt];
            var created = new List<string>();
            var linkedPairs = new List<(string Source, string Canonical)>();

            foreach ((VolumeSlotScoring.Candidate candidate, int slot) in picked.Pairs)
            {
                /*
                 * 槽位名：走**唯一出口** <see cref="ExtractionCoordinator.ResolveMissingSlotName"/> —— 它自己会
                 * ①照"已经在层里的那一片"的写法（样板优先取名字读得出卷号的那一片）、②拼完自验、
                 * ③回落既有命名出口、④两条都不成 ⇒ 空串（这一趟整体回滚、一份都不补）。
                 * ⚠ 实测 `24242424`（垃圾名 `111-L2.part000000002.rar`）与 `28282828`（丢族段 `111-L2.002`）
                 * 两条教训都已经收进那一个出口里。
                 */
                string slotName = ExtractionCoordinator.ResolveMissingSlotName(present, entryName, slot, ExtractionCoordinator.ResolveLayerNamingFamily(entryLayer));

                if (slotName.Length == 0)
                {
                    Rollback(created);
                    return;
                }

                string target = Path.Combine(entryLayer, slotName);

                if (File.Exists(target)
                    || !HardLinkHelper.CanHardLink(candidate.Path, entryLayer)
                    || !HardLinkHelper.TryCreateHardLink(target, candidate.Path))
                {
                    Rollback(created);
                    return;
                }

                created.Add(target);
                linkedPairs.Add((candidate.Path, target));
            }

            _host._scoredSlotAttempts[attemptKey] = attempt + 1;
            _host._scoredSlotLinks[attemptKey] = created;

            foreach ((string source, string target) in linkedPairs)
            {
                string sourceName = FileNameHelper.GetFileName(source);

                _host._byteBudgetLinkedSources.Add((task, groupBaseName, source));
                _host.RememberAdoptedLandedPiece(source, task, groupBaseName, renamedInPlace: false);

                _host.AppendLog(
                    "INFO",
                    $"{task.LogName}：按「打分分配」把 `{sourceName}` 补成槽位名 `{FileNameHelper.GetFileName(target)}`"
                    + $"（第 {attempt + 1} 版尝试，零字节硬链接，原件一个字节没动）。"
                    + $"判据输入：{string.Join("；", picked.Evidence)}"
                    + (picked.HasTie ? "｜⚠ 最高分与次高分并列 ⇒ 这一版只是概率，不成会自动换边。" : string.Empty));
            }

            void Rollback(List<string> built)
            {
                foreach (string path in built)
                {
                    try
                    {
                        FileOperations.Delete(path);
                    }
                    catch
                    {
                        // 删不掉就留着。
                    }
                }

                built.Clear();
            }
        }
    }
}

