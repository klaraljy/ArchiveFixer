using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **一层里"哪几份是同一组的分卷片"—— 只按底层识别卡回答**（用户 2026-10-17 口径：
    /// 「分卷的底层识别算法还要加强」＋「7z 的中间卷一开始识别不到…这个时候利用现有的知识该怎么判断」）。
    ///
    /// <para><b>为什么需要它</b>：7z 格式里**没有卷号字段**，所以中间卷从内容上永远判不出"我是第几卷"；
    /// 只有首卷的起始头自述两件事 ——「整包应当有多少字节」与「按它算出来共几片」。而真机上常见的情形
    /// 恰恰是首卷**没被识别出来**（名字被改成 `xxx.7z.txt` 之类），那时中间卷与"普通内容物"在内容上
    /// 没有区别。</para>
    ///
    /// <para><b>判据顺序（三条证据，串成一个出口）</b>：</para>
    /// <list type="number">
    /// <item><description><b>首卷的自述</b>（最硬的证据）：卡上 `BodyKind = VolumeFirstPiece`，且
    /// `DeclaredTotalBytes &gt; 0`、`DeclaredVolumeCount &gt; 0` ⇒ 这一组"应当有几片、共多少字节"是白拿的。</description></item>
    /// <item><description><b>体积规律</b>（用户口径「除末卷外等大」）：一组里最多只有一片比其余的**小**
    /// —— 那一片就是末卷；其余必须**逐字节等大**。两片都比其余更小 / 一片大一片小 ⇒ 判不出。</description></item>
    /// <item><description><b>合计对上自述</b>：这几片的字节数之和 **==** 首卷自述的整包字节数 ⇒ 整组可证完整。</description></item>
    /// </list>
    ///
    /// <para>⛔ **三条里任何一条不成立都不猜**：没有首卷自述 ⇒ 不给槽位（`HasFirstPiece = false`，
    /// 只如实说"判不出哪一份是首卷"）；体积规律不成立 ⇒ 不下"完整"的结论。调用方拿到
    /// `Complete = false` 时该做的仍然是"什么都不做"（与 `VolumeGatherPlanner` 的兜底同一条）。</para>
    ///
    /// <para>⛔ 纯函数：只读**传进来的卡**，不读盘、不碰任何文件（卡本身由调用方用
    /// <see cref="FileNature.Inspect"/> 拍好）。</para>
    /// </summary>
    public static class VolumePiecesFromCards
    {
        /// <summary>
        /// 这一层里的**同组候选**（调用方只传"同一层、名字上像一组"的那几份，或者干脆传整层里
        /// 内容自证是分卷片的那些）⇒ 给出这一组的结论。
        /// </summary>
        /// <summary>
        /// **按底层识别卡定族，给某个槽位生成规范卷名**（用户 2026-10-17 总则：族由底层识别定，
        /// 名字只当样本）。卡的 `Format` 决定族；基名剥两层归档后缀；最终名交给既有唯一出口
        /// <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/>。
        /// ⛔ 卡给不出格式 / 基名 ⇒ 空串（不猜，调用方照旧走名字那条路）。
        /// </summary>
        public static string CanonicalNameForSlot(FileNatureCard? card, int slot, VolumeNamingFamily? namingFamily = null)
        {
            if (card == null || slot < 1 || string.IsNullOrWhiteSpace(card.Path))
            {
                return string.Empty;
            }

            string stem = Helpers.FileNameHelper.GetArchiveBaseName(
                Helpers.FileNameHelper.GetArchiveBaseName(card.Path));

            /*
             * ⚠ 现场新样本 `1111`：`222-L2.part34B+.rar` 的第一层剥离只掉 `.rar`，
             * 第二层拿不掉 `.part34B+` ⇒ 基名被算成 `222-L2.part34B+` ⇒ 按卡上槽位 3 拼出来是
             * `222-L2.part34B+.part3.rar`（错）。⇒ 再剥一次**卷标记段**（partN / zNN / 三位数字，
             * 连同粘在它上面的杂质一起剥）。⛔ 只是"取基名"这一步，盘上一个字节不动。
             */
            stem = System.Text.RegularExpressions.Regex.Replace(
                stem,
                @"(?i)\.(part\d+|z\d+|\d{3}).*$",
                string.Empty);

            if (stem.Length == 0)
            {
                return string.Empty;
            }

            /*
             * ===== ZIP 有**两个族**，只能由内容分辨（2026-10-18 困难批 `19191919` 现场）=====
             *
             * <para>`X.zip` 这一个名字同时是两族的形状：跨盘 ZIP 族的**末片**（`.z01` 才是第 1 片）、
             * 通用分片族 `X.zip.001` 的**基名**（7z `-tzip -v` 切出来的）。名字分不开，内容分得开：
             * `ZipEndRecordMeansSplit`（EOCD 的中央目录偏移 ≥ 本文件长度）= 通用分片；EOCD 盘号 &gt; 0 = 跨盘。
             * ⇒ 族由**调用方交下来的组证据**决定（<paramref name="namingFamily"/>，来源
             * <see cref="CardVolumeGroupReading.NamingFamily"/>），判不出才退回老口径（按名字样本）。</para>
             *
             * <para>现场逐字：`bbb-L2.zip.001/.002/.003` 三片被认成跨盘族 ⇒ 规范名算成 `X.z01` / `X.zip`
             * ⇒ 两片撞同一个目标名（`绝不覆盖`）⇒ 整组接不起来 ⇒ `contentBytes=0`。</para>
             */
            /*
             * ⚠ **单片卡上格式判不出（中间片没有魔数）时，用组证据补**（2026-10-18 `19191919` 第二次实测）：
             * 通用分片族的**中间片**头是切开的 ⇒ `Format=Unknown` ⇒ 老写法这里 `sample` 给空串
             * ⇒ 又退回名字路径（`X.zip`）⇒ 两片撞名。组读取已经知道这一组是哪一族
             * （`NamingFamily`）⇒ 直接拿它顶格式，⛔ 不猜、⛔ 不放宽。
             */
            VolumeContentFormat format = card.Format;

            if (format == VolumeContentFormat.Unknown && namingFamily is VolumeNamingFamily byGroup)
            {
                format = byGroup switch
                {
                    VolumeNamingFamily.SevenZipNumbered => VolumeContentFormat.SevenZip,
                    VolumeNamingFamily.RarPart => VolumeContentFormat.Rar,
                    VolumeNamingFamily.ZipNumbered or VolumeNamingFamily.ZipSpanned => VolumeContentFormat.Zip,
                    _ => VolumeContentFormat.Unknown
                };
            }

            string sample;

            if (format == VolumeContentFormat.Zip && namingFamily == VolumeNamingFamily.ZipNumbered)
            {
                // 通用分片族的入口形状：`X.zip.001`（命名出口按它定族，⛔ 不是 `X.zip`）。
                sample = stem + ".zip.001";
            }
            else
            {
                sample = format switch
                {
                    // 数字族（`X.7z.00N`）的命名出口要的是**带卷号的样本**，给 `X.7z` 它认不出族。
                    VolumeContentFormat.SevenZip => stem + ".7z.001",
                    /*
                     * ⚠ 现场（新样本 `1111`，卡行逐字 `槽位=3:…part34.rar`）：RAR 族这里原来给的是
                     * `stem + ".rar"` —— **不带卷标记**，命名出口认不出"这是 partN 族"⇒ 返回空串
                     * ⇒ 调用方掉进"名字兜底"（`222-L2.part34B+.rar` → `part34.rar`，正是那个矛盾点）。
                     * ⇒ 与 7z 同理，样本必须**带一个卷标记**（`stem.part1.rar`），按 slot 重排才成立。
                     */
                    VolumeContentFormat.Rar => stem + ".part1.rar",
                    VolumeContentFormat.Zip => stem + ".zip",
                    _ => string.Empty
                };
            }

            if (sample.Length == 0)
            {
                return string.Empty;
            }

            return VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(sample, slot, out string canonical)
                ? canonical
                : string.Empty;
        }

        /// <summary>
        /// **RAR 1.5–4.x（RAR4 新编号族）定槽位**（设计文档 `docs/分卷专属算法-待校验.md` §6.3 第 2 步，工单 #6）：
        /// 卡上 `VolumeNumber` 为空，但 `RawVolumeField` 有值、且首卷那一份带 `FirstVolumeFlag`
        /// ⇒ `基数 = raw₁ − 1`，其余片 `绝对卷号 = raw − 基数`。
        ///
        /// <para><b>四条闸门全过才采信</b>（缺一条 ⇒ 返回空表，什么都不做）：① 每组片都得读得出 raw；
        /// ② raw 互不相同；③ 换算后全部 ≥ 1；④ 一份对一个（同一槽位不许两份）+ 首卷那一份必须是槽位 1 +
        /// 洞最多一个（缺 2 片以上 ⇒ 谁先谁后判不出）。</para>
        ///
        /// <para>⛔ 不写新解析器：raw 与首卷标志都是卡上现成字段（`FileNature.cs:122/86`）。</para>
        /// </summary>
        public static IReadOnlyDictionary<string, int> ResolveRar4Slots(IReadOnlyList<FileNatureCard>? pieces)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            List<FileNatureCard> list = (pieces ?? Array.Empty<FileNatureCard>())
                .Where(card => card != null && !string.IsNullOrWhiteSpace(card.Path))
                .ToList();

            if (list.Count < 2)
            {
                return result;
            }

            FileNatureCard? first = list.FirstOrDefault(card => card.FirstVolumeFlag && card.RawVolumeField != null);

            if (first == null || list.Any(card => card.RawVolumeField == null))
            {
                return result;   // ①
            }

            var raws = list.Select(card => card.RawVolumeField!.Value).ToList();

            if (raws.Distinct().Count() != raws.Count)
            {
                return result;   // ②
            }

            int baseline = first.RawVolumeField!.Value - 1;
            var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (FileNatureCard card in list)
            {
                int slot = card.RawVolumeField!.Value - baseline;

                if (slot < 1)
                {
                    return result;   // ③
                }

                slots[card.Path] = slot;
            }

            var bySlot = new HashSet<int>();

            foreach (int slot in slots.Values)
            {
                if (!bySlot.Add(slot))
                {
                    return result;   // ④ 同槽两份
                }
            }

            if (!bySlot.Contains(1))
            {
                return result;   // ④ 首卷不在槽位 1（基数推错）
            }

            if (bySlot.Max() - bySlot.Count > 1)
            {
                return result;   // ④ 缺 2 片以上
            }

            return slots;
        }

        /// <summary>
        /// **卡的绝对卷号 → 槽位表**（用户 2026-10-17：底层识别优先）。凡是卡上 `VolumeNumber` 有值的
        /// 都要用上（RAR5 / RAR4 新编号族 / 跨盘 zip 末片都能给），要求：每片都有号、号互不相同、
        /// 连成 1..N。⛔ 任何一条不成立 ⇒ 返回空表（⛔ 不猜，也不拿名字顶替）。
        ///
        /// <para>它存在的理由（新样本 `1111` 的现场）：`222-L2.part34B+.rar` 的**名字被读成 part34**，
        /// 名字路据此以为整组有 34 卷、永远"缺卷"；而卡上三片是 **1/2/3**，整组其实齐了。
        /// ⇒ "整组齐不齐"必须由**卡上的卷号**回答，名字只用于定序/改名执行。</para>
        /// </summary>
        public static IReadOnlyList<CardVolumeSlot> BuildContentSlots(IReadOnlyList<FileNatureCard>? pieces)
        {
            List<FileNatureCard> list = (pieces ?? Array.Empty<FileNatureCard>())
                .Where(card => card != null && !string.IsNullOrWhiteSpace(card.Path))
                .ToList();

            if (list.Count < 2)
            {
                return Array.Empty<CardVolumeSlot>();
            }

            /*
             * 同一份文件的**多个名字**（硬链接 / 同目录副本）不算两片（现场 `1111`：候选里第 4 片
             * 与 part34 等大且同一份物理文件 ⇒ 老写法"每片都得有卷号"过不去 ⇒ 整组判不出）。
             * ⇒ 先只拿"卡上有卷号"的那些定槽位，剩下没号的必须是**已在槽位里那一份的别名**，否则判不出。
             */
            List<FileNatureCard> numbered = list.Where(card => card.VolumeNumber != null).ToList();
            List<FileNatureCard> unnumbered = list.Where(card => card.VolumeNumber == null).ToList();

            if (numbered.Count < 2)
            {
                return Array.Empty<CardVolumeSlot>();
            }

            foreach (FileNatureCard alias in unnumbered)
            {
                bool samePhysical = numbered.Any(card =>
                {
                    try
                    {
                        return FileIdentity.IsSamePhysicalFile(card.Path, alias.Path);
                    }
                    catch
                    {
                        return false;
                    }
                });

                if (!samePhysical)
                {
                    return Array.Empty<CardVolumeSlot>();   // 有号的不全 ⇒ 判不出
                }
            }

            var byNumber = new Dictionary<int, FileNatureCard>();

            foreach (FileNatureCard card in numbered)
            {
                if (byNumber.TryAdd(card.VolumeNumber!.Value, card))
                {
                    continue;
                }

                // 同号两份：同一份文件的别名 ⇒ 留一份；不同文件 ⇒ 判不出。
                bool alias = false;

                try
                {
                    alias = FileIdentity.IsSamePhysicalFile(byNumber[card.VolumeNumber!.Value].Path, card.Path);
                }
                catch
                {
                    alias = false;
                }

                if (!alias)
                {
                    return Array.Empty<CardVolumeSlot>();
                }
            }

            long fullSize = numbered.Max(card => card.Size);

            int slotCount = byNumber.Count;

            if (byNumber.Keys.Min() != 1 || byNumber.Keys.Max() != slotCount
                || Enumerable.Range(1, slotCount).Any(number => !byNumber.ContainsKey(number)))
            {
                return Array.Empty<CardVolumeSlot>();   // 不连成 1..N ⇒ 判不出
            }

            return byNumber
                .OrderBy(pair => pair.Key)
                .Select(pair => new CardVolumeSlot(
                    pair.Key,
                    pair.Value.Path,
                    pair.Value.Size,
                    pair.Value.Size < fullSize))
                .ToList();
        }

        /// <summary>
        /// 把"同一份物理文件的两条名字"折叠成一条（优先留**名字本来就是规范名**的那一条）。
        /// <para>⛔ 只读卡上的盘上物理身份；判不出 ⇒ 不合并（"判不出 ⇒ 什么都不做"的同一条）。</para>
        /// </summary>
        private static List<FileNatureCard> CollapseSamePhysicalFile(List<FileNatureCard> pieces)
        {
            if (pieces.Count < 2)
            {
                return pieces;
            }

            var kept = new List<FileNatureCard>(pieces.Count);
            var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (FileNatureCard card in pieces)
            {
                if (!card.HasIdentity)
                {
                    kept.Add(card);
                    continue;
                }

                string key = card.VolumeSerial.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":"
                    + card.FileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (!indexByKey.TryGetValue(key, out int existing))
                {
                    indexByKey[key] = kept.Count;
                    kept.Add(card);
                    continue;
                }

                if (!IsCanonicalFileName(kept[existing]) && IsCanonicalFileName(card))
                {
                    kept[existing] = card;
                }
            }

            return kept;
        }

        /// <summary>这一份的名字**本来就是规范卷名**（卡的 `CanonicalVolumeName` 与文件名一致）。</summary>
        private static bool IsCanonicalFileName(FileNatureCard card) =>
            VolumePieceFacts.IsNameItsOwnCanonicalName(card);

        /// <summary>
        /// **这一组的命名族（由内容证据定）**：ZIP 那一族名字分不开的两个子族就靠它分开
        /// （`ZipNumbered` = 通用分片 `X.zip.001`；`ZipSpanned` = 跨盘 `X.z01…X.zip`）。
        /// 判不出 ⇒ null（调用方退回按名字样本定族）。
        /// </summary>
        internal static VolumeNamingFamily? ResolveNamingFamily(IReadOnlyList<FileNatureCard> pieces)
        {
            /*
             * ===== ZIP 两个子族的分水岭：**带 EOCD 的那一片自己的盘号**（唯一出口）=====
             *
             * `ZipEndRecordMeansSplit` 只回答"中央目录起在这一份之前"（= 它不是一份完整单卷），
             * **两族都成立**：
             *   · 通用分片 `X.zip.001..00N`：单盘 zip 按字节切开 ⇒ EOCD 盘号 0/0
             *     （内容读数 `IsVolumeMember=false`）；
             *   · 跨盘 ZIP `X.z01…X.zip`：归档器写的多盘 zip ⇒ EOCD 盘号 &gt; 0
             *     （内容读数 `IsVolumeMember=true`、`VolumeNumber = 盘号 + 1`）。
             * ⇒ 判族必须看带 EOCD 的那一片的 `Membership`，⛔ 不能用"某一片有 EOCD ⇒ 通用分片"。
             *
             * 2026-10-18 困难批 `15151515` 组合 6 的运行期现场（调试器读数 + 新旧构建日志对照逐字）：
             * 第 1 片被补成 `aaa-L2.zip.002`（通用分片名），而这一组真名是 `aaa-L2.z01/.z02/.zip`
             * ⇒ 引擎按数字分片找 `.z01` 找不到 ⇒ 整组永远拼不起来（① ② 双红）。
             */
            if (pieces.Any(card => card.ZipEndRecordMeansSplit))
            {
                return pieces.Any(card =>
                    card.ZipEndRecordMeansSplit
                    && card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
                    ? VolumeNamingFamily.ZipSpanned
                    : VolumeNamingFamily.ZipNumbered;
            }

            VolumeContentFormat format = pieces
                .Select(card => card.Format)
                .FirstOrDefault(value => value != VolumeContentFormat.Unknown);

            switch (format)
            {
                case VolumeContentFormat.SevenZip:
                    return VolumeNamingFamily.SevenZipNumbered;

                case VolumeContentFormat.Rar:
                    return VolumeNamingFamily.RarPart;

                case VolumeContentFormat.Zip:
                    return pieces.Any(card => card.VolumeNumber is int number && number >= 2)
                        ? VolumeNamingFamily.ZipSpanned
                        : null;

                default:
                    return null;
            }
        }

        public static CardVolumeGroupReading Read(IReadOnlyList<FileNatureCard>? cards)
        {
            var pieces = (cards ?? Array.Empty<FileNatureCard>())
                .Where(card => card != null && !string.IsNullOrWhiteSpace(card.Path))
                .ToList();

            if (pieces.Count == 0)
            {
                return CardVolumeGroupReading.None("这一层里一份分卷片都没有");
            }

            /*
             * ④ 同一份物理文件的两条名字（硬链接）**只算一片**（用户口径「同一份不出现两条名字」）。
             * 真机 2026-10-18 两处现场（`fsutil file queryfileid` 读数）：
             *  · `19191919`：`bbb-L2.z01` 与 `bbb-L2.zip` 是同一份文件（`0x…00045fe4`）——
             *    接片那一档给它另起了规范名（零字节硬链接），原件那条名字还在；按"片"数就变成 4 片，
             *    卡上"恰好 3 片"的纯内容定序永远不成立 ⇒ 整组判不出。
             *  · `8888`：`打包111-L2.part1.rar` 与 `…part1删删删删删删删.rar` 同一 File ID
             *    （`0xB7000000046130`）⇒ 那一组被解了两遍，内容出来两份（判定 ①② 红）。
             * ⛔ 判据只读卡上现成的盘上物理身份；判不出身份（`HasIdentity=false`）⇒ 原样保留，不合并。
             */
            pieces = CollapseSamePhysicalFile(pieces);

            /*
             * ① 首卷的自述（7z 独有）：片数与整包字节数都在卡上。
             * ⛔ 只有 `VolumeFirstPiece` 那一档有；`VolumePiece`（后续片）按定义没有。
             */
            FileNatureCard? first = pieces.FirstOrDefault(card =>
                card.BodyKind == ArchiveBodyKind.VolumeFirstPiece
                && card.DeclaredVolumeCount > 0
                && card.DeclaredTotalBytes > 0);

            var sizes = pieces.Select(card => card.Size).ToList();
            long sum = sizes.Sum();

            /*
             * ② 体积规律：除末卷外每一片都必须与"满片尺寸"逐字节相等，而且"比满片小的"最多一片。
             * 判据只读盘上的字节数（卡的 `Size`），⛔ 不看任何名字。
             */
            long fullSize = sizes.Max();
            int smallerCount = sizes.Count(size => size < fullSize);

            if (fullSize <= 0 || smallerCount > 1)
            {
                /*
                 * 体积规律不成立时，"整组齐不齐"仍可由**卡上的卷号**回答（用户 2026-10-17：底层识别优先）：
                 * 卡上卷号连成 1..N ⇒ 齐了（体积不一致只说明这不是"除末卷外等大"那一族，不否定整齐）。
                 */
                IReadOnlyList<CardVolumeSlot> ruleSlots = BuildContentSlots(pieces);

                return CardVolumeGroupReading.Incomplete(
                    ruleSlots.Count >= 2
                        ? $"体积规律不成立（{sizes.Count} 片里有 {smallerCount} 片比满片小），"
                          + $"但卡上卷号连成 1..{ruleSlots.Count}（来自内容）"
                        : $"体积规律不成立：{sizes.Count} 片里有 {smallerCount} 片比满片（{fullSize} 字节）小",
                    hasFirstPiece: first != null,
                    pieceCount: pieces.Count,
                    declaredTotalBytes: first?.DeclaredTotalBytes ?? 0,
                    sumBytes: sum,
                    entryPath: first?.Path ?? string.Empty,
                    slots: ruleSlots,
                    namingFamily: ResolveNamingFamily(pieces));
            }

            if (first == null)
            {
                /*
                 * 没有首卷自述 ⇒ 7z 中间卷那一档槽位与"整组齐不齐"都判不出（⛔ 不猜）。
                 * **但卡上有绝对卷号时就能判**（用户 2026-10-17：底层识别优先）：
                 * ① 卡上 `VolumeNumber`（RAR5 / RAR4 新编号 / 跨盘 zip 末片）连成 1..N ⇒ 直接用；
                 * ② 否则 RAR 1.5–4.x 用 `RawVolumeField` + 首卷标志锚定基数换算（设计文档 §6.3 第 2 步 / 工单 #6）。
                 */
                IReadOnlyList<CardVolumeSlot> contentSlots = BuildContentSlots(pieces);
                IReadOnlyDictionary<string, int> rar4Incomplete = ResolveRar4Slots(pieces);
                IReadOnlyList<CardVolumeSlot> slotList = contentSlots.Count >= 2
                    ? contentSlots
                    : rar4Incomplete.Count >= 2
                        ? pieces
                            .Where(card => rar4Incomplete.ContainsKey(card.Path))
                            .OrderBy(card => rar4Incomplete[card.Path])
                            .Select(card => new CardVolumeSlot(
                                rar4Incomplete[card.Path], card.Path, card.Size, card.Size < fullSize))
                            .ToList()
                        : Array.Empty<CardVolumeSlot>();

                /*
                 * ===== 通用分片族：**纯内容定序**（设计文档 §6.6 A / 工单 #10）=====
                 * 现场（新样本 `6666` 的配方逐字）：三片分别在三个文件夹，名字是
                 * `打包111-W0-0` / `…W1-0.png` / `…L2.zip.000003` —— 名字一点忙帮不上。
                 * 内容给三条事实：① 尾部有 EOCD 的是**末片**；② 开头是**本地文件头**的是**第 1 片**；
                 * ③ 剩下那片就是中间片 —— 恰好 3 片时唯一（多于 3 片判不出，⛔ 不猜）。
                 */
                if (slotList.Count < 2)
                {
                    List<FileNatureCard> endPieces = pieces.Where(card => card.ZipEndRecordMeansSplit).ToList();
                    List<FileNatureCard> localPieces = pieces.Where(card => card.HasZipLocalHeader).ToList();
                    List<FileNatureCard> middlePieces = pieces
                        .Where(card => !card.HasZipLocalHeader && !card.ZipEndRecordMeansSplit)
                        .ToList();

                    if (endPieces.Count == 1
                        && localPieces.Count == 1
                        && middlePieces.Count == 1
                        && pieces.Count == 3)
                    {
                        slotList = new List<CardVolumeSlot>
                        {
                            new(1, localPieces[0].Path, localPieces[0].Size, false),
                            new(2, middlePieces[0].Path, middlePieces[0].Size, false),
                            new(3, endPieces[0].Path, endPieces[0].Size, true)
                        };
                    }
                }

                /*
                 * ⚠ 措辞必须写实（2026-10-18）：`slotList` 的号可能**不连续**（RAR4 手上只有第 1、3 片时
                 * 是 `1,3`），老写法把它印成「卷号连成 1..2」⇒ 看日志的人会以为整组齐了。
                 * ⇒ 逐号列出来，缺哪几号也直接写。
                 */
                string slotText = string.Join("、", slotList.Select(slot => slot.Index));
                int highest = slotList.Count > 0 ? slotList.Max(slot => slot.Index) : 0;
                List<int> missingSlots = slotList.Count == 0
                    ? new List<int>()
                    : Enumerable.Range(1, highest).Where(number => slotList.All(slot => slot.Index != number)).ToList();

                return CardVolumeGroupReading.Incomplete(
                    slotList.Count >= 2
                        ? (missingSlots.Count == 0
                            ? $"卡上卷号连成 1..{highest}（来自内容；槽位 {slotText}）"
                            : $"卡上卷号：{slotText}（来自内容），缺 {string.Join("、", missingSlots)} 号")
                        : $"体积规律成立（{pieces.Count} 片，满片 {fullSize} 字节），但没有首卷的自述 ⇒ "
                          + "判不出哪一份是首卷、也判不出整组齐不齐",
                    hasFirstPiece: false,
                    pieceCount: pieces.Count,
                    declaredTotalBytes: 0,
                    sumBytes: sum,
                    entryPath: string.Empty,
                    slots: slotList,
                    namingFamily: ResolveNamingFamily(pieces));
            }

            /*
             * ③ 合计 == 首卷自述的整包字节数 ⇒ 可证完整。
             * ⚠ 片数也要对上（自述的片数把首卷算在里面）。
             */
            if (pieces.Count != first.DeclaredVolumeCount || sum != first.DeclaredTotalBytes)
            {
                long missing = first.DeclaredTotalBytes - sum;

                return CardVolumeGroupReading.Incomplete(
                    $"对不上首卷的自述：自述 {first.DeclaredVolumeCount} 片 / {first.DeclaredTotalBytes} 字节，"
                    + $"手上 {pieces.Count} 片 / {sum} 字节"
                    + (missing > 0 ? $"（还差 {missing} 字节）" : "（多出来了）"),
                    hasFirstPiece: true,
                    pieceCount: pieces.Count,
                    declaredTotalBytes: first.DeclaredTotalBytes,
                    sumBytes: sum,
                    entryPath: first.Path);
            }

            /*
             * 槽位：**卡上有卷号就按卷号排**（用户 2026-10-17 总则：底层识别优先），没有的按传入顺序。
             * ⚠ 7z 中间卷没有卷号字段 ⇒ 那一族的槽位仍是"判不出"（调用方⛔ 不许拿名字顶替）。
             * RAR 1.5–4.x：卡上 `VolumeNumber` 为空、但 `RawVolumeField` + 首卷标志有值 ⇒
             * 按设计文档 §6.3 第 2 步换算出绝对卷号（工单 #6），再按它排。
             */
            IReadOnlyDictionary<string, int> rar4Slots = ResolveRar4Slots(pieces);

            int SlotOf(FileNatureCard card, int position) =>
                card.VolumeNumber ?? (rar4Slots.TryGetValue(card.Path, out int rar4Slot) ? rar4Slot : position + 1);

            var ordered = pieces
                .Select((card, position) => (card, position))
                .OrderBy(item => SlotOf(item.card, item.position))
                .ThenBy(item => item.position)
                .ToList();

            var slots = ordered
                .Select((item, position) => new CardVolumeSlot(
                    Index: SlotOf(item.card, position),
                    Path: item.card.Path,
                    Size: item.card.Size,
                    IsLast: item.card.Size < fullSize || (ordered.Count == 1 && item.card.Size == fullSize)))
                .ToList();

            return new CardVolumeGroupReading
            {
                Complete = true,
                HasFirstPiece = true,
                PieceCount = pieces.Count,
                DeclaredTotalBytes = first.DeclaredTotalBytes,
                SumBytes = sum,
                Slots = slots,
                EntryPath = first.Path,
                Reason = $"可证完整：{pieces.Count} 片合计 {sum} 字节 == 首卷自述的整包字节数，满片 {fullSize} 字节",
                NamingFamily = ResolveNamingFamily(pieces)
            };
        }
    }

    /// <summary>一组里的一片（槽位只按"能确定的那几件事"编号，⛔ 不是卷号）。</summary>
    public readonly record struct CardVolumeSlot(int Index, string Path, long Size, bool IsLast);

    /// <summary>「这一层里的分卷片归成一组」的结论。</summary>
    public sealed class CardVolumeGroupReading
    {
        /// <summary>可证完整（片数 + 字节数都对得上首卷自述，且体积规律成立）。</summary>
        public bool Complete { get; init; }

        /// <summary>这一层里有没有**首卷**（有它才有"整包应当多少字节"这条自述）。</summary>
        public bool HasFirstPiece { get; init; }

        /// <summary>参与这一组的片数。</summary>
        public int PieceCount { get; init; }

        /// <summary>首卷自述的整包字节数（没有首卷时为 0）。</summary>
        public long DeclaredTotalBytes { get; init; }

        /// <summary>这几片实际的字节数之和。</summary>
        public long SumBytes { get; init; }

        /// <summary>槽位（只在可证完整时有值）。</summary>
        public IReadOnlyList<CardVolumeSlot> Slots { get; init; } = Array.Empty<CardVolumeSlot>();

        /// <summary>
        /// 这一组的**入口那一份**（引擎这一趟要打开的文件）。判据只有一条：卡上自证
        /// `BodyKind = VolumeFirstPiece` 的那一份 —— 它自己就是"从它开始解"的那一片
        /// （7z / RAR 老式 / ZIP 通用分片这一族；跨盘 ZIP 的入口是末片 `X.zip`，由族规则另行回答）。
        /// 空 = 这一层里没有首卷 ⇒ **判不出**（⛔ 不拿名字顶替）。
        /// </summary>
        public string EntryPath { get; init; } = string.Empty;

        /// <summary>
        /// **还差多少字节**（`DeclaredTotalBytes - SumBytes`，负数按 0）。
        /// 唯一口径：批末那道"卡上说还差字节 ⇒ 这一趟先不收"的闸门、以及日志里的那句话，
        /// ⛔ 都从这里读，别各自再算一遍（用户 2026-10-17：「同一件事的真值只允许有一个出口」）。
        /// </summary>
        public long MissingBytes => Math.Max(0L, DeclaredTotalBytes - SumBytes);

        /// <summary>
        /// **这一组的命名族（由内容证据定）**：ZIP 那一族名字分不开的两个子族就靠它分开
        /// （`ZipNumbered` = 通用分片 `X.zip.001`；`ZipSpanned` = 跨盘 `X.z01…X.zip`）。
        /// 判不出 ⇒ null（调用方退回按名字样本定族，与老口径逐字相同）。
        /// </summary>
        public VolumeNamingFamily? NamingFamily { get; init; }

        /// <summary>判据输入（一句人话：为什么是这个结论）。</summary>
        public string Reason { get; init; } = string.Empty;

        internal static CardVolumeGroupReading None(string reason) => new()
        {
            Reason = reason
        };

        internal static CardVolumeGroupReading Incomplete(
            string reason,
            bool hasFirstPiece,
            int pieceCount,
            long declaredTotalBytes,
            long sumBytes,
            string entryPath,
            IReadOnlyList<CardVolumeSlot>? slots = null,
            VolumeNamingFamily? namingFamily = null) => new()
            {
                Complete = false,
                HasFirstPiece = hasFirstPiece,
                PieceCount = pieceCount,
                DeclaredTotalBytes = declaredTotalBytes,
                SumBytes = sumBytes,
                EntryPath = entryPath,
                Slots = slots ?? Array.Empty<CardVolumeSlot>(),
                NamingFamily = namingFamily,
                Reason = reason
            };
    }
}
