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
        public static string CanonicalNameForSlot(FileNatureCard? card, int slot)
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

            string sample = card.Format switch
            {
                // 数字族（`X.7z.00N`）的命名出口要的是**带卷号的样本**，给 `X.7z` 它认不出族。
                VolumeContentFormat.SevenZip => stem + ".7z.001",
                VolumeContentFormat.Rar => stem + ".rar",
                VolumeContentFormat.Zip => stem + ".zip",
                _ => string.Empty
            };

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
                    slots: ruleSlots);
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

                return CardVolumeGroupReading.Incomplete(
                    slotList.Count >= 2
                        ? $"卡上卷号连成 1..{slotList.Count}（来自内容；"
                          + $"槽位 {string.Join(",", slotList.Select(slot => slot.Index))}）"
                        : $"体积规律成立（{pieces.Count} 片，满片 {fullSize} 字节），但没有首卷的自述 ⇒ "
                          + "判不出哪一份是首卷、也判不出整组齐不齐",
                    hasFirstPiece: false,
                    pieceCount: pieces.Count,
                    declaredTotalBytes: 0,
                    sumBytes: sum,
                    entryPath: string.Empty,
                    slots: slotList);
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
                Reason = $"可证完整：{pieces.Count} 片合计 {sum} 字节 == 首卷自述的整包字节数，满片 {fullSize} 字节"
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
            IReadOnlyList<CardVolumeSlot>? slots = null) => new()
            {
                Complete = false,
                HasFirstPiece = hasFirstPiece,
                PieceCount = pieceCount,
                DeclaredTotalBytes = declaredTotalBytes,
                SumBytes = sumBytes,
                EntryPath = entryPath,
                Slots = slots ?? Array.Empty<CardVolumeSlot>(),
                Reason = reason
            };
    }
}
