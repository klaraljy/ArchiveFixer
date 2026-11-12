using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    public static partial class VolumeNameRepair
    {        /// <summary>
             /// **跨层收卷**（用户 2026-10-05 真机原话：「各分卷在不同的目录，你就将其全部移动到头文件 .001
             /// 同级目录里面去」）。
             ///
             /// <para><b>现场</b>：三卷 7z 的三片分别躺在**三个不同的层产物目录**里
             /// （<c>layer-001\output\HK.7z.002</c>、<c>layer-002\output\HK.7z.003</c>、
             /// <c>layer-003\output\HK.7z.001</c>）。引擎找兄弟卷**只看入口文件旁边那一层** ——
             /// 所以第 2 层把 <c>HK.7z.001</c> 单独交给 7-Zip 必然打不开，而它回的那句
             /// <c>Open ERROR: Cannot open the file as [7z] archive</c> 还被说成「缺少首卷」（说反了，见
             /// <see cref="Engines.SevenZip.SevenZipOutputParser.LooksLikeMissingVolumePart"/>）——
             /// 整条链判「部分完成」，工作区 7 个文件 / 13.48 GiB 整份删掉，什么都没发布。</para>
             ///
             /// <para><b>三条边界</b>（不可逆动作，兜底一律落"什么都不做"）：</para>
             /// <list type="number">
             /// <item><description>候选池 = **入口自己那一层**（只读，引擎唯一会去找兄弟卷的地方）
             /// + 调用方给的**这条链的各层产物目录**（只读枚举，搬的来源）
             /// + 调用方给的**找卷窗口**（<paramref name="nearbyCandidates"/>；批首那条链给的是
             /// <see cref="EnumerateVolumeCandidatesNearby"/> 收的池，于是"两半分在两个兄弟文件夹里"
             /// 这种真机形状也能收）；
             /// ⛔ 不递归着往下满盘找、⛔ 不含 <c>carved</c> 这种我们自造的兄弟目录、⛔ 不跨盘。</description></item>
             /// <item><description>闸门（全过才动）：**整组自洽**（把"移动之后的样子"虚拟出来交给既有出口
             /// <see cref="VolumeGroupDetector.Group"/>：基名逐字相同 + 卷标记连续 + 完整且含自己）
             /// **并且**（7z）起始头自述的整包字节数 **==** 这几片字节数之和；目标名被占 / 入口自己的名字不标准
             /// / 不同盘 ⇒ **整组不动**。</description></item>
             /// <item><description>只移动**已经判定成这一组成员的那几片**（入口那一卷所在的目录里的文件，
             /// 以及收进来的那几片）。⛔ 绝不覆盖任何已存在的文件（<see cref="TryApply"/> 全成或全不成）。</description></item>
             /// </list>
             ///
             /// <para><b>找卷的基准</b>（用户 2026-10-05 口径：「你要以一开始的分卷文件为准，不要以 001 为准，
             /// 否则这时的第一层父文件夹就探测不到」）：先把手上这几份过一遍，取**最浅的那一卷**当基准
             /// （<see cref="ResolveShallowestAnchor"/>）；基准与入口不在同一层时，**再按基准收一次窗口**
             /// （<see cref="EnumerateVolumeCandidatesNearby"/>）—— 那是"最外层那个分卷的第一层父文件夹"
             /// 唯一能被带出来的地方。⚠ 「修复后缀之后」也不许改用 001 当基准：基准只由**手上这几卷的路径**回答。</para>
             ///
             /// <para><b>免试</b>（同一批用户口径：「要不然你在分开了你还会继续解压单独的001」）：
             /// 入口这一层配不出完整一组、又收不到一起时，**一次引擎调用都不做**，按不变量 7 如实报"缺哪几片"。
             /// ⛔ 只对**有硬证据**的那一档免试：名字自报第 1 卷 + <see cref="SevenZipStartHeader"/> 能读出
             /// 起始头（<c>32 + NextHeaderOffset + NextHeaderSize</c> = 整包应当有的字节数，<c>-mhe</c> 也不加密）。
             /// 其他族（RAR / 跨盘 zip）**本轮不做免试**（它们的内容里没有"整包多长"这个自述数），
             /// 照旧让引擎去试、按引擎原话给结论 —— 那几条既有用例断言的就是那条口径，⛔ 别顺手改。
             /// 起始头读不出来 / 字节数**多于**自述且缩不出唯一解释 ⇒ 判不出 ⇒ 也不免试（照旧解）。</para>
             /// </summary>
             /// <param name="entryVolumePath">引擎这一趟要打开的那一份（这一层的输入；名字自报卷号 1）。</param>
             /// <param name="chainPoolCandidates">
             /// 这条递归链**各层产物目录**里的直接子文件（调用方枚举，通常带真实尺寸）。
             /// 传 null / 空 ⇒ 只看入口自己那一层（不会搬任何东西）。
             /// </param>
             /// <param name="nearbyCandidates">
             /// **找卷窗口**里那一批候选（调用方用 <see cref="EnumerateVolumeCandidatesNearby"/> 收的池，
             /// 基准 = 手上最浅的那一卷）。传 null / 空 ⇒ 与以前逐字一致（只看入口自己那一层 + 链池）。
             /// </param>
        internal static CrossLayerVolumeGather ResolveCrossLayerVolumeGather(
            string? entryVolumePath,
            IEnumerable<VolumeCandidate>? chainPoolCandidates,
            IEnumerable<VolumeCandidate>? nearbyCandidates = null,
            string? cardEntryPath = null)
        {
            /*
             * "不适用"也要**说清是哪一道闸门关着**（用户 2026-10-12：大测试 run-8888 / run-15151515 两轮里
             * 候选明明都找齐了、结论却是"判不出"，日志里看不出卡在哪 ⇒ 下一轮没法查）。
             * ⛔ 只是把既有判据的结论写出来，判据一个字不改。
             */
            CrossLayerVolumeGather NotApplicable(string reason) =>
                new() { Applicable = false, Detail = reason };

            var notApplicable = NotApplicable("入口那一卷不在盘上（或路径形态认不出）");

            if (string.IsNullOrWhiteSpace(entryVolumePath) || !File.Exists(entryVolumePath))
            {
                return notApplicable;
            }

            string entryPath = SafePathHelper.GetFullPathSafe(entryVolumePath);
            string entryName = SafeFileName(entryPath);
            string entryDirectory = Path.GetDirectoryName(entryPath) ?? string.Empty;

            /*
             * ⛔ **不再要求"手上这一份必须是第 1 卷"**（用户 2026-10-17 拍板：这一档问的是
             * 「这一组该拼到哪一层」，与"我手上正好拿着哪一片"无关）。
             *
             * 基准改由池子里的**第 1 卷**回答（下面 `baselinePath`）—— 池子的来源本来就是**四处**
             * （入口那一份所在目录 + 链池 + 窗口 + 按基准再收一轮窗口），与"手上是哪一片"无关，
             * 所以删这道闸门不会让池子变窄；真的判不出（这一组没有第 1 卷）由下面那道
             * `byIndex.ContainsKey(1)` 闸门如实报出来。
             *
             * ⚠ 2026-10-17 记账：上一轮曾以为"删掉它打红 4 条守门"，`git stash` 到 HEAD 复验
             * 证明那 4 条**在 HEAD 上本来就是红的**（通过 29 / 失败 4）⇒ 那个推断作废。
             */
            if (entryDirectory.Length == 0)
            {
                return NotApplicable("入口那一卷的目录认不出（路径形态不对）");
            }

            VolumeContentFormat format = VolumeContentInference.SniffFormat(entryPath);

            /*
             * 候选池：同族同基名（唯一出口 BelongsToSameGroup）+ 卷号读得出来。
             * ⛔ 两处来源都只"看"：入口那一层用既有的同目录枚举、链池由调用方给。
             *
             * ⚠ **≥ 16 KiB 粗筛只加在"别的层产物目录"那一份上**（用户给的口径：跨目录找卷要挡噪声）——
             * 入口自己那一层**一个都不许滤掉**：那一层正是引擎要读的地方，漏掉一份小的末卷
             * 会让下面的字节数证据判成"缺卷"（免试）而其实它就在旁边。几 KB 的说明文件靠
             * `BelongsToSameGroup`（同族同基名、名字里得有卷标记）挡住，不需要尺寸这一刀。
             */
            var pool = new List<GatherPiece>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /*
             * 名字那一把尺子**不认**、但内容自述"我是这一族的分卷成员"的那些（用户 2026-10-12 口径 2 +
             * 「先看看底层能够识别到文件是什么东西」）—— 先收在这里，等名字那一份收完再看它们能不能
             * **把这一组补成 1..N 连号**（见下面那段），补不成就不采纳。
             *
             * 现场（大测试 sample `2222`，组合 4）：一组的四片里第 3、4 片被改名成别族
             * （`333.zip` / `333-L4.part4.zip`）⇒ 名字尺子当场把它们挡在池子外 ⇒ 整组永远差一片 ⇒
             * 引擎报「分卷缺失」⇒ ①「内容物解出来」红。
             *
             * ⚠ 为什么不能"内容像就收"（本条第一版就是那样，被 `基名不同_不碰别的组` 当场打红）：
             * 内容只能证明"它是**某一组**的一卷"，证明不了"它是**这一组**的一卷" ——
             * 两组同样 25 MiB 的切片、卷号也撞得上，光靠内容分不开。
             * ⇒ 只承认**能把这一组补成连号**的那一种（连号 + 尺寸规律两条一起看），
             * 剩下的照旧交给下面那三道闸门与"整组完整才允许搬/删"。
             */
            var contentOnly = new List<GatherPiece>();

            /*
             * 「名字与内容**都判不出**卷号」但**基名与入口同族**的那些（用户 2026-10-16 口径：
             * 「消费方接受"第 1 槽不在这层"后把**卡的绝对卷号**翻成权威」）。
             * ⛔ 它们不进池子，只在这里待判 —— 采纳的闸门见下面那一段（唯一填平才要）。
             */
            var sameBaseUnknown = new List<GatherPiece>();

            void Collect(IEnumerable<VolumeCandidate>? candidates, bool applySizeFilter)
            {
                foreach (VolumeCandidate? candidate in candidates ?? Array.Empty<VolumeCandidate>())
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
                    {
                        continue;
                    }

                    string full = SafePathHelper.GetFullPathSafe(candidate.Path);

                    if (full.Length == 0 || !seenPaths.Add(full) || !File.Exists(full))
                    {
                        continue;
                    }

                    string name = SafeFileName(full);
                    int? index = LegacyVolumeNames.TryGetIndex(name);
                    long size = LengthOf(full);

                    if (size <= 0 || (applySizeFilter && size < NearbyMinimumBytes))
                    {
                        continue;
                    }

                    bool byName = index != null && LegacyVolumeNames.BelongsToSameGroup(entryName, name);
                    int? resolvedIndex = index;

                    if (!byName)
                    {
                        /*
                         * ===== 名字那把尺子不认时的**内容证据**那一档 =====
                         *
                         * 用户 2026-10-12 口径 2 的原话：「这个第一大步完成之后就是扫描所有剩余的，
                         * 我之前说过的什么"父目录+自己这一层+所有子目录"在这个新框架下可能是不成立的」；
                         * 加上他反复讲过的总口径「判断一个文件，绝对不能只看基名和后缀…先看看底层能够
                         * 识别到文件是什么东西」。
                         *
                         * 现场（大测试 sample `2222`，组合 4）：一组的四片里，第 3、4 片被**改名成别族**
                         * （`333.zip` / `333-L4.part4.zip`）—— 名字那一把尺子（同族同基名）当场把它们挡在池子外
                         * ⇒ 整组永远差一片 ⇒ 引擎报「分卷缺失」⇒ ①「内容物解出来」红。
                         *
                         * 判据三条**全是内容事实**（⛔ 不看名字、⛔ 不猜）：
                         *   ① 内容自述"我就是分卷组的一员"（<see cref="VolumeNumberReading.IsVolumeMember"/>）；
                         *   ② 内容里读得出卷号（<see cref="VolumeNumberReading.Number"/>）；
                         *   ③ 格式与入口那一族**相同**（同一份 SniffFormat 结论）。
                         * ⛔ 它只是**收进池子**：下面那三道闸门（卷号一份对一份 / 尺寸规律 / 整组自洽 + 试开）
                         * 与"必须证明整组完整才允许搬/删"照旧一条不落。
                         */
                        VolumeNumberReading content = VolumeNumberFromContent.Read(full);

                        if (content.IsVolumeMember
                            && content.Number is int number
                            && content.Format == format)
                        {
                            contentOnly.Add(new GatherPiece(full, number, size));
                        }

                        /*
                         * 名字那一把尺子不认、内容也判不出（7z 族的后续卷按定义没有魔数）——
                         * 但**基名与入口同族**的那些，先原样收在这一档里（⛔ 不进池子）：
                         * 它们只有在"能把入口自述的字节数**唯一**填平"时才被采纳（见下面那一段）。
                         */
                        string entryBaseForFill = FileNameHelper.GetArchiveBaseName(entryPath);
                        string candidateBase = FileNameHelper.GetArchiveBaseName(full);

                        if (entryBaseForFill.Length > 0
                            && candidateBase.Length > 0
                            && (string.Equals(entryBaseForFill, candidateBase, StringComparison.OrdinalIgnoreCase)
                                || entryBaseForFill.StartsWith(candidateBase, StringComparison.OrdinalIgnoreCase)
                                || candidateBase.StartsWith(entryBaseForFill, StringComparison.OrdinalIgnoreCase)))
                        {
                            sameBaseUnknown.Add(new GatherPiece(full, 0, size));
                        }

                        continue;
                    }

                    if (resolvedIndex == null)
                    {
                        continue;
                    }

                    /*
                     * **名字认得出、卡也认得出时，槽位以卡为准**（用户 2026-10-17：「一切看底层识别的来」）。
                     * 现场（新样本 `1111`）：`222-L2.part34B+.rar` 的名字被读成 part34、卡上是 3 ——
                     * 若用名字那个 34，规范名会拼成 `part34.rar`，试开必然报"缺必要分卷"。
                     */
                    try
                    {
                        if (FileNature.Inspect(full).VolumeNumber is int cardNumber && cardNumber >= 1)
                        {
                            resolvedIndex = cardNumber;
                        }
                    }
                    catch
                    {
                        // 卡读不出来 ⇒ 用名字那个槽位（老口径）。
                    }

                    pool.Add(new GatherPiece(full, resolvedIndex.Value, size));
                }
            }

            Collect(EnumerateVolumeCandidatesInDirectory(entryPath), applySizeFilter: false);

            /*
             * **卡指出的入口那一份所在层也扫一遍**（用户 2026-10-17 总则：底层识别优先）。
             * 现场形状：手上拿的是中间卷（名字认不出卷号），而卡上自证首卷的那一份躺在**另一层** ——
             * 只扫"手上那一份的目录"就永远看不到它。⛔ 只扩"去哪儿找"，下面那几道闸门一条不落。
             */
            if (!string.IsNullOrWhiteSpace(cardEntryPath)
                && File.Exists(cardEntryPath)
                && !SameDirectory(cardEntryPath!, entryPath))
            {
                Collect(EnumerateVolumeCandidatesInDirectory(cardEntryPath!), applySizeFilter: false);
            }
            Collect(chainPoolCandidates, applySizeFilter: true);
            Collect(nearbyCandidates, applySizeFilter: true);

            /*
             * ===== 入口那一份**先按卡入池**（用户 2026-10-17：底层识别优先）=====
             * 现场（新样本 `1111`，探针逐字）：`222-L2.part1.rar-875` 名字判不出卷号，
             * 而卡上 `格式=Rar 分卷身份=VolumeMember 卷号=1 专属算法=VolumeFirstPiece`。
             * 名字那条路让 `pool` 空着 ⇒ 下面"内容证据那一档"的门槛（`pool.Count > 0`）
             * 永远过不去 ⇒ 兄弟片（卡上卷号 2、3）一个都进不来 ⇒ 整组判成"缺卷"。
             * ⇒ 池子空着时，入口那一份按**卡上的绝对卷号**补进来（⛔ 后面那几道闸门一条不落）。
             */
            if (pool.Count == 0 && File.Exists(entryPath))
            {
                try
                {
                    FileNatureCard entrySeatCard = FileNature.Inspect(entryPath);

                    if (entrySeatCard.VolumeNumber is int entrySeatNumber && entrySeatNumber >= 1)
                    {
                        pool.Add(new GatherPiece(entryPath, entrySeatNumber, LengthOf(entryPath)));
                    }
                }
                catch
                {
                    // 卡读不出来 ⇒ 池子照旧空着（什么都不做）。
                }
            }

            /*
             * ===== 内容证据那一档：只承认"**把这一组补成 1..N 连号**"的那一种 =====
             *
             * 判据（全是事实，⛔ 不看名字）：
             *   ① 补完之后卷号必须是 **1..N 连号**（一个洞都不许剩）；
             *   ② 除最后一片外，每一片都得是**满片**（尺寸 = 名字那一份里最大的那一片）；
             *   ③ 最后一片不许比满片还大。
             * 三条有一条不成立 ⇒ **这一档整个不采纳**（名字那一份原样用），
             * 于是 `基名不同_不碰别的组` 那一类（两组同尺寸、卷号撞得上）照旧分得开。
             */
            if (contentOnly.Count > 0 && pool.Count > 0)
            {
                var trial = new List<GatherPiece>(pool);
                var have = new HashSet<int>(trial.Select(piece => piece.Index));

                foreach (GatherPiece piece in contentOnly
                             .Where(piece => !have.Contains(piece.Index))
                             .OrderBy(piece => piece.Index))
                {
                    have.Add(piece.Index);
                    trial.Add(piece);
                }

                int count = have.Count;
                long fullSize = trial.Max(piece => piece.Size);

                bool contiguous = count > 0
                    && have.Min() == 1
                    && have.Max() == count
                    && Enumerable.Range(1, count).All(have.Contains);

                bool sizesFit = contiguous && trial.All(piece =>
                    piece.Index == count || piece.Size == fullSize);

                if (contiguous && sizesFit)
                {
                    pool = trial;
                }
            }

            /*
             * ===== 「唯一填平」那一档：用**入口自述的整包字节数**给判不出卷号的那几份定槽位 =====
             *
             * 用户 2026-10-16 口径（逐字）：「批末用**卡算出的入口层**收拢 ⇒ 消费方接受"第 1 槽不在这层"后
             * 把**卡的绝对卷号**翻成权威」。
             *
             * 现场（真机样本 `10101010`，探针逐字）：窗口里三片都在 ——
             * `.001`(26214400) 与 `.002`(26214400) 名字读得出卷号，第 3 片被伪装法吃掉了卷标记
             * （`pack111-L2.7z`，10490213）⇒ 名字与内容都判不出 ⇒ 老判据把它丢在池子外 ⇒
             * 明细永远是「7z 起始头自述这一包应当有 62919013 字节，手上这几卷加起来只有 52428800 ——
             * 还差 10490213 字节」，而那"还差的"正是这一份。
             *
             * 闸门（⛔ 全部是事实，⛔ 不猜卷号、⛔ 不动盘上任何东西）：
             *   ① 只对 **7z** 开（只有它给得出"整包字节数 + 片数"这两条自述）；
             *   ② 入口自述必须**同时**给出片数与整包字节数；
             *   ③ 空洞只有**一个**（多个洞 ⇒ 谁先谁后判不出 ⇒ 整档不做）；
             *   ④ 待填的候选里 **恰好只有一份**的字节数等于"还差的那一串"（唯一性 ⇒ 才敢定槽位）。
             * 四条同时成立 ⇒ 补进池子（槽位 = 那个唯一的空洞），后面那三道闸门照旧一条不落。
             */
            if (format == VolumeContentFormat.SevenZip && sameBaseUnknown.Count > 0)
            {
                FileNatureCard fillCard = FileNature.Inspect(entryPath);

                if (fillCard.DeclaredVolumeCount > 0 && fillCard.DeclaredTotalBytes > 0)
                {
                    var haveSlots = new HashSet<int>(pool.Select(piece => piece.Index));
                    var holes = Enumerable.Range(1, fillCard.DeclaredVolumeCount)
                        .Where(slot => !haveSlots.Contains(slot))
                        .ToList();
                    long missingBytes = fillCard.DeclaredTotalBytes - pool.Sum(piece => piece.Size);

                    if (holes.Count == 1 && missingBytes > 0)
                    {
                        List<GatherPiece> exact = sameBaseUnknown
                            .Where(piece => piece.Size == missingBytes)
                            .ToList();

                        if (exact.Count == 1)
                        {
                            pool.Add(new GatherPiece(exact[0].Path, holes[0], exact[0].Size));
                        }
                    }
                }
            }

            /*
             * ===== 找卷的基准 = 手上**最浅的那一卷**（用户 2026-10-05 口径）=====
             * ⛔ 绝不是 001：真机上真正该被看见的父文件夹是**最外层那个分卷**的父文件夹。拿一个解出来的
             * `001`（它躺在产物树深处）当基准时，窗口的"第一层父文件夹"成了那个深层目录 ⇒
             * `111\1111.7z.002删除` / `.003删除` 一个都探测不到。
             *
             * 做法：从**手上这几份**（入口 + 链池 + 窗口池）里挑路径最浅的那一卷当基准；
             * 它与入口不在同一层时，**再按基准收一次窗口**（`EnumerateVolumeCandidatesNearby`）——
             * "最外层那个分卷的第一层父文件夹"只有这一条路能带出来。
             *
             * ⚠ **只补一轮**（不迭代）：这一档是加法，多收一轮窗口的代价是目录枚举；
             * 收进来的每一份仍然要过下面那三道闸门（同组 / 卷号一份对一份 / 整组自洽 + 字节数），
             * 判不出就整档不做 —— 兜底永远落在"什么都不做"。
             */
            string anchorPath = ResolveShallowestAnchor(entryPath, pool);

            if (!SameDirectory(anchorPath, entryPath))
            {
                Collect(EnumerateVolumeCandidatesNearby(anchorPath), applySizeFilter: true);
            }

            bool hasEntry = pool.Any(piece => SamePath(piece.Path, entryPath));

            /*
             * ===== 入口那一份进不了池子时，让**底层识别**补一次（用户 2026-10-17 总则）=====
             * `Collect(...)` 是按**名字**定槽位的 ⇒ 名字被伪装法吃掉卷标记的那一份（现场 `pack111-L2.7z`）
             * 永远进不了池子 ⇒ 后面 `hasEntry=false` 整档判不出（日志逐字「入口在池子里=False」）。
             * 判据只认一条底层事实：卡上自证 `BodyKind = VolumeFirstPiece` ⇒ 它就是**槽位 1**。
             * ⛔ 只在"入口确实不在池子里"时补一份；后面那些闸门（同号两份 / 体积规律 / 整组自洽 / 试开）一条不落。
             */
            if (!hasEntry && File.Exists(entryPath))
            {
                try
                {
                    if (FileNature.Inspect(entryPath).BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                    {
                        pool.Add(new GatherPiece(entryPath, 1, LengthOf(entryPath)));
                        hasEntry = true;
                    }
                }
                catch
                {
                    // 卡读不出来 ⇒ 照旧判不出（什么都不做）。
                }
            }

            /*
             * 卷号只能一份对一个：同一个卷号上冒出两份候选（同目录里的 `HK.7z.002` 与 `HK.7z.00删除2`、
             * 或者别的分支里另一组同名卷）⇒ 判不出哪一份属于这一组 ⇒ 整档不做（照旧让引擎去判）。
             *
             * ⚠ 2026-10-16（真机 `14141414` 的收拢探针逐字：`同号两份候选=True；池子=3 份；有第 1 卷=True`）：
             * **同一份文件的多个名字不算"两份候选"** —— 现场是伪装原名 `打包111-L2.part0631中文3.rar`
             * 与规范名 `打包111-L2.part3.rar` 落在同一个卷号上，而 `fsutil hardlink list` 一次列出两个路径、
             * 同一个 File ID（同一天在 `4444` 上也量到同一形状：4 个名字、1 个 File ID）。
             * ⇒ 先用**物理同一性**判一次（既有唯一出口 `FileIdentity.IsSamePhysicalFile`）：是同一份 ⇒
             * 留**名字本身就是规范卷名的那一份**（判据 = 卡上的 `CanonicalVolumeName` 与文件名逐字相同，
             * 并列再比路径深浅、最后按名字定序）—— ⛔ 不算歧义；**不是**同一份 ⇒ 照旧整档不做（判不出）。
             */
            bool IsCanonicalNameOfItself(string piecePath)
            {
                FileNatureCard pieceCard = FileNature.Inspect(piecePath);

                return pieceCard.CanonicalVolumeName.Length > 0
                    && string.Equals(
                        pieceCard.CanonicalVolumeName,
                        SafeFileName(piecePath),
                        StringComparison.OrdinalIgnoreCase);
            }

            var byIndex = new Dictionary<int, GatherPiece>();
            bool ambiguousIndex = false;

            foreach (GatherPiece piece in pool
                         .OrderBy(p => p.Index)
                         .ThenBy(p => IsCanonicalNameOfItself(p.Path) ? 0 : 1)
                         .ThenBy(p => p.Path.Count(character => character == '\\'))
                         .ThenBy(p => SafeFileName(p.Path), StringComparer.OrdinalIgnoreCase))
            {
                /*
                 * ===== 槽位以**卡上的卷号**为准（用户 2026-10-17：「一切看底层识别的来」）=====
                 * 现场（新样本 `1111`）：`222-L2.part34B+.rar` 的名字读成第 34 卷、卡上是第 3 卷。
                 * 名字路若有发言权，规范名就会算成 `222-L2.part34.rar`（试开必报"缺必要分卷"）。
                 * ⇒ 入表前先把槽位换成卡上的卷号；卡判不出 ⇒ 用原来那个（老口径）。
                 */
                int seatIndex = piece.Index;

                try
                {
                    if (FileNature.Inspect(piece.Path).VolumeNumber is int contentSeat && contentSeat >= 1)
                    {
                        seatIndex = contentSeat;
                    }
                }
                catch
                {
                    // 卡读不出来 ⇒ 用名字那个槽位。
                }

                if (byIndex.TryAdd(seatIndex, piece with { Index = seatIndex }))
                {
                    continue;
                }

                if (!FileIdentity.IsSamePhysicalFile(byIndex[seatIndex].Path, piece.Path))
                {
                    ambiguousIndex = true;
                }
            }

            if (!hasEntry || ambiguousIndex || byIndex.Count == 0 || !byIndex.ContainsKey(1))
            {
                return NotApplicable(
                    $"卷号那一档过不去：入口在池子里={hasEntry}；同号两份候选={ambiguousIndex}；池子={byIndex.Count} 份；有第 1 卷={byIndex.ContainsKey(1)}");
            }

            /*
             * 期望基名 = 与 <see cref="VolumeGroupDetector.Group"/> **同一条代码路径**算出来的那个基名
             * （组键那一档本身**不剥卷标记**：旧的归组键档对 `HK.7z.001`
             * 原样返回 `HK.7z.001`，而组里算出来的是 `HK.7z` —— 必须交给同一个出口判）。
             * 拿不到（这一份的名字/尺寸进不了任何一个桶）⇒ 判不出 ⇒ 这一档整个不做。
             */
            string? expectedBaseName = ResolveGatherBaseName(pool, entryName);

            if (string.IsNullOrWhiteSpace(expectedBaseName))
            {
                return NotApplicable("推不出这一组的期望基名（那几份的名字/尺寸进不了任何一个桶）");
            }

            /*
             * 规范卷名 = "移动之后该叫什么"：认得出伪装的按既有还原尺子还原（`HK.7z.00删除2` → `HK.7z.002`），
             * 认不出的保持原名。⛔ 不在这里拼名字（`TryBuildRestoredName` 是唯一出口）。
             */
            var targetNames = new Dictionary<int, string>();
            var targetNamesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (GatherPiece piece in byIndex.Values)
            {
                /*
                 * ===== 规范卷名：**族由底层识别卡定**（用户 2026-10-17 总则，唯一出口）=====
                 * 卡上 `Format` 说这一族是什么，就按那一族给槽位名；卡判不出（格式读不出来）才退回
                 * 原来那两条名字路（`TryBuildRestoredName` / `<入口卷基名>.<三位卷号>`）。
                 * ⛔ 没有第二套命名：最终名仍由 `VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex` 生成。
                 */
                string canonical = string.Empty;

                try
                {
                    canonical = ArchiveFixer.Detection.VolumePiecesFromCards.CanonicalNameForSlot(
                        FileNature.Inspect(piece.Path),
                        piece.Index);
                }
                catch
                {
                    canonical = string.Empty;
                }

                if (canonical.Length == 0)
                {
                    canonical = SafeFileName(piece.Path);

                    if (TryBuildRestoredName(canonical, format, out string restored))
                    {
                        canonical = restored;
                    }
                    else if (TrySplitDisguised(entryName, out string entryVolumeBase, out string entrySegment)
                             && entryVolumeBase.Length > 0)
                    {
                        /*
                         * 这一份的名字里**根本没有卷标记**（伪装法把卷号吃掉了，如 `pack111-L2.7z`）
                         * ⇒ `TryBuildRestoredName` 帮不上（它要有标记才还原）。
                         * 它的槽位是我们**按入口自述的整包字节数唯一填平**推出来的（见上面那一段），
                         * 所以规范名照**入口那一份的形状**拼：`<入口的基名>.<入口的族后缀>.<三位卷号>`。
                         *
                         * ⛔ 不另造一套命名口径：基名仍出自**第二大步那一份**
                         * （<see cref="FileNameHelper.SecondStepBaseName"/>），族后缀与卷标记就是
                         * <see cref="TrySplitDisguised"/> 拆出来的那一段。
                         * 现场（`10101010`）：这一步之前，明细是「目标名「pack111-L2.7z」已经被别的文件占用了」
                         * —— 因为名字原样没带卷号、撞上入口层里那份同名文件；拼成 `pack111-L2.7z.003` 之后才落得下去。
                         */
                        canonical = ReplaceVolumeMarker(
                            entryVolumeBase,
                            entrySegment,
                            piece.Index.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                if (!targetNamesSeen.Add(canonical))
                {
                    // 两份还原成同一个名字 ⇒ 判不出（⛔ 绝不覆盖）。
                    return notApplicable;
                }

                targetNames[piece.Index] = canonical;
            }

            /*
             * ===== 基准 = 这一组的**第 1 卷**（手上有哪一片都不影响）=====
             * 搬的目标层 = **入口包所在的那一层**。
             *
             * ⛔ **第 1 卷自己的名字**必须是规范名：这一档只搬兄弟卷、**不改入口的名字** ——
             * 调用方手上那个路径是开工之前抓下来的，改了它就等于带着一个失效的路径往下走
             * （改名是「还原」工序的活，不是这一档的）。第 1 卷名字不标准 ⇒ 这一组不收（一个字节都不动）。
             */
            string baselinePath = byIndex[1].Path;
            string baselineDirectory = Path.GetDirectoryName(baselinePath) ?? string.Empty;

            if (!SamePath(baselinePath, Path.Combine(baselineDirectory, targetNames[1])))
            {
                return new CrossLayerVolumeGather
                {
                    Applicable = true,
                    AnchorPath = anchorPath,
                    EntryPath = baselinePath,
                    Detail = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.CrossLayerGatherEntryNameNotCanonicalFormat,
                        SafeFileName(baselinePath))
                };
            }

            List<GatherPiece> beside = byIndex.Values
                .Where(piece => SameDirectory(piece.Path, baselinePath))
                .ToList();

            // ① 入口这一层**已经自证完整** ⇒ 什么都不用做（正常分卷 / 完整单卷包走的都是这一支）。
            GroupEvidence besideEvidence = EvaluateGatherGroup(
                beside, baselinePath, baselineDirectory, expectedBaseName, targetNames, format);

            if (besideEvidence.NeedsNoWork && besideEvidence.Complete)
            {
                return new CrossLayerVolumeGather
                {
                    Applicable = true,
                    AnchorPath = anchorPath,
                    EntryPath = baselinePath,
                    CompleteBesideEntry = true,
                    Detail = besideEvidence.Describe()
                };
            }

            // ② 不齐 / 名字不标准 ⇒ 试着把链池里那几份收过来（纯计划）。
            GroupEvidence allEvidence = EvaluateGatherGroup(
                byIndex.Values.ToList(), baselinePath, baselineDirectory, expectedBaseName, targetNames, format);

            VolumeNameRepairPlan? plan = null;
            string gatherBlocker;

            if (allEvidence.Complete)
            {
                plan = TryBuildGatherPlan(baselinePath, byIndex.Values.ToList(), targetNames, out gatherBlocker);
            }
            else
            {
                gatherBlocker = allEvidence.Describe();
            }

            if (plan is { CanRepair: true })
            {
                return new CrossLayerVolumeGather
                {
                    Applicable = true,
                    AnchorPath = anchorPath,
                    EntryPath = baselinePath,
                    Plan = plan,
                    MovedCount = plan.Items.Count(item => !string.Equals(
                        item.CurrentPath,
                        item.TargetPath,
                        StringComparison.OrdinalIgnoreCase)),
                    Detail = DescribeGatherPlan(plan, baselineDirectory)
                };
            }

            /*
             * ③ 收不成 ⇒ 报"缺哪几片"。免试**只认 7z 那条硬证据**（见方法注释）：
             * 起始头读得出来、而且池子自证不是整包（少了字节）或已是整包却收不到一起。
             * 判不出（读不出起始头 / 多出来的那份缩不出唯一解释）⇒ 不免试，照旧让引擎去判。
             */
            bool hardEvidence = format == VolumeContentFormat.SevenZip
                && allEvidence.BytesBudgetKnown
                && !allEvidence.Ambiguous;

            return new CrossLayerVolumeGather
            {
                Applicable = true,
                AnchorPath = anchorPath,
                EntryPath = baselinePath,
                ShouldSkipTrial = hardEvidence,
                MissingNames = allEvidence.MissingNames,
                Detail = gatherBlocker
            };
        }

        // ================================================================
        // 跨盘 ZIP：末片在手、其余几片散在源目录里（2026-10-05 真机第八批）
        // ================================================================

        /// <summary>**跨盘 ZIP 收卷**的结论（纯事实 + 已经建好的链接；用户的源文件一个字节都不动）。</summary>
        internal sealed class SpannedZipDiskGather
        {
            /// <summary>这一档适不适用：入口必须**自述是跨盘 zip 的末片**（EOCD 盘号 &gt; 0 且总片数 ≥ 2）。</summary>
            public bool Applicable { get; init; }

            /// <summary>入口那一层现在**逐字规范名**地摆着第 1..n-1 片（引擎解得开）。</summary>
            public bool Complete { get; init; }

            /// <summary>入口自述的总片数（含末片）。</summary>
            public int DiskCount { get; init; }

            /// <summary>这一趟真建了几条链接。</summary>
            public int LinkedCount { get; init; }

            /// <summary>
            /// 这一趟建出来的链接**路径**（调用方必须在用完这一层之后把它们删掉 —— 它们是我们的临时名字，
            /// ⛔ 不是内容物：留着会被发布侧当成品搬进用户目录、还会把"半套分卷"闸门自己绊倒，见 2026-10-05 真机）。
            /// </summary>
            public IReadOnlyList<string> LinkedPaths { get; init; } = Array.Empty<string>();

            /// <summary>这几条链接指向的**用户源片路径**（用于"这一组已由谁解出"的记账）。</summary>
            public IReadOnlyList<string> LinkedSources { get; init; } = Array.Empty<string>();

            /// <summary>点名还缺哪几片（规范卷名）。</summary>
            public IReadOnlyList<string> MissingNames { get; init; } = Array.Empty<string>();

            /// <summary>给人看的一句话。</summary>
            public string Detail { get; init; } = string.Empty;
        }

    }
}