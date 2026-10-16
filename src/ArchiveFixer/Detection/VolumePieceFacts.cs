using System;
using System.IO;

using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **专属算法的入口：先底层识别，后名字**（用户 2026-10-14 口径：「居然先改名字而不看底层识别，
    /// 这恰恰说明你底层识别的部分专属算法根本就没做」）。
    ///
    /// <para><b>顺序（唯一出口，每一步只在前一步答不出时才往下走）</b>：
    /// <list type="number">
    /// <item><description><b>① 卡：格式 = 族</b>（`格式=SevenZip` ⇒ 这一族；⛔ 不按后缀猜族）；</description></item>
    /// <item><description><b>② 卡：`BodyKind`</b> = 单卷 / **这一组的第一片** / 这一组的一片
    /// （7z 那一族只有这条卡答得出，名字与内容都答不出）；</description></item>
    /// <item><description><b>③ 卡：卷号 / 自述整包大小 / 按内容算的片数</b>（RAR5、RAR4 新编号、跨盘 ZIP 末片给得出）；</description></item>
    /// <item><description><b>④ 卡：基名 + 盘上物理身份</b>；</description></item>
    /// <item><description><b>⑤ 名字（兜底）</b>：基名、族、卷标记 —— **只在卡答不出时**用，
    /// 而且卡说"我不是第一片"时，名字里那个 1 一律作废。</description></item>
    /// </list></para>
    ///
    /// <para>⛔ 这个类**只做判断**（纯函数，读卡 + 必要时读盘）；改名 / 硬链接 / 搬运仍在协调器里、
    /// 仍只走既有那几个出口。⛔ 别处不许再自己拼一遍这套顺序。</para>
    /// </summary>
    internal static class VolumePieceFacts
    {
        /// <summary>这一份是不是"某一组的一片"（卡答得出 ⇒ 直接当真；答不出 ⇒ false，交给调用方的老口径）。</summary>
        internal static bool CardSaysItIsPiece(FileNatureCard card)
        {
            return card.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece
                || card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember;
        }

        /// <summary>卡说"我是这一组的一片"吗（第一片或后续片；⛔ 不含 `Membership` 那一档 —— 那一档有它自己的次序）。</summary>
        internal static bool CardSaysItIsOneOfTheGroup(FileNatureCard card)
        {
            return card.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece;
        }
        /// <summary>卡说"我是这一组的第一片"吗（7z 起始头自述整包 &gt; 这一份，或 RAR 的首卷标记）。</summary>
        internal static bool CardSaysFirstPiece(FileNatureCard card)
        {
            return card.BodyKind == ArchiveBodyKind.VolumeFirstPiece;
        }

        /// <summary>
        /// **RAR 族的首卷**（卡上：格式 RAR + 内容自证是成员 + 专属算法 = 这一组的第一片）。
        ///
        /// <para>干什么用（2026-10-16，§29.12 / §29.14）：后缀修正那一档要把它与"单卷归档"分开 ——
        /// 真机 `14141414` 的 `打包111-L2.png` 就是 RAR 首卷，被"按内容格式给后缀"叫成单卷归档名
        /// `打包111-L2.rar`，而同组其余片是 `打包111-L2.part2/3.rar` ⇒ 引擎按 `X.rar` 去找 `X.r00`
        /// ⇒ `Missing volume` ⇒ 整组永远拼不起来。</para>
        /// </summary>
        internal static bool IsRarFirstVolumePiece(FileNatureCard? card)
        {
            return card != null
                && card.Format == VolumeContentFormat.Rar
                && card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                && card.BodyKind == ArchiveBodyKind.VolumeFirstPiece;
        }

        /// <summary>
        /// **RAR 1.5–4.x（RAR4）的绝对卷号**（用户 2026-10-16 拍板，逐字「同意」）：
        /// 主头 `MHD_FIRSTVOLUME` 认出**第 1 卷** ⇒ 它那一份的**原始字段就是基数**
        /// ⇒ `绝对卷号 = 这一片的原始字段 − 基数 + 1`。
        ///
        /// <para>为什么必须这样：RAR4 的**头里没有绝对卷号**，只有卷尾归档结尾块的原始字段，
        /// 而基数是 0 起还是 1 起在 RAR 文档里没写。真机实测（`4444` 三片，走本类的出口读卡）：
        /// 第 1 卷 `首卷标志=True 原始卷号字段=0`、另两片 `=1 / =2` ⇒ 基数 0、绝对卷号 1/2/3。</para>
        ///
        /// <para>判不出（入口不是第 1 卷 / 任一侧读不出原始字段 / 算出来小于 1 / 不是 RAR 族）⇒
        /// 返回 null，调用方照旧判不出 ⇒ 什么都不做（⛔ 不猜）。</para>
        /// </summary>
        internal static int? ResolveRar4SlotIndex(FileNatureCard? pieceCard, FileNatureCard? entryCard)
        {
            if (pieceCard == null
                || entryCard == null
                || !entryCard.FirstVolumeFlag
                || entryCard.RawVolumeField is not int baseRaw
                || pieceCard.Format != VolumeContentFormat.Rar
                || pieceCard.RawVolumeField is not int raw)
            {
                return null;
            }

            int index = raw - baseRaw + 1;

            return index >= 1 ? index : null;
        }

        /// <summary>
        /// **这一份的槽位号该信谁**（顺序见类注释；⛔ 卡上的数字**不覆盖**名字读出来的号，
        /// 理由见下）。
        ///
        /// <para>① 卡说"我是第一片" ⇒ <c>1</c>（这一条名字永远给不出：7z 第一片的名字里没有卷标记）；
        /// ② 卡说"我是这一组的一片、但不是第一片"（`VolumePiece`）而**名字读出 1** ⇒ 那个 1 是假的
        /// ⇒ <c>null</c>（判不出，交给"缺口 + 体积"去定）；
        /// ③ 其余 ⇒ 名字那把尺子（老口径，⛔ 不放宽）。</para>
        ///
        /// <para>⚠ 为什么不直接用卡上的"卷号数字"覆盖名字：跨盘 ZIP 末片的名字（`.zip`）按那一族的约定
        /// 读出来是"第 3 片"，而 EOCD 盘号是另一套基数 ⇒ 一覆盖，`present` 里那个可用锚点就没了、
        /// 两条补片路的前提不成立、整组没人管（实测：官方环台读数 13 绿 → 12 绿，`13131313` / `23232323` 同时红）。</para>
        /// </summary>
        internal static int? ResolveSlotIndex(string path, FileNatureCard card, FileNatureCard? groupAnchorCard = null)
        {
            string name = FileNameHelper.GetFileName(path);
            int? byName = VolumeGroupDetector.TryGetVolumeIndex(name);

            /*
             * ⚠⚠ 2026-10-14 实测两版，**逐字记账，⛔ 别再往回改**：
             *
             * <para>第一版把卡上的**绝对卷号**当权威（`VolumeNumber` 优先；RAR5 / RAR4 新编号 / 跨盘 ZIP
             * 末片的盘号都算）—— 走查档验过、**四族都对**，可真跑**当场打红三份**
             * （`13131313` / `23232323` / `21212121` 全丢内容物）。原因不在卡、**在消费方**：
             * 跨盘 ZIP 末片（`X.zip`）在卡上是 `卷号=3`（对的），而名字那把尺子按那一族的约定读成"第 1 片"，
             * 而 `present` / 收口 / 收拢**都靠那个 1 当锚点**；换成真值 3 之后 `present` 里就没有第 1 槽了，
             * 两条补片路的前提不成立 ⇒ 整组没人管。</para>
             *
             * <para>⇒ 现在的次序（**卡补缺、名字优先**；等消费方能处理"入口不在这一层"再翻过来）：
             * ① 名字给得出卷号 ⇒ 用它；② 名字给不出、卡说"我是这一组的第一片" ⇒ 1
             * （7z / RAR5 / RAR4 首卷**只有这条卡答得出**）；③ 名字给不出、卡说"我是这一组的一片"
             * （7z 的中间片 / 末片）⇒ **判不出**，交给"缺口 + 体积"；④ 其余 ⇒ 判不出。</para>
             */
            if (byName is int named)
            {
                return named;
            }

            if (card.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
            {
                return 1;
            }

            /*
             * ⑤ **RAR 1.5–4.x（RAR4）**：名字与卡都拿不出绝对卷号（头里只有卷尾结尾块的原始字段、
             * 基数待定）⇒ 用**这一组第 1 卷的卡**锚定基数（用户 2026-10-16 拍板，逐字「同意」）。
             * ⛔ 锚取不到 ⇒ null，与加这条之前逐字相同（什么都不做）。
             */
            return ResolveRar4SlotIndex(card, groupAnchorCard);
        }

        /// <summary>
        /// **这一份该叫什么规范名**（族来自卡、基名把伪装尾巴剥掉、卷号来自 <see cref="ResolveSlotIndex"/>）。
        ///
        /// <para>⛔ 只在名字那条路**给不出族**时才该被调用（调用方先试 `TryGetCanonicalVolumeName`）；
        /// 判不出 ⇒ 空串（调用方什么都不做）。</para>
        ///
        /// <para>⚠ 两步（夹具实测）：既有规范名出口是**按"入口名的形状"定族的** ⇒ 先喂
        /// `<剥掉伪装尾巴的名字>.001`（这一族的入口形状），它才给出 `X.7z.001`；直接喂 `X.7z` 会给成
        /// `X.001`（丢族段）。标记写法仍由那个出口决定，⛔ 这里不自己拼最终名。</para>
        /// </summary>
        internal static string ResolveCanonicalName(string path, FileNatureCard card, FileNatureCard? groupAnchorCard = null)
        {
            if (ResolveSlotIndex(path, card, groupAnchorCard) is not int slot || slot < 1)
            {
                return string.Empty;
            }

            string name = FileNameHelper.GetFileName(path);

            /*
             * ⚠ 2026-10-14（走查档实测，`21212121` 的 RAR 族这一份）：卡上明明写着
             * `格式=Rar 分卷身份=VolumeMember 首卷标志=True 专属算法=VolumeFirstPiece` ⇒ 槽位 1，
             * 可这一格原来**只做 7z 一族** ⇒ 逐字 `② 规范名（卡优先）= 判不出`。
             *
             * <para>⇒ 加一条**任何族通用**的路：拿"本族第一卷名"（`TryGetFirstVolumeName`，
             * 唯一出口）当样本交给既有命名出口 —— 它按样本的形状定族，`aaa-L2.part1.rar` ⇒ 这一族的
             * 第 N 卷名。（7z 那一族名字里没有卷标记时它给不出，才走下面那两步。）</para>
             */
            string? firstByName = VolumeGroupDetector.TryGetFirstVolumeName(name);

            /*
             * ⚠ 2026-10-16（RAR4 那一档）：这一片的名字被伪装法吃过卷标记（`bbb-L2.txt` / `bbb-L2.jpg`）
             * ⇒ 名字给不出"本族第一卷名"这个样本。而**这一组第 1 卷的名字**天然就是本族的入口形状
             * （`bbb-L2.part1.rar`）⇒ 拿它当样本，交给同一个命名出口（⛔ 不在这里自己拼名字）。
             */
            if (string.IsNullOrWhiteSpace(firstByName)
                && groupAnchorCard != null
                && groupAnchorCard.Path.Length > 0)
            {
                firstByName = FileNameHelper.GetFileName(groupAnchorCard.Path);
            }

            if (!string.IsNullOrWhiteSpace(firstByName)
                && VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(firstByName!, slot, out string byNameSample)
                && byNameSample.Length > 0)
            {
                return byNameSample;
            }

            if (card.Format != VolumeContentFormat.SevenZip)
            {
                return string.Empty;   // 别的族拿不到"本族第一卷名" ⇒ 判不出（⛔ 不猜）
            }

            string cleaned = FileNameHelper.GetArchiveBaseName(name);

            /*
             * ⚠ 2026-10-14（走查档实测，`10101010` 收拢之后那一份）：名字**已经被清干净**成 `X.7z`
             * （伪装尾巴早剥掉了）时，`GetArchiveBaseName("X.7z")` 会把**族段 `.7z` 一起剥掉** ⇒ 算出 `X.001`
             * （逐字 `② 规范名（卡优先）= pack111-L2.001`，而同组的片叫 `pack111-L2.7z.002`）⇒
             * 收拢按它命名的槽位跟同组对不上。
             * ⇒ 名字自己就以归档后缀结尾时，**基名就用它自己**（族段留着）。
             */
            if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = name;
            }

            if (cleaned.Length == 0)
            {
                return string.Empty;
            }

            if (VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(cleaned + ".001", slot, out string entryShaped)
                && entryShaped.Length > 0)
            {
                return entryShaped;
            }

            return VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(cleaned, slot, out string direct)
                ? direct
                : string.Empty;
        }

        /// <summary>
        /// 这一份的**组引用名**（"哪些文件属于这一组"那把尺子要一个组内成员的名字当参照）：
        /// 名字解析得出就用名字；解析不出 ⇒ **问卡**（卡拼出的规范名就是这一组入口该有的名字）；
        /// 卡也答不出 ⇒ 退回调用方给的老口径（`&lt;基名&gt;.zip`）。
        /// </summary>
        internal static string ResolveGroupReferenceName(string path, string fallback)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return fallback;
            }

            string own = FileNameHelper.GetFileName(path);

            if (own.Length > 0 && VolumeGroupDetector.BelongsToSameGroup(own, own))
            {
                return own;
            }

            string fromCard = ResolveCanonicalName(path, FileNature.Inspect(path));

            return fromCard.Length > 0 && VolumeGroupDetector.BelongsToSameGroup(fromCard, fromCard)
                ? fromCard
                : fallback;
        }

        /// <summary>只读探针用：把这一份走上面的顺序之后的三个结论写成一行（大测试日志与调试探针共用）。</summary>
        internal static string Describe(string path)
        {
            FileNatureCard card = FileNature.Inspect(path);
            int? slot = ResolveSlotIndex(path, card);
            string canonical = ResolveCanonicalName(path, card);

            return $"{FileNameHelper.GetFileName(path)} ⇒ 族={card.Format} 专属算法={card.BodyKind} "
                + $"槽位={(slot?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出")} "
                + $"规范名={(canonical.Length > 0 ? canonical : "判不出")}";
        }
    }
}
