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
using static ArchiveFixer.Extraction.VolumeNameRestorer;

namespace ArchiveFixer.Extraction
{
    internal static partial class VolumeNamePlanner
    {

        /// <summary>
        /// 算出改名计划。任何 IO 意外都落成"不能改 + 原因"，绝不抛。
        /// </summary>
        /// <param name="currentPath">那一卷现在的完整路径。</param>
        /// <param name="fileNamesInDirectory">同目录里的文件名（只要名字，不要路径）。</param>
        public static VolumeNameRepairPlan Plan(string? currentPath, IEnumerable<string?>? fileNamesInDirectory)
        {
            string path = currentPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                return Cannot(path, StatusText.VolumeRepairSourceMissing);
            }

            string fileName;

            try
            {
                if (!File.Exists(path))
                {
                    return Cannot(path, StatusText.VolumeRepairSourceMissing);
                }

                fileName = Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("分卷改名", "降级处理", ex);
                return Cannot(path, string.Format(StatusText.VolumeRepairPlanFailedFormat, ex.Message));
            }

            /*
             * ===== 通用分片族（`X.zip.001`）的名字**本来就是规范的**，⛔ 不许改成跨盘 zip 形状 =====
             * （用户 2026-10-17 现场，新样本 `1111` 逐字：`222-L1.zip.001 → 222-L1.z01；…002 → 222-L1.z02；
             *  …003 → 222-L1.zip` —— 那是 7-Zip `-v` 切出来的**通用分片**，引擎按 `.zip.00N` 打开；
             *  改成 `.z01/.z02/.zip` 那一套（PKZIP 跨盘族的形状，末片叫 `.zip`）之后引擎反而报
             *  「分卷缺失」⇒ 内层整组解不出来 ⇒ ①红。）
             * 判据：名字就是 `<基名>.zip.<三位数字>` ⇒ 一个名字都不改。
             */
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    fileName,
                    @"(?i)\.zip\.\d{3}$"))
            {
                return Cannot(
                    path,
                    "这是通用分片族（.zip.001 这种名字本来就是规范的，引擎按它打开）—— 名字一个字节都不改");
            }

            /*
             * ===== 底层识别优先：**名字与卡上的卷号不一致时，名字路让位** =====
             * （用户 2026-10-17：「一切看底层识别的来」；现场新样本 `1111`：
             *  `222-L2.part34B+.rar` 的名字被读成 part34、而卡上是第 3 卷 —— 名字路若照名字还原成
             *  `222-L2.part34.rar`，整组就变成 34 卷、引擎永远报"缺必要分卷"。
             *  卡判得出卷号 ⇒ 名字路一个名字都不改（留给内容那条路按卡的槽位命名）。
             *  ⛔ 卡判不出（7z 中间卷、读不出来）⇒ 照旧走名字路，与加这条之前逐字相同。）
             */
            try
            {
                /*
                 * ⛔ **内容可证"不是分卷的一员" ⇒ 卷名这一套一个字节都不改**（2026-10-18，唯一出口
                 * <see cref="Detection.VolumePieceFacts.ContentProvesNotAVolumePiece"/>）。
                 *
                 * <para>现场逐字（困难批 `24242424`，`walk` / `names` 探针）：`222-W1-1.mp4-932` 的
                 * 真实内容是**单盘 ZIP**（卡上 `Membership=ProvablyNotVolumeMember`、
                 * `BodyKind=SingleComplete`），而名字尺子把末段 `mp4-932` 的容差档读成"卷号 932"
                 * （`Tolerant(末段「mp4-932」) = True（→932，垃圾「mp4-」）`）⇒ 老写法把它还原成
                 * `222-W1-1.932`（凭空造出一个卷号）⇒ 下游"名字优先"的判据当场把它当后续卷
                 * ⇒ 9 个密码候选只试了 1 个 ⇒ 那一片永远出不来 ⇒ 整组缺第 2 片。</para>
                 *
                 * <para>⛔ 只否证这一种；内容判不出（7z 中间卷等）⇒ 照旧走名字那条路，一个字不放宽。</para>
                 */
                FileNatureCard natureCard = FileNature.Inspect(path);

                if (ArchiveFixer.Detection.VolumePieceFacts.ContentProvesNotAVolumePiece(natureCard))
                {
                    return Cannot(
                        path,
                        "内容可证不是分卷的一员（名字里的卷标记只是伪装尾巴）—— 卷名一个字节都不改");
                }

                int? nameIndex = LegacyVolumeNames.TryGetIndex(fileName);
                FileNatureCard entryCard = natureCard;
                int? cardIndex = entryCard.VolumeNumber;

                if (cardIndex != null && (nameIndex == null || nameIndex.Value != cardIndex.Value))
                {
                    /*
                     * ===== 名字与卡冲突 ⇒ **按卡上的卷号改名**（用户 2026-10-17：底层识别先，名字配合）=====
                     * 不再"一个名字都不改"（那会让整组永远缺 part3）：用法是**拿卡上的族按卡上的槽位**
                     * 拼出规范名（`222-L2.part34B+.rar` + 卡上第 3 卷 ⇒ `222-L2.part3.rar`），
                     * 判据与拼法都只有既有出口 `VolumePiecesFromCards.CanonicalNameForSlot`。
                     * 目标名已被占 / 拼不出来 ⇒ 照旧什么都不做（`Cannot`）。
                     */
                    /*
                     * ⛔ **跨盘 ZIP 的末片：名字里的 1 是入口形状，内容里的号是盘号**（2026-10-18 困难批
                     * `12121212` / `26262626` 运行期逐字）：老写法把 `cardIndex`（3 = 末片）直接当
                     * "名字槽位"交给命名出口 ⇒ `资源222-L2.zip` 被改成 `资源222-L2.z02`
                     * ⇒ 一组里两个 `.z02`、没有 `.zip`（引擎的入口）⇒ 整组缺末片 ⇒ ① ② 双红。
                     * ⇒ 末片一律用**卡上已有的规范名**（`FileNatureCard.CanonicalVolumeName`，
                     * 它由内容那一套算出来 = `X.zip`）；卡给不出才走老命名出口。
                     * ⛔ 判据只读卡，不比名字猜。
                     */
                    string canonical = ArchiveFixer.Detection.VolumePieceFacts.IsSpannedZipTailPiece(entryCard)
                        && entryCard.CanonicalVolumeName.Length > 0
                            ? entryCard.CanonicalVolumeName
                            : ArchiveFixer.Detection.VolumePiecesFromCards.CanonicalNameForSlot(
                                entryCard, cardIndex.Value);

                    if (canonical.Length > 0
                        && !string.Equals(canonical, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        string cardDirectory = Path.GetDirectoryName(path) ?? string.Empty;
                        string cardTarget = Path.Combine(cardDirectory, canonical);

                        if (!File.Exists(cardTarget))
                        {
                            return new VolumeNameRepairPlan
                            {
                                CanRepair = true,
                                CurrentPath = path,
                                CurrentFileName = fileName,
                                SuggestedFileName = canonical,
                                TargetPath = cardTarget,
                                Siblings = Array.Empty<string>(),
                                Items = new[]
                                {
                                    new VolumeRepairItem
                                    {
                                        CurrentPath = path,
                                        CurrentFileName = fileName,
                                        SuggestedFileName = canonical,
                                        TargetPath = cardTarget
                                    }
                                }
                            };
                        }

                        return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, canonical));
                    }

                    return Cannot(
                        path,
                        $"名字说这一卷是第 {nameIndex?.ToString() ?? "判不出"} 卷，"
                        + $"而内容（底层识别）说是第 {cardIndex.Value} 卷 —— "
                        + "按内容算出来的规范名与现在这个名字一样，这里不动");
                }
            }
            catch
            {
                // 卡读不出来 ⇒ 不拦（老口径）。
            }

            /*
             * 「本体是标准名、只有几个续卷的名字被改坏」这一档（用户 2026-10-01 真机 DDD：跨盘 ZIP 分了 4 片，
             * 本体 `222.zip` 干净、三个续卷叫 `222.z0删1` / `222.z除02` / `222.z文03`；7-Zip 报
             * `ERROR = Missing volume : 222.z01` ⇒ 整包解不开、源包四个都不进其余物）。
             *
             * ⛔ 必须排在下面两道门**之前**：那两道门（"名字里得有卷号"、"必须是第 1 卷 / 得找得到兄弟"）
             * 都是为"**本体自己**被改坏"设计的，而这一档里本体本来就是对的 —— 走到下面只会被
             * `FindSiblingVolumes`（**按标准名**找兄弟）判成"没有兄弟"，于是整组一个名字都不改。
             */
            VolumeNameRepairPlan? disguisedVolumes =
                PlanDisguisedVolumesBesideStandardSelf(path, fileName, fileNamesInDirectory);

            if (disguisedVolumes != null)
            {
                return disguisedVolumes;
            }

            /*
             * 「名字里得有卷号、而且必须是第 1 卷」这两条是**这道门的核心**：
             * 名字里没有卷号的文件（普通包）改名只会把它弄坏；不是第一卷的（.002 之类）
             * 改名解决不了"缺第一卷"的问题 —— 那种情况该做的是把它放回原组，不是改它的名字。
             */
            int? index = LegacyVolumeNames.TryGetIndex(fileName);

            if (index == null)
            {
                return Cannot(path, StatusText.VolumeRepairNotAVolumeName);
            }

            /*
             * 先看"整组名字都被缀了垃圾"这一档（2026-09-28 真机：百度网盘给每个分卷名缀「删除」）。
             * ⚠ 必须排在"必须是第 1 卷"与"得有兄弟卷"这两道门**之前** —— 带垃圾的组里，
             * 第 2/3 卷也有名字要改，而且 `RawSplitStreamDetector.FindSiblingVolumes` 认不出它们
             * （它按标准名找兄弟），排后面就永远走不到。
             * 判据只有一条：这一段的卷标记后面粘着垃圾，去掉垃圾就是标准名 —— 只删尾巴、不动卷号。
             */
            VolumeNameRepairPlan? groupPlan = PlanJunkTailGroup(path, fileName, fileNamesInDirectory);

            if (groupPlan != null && groupPlan.CanRepair)
            {
                // 能**一次把整组改干净** ⇒ 走它（那一份计划最完整）。
                return groupPlan;
            }

            /*
             * ⛔ **"整组一起改"拿不出计划时，要接着让"只改这一卷"那条路试**（用户 2026-10-06 拍板）。
             *
             * 现场（真机 CCCC）：`111(3)\111.z0删除2` / `111(4)\111.z0删除3` 这两片各自单独躺在一个目录里
             * ⇒ 上面那一档必然拿不出计划（它要有兄弟卷才成立）⇒ **过去直接 return 掉**
             * ⇒ 下面那条"只把这一卷的名字归一"的路**永远走不到**，那两片就永远带着伪装名。
             * ⇒ 这里把"拿不出计划"与"一次改干净"分开：拿不出 ⇒ 继续往下试单卷那条；
             * ⛔ 只在**它自己确实算得出这个卷的标准名**时才改（判据仍在 `PlanDisguisedRenamedSelf` 里）。
             */
            VolumeNameRepairPlan? singleVolumePlan = PlanDisguisedRenamedSelf(path, fileName, index.Value);

            if (singleVolumePlan != null)
            {
                return singleVolumePlan;
            }

            if (groupPlan != null)
            {
                // 单卷那条也拿不出计划 ⇒ 照旧如实回报"整组那一档"的原因（诊断信息更全）。
                return groupPlan;
            }

            /*
             * ⛔ **「卷标记自己粘着垃圾」的续卷也要能改回标准名**（用户 2026-10-06 拍板，原话
             * 「只修一卷这是你私自弄的，重大危险」「我们攻破伪装不就是要将其改为标准名字吗」）。
             *
             * 现场（真机 CCCC）：`111(3)\111.z0删除2` + `111(4)\111.z0删除3` 是跨盘 ZIP 的续片，
             * 容忍解析**早就认得出**它们是 `111.z02` / `111.z03`（`ExtensionHelper.cs:585` 的注释里
             * 逐字就是这个例子），可改名这一层过去只放行"第 1 卷" ⇒ 这两片**从来没被改成标准名**，
             * 一直带着伪装名躺在用户目录里（列表上那两行"没改名"就是它）。
             *
             * 老的那道"必须是第 1 卷"是为**换归档格式后缀**那条路写的顾虑（改格式后缀解决不了缺首卷）；
             * 而这里要做的是**只把卷标记那一段归一**（`z0删除2` → `z02`）：基名不动、族不动、
             * 卷序不动、可逆、绝不覆盖 ⇒ 与"缺不缺第 1 卷"毫无关系。
             *
             * 判据（三条同时成立才改，一条不成立就什么都不做）：
             * ① 容忍解析/骨架命中的**规范卷标记**算得出来（`TrySplitDisguised`）；
             * ② 推出来的名字**不是**它现在的名字（名字本来就标准 ⇒ 没有可修的东西）；
             * ③ 推出来的名字**自己是规范卷名**（`IsCanonicalVolumeName` —— ⛔ 不许把脏名写回去）；
             * 另外照旧：目标名**被占 ⇒ 不改**（下面 `File.Exists` 那一关）。
             * ⛔ 不需要兄弟卷在场、也不动别的卷：一件是一件，各自可证。
             *
             * ⚠ 这一调**必须排在 `PlanJunkTailGroup` 那个早退之前**（实测踩到）：那一档在"这一卷自己
             * 单独躺在一个目录里"时拿不出计划，老写法直接 `return` 掉它 ⇒ 这条路永远走不到。
             * 现在那里改成"拿不出计划就继续往下"，这里就是它继续下来的第一站。
             */
            VolumeNameRepairPlan? singleVolume = PlanDisguisedRenamedSelf(path, fileName, index.Value);

            if (singleVolume != null)
            {
                return singleVolume;
            }

            if (index.Value != 1)
            {
                return Cannot(path, StatusText.VolumeRepairNotFirstVolume);
            }

            IReadOnlyList<string> siblings = RawSplitStreamDetector.FindSiblingVolumes(fileNamesInDirectory, path);

            if (siblings.Count == 0)
            {
                return Cannot(path, StatusText.VolumeRepairNoSiblings);
            }

            string suggested = RawSplitStreamDetector.SuggestStandardFirstName(siblings);

            if (string.IsNullOrWhiteSpace(suggested))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            if (string.Equals(suggested, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return Cannot(path, StatusText.VolumeRepairAlreadyStandard);
            }

            /*
             * ⛔ **第二道闸门（用户 2026-10-04 点名）：建议名自己必须是规范名。**
             *
             * 现场（2026-10-04 只读探针实测，同一组 `x.7z.001` + `x.7z.002.txt`）：
             * 建议名是从**兄弟卷的名字**推出来的（`TryGetFirstVolumeName('x.7z.002.txt')` ⇒ `x.7z.001.txt`）
             * —— **脏尾巴被原样继承**；而下面那道"兄弟基名逐字相等"的闸门用的又是**剥标记档**
             * （跨段形状两边都退化成 `x`）⇒ 放行 ⇒ 计划把**干净的名字改成脏名字**（方向反了）。
             * 入口换成那个脏兄弟时反而给出正确方向 —— 同一个判据在两种入口下给出相反结论。
             *
             * 判据只转调既有出口（见 <see cref="IsCanonicalVolumeName"/>）：`x.7z.001` / `x.zip` / `X.part1.rar`
             * 算规范；`x.7z.001.txt`（另起一段的尾巴）、`x.7z.001删除`（粘着垃圾）、`x.z0删1` 一律不算
             * ⇒ **整份计划不成立**（判不出就什么都不做，兜底落在"不改名"那一档）。
             */
            if (!IsCanonicalVolumeName(suggested))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            /*
             * ⛔ 第二道闸门：**这份计划只改第一卷**（Items 只有一项）。
             *
             * 如果同目录里的兄弟卷自己也带着垃圾（它们的基名与建议名的基名不一致），那改完第一卷之后
             * 7-Zip 会按**新的**基名去找 `.002` / `.003` —— 找不到就整组打不开：
             * 「改一个」比「一个都不改」更糟（2026-09-29 复查逮到：`全坏.7z(删掉.001` 那一组两卷都带垃圾，
             * 老代码会把第一卷改成 `全坏.7z.001`，剩下两卷还叫 `(删掉` 那套名字）。
             *
             * 判据只读事实：兄弟卷的**名字前缀**与建议名的名字前缀**必须逐字相等**
             * （<see cref="ExtensionHelper.ResolveNamePrefix"/>：卷标记之前那一段，保留族后缀段）。
             * ⚠ 2026-10-23：这里**不能**拿基名（第一个点之前）比 —— 那只回答"是不是同一个包"，
             * 回答不了"兄弟卷的族后缀是不是同一套"：`x.ra31415926535r` 与 `x.r00` 的基名都是 `x`，
             * 而它们不是一族（旧口径用前缀比，正是为了拦这一格）。
             * 不等 = 它们也得跟着改，而这条路改不到它们 → 退到「什么都不做」。
             * 「能一次把整组改干净」的那一档是 <see cref="PlanJunkTailGroup"/>，它排在这条路**前面** ——
             * 走到这里说明它认不出这个形状（垃圾塞在压缩后缀里，而不是粘在卷标记上）。
             */
            string suggestedStem = ExtensionHelper.ResolveNamePrefix(suggested);

            if (siblings.Any(sibling => !string.Equals(
                ExtensionHelper.ResolveNamePrefix(sibling),
                suggestedStem,
                StringComparison.OrdinalIgnoreCase)))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string targetPath = Path.Combine(directory, suggested);

            if (File.Exists(targetPath))
            {
                return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, suggested));
            }

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = path,
                CurrentFileName = fileName,
                SuggestedFileName = suggested,
                TargetPath = targetPath,
                Siblings = siblings,
                Items = new[]
                {
                    new VolumeRepairItem
                    {
                        CurrentPath = path,
                        CurrentFileName = fileName,
                        SuggestedFileName = suggested,
                        TargetPath = targetPath
                    }
                }
            };
        }
    }
}
