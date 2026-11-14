using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Services;

/// <summary>
/// 「按真实格式修正」的名字计算：内容卡说是什么格式、叫什么基名，就落成什么名字。
///
/// <para>从 <see cref="RenameService"/> 抽出的协作类（「命名规则」里最重的一块）：
/// 判据只用**内容事实**（<see cref="FileNatureCard"/>）与既有命名出口
/// （<c>VolumeNameRepair</c> / <c>VolumeGroupResolver</c>），⛔ 不比名字猜卷号；
/// 判不出 ⇒ 原样返回（一个字都不改）。</para>
///
/// <para>⛔ 闸门顺序有意为之：先"单卷完整包"，再"名字被改坏的第一卷"，再"格式未知 / 纯数字卷号 /
/// 后缀正常"，最后才按内容卡给后缀 —— 顺序就是真机事故换来的判据顺序，⛔ 不许调换。</para>
/// </summary>
internal static class RenameDetectedFormatFixer
{
    /// <summary>识别器认不出格式时给的那个值（见 <c>ArchiveEngineContracts</c>）。</summary>
    private const string UnknownDetectedFormat = "Unknown";

    /// <summary>
    /// **这一份"被判成单卷归档名"会是错的吗**（2026-10-16；7z 版先落、RAR 版随 A 簇补上）。
    ///
    /// <para>判据只用卡上的**内容事实**（⛔ 不比名字、⛔ 不猜卷号、⛔ **不用 `FirstVolumeFlag`**）：</para>
    /// <list type="number">
    /// <item><description>族必须是 **7z 或 RAR**（这两族的 `<基名>.<ext>` 只有两种含义：单卷归档、或"该组第 1 片"）；</description></item>
    /// <item><description>内容自述是**某分卷组的一员**（`Membership == VolumeMember`）；</description></item>
    /// <item><description>而它**不是那一组的第一片**（`BodyKind != VolumeFirstPiece`）⇒ 这个名字会把槽位 1 占掉。</description></item>
    /// </list>
    ///
    /// <para>⛔ **为什么不能拦 ZIP 族**（实测教训）：跨盘 ZIP 的**入口就是** `X.zip`（`.zip` 是末片、`.z01` 才是第 1 片）
    /// ⇒ 把 `X.zi#cp` 建议回 `X.zip` 是**正确**的；拦了它 ⇒ `23232323` 从 ①✔ 变 ①✘（7-Zip 找不到入口 ⇒ 分卷缺失）。</para>
    ///
    /// <para>⛔⛔ **为什么不能用 `FirstVolumeFlag` 当权威**（另一条实测教训）：`10101010` 的**入口**那一份
    /// 卡上逐字是 `BodyKind=VolumeFirstPiece | First=False` —— 对 7z 族这两个字段**并不一致**，
    /// 用它当闸门会把**入口自己**的槽位 1 撤掉（那份当场从 ①✔ 变 ①✘）。</para>
    ///
    /// <para>判不出（文件不在了 / 读不出来 / 族对不上）⇒ <c>false</c> ⇒ 照旧老口径。</para>
    /// </summary>
    private static bool IsVolumePieceWithoutFirstSlot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            ArchiveFixer.Detection.FileNatureCard card = FileNature.Inspect(path!);

            /*
             * ⛔ **7z 族**：只认 `BodyKind`（`VolumeFirstPiece` / `VolumePiece`）——
             * 这一份可能就是入口自己，而它的 `Membership` 是 `Unknown`（7z 的头里没有分卷身份字段）
             * ⇒ ⛔ 不能要求 `Membership == VolumeMember`（2026-10-16 实测：那样这条闸门会**失效**，
             * `10101010` 的入口当场被改回 `<基名>.7z`、判据 ① 又红）。
             */
            bool sevenZipPiece = card.Format == VolumeContentFormat.SevenZip
                && card.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece;

            /*
             * ⛔ **RAR 族**：头里给不出绝对卷号（RAR4 只有"我是分卷"一个标志、RAR5 才有卷号），
             * 判据 = 内容自述是这一族的一员（`Membership == VolumeMember`）**且不是第一片**
             * （`BodyKind != VolumeFirstPiece`）⇒ 把它叫成 `X.rar`（单卷归档名 / 老式第 1 卷名）
             * 会占掉槽位 1 ⇒ 整组永远凑不起来（真机 A 簇 7 份：`bbb-L2.part2/.part3.rar` 被叫成 `bbb-L2.rar`）。
             */
            bool rarPieceWithoutFirstSlot = card.Format == VolumeContentFormat.Rar
                && card.Membership == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                && card.BodyKind != ArchiveBodyKind.VolumeFirstPiece;

            return sevenZipPiece || rarPieceWithoutFirstSlot;
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildFixByDetectedFormatFileName(ArchiveTask task, RenameOptions options)
    {
        string oldPath = task.CurrentPath;

        if (string.IsNullOrWhiteSpace(oldPath))
        {
            oldPath = task.OriginalPath;
        }

        string fileName = Path.GetFileName(oldPath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        /*
         * 第一大步已经用内容卡确认 SingleComplete：先按卡上的基名 + 规范格式名，
         * ⛔ 不能先落到"分卷名"分支（`aaa-W1-1.rar.000001` 会被 VolumeNameRepair 原样退回）。
         */
        FileNatureCard? singleCompleteCard = TryGetSingleCompleteCard(task);
        bool singleNameLooksLikeVolume = LegacyVolumeNames.IsVolumePart(fileName);
        if (singleCompleteCard != null
            && (singleNameLooksLikeVolume
                || (task.ExtensionStatus != StatusText.ExtensionNormal
                    && task.ExtensionStatus != StatusText.ExtensionEmbedded)))
        {
            string cardExtension = VolumeContentInference.ExtensionFor(singleCompleteCard.Format);
            string singleExtension = string.IsNullOrWhiteSpace(cardExtension) ? string.Empty : "." + cardExtension;

            if (string.IsNullOrWhiteSpace(singleExtension))
            {
                singleExtension = task.SuggestedExtension;
            }

            if (string.IsNullOrWhiteSpace(singleExtension))
            {
                singleExtension = GetSuggestedExtensionByFormat(task.DetectedFormat);
            }

            singleExtension = RenameNameRules.NormalizeExtension(singleExtension, options.TargetExtension);

            return RenameNameRules.NormalizeResultFileName(singleCompleteCard.ArchiveBaseName + singleExtension);
        }
        /*
         * 分卷文件：先看"名字被改坏的第一卷"能不能靠这一颗按钮修好（2026-09-26 审计：入口收敛）。
         *
         * 判据仍然是 VolumeNameRepair.Plan 那六条（与失败清单里写的建议、与①页「修复分卷名并重试」
         * 真正会改成的名字**同一份计算**）—— 能修就给标准名；修不了（是后续卷 / 没有同组卷 /
         * 目标名被占 / 名字本来就标准…）才落到下面那句"分卷文件不改名"。
         *
         * 以前这条路是断的：智能修正对分卷一律原样返回，于是"名字被改坏的第一卷"只能靠
         * 另一颗按钮（而且那颗按钮要先跑一次失败的解压才亮）—— 用户看到的就是"两颗按钮意义一样，
         * 可第一颗从来没亮过"。
         */
        if (LegacyVolumeNames.IsVolumePart(fileName))
        {
            VolumeNameRepairPlan volumeRepair = VolumeNameRepair.Plan(
                oldPath,
                VolumeNameRepair.EnumerateFileNamesInDirectory(oldPath));

            return volumeRepair.CanRepair ? volumeRepair.SuggestedFileName : fileName;
        }

        /*
         * ⛔ 两道"不许动"的闸门（2026-09-29 真机事故，用户当场报）。
         *
         * 现场：`amb909.7.01`（2 GiB，内容带 7z 魔数）与 `amb909.z.2`（1.89 GB，认不出格式）
         * 在同一个目录里，是一组**名字被改坏的分卷**。老逻辑各改一次后缀 ——
         * `.01` → `.7z`、`.2` → `.7z`（认不出格式时"建议后缀"是空的，就兜底成设置里的默认后缀 `.7z`），
         * **卷号被吃掉**：7-Zip 再也找不到后续卷，整个包从"还能救"变成"文件损坏"。
         *
         * ① **格式未知 → 一个字都不改**：连"它是什么"都不知道，改后缀只能是猜；
         *    设置里的默认后缀不是证据（这条同时治"Unknown 文件被改成默认后缀"这一类）。
         * ② **末尾那一段是纯数字**（`.01` / `.2` / `..3` 这种）→ 那是**卷号**，不是后缀：
         *    交给分卷那条路（`VolumeNameRepair` / 内容级推断），这里绝不动它。
         */
        if (string.IsNullOrWhiteSpace(task.DetectedFormat) ||
            string.Equals(task.DetectedFormat, UnknownDetectedFormat, StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        if (RenameNameRules.LooksLikeNumericVolumeTail(fileName))
        {
            return fileName;
        }

        /*
         * 后缀本来就正常 → 没有可修正的东西，原样返回。
         *
         * 光比"当前后缀 == 建议后缀"是不够的：`xxx.apk` 的内容也是 ZIP，建议后缀是 .zip，
         * 按那个比法它就会被改成 `xxx.zip` —— 把 Android 安装包变成一个打不开的压缩包。
         * 同一类还有 .jar / .docx / .xlsx / .pptx / .epub / .vsix / .nupkg …（ExtensionHelper.ZipContainerExtensions）。
         * 判定统一由 ArchiveDetectService 给（后缀正常 / 分卷后缀 / 内嵌归档 / 不匹配 / …），这里只认结论，不自己再推一遍。
         */
        if (task.ExtensionStatus == StatusText.ExtensionNormal ||
            task.ExtensionStatus == StatusText.ExtensionVolume ||
            task.ExtensionStatus == StatusText.ExtensionEmbedded)
        {
            return fileName;
        }

        /*
         * ⛔ 2026-10-16（ProcMon 定案：六步改名链的第 5/6 步就是这里）：**内容自证是"某分卷组的一片"的，
         * 不许按"单卷归档"改后缀**。
         *
         * 现场逐字：`pack111-L2.7z.txt`（卡上 `BodyKind=VolumeFirstPiece`，实为某分卷组的**第 1 片**）
         * 走到这里 ⇒ `.txt` 与检测到的 7z 不匹配 ⇒ 建议成 `<基名>.7z` ⇒ 产出层里凭空多出一个
         * **单卷归档名**、与同一份字节的 `.001` 并存（同一 inode）⇒ 收拢计划随后被
         * 「目标名已经被别的文件占用了」挡住 ⇒ `10101010` 的判据 ① 一直红。
         *
         * 判据只用事实（卡的 `BodyKind`，⛔ 不比名字、⛔ 不猜卷号）：是某组的一片 ⇒ **不给建议**
         * （它的规范卷名交给卷名那一套：㈠档 `X.7z.001` / 接片 / 收拢）。
         * 判不出（读不到卡 / 不是分卷）⇒ 照旧老口径，一个字不改。
         */
        if (IsVolumePieceWithoutFirstSlot(task.CurrentPath))
        {
            return fileName;
        }

        /*
         * ⚠ 2026-10-16（ProcMon 定案 §29.12 + 尺子裁决 §29.14）：RAR 的**首卷**原来漏在上面那条闸门之外 ——
         * 卡上它是 `格式=Rar 分卷身份=VolumeMember 专属算法=VolumeFirstPiece`（真机 `14141414` 的
         * `打包111-L2.png`，payload 就是 `打包111-L1.7z`），于是被"按内容格式给后缀"叫成**单卷归档名**
         * `打包111-L2.rar`；而同组其余片是 `打包111-L2.part2/3.rar` ⇒ 引擎按 `X.rar` 去找老式编号族的
         * `X.r00` ⇒ `Missing volume` ⇒ 整组永远拼不起来。
         *
         * ⇒ 首卷要给的是**规范卷名** `X.part1.rar`（本程序只认 `partN` 一族，用户 2026-10-16 拍板
         * 「老编号族少见、少花心思」）。名字仍交给**既有命名出口**拼（拿 `stem.part1.rar` 当本族入口形状的
         * 样本），⛔ 不在这里手写卷名；拼不出来 ⇒ 照旧不改（判不出 ⇒ 什么都不做）。
         *
         * ⚠ **判别事实**（`DebugRunner` 的 `names` 档实测，⛔ 不猜）：
         * · `风景03.ra删除r` ⇒ `TryGetFirstVolumeName = 风景03.rar` —— **名字自己就读得出"本族第 1 卷"**
         *   ⇒ 照旧老口径（给 `X.rar`，既有守门 `VolumeEntryPointTests.批首改名_格式未知与末尾纯数字…` 钉着）；
         * · `打包111-L2.png` ⇒ `TryGetFirstVolumeName = 判不出`、`BelongsToSameGroup(自己,自己) = False`
         *   —— 名字被改成别的类型（伪装法⑤）⇒ 名字这一路什么都给不出，只能靠卡说"我是这一组的第一片"。
         * ⇒ 只接**后者**：名字再也给不出"本族第一卷名"时，才按规范卷名改。
         */
        FileNatureCard firstVolumeCard = FileNature.Inspect(task.CurrentPath);

        if (VolumePieceFacts.IsRarFirstVolumePiece(firstVolumeCard)
            && string.IsNullOrWhiteSpace(LegacyVolumeNames.TryGetFirstVolumeName(fileName)))
        {
            string rarStem = FileNameHelper.GetArchiveBaseName(task.CurrentPath);

            if (rarStem.Length > 0
                && VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(
                    rarStem + ".part1.rar",
                    1,
                    out string rarCanonicalName)
                && rarCanonicalName.Length > 0)
            {
                return rarCanonicalName;
            }

            return fileName;
        }

        string suggestedExtension = task.SuggestedExtension;
        if (string.IsNullOrWhiteSpace(suggestedExtension))
        {
            suggestedExtension = GetSuggestedExtensionByFormat(task.DetectedFormat);
        }

        suggestedExtension = RenameNameRules.NormalizeExtension(suggestedExtension, options.TargetExtension);

        if (string.IsNullOrWhiteSpace(task.DetectedFormat) ||
            string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(options.UnknownFormatAction, "Skip", StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            suggestedExtension = RenameNameRules.NormalizeExtension(options.TargetExtension, ".7z");
        }

        string currentExtension = Path.GetExtension(fileName);

        if (string.IsNullOrWhiteSpace(currentExtension))
        {
            return fileName + suggestedExtension;
        }

        if (string.Equals(currentExtension, suggestedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        string fixedMultiExtensionName = TryFixMultiExtensionFileName(fileName, suggestedExtension);

        if (!string.IsNullOrWhiteSpace(fixedMultiExtensionName))
        {
            return fixedMultiExtensionName;
        }

        string baseName = fileName[..^currentExtension.Length];

        if (string.IsNullOrWhiteSpace(baseName))
        {
            return fileName;
        }

        return RenameNameRules.NormalizeResultFileName(baseName + suggestedExtension);
    }

    private static FileNatureCard? TryGetSingleCompleteCard(ArchiveTask task)
    {
        try
        {
            string path = string.IsNullOrWhiteSpace(task.CurrentPath) ? task.OriginalPath : task.CurrentPath;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            FileNatureCard card = FileNature.Inspect(path);

            return card.BodyKind == ArchiveBodyKind.SingleComplete
                   && !string.IsNullOrWhiteSpace(card.ArchiveBaseName)
                ? card
                : null;
        }
        catch
        {
            // 读不到内容卡 = 判不出 ⇒ 回落到既有后缀修复路径，不在改名里猜。
            return null;
        }
    }

    private static string TryFixMultiExtensionFileName(string fileName, string suggestedExtension)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(suggestedExtension))
        {
            return string.Empty;
        }

        suggestedExtension = RenameNameRules.NormalizeExtension(suggestedExtension, ".7z");

        List<string> extensions = RenameNameRules.GetAllExtensions(fileName);

        if (extensions.Count < 2)
        {
            return string.Empty;
        }

        int matchedIndex = -1;
        string matchedCanonical = string.Empty;

        for (int i = 0; i < extensions.Count; i++)
        {
            if (string.Equals(extensions[i], suggestedExtension, StringComparison.OrdinalIgnoreCase))
            {
                matchedIndex = i;
                matchedCanonical = extensions[i];
                continue;
            }

            /*
             * ⚠ 2026-10-08（用户 2026-10-07 口径「无论是专属档还是统一档，第一步都是去除中文字符」）：
             * 命中的那一段**自己也可能被塞了杂质** —— 网盘把 `111.zip.txt` 变成 `111.zi删除p.txt`
             * 是常规形状。老写法只比"逐字相等"，于是这一段匹配不上 ⇒ 整条退回
             * `baseName + suggestedExtension`，拼出 `111.zi删除p.zip`（脏段留在名字里）。
             *
             * 判据**转调既有出口**（⛔ 不新造第二把尺子）：`TryRecoverDisguisedArchiveBody`
             * 就是分卷专属档㈡档用的那把"归档体还原"尺子（唯一解，有歧义一律不认）。
             * 它返回的规范段**不带点**（与 `VolumeContentInference.ExtensionFor` 同口径），这里补点再比。
             */
            if (ExtensionHelper.TryRecoverDisguisedArchiveBody(extensions[i], out string recovered, out _) &&
                string.Equals("." + recovered, suggestedExtension, StringComparison.OrdinalIgnoreCase))
            {
                matchedIndex = i;
                matchedCanonical = "." + recovered;
            }
        }

        if (matchedIndex < 0)
        {
            return string.Empty;
        }

        string namePart = fileName;

        int totalRemoveLength = 0;

        for (int i = extensions.Count - 1; i > matchedIndex; i--)
        {
            totalRemoveLength += extensions[i].Length;
        }

        if (totalRemoveLength <= 0 || totalRemoveLength >= fileName.Length)
        {
            return string.Empty;
        }

        namePart = fileName[..^totalRemoveLength];

        if (string.IsNullOrWhiteSpace(namePart))
        {
            return string.Empty;
        }

        /*
         * 命中那一段的**规范形态**与它现在的样子不同 ⇒ 连它一起改（`zi删除p` ⇒ `zip`）：
         * 只删后面的 `.txt` 会留下脏段，那正是"第一步去中文"没落到统一档上的表现。
         * 逐字相等时这一步是恒等变换（既有形状一个字不动）。
         */
        string matchedRaw = extensions[matchedIndex];

        if (!string.Equals(matchedRaw, matchedCanonical, StringComparison.OrdinalIgnoreCase))
        {
            string stem = namePart[..^matchedRaw.Length];

            if (string.IsNullOrWhiteSpace(stem))
            {
                return string.Empty;
            }

            namePart = stem + matchedCanonical;
        }

        namePart = RenameNameRules.NormalizeResultFileName(namePart);

        if (string.IsNullOrWhiteSpace(namePart))
        {
            return string.Empty;
        }

        return namePart;
    }

    private static string GetSuggestedExtensionByFormat(string format)
    {
        return ExtensionHelper.GetSuggestedExtensionByFormat(format);
    }
}
