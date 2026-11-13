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
    /// <summary>缺失卷槽位补链接（打分分配/字节预算/槽名生成；第 9 块，从 Volume 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **按 7z 起始头自述的字节数把缺口补齐**（2026-10-13 困难批 `1111`，用户口径「靠底层识别」）。
        ///
        /// <para><b>现场（运行期事实）</b>：那一组的三片里，入口片 `111-L2.7z.001` 已经由「还原」改回规范名、
        /// 也留在了盘上（第 15 修），链里逐字
        /// `7z 起始头自述这一包应当有 62907634 字节，手上这几卷加起来只有 26214400 字节`；
        /// 而另外两片被伪装成 `111-L2.7z`（吃掉卷标记 = "伪装成单一"）与 `111-L2.7z-924`（本体后缀后粘垃圾尾巴）
        /// ⇒ 名字那一栏读不出卷号 ⇒ 收片（按名字）永远收不到它们 ⇒ 整组凑不齐 ⇒ ①红。
        /// 算术上正好闭合：`62907634 − 26214400 = 36693234 = 26214400 + 10478834`（那两片自己的字节数）。</para>
        ///
        /// <para><b>判据（只用两条既有的硬证据，⛔ 不猜卷号）</b>：
        /// ① <see cref="SevenZipStartHeader.TryReadExpectedTotalBytes"/> 读出"整包应当有多少字节"（明文起始头）；
        /// ② 候选必须**体积正好凑上**那个差额（1–3 份、每份不超过手上最大的那一卷）、
        ///    且按内容**不是可读归档**（<see cref="FileNature.Inspect"/> 说格式未知或 7z —— 中间片本来就没有身份信息）；
        /// ③ 填完之后必须**1..N 连号 + 除末片外等大 + 总字节数正好等于自述值**。
        /// ⇒ 用**规范槽位名**（唯一出口 <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/>）
        /// 把它们**硬链接**进这一层（⛔ 原件一个字节都不动、⛔ 绝不覆盖已有名字）。
        /// ⛔ 判不出（凑不出正好相等 / 三条有一条不成立）⇒ 一份都不补。</para>
        /// </summary>
        /// <summary>
        /// **照"已经在层里的那几片"的命名规律**，给缺的这一片起一个同族的名字（唯一出口）。
        ///
        /// <para>⚠ 2026-10-14（实测 `3333` 组合 1 定案）：缺片那一档原来一律走
        /// <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/>，而那一族**没有**
        /// "`X.zip.001`"这一格 ⇒ 落到 default ⇒ 出来的是 `111-L2.003`，可层里那两片叫
        /// `111-L2.zip.001/.002` ⇒ 引擎按名字找 `.zip.003` **找不到**（日志逐字：链里仍然缺第 3 片）⇒ ①红。
        /// ⇒ 按**既有片的写法**换掉卷号那一段（宽度照旧、前后缀逐字保留）：
        /// `111-L2.zip.002` ⇒ `111-L2.zip.003`、`111-L2.001` ⇒ `111-L2.002`、
        /// `x.part2.rar` ⇒ `x.part3.rar`、`x.z01` ⇒ `x.z03`（跨盘 ZIP 族的反向编号不在这里动，见调用点的过滤）。</para>
        ///
        /// <para>判不出（找不到卷号那一段 / 拼出来的名字自验不过）⇒ 回落到既有那个出口；
        /// 仍然判不出 ⇒ false（调用方什么都不做）。</para>
        /// </summary>
        internal static bool TryNameSlotLikeExistingPiece(string existingPiecePath, int slot, out string name)
        {
            name = string.Empty;

            string existingName = FileNameHelper.GetFileName(existingPiecePath);

            if (existingName.Length == 0)
            {
                return false;
            }

            // ① 数字分片（`…002` / `…002` 前带任何前后缀，宽度照旧）
            System.Text.RegularExpressions.Match digits = System.Text.RegularExpressions.Regex.Match(
                existingName,
                @"(?<head>.*?)(?<number>\d{2,})(?<tail>\D*)$");

            if (digits.Success)
            {
                string width = digits.Groups["number"].Value.Length.ToString(CultureInfo.InvariantCulture);

                name = digits.Groups["head"].Value
                    + slot.ToString("D" + width, CultureInfo.InvariantCulture)
                    + digits.Groups["tail"].Value;

                return name.Length > 0;
            }

            // ② `partN`（RAR 新式）
            System.Text.RegularExpressions.Match part = System.Text.RegularExpressions.Regex.Match(
                existingName,
                @"(?<head>.*?part)(?<number>\d+)(?<tail>.*)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (part.Success)
            {
                name = part.Groups["head"].Value
                    + slot.ToString(CultureInfo.InvariantCulture)
                    + part.Groups["tail"].Value;

                return name.Length > 0;
            }

            return false;
        }

        /// <summary>
        /// **给缺的这个槽位起一个名字**（唯一出口；字节路与打分分配路都走这里）。
        ///
        /// <para>① 先照"**已经在层里的那一片**"的写法（同族同宽、前后缀逐字保留，
        /// <see cref="TryNameSlotLikeExistingPiece"/>），拼完**自验** —— 名字读出来的卷号必须正好是这个槽位；
        /// ② 过不了才回落既有命名出口 <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/>
        /// （同样自验）；③ 两条都不成 ⇒ 空串，调用方**一份都不补**。</para>
        ///
        /// <para>⚠ 2026-10-14 实测 `28282828`（磁盘逐字）：字节路原来只走 ②，而 `X.7z.001` 这一族的
        /// 规范名出口**给不出 `.7z` 那一段** ⇒ 建出来的链接叫 `111-L2.002`，可层里那一片叫
        /// `111-L2.7z.001` ⇒ 引擎按 `111-L2.7z.002` 找 **找不到** ⇒ 组照样凑不齐（这就是"改对了槽位、
        /// 起错了名字"）。⛔ 与打分路同一条判据，⛔ 不新写第二套命名规则。</para>
        /// </summary>
        /// <summary>
        /// **这一层的命名族（由内容证据定）**：层里只要有一片 `ZipEndRecordMeansSplit`（通用分片末片），
        /// 这一组就是 `ZipNumbered`；判不出 ⇒ null（调用方照旧按名字）。
        ///
        /// <para>2026-10-18 困难批 `30303030` 运行期逐字：`按「打分分配」把 333-L2.zip.001 补成槽位名 333-L2.z01`
        /// —— 样板是 `X.zip` 时命名出口给的是跨盘族形状；而这一组的内容是通用分片 ⇒ 必须用数字样本。</para>
        /// </summary>
        internal static VolumeNamingFamily? ResolveLayerNamingFamily(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            try
            {
                /*
                 * ⛔ 这一格**不再自己判一遍族**（2026-10-18 困难批 `15151515` 组合 6 的运行期定案）：
                 * 原来它只看 `ZipEndRecordMeansSplit` 一个布尔 ⇒ 把**跨盘 ZIP 的末片**也归纳成
                 * `ZipNumbered`（跨盘末片同样"中央目录起在这一份之前"，但它的 EOCD 盘号 &gt; 0）
                 * ⇒ 打分分配把第 1 片补成 `X.zip.002` 而不是 `X.z01` ⇒ 引擎找不到 `.z01` ⇒ 整组拼不起来。
                 * ⇒ 族的唯一出口在检测层（`VolumePiecesFromCards.ResolveNamingFamily`，
                 * 判据 = **带 EOCD 的那一片自己的盘号 / `Membership`**）；这一格只负责
                 * "把这一层的卡收集起来交给它"，并按本格的契约只接受 ZIP 那两个子族。
                 */
                List<FileNatureCard> cards = Directory.EnumerateFiles(directory!)
                    .Select(file => FileNature.Inspect(file))
                    .ToList();

                VolumeNamingFamily? family = Detection.VolumePiecesFromCards.ResolveNamingFamily(cards);

                return family is VolumeNamingFamily.ZipNumbered or VolumeNamingFamily.ZipSpanned
                    ? family
                    : null;
            }
            catch
            {
                // 读不动 ⇒ 判不出 ⇒ 照旧名字路。
            }

            return null;
        }

        internal static string ResolveMissingSlotName(
            IReadOnlyDictionary<int, string> present,
            string entryName,
            int slot,
            VolumeNamingFamily? namingFamily = null)
        {
            string? existingPiece = present.Values
                .FirstOrDefault(path => LegacyVolumeNames.TryGetIndex(FileNameHelper.GetFileName(path)) != null);

            if (!string.IsNullOrEmpty(existingPiece)
                && TryNameSlotLikeExistingPiece(existingPiece!, slot, out string likeExisting)
                && LegacyVolumeNames.TryGetIndex(likeExisting) == slot)
            {
                return likeExisting;
            }

            /*
             * ⛔ **通用分片族：样本要有数字卷标记**（2026-10-18 困难批 `30303030` 运行期逐字：
             * `按「打分分配」把 333-L2.zip.001 补成槽位名 333-L2.z01`）。
             * 样板是 `X.zip` 时命名出口给的是跨盘族形状（槽位 2 = `.z01`）；而这一组的内容
             * 判据（`ZipEndRecordMeansSplit`）说它是通用分片 ⇒ 样本改成 `X.zip.001`，
             * 槽位 2 才会拼成 `X.zip.002`。⛔ 族判不出 ⇒ 照旧名字口径。
             */
            if (namingFamily == VolumeNamingFamily.ZipNumbered
                && !System.Text.RegularExpressions.Regex.IsMatch(entryName, @"\.\d{3}$"))
            {
                entryName += ".001";
            }

            if (VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(entryName, slot, out string canonical)
                && LegacyVolumeNames.TryGetIndex(canonical) == slot)
            {
                return canonical;
            }

            return string.Empty;
        }

        /// <summary>
        /// **候选池要扫的那几棵树**（唯一出口；两条路 —— 字节路与打分分配路 —— 都从这里取）。
        ///
        /// <para>⚠ 2026-10-14 实测 `28282828`（断点读运行期事实，⛔ 不是读代码推）：真第 3 片
        /// `111-L2.7z.00_!63` 躺在**它自己那一单的源目录**里（`…\28282828\111(3)\111(3)\`），
        /// 而原来这一档只扫"落点 / 内容目录 / 其余物目录" ⇒ `roots` 七条里**没有 `111(3)\111(3)`**
        /// （逐字：`…\111-W0-0`、`…\recipe`、`…\111(2)\111-W1-0`、`…\111(2)\111-W1-0\其余物`），
        /// 于是 `candidates.Count == 0` 而缺口是 `[2,3]` ⇒ 数目不等 ⇒ 判不出 ⇒ 这一组永远缺 2、3。</para>
        ///
        /// <para>⇒ 补上"每一单自己所在的那一层"。它落在**用户源树**里，所以从这一棵树选出来的候选
        /// 额外要求"基名与这一组有关系"（<see cref="BaseNameRelatedToGroup"/>）—— 用户原话
        /// 「两个看不懂就看**大小**、看**后缀有没有相似的地方**」。其余几棵树照旧（判据一个字不放宽）。</para>
        /// </summary>
        internal List<(string Directory, ArchiveTask? Owner, bool RequiresBaseNameRelation)> CollectCandidateRoots(
            IEnumerable<ArchiveTask?> tasks,
            string entryLayer)
        {
            var roots = new List<(string, ArchiveTask?, bool)>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? directory, ArchiveTask? owner, bool requiresRelation)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory!))
                {
                    return;
                }

                string full = SafePathHelper.GetFullPathSafe(directory!);

                if (known.Add(full))
                {
                    roots.Add((full, owner, requiresRelation));
                }
            }

            Add(entryLayer, null, false);

            foreach (ArchiveTask? other in tasks)
            {
                if (other == null)
                {
                    continue;
                }

                Add(other.OutputPath, other, false);
                Add(other.ContentDirectoryPath, other, false);
                Add(other.RestDirectoryPath, other, false);

                string? ownDirectory = null;

                try
                {
                    ownDirectory = string.IsNullOrWhiteSpace(other.CurrentPath)
                        ? null
                        : Path.GetDirectoryName(other.CurrentPath);
                }
                catch
                {
                    ownDirectory = null;
                }

                Add(ownDirectory, other, true);
            }

            return roots;
        }

        /// <summary>
        /// 自述片数是**硬上限**：名字读出 `index &gt; DeclaredVolumeCount` 的片，它的卷号**不可信** ⇒
        /// 从 `present` 里摘掉，并把路径交回给调用方当候选（它其实就是缺口里的那一片）。
        ///
        /// <para>⚠ 2026-10-14 实测 `28282828`（断点逐字）：真第 2 片原该叫 `111-L2.7z.002`，
        /// 套在壳里时成员名被写成 `111-L2.7z.004pv2` ⇒ 名字尺子读成 **4**
        /// （`Tolerant(末段「004pv2」) = True（→004，垃圾「pv2」）`）⇒ `present` 里凭空多出槽位 4、
        /// 缺口算成 `[2,3]`；而 7z 起始头**自述只有 3 片**（`entryCard.DeclaredVolumeCount == 3`）
        /// ⇒ 槽位 4 从内容上讲**不可能存在** ⇒ 那个名字在撒谎。
        /// ⛔ 摘掉的是**名字的解释**，盘上一个字节都不动（文件照旧在那一层、照旧进候选池）。</para>
        /// </summary>
        internal static List<string> DetachImpossibleSlots(SortedDictionary<int, string> present, int declaredCount)
        {
            var detached = new List<string>();

            if (declaredCount <= 0 || present.Count == 0)
            {
                return detached;
            }

            foreach (int index in present.Keys.Where(key => key > declaredCount).ToList())
            {
                detached.Add(present[index]);
                present.Remove(index);
            }

            return detached;
        }


        internal void TryLinkMissingPiecesByByteBudget(ArchiveTask task, string entryLayer, string? memberPath, string? cardEntryPath = null)
        {
            if (task == null || string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                return;
            }

            string tailName = ResolveGroupReferenceName(
                task,
                FileNameHelper.GetArchiveBaseName(ResolveObservedPiecePath(task, memberPath)),
                memberPath);

            var present = new SortedDictionary<int, string>(Comparer<int>.Default);
            long largest = 0;

            foreach (string path in CollectVolumeFilesInDirectory(entryLayer))
            {
                if (!File.Exists(path) || !LegacyVolumeNames.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                {
                    continue;
                }

                /*
                 * ⚠ 2026-10-14（用户口径「专属算法不就是靠识别的吗」）：槽位号**先问底层识别卡**
                 * （唯一出口 <see cref="ResolveVolumeSlotIndexForPiece"/>），卡上没有才问名字。
                 * 现场 `10101010`：三片里第 1 片与第 3 片**名字一模一样**（都读成 1），
                 * 只有卡分得开（`VolumeFirstPiece` vs `VolumePiece`）。
                 */
                int? index = ResolveVolumeSlotIndexForPiece(path, FileNature.Inspect(path));

                if (!index.HasValue || present.ContainsKey(index.Value))
                {
                    continue;
                }

                present[index.Value] = path;

                try
                {
                    largest = Math.Max(largest, new FileInfo(path).Length);
                }
                catch
                {
                    return;
                }
            }

            if (present.Count == 0 || !present.TryGetValue(1, out string? firstVolume) || largest <= 0)
            {
                return;
            }

            /*
             * ⛔ 判据全部走**底层识别那一张卡**（用户 2026-10-13 口径：「『名字被伪装到读不出卷号 ⇒ 组凑不齐』
             * 这个是**底层识别**的问题」）——这一档只消费，⛔ 不自己读魔数、⛔ 不自己解析起始头：
             *   · `BodyKind == VolumeFirstPiece` ⇒ 内容自证"入口那一份是这一组的第一片"（而不是一份单卷包）；
             *   · `DeclaredTotalBytes` ⇒ 整包应当有多少字节（7z 起始头明文自述）；
             *   · `DeclaredVolumeCount` ⇒ 按内容算出来的片数（整包 ÷ 这一片向上取整）。
             */
            FileNatureCard entryCard = FileNature.Inspect(firstVolume);

            if (entryCard.BodyKind != ArchiveBodyKind.VolumeFirstPiece
                || entryCard.DeclaredTotalBytes <= 0)
            {
                /*
                 * ⚠ 2026-10-14（用户口径 ①③：**"缺什么补什么"是概率，不是"试过没用就算了"**）：
                 * 7z 那条"自述整包字节数"给不出证据时（RAR / ZIP 族、或中间片），**不许就此收手** ——
                 * 改走**打分分配**：由"已知几片 + 缺哪几个槽位"与"名字读不出卷号的候选"，
                 * 按 基名 / 后缀相似 / 空间（除末卷等大）三条既有证据投票，取最高分那一版先试，
                 * 下一趟（同一组再来一次）**换边**试第二版；两版都不成 ⇒ 回滚（删掉我们建的那几个链接）。
                 */
                TryLinkMissingPiecesByScoredAssignment(task, entryLayer, firstVolume, entryCard, present, largest);

                return;
            }

            long expected = entryCard.DeclaredTotalBytes;
            int declaredCount = entryCard.DeclaredVolumeCount;

            /*
             * ⚠ 2026-10-14（实测 `28282828`）："越界卷号"的片（成员名被写坏 ⇒ 读成 4 号，而自述只有 3 片）
             * 从 present 摘掉后再算差额 —— 它其实就是缺口里的那一片，会由下面的候选池收进来。⛔ 盘上不动。
             */
            DetachImpossibleSlots(present, declaredCount);

            long actual = 0;

            foreach (string path in present.Values)
            {
                try
                {
                    actual += new FileInfo(path).Length;
                }
                catch
                {
                    return;
                }
            }

            long difference = expected - actual;

            if (difference <= 0)
            {
                return;
            }

            /*
             * 候选池 = 各单的几棵树 + 这一层（来源纪律唯一出口 <see cref="CollectCandidateRoots"/>：
             * ⛔ 不扫全盘；⚠ 2026-10-14 起**多收"每一单自己所在的那一层"** —— 实测 `28282828` 的真第 3 片
             * 就躺在它自己那一单的源目录里，老写法一条扫描根都没覆盖到它 ⇒ 候选 0 个 ⇒ 组永远缺 2、3）。
             * 只留"体积能派上用场"的那些：未知格式 / 7z 片段（中间片按定义读不出格式）。
             */
            var candidates = new List<(ArchiveTask Owner, string Path)>();
            string scanGroupBaseName = FileNameHelper.GetArchiveBaseName(FileNameHelper.GetFileName(firstVolume));

            foreach ((string scanRoot, ArchiveTask? scanOwner, bool scanRequiresBaseNameRelation)
                     in CollectCandidateRoots(SnapshotTaskTable(Tasks), entryLayer))
            {
                string[] found;

                try
                {
                    found = Directory.GetFiles(scanRoot, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string path in found)
                {
                    long size;

                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    /*
                     * ⛔ **已经在组里的那几片，绝不许再拿来填缺口**（2026-10-18 困难批 `21212121` 组合 4
                     * 运行期逐字）：`.002` 还在并行那一单的包装壳里没落地时，这一档用**已经在 slot 1 的
                     * `bbb-L2.7z.001`** 凑了差额（日志：`用 bbb-L2.7z.001 → bbb-L2.7z.002`，零字节硬链接）
                     * ⇒ 这一组的 `.002` 是 `.001` 的字节 ⇒ 引擎对**真密码**也报
                     * `Data Error in encrypted file. Wrong password?` ⇒ 10 个候选全废 ⇒ ① 红。
                     * 判据两件盘上事实：① 路径就在 `present` 里；② 与 `present` 里某一份是**同一物理文件**
                     * （改名/硬链接，按 `FileIdentity` 认）。两条任一成立 ⇒ 它不是缺口候选。
                     * 判不出身份 ⇒ 照旧收（不放开任何既有判据）。
                     */
                    if (present.Values.Any(presentPath => SafePathHelper.PathEquals(presentPath, path)))
                    {
                        continue;
                    }

                    if (ArchiveFixer.Detection.FileIdentity.TryGetIdentity(path, out ulong candidateVolume, out ulong candidateIndex)
                        && present.Values.Any(presentPath =>
                            ArchiveFixer.Detection.FileIdentity.TryGetIdentity(presentPath, out ulong presentVolume, out ulong presentIndex)
                            && presentVolume == candidateVolume
                            && presentIndex == candidateIndex))
                    {
                        continue;
                    }

                    if (size <= 0 || size > largest || size > difference)
                    {
                        continue;
                    }

                    if (scanRequiresBaseNameRelation
                        && !VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetFileName(path), scanGroupBaseName))
                    {
                        continue;   // 用户源树里选出来的候选：基名得跟这一组有关系（弱证据那道闸）
                    }

                    FileNatureCard card = FileNature.Inspect(path);

                    /*
                     * ⛔ **内容自证是"完整单卷归档"的那一份绝不许当补片用**（它就是一份正常包）；
                     * 只收"分卷组的一片"与"判不出"这两档（中间片按定义读不出格式）。
                     */
                    if (card.BodyKind == ArchiveBodyKind.SingleComplete
                        || (card.Format != VolumeContentFormat.Unknown
                            && card.Format != VolumeContentFormat.SevenZip))
                    {
                        continue;
                    }

                    candidates.Add((scanOwner ?? task, path));
                }
            }

            // 挑**正好凑上差额**的那几份（最多 3 份；体积降序 —— 与"除末卷外等大"那条规律一致）。
            var chosen = new List<(ArchiveTask Owner, string Path)>();
            long remaining = difference;

            foreach ((ArchiveTask owner, string path) in candidates
                         .OrderByDescending(item => new FileInfo(item.Path).Length)
                         .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (chosen.Count == 3)
                {
                    break;
                }

                long size = new FileInfo(path).Length;

                if (size > remaining)
                {
                    continue;
                }

                chosen.Add((owner, path));
                remaining -= size;

                if (remaining == 0)
                {
                    break;
                }
            }

            if (chosen.Count == 0 || remaining != 0)
            {
                /*
                 * ⚠ 2026-10-14（实测 `8888` / `28282828`）：字节数那条路**凑不出"正好相等"**时，
                 * 原来直接收手 —— 可那两片可能就在盘上、只是名字读不出卷号。
                 * ⇒ 落到下面那条打分分配（同一个出口；判据一条不放宽：数目不等照样一份都不补）。
                 */
                TryLinkMissingPiecesByScoredAssignment(task, entryLayer, firstVolume, entryCard, present, largest);

                return;
            }

            // 缺哪几个槽位：1..max 里的洞；不够就往 max 后面续（⛔ 上限 = 内容算出来的片数，算得出时）。
            var slots = new List<int>();
            int maxIndex = present.Keys.Max();

            for (int index = 1; index <= maxIndex; index++)
            {
                if (!present.ContainsKey(index))
                {
                    slots.Add(index);
                }
            }

            while (slots.Count < chosen.Count)
            {
                int next = ++maxIndex;

                if (declaredCount > 0 && next > declaredCount)
                {
                    return;   // 内容说这一组只有 declaredCount 片 ⇒ 判不出 ⇒ 一份都不补
                }

                slots.Add(next);
            }

            /*
             * **命名样本只走唯一出口**（用户 2026-10-17 小目标第③项）：族由卡的 `Format` 定，
             * 名字被伪装法改烂时也能给出本族形状；卡判不出（格式读不出来）才退回文件名当样本。
             * ⛔ 最终卷名仍由 `VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex` 生成。
             */
            string entryName = !string.IsNullOrWhiteSpace(cardEntryPath) && File.Exists(cardEntryPath)
                ? FileNameHelper.GetFileName(cardEntryPath!)
                : FileNameHelper.GetFileName(firstVolume);

            if (!string.IsNullOrWhiteSpace(cardEntryPath) && File.Exists(cardEntryPath))
            {
                try
                {
                    string byCardFamily = VolumePiecesFromCards.CanonicalNameForSlot(
                        FileNature.Inspect(cardEntryPath!), 1);

                    if (byCardFamily.Length > 0)
                    {
                        entryName = byCardFamily;
                    }
                }
                catch
                {
                    // 卡读不出来 ⇒ 用文件名当样本（老口径）。
                }
            }
            var linked = new List<string>();
            string groupBaseName = FileNameHelper.GetArchiveBaseName(entryName);

            for (int i = 0; i < chosen.Count; i++)
            {
                /*
                 * ⚠ 2026-10-14（实测 `28282828`）：槽位名走**唯一出口**（照既有片写、拼完自验、回落规范名）
                 * —— 原来这里只用规范名出口 ⇒ `111-L2.7z.001` 那一族被起成 `111-L2.002` ⇒ 引擎找不到。
                 */
                string slotName = ResolveMissingSlotName(present, entryName, slots[i]);

                if (slotName.Length == 0)
                {
                    return;   // 推不出槽位名 ⇒ 一份都不补（已经建的那几个链接由下面回收）
                }

                string target = Path.Combine(entryLayer, slotName);

                if (File.Exists(target)
                    || !HardLinkHelper.CanHardLink(chosen[i].Path, entryLayer)
                    || !HardLinkHelper.TryCreateHardLink(target, chosen[i].Path))
                {
                    foreach (string built in linked)
                    {
                        try
                        {
                            FileOperations.Delete(built);
                        }
                        catch
                        {
                            // 删不掉就留着（它只是我们建的链接，不影响任何判据）。
                        }
                    }

                    return;
                }

                linked.Add(target);

                /*
                 * ⚠ 记下"**我亲手从这一棵树里把它链接成了槽位名**"（见 <see cref="_byteBudgetLinkedSources"/>）：
                 * 收场那一档据此按档处理它自己那一份（名字读不出卷号的片子，任何按名字的筛子都认不出它）。
                 */
                _byteBudgetLinkedSources.Add((chosen[i].Owner, groupBaseName, chosen[i].Path));

                /*
                 * ⚠ 2026-10-13（困难批 `1111` 判据 ③，运行期定案）：**同一件事写进既有那唯一一本账**。
                 *
                 * <para>现场（断点 + 插桩实测，`dbg-b6a4ee4a` 夹具那一轮）：这一档按内容把
                 * `111-L2.7z` / `111-L2.7z-924` 链接成 `111-L2.002` / `111-L2.003` 之后，
                 * `_adoptedLandedPieces`（`PurgeGroupLandedPieces` 用来找"产出方那一份"的那本账）**0 条**
                 * ⇒ 收场那一档的 `targets.Count == 0` ⇒ 两份落地副本留在
                 * `111(2)\…\111-W1-0\` / `111(3)\111-W2-0\` 里 ⇒ 判据 ③ 红。</para>
                 *
                 * <para>⛔ 不另造第二套判据：这一调走的就是"接片那一档"记那一份的**同一个出口**
                 * （<see cref="RememberAdoptedLandedPiece"/>），它自己会把"产出方是谁、落点根在哪、
                 * 是不是某一单自己的源"逐条问清；判不出 ⇒ 它什么都不记（照旧红，如实暴露）。</para>
                 */
                RememberAdoptedLandedPiece(chosen[i].Path, chosen[i].Owner, groupBaseName, renamedInPlace: false);

                /*
                 * ⛔ **补片这一档也要记进"谁吐过这一组的片"那本账**（2026-10-18 用户 `&&` 口径）：
                 * 走 `AdoptPieceIntoGroup` 那条路的片会自己记（见那里），而这一档是**按字节数硬链接补片**，
                 * 绕过了那个入口 ⇒ 那个原包在链尾永远收不了场（现场 `3333`：`222-L2.7z.^Z6002`
                 * 被链接成 `222-L2.7z.002`，组解开并校验通过，它却一直留着）。
                 * 片名用**槽位名**（`linked[i]`），⛔ 不用源文件那个脏名 —— 族判据要认得出族。
                 */
                RememberGroupPieceProducer(groupBaseName, FileNameHelper.GetFileName(linked[i]), chosen[i].Owner);
                RememberPieceSupplyingTask(groupBaseName, chosen[i].Path);
            }

            if (linked.Count > 0)
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：按 7z 起始头自述的字节数补齐缺口 —— 自述 {expected} 字节、"
                    + $"手上 {actual} 字节、差 {difference} 字节 ⇒ 用 "
                    + string.Join("、", chosen.Select((item, i) => FileNameHelper.GetFileName(item.Path) + " → " + FileNameHelper.GetFileName(linked[i])))
                    + " 照既有片的写法起槽位名补进这一层（零字节硬链接，原件一个字节都不动）。");
            }
        }

        /// <summary>
        /// **整组已经在入口那一层凑齐了吗**（只看那一层，⛔ 不看这一单自己那一层）。
        ///
        /// <para>用户 2026-10-06：「111.zip 的位置已经确定，你把分卷移过来不就是了吗」——
        /// 收片那一档按规范卷名把片接进入口那一层之后，那一层里就是**一个目录里的一整组**；
        /// 而这一单自己那一层永远只有它自己一片。⇒ 判据只在入口那一层问，返回的组直接可以拿去开解。</para>
        ///
        /// <para>⛔ 判据本体一个字没改：仍然由 <see cref="VolumeGroupDetector.Group"/> 的
        /// "完整 / 卷号连续 / 除末片外等大"回答；⛔ 这一层里没有入口包 / 不完整 ⇒ 返回 null，
        /// 由调用方退回既有口径。</para>
        /// </summary>
        /// <summary>
        /// **按内容定的规范名把这一组认出来**（2026-10-18，困难批 `19191919`）。
        ///
        /// <para>族与槽位都来自内容（`CardVolumeGroupReading`）⇒ 入口就是槽位 1 的规范名
        /// （通用分片 = `X.zip.001`）；⛔ 不让"用户那一份原来的名字"（`X.zip`，同一份文件的别名）
        /// 冒充入口 —— 否则引擎按跨盘族的入口名去找 `.z01` ⇒ `Unexpected end of archive`、
        /// 整组判成缺卷（真跑逐字）。</para>
        ///
        /// <para>判不出 / 规范名不齐 ⇒ null（调用方照旧走名字那把尺子，一个字不放宽）。</para>
        /// </summary>
        internal static VolumeGroup? ResolveCanonicalGroupFromCardSlots(
            string entryLayer,
            ArchiveFixer.Detection.CardVolumeGroupReading reading)
        {
            if (reading.NamingFamily is not VolumeNamingFamily family
                || reading.Slots.Count < 2
                || string.IsNullOrWhiteSpace(entryLayer))
            {
                return null;
            }

            /*
             * 基名从**卡上**取（唯一出口 `ArchiveBaseName`），⛔ 不从 `EntryPath` 取：
             * 不完整的组读取没有入口（`EntryPath` 为空），而槽位那边有每一片的路径。
             * 取第一个**还在盘上**的槽位；一个都不在 ⇒ 判不出。
             */
            string stem = string.Empty;

            foreach (ArchiveFixer.Detection.CardVolumeSlot slot in reading.Slots)
            {
                if (slot.Path.Length > 0 && File.Exists(slot.Path))
                {
                    stem = FileNature.Inspect(slot.Path).ArchiveBaseName;
                    break;
                }
            }

            if (stem.Length == 0)
            {
                return null;
            }

            IReadOnlyList<string> names = VolumeNumberFromContent.BuildStandardNames(stem, family, reading.Slots.Count);

            if (names.Count < 2)
            {
                return null;
            }

            var candidates = new List<VolumeCandidate>();

            foreach (string name in names)
            {
                string path = Path.Combine(entryLayer, name);

                if (!File.Exists(path))
                {
                    return null;
                }

                long size = -1;

                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    // 量不出大小不影响归组。
                }

                candidates.Add(new VolumeCandidate { Path = path, Size = size });
            }

            return VolumeGroupDetector.Group(candidates)
                .FirstOrDefault(group => group.IsComplete
                    && !string.IsNullOrWhiteSpace(group.FirstVolumePath)
                    && File.Exists(group.FirstVolumePath));
        }

        private ExtractionSlotScoredAssignment? _slotScoredAssignment;
        private ExtractionSlotScoredAssignment SlotScoredAssignment =>
            _slotScoredAssignment ??= new ExtractionSlotScoredAssignment(this);

        private void TryLinkMissingPiecesByScoredAssignment(
            ArchiveTask task,
            string entryLayer,
            string firstVolume,
            FileNatureCard entryCard,
            SortedDictionary<int, string> present,
            long largest) =>
            SlotScoredAssignment.TryLinkMissingPiecesByScoredAssignment(
                task, entryLayer, firstVolume, entryCard, present, largest);

    }
}
