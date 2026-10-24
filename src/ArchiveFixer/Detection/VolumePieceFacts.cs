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

        /// <summary>
        /// **内容可证"这一份不是分卷组的一员" ⇒ 名字层的一切"我是第 N 卷"在此作废**（唯一出口）。
        ///
        /// <para><b>为什么必须有这一个出口</b>（2026-10-18 困难批 `24242424`，`walk` 探针逐字）：
        /// `222-W1-1.932` 的真实内容 = **单盘 ZIP**（卡上 `Membership=ProvablyNotVolumeMember`、
        /// `BodyKind=SingleComplete`、`参与分卷那一套=False`），可名字里那个 `932` 被名字尺子读成卷号 ⇒
        /// `ResolveSlotIndex` 回来 `932`、`IsPieceOfIncompleteGroup` 回来 `true` ⇒ 递归路把它当成
        /// "这一组的后续卷、而同组入口不在这一层" ⇒ **第一条候选一失败就收场**（9 个候选只试了 1 个），
        /// 而真正的密码就在密码本第 7 条（`7z t -p…` 实测 `Everything is Ok`）⇒ 它里面那一片
        /// （`222-L2.mp4`，RAR4 的第 2 片）永远出不来 ⇒ 那一组只剩槽位 1、3，缺 2。</para>
        ///
        /// <para>⛔ 判据只读卡上的**内容**事实（`Membership` 与 `BodyKind`），⛔ 不比名字、⛔ 不猜；
        /// 判不出（`Unknown`）⇒ `false` —— 照旧让名字那一半说话（7z 中间片 / 跨盘 ZIP 中间片正是这一档）。</para>
        /// </summary>
        internal static bool ContentProvesNotAVolumePiece(FileNatureCard? card) =>
            card != null
            && (card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                || (card.Format == VolumeContentFormat.SevenZip
                    && card.BodyKind == ArchiveBodyKind.SingleComplete));

        /// <summary>同上，只拿到路径时用它（自己拍一次卡；⛔ 调用方别再自己判一遍）。</summary>
        internal static bool ContentProvesNotAVolumePiece(string? path) =>
            !string.IsNullOrWhiteSpace(path)
            && ContentProvesNotAVolumePiece(FileNature.Inspect(path));

        /// <summary>
        /// **这条名字是不是内容卡自证的规范名**（唯一出口；给"同一份字节两条名字留哪一条"用）。
        ///
        /// <para>2026-10-18 困难批 `15151515` 组合 6 运行期现场（`walk` 探针逐字）：`aaa-L2.zip` 与
        /// `aaa-L2.z00000000ip` 是同一份字节（跨盘 ZIP 末片），两张卡的 `CanonicalVolumeName` 都是
        /// `aaa-L2.zip`；扫描去重原来"留先出现的"留下了脏名 ⇒ 第二轮拿脏名去开这一组
        /// ⇒ 引擎找不到 `.z01` ⇒ ① ② 双红。判据只读卡上的规范名（名字只是辅助，内容说了算），
        /// ⛔ 不比"谁的名字更短/更干净"那种主观尺子；判不出（规范名为空）⇒ false。</para>
        /// </summary>
        internal static bool IsNameItsOwnCanonicalName(FileNatureCard? card) =>
            card != null
            && !string.IsNullOrWhiteSpace(card.CanonicalVolumeName)
            && string.Equals(
                FileNameHelper.GetFileName(card.Path),
                card.CanonicalVolumeName,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// **跨盘 ZIP 的末片**（卡上：格式 ZIP + 内容自证是成员 + 卷号 == 内容自述的总片数）。
        ///
        /// <para>为什么单独问这一件事（2026-10-18 困难批 `12121212` / `26262626` 运行期定案）：
        /// 名字层给 `.zip` 读出来的卷号是 **1**（这一族的入口形状），而内容给的是**盘号**（3 = 末片）。
        /// 把内容号直接当"名字槽位"交给命名出口 ⇒ `X.zip` 被改成 `X.z02` ⇒ 这一组出现两个 `.z02`、
        /// 没有 `.zip`（引擎的入口）⇒ 整组缺末片 ⇒ ① ② 双红。
        /// ⛔ 判据只读卡：`Format=Zip` + `Membership=VolumeMember` + `VolumeNumber == DeclaredVolumeCount ≥ 2`。</para>
        /// </summary>
        internal static bool IsSpannedZipTailPiece(FileNatureCard? card) =>
            card != null
            && card.Format == VolumeContentFormat.Zip
            && card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
            && card.VolumeNumber is int number
            && card.DeclaredVolumeCount >= 2
            && number == card.DeclaredVolumeCount;

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
                || entryCard.BodyKind != ArchiveBodyKind.VolumeFirstPiece
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
        internal static int? ResolveSlotIndex(
            string path,
            FileNatureCard card,
            FileNatureCard? groupAnchorCard = null,
            bool trustCardNumberWhenNameGivesNothing = false)
        {
            /*
             * ⛔ **内容可证"不是分卷的一员" ⇒ 名字这一格全部作废**（2026-10-18，见
             * <see cref="ContentProvesNotAVolumePiece"/> 的现场）：单盘 ZIP 名字里那个 `932`
             * 不是卷号，`222-W1-1.932` 因此被判成"第 932 片"⇒ 下游按分卷收场、密码只试 1 个。
             * ⛔ 只否证这一种；内容判不出时照旧让名字说话。
             */
            if (ContentProvesNotAVolumePiece(card))
            {
                return null;
            }

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
                /*
                 * ⚠ 2026-10-16（第 111 轮，困难批 `24242424` 运行期实测 + 用户口径「底层识别永远最高优先级」）：
                 * 名字这一格**是猜的**时让位给内容 —— 闸门见
                 * <see cref="NameOnlyGuessedItselfAsFirstVolume"/>。
                 *
                 * <para>现场逐字：`111-L2.part020745382.rar` 卡上 `VolumeMember 卷号=2 专属算法=VolumePiece`，
                 * 名字却是 `卷号=1 本族第1卷=它自己 基名=111-L2.part020745382` ⇒ 与同层
                 * `111-L2.part1.rar`（基名 `111-L2`）不同组 ⇒ 收卷窗口只匹配到它自己 1 个
                 * （日志逐字 `引用名=「111-L2.part020745382.rar」｜这一层同族同基名的候选 1 个`）。</para>
                 */
                if (named == 1 && NameOnlyGuessedItselfAsFirstVolume(name, card))
                {
                    return card.VolumeNumber;
                }

                return named;
            }

            if (card.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
            {
                return 1;
            }

            /*
             * ⑤ **消费方明确表示"第 1 槽不在这层也认"时，才信卡上的绝对卷号**
             * （用户 2026-10-16 口径：「消费方接受『第 1 槽不在这层』后把**卡的绝对卷号**翻成权威」）。
             *
             * <para>⛔ 只在**名字什么都给不出**时才用（上面两步已经判过名字与"第一片"）；
             * ⛔ 只认 **≥ 2** 的号 —— 卡上的 1 与"名字读出来的 1"在过去那次全局翻转里正是打红三份的
             * 那一格（跨盘 ZIP 末片的卡写 3、名字读 1，消费方靠那个 1 当锚点，见上面那段实测教训）。</para>
             *
             * <para>真机现场（`19191919`，卡逐字）：`aaa-L2-976` = `分卷身份=VolumeMember 卷号=2 raw=1
             * 专属算法=VolumePiece`，而它的名字什么都给不出（`卷号=判不出 规范名=` 空）⇒ 加这一档之前
             * 接片那一档永远拿不到规范名 ⇒ 整组收不到一起。</para>
             */
            if (trustCardNumberWhenNameGivesNothing
                && card.VolumeNumber is int cardNumber
                && cardNumber >= 2)
            {
                return cardNumber;
            }

            /*
             * ⑥ **RAR 1.5–4.x（RAR4）**：名字与卡都拿不出绝对卷号（头里只有卷尾结尾块的原始字段、
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
        /// <summary>
        /// **名字这一格是不是"猜的"**（用户 2026-10-16 口径：「底层识别一定大于名字，底层识别永远最高优先级」）：
        /// 那把名字尺子**压根没认出卷标记**、于是按"本体"把它读成第 1 卷（`TryGetFirstVolumeName` 回来
        /// 就是它自己），而**卡上的内容是"我是这一组的一片、绝对卷号 N≥2"** ⇒ 名字那句是猜的、内容那句是读出来的。
        ///
        /// <para>⛔ **跨盘 ZIP 那一族整个排除在外**（`*.zip` 本体）：它的名字读出来的 1 **不是猜的** ——
        /// 那是这一族的入口形状（消费方靠它找这一组的入口），而内容那个号是**盘号**。实测两张卡逐字摆一起看：
        /// `333-L2.zip`（困难批 `13131313` 跑完后的树）＝名字 `卷号=1 本族第1卷=333-L2.zip（它自己）`、
        /// 内容 `VolumeMember 卷号=3 专属算法=VolumePiece 按内容算的片数=3`；而 RAR 那一片
        /// `111-L2.part020745382.rar` ＝名字 `卷号=1 本族第1卷=它自己`、内容 `VolumeMember 卷号=2`。
        /// 两张卡形状一样、**结论必须相反**：前者翻成 3 的实测后果 = `13131313` / `23232323` 当场从
        /// 「①✔ 成品 1」变成「①✘ 成品 0」（日志逐字多出 `333-L2.zip：引擎调用 … -aoa` ⇒
        /// `解压失败：333-L2.zip，原因：分卷压缩包缺少必要分卷`）。</para>
        ///
        /// <para>⛔ 另一条闸门：本族第 1 卷名**必须就是它自己**。跨盘 ZIP 末片的名字被改坏时（实测
        /// `bbb-L2.zi#cp`：名字读出来的是**另一个名字** `bbb-L2.zip` —— 那一族的入口形状）**不算猜**。</para>
        /// </summary>
        internal static bool NameOnlyGuessedItselfAsFirstVolume(string name, FileNatureCard card) =>
            card.BodyKind == ArchiveBodyKind.VolumePiece
            && card.VolumeNumber is int contentNumber
            && contentNumber >= 2
            && string.Equals(VolumeGroupDetector.TryGetFirstVolumeName(name), name, StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        internal static string ResolveCanonicalName(
            string path,
            FileNatureCard card,
            FileNatureCard? groupAnchorCard = null,
            bool trustCardNumberWhenNameGivesNothing = false,
            int? contentSlot = null,
            VolumeNamingFamily? namingFamily = null)
        {
            /*
             * ⛔ **有"内容定的槽位 + 内容定的命名族"时，名字一个字节都不许参与命名**
             * （2026-10-18 困难批 `19191919` 现场）。
             *
             * <para>现场逐字：`bbb-L2.zip.001/.002/.003` 是 7z `-tzip -v` 的通用分片，三片被伪装成
             * `bbb-L2.zip` / `bbb-W1-0.BAyzip` / `bbb-L2.zip-658`；卡上三条内容事实已经排出 1/2/3
             * （本地文件头 / 中间 / 尾部 EOCD），而名字把 `X.zip` 读成跨盘 ZIP 族的**末片**
             * ⇒ 规范名算成 `X.z01` / `X.zip` ⇒ 两片撞同一个目标名（`绝不覆盖`）⇒ 整组接不起来。</para>
             *
             * <para>⇒ 判据全用内容（槽位 = 组读取的 `Slots`，族 = 组读取的 `NamingFamily`），
             * 名字只用来取基名；拼名仍交给唯一出口 <see cref="VolumePiecesFromCards.CanonicalNameForSlot"/>。
             * 两者缺一 ⇒ 照旧走下面老口径（判不出 ⇒ 不猜）。</para>
             */
            if (contentSlot is int slotFromContent
                && slotFromContent >= 1
                && namingFamily is VolumeNamingFamily family)
            {
                string byContent = VolumePiecesFromCards.CanonicalNameForSlot(card, slotFromContent, family);

                if (byContent.Length > 0)
                {
                    return byContent;
                }
            }

            if (ResolveSlotIndex(path, card, groupAnchorCard, trustCardNumberWhenNameGivesNothing) is not int slot || slot < 1)
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
             * ⚠ 2026-10-16（与上面槽位同一格）：名字自己就是"本族第 1 卷的样子"、而内容说它是第 N≥2 片时，
             * **别拿它自己当命名样本** —— 那会把伪装数字一起并进基名（`111-L2.part020745382.part2.rar`）。
             * 改用**这一组第 1 卷的名字**（锚）当样本：它就是本族的入口形状，交给同一个命名出口
             * （与下面 RAR4 那一档同一个做法，⛔ 不在这里自己拼名字）。
             */
            if (NameOnlyGuessedItselfAsFirstVolume(name, card)
                && groupAnchorCard != null
                && groupAnchorCard.Path.Length > 0
                && groupAnchorCard.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
            {
                firstByName = FileNameHelper.GetFileName(groupAnchorCard.Path);
            }

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

            string cleaned = FileNameHelper.SecondStepBaseName(name);

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
