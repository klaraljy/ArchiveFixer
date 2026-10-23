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
    /// <summary>整组候选收集与收拢（组目录/参考名/成员判定/槽位键/规范名准备；第 9 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **这一组的锚（第 1 卷卡）该从哪拿** —— 两处，按可信度排：
        ///
        /// <list type="number">
        /// <item><description>**这一单自己那一份**（<paramref name="owner"/> 的 `CurrentPath`）：批末重判里
        /// "这一组该拼到哪一层"就是**按这一单**算出来的，所以它手上那份就是这一组的入口。</description></item>
        /// <item><description>**目标层里那一份**（<see cref="ResolveGroupFirstVolumeCard"/>）。</description></item>
        /// </list>
        ///
        /// <para>为什么必须有第 ① 处（真机 `19191919` 探针逐字）：那一刻
        /// `目标层=有 目录在=是 层内文件数=2 **层内第1卷=无**`（入口还没被链接进那一层），
        /// 而同一份探针里 `片=aaa-L2 卡号=1 卡类=VolumeFirstPiece` ⇒ 锚其实**就在这一单自己手上**；
        /// 只盯目标层 ⇒ 锚=无 ⇒ 规范名推不出来（`[规范名·进] … 锚=无`）⇒ 那一片永远接不进这一组。</para>
        ///
        /// <para>⛔ 两处都拿不到 ⇒ null（判不出 ⇒ 什么都不做）。</para>
        /// </summary>
        private static FileNatureCard? ResolveGroupAnchorCard(
            ArchiveTask? owner,
            string targetDir,
            string baseName,
            string? groupEntryPath = null)
        {
            /* ① **外层交下来的入口**最可信：收拢那一趟的成员名单里就有它（探针逐字见上面那段）。 */
            if (!string.IsNullOrWhiteSpace(groupEntryPath) && File.Exists(groupEntryPath))
            {
                FileNatureCard groupEntryCard = FileNature.Inspect(groupEntryPath);
                if (groupEntryCard.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                {
                    return groupEntryCard;
                }
            }

            string ownerPath = owner?.CurrentPath ?? string.Empty;

            if (ownerPath.Length > 0)
            {
                FileNatureCard ownerCard = FileNature.Inspect(ownerPath);

                /*
                 * ⛔ **权威是 `BodyKind`，不是 `FirstVolumeFlag`** —— §29.8 那条写死的教训，
                 * 本轮又实测一次（探针逐字）：`[取锚·owner] 单=aaa-L2.part1.rar 卡类=VolumeFirstPiece
                 * **首卷位=False** raw=0` ⇒ 锚本来就在这一单手上，被那个标志挡掉了。
                 * （真机 `4444` 的入口当年也是同一形状：`VolumeFirstPiece` + `First=False`。）
                 */
                if (ownerCard.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                {
                    return ownerCard;
                }
            }

            return ResolveGroupFirstVolumeCard(targetDir, baseName);
        }

        /// <summary>
        /// **这一组的第 1 卷卡**（RAR 1.5–4.x 锚定基数、以及"本族入口形状"的样本都要用它；
        /// 用户 2026-10-16 拍板）。
        ///
        /// <para>先在<paramref name="targetDir"/> **这一层**里找与这一片**同基名**、卡上自报"我就是第 1 卷"
        /// （<see cref="FileNatureCard.FirstVolumeFlag"/>）的那一份；同基名一个都没有时（真机 `19191919`：
        /// 这一片的名字被改烂成 `aaa-L2-976`，基名本身就是垃圾 ⇒ 按基名永远找不到入口），
        /// 退回"**这一层里唯一的那个自报第 1 卷的片**" —— ⛔ 必须**恰好一份**才采信，
        /// 两份以上 ⇒ 判不出（⛔ 不猜）。⛔ 只扫一层、⛔ 一个字节都不动、⛔ 找不到 ⇒ null。</para>
        /// </summary>
        private static FileNatureCard? ResolveGroupFirstVolumeCard(string targetDir, string baseName)
        {
            if (targetDir.Length == 0 || !Directory.Exists(targetDir))
            {
                return null;
            }

            FileNatureCard? uniqueFirstVolume = null;
            int firstVolumeCount = 0;

            foreach (string candidate in Directory.EnumerateFiles(targetDir))
            {
                FileNatureCard card = FileNature.Inspect(candidate);

                // ⛔ 权威同样是 `BodyKind`（见 `ResolveGroupAnchorCard` 那条实测教训），⛔ 不要求首卷标志。
                if (card.BodyKind != ArchiveBodyKind.VolumeFirstPiece)
                {
                    continue;
                }

                if (baseName.Length > 0
                    && VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetFileName(candidate), baseName))
                {
                    return card;   // 同基名的那一份最可信
                }

                firstVolumeCount++;
                uniqueFirstVolume = card;
            }

            return firstVolumeCount == 1 ? uniqueFirstVolume : null;
        }

        /// <summary>
        /// **这一份的"槽位号"该信谁**（专属算法的入口判据，唯一出口）。
        ///
        /// <para>次序（用户 2026-10-14：「**专属算法不就是靠识别的吗**」）：**先问底层识别卡，卡上没有才问名字**：
        /// ① 卡上有卷号（RAR5 主头 / RAR4 新编号 / 跨盘 ZIP 末片写着第几盘）⇒ 用卡上的；
        /// ② 卡上写着 <see cref="ArchiveBodyKind.VolumeFirstPiece"/>（起始头自述的整包比这一份大）⇒ **1**；
        /// ③ 卡上写着 <see cref="ArchiveBodyKind.VolumePiece"/>（"我是这一组的一片、**但不是第一片**"）
        ///    而**名字读出来是 1** ⇒ 那个 1 是假的（卷标记被伪装吃掉之后，名字那把尺子会退回"1"）
        ///    ⇒ **判不出**（null），交给"缺口 + 体积"（末片最小）去定 —— ⛔ 不许拿假锚点去补片；
        /// ④ 卡上什么都没有（7z 的中间片之类）⇒ 回落到名字那把尺子（老口径，⛔ 不放宽）。</para>
        ///
        /// <para>现场（官方环台 `10101010`，卡逐字）：那一组三片分别叫 `pack111-L2.7z`、`pack111-L2.7z.002`、
        /// `pack111-L2.7z` —— 第 1 片与第 3 片的**名字一模一样**（名字尺子都读成 1）⇒ 旧口径把第 3 片也摆在
        /// 1 号槽 ⇒ 组永远凑不齐；而卡上写得清清楚楚：第 1 片 `专属算法=VolumeFirstPiece`、
        /// 第 3 片 `专属算法=VolumePiece`（体积 26 214 400 vs 10 490 213，末片最小）。</para>
        /// </summary>
        private static int? ResolveVolumeSlotIndexForPiece(string path, FileNatureCard card)
        {
            /*
             * ⚠ 2026-10-14：这套顺序（**卡 → 内容 → 名字**）已经收成一个唯一出口 ——
             * <see cref="VolumePieceFacts.ResolveSlotIndex"/>（用户口径：「把现在的专属算法顺序给我弄好了」）。
             * ⛔ 别处不许再自己拼一遍。
             */
            return VolumePieceFacts.ResolveSlotIndex(path, card);
        }

        /// <summary>
        /// 这一组的第 1 卷是不是**同批另一个任务**在负责（判据与
        /// <see cref="SkipWhenAnotherTaskOwnsThisVolumeGroup"/> 同一个口径：任务表里有没有别人指着它）。
        /// 是 ⇒ 这一单不许把起点改过去（那一组只解一次，别的卷按「跟班卷」跳过）。
        /// </summary>
        private bool IsFirstVolumeOwnedByAnotherTask(ArchiveTask task, string firstVolumePath)
        {
            string first = SafePathHelper.GetFullPathSafe(firstVolumePath);

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null || ReferenceEquals(candidate, task))
                {
                    continue;
                }

                if (string.Equals(
                        SafePathHelper.GetFullPathSafe(candidate.CurrentPath),
                        first,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// **底层识别卡**说"这一份是某一分卷组的一片"（用户 2026-10-17：「定稿分类读卡」——
        /// 分卷片不再当内容物）。⛔ 不看后缀、⛔ 不猜：判据唯一出口
        /// <see cref="VolumePieceFacts.CardSaysItIsOneOfTheGroup"/>（`BodyKind` = 首片 / 后续片）。
        /// </summary>
        private static bool CardSaysItIsOneOfTheGroup(string path) => StageFilePredicates.CardSaysItIsOneOfTheGroup(path);

        /// <summary>
        /// 找卷的基准那一句（只在**基准不是入口那一卷**时补出来）：用户口径是"以一开始的分卷文件为准，
        /// 不要以 001 为准" —— 这一行就是它落地的证据（窗口的父文件夹是**基准**那一层的父文件夹）。
        /// ⛔ 只写目录名，不写完整路径（§8）。
        /// </summary>
        private static string DescribeGatherAnchor(
            VolumeNameRepair.CrossLayerVolumeGather decision,
            string entryPath)
        {
            string? anchor = decision.AnchorPath;

            if (string.IsNullOrWhiteSpace(anchor)
                || string.Equals(
                    SafePathHelper.GetFullPathSafe(anchor),
                    SafePathHelper.GetFullPathSafe(entryPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return "。" + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeGatherAnchorFormat,
                Path.GetFileName(anchor),
                Path.GetFileName(Path.GetDirectoryName(anchor) ?? string.Empty));
        }

        /// <summary>
        /// 把一片从**我们自己的过程物目录**挪进这一组的目录（同盘改名、零字节；失败 ⇒ false 由调用方退回硬链接）。
        /// ⛔ 只用于过程物：用户目录里的片从这条路走不进来。
        /// </summary>
        private static bool TryMovePieceIntoGroup(string piecePath, string target)
        {
            try
            {
                File.Move(piecePath, target);
                return File.Exists(target);
            }
            catch
            {
                // 挪不动（被占用 / 目标刚出现）⇒ 交给调用方退回"建硬链接"那一档。
                return false;
            }
        }

        /// <summary>
        /// **这一单"属于哪一组"的引用名**：那把「同一组 = 族 + 基名」的尺子（<see cref="VolumeGroupDetector.BelongsToSameGroup"/>）
        /// 要一个**组内成员的名字**当引用，这个成员就是**这一单自己**。
        ///
        /// <para><b>为什么必须改（真机 FFFF 2026-10-10 夜，定案到行）</b>：这里各处原来一律写死
        /// `<基名>.zip` —— 那等于**假定每一组都是跨盘 ZIP 族**。族一旦不是 <c>ZipSpanned</c>
        /// （`X.partN.rar` 是新式族、`X.7z.001` 是数字族、`X.r00` 是老式族），
        /// `BelongsToSameGroup(别的片, "X.zip")` **恒为 false**（尺子自己写着：`x.rar` 与 `x.part1.rar`
        /// 不同组、族不同就是不同组）⇒ 收拢那一档连"这一组有哪些成员"都收不出来。</para>
        ///
        /// <para>调试器实测（`steps-ffff2.json`，暂停在 `RecheckDeferredVolumeDeficits:10698`）：
        /// 那一批的待判单是 `111_.part4.rar`、`baseName = "111_"` ⇒ 引用名是 `111_.zip`，
        /// 而四片叫 `111_.part1.rar` / `111_.part2` / `111_.part3.rar` / `111_.part4.rar`
        /// ⇒ 一个成员都匹配不上 ⇒ 入口那一层推不出来 ⇒ 整组到最后也没解开
        /// （真机：成功 0 / 失败 3；我这边同一批：成功 0 / 失败 0 / 跳过 3 / 部分完成 3）。
        /// 守门 `ChainInnerVolumeRestoreTests.真机布局_四个外层包各在一个子目录里_四片分处四地_整组照样凑齐并解开`
        /// （**改之前是红的**，逐字红在 `Assert.NotNull() Failure: Value is null`）。</para>
        ///
        /// <para>⛔ **判不出就照旧**：这一单的名字解析不出来时退回老的 `<基名>.zip`
        /// —— 一个字都不放宽，只是"不认得就不认"。⛔ 同一个问题（"哪些文件属于这一组"）
        /// 只许有这一个出口，各处不许再自己拼 `<基名>.zip`。</para>
        /// </summary>
        private static string ResolveGroupReferenceName(ArchiveTask? task, string baseName, string? memberPath = null)
        {
            /*
             * ⚠ **优先拿"手上这一片"的名字**（2026-10-11 订正）：它按定义就在这一组里；而"调用方那一单自己的文件名"
             * 在**接片**那条路上拿不到（调用方给的是 null）⇒ 老写法只能退回 `<基名>.zip`，
             * 可那把尺子只对**跨盘 ZIP 族**成立 —— RAR 新式（`X.partN.rar`）/ 数字族（`X.7z.001`）
             * 用 `X.zip` 去比，`BelongsToSameGroup` 恒 false ⇒ 这一组的成员**一个都收不进来**
             * （真机 FFFF：`111_` 那一组就是这么互相看不见的）。
             *
             * ⛔ 判据与老口径**同一把尺子**（`BelongsToSameGroup` 自比），只是参照物多了一个来源；
             * 名字解析不出来 ⇒ 照旧退回 `<基名>.zip`（一个字都不放宽）。
             */
            string ownPath = !string.IsNullOrWhiteSpace(memberPath)
                ? memberPath!
                : task?.CurrentPath ?? string.Empty;

            string own = FileNameHelper.GetFileName(ownPath);

            /*
             * 拿尺子自比来问"这个名字解析得出来吗"：解析得出来时它必然与自己是同一族同一基名。
             * ⛔ 不新造"能不能解析"的第二把尺子（`Analyze` 是私有的，这里也不该去开它）。
             */
            if (own.Length > 0 && VolumeGroupDetector.BelongsToSameGroup(own, own))
            {
                return own;
            }

            /*
             * ⚠ 2026-10-14（官方环台 `10101010`，日志逐字）：名字**解析不出来**时退回 `<基名>.zip`
             * 那一条等于**假定每一组都是跨盘 ZIP 族** —— 逐字 `这一组的引用名=「pack111-L2.7z.zip」`
             * ⇒ 紧接着 `这一组该拼到哪一层 —— 判不出`（三片都齐了也拼不到一层去）。
             *
             * <para>⇒ 这一格也改成**问卡**（用户口径：「专属算法不就是靠识别的吗」）：卡上说
             * "我是这一组的第一片 / 一片"（`VolumeFirstPiece` / `VolumePiece`）⇒ 拿**卡拼出来的规范名**
             * 当引用名（那正是这一组入口该有的名字，`pack111-L2.7z.txt` ⇒ `pack111-L2.7z.001`）；
             * 卡上也给不出 ⇒ 才退回老口径 `<基名>.zip`（⛔ 一个字都不放宽）。</para>
             */
            if (ownPath.Length > 0)
            {
                string fromCard = VolumeNameOrchestration.ResolveCanonicalNameFromCard(ownPath, FileNature.Inspect(ownPath));

                if (fromCard.Length > 0 && VolumeGroupDetector.BelongsToSameGroup(fromCard, fromCard))
                {
                    return fromCard;
                }

                /*
                 * ⚠ 2026-10-17（本轮判据输入定死）：名字与卡都拼不出规范名时（真机 `aaa-L2.txt`：
                 * 卡上 `分卷身份=VolumeMember` 但 `BodyKind=Unknown`、规范名空），老写法退回
                 * `<基名>.zip` ⇒ 与 `.rar` / `.7z` 族的兄弟**一个都匹配不上**（判据输入逐字：
                 * `这一层文件 3 个｜参照名=「aaa-L2.zip」｜同族候选 0 个`）⇒ 收拢永远收不到一起。
                 *
                 * <p>现在的口径：**拿同层里真实存在的"这一族的成员"当参照名**（卡说它是某族的一片），
                 * 那个名字天然就在这一组里、族也天然对得上。⛔ 找不到才退回 `<基名>.zip`（老兜底不变）。</p>
                 */
                string? ownDirectory = Path.GetDirectoryName(ownPath);

                if (!string.IsNullOrWhiteSpace(ownDirectory) && Directory.Exists(ownDirectory))
                {
                    foreach (string sibling in Directory.EnumerateFiles(ownDirectory))
                    {
                        if (SafePathHelper.PathEquals(sibling, ownPath))
                        {
                            continue;
                        }

                        if (VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(sibling)))
                        {
                            return FileNameHelper.GetFileName(sibling);
                        }
                    }
                }
            }

            return baseName + ".zip";
        }

        /// <summary>
        /// 这一组该**拼到哪一层** = 入口包（引擎要打开、要对它输密码的那一份）所在目录，
        /// 也就是这一组该**落**的那一层（用户 2026-10-05 口径：「谁可以输入密码，就解压到谁那边」；
        /// 见 <see cref="Detection.GroupVolumeDirectory"/>：RAR 分卷 = `.part1.rar`、7z 分卷 = `.7z.001`、
        /// zip 分卷 = `.zip`（跨盘那一族）或 `.zip.001`（7-Zip 切的通用分片））。
        ///
        /// <para><b>判据是"程序性"的（2026-10-11 订正）</b>：答案只能是 **(族, 基名, 盘上事实)** 的纯函数 ——
        /// ① 入口包**在盘上** ⇒ 它所在那一层（纯查盘：任务表里这一组的成员 + 各单产出树里已经落盘的这一组成员，
        /// 一起交给 <see cref="Detection.GroupVolumeDirectory.Resolve"/>）；② 还不在盘上 ⇒ 沿**产出链**推它将来落在哪一层
        /// （与落点实现同一个出口）；③ 两条都答不出 ⇒ **空串 = 这一次不决定**。
        /// ⛔ 顺序只决定"什么时候算得出来"，**不决定"算出来是什么"**。</para>
        ///
        /// <para>⛔ 判据全部转调既有那几把尺子（<see cref="VolumeGroupDetector.BelongsToSameGroup"/> 认"同一组"，
        /// 所以脏名 `111.z0删除2` 照样认得出；<see cref="Detection.GroupVolumeDirectory.Resolve"/> 认"入口包在哪儿"），
        /// ⛔ 这里不自己拼族规则、⛔ 不看盘上还有没有别的兄弟。</para>
        /// </summary>
        /// <param name="memberPath">
        /// **手上这一片**（可选）：它是这一组的成员，拿它的名字当"同一组"那把尺子的参照物
        /// —— ⛔ 不再一律退回 `<基名>.zip`（那只对跨盘 ZIP 族成立）。
        /// ⚠ **闸门**：它的名字必须解析得出一条"**没被改坏**"的线索（见方法体里那句"干不干净"），
        /// 名字带垃圾尾巴的那一档（实测 `333.7z.001.txt`）**不许**启用新判据 —— 那一档连规范名都算不对
        /// （会算出 `333.7z.001.001`），照旧走老口径（行为与改动前逐字相同）。
        /// </param>
        /// <summary>
        /// **这一份算不算"这一组"的成员**（唯一出口）。
        ///
        /// <para>① 名字那把尺子（<see cref="VolumeGroupDetector.BelongsToSameGroup"/>）说"是" ⇒ 是；
        /// ② 名字说"不是"时**问卡**（用户口径：「专属算法不就是靠识别的吗」）：卡上说"我是这一组的一片"
        /// （`VolumeFirstPiece` / `VolumePiece`）**而且基名与这一组有关系**（一个是另一个的前缀也算）⇒ 也算。
        /// 现场（官方环台 `10101010`）：入口那一份叫 `pack111-L2.7z`（卷标记被伪装法吃掉 ⇒
        /// 名字尺子连"像分卷"都判不出）⇒ 它进不了成员表 ⇒ 逐字 `任务表里这一组的单 0 个，入口包=判不出`
        /// ⇒ 三片都齐了也拼不到一层去。</para>
        ///
        /// <para>⛔ 判不出（读不到卡 / 基名对不上）⇒ 照旧"不是"（一个字不放宽）。</para>
        /// </summary>
        private static bool MemberBelongsToGroup(string path, string tailName)
        {
            string name = FileNameHelper.GetFileName(path);

            if (name.Length == 0)
            {
                return false;
            }

            if (VolumeGroupDetector.BelongsToSameGroup(name, tailName))
            {
                return true;
            }

            FileNatureCard card = FileNature.Inspect(path);

            if (!VolumePieceFacts.CardSaysItIsOneOfTheGroup(card))
            {
                return false;
            }

            return VolumeGatherStep.BaseNameRelatedToGroup(name, FileNameHelper.GetArchiveBaseName(tailName));
        }

        private string ResolveBatchGroupDirectory(string baseName, ArchiveTask? task = null, string? memberPath = null)
        {
            /*
             * ⚠ "手上这一片的名字干不干净" = **它自己算出来的规范名就是它自己**（唯一出口
             * `VolumeGroupResolver.TryGetCanonicalVolumeName`）。
             * ⛔ 脏名那一档不许启用新判据：`333.7z.001.txt` 那一档的规范名是 `333.7z.001.001`（错的），
             * 拿它当参照物会牵出一串错名字（实测：`PlacementRulesTests.文件未指定位置…` 多出一个同名目录）。
             * 判不出 ⇒ 当作"不干净" ⇒ 老口径。
             */
            string memberName = FileNameHelper.GetFileName(memberPath ?? string.Empty);
            bool memberNameIsClean = memberName.Length > 0
                && VolumeGroupResolver.TryGetCanonicalVolumeName(memberName, out string memberCanonical)
                && string.Equals(memberCanonical, memberName, StringComparison.OrdinalIgnoreCase);

            /*
             * ⚠ 2026-10-13 订正（困难批 `run-2222`，逐字）：这道"干不干净"的闸门**不许把 `memberPath` 整个丢掉**。
             *
             * 现场：手上这一片叫 `bbb-L2.part98598542删除1.rar`（EEE 式伪装），它的规范名是 `bbb-L2.part1.rar`
             * ⇒ `memberNameIsClean = false` ⇒ 老写法走**两参**重载（`memberPath` 根本没参与）⇒ 引用名退回
             * `<基名>.zip`（**zip 族**）⇒ 与这一组的兄弟（partN 族）`BelongsToSameGroup` **一个都匹配不上**
             * ⇒ 逐字 `这一组的引用名=「bbb-L2.zip」，任务表里这一组的单 0 个` ⇒ `这一组该拼到哪一层 —— 判不出`
             * ⇒ 整组永远缺片 ⇒ ①红。（同一份里名字干净的那一片 `bbb-L2.part2.rar` 反而能判出来：
             * `批末重判 —— 这一组该拼到哪一层 = …\bbb\bbb\bbb`。）
             *
             * ⛔ **安全性一个字没放宽**：`ResolveGroupReferenceName` 内部本来就拿**同一把尺子自比**
             * （`BelongsToSameGroup(own, own)`），解析不出来的名字（`333.7z.001.txt` 那一档）照样退回
             * `<基名>.zip` —— 老口径与它的守门用例行为不变，这里只是把"手上这一片"重新交给那把尺子。
             */
            string tailName = ResolveGroupReferenceName(task, baseName, memberPath);

            var members = new List<string>();

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                string path = candidate?.CurrentPath ?? string.Empty;

                if (path.Length > 0 && MemberBelongsToGroup(path, tailName))
                {
                    members.Add(path);
                }
            }

            /*
             * ⚠ 2026-10-14（walk 档一步步走查的直接结论）：**卡说"我是这一组的一片"时，名字干不干净不该再当闸门**。
             *
             * <para>走查逐字（三片摆在一层目录时）：`pack111-L2.7z.txt` 的卡写着
             * `格式=SevenZip 专属算法=VolumeFirstPiece 自述整包=62919013 按内容算的片数=3`
             * ⇒ 槽位 1、规范名 `pack111-L2.7z.001`、入口=它自己、目标层=它所在那一层 —— **整条链是通的**。
             * 可真跑时它卡在 `任务表里这一组的单 0 个，入口包=判不出`：因为"名字不干净"（`.txt` 尾巴）
             * ⇒ 下面那两段收拢**整段不跑**（连产出树都不扫）⇒ 成员表恒空 ⇒ 那一组永远拼不到一起。</para>
             *
             * <para>⇒ 判据：名字干净 **或** 卡说它是这一组的一片 ⇒ 都按"可用"处理。⛔ 判不出（读不到卡 / 名字也解析不出）
             * ⇒ 照旧老口径（那两段不跑）。</para>
             */
            bool memberUsable = memberNameIsClean
                || (memberName.Length > 0
                    && VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(memberPath!)));

            // 手上这一片也算成员（它可能还没进任务表，或者来自别的单那条链）。
            if (memberUsable && !members.Contains(memberPath!))
            {
                members.Add(memberPath!);
            }

            /*
             * ===== ① 入口包在盘上 ⇒ 它所在那一层 =====
             * 成员除了"任务表里这一组的"，还要包括**各单产出树里已经落盘的这一组成员** ——
             * 分卷的片是别的单解出来的产物，它们不在任务表里（任务表只装用户导入的那些包），
             * 只看任务表就会漏掉入口包（真机 FFFF：入口包 `111_.part1.rar` 是 `111__.rar` 那条链解出来的）。
             * ⛔ 仍然只由那把唯一尺子回答"入口包在哪一层"，⛔ 不按名字单独找入口包
             * （实测踩到：就地整理那一档下"按名字在产出树里找"会把**源文件自己**当成入口）。
             */
            if (memberUsable)
            {
                foreach (ArchiveTask? other in SnapshotTaskTable(Tasks))
                {
                    if (other == null)
                    {
                        continue;
                    }

                    foreach (string directory in new[] { other.OutputPath, other.ContentDirectoryPath, other.RestDirectoryPath })
                    {
                        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                        {
                            continue;
                        }

                        foreach (string candidate in CollectVolumeFilesInDirectory(directory))
                        {
                            if (MemberBelongsToGroup(candidate, tailName)
                                && !members.Contains(candidate))
                            {
                                members.Add(candidate);
                            }
                        }
                    }
                }
            }

            Detection.GroupVolumeDirectory.Entry entry = Detection.GroupVolumeDirectory.Resolve(members);

            if (entry.Directory.Length > 0 && Directory.Exists(entry.Directory))
            {
                return entry.Directory;
            }

            /*
             * ===== ② 入口包**还没落地**：按产出链**一层一层**推它将来落在哪一层（用户 2026-10-06 拍板）=====
             *
             * ⛔ **旧口径（"任务表里第一单那一层"）已作废**：真机 2026-10-06 14:33 那次就是它把入口包
             * `111.zip` 接到了 `111(4)`（日志逐字：`已按规范卷名 111.zip 接到「111(4)」这一层`），
             * 于是列表第一项挂着 48 MB、产物也跟着落错层。用户原话：「**我说过多少遍在 `111.zip` 所在位置**，
             * 你为什么还解压在列表第一项」。
             *
             * 只认一个出口（与落点实现同一份判据 `ResolveProducerEntryArchivePathCore`）：
             * 沿产出链把入口包的落点**逐层叠**出来，取它所在那一层。
             * ⚠ 问的是"**产出入口包**的那一单"，⛔ 不是"谁先吐过一片"。
             */
            foreach (string member in members)
            {
                /*
                 * ⚠ 必须传**这一片自己那一单**（找不到就退回调用方给的那一单）：产出方的判据里有一条
                 * `ReferenceEquals(candidate, task)` —— 传个临时对象进去，等于把"产出方是不是我自己"
                 * 判错（实测踩到：单层路这一档因此拿到空串，收片层退回旧口径、整组凑不齐）。
                 */
                ArchiveTask owner = FindTaskByPath(member) ?? task ?? new ArchiveTask(member, 0);

                string predicted = ResolveProducerEntryArchivePathCore(
                    owner,
                    BuildExtractOptions(tryExtractUnknownFormat: false),
                    member,
                    0);

                if (predicted.Length == 0)
                {
                    continue;
                }

                string? layer = Path.GetDirectoryName(predicted);

                if (!string.IsNullOrWhiteSpace(layer))
                {
                    return layer!;
                }
            }

            /*
             * ===== ③ 判不出 ⇒ **空串 = 这一次不决定**（调用方"什么都不做"，等入口包出现后再问一次）=====
             *
             * ⛔ **这里删掉了一条兜底**（2026-10-11，真机 FFFF 那一批 + 复现用例把它钉死）：
             * 原来最后还问 `ResolveGroupProducerDirectory(baseName)` = "**谁先把这一组的片吐出来**，
             * 就落谁那条链的落点目录"（读 `_groupPieceProducers`）—— 于是这一组的层**由时序决定**：
             * 同一份输入，谁先跑完谁定层。真机那一批就是它把四片全收进了 `1113__.rar` 那条链的层
             * （它只是恰好第一个吐出 part3），而真正的入口包 `111_.part1.rar` 是**最后**才出来的
             * （它得先穿两层 rar→zip），反被搬进别人的层（盘上同一个 inode 挂了三四个名字，
             * `fsutil hardlink list` 逐字可查）。
             * ⇒ **"谁先到"不许当判据**；判不出时正解是"这一次不决定"，不是"先占一层"。
             * ⛔ `_groupPieceProducers` 那份账照旧保留：它回答"有哪些片被产出来了 / 该谁收场"，
             *   **不回答层**（谁先产出一片与"入口包在哪"是两件事）。
             */
            string observed = !string.IsNullOrWhiteSpace(memberPath)
                ? memberPath!
                : task?.CurrentPath ?? string.Empty;
            string entryName = VolumeGroupDetector.TryGetFirstVolumeName(FileNameHelper.GetFileName(observed))
                ?? string.Empty;
            string producerName = "判不出";

            foreach (string member in members)
            {
                ArchiveTask owner = FindTaskByPath(member) ?? task ?? new ArchiveTask(member, 0);

                if (FindProducerTaskForPackage(owner, member, entryName) is { } producerTask)
                {
                    producerName = producerTask.LogName;
                    break;
                }
            }

            /*
             * ⚠ 这一行是"判不出"那一档的**判据输入**（规矩：只拿一份用户日志就能定位）——
             * 它此前一个字都不写，真机 FFFF 那一批就是卡在这里看不出是"尺子不认"还是"产出方找不到"。
             */
            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CriterionInputsGroupLayerFormat,
                    FileNameHelper.GetFileName(observed),
                    tailName,
                    members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    entryName.Length == 0 ? "判不出" : entryName,
                    producerName,
                    memberNameIsClean ? "是" : "否",
                    memberUsable ? "是" : "否",
                    observed.Length > 0 ? FileNature.Inspect(observed).BodyKind.ToString() : "（没有这一片）",
                    $"task={(task == null ? "null" : "有")} memberPath={(string.IsNullOrWhiteSpace(memberPath) ? "空" : "有")}",
                    observed,
                    SafeFileSizeText(observed)));

            return string.Empty;
        }

        /// <summary>按路径找任务表里那一单（找不到 ⇒ null；⛔ 只读，不新建）。</summary>
        /// <summary>"这一份多大"（判据输入用；读不到 ⇒ "判不出"）。</summary>
        private static string SafeFileSizeText(string path)
        {
            try
            {
                return File.Exists(path)
                    ? new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "（不在盘上）";
            }
            catch
            {
                return "判不出";
            }
        }

        /// <summary>这一份是不是已经在某个「其余物」目录里（按路径段判；是 ⇒ 不再搬第二遍）。</summary>
        private static bool IsInsideProcessArtifactDirectory(string path)
        {
            string? directory = Path.GetDirectoryName(path);

            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (ProcessArtifactLayout.IsArtifactDirectoryName(Path.GetFileName(directory)))
                {
                    return true;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return false;
        }

        private ArchiveTask? FindTaskByPath(string path)
        {
            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null)
                {
                    continue;
                }

                if (SafePathHelper.PathEquals(candidate.CurrentPath, path))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// **把这一片收进"那一组该在的那一层"**（批末补齐那一站用的唯一出口）。
        /// </summary>
        /// <param name="memberPath">
        /// 手上这一片（可选）：<c>null</c> ⇒ 老口径（拿这一单自己的包基名当参照物）。
        /// 传了它 ⇒ 参照物换成它自己的名字（停的是**别人那一组**时唯一说得清的办法）。
        /// </param>
        /// <summary>
        /// 这一层里与"这一单手上那一片"**同族同基名**的文件（收拢决策器的候选池；⛔ 只读这一层、不递归）。
        /// 判据 = 既有宽尺子 `VolumeGroupDetector.BelongsToSameGroup`，参照名 = 手上那一片。
        /// </summary>
        /// <summary>枚举一个目录里的**所有**文件（读不动 ⇒ 空表，不抛）。</summary>
        private static IEnumerable<string> SafeEnumerateFiles(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory.GetFiles(directory);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private List<string> CollectGroupCandidatePaths(ArchiveTask task, string entryLayer, out string referenceName, out int scannedInEntryLayer)
        {
            var paths = new List<string>();

            referenceName = string.Empty;
            scannedInEntryLayer = 0;

            if (string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                return paths;
            }

            string observed = ResolveDeferredGroupMemberPath(task);
            string reference = ResolveGroupReferenceName(task, FileNameHelper.GetArchiveBaseName(observed), observed);

            referenceName = reference;

            if (reference.Length == 0)
            {
                return paths;
            }

            /*
             * ⚠ 2026-10-17（运行期判据输入定死）：参照名退回 `<基名>.zip` 时（`pack111-L2.7z.zip`、`aaa-L2.zip`），
             * 与 `.7z` / `.rar` 族的兄弟**一个都对不上**（逐字 `这一层文件 3 个｜同族候选 0 个`）——
             * 而那一层里明明躺着真成员。⇒ **参照名允许改由"真实成员"充当**：
             * 先把四档来源的文件都收集起来（不过滤），若参照名自己都过不了那把尺子（`BelongsToSameGroup(ref, ref)`），
             * 就从真实文件里挑第一个"卡说它是这一族一片"的，用它的名字当参照名。
             * ⛔ 判不出（一个成员都没有）⇒ 照旧空结果（兜底不变）。
             */
            var allFiles = new List<string>();

            void CollectAll(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in CollectVolumeFilesInDirectory(directory!))
                {
                    if (!allFiles.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        allFiles.Add(file);
                    }
                }
            }

            /*
             * 源层候选：`OriginalPath` 是用户导入那一刻的位置，⛔ 不是过程物层。
             * 分卷片被包装壳拆到不同源目录时（2026-10-19 `3333`：`.002` / `.003` 各在一个
             * `333(2)` / `333(3)` 源目录里），只收 `CurrentPath`/`OutputPath` 永远看不到整组。
             * 这里按**整层文件**枚举，收不收仍由唯一出口 `AcceptsAsGroupPiece` 回答。
             */
            void CollectAllFromSourceLayer(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in SafeEnumerateFiles(directory!))
                {
                    if (!allFiles.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        allFiles.Add(file);
                    }
                }
            }

            /*
             * **源树的“根”就是带 recipe.json 的那一层**（造包器写下的样本根）。
             * 只从单份任务的 OriginalPath 找，会漏掉同一份样本里**没被导入成任务**的裸片
             * （2026-10-19 `3333`：`.002` / `.003` 没有任务，只有源文件）。
             * ⛔ 只扫这一棵源树、文件上限 200，收不收仍由 `AcceptsAsGroupPiece` 回答。
             */
            void CollectAllFromSourceTree(string? root)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return;
                }

                int collected = 0;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(root!, "*", SearchOption.AllDirectories))
                    {
                        if (++collected > 200)
                        {
                            break;
                        }

                        if (!allFiles.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                        {
                            allFiles.Add(file);
                        }
                    }
                }
                catch
                {
                    // 读不动就保留已经收进来的部分。
                }
            }

            /*
             * 运行根里没有 `recipe.json`（`bt30` 只复制样本文件）⇒ 不能靠它找源树根。
             * 从 `OriginalPath` 往上最多 3 层；到批根 `大测试原包-困难` / `大测试指定位置-困难`
             * 就停下（⛔ 不扫进别的样本）。每层递归收、文件上限 200，收不收仍由唯一出口回答。
             */
            void CollectAllFromSourceAncestors(string? path)
            {
                string? dir = string.IsNullOrWhiteSpace(path)
                    ? null
                    : Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(path));

                for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(dir); up++)
                {
                    string name = Path.GetFileName(dir);

                    if (name.Contains("大测试原包", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("大测试指定位置", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    CollectAllFromSourceTree(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }

            CollectAll(entryLayer);
            scannedInEntryLayer = allFiles.Count;

            string ownLayer = Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(observed)) ?? string.Empty;

            CollectAll(ownLayer);

            /*
             * ===== 卡自证"我是 zip 分卷的一片"时，**再往外找一层**（用户 2026-10-17：底层识别先）=====
             * 现场（新样本 `6666` 的 `recipe.json` 逐字）：同一组三片分处
             * `打包111\打包111-W0-0\`、`打包111(2)\打包111(2)\打包111(2)\`、`打包111(3)\打包111(3)\` ——
             * 名字也全烂（`打包111-W0-0` / `…W1-0.png` / `…L2.zip.000003`）。
             * ⛔ 只在"入口那一份卡上确实是 zip 分片"时才补扫（`Format=Zip` 且 成员/EOCD/本地头 之一）；
             * ⛔ 共用找卷窗口一个字不改（上一轮试放大它打红了既有守门：`CrossLayerVolumeGatherTests…`）；
             * ⛔ 补扫有上限（目录 ≤ 24 个、文件 ≤ 400 个），且收进来的还要过后面那几道闸门。
             */
            bool entryIsZipPiece;

            try
            {
                FileNatureCard entrySeat = FileNature.Inspect(observed);

                /*
                 * ⚠ 2026-10-18 新批 `3333`/`4444`/`7777`/`9999`：**7z / RAR 的分卷片同样会散在
                 * 包装壳的产出目录里**，补扫不能只给 ZIP 开。判据仍读卡：格式认得出，且卡上自证
                 * "是这一族的一片"（BodyKind / 成员位）—— 名字不参与这一道门。
                 */
                entryIsZipPiece = entrySeat.Format != VolumeContentFormat.Unknown
                    && (entrySeat.HasZipEndRecord
                        || entrySeat.HasZipLocalHeader
                        || entrySeat.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                        || entrySeat.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece);
            }
            catch
            {
                entryIsZipPiece = false;
            }

            /*
             * 补扫那一侧要用的两个判据输入（与本地那一侧同源）：基名 + "第 1 片有多大"的锚点。
             */
            string sweepReferenceBase = FileNameHelper.GetArchiveBaseName(reference);
            long sweepAnchorSize = 0;

            try
            {
                FileNatureCard sweepAnchorSeat = FileNature.Inspect(observed);

                if (sweepAnchorSeat.HasZipLocalHeader && sweepAnchorSeat.Size > 0)
                {
                    sweepAnchorSize = sweepAnchorSeat.Size;
                }
            }
            catch
            {
                sweepAnchorSize = 0;
            }

            if (entryIsZipPiece)
            {
                try
                {
                    string observedFull = SafePathHelper.GetFullPathSafe(observed);
                    string? parentDirectory = Path.GetDirectoryName(observedFull);
                    string? grandParent = string.IsNullOrWhiteSpace(parentDirectory)
                        ? null
                        : Directory.GetParent(parentDirectory)?.FullName;

                    /*
                     * 起点要**再往上两层**（现场 `6666`：入口在 `…\打包111(3)\打包111(3)\`，
                     * 同组另外两片在 `…\打包111\打包111-W0-0\` 与 `…\打包111(2)\打包111(2)\打包111(2)\` ——
                     * 从 `打包111(3)` 起扫永远够不到兄弟目录的下一层）。
                     */
                    /*
                     * ⚠ 2026-10-18（运行期坐实：`run-19191919` 09:20:09 逐字 `补扫根=…\大测试原包-困难
                     * ｜实际扫了 66 个目录 / 2 个文件`）：**往上 3 层会越过样本根**，指到装着 30 份样本的父目录，
                     * 目录预算（40）当场被兄弟样本目录吃光 ⇒ 这一组真正的片一片都没走到。
                     * ⇒ 收窄上界：从祖父目录再往上时，只有当"待上溯的那个目录"里存在**名字以本组基名前缀开头**
                     * 的子目录时才继续往上（`6666` 靠 `打包111(2)`/`打包111(3)` 成立；`19191919` 的那一层
                     * 兄弟叫 `6666`/`8888`/…，一个都对不上 ⇒ 停在样本根本身）。⛔ 判不出前缀 ⇒ 不上溯。
                     */
                    string? scanRoot = grandParent;

                    if (!string.IsNullOrWhiteSpace(grandParent))
                    {
                        string prefix = FileNameHelper.GetArchiveBaseName(referenceName);
                        int dash = prefix.IndexOf('-');

                        if (dash > 0)
                        {
                            prefix = prefix[..dash];
                        }

                        string? candidateRoot = grandParent;

                        for (int up = 0; up < 3; up++)
                        {
                            string? parent = Directory.GetParent(candidateRoot!)?.FullName;

                            if (string.IsNullOrWhiteSpace(parent) || prefix.Length == 0)
                            {
                                break;
                            }

                            bool related = false;

                            try
                            {
                                foreach (string sub in Directory.GetDirectories(parent))
                                {
                                    if (Path.GetFileName(sub).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                    {
                                        related = true;
                                        break;
                                    }
                                }
                            }
                            catch
                            {
                                related = false;
                            }

                            if (!related)
                            {
                                break;
                            }

                            candidateRoot = parent;
                        }

                        scanRoot = candidateRoot;
                    }

                    if (!string.IsNullOrWhiteSpace(scanRoot) && Directory.Exists(scanRoot))
                    {
                        int directories = 0;
                        int files = 0;
                        /*
                         * ⛔ **由近及远**（2026-10-18 困难批 `30303030` 定案）：原来只从 `scanRoot` 向下 BFS，
                         * 而那一轮 `scanRoot` 是**批次根**（30 份样本的父目录）⇒ 40 个目录预算全花在
                         * `10101010`/`1111` 等别的样本上，本组的兄弟目录一个都没轮到（逐字：
                         * `实际扫了 75 个目录 / 8 个文件`，全是别组的文件）⇒ 整组凑不齐。
                         * ⇒ 种子 = **这一单自己那一层 + 往上 3 层**（近的优先）；只有一层都取不到时才退回 `scanRoot`。
                         * ⛔ 判据（`AcceptsAsGroupPiece`）一个字不改，只改"先扫哪儿"。
                         */
                        var sweepSeeds = new List<string>();
                        string? seedCursor = Path.GetDirectoryName(
                            SafePathHelper.GetFullPathSafe(task.CurrentPath ?? string.Empty));

                        for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(seedCursor); up++)
                        {
                            if (Directory.Exists(seedCursor))
                            {
                                sweepSeeds.Add(seedCursor!);
                            }

                            seedCursor = Path.GetDirectoryName(seedCursor);
                        }

                        if (sweepSeeds.Count == 0)
                        {
                            sweepSeeds.Add(scanRoot!);
                        }

                        /*
                         * 源层也要进补扫种子：分卷片可能先在别的包装壳里产出，再落回
                         * `OriginalPath` 那一层；只从 `CurrentPath`（过程物层）往上找够不到
                         * （2026-10-19 `3333`：`.002` / `.003` 在兄弟源目录里）。
                         */
                        string? sourceSeed = Path.GetDirectoryName(
                            SafePathHelper.GetFullPathSafe(task.OriginalPath ?? string.Empty));

                        for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(sourceSeed); up++)
                        {
                            if (Directory.Exists(sourceSeed)
                                && !sweepSeeds.Any(existing => string.Equals(existing, sourceSeed, StringComparison.OrdinalIgnoreCase)))
                            {
                                sweepSeeds.Add(sourceSeed!);
                            }

                            sourceSeed = Path.GetDirectoryName(sourceSeed);
                        }

                        var frontier = new List<string>(sweepSeeds);
                        var sweepTrace = new List<string>();
                        var visited = new HashSet<string>(sweepSeeds, StringComparer.OrdinalIgnoreCase);

                        for (int level = 0; level < 8 && frontier.Count > 0 && directories < 200; level++)
                        {
                            var next = new List<string>();

                            foreach (string directory in frontier)
                            {
                                try
                                {
                                    foreach (string sub in Directory.GetDirectories(directory))
                                    {
                                        if (directories++ >= 40)
                                        {
                                            break;
                                        }

                                        if (visited.Add(sub))
                                        {
                                            next.Add(sub);
                                        }
                                    }
                                }
                                catch
                                {
                                    // 读不动的目录跳过（不影响已收到的候选）。
                                }
                            }

                            foreach (string directory in frontier.Concat(next))
                            {
                                /*
                                 * ⚠ 2026-10-18（运行期逐份判据输入定案）：这里原来用 `CollectVolumeFilesInDirectory`
                                 * （按**后缀**筛"像不像卷文件"），于是 `bbb-L2.zip-658` 这种"后缀被改坏、内容却是
                                 * 末片"的文件**根本没被枚举**（同一份日志里逐份只出现 3 个名字，第三个名字一次没出现）。
                                 * ⇒ 补扫改枚举**整层所有文件**，由唯一出口 `AcceptsAsGroupPiece`（内容先行）来收/挡；
                                 * 文件预算 400 只当上限用，⛔ 不再用后缀当前置筛子。
                                 */
                                foreach (string file in SafeEnumerateFiles(directory))
                                {
                                    if (++files > 400)
                                    {
                                        break;
                                    }

                                    try
                                    {
                                        FileNatureCard card = FileNature.Inspect(file);

                                        /*
                                         * ===== 补扫锚点：末片没有本地头时，从**本组中段片**取满片尺寸 =====
                                         * （2026-10-18 新批 `24242424` 运行期定案）跨盘 ZIP 的入口是末片
                                         * （`EOCD=True`、没有本地头）⇒ `sweepAnchorSize` 原来是 0 ⇒ 字节数守恒整档
                                         * 不启用 ⇒ 从数据中间切开的第 2 片（`333-L2.pdf`，`Format=Unknown`）永远进不了
                                         * 候选池，组只剩 2 片。
                                         * 判据：只拿**没有 EOCD 的、基名相关的、内容没证明不是成员**的片当锚点
                                         * （末片自己绝不能当锚点）；取第一份这样的片的大小，后续同尺寸中段片就能过 ③。
                                         * ⛔ 只是把"锚点"补上，`AcceptsAsGroupPiece` 的阶梯一个字没改。
                                         */
                                        if (sweepAnchorSize <= 0
                                            && sweepReferenceBase.Length > 0
                                            && !card.HasZipEndRecord
                                            && card.Size > 0
                                            && card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                                            && card.BodyKind != ArchiveBodyKind.SingleComplete
                                            && VolumeGatherStep.BaseNameRelatedToGroup(card.ArchiveBaseName, sweepReferenceBase))
                                        {
                                            sweepAnchorSize = card.Size;
                                        }

                                        /* 与本地那一侧**同一个出口**（含字节数守恒 ⇒ 中间片才收得进来）。 */
                                        bool accepted = VolumeGatherStep.AcceptsAsGroupPiece(card, reference, sweepReferenceBase, sweepAnchorSize);

                                        if (sweepTrace.Count < 20)
                                        {
                                            sweepTrace.Add(
                                                $"{FileNameHelper.GetFileName(file)}〔F={card.Format} EOCD={card.HasZipEndRecord} LH={card.HasZipLocalHeader} "
                                                + $"成员={card.Membership} 种类={card.BodyKind} 大小={card.Size} 准入={accepted}〕");
                                        }

                                        if (accepted
                                            && !allFiles.Any(existing => string.Equals(
                                                existing, file, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            allFiles.Add(file);
                                        }
                                    }
                                    catch
                                    {
                                        // 单份读不出来跳过。
                                    }
                                }
                            }

                            frontier = next;
                        }

                        /*
                         * **判据输入永久留证**（用户 2026-10-17：「判据输入必须写进日志」）：
                         * 补扫到底扫了哪儿、收进几份 —— 没有这一行，"远处那两片到底收没收进来"
                         * 只能靠猜（这一轮就是靠断点读 `paths` 才看出来的）。
                         */
                        AppendLog(
                            "INFO",
                            $"{task.LogName}：收拢补扫 —— 入口卡自证是 zip 分片；补扫根={scanRoot}（入口往上 3 层）"
                            + $"｜下探最多 8 层、目录上限 200、文件上限 400｜实际扫了 {directories} 个目录 / {files} 个文件"
                            + $"｜锚点={sweepAnchorSize} 基名={sweepReferenceBase}"
                            + $"｜候选池里现在 {allFiles.Count} 份"
                            + (sweepTrace.Count > 0 ? $"｜逐份：[{string.Join("；", sweepTrace)}]" : string.Empty));
                    }
                }
                catch
                {
                    // 补扫失败 ⇒ 与没有这一档逐字相同。
                }
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null)
                {
                    continue;
                }

                CollectAll(Path.GetDirectoryName(candidate.CurrentPath ?? string.Empty));
                CollectAll(candidate.OutputPath);
                CollectAll(candidate.ContentDirectoryPath);
                CollectAll(candidate.RestDirectoryPath);
                CollectAllFromSourceLayer(Path.GetDirectoryName(candidate.OriginalPath ?? string.Empty));
                CollectAllFromSourceAncestors(candidate.OriginalPath);
            }

            /*
             * ⛔ **不能拿"参照名自己过不过尺子"当闸门**（2026-10-17 实测）：`pack111-L2.7z.zip` 自己是个合法的
             * zip 名 ⇒ `BelongsToSameGroup(ref, ref)` = **true**，于是兜底永远不触发、层里明明有真成员却判 0 个。
             * 现在的闸门 = **这个参照名在收集到的文件里到底存不存在**（不存在 ⇒ 说明它是拼出来的兜底名）。
             */
            bool referenceIsRealFile = allFiles.Any(file =>
                string.Equals(FileNameHelper.GetFileName(file), reference, StringComparison.OrdinalIgnoreCase));

            if (!referenceIsRealFile)
            {
                /*
                 * 三档递进找参照名（⛔ 都找不到就保持 `<基名>.zip` 的老兜底）：
                 * ① 卡说"我是这一族的一片"的；② **名字读得出卷号**的（7z/RAR 的伪装片按内容认不出，
                 * 只有名字这一条线索）；③ 一个都不满足 ⇒ 老兜底。
                 */
                string? byCard = null;
                string? byVolumeName = null;

                foreach (string file in allFiles)
                {
                    string name = FileNameHelper.GetFileName(file);

                    if (byCard == null && VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(file)))
                    {
                        byCard = name;
                    }

                    if (byVolumeName == null && VolumeGroupDetector.TryGetVolumeIndex(name) != null)
                    {
                        byVolumeName = name;
                    }

                    if (byCard != null && byVolumeName != null)
                    {
                        break;
                    }
                }

                string? chosen = byCard ?? byVolumeName;

                if (chosen != null)
                {
                    reference = chosen;
                    referenceName = reference;
                }
            }

            string referenceBase = FileNameHelper.GetArchiveBaseName(reference);

            /*
             * 通用分片族的"第 1 片有多大" —— 文档 §6.6 A 的**字节数守恒**要用的锚点：
             * 从收集到的候选里找"开头是本地文件头"的那一份；找不到 ⇒ 0（这一档不启用）。
             */
            long zipFullSize = 0;

            if (entryIsZipPiece)
            {
                foreach (string file in allFiles)
                {
                    try
                    {
                        FileNatureCard anchorCard = FileNature.Inspect(file);

                        if (anchorCard.HasZipLocalHeader && anchorCard.Size > 0)
                        {
                            zipFullSize = anchorCard.Size;
                            break;
                        }
                    }
                    catch
                    {
                        // 单份读不出来就跳到下一份。
                    }
                }
            }

            /*
             * ⚠ 本地这一侧也补同一个锚点（2026-10-18 新批 `24242424`）：本组可能**一片带本地头的都没有**
             * （跨盘 ZIP 的中段片起在数据中间）⇒ `zipFullSize=0` ⇒ 下面这一遍又把补扫刚收进来的
             * `Unknown` 中段片（`333-L2.pdf`）筛掉。补扫那一侧已经从"没有 EOCD、基名相关"的片取了锚点，
             * 这一侧原样沿用；⛔ 只补锚点，判据阶梯不改。
             */
            if (zipFullSize <= 0)
            {
                /*
                 * 7z/RAR 的首卷卡上有“自述片数 / 自述整包字节数”，用**首卷自己的大小**
                 * 当满片锚点；中间片/末片本身没有魔数，这是唯一能量出来的锚点。
                 */
                try
                {
                    FileNatureCard anchorSeat = FileNature.Inspect(observed);

                    if (anchorSeat.DeclaredVolumeCount > 0
                        && anchorSeat.Size > 0
                        && anchorSeat.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                    {
                        zipFullSize = anchorSeat.Size;
                    }
                }
                catch
                {
                    // 读不出来就维持 0（这一档不启用）。
                }
            }

            if (zipFullSize <= 0)
            {
                /*
                 * `observed` 可能只是包装壳（卡上没有自述）；首卷自述在候选池里的另一份卡上。
                 * 这是唯一一个“内容能给出满片尺寸”的出口，⛔ 不按名字猜大小。
                 */
                foreach (string candidateFile in allFiles)
                {
                    try
                    {
                        FileNatureCard candidateSeat = FileNature.Inspect(candidateFile);

                        if (candidateSeat.DeclaredVolumeCount > 0
                            && candidateSeat.Size > 0
                            && candidateSeat.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                        {
                            zipFullSize = candidateSeat.Size;
                            break;
                        }
                    }
                    catch
                    {
                        // 单份读不出来就跳过。
                    }
                }
            }

            if (zipFullSize <= 0)
            {
                zipFullSize = sweepAnchorSize;
            }

            foreach (string file in allFiles)
            {
                /*
                 * **只问一个出口**（`AcceptsAsGroupPiece`）：底层识别先行、名字只保底。
                 * ⛔ 原来这里与补扫那一侧各有一套阶梯，于是"中间片"在补扫里永远进不来
                 * （`19191919` 的第二片就是这样被挡掉的）。
                 */
                if (VolumeGatherStep.AcceptsAsGroupPiece(FileNature.Inspect(file), reference, referenceBase, zipFullSize))
                {
                    paths.Add(file);
                    continue;
                }

                /*
                 * ===== 名字不放行时**问卡**（用户 2026-10-17 总则：底层识别优先）=====
                 * 现场（调试器逐字，`10101010`，`VolumeGatherPlanner.Decide` 帧 0）：
                 * `Complete=false｜PieceCount=1｜自述 3 片 / 62919013｜合计 26214400` ——
                 * 卡完全认得出这一组，可决策那一刻候选里**只有 1 片**：`pack111-L2.7z` 的卷标记
                 * 被伪装法吃掉，名字那把尺子把它判成"不是这一组" ⇒ 自述永远对不上。
                 * 判据：卡说它是这一族的一片（`CardSaysItIsOneOfTheGroup`）**且基名与本组相关**
                 * （既有弱证据尺子 `BaseNameRelatedToGroup`）⇒ 收进候选。
                 * ⛔ 后面那些闸门（同号两份 / 体积规律 / 整组自洽 / 试开）一条不落。
                 */
                if (referenceBase.Length > 0
                    && VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(file))
                    && VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetArchiveBaseName(file), referenceBase))
                {
                    paths.Add(file);
                    continue;
                }

                /*
                 * ===== 名字与基名**全被改烂**的那一档：只认卡上的 zip 分片事实 =====
                 * （现场新样本 `6666`：三片叫 `打包111-W0-0` / `…W1-0.png` / `…L2.zip.000003`，
                 *  基名一个都对不上，但卡上全是 zip 流：一片有本地头、一片是中间片、一片尾部有 EOCD。）
                 * ⛔ 只在"入口那一份也是 zip 分片"时才这么做（`entryIsZipPiece`），
                 *   免得把同层别的 zip 包卷进来；后面那几道闸门照旧。
                 */
                if (entryIsZipPiece)
                {
                    FileNatureCard candidateCard = FileNature.Inspect(file);

                    /*
                     * ⚠ 单卷 zip 也有 EOCD，但它**可证不是分卷成员**（`BodyKind=SingleComplete`）⇒ 不收。
                     * ⚠ 中间片的名字与头都认不出（现场 `6666`：`打包111-W1-0.png`，头是数据中间切开的）
                     *   ⇒ 用文档 §6.6 A 的**字节数守恒**这条证据：与"第 1 片"同样大的、且没有被内容证明
                     *   不是成员的，才当中间片。
                     */
                    bool isZipPiece = candidateCard.Format == VolumeContentFormat.Zip
                        && candidateCard.BodyKind != ArchiveBodyKind.SingleComplete
                        && (candidateCard.HasZipEndRecord
                            || candidateCard.HasZipLocalHeader
                            || candidateCard.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember);

                    bool isByteConservedMiddle = !isZipPiece
                        && zipFullSize > 0
                        && candidateCard.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                        && candidateCard.Size > 0
                        && Math.Abs(candidateCard.Size - zipFullSize) <= Math.Max(1, zipFullSize / 100);

                    if (isZipPiece || isByteConservedMiddle)
                    {
                        paths.Add(file);
                    }
                }
            }

            /*
             * **被筛掉的那几份要能看见为什么**（用户 2026-10-17：「判据输入必须写进日志」）。
             * 只在"确实丢了东西"时写（收集到的比收进来的多），否则正常任务不加噪声。
             * 逐条给：文件名 / 名字那把尺子认不认 / 卡上的分卷身份与 BodyKind / 卡上的基名。
             */
            if (allFiles.Count > paths.Count)
            {
                var dropped = new List<string>();

                foreach (string file in allFiles)
                {
                    string candidateName = FileNameHelper.GetFileName(file);

                    if (paths.Contains(file))
                    {
                        continue;
                    }

                    FileNatureCard droppedCard = FileNature.Inspect(file);

                    dropped.Add(
                        candidateName
                        + "〔名字同组=" + (VolumeGroupDetector.BelongsToSameGroup(candidateName, reference) ? "是" : "否")
                        + $"｜分卷身份={droppedCard.Membership}"
                        + $"｜BodyKind={droppedCard.BodyKind}"
                        + $"｜卡上基名={droppedCard.ArchiveBaseName}"
                        + $"｜基名与本组相关={(VolumeGatherStep.BaseNameRelatedToGroup(droppedCard.ArchiveBaseName, referenceBase) ? "是" : "否")}〕");
                }

                AppendLog(
                    "WARN",
                    $"{task.LogName}：这一层收集到 {allFiles.Count} 份、收进候选 {paths.Count} 份 —— 被筛掉的："
                    + string.Join("；", dropped)
                    + $"｜参照名=「{reference}」参照基名=「{referenceBase}」");
            }

            return paths;
        }

        /// <summary>
        /// **手上那一片的取法 = 唯一一处**（用户 2026-10-17：删掉 `memberPath` / `task.CurrentPath`
        /// 两套取法）。调用方给了 `memberPath` 就用它（接片那条路手上就是别人那一份），
        /// 否则用这一单账上的路径；两个都没有 ⇒ 空串（下游按"判不出"办）。
        /// </summary>
        private static string ResolveObservedPiecePath(ArchiveTask? task, string? memberPath)
            => !string.IsNullOrWhiteSpace(memberPath)
                ? memberPath!
                : task?.CurrentPath ?? string.Empty;

        /// <summary>
        /// 收片 / 组读取之间传槽位用的**键**：盘上物理身份（卷序列号:文件索引）；判不出身份才退回完整路径。
        /// <para>⛔ 用身份不用路径（2026-10-18 困难批 `19191919` 实测）：接片会移动 / 建硬链接，
        /// 路径随动作而变 —— 按原路径查表会落空、又退回"名字优先"，两片就撞同一个规范名。</para>
        /// </summary>
        private static string VolumeSlotKey(string path)
        {
            FileNatureCard card = FileNature.Inspect(path);

            return card.HasIdentity
                ? card.VolumeSerial.ToString("X8", CultureInfo.InvariantCulture)
                  + ":"
                  + card.FileIndex.ToString("X", CultureInfo.InvariantCulture)
                : SafePathHelper.GetFullPathSafe(path);
        }

        /// <summary>
        /// **按试开确认的顺序，先清掉入口层里"旧规范名"**（2026-10-18 新批 `24242424` 定案）。
        ///
        /// <para>现场：入口层已有 `.z01`/`.z02` 两个**我们自己早先建的硬链接**，但内容顺序把另一份中段片
        /// 排到槽位 1 ⇒ 接片那一档按内容算出的规范名与旧链接撞名（目标名已被别的文件占着）⇒ 一片接不进、
        /// 整组永远缺卷。旧链接是零字节硬链接（linkCount &gt; 1），删掉名字不丢字节；而用户源包 / 任务源路径
        /// 一律不碰（⛔ 红线不变）。</para>
        ///
        /// <para>清完再由既有接片出口按内容槽位建正确的规范名；⛔ 不在这里建链接、不碰非规范名的原件。</para>
        /// </summary>
        private void PrepareCanonicalVolumeNamesForConfirmedOrder(
            string entryLayer,
            IReadOnlyList<string> orderedPieces,
            string stem,
            VolumeNamingFamily family,
            string logName)
        {
            IReadOnlyList<string> names = VolumeNumberFromContent.BuildStandardNames(stem, family, orderedPieces.Count);

            if (names.Count != orderedPieces.Count)
            {
                return;
            }

            for (int index = 0; index < names.Count; index++)
            {
                string target = Path.Combine(entryLayer, names[index]);

                if (!File.Exists(target))
                {
                    continue;
                }

                bool samePhysical = FileIdentity.IsSamePhysicalFile(target, orderedPieces[index]);
                bool hasLinks = FileIdentity.TryGetLinkCount(target, out uint linkCount);
                bool userSource = IsUserImportedSourcePath(target);
                bool taskSource = IsAnyTaskSourcePath(target);

                AppendLog(
                    "INFO",
                    $"{logName}：规范名预清理判据 —— {names[index]}"
                    + $"｜同一份={samePhysical}"
                    + $"｜链接数={(hasLinks ? linkCount.ToString(CultureInfo.InvariantCulture) : "判不出")}"
                    + $"｜用户源={userSource}｜任务源={taskSource}");

                if (samePhysical
                    || !hasLinks
                    || linkCount <= 1
                    || userSource
                    || taskSource)
                {
                    continue;
                }

                try
                {
                    File.Delete(target);

                    AppendLog(
                        "INFO",
                        $"{logName}：按试开确认的顺序，收掉入口层里旧的规范名 {names[index]}"
                        + "（零字节硬链接，删名字不丢字节）");
                }
                catch (Exception ex)
                {
                    AppendLog("WARN", $"{logName}：旧的规范名 {names[index]} 没能收掉（{ex.Message}）");
                }
            }
        }

        private void TryGatherGroupPiecesInto(ArchiveTask task, string entryLayer, string? memberPath = null, bool allowMoveIntoGroup = true, string? cardEntryPath = null, ArchiveFixer.Detection.CardVolumeGroupReading? cardEvidence = null)
        {
            if (task == null || string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                return;
            }

            /*
             * 候选 = ①这一单账上那几卷 + ②**全表里同组的每一条**（真机 CCCC：`.z03` 那一单在
             * `111(4)\`，它自己还没轮到重判 ⇒ 只看这一单的账就会漏掉它，整组永远停在 3 卷）。
             * ⛔ "同组"仍由既有尺子回答（`BelongsToSameGroup`），⛔ 接片仍只硬链接、绝不搬用户文件。
             */
            string tailName = ResolveGroupReferenceName(
                task,
                FileNameHelper.GetArchiveBaseName(ResolveObservedPiecePath(task, memberPath)),
                memberPath);
            var sources = new List<string>(task.VolumePaths);

            /*
             * ⛔ **组读取排出来的那几片本身就是源**（2026-10-18，困难批 `19191919` 实测）：
             * `sources` 原来只收"任务表里的同组成员"，而第 3 片（`bbb-L2.zip-658`）既不是任务、
             * 也不在任何一单的其余物里（它被扫描判成"不是压缩包"⇒ 跳过）⇒ 它永远接不进来
             * ⇒ 整组缺末片 ⇒ `contentBytes=0`。内容已经把它排进槽位 3 ⇒ 原样当源收下，
             * 接片仍走它自己的闸门（硬链接 / 不碰用户源包）。
             */
            if (cardEvidence != null)
            {
                foreach (ArchiveFixer.Detection.CardVolumeSlot slot in cardEvidence.Slots)
                {
                    if (slot.Path.Length > 0 && File.Exists(slot.Path))
                    {
                        sources.Add(slot.Path);
                    }
                }
            }
            string groupBaseForScan = FileNameHelper.GetArchiveBaseName(ResolveObservedPiecePath(task, memberPath));

            foreach (ArchiveTask other in SnapshotTaskTable(Tasks))
            {
                string path = other?.CurrentPath ?? string.Empty;

                if (path.Length == 0 || !File.Exists(path))
                {
                    continue;
                }

                if (MemberBelongsToGroup(path, tailName))
                {
                    sources.Add(path);
                    continue;
                }

                /*
                 * ⚠ 2026-10-16（真机 `29292929` 的永久判据输入逐字）：片名被改烂时，**名字那把尺子**
                 * （`MemberBelongsToGroup`）会把**同组的兄弟片**判成"不是这一组"（`pack111-L2` 对
                 * `tailName=pack111-L2.zip`）⇒ 这一趟只收到入口自己 ⇒ 兄弟片永远接不进来，
                 * 而接片那一档又借不到组（同一时刻 `任务表首卷份数=0`，见 §29.25）。
                 *
                 * <para>⇒ 补一条**内容证据**（卡那一条路，用户口径「先看看底层能够识别到文件是什么东西」）：
                 * 卡上自报"我是这一族的成员"（`Membership == VolumeMember` 或 `BodyKind == VolumeFirstPiece`）
                 * **且卡上的基名与这一组相关**（`BaseNameRelatedToGroup`，既有的弱证据尺子）才收进来。
                 * ⛔ 判不出就不收（老口径一个字不改）；后面那三道闸门（同组 / 卷号一份对一份 / 整组自洽）
                 * 照旧一条不落 —— 这一条只是把"兄弟片在哪"这一件事从名字换成内容。</para>
                 */
                FileNatureCard otherCard = FileNature.Inspect(path);

                bool cardSaysMember =
                    otherCard.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    || otherCard.BodyKind == ArchiveBodyKind.VolumeFirstPiece;

                if (cardSaysMember
                    && groupBaseForScan.Length > 0
                    && VolumeGatherStep.BaseNameRelatedToGroup(otherCard.ArchiveBaseName, groupBaseForScan))
                {
                    sources.Add(path);
                }
            }

            /*
             * ⚠ 2026-10-16（探针逐字：`[组] 单=pack111-L2 tailName=pack111-L2.zip 层=…\pack111\pack111-W0-0 源数=1 源=pack111-L2.part1.rar`）：
             * **组的入口就躺在成员名单 `sources` 里**，而每一条中段任务自己算出来的 `tailName` 是垃圾
             * （`pack111-L2.zip` / `pack111-L2.part3.zip`）⇒ 接片那一档只看"这一片自己的名字"永远推不出这一组。
             * ⇒ 把**入口那一份的路径**一路交下去（既有先例 = 同一个方法上的 `layerOverride`：
             * "外层已经定过的层，原样交下来用"）。⛔ 判据仍是卡上的 `BodyKind`（§29.8/§29.16 的教训），
             * 拿不到 ⇒ null，接片那一档照旧自己找（老口径一个字不改）。
             */
            string? groupEntryPath = !string.IsNullOrWhiteSpace(cardEntryPath) && File.Exists(cardEntryPath)
                ?
                /*
                 * **调用方已经把卡上的入口算好了**（`VolumePiecesFromCards.EntryPath`）⇒ 直接用它，
                 * 不再在这里重读一遍卡（同一件事的真值只允许有一个出口；用户 2026-10-17 总则：
                 * 底层识别优先）。⛔ 只在"盘上真有它"时采用。
                 */
                cardEntryPath
                : sources.FirstOrDefault(item =>
                    File.Exists(item) && FileNature.Inspect(item).BodyKind == ArchiveBodyKind.VolumeFirstPiece);

            /*
             * ===== 内容定的槽位 + 内容定的命名族（2026-10-18，困难批 `19191919`）=====
             *
             * <para>组读取（`VolumePiecesFromCards.Read`）已经用**内容**排出槽位（本地文件头 / 中间 / 尾部 EOCD），
             * 也给出了命名族（`ZipEndRecordMeansSplit` ⇒ `ZipNumbered`）。而接片这一档原来只拿**名字**算规范名
             * —— `X.zip` 被读成跨盘族的末片 ⇒ `X.z01` / `X.zip` 撞名 ⇒ 整组接不起来。
             * ⇒ 把这两个内容事实原样交下去；判不出（没有卡证据）⇒ null，接片照旧走名字那条路。</para>
             */
            var contentSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (cardEvidence != null)
            {
                foreach (ArchiveFixer.Detection.CardVolumeSlot slot in cardEvidence.Slots)
                {
                    /*
                     * ⚠ 键用**盘上物理身份**（见 VolumeSlotKey），⛔ 不用路径：接片会移动 / 建硬链接，
                     * 按原路径查表会落空 ⇒ 又退回"名字优先"⇒ 两片撞同一个规范名。
                     */
                    contentSlots[VolumeSlotKey(slot.Path)] = slot.Index;
                }
            }

            VolumeNamingFamily? groupNamingFamily = cardEvidence?.NamingFamily;

            /*
             * **每个槽位"正确规范名"的路径**（给"收旧名字"那一条判据用）：只在这一组有内容槽位、族判得出、
             * 规范名拼得出时才有值；⛔ 判不出 ⇒ null，接片照旧不碰任何旧名字。
             */
            IReadOnlyDictionary<int, string>? slotCanonicalTargets = null;

            if (cardEvidence != null
                && groupNamingFamily is VolumeNamingFamily namingFamily
                && cardEvidence.Slots.Count >= 2)
            {
                string slotStem = FileNameHelper.GetArchiveBaseName(cardEvidence.Slots[0].Path);
                IReadOnlyList<string> slotNames = VolumeNumberFromContent.BuildStandardNames(
                    slotStem,
                    namingFamily,
                    cardEvidence.Slots.Count);

                if (slotNames.Count == cardEvidence.Slots.Count)
                {
                    var slotTargets = new Dictionary<int, string>();

                    for (int slotIndex = 0; slotIndex < cardEvidence.Slots.Count; slotIndex++)
                    {
                        slotTargets[cardEvidence.Slots[slotIndex].Index] =
                            Path.Combine(entryLayer, slotNames[slotIndex]);
                    }

                    slotCanonicalTargets = slotTargets;
                }
            }

            if (cardEvidence != null && contentSlots.Count > 0)
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：内容槽位判据输入 —— "
                    + string.Join(
                        "、",
                        cardEvidence.Slots.Select(slot =>
                            slot.Index + ":" + FileNameHelper.GetFileName(slot.Path) + "（" + VolumeSlotKey(slot.Path) + "）")));
            }

            if (contentSlots.Count > 0)
            {
                var srcTrace = sources
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => FileNameHelper.GetFileName(path) + "（" + VolumeSlotKey(path) + "）");

                AppendLog(
                    "INFO",
                    $"{task.LogName}：收片源判据输入 —— 源=[{string.Join("、", srcTrace)}]"
                    + $"｜内容槽位=[{string.Join("、", contentSlots.Select(pair => pair.Value + ":" + pair.Key))}]"
                    + $"｜入口=「{FileNameHelper.GetFileName(groupEntryPath ?? string.Empty)}」"
                    + $"｜族={groupNamingFamily}");
            }

            /*
             * 处理顺序 = **内容槽位从大到小**（2026-10-18 新批 `24242424`）：先处理末片/高槽位，
             * 它们可能正占着低槽位的名字；等高槽位那一片已经在自己的规范名下，低槽位再收旧名字才安全。
             * 判不出槽位的源排在最前（不参与换名，按老口径）。
             */
            var orderedSources = sources
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    int slot = contentSlots.TryGetValue(VolumeSlotKey(path), out int byContent)
                        ? byContent
                        : int.MaxValue;

                    return (Path: path, Slot: slot);
                })
                .OrderByDescending(item => item.Slot)
                .ToList();

            foreach ((string path, int _) in orderedSources)
            {
                try
                {
                    TryAdoptUnresolvedVolumePiece(
                        path,
                        task,
                        entryLayer,
                        groupEntryPath,
                        allowMoveIntoGroup,
                        contentSlots,
                        groupNamingFamily,
                        slotCanonicalTargets);
                }
                catch (Exception ex)
                {
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }

            /*
             * ⚠ 再扫一遍**我们自己那几棵树**：成品那一层 / 其余物那一层 / **这一单自己的落点层**。
             *
             * · 成品 + 其余物：单层路里入口包（真机 `111.zip`）是**下一轮的内层包**，正躺在"其余物"里
             *   （那条路承诺"内层包原样留着"，`InnerLayerContinuationTests` 钉着这条红线）⇒ 光看任务表看不见它。
             * · **这一单的落点层（`OutputPath`）**：每一条链解开之后是**它自己**把片"定稿"进自己那一层的
             *   （`PublishUnresolvedPieceLandingCopies` 用的就是这一个事实位）—— 而 `ContentDirectoryPath`
             *   只是**成功那条路**才记的一笔账。
             *
             * ⚠ 为什么必须补第 ③ 个目录（2026-10-11 调试器实测，`TryGatherGroupPiecesInto` 帧里读的变量）：
             * 真机形状「入口包最后到」跑批末那一遍时
             *   `pack3.rar|…\in\pack3\pack3 ;; pack2.rar|（空串） ;; pack1.rar|（空串） ;; 111_.part4.rar|（空串）`
             * —— 四单的 `ContentDirectoryPath` 只有 pack3 有值；而同一帧里 `OutputPath` 四单**全都有值**
             *   （`pack2.rar|…\in\pack2\pack2` 正是 `111_.part2.rar` 躺着的那个目录）。
             * `pack2.rar` 之所以没记上那一笔：它的链**校验未通过**（产物校验未通过：预期 2 个文件 / 2097152 字节，
             * 实际 1 个 / 1048576 字节），在那条 `return false` 上早退了，`ContentDirectoryPath` 的赋值在它下面
             * ⇒ 这一组永远收不到 part2 ⇒ 四片永远凑不齐。**这一档与"谁先到"无关**：片在不在自己落点层，
             * 是那一条链定稿之后盘上的事实。
             *
             * ⛔ 这一步只**硬链接**（零字节、只读），⛔ 其余物里那一份一个字节都不动 ——
             * 它的去留仍由既有口径决定（轮次续解会把剩下的当内层包接着解）。
             */
            foreach (ArchiveTask other in SnapshotTaskTable(Tasks))
            {
                if (other == null)
                {
                    continue;
                }

                foreach (string directory in new[]
                         {
                             other.ContentDirectoryPath, other.RestDirectoryPath, other.OutputPath
                         })
                {
                    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (string path in CollectVolumeFilesInDirectory(directory))
                    {
                        try
                        {
                            if (VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                            {
                                TryAdoptUnresolvedVolumePiece(
                                    path,
                                    task,
                                    entryLayer,
                                    groupEntryPath,
                                    allowMoveIntoGroup,
                                    contentSlots,
                                    groupNamingFamily);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                        }
                    }
                }
            }
        }

    }
}