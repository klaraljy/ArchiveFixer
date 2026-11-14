using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using System;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 「这一份是不是某个还没凑齐的分卷组的一员」的唯一判据出口（失败模型的一部分）。
///
/// <para>本类只读事实、不动盘上任何东西：把"缺卷"从"损坏 / 失败"里分出来，
/// 让递归路与单层路共用同一条闸门。调用方仍是 <see cref="RecursiveExtractor"/> 的
/// 同名转发（既有调用点与测试钩子一个字不改）。</para>
/// </summary>
internal static class RecursiveVolumeGroupGate
{
    /// <summary>
    /// 同一层里还有**名字**属于同一组的东西吗（⛔ 不看内容 —— 用法见
    /// <see cref="IsPieceOfIncompleteGroup"/> 里那条"片都在、只是名字都坏了"的闸门）？
    /// 判不出 ⇒ true（不抢结论）。
    /// </summary>
    private static bool HasSameGroupNeighbourByName(string piecePath)
    {
        string pieceName = FileNameHelper.GetFileName(piecePath);
        string? directory = Path.GetDirectoryName(piecePath);

        if (pieceName.Length == 0 || string.IsNullOrWhiteSpace(directory))
        {
            return true;
        }

        string self = SafePathHelper.GetFullPathSafe(piecePath);

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory))
            {
                if (self.Length > 0 && SafePathHelper.PathEquals(candidate, self))
                {
                    continue;
                }

                string name = FileNameHelper.GetFileName(candidate);

                if (name.Length == 0)
                {
                    continue;
                }

                if (LegacyVolumeNames.BelongsToSameGroup(name, pieceName)
                    || string.Equals(
                        FileNameHelper.GetArchiveBaseName(name),
                        FileNameHelper.GetArchiveBaseName(pieceName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 这一片分卷的**入口包**在不在它旁边（同一个目录里）？
    ///
    /// <para>判据只认两把既有尺子：<see cref="LegacyVolumeNames.BelongsToSameGroup"/>（同族同基名）
    /// + <see cref="FileNameHelper.IsVolumeContinuationPart"/>（是不是后续卷）。
    /// 「同组里有一份**不是后续卷**的」= 入口在 ✓（RAR 新式是 `.part1.rar`、7z 数字族是 `.001`、
    /// 跨盘 ZIP 族是 `.zip` 那一片）。</para>
    ///
    /// <para>⛔ **判不出 ⇒ 返回 true**（= 按老口径：照旧走引擎给的那一档，一个字节不改）——
    /// 这是补充闸门，不许因为目录读不了把正常结论覆盖掉。</para>
    ///
    /// <para>⚠ 2026-10-13（困难批 `run-23232323`，运行期定案）：**这一份自己不算"同组的入口"**。
    /// 老写法把**待查的这一份自己**也当候选（它当然"同族同基名"、而且不在"后续卷"那一档）⇒
    /// 对**跨盘 ZIP 族**（那一族的入口名就是 `.zip`）恒返回"入口在旁边" ⇒ 补充闸门等于瞎的：
    /// 夹具 `dbg-c6092d64` 里 `out\bbb-W0-0\bbb-L2.zip`（内容自证是分卷组的一员、同组还缺 `.z01/.z02`）
    /// 明明只有它自己一个文件，却被判成"入口在"⇒ 落「达到密码尝试上限」⇒ 整单失败 + 片被扔掉。
    /// ⇒ 跳过与 <paramref name="piecePath"/> **同一个文件**的候选（比对规范化后的完整路径）。</para>
    /// </summary>
    internal static bool IsGroupEntryPresentBeside(string piecePath)
    {
        string pieceName = FileNameHelper.GetFileName(piecePath);
        string? directory = Path.GetDirectoryName(piecePath);

        if (pieceName.Length == 0 || string.IsNullOrWhiteSpace(directory))
        {
            return true;
        }

        string self = SafePathHelper.GetFullPathSafe(piecePath);

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory))
            {
                if (self.Length > 0 && SafePathHelper.PathEquals(candidate, self))
                {
                    // ⛔ 这一份自己不是"同组的入口"（见上面 2026-10-13 那条）。
                    continue;
                }

                string name = FileNameHelper.GetFileName(candidate);

                if (name.Length == 0
                    || !LegacyVolumeNames.BelongsToSameGroup(name, pieceName))
                {
                    continue;
                }

                if (!FileNameHelper.IsVolumeContinuationPart(name))
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// **这一份是不是"某个还没凑齐的分卷组的一员"**（唯一出口；⛔ 只读事实、⛔ 不动盘上任何东西）。
    ///
    /// <para>两个条件同时成立才算：① **它属于某个分卷组** —— 名字像后续卷
    /// （<see cref="FileNameHelper.IsVolumeContinuationPart"/>，能吃骨架档 `par@@@@t3`）
    /// **或** 内容自证（底层识别那张卡 <see cref="FileNatureCard.Membership"/> = <c>VolumeMember</c>）；
    /// ② **同组入口不在它旁边**（<see cref="IsGroupEntryPresentBeside"/>）。</para>
    ///
    /// <para><b>为什么要有这个出口</b>（2026-10-13 困难批 `run-4444`，运行期定案）：以前这一档
    /// 只有名字那一半，于是**名字被伪装过头**的那一片（后缀整个换成 `.jpg`）落不进"缺卷"，
    /// 掉进"密码对了但数据坏了 ⇒ 损坏"⇒ 整单「解压失败」+ 已经解出来的那一片被扔掉
    /// ⇒ 那一组永远缺片（①红）。而事实是**这一组不全**：手上只有这一卷，引擎当然读不到
    /// 后面那些卷里的数据。内容那一半（`Membership`）正是用户要的"底层识别"结论：
    /// RAR 的 Volume 位 / RAR4 的首卷标记 / 跨盘 ZIP 的盘号 —— ⛔ 全都不看名字。</para>
    ///
    /// <para>⛔ 判据**没有放宽到"判不出"**：`Membership == Unknown` 照旧按名字那一半判，
    /// 名字也不像 ⇒ 走引擎给的那一档；`ProvablyNotVolumeMember`（可证不是分卷成员）更是明确排除。</para>
    ///
    /// <para>⛔ <b>落"缺卷"之前先过一道闸：同一层里还有没有别的<b>名字</b>属于这一组的东西</b>
    /// （唯一出口 <see cref="HasSameGroupNeighbourByName"/>）。</para>
    ///
    /// <para><b>为什么这一条对两条路都必须有</b>（守门用例实测逐字）：`EncryptedVolumePipelineTests`
    /// 那两条把**三片都放在同一层**（7z 造的 ZIP 分卷、每片名字缀「删除」）。本机用 7z 复刻同一形状 +
    /// `volinfo` 探针实测逐字：第一片是 `格式=Zip 分卷身份=VolumeMember 卷号=判不出`，
    /// **另外两片内容里一点证据都没有**（`格式=Unknown 分卷身份=Unknown` —— 中间片本来就没有魔数）
    /// ⇒ "旁边还有同族成员"那条**内容**否决对它们等于瞎的；而**名字**那把尺子看得见
    /// （探针逐字：`enc.zip.002删除` ⇒ `名字：卷号=2 … 基名=enc`）。
    /// 缺了它，会把"片都在、只是名字都坏了"错判成「缺卷」⇒ 两条既有守门当场红
    /// （`Expected: "密码错误" Actual: "分卷缺失"`）。</para>
    ///
    /// <para>⚠ 2026-10-21：这一条原来**只给单层路**那两个调用点（参数 `requireNothingElseBeside`），
    /// 迁移成"普通任务全递归"之后同一个形状改由**递归路**遇到 ⇒ 运行期复现
    /// （夹具 `dbg-a9668697`：`enc.zip.001` 是完整一组的第一片、密码错误）被测成「分卷缺失」。
    /// ⇒ 参数删掉、两条路共用同一条闸门（⛔ 不再按"哪条路"分档）。</para>
    /// </summary>
    internal static bool IsPieceOfIncompleteGroup(string archivePath, bool requireNothingElseBeside = false)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return false;
        }

        /*
         * 单层路那一档多一条闸门：**同层里不许还有别的"属于这一组"的东西**
         * （三片同层、只是名字都坏了 ⇒ 那不是"入口不在旁边"，是这一组齐了/在尝试拼）。
         */
        if (requireNothingElseBeside && HasSameGroupNeighbourByName(archivePath))
        {
            return false;
        }

        FileNatureCard card = FileNature.Inspect(archivePath);

        /*
         * ⛔ **内容可证"不是分卷的一员" ⇒ 名字那一格一律作废**（2026-10-18，唯一出口
         * <see cref="Detection.VolumePieceFacts.ContentProvesNotAVolumePiece"/>）。
         *
         * <para>现场逐字（困难批 `24242424`，`walk` 探针）：`222-W1-1.932` 的真实内容是
         * **单盘 ZIP**（卡上 `Membership=ProvablyNotVolumeMember / BodyKind=SingleComplete`），
         * 老写法在这一行**之前**就用名字的 `IsVolumeContinuationPart` 当场 `return true` ——
         * 于是递归路把一条普通 ZIP 当成"这一组的后续卷、入口不在旁边"⇒ 第一条密码候选一错就收场
         * （9 个候选只试了 1 个）⇒ 它里面那一片（第 2 片）永远出不来 ⇒ 整组缺卷。</para>
         *
         * <para>⛔ 判据只否证这一种；`Unknown`（7z 中间片等）照旧让名字那一半说话（见下面那条）。</para>
         */
        if (Detection.VolumePieceFacts.ContentProvesNotAVolumePiece(card))
        {
            return false;
        }

        /*
         * ===== 通用分片族的**第一片**（2026-10-18，困难批 `30303030` 现场）=====
         *
         * <para>现场逐字：`333-L2.zip.png`（7z `-tzip -v` 的第 1 片、被伪装成 `.png`）单独打开 ⇒
         * 7-Zip `Unexpected end of archive` ⇒ 老判据落「压缩包损坏」⇒ 整单失败 + 那一片被连工作区扔掉
         * ⇒ 那一组永远缺第 1 片 ⇒ `contentBytes=0`。</para>
         *
         * <para>内容事实（卡上）：格式=Zip + **开头是本地文件头** + **尾部没有 EOCD** ⇒ 它是从数据里
         * 切出来的**第 1 片**（一份完整的单卷 zip 一定有 EOCD）。⇒ 按「缺卷」收，片原样留着，
         * ⛔ 不按「损坏」扔掉。⛔ 同组入口就在旁边 ⇒ 不抢结论（交给引擎）。</para>
         */
        if (card.Format == VolumeContentFormat.Zip
            && card.HasZipLocalHeader
            && !card.HasZipEndRecord
            && !IsGroupEntryPresentBeside(archivePath)
            && !HasSameGroupNeighbourByName(archivePath))
        {
            return true;
        }

        if (FileNameHelper.IsVolumeContinuationPart(FileNameHelper.GetFileName(archivePath)))
        {
            return true;
        }

        if (card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
        {
            /*
             * ⚠ 2026-10-14（官方环台 `10101010`，运行期事实 —— 用户当场点出来的那一条：
             * 「这个不就是 7z.001 吗，为什么底层识别不到」）：**底层识别到了，是这一档没看那张卡**。
             *
             * <para>卡上逐字（那一刻的探针）：`…\recursive\pack111-W0-0_…\layer-000\output\pack111-L2.7z.txt
             * ⇒ 格式=SevenZip 分卷身份=Unknown 卷号=判不出 专属算法=**VolumeFirstPiece**
             * 自述整包=62919013 体积=26214400`（文件头 `37 7A BC AF 27 1C 00 04` = 7z 魔数）
             * —— 起始头自述的整包字节数比这一份大 ⇒ **还有别的片** ⇒ 这一组还没凑齐。</para>
             *
             * <para>为什么原来轮不到看它：**7z 这个格式里根本没有卷号字段** ⇒ 它的 `Membership`
             * 恒为 `Unknown` ⇒ 老这一支（"判不出 / 可证不是成员 ⇒ 返回 false"）**把 7z 族整族挡在门外**，
             * 于是那一份被当成"引擎解不开的怪东西" ⇒ 整单落「引擎操作失败」⇒ 产物连工作区一起被清掉
             * ⇒ 组 `pack111-L2.7z.001` 永远缺第 1 片（判据 ① 红）。</para>
             *
             * <para>⇒ 判据：**卡上说"我是这一组的第一片"就当真**（`BodyKind == VolumeFirstPiece`），
             * 结论是**缺卷**（⛔ 不是「损坏」、⛔ 不是「失败」）。
             * ⚠ 先过"同层里还有没有同组的东西"那道闸 —— 实测 `EncryptedVolumePipelineTests.加密的RAR4三卷_名字被改烂…`
             * 就是这一档：三片都在同一层、只是名字都坏了，引擎其实解得开 ⇒ 这一档必须**不抢结论**（先让引擎去解）。
             * ⚠ 2026-10-21：这道闸现在**两条路共用**（参数 `requireNothingElseBeside` 已删，见方法说明）。</para>
             */
            if (Detection.VolumePieceFacts.CardSaysItIsOneOfTheGroup(card)
                && !HasSameGroupNeighbourByName(archivePath))
            {
                /*
                 * ⚠ 2026-10-14 同一类的**第二句**也补上：`VolumePiece` = 卡上写着"我是这一组的一片、
                 * 而且不是第 1 片"（7z 族的中间片 / 末片正是这一档：`格式=Unknown` 但
                 * `专属算法：单卷/分卷=VolumePiece`）—— 它**同样**只有这张卡答得出，
                 * 名字那半边（后缀被改、卷标记被吃）与内容那半边（7z 没有卷号字段）都答不出。
                 */
                return true;
            }

            // 判不出 / 可证不是分卷成员 ⇒ 老口径（⛔ 判据没有放宽到"判不出"）。
            return false;
        }

        /*
         * ⚠ 2026-10-13（困难批 `run-23232323`，运行期定案）：**"我不是第一片"这件事内容自己说得出来**，
         * 那一档就不必再问"入口在不在旁边"——入口**不可能**是这一份自己。
         *
         * <para>现场逐字（夹具 `dbg-31260820`）：程序产出那一份跨盘 ZIP 末片 `out\bbb-W0-0\bbb-L2.zip`
         * （内容自述"一共 3 片、我是第 3 片"），第 1 层开它 ⇒ 引擎解不动（同组还缺 `.z01/.z02`）
         * ⇒ 10 条候选试完落「达到密码尝试上限」⇒ 那一单「解压失败」。
         * 为什么原来那两半都没拦住它：名字那一半认的是"后续卷"，而**跨盘 ZIP 族的入口名就是 `.zip`**
         * （那一族的头尾编号是反的）⇒ 名字说"它是入口"；而"入口在不在旁边"那把尺子会把**它自己**
         * 算成"同组入口"（同名同族、又不在"后续卷"那一档）⇒ 两道都放行。</para>
         *
         * <para>内容证据（卷号 &gt; 1）不给这种歧义留位置：这一份**自己写着不是第 1 片**
         * ⇒ 它那一组的入口必然在别处 ⇒ 结论就是**缺卷**（跳过语义），⛔ 不是失败。</para>
         */
        if (card.VolumeNumber is int number && number > 1)
        {
            return true;
        }

        if (HasSameGroupNeighbourByName(archivePath))
        {
            // 同层里还有"名字也属于这一组"的东西 ⇒ 可能是"片都在、只是名字都坏了" ⇒ 不抢结论。
            return false;
        }

        // 判不出片号 / 内容说"我是第 1 片" ⇒ 才需要"同组入口不在它旁边"这一条
        return !IsGroupEntryPresentBeside(archivePath);
    }
}
