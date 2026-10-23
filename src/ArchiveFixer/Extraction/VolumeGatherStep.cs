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




namespace ArchiveFixer.Extraction
{
    /// <summary>收片/组内片的专用出口（从 ExtractionCoordinator 搬出的第 1 个功能块，2026-10-23）。协调器原处只留调用，不再留第二份实现。</summary>
    public static class VolumeGatherStep
    {
        /// <summary>
        /// 引擎这一趟该打开的那一份：手上就是第 1 卷 ⇒ 它自己；手上是后续卷 ⇒ 在
        /// <see cref="VolumeNameRepair.EnumerateVolumeCandidatesNearby"/> 的窗口里找同一组的第 1 卷
        /// （窗口里自己那一层排在最前，所以"同一层优先"是白拿的）；判不出 ⇒ 退回手上那一份。
        ///
        /// <para>⚠ 2026-10-17（本轮实测）：这一处**不能只换 `current`** —— 它同时是
        /// <see cref="VolumeNameRepair.ResolveCrossLayerVolumeGather"/> 的**候选池来源**
        /// （那一段会枚举"入口那一份所在的目录"）。直接用 `current` 会让池子换成"手上那一片的目录"，
        /// `SiblingFolderVolumeGatherTests` 四条守门当场红（跨盘 ZIP 的两个半边在不同的包里）。
        /// ⇒ 池子来源照旧走这一条；而"这一组该拼到哪一层"的**基准**判据已经改由
        /// `CrossLayerVolumeGather.EntryPath`（按池子里的第 1 卷）回答，闸门也据此删掉了。</para>
        /// </summary>
        internal static string ResolveGatherEntryPath(string current)
        {
            try
            {
                string name = Path.GetFileName(current);

                /*
                 * ① **底层识别优先**（用户 2026-10-17 总则：「一开始肯定是用底层识别来弄……只有底层做不到
                 * 或者哪些地方需要名字的修改来弄的」）：手上这一份自己自证是首卷
                 * （`BodyKind = VolumeFirstPiece`）⇒ 它就是入口，名字认不出也一样。
                 */
                if (File.Exists(current)
                    && FileNature.Inspect(current).BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                {
                    return current;
                }

                List<VolumeCandidate> nearby = VolumeNameRepair
                    .EnumerateVolumeCandidatesNearby(current)
                    .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Path))
                    .ToList();

                /*
                 * ② 窗口里逐份读卡：**名字只当"同一组"的粗筛**（⛔ 不拿名字定卷号），
                 * 卡上自证首卷的那一份就是入口。
                 */
                foreach (VolumeCandidate candidate in nearby)
                {
                    string candidateName = Path.GetFileName(candidate.Path);

                    if (!File.Exists(candidate.Path)
                        || !VolumeGroupDetector.BelongsToSameGroup(name, candidateName))
                    {
                        continue;
                    }

                    if (FileNature.Inspect(candidate.Path).BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                    {
                        return candidate.Path;
                    }
                }

                /*
                 * ③ 名字兜底（底层做不到的那一档：7z 中间卷内容里没有卷号，只能靠名字排）。
                 */
                if (VolumeGroupDetector.TryGetVolumeIndex(name) == 1)
                {
                    return current;
                }

                foreach (VolumeCandidate candidate in nearby)
                {
                    string candidateName = Path.GetFileName(candidate.Path);

                    if (VolumeGroupDetector.TryGetVolumeIndex(candidateName) == 1
                        && VolumeGroupDetector.BelongsToSameGroup(name, candidateName)
                        && File.Exists(candidate.Path))
                    {
                        return candidate.Path;
                    }
                }
            }
            catch
            {
                // 判不出 ⇒ 什么都不做（收卷那一档会因为找不到第 1 卷整档判不出）。
            }

            return current;
        }

        /// <summary>
        /// **"这一份能不能算本组的一片"的唯一出口**（底层识别先行，名字只保底）。
        ///
        /// <para>顺序＝证据强度（⛔ 每个调用方都只许问这一处，不许再自己拼一套）：</para>
        /// <list type="number">
        /// <item><description><b>内容可证不是成员</b>（`ProvablyNotVolumeMember` / `SingleComplete`）⇒ **否决**，
        /// 名字再像也不收 —— 这是"底层识别先"的那一刀。</description></item>
        /// <item><description><b>卡自证是 zip 分片</b>（EOCD / 本地头 / 跨盘成员且非单卷）⇒ 收。</description></item>
        /// <item><description><b>字节数守恒</b>（与第 1 片同尺寸 ±1%，且没被内容证明不是成员）⇒ 收 ——
        /// 这是**中间片**唯一能过的证据（头从数据中间切开、没有 EOCD、成员身份判不出）。</description></item>
        /// <item><description><b>卡说它是这一族的一片且基名与本组相关</b> ⇒ 收（卡 + 弱名字证据）。</description></item>
        /// <item><description><b>名字尺子同组</b> ⇒ 收（兜底；只在前四条都没否决它之后）。</description></item>
        /// </list>
        /// </summary>
        /// <param name="fullSizeAnchor">"第 1 片有多大"的锚点（字节数守恒要用）；0 = 这一档不启用。</param>
        internal static bool AcceptsAsGroupPiece(
            FileNatureCard card,
            string reference,
            string referenceBase,
            long fullSizeAnchor)
        {
            if (card == null || string.IsNullOrWhiteSpace(card.Path))
            {
                return false;
            }

            string candidateName = FileNameHelper.GetFileName(card.Path);

            /* ① 内容可证不是成员 ⇒ 否决（⛔ 这一条必须在名字之前）。 */
            bool provenNotMember = card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                || card.BodyKind == ArchiveBodyKind.SingleComplete;

            if (provenNotMember)
            {
                return false;
            }

            /* ② 卡自证是 zip 分片。 */
            bool cardSaysZipPiece = card.Format == VolumeContentFormat.Zip
                && (card.HasZipEndRecord
                    || card.HasZipLocalHeader
                    || card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember);

            if (cardSaysZipPiece)
            {
                return true;
            }

            /* ③ 字节数守恒（中间片那一档）：大小 + 基名相关，两条都要。 */
            /*
             * ⚠ 2026-10-18（困难批 `30303030` 运行期定案）：**字节数守恒不能单独当准入**。
             * 现场逐字：补扫把另一个样本的 `222-L2.part2.rar-113`（同样 26 214 400 字节）收进池子
             * （`基名与本组相关=否` 却 `准入=True`）⇒ 池子里混进别组文件 ⇒ "可证完整"是假的。
             * ⇒ ③ 必须再加**基名与本组相关**这一条弱证据；⛔ 只看大小等于"谁大谁进"。
             * （② 那一条是**内容**证据（本地文件头 / 尾部 EOCD / 成员位），不加基名限制 ——
             *  `6666` 的第 1 片叫 `打包111-W0-0`，基名与参照名毫不相干，靠的正是那一条。）
             */
            if (fullSizeAnchor > 0
                && referenceBase.Length > 0
                && BaseNameRelatedToGroup(FileNameHelper.GetArchiveBaseName(card.Path), referenceBase)
                && card.Size > 0
                && Math.Abs(card.Size - fullSizeAnchor) <= Math.Max(1, fullSizeAnchor / 100))
            {
                return true;
            }

            /*
             * ③b **7z/RAR 的中间片 / 末片**：内容里没有魔数（只有首卷的起始头自述），
             * `FileNature` 只能答 `Unknown`。首卷自述的片数/字节数和满片尺寸是现有证据，
             * 只要基名相关、尺寸不超过满片 ⇒ 允许进候选；是否真的成组仍由
             * `VolumePiecesFromCards` 的片数/合计和试开回答。
             * 现场（2026-10-19 `3333`）：`.002`=`333-L2.7z.png`、`.003`=`333-L2.7z` 都是 Unknown，
             * 被唯一出口挡掉 ⇒ 候选永远只有 `.601` 一片。
             */
            if (fullSizeAnchor > 0
                && referenceBase.Length > 0
                && BaseNameRelatedToGroup(FileNameHelper.GetArchiveBaseName(card.Path), referenceBase)
                && card.Size > 0
                && card.Size <= fullSizeAnchor
                && card.BodyKind == ArchiveBodyKind.Unknown
                && card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.Unknown)
            {
                return true;
            }

            /* ④ 卡 + 弱名字证据（既有两把尺子）。 */
            if (referenceBase.Length > 0
                && VolumePieceFacts.CardSaysItIsOneOfTheGroup(card)
                && BaseNameRelatedToGroup(FileNameHelper.GetArchiveBaseName(card.Path), referenceBase))
            {
                return true;
            }

            /* ⑤ 名字兜底。 */
            return VolumeGroupDetector.BelongsToSameGroup(candidateName, reference);
        }

        /// <summary>
        /// 这一份的基名与这一组"有关系"吗（用户口径：「看后缀有没有相似的地方」）。
        ///
        /// <para>判据只有两条，都读**既有出口** <see cref="FileNameHelper.GetArchiveBaseName"/>：
        /// ① 逐字相同；② 一方是另一方的前缀（实测 `28282828`：这一片的基名读出来是 `111-L2.7z`，
        /// 而这一组的基名是 `111-L2` ⇒ 卷标记 `00_!63` 被垃圾污染 ⇒ 只有前缀关系还活着）。</para>
        /// ⛔ 它是**弱证据**，只用来决定"要不要把它放进候选池"；占哪个槽位仍由打分分配说了算。
        /// </summary>
        internal static bool BaseNameRelatedToGroup(string candidateFileName, string groupBaseName)
        {
            if (string.IsNullOrWhiteSpace(candidateFileName) || string.IsNullOrWhiteSpace(groupBaseName))
            {
                return false;
            }

            string candidateBase = FileNameHelper.GetArchiveBaseName(candidateFileName);

            if (candidateBase.Length == 0)
            {
                return false;
            }

            return candidateBase.StartsWith(groupBaseName, StringComparison.OrdinalIgnoreCase)
                || groupBaseName.StartsWith(candidateBase, StringComparison.OrdinalIgnoreCase);
        }

    }
}
