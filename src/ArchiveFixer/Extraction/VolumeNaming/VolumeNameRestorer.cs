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
using static ArchiveFixer.Extraction.VolumeNameRepair;

namespace ArchiveFixer.Extraction
{
    /// <summary>伪装名恢复：把被改坏/夹了杂质的卷名恢复成可打开的标准名；执行仍只走 <see cref="VolumeNameRepair.TryApply"/>。</summary>
    public static class VolumeNameRestorer
    {        public static IReadOnlyList<string> RestoreDisguisedInnerPackageNames(
            IReadOnlyList<string>? candidatePaths,
            Action<string, string>? log = null)
        {
            if (candidatePaths == null || candidatePaths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var handledGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? candidate in candidatePaths)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                /*
                 * ⚠ **第 0 档：纯文本清洗（只去非 ASCII 杂质）** —— 用户 2026-10-11 口径：
                 * 「简单的改后缀，像删除中文字符这么简单的为什么做不了」「分卷识别和操作是第二大步的事」。
                 *
                 * 它排在"认底层 / 整组自洽 / 兄弟在场"**前面**，因为它一条都不需要：
                 * 名字里那几个中文字是**插进去的杂质**，去掉之后剩下的还是**本来那个形态**
                 * （`111.zi删除p` → `111.zip`、`111_.par删t1.ra除r` → `111_.part1.rar`），
                 * 既不用问魔数（末片是续盘、`PK\x07\x08` 本来就嗅不出格式），也不用等同目录的兄弟回来
                 * —— 真机 FFFF 就是卡在这两处：三个数字卷改好了、**末片一个汉字没去掉** ⇒
                 * `第 1 层：111.zi删除p：这一组还没凑齐（分卷压缩包缺少必要分卷）` ⇒ 第二大步解不开。
                 *
                 * ⛔ 只动**卷标记 / 归档后缀那一段**，基名一个字符不动（脏基名原文不动 —— 那可能是真名字）；
                 * ⛔ 执行体仍只有 `TryApply` 一套（只改名、字节数核对、失败倒序改回、绝不覆盖）。
                 */
                if (TryStripNonAsciiJunkFromTailSegment(Path.GetFileName(candidate), out string junkCleaned))
                {
                    string junkDirectory = Path.GetDirectoryName(candidate) ?? string.Empty;
                    string junkTarget = Path.Combine(junkDirectory, junkCleaned);

                    VolumeNameRepairPlan junkPlan = BuildRestorePlan(
                        new[]
                        {
                            new RestoreRenameItem(
                                candidate,
                                junkTarget,
                                Path.GetFileName(candidate),
                                junkCleaned)
                        });

                    VolumeNameRepairResult junkApplied = TryApply(junkPlan);

                    if (junkApplied.Success)
                    {
                        renamed[candidate] = junkTarget;

                        log?.Invoke(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.InnerRestoreDoneFormat,
                                junkPlan.Describe()));

                        continue;
                    }

                    log?.Invoke(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.InnerRestoreBlockedFormat,
                            Path.GetFileName(candidate),
                            junkApplied.Message));
                }

                IReadOnlyList<string> members = EnumerateRestoreGroupMembers(candidate);

                if (members.Count == 0)
                {
                    continue;
                }

                if (!handledGroups.Add(BuildRestoreGroupKey(members)))
                {
                    continue;
                }

                /*
                 * ① 认出底层（魔数；续卷退到同组第 1 卷那一份）。
                 *
                 * ⚠ 2026-10-08 放宽（用户拍板"选项 1"，EEEE 真机 `111(3)\111(3)\111.z0删除2`）：
                 * **格式未知不再当场退出** —— 改由 `TryBuildRestoredName` 逐档判：
                 * ㈠「卷标记段被伪装」那一档只要规范标记自己说得清族（`zNN`/`partN`/`NNN`）就放行；
                 * ㈡「本体后缀被伪装」那一档照旧必须有魔数（否则就是按后缀猜格式，2026-09-29 真机那条死路）。
                 * 判不出来 ⇒ `items.Count == 0` ⇒ 什么都不做（兜底仍在"什么都不做"那一档）。
                 */
                VolumeContentFormat format = ResolveRestoreFormat(members);

                /*
                 * ①b ⛔ **「末尾那段本身脏」那一档必须先过"整组自洽"**（用户 2026-10-04 拍板）：
                 * `444.pa8rt1.rar` 这种卷标记是靠**骨架档**才读出来的名字，而同一段也可能是**基名自己的一段**
                 * （`444.p1art2.part2.rar` 里的 `p1art2`）—— 形状分不开，所以只听**整组**的话：
                 * 同一层里配得出一组自洽的兄弟卷才允许还原，**配不出 ⇒ 整组一个字节都不动**。
                 */
                string selfBase = TryResolvePackageBaseName(Path.GetFileName(candidate), out string restoreBase)
                    ? restoreBase
                    : string.Empty;

                if (members.Any(m => ExtensionHelper.IsPartNumberedMarkBySkeleton(Path.GetFileName(m), out _)) &&
                    !TryConfirmSelfConsistentVolumeGroup(members, candidate, selfBase))
                {
                    log?.Invoke(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.InnerRestoreBlockedFormat,
                            string.Join("；", members.Select(m => Path.GetFileName(m))),
                            StatusText.VolumeRepairNoSiblings));

                    /*
                     * 把**判据输入**原样补一行（用户 2026-10-10 目标单③）：上面那行只说"没做"，
                     * 而"这一片被判成哪一族 / 第几卷、同层到底有哪些候选"才是下一轮排查必须的输入。
                     * 真机 FFFF 那批就卡在这儿：只知道没改名，分不清是尺子不认那个坏名字、还是片真的不在。
                     * ⛔ 只写程序当场算出来的值（判不出就写「判不出」），⛔ 一处判据都不改。
                     */
                    string candidateName = Path.GetFileName(candidate);
                    string? firstInFamily = LegacyVolumeNames.TryGetFirstVolumeName(candidateName);
                    int? volumeIndex = LegacyVolumeNames.TryGetIndex(candidateName);

                    log?.Invoke(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.CriterionInputsRestoreFormat,
                            candidateName,
                            StatusText.VolumeRepairNoSiblings,
                            string.IsNullOrWhiteSpace(firstInFamily) ? "判不出" : firstInFamily,
                            volumeIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出",
                            members.Count,
                            string.Join("；", members.Select(m => Path.GetFileName(m)))));

                    continue;
                }

                // ② 算规范名（只换后缀那一段，基名一个字不动）
                string directory = Path.GetDirectoryName(candidate) ?? string.Empty;
                var items = new List<RestoreRenameItem>();

                foreach (string member in members)
                {
                    string memberName = Path.GetFileName(member);

                    if (!TryBuildRestoredName(memberName, format, out string canonical) ||
                        string.Equals(memberName, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    items.Add(new RestoreRenameItem(member, Path.Combine(directory, canonical), memberName, canonical));
                }

                if (items.Count == 0)
                {
                    continue;
                }

                // ③ 全成或全不成 / 绝不覆盖
                string? blocker = DescribeRestoreBlocker(items);

                if (blocker != null)
                {
                    log?.Invoke(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.InnerRestoreBlockedFormat,
                            string.Join("；", items.Select(i => i.FromName)),
                            blocker));
                    continue;
                }

                // ④ 执行体只有既有那一套（TryApply：只 File.Move、字节数核对、失败倒序改回）
                VolumeNameRepairPlan plan = BuildRestorePlan(items);
                VolumeNameRepairResult applied = TryApply(plan);

                if (!applied.Success)
                {
                    log?.Invoke(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.InnerRestoreBlockedFormat,
                            string.Join("；", items.Select(i => i.FromName)),
                            applied.Message));
                    continue;
                }

                foreach (RestoreRenameItem item in items)
                {
                    renamed[item.From] = item.To;
                }

                log?.Invoke(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.InnerRestoreDoneFormat,
                        plan.Describe()));
            }

            var result = new List<string>(candidatePaths.Count);

            foreach (string candidate in candidatePaths)
            {
                result.Add(renamed.TryGetValue(candidate, out string? updated) ? updated : candidate);
            }

            return result;
        }

        /// <summary>一次还原里的一卷：现在叫什么、该叫什么。</summary>
        internal sealed record RestoreRenameItem(string From, string To, string FromName, string ToName);

        /// <summary>
        /// 这一份候选所属的**一组**（含自己）：同目录 + 卷序认得出 + **包基名逐字相等**
        /// （判据全在既有出口里：卷序 <see cref="LegacyVolumeNames.TryGetIndex"/>、
        /// 基名 = 第二大步那份（<see cref="FileNameHelper.SecondStepBaseName"/>，第一个点之前）。
        ///
        /// <para>为什么必须把兄弟一起收进来：7z / 跨盘 zip 的续卷没有魔数、探测器根本不会把它们当候选，
        /// 而引擎找兄弟卷**只看入口文件旁边那一层** —— 只改第一卷的名字，整组照样打不开。</para>
        ///
        /// <para>没有卷号的那种（本体后缀被伪装，<c>222.zscip</c>）自己就是一组。</para>
        /// </summary>
        /// <summary>
        /// **纯文本清洗：只去掉"卷标记 / 归档后缀那一段"里夹着的非 ASCII 杂质**（用户 2026-10-11 口径）。
        ///
        /// <para>判据三条，一条都不看别的东西（⛔ 不问魔数、⛔ 不问同目录有没有兄弟、⛔ 不问"整组自洽"）：</para>
        /// <list type="number">
        /// <item><description>那一段里**确实夹着非 ASCII 字符**（中文等杂质 —— 那是"插进去的"，不是名字本身）；</description></item>
        /// <item><description>去掉杂质之后**还是本来那个形态**：规范卷段（`partN.rar` / `zNN` / 三位数字）
        /// 或已知归档后缀（`zip` / `rar` / `7z`）—— 这一条是"不猜格式"的底线：我们不是从后缀推格式，
        /// 而是把**被插进字符的那个后缀**还原回它自己；</description></item>
        /// <item><description>基名一个字符都不动（基名里可能有真的中文标题，那绝不能碰）。</description></item>
        /// </list>
        ///
        /// <para>判不出 ⇒ <c>false</c>（调用方什么都不做）。</para>
        /// </summary>
        internal static bool TryStripNonAsciiJunkFromTailSegment(string fileName, out string cleaned)
        {
            cleaned = string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            /*
             * ===== 第三档（2026-10-13 下午，困难批 `1111` 取证）：**纯字母数字的"垃圾" + 上下文** =====
             *
             * <para><b>现场</b>（`run-1111` 逐字）：`111-L2.7z.001` 被伪装成 `111-L2.7z.yvA001`
             * （造包细则第 ② 法：非中文杂质 2–3 个 —— 备好的那 30 个字符里**有字母**）⇒
             * 卷标记容忍尺子的第四档要求"垃圾里至少含一个非字母数字字符、且不许出现 ≥3 个连续字母"
             * ⇒ `yvA` 两条都不满足（纯字母、正好 3 个）⇒ 认不出 ⇒ 链里试开它
             * 逐字 `第 1 层：111-L2.7z.yvA001：解压失败：7-Zip 无法识别或不支持该格式`
             * ⇒ 那一组的入口包永远不叫规范名 ⇒ 整组凑不齐 ⇒ ①红。</para>
             *
             * <para><b>判据（结构 + 上下文，⛔ 不是"看着像"）</b>：`<基名>.<已知归档后缀>.<垃圾><卷号>` ——
             * ① 倒数第二段**就是归档本体后缀**（只认 `7z` / `zip`：只有这两族用三位数字卷号）；
             * ② 末段 = 纯字母数字垃圾（1–4 个字符）+ **三位数字卷号**；
             * ③ 基名逐字纯 ASCII。
             * 三条同时成立 ⇒ 还原成 `<基名>.<后缀>.<卷号>`（⛔ 基名一个字符不动、⛔ 不问魔数、⛔ 不问兄弟、
             * ⛔ 不问整组自洽 —— 与上面两档同一条纪律；执行体仍只有 `TryApply` 一套）。</para>
             *
             * <para>⛔ 为什么"≤4 个字符 + 纯字母数字"够保守：`photo-123` / `backup-002` 那种真名字
             * 要么带非字母数字、要么超过 4 个字符（也与第四档那条"≥3 连续字母"的规矩一致）；
             * 认不出 ⇒ 一个字节都不动。</para>
             */
            if (TryRestoreMarkerSegmentWithAsciiJunk(fileName, out cleaned))
            {
                return true;
            }

            if (IsPureAscii(fileName))
            {
                return false;
            }

            /*
             * 基名 = **第二大步那份**（第一个点之前，唯一出口 `FileNameHelper.SecondStepBaseName`）；
             * 剩下的那一段整段就是"族后缀 + 规范卷标记"。⛔ 这里不自己找边界。
             */
            if (TryCleanTailFromSecondStep(fileName, out cleaned))
            {
                return true;
            }

            /*
             * ===== 第二档（2026-10-13，困难批 `11111111` 取证）：**边界拆不出来时按"纯 ASCII 基名"逐档试** =====
             *
             * <para><b>现场（真机日志逐字）</b>：`222-L2.part删删删1.rar` 在链里一次改名行都没有，
             * 而同一批的 `222-L2.part2.中文rar → 222-L2.part2.rar` 改得好好的。探针实测差别在**第一步**：
             * 名字拆解（基名 + 族后缀段）对前者 = **False**、对后者 = True
             * —— 脏字符把卷标记本身撑坏了（`part删删删1` 里数字已经挨不着 `part`），边界找不出来，
             * 上面那一档于是**整档不做**；产物名字永远是脏的 ⇒ 第二大步拿不到规范卷名 ⇒
             * 那一组的入口包在盘上不叫 `222-L2.part1.rar` ⇒ 判据输入里
             * 「产出它的那一单=判不出」⇒「推不出这一组该拼到哪一层」⇒ 接片一个字节都不动 ⇒
             * 整组永远凑不齐 ⇒ ①「内容物解出来」红。</para>
             *
             * <para><b>判据（用户 2026-10-11 口径：「简单的改后缀，像删除中文字符这么简单的为什么做不了」）</b>：
             * 在**每一个点**处试拆一次，只要
             * ① 基名逐字**纯 ASCII**（⛔ 基名里可能有真的中文标题，绝不动）、
             * ② 剩下那一段去掉非 ASCII 之后**还是本来那个形态**（<see cref="IsRestoredTailForm"/>：
             *    `partN.rar` / `zNN` / 三位数字 / `zip`·`rar`·`7z`）、
             * ③ 去完杂质那一段**点段不许出现空段**（防 `X.中.rar` 被拼成 `X..rar`）、
             * ⇒ 那个名字就是一个候选。**结果唯一**才认（两个拆法都成立 ⇒ 判不出，什么都不做）。</para>
             *
             * <para>⛔ 与上面那一档同一条纪律：只动**卷标记 / 归档后缀那一段**，⛔ 不问魔数、⛔ 不问兄弟、
             * ⛔ 不问"整组自洽"（那是后面几档的事）；执行体仍只有 <see cref="TryApply"/> 一套。</para>
             */
            return false;
        }

        /// <summary>
        /// **纯字母数字垃圾挤在卷号前面**那一档的还原（见调用处那段说明）：
        /// `<基名>.<7z|zip>.<垃圾><三位卷号>` ⇒ `<基名>.<7z|zip>.<三位卷号>`。
        ///
        /// <para>⛔ 三条判据（上下文 + 结构 + 基名纯 ASCII）缺一不可；判不出 ⇒ 空串。</para>
        /// </summary>
        internal static bool TryRestoreMarkerSegmentWithAsciiJunk(string fileName, out string cleaned)
        {
            cleaned = string.Empty;

            string[] parts = fileName.Split('.');

            if (parts.Length < 3)
            {
                return false;
            }

            string extension = parts[^2];

            // ① 上下文：倒数第二段就是归档本体后缀 —— 只有 `7z` / `zip` 两族用三位数字卷号。
            if (!string.Equals(extension, "7z", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, "zip", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string tail = parts[^1];
            int digitStart = tail.Length;

            while (digitStart > 0 && char.IsAsciiDigit(tail[digitStart - 1]))
            {
                digitStart--;
            }

            if (tail.Length - digitStart != 3)
            {
                return false;
            }

            string mark = tail[digitStart..];
            string junk = tail[..digitStart];

            // ② 垃圾 = 1–4 个字符、纯字母数字（含非字母数字的那种由前面那两档管）。
            if (junk.Length == 0 || junk.Length > 4 || !junk.All(char.IsAsciiLetterOrDigit))
            {
                return false;
            }

            // ③ 基名逐字纯 ASCII（与第二档同一条纪律：中文标题绝不碰）。
            string baseName = string.Join('.', parts, 0, parts.Length - 2);

            if (baseName.Length == 0 || !IsPureAscii(baseName))
            {
                return false;
            }

            cleaned = baseName + "." + extension + "." + mark;

            return !string.Equals(cleaned, fileName, StringComparison.OrdinalIgnoreCase);
        }


        /// <summary>
        /// 把"第一个点之后那**一整段**"（族后缀 + 卷标记）里的非 ASCII 杂质去掉：
        /// <c>giu910.7z.001删除</c> → <c>giu910.7z.001</c>、<c>111_.par删t1.ra除r</c> → <c>111_.part1.rar</c>。
        ///
        /// <para>三条缺一不可：① 去完真的变了；② 点段不许拆出空段（防 <c>X..rar</c>）；
        /// ③ 最后一段仍是说得清的形态（规范卷段 / 已知归档后缀，唯一出口 <see cref="IsRestoredTailForm"/>）。
        /// 基名由 <see cref="FileNameHelper.SecondStepBaseName"/> 一处给出，⛔ 这里不自己找边界。</para>
        /// </summary>
        internal static bool TryCleanTailFromSecondStep(string fileName, out string cleaned)
        {
            cleaned = string.Empty;

            string baseName = FileNameHelper.SecondStepBaseName(fileName);

            if (baseName.Length == 0 || fileName.Length <= baseName.Length + 1)
            {
                return false;
            }

            string segment = fileName[(baseName.Length + 1)..];
            string stripped = StripNonAscii(segment);

            if (stripped.Length == 0 || string.Equals(stripped, segment, StringComparison.Ordinal))
            {
                return false;
            }

            if (stripped[0] == '.' || stripped.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }

            int lastDot = stripped.LastIndexOf('.');
            string lastSegment = lastDot >= 0 ? stripped[(lastDot + 1)..] : stripped;

            if (!IsRestoredTailForm(lastSegment))
            {
                return false;
            }

            cleaned = baseName + "." + stripped;

            return !string.Equals(cleaned, fileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把非 ASCII 字符摘掉（只用于"卷标记 / 归档后缀那一段"）。</summary>
        internal static string StripNonAscii(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);

            foreach (char ch in value)
            {
                if (ch <= 0x7F)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        internal static bool IsPureAscii(string value)
        {
            foreach (char ch in value)
            {
                if (ch > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 去掉杂质之后那一段**是不是一个说得清的规范形态**：规范卷段（既有那把尺子）或已知归档后缀。
        /// ⛔ 不认的一律 false —— 宁可一个字符都不改。
        /// </summary>
        internal static bool IsRestoredTailForm(string segment)
        {
            if (IsSegmentFamilySelfDetermined(segment))
            {
                return true;
            }

            return string.Equals(segment, "zip", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "rar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "7z", StringComparison.OrdinalIgnoreCase);
        }

        internal static IReadOnlyList<string> EnumerateRestoreGroupMembers(string candidate)
        {
            var members = new List<string> { candidate };

            string candidateName = Path.GetFileName(candidate);
            string directory = Path.GetDirectoryName(candidate) ?? string.Empty;

            if (directory.Length == 0 ||
                LegacyVolumeNames.TryGetIndex(candidateName) == null ||
                !TryResolvePackageBaseName(candidateName, out string candidateBase))
            {
                return members;
            }

            string[] entries;

            try
            {
                entries = Directory.GetFiles(directory);
            }
            catch
            {
                // 读不了目录 ⇒ 只处理自己（判不出就少做，绝不多做）。
                return members;
            }

            foreach (string entry in entries)
            {
                if (string.Equals(entry, candidate, StringComparison.OrdinalIgnoreCase) ||
                    LegacyVolumeNames.TryGetIndex(Path.GetFileName(entry)) == null ||
                    !TryResolvePackageBaseName(Path.GetFileName(entry), out string siblingBase))
                {
                    continue;
                }

                if (string.Equals(siblingBase, candidateBase, StringComparison.OrdinalIgnoreCase))
                {
                    members.Add(entry);
                }
            }

            return members;
        }

        /// <summary>包基名（**第二大步那份**：<see cref="FileNameHelper.SecondStepBaseName"/>）。</summary>
        internal static bool TryResolvePackageBaseName(string fileName, out string baseName)
        {
            baseName = FileNameHelper.SecondStepBaseName(fileName);

            return baseName.Length > 0;
        }

        /// <summary>同一组只还原一次：键 = 目录 + 第 1 卷的包基名。</summary>
        internal static string BuildRestoreGroupKey(IReadOnlyList<string> members)
        {
            string directory = Path.GetDirectoryName(members[0]) ?? string.Empty;
            string anchor = members
                .OrderBy(member => LegacyVolumeNames.TryGetIndex(Path.GetFileName(member)) ?? int.MaxValue)
                .First();

            TryResolvePackageBaseName(Path.GetFileName(anchor), out string baseName);

            return directory + "|" + baseName;
        }

        /// <summary>
        /// 认出这一组的底层：**只认魔数**。第 1 卷那一份的魔数优先（7z / 跨盘 zip 只有它带魔数）；
        /// 一个魔数都认不出 ⇒ <see cref="VolumeContentFormat.Unknown"/>。
        ///
        /// <para>⚠ 2026-10-08 起 <c>Unknown</c> **不再等于"整组什么都不做"**：调用方还会走
        /// <see cref="TryBuildRestoredName"/> 的㈠档（卷标记段被伪装，规范标记自带族）；
        /// ㈡档（本体后缀被伪装）仍然必须有魔数。</para>
        /// </summary>
        internal static VolumeContentFormat ResolveRestoreFormat(IReadOnlyList<string> members)
        {
            VolumeContentFormat anyRecognized = VolumeContentFormat.Unknown;

            foreach (string member in members)
            {
                VolumeContentFormat format = VolumeContentInference.SniffFormat(member);

                if (format == VolumeContentFormat.Unknown)
                {
                    continue;
                }

                if (LegacyVolumeNames.TryGetIndex(Path.GetFileName(member)) == 1)
                {
                    return format;
                }

                if (anyRecognized == VolumeContentFormat.Unknown)
                {
                    anyRecognized = format;
                }
            }

            return anyRecognized;
        }

        /// <summary>
        /// 算出"规范形态"的文件名：**只换后缀那一段**（替换，⛔ 不是追加），基名一个字不动。
        ///
        /// <para>两条路各自转调一把既有尺子：
        /// ㈠ 卷标记段被伪装（<c>X.part1.rar删除</c> / <c>x.7z.001.txt</c> / <c>111.parst1.racr</c>）
        ///    —— 骨架化，经 <see cref="TrySplitDisguised"/>（基名 = 第二大步那份，段 = 族后缀 + 卷标记）；
        /// ㈡ 本体后缀被伪装（<c>222.zscip</c>）—— 归档体还原 <c>TryRecoverDisguisedArchiveBody</c>。</para>
        ///
        /// <para>⚠ 还原出来的名字必须与**认出来的底层**同族（RAR 的 <c>partN.rar</c> 不能扣到 7z 内容上），
        /// 族对不上就判"不还原" —— 改错名字比不改更糟（改名不可逆）。</para>
        /// </summary>
        internal static bool TryBuildRestoredName(string fileName, VolumeContentFormat format, out string canonical)
        {
            canonical = string.Empty;

            if (TrySplitDisguised(fileName, out string baseName, out string segment) &&
                baseName.Length > 0 &&
                segment.Length > 0 &&
                (format == VolumeContentFormat.Unknown
                    ? IsSegmentFamilySelfDetermined(FamilyMarkerOf(segment))
                    : IsSegmentFamilyOfFormat(FamilyMarkerOf(segment), format)))
            {
                canonical = baseName + "." + segment;
                return true;
            }

            /*
             * ⛔ ㈡ 档（**本体后缀**被伪装，`222.zscip` ⇒ `222.zip`）**必须有魔数**：
             * "该扣哪个后缀"完全依赖"认出来是什么格式"（下一句就问 `ExtensionFor(format)`），
             * 格式未知时猜一个后缀出来正是 2026-09-29 真机把整组改成解不开的那条路。
             * ⇒ 用户 2026-10-08 拍板的放宽**只放宽㈠档**，这一档一个字都不动。
             */
            if (format == VolumeContentFormat.Unknown)
            {
                return false;
            }

            string extension = VolumeContentInference.ExtensionFor(format);
            string[] parts = fileName.Split('.');

            if (extension.Length == 0 || parts.Length < 2)
            {
                return false;
            }

            if (ExtensionHelper.TryRecoverDisguisedArchiveBody(parts[^1], out string recovered, out _) &&
                string.Equals(recovered, extension, StringComparison.OrdinalIgnoreCase))
            {
                canonical = string.Join('.', parts, 0, parts.Length - 1) + "." + recovered;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 把"族后缀 + 卷标记"里的**卷标记**换成这个槽位号（7z / 跨盘 ZIP 的数字族）：
        /// <c>x</c> + <c>7z.001</c> + <c>003</c> ⇒ <c>x.7z.003</c>；<c>z02</c> 那族同样只换最后一段。
        /// 段里没有可替换的标记（<c>part1.rar</c> 那种）⇒ 退回"基名 + .卷号"（与旧实现逐字相同）。
        /// </summary>
        internal static string ReplaceVolumeMarker(string baseName, string segment, string indexText)
        {
            int lastDot = segment.LastIndexOf('.');
            string marker = lastDot >= 0 ? segment[(lastDot + 1)..] : segment;

            bool numericMarker =
                marker.Length == 3
                && ((char.IsAsciiDigit(marker[0]) && char.IsAsciiDigit(marker[1]) && char.IsAsciiDigit(marker[2]))
                    || (marker[0] is 'z' or 'Z' && char.IsAsciiDigit(marker[1]) && char.IsAsciiDigit(marker[2])));

            return numericMarker && lastDot > 0
                ? baseName + "." + segment[..lastDot] + "." + indexText
                : baseName + "." + indexText;
        }

        /// <summary>
        /// 从"族后缀 + 卷标记"那一段里取**卷标记**（判族用）：<c>7z.001</c> ⇒ <c>001</c>、
        /// <c>z02</c> ⇒ <c>z02</c>、<c>part1.rar</c> ⇒ 整段（RAR 的族就写在尾部后缀上）。
        /// </summary>
        internal static string FamilyMarkerOf(string segment) =>
            segment.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                ? segment
                : segment[(segment.LastIndexOf('.') + 1)..];

        /// <summary>规范卷段是不是**该族**的写法（`partN.rar` / `NNN` / `zNN`）—— 族对不上就不还原。</summary>
        internal static bool IsSegmentFamilyOfFormat(string segment, VolumeContentFormat format)
        {
            if (segment.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                return format == VolumeContentFormat.Rar;
            }

            if (segment.Length == 3 && segment[0] is 'z' or 'Z' &&
                char.IsAsciiDigit(segment[1]) && char.IsAsciiDigit(segment[2]))
            {
                return format == VolumeContentFormat.Zip;
            }

            if (segment.Length == 3 && char.IsAsciiDigit(segment[0]) &&
                char.IsAsciiDigit(segment[1]) && char.IsAsciiDigit(segment[2]))
            {
                return format == VolumeContentFormat.SevenZip;
            }

            return false;
        }

        /// <summary>
        /// 规范卷段**自己就说得清族**（⛔ 不需要魔数）：`partN` / <c>.rar</c> 结尾 ⇒ RAR、
        /// <c>zNN</c> ⇒ 跨盘 ZIP、三位数字 ⇒ 7z —— 三档按**形状互斥**，所以"能唯一归类"就等于"族定了"。
        ///
        /// <para>用户 2026-10-08 拍板（EEEE 真机）：7z 续卷是裸切字节流、**本来就没有魔数**，
        /// 首卷又不在这一层 ⇒ 整组嗅不出格式，旧口径于是"一个字节都不动"，
        /// <c>111.z0删除2</c> 永远是脏名。可它的规范标记 <c>z02</c> 自己就写着"我是跨盘 ZIP 的第 2 片"
        /// —— 这一档不靠魔数也站得住，所以格式未知时**只放行㈠档**（改名目标名 = 基名 + 规范标记，
        /// 基名一个字不动、绝不覆盖、全成或全不成照旧）。</para>
        ///
        /// <para>⚠ 与源层那条路**同一口径**：<c>VolumeNameRepairTests.孤立一片的脏卷名_骨架算得出就要能归一</c>
        /// 早就钉住"孤立一片、无魔数也要能出改名计划"；这里只是把递归层对齐过去（⛔ 不是新开一门判据）。</para>
        /// </summary>
        internal static bool IsSegmentFamilySelfDetermined(string segment) =>
            IsSegmentFamilyOfFormat(segment, VolumeContentFormat.Rar) ||
            IsSegmentFamilyOfFormat(segment, VolumeContentFormat.Zip) ||
            IsSegmentFamilyOfFormat(segment, VolumeContentFormat.SevenZip);

        /// <summary>
        /// 全成或全不成的两道闸门：① 目标名一个都不许被占（⛔ 绝不覆盖）；
        /// ② 两份候选还原之后不许撞成同一个名字。任一不过 ⇒ 返回原因（整组一个都不改）。
        /// </summary>
        internal static string? DescribeRestoreBlocker(IReadOnlyList<RestoreRenameItem> items)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RestoreRenameItem item in items)
            {
                if (!targets.Add(item.To))
                {
                    return string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.InnerRestoreCollisionFormat,
                        item.ToName);
                }

                if (File.Exists(item.To))
                {
                    return string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeRepairTargetTakenFormat,
                        item.ToName);
                }
            }

            return null;
        }

        internal static VolumeNameRepairPlan BuildRestorePlan(IReadOnlyList<RestoreRenameItem> items)
        {
            List<RestoreRenameItem> ordered = items
                .OrderBy(i => i.FromName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = ordered[0].From,
                CurrentFileName = ordered[0].FromName,
                SuggestedFileName = ordered[0].ToName,
                TargetPath = ordered[0].To,
                Siblings = ordered.Select(i => i.FromName).ToList(),
                Items = ordered
                    .Select(i => new VolumeRepairItem
                    {
                        CurrentPath = i.From,
                        CurrentFileName = i.FromName,
                        SuggestedFileName = i.ToName,
                        TargetPath = i.To
                    })
                    .ToList()
            };
        }

        // ================================================================ 跨层收卷（2026-10-05 真机）

    }
}