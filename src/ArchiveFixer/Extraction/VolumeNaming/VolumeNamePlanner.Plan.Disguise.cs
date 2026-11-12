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
        /// 「**本体是标准名，只有几个续卷的名字被改坏**」这一档的计划（用户 2026-10-01 真机 DDD）。
        ///
        /// <para>现场：跨盘 ZIP 分了 4 片 —— 本体 `222.zip`（标准名，7-Zip 打开这一组的入口），
        /// 三片续卷叫 `222.z0删1` / `222.z除02` / `222.z文03`（字被塞进卷标记里）。7-Zip 找 `222.z01` 找不到 ⇒
        /// 「分卷压缩包缺少必要分卷」⇒ 整包解不开。用户原话：「我这次将 zip 多分了几个卷你就弄不了了」。</para>
        ///
        /// <para>判据（只用名字，不读内容、不试开）：</para>
        /// <list type="number">
        /// <item><description>自己**不是**"被伪装的卷名"（那种形状归 <see cref="PlanJunkTailGroup"/> 管，它在另一条路上）；</description></item>
        /// <item><description>自己的基名（剥卷标记 + 剥已知归档后缀，唯一出口 <see cref="OutputPlacement.ResolveArchiveBaseName"/>）
        /// 与某个同目录兄弟"去杂质之后的基名"**逐字相等**；</description></item>
        /// <item><description>这些兄弟去杂质之后得到的**规范卷标记互不相同**（两个文件还原成同一个卷名 ⇒ 有歧义 ⇒ 整组不动）；</description></item>
        /// <item><description>每一卷的目标名**都没被占用**（⛔ 绝不覆盖；占了一个 ⇒ 整组不动）。</description></item>
        /// </list>
        ///
        /// <para>⛔ 与其余改名路同一条纪律：只改名字、内容一个字节不动；全成或全不成（执行体仍是
        /// <see cref="TryApply"/>，中途失败倒序改回原名）。</para>
        /// </summary>
        /// <summary>
        /// **只把这一个卷名归一**（"卷标记段自己粘着垃圾"的续卷）：`X.z0删除2` → `X.z02`、`X.7z.00删2` → `X.7z.002`。
        ///
        /// <para>用户 2026-10-06 拍板：容忍解析/骨架命中已经认得出规范卷标记，就该**任何一卷**都改回标准名
        /// （⛔ 不再是"只有第 1 卷能修"—— 那条是我私自收窄的）。判据三条同时成立才给计划：
        /// ① 规范卷标记算得出来；② 推出来的名字与现在的名字不同；③ 推出来的名字自己是规范卷名。
        /// 目标名被占 ⇒ 什么都不做（⛔ 绝不覆盖）。⛔ 不要求兄弟卷在场：只动这一个名字。</para>
        /// </summary>
        private static VolumeNameRepairPlan? PlanDisguisedRenamedSelf(string path, string fileName, int index)
        {
            _ = index;

            if (!TrySplitDisguised(fileName, out string baseName, out string canonicalMark))
            {
                return null;
            }

            if (baseName.Length == 0 || canonicalMark.Length == 0)
            {
                return null;
            }

            /*
             * 推出来的名字：**基名逐字保留**（含归档后缀段）+ 规范卷标记。
             * ⚠ 两族同一个拼法都对：`X.zip` / `X.rar` 的基名不含卷标记；`X.7z.001` 这一族的
             * `TrySplitDisguised` 给出的基名已经含 `.7z`。
             */
            string suggested = baseName + "." + canonicalMark;

            if (string.Equals(suggested, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return null;   // 名字本来就标准 ⇒ 没有可修的东西
            }

            if (!IsCanonicalVolumeName(suggested))
            {
                return null;   // ⛔ 绝不把脏名写回去（"改名方向"那把尺子）
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
                Siblings = Array.Empty<string>(),
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

        private static VolumeNameRepairPlan? PlanDisguisedVolumesBesideStandardSelf(
            string path,
            string fileName,
            IEnumerable<string?>? fileNamesInDirectory)
        {
            // ① 自己带垃圾 ⇒ 不是这一档（那是 PlanJunkTailGroup 的形状）。
            if (TrySplitDisguised(fileName, out _, out _))
            {
                return null;
            }

            /*
             * ⛔ 2026-10-04：**同档比较**（"哪一档比就跟同一档比"）。
             *
             * ⚠ 2026-10-23（用户口径：基名 = 第一个点之前、只留两份）。这一档比的是**名字前缀**
             * （<see cref="VolumeGroupDetector.TryGetBaseName"/>，= 写出来的规范卷名往它后面接卷标记的那一段：
             * 本体 <c>x.zip</c> → <c>x</c>、<c>x.7z.001</c> → <c>x.7z</c>）——
             * ⛔ 不是基名：`x.001` 与 `x.7z.002.txt` 的基名都是 `x`，可它们**不是同一个包**
             * （族后缀段不同），拿基名比会把它们并成一组、把干净名改成脏族名。
             */
            string selfBase = VolumeGroupDetector.TryGetBaseName(fileName);

            if (selfBase.Length == 0)
            {
                return null;
            }

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            var items = new List<VolumeRepairItem>();
            var marks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? sibling in fileNamesInDirectory ?? Array.Empty<string?>())
            {
                if (string.IsNullOrWhiteSpace(sibling))
                {
                    continue;
                }

                string siblingPrefix = VolumeGroupDetector.TryGetBaseName(sibling);

                if (!TrySplitDisguised(sibling, out _, out string siblingMark)
                    || siblingPrefix.Length == 0
                    || !string.Equals(siblingPrefix, selfBase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(sibling, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 两个文件去杂质之后是同一个卷名 ⇒ 推不出"谁是谁"，整组不动（判不出就不改）。
                if (!marks.Add(siblingMark))
                {
                    return Cannot(path, StatusText.VolumeRepairNoSuggestion);
                }

                string targetName = selfBase + "." + FamilyMarkerOf(siblingMark);

                if (File.Exists(Path.Combine(directory, targetName)))
                {
                    return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, targetName));
                }

                items.Add(new VolumeRepairItem
                {
                    CurrentPath = Path.Combine(directory, sibling),
                    CurrentFileName = sibling,
                    SuggestedFileName = targetName,
                    TargetPath = Path.Combine(directory, targetName)
                });
            }

            if (items.Count == 0)
            {
                return null;
            }

            /*
             * 第一卷先改（`TryApply` 按 CurrentPath/TargetPath 起手，再走 Items 里剩下的）。
             * ⚠ CurrentPath 必须指向**真要改的那一卷**，不能指向调用方手上那个已经标准名的本体：
             * TryApply 见到"源 == 目标"会直接判 AlreadyStandard 并把整份计划拒掉。
             */
            List<VolumeRepairItem> ordered = items
                .OrderBy(i => i.CurrentFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            /*
             * ⛔ **兄弟卷的名字是靠"猜"出来的 ⇒ 必须再过「整组自洽」**（用户 2026-10-04 第六轮：
             * 「兜底仍要有"敢不敢改"的证据 …… 判不出 ⇒ 什么都不做」）。
             *
             * 这一档（本体/入口是标准名、脏的是兄弟卷）原先只有"基名对得上"一条证据；而
             * `set.7aaaaz.002` 这种**后缀段靠容错/骨架档猜出来**的名字，光凭基名吻合不足以证明
             * "这一堆文件真是一组卷"—— 判据与 `PlanJunkTailGroup` 那道**完全同一份**
             * （<see cref="IsGuessedVolumeName"/> + <see cref="TryConfirmSelfConsistentVolumeGroup"/>），
             * ⛔ 不新造第二套；⛔ 逐字干净的兄弟卷（`x.7z.002` / `x.7z.002.txt`）照旧不受影响。
             */
            if (ordered.Any(i => IsGuessedVolumeName(i.CurrentFileName)) &&
                !TryConfirmSelfConsistentVolumeGroup(
                    (fileNamesInDirectory ?? Array.Empty<string?>())
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => Path.Combine(directory, n!)),
                    path,
                    selfBase))
            {
                return Cannot(path, StatusText.VolumeRepairNoSiblings);
            }

            VolumeRepairItem first = ordered[0];

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = first.CurrentPath,
                CurrentFileName = first.CurrentFileName,
                SuggestedFileName = first.SuggestedFileName,
                TargetPath = first.TargetPath,
                Siblings = ordered.Select(i => i.CurrentFileName).ToList(),
                Items = ordered
            };
        }

        /// <summary>
        /// 「整组名字的卷号后面都粘着垃圾」这一档的计划：<c>giu910.7z.001删除</c> →
        /// <c>giu910.7z.001</c>、<c>.002删除</c> → <c>.002</c>……一次把整组改回标准名。
        ///
        /// <para>⛔ 只删尾巴、**绝不动卷号**；任何一卷的目标名已被占用 → 整组不改（宁可不做，也不覆盖）；
        /// 只有一卷需要改时也算这一档（用户点一下就好）。返回 null 表示"不是这一档"，交给老的
        /// 「第一卷名字被改坏」那条路。</para>
        /// </summary>
        private static VolumeNameRepairPlan? PlanJunkTailGroup(
            string path,
            string fileName,
            IEnumerable<string?>? fileNamesInDirectory)
        {
            /*
             * 跨盘 zip 例外（用户 2026-09-29 真样本）：这一族的**整组名字只能靠内容定** ——
             * 末片叫 `.zip`（7-Zip 打开这一组的入口），之前的片叫 `.z01`/`.z02`……，
             * 而"哪一片才是末片"名字里根本没有（真样本第一片 `222.z0删除1`、末片 `222.z1111ip`）。
             * 按名字把每片还原成自己的卷标记只会得到 `222.z01` + `222.z11` ——
             * 那是一个 7-Zip 永远打不开的组（改错名字比不改更糟）。
             *
             * 所以：内容是**跨盘 zip 的成员**时，这条名字路让位给 PlanByContentAsync
             * （调用方在"推不出标准名"时会接着走内容那条路，判据与执行体仍只有一份）。
             *
             * ⚠ 但"让位"必须看**内容路真能不能拼出组**（2026-09-29 复核补的那半条）：
             * 7-Zip 自己造的 `-tzip -v` 分卷 zip 长这样 —— 第 1 片开头是本地文件头、后面几片是从数据中间切开的，
             * 而**末片的 EOCD 写的是"盘号 0 / 总盘数 0"**（7-Zip 就是这么写的，它不是 PKZIP 那套跨盘 EOCD）。
             * 内容路因此认不出它（末片自述"单盘"），可名字路才是它的正路：`enc.zip.001删除` 里
             * 卷标记 `001` 明明白白，去掉垃圾就是 7-Zip 认的标准名。
             * 老写法只看"手上这一片像不像 zip 成员"就让位 → 内容路拼不出组 → **两边都不动**，
             * 接着 7-Zip 打不开这一组、任务报「密码错误」（实测踩到）。
             * 判据：**目录里存在一片自述"带盘号的跨盘 zip 末片"**（`Number != null`）才让位。
             */
            if (IsDisguisedZipMember(path) && HasSpannedZipTailInDirectory(path, fileNamesInDirectory))
            {
                return null;
            }

            if (!TrySplitDisguised(fileName, out string baseName, out string _))
            {
                return null;
            }
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            var items = new List<VolumeRepairItem>();

            foreach (string? sibling in fileNamesInDirectory ?? Array.Empty<string?>())
            {
                if (string.IsNullOrWhiteSpace(sibling))
                {
                    continue;
                }

                // 同一组：同一个基名 + 同一种伪装形状（都带点段尾巴，或都不带）
                if (!TrySplitDisguised(sibling, out string siblingBase, out string siblingMark) ||
                    !string.Equals(siblingBase, baseName, StringComparison.OrdinalIgnoreCase) ||
                    !LooksLikeSameFamilyShape(fileName, sibling))
                {
                    continue;
                }

                string targetName = siblingBase + "." + siblingMark;

                if (string.Equals(targetName, sibling, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string current = Path.Combine(directory, sibling);
                string target = Path.Combine(directory, targetName);

                if (File.Exists(target))
                {
                    return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, targetName));
                }

                items.Add(new VolumeRepairItem
                {
                    CurrentPath = current,
                    CurrentFileName = sibling,
                    SuggestedFileName = targetName,
                    TargetPath = target
                });
            }

            if (items.Count == 0)
            {
                return null;
            }

            // 自己必须在里面（调用方给的这一卷就是要修的那一卷）
            VolumeRepairItem self = items.FirstOrDefault(
                i => string.Equals(i.CurrentFileName, fileName, StringComparison.OrdinalIgnoreCase))!;

            if (self == null)
            {
                return null;
            }

            var ordered = items
                .OrderBy(i => i.CurrentFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            /*
             * ⛔ **名字要"重建"才读得出来的那一档必须先过"整组自洽"**（用户 2026-10-04 第四轮起、第六轮扩面）：
             *
             * ① partN 骨架那一档（`444.pa8rt1.rar`）：同一段也可能是**基名自己的一段**
             *    （`444.p1art2.part2.rar` 里的 `p1art2`）—— 形状分不开；
             * ② **卷标记段要重建的数字族**（`333.7z.0a0b1` ⇒ `001`、`222.z0删1` ⇒ `z01`）：
             *    孤立一个也会被形状判据说成"第 1 卷"；
             * ③ **归档后缀段要重建的**（`set.7aaaaz.002` ⇒ `set.7z.002`）：后缀段的真实身份同样是猜的。
             *
             * ⇒ 三者只听**整组**的话：同一目录里配得出一组自洽的兄弟卷（基名逐字相同 + 卷标记连续 +
             * 除末片外等大）才允许改；**配不出 ⇒ 一个字都不改**（判不出 ⇒ 什么都不做）。
             * ⛔ 判据只有 <see cref="IsGuessedVolumeName"/> 一处（转调两个既有出口），
             * ⛔ 逐字干净的名字（`x.7z.001` / `x.7z.002.txt`）与**「逐字标记 + 粘着的垃圾」**
             * （`giu910.7z.001删除`，老口径）都不进这道闸门，一个字节的老口径都不动。
             */
            if (ordered.Any(i => IsGuessedVolumeName(i.CurrentFileName)) &&
                !TryConfirmSelfConsistentVolumeGroup(
                    ordered
                        .Select(i => i.CurrentPath)
                        .Concat((fileNamesInDirectory ?? Array.Empty<string?>())
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => Path.Combine(directory, n!))),
                    path,
                    baseName))
            {
                return Cannot(path, StatusText.VolumeRepairNoSiblings);
            }

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = self.CurrentPath,
                CurrentFileName = self.CurrentFileName,
                SuggestedFileName = self.SuggestedFileName,
                TargetPath = self.TargetPath,
                Siblings = ordered.Select(i => i.CurrentFileName).ToList(),
                Items = ordered
            };
        }

        /// <summary>
        /// 内容是**跨盘 zip 的成员**（判据只在 <see cref="Detection.VolumeNumberFromContent"/> 那一处，
        /// 这里只问结论）。⛔ 单盘 zip（EOCD 盘号 0）不算 —— 那种包的名字本来就该由别的路管。
        /// </summary>
        private static bool IsDisguisedZipMember(string path)
        {
            Detection.VolumeNumberReading reading = Detection.VolumeNumberFromContent.Read(path);

            return reading.Format == Detection.VolumeContentFormat.Zip && reading.IsVolumeMember;
        }

        /// <summary>
        /// 同目录里有没有**自述带盘号的跨盘 zip 末片** —— 有它，内容级那条路才真的拼得出整组
        /// （名字路这时必须让位，否则会把 `222.z0删除1` + `222.z1111ip` 还原成 7-Zip 永远打不开的
        /// `222.z01` + `222.z11`）。
        ///
        /// <para>⛔ 反过来不成立：7-Zip 自己造的 `-tzip -v` 分卷 zip，末片 EOCD 写的是"盘号 0"，
        /// 内容级认不出来 —— 那种组必须留给名字路（卷标记就在名字里）。判据只问
        /// <see cref="Detection.VolumeNumberFromContent"/> 的结论，这里不另写一套。</para>
        /// </summary>
        public static bool HasSpannedZipTailNearby(string? anyFileInDirectory) =>
            HasSpannedZipTailInDirectory(
                anyFileInDirectory ?? string.Empty,
                EnumerateFileNamesInDirectory(anyFileInDirectory));

        private static bool HasSpannedZipTailInDirectory(string path, IEnumerable<string?>? fileNamesInDirectory)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;

            foreach (string? name in fileNamesInDirectory ?? Array.Empty<string?>())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                Detection.VolumeNumberReading reading = Detection.VolumeNumberFromContent.Read(
                    Path.Combine(directory, name));

                if (reading.Format == Detection.VolumeContentFormat.Zip
                    && reading.IsVolumeMember
                    && reading.Number != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 把"卷号段 + 粘着的垃圾"拆出来：<c>giu910.7z.001删除</c> → 基名 <c>giu910.7z</c>、垃圾 <c>删除</c>。
        /// 判据转调 <see cref="ExtensionHelper.TrySplitVolumeSegment"/>（只此一处）。
        /// </summary>
        /// <summary>
        /// 两个名字是不是"同一种伪装形状"：都带点段尾巴（<c>001.txt</c>）或都不带（<c>001删除</c>）。
        /// 不这么分，<c>x.7z.001</c> 与 <c>x.7z.002.txt</c> 会被当成同一组的两卷，改出来一半带尾巴一半不带。
        /// </summary>
        private static bool LooksLikeSameFamilyShape(string a, string b) =>
            HasDotTailSegment(a) == HasDotTailSegment(b);

        private static bool HasDotTailSegment(string fileName)
        {
            string[] parts = fileName.Split('.');

            if (parts.Length < 3)
            {
                return false;
            }

            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^1], out _, out _))
            {
                return false;
            }

            return ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^2], out _, out _);
        }

        /// <summary>
        /// 把"被伪装的卷名"拆开：<c>giu910.7z.001删除</c> → 基名 <c>giu910</c>、那一段 <c>7z.001</c>；
        /// <c>444.p1art2.part2.ra3r</c> → 基名 <c>444</c>、那一段 <c>p1art2.part2.rar</c>；
        /// <c>y.z0删除3</c> → 基名 <c>y</c>、那一段 <c>z03</c>。
        ///
        /// <para>⚠ 2026-10-23（用户口径）：**基名只有一份** = <see cref="FileNameHelper.SecondStepBaseName"/>
        /// （第一个点之前）；这里只剩"规范卷名重建"（族后缀 + 卷标记），
        /// <c>baseName + "." + canonicalSegment</c> 与旧实现拼出的目标名**逐字相同**。</para>
        /// </summary>
        internal static bool TrySplitDisguised(string fileName, out string baseName, out string canonicalSegment)
        {
            baseName = string.Empty;
            canonicalSegment = string.Empty;

            if (!TryResolveCanonicalVolumeName(fileName, out string canonicalName))
            {
                return false;
            }

            /*
             * 基名 = **第二大步那份**（第一个点之前，唯一出口 `FileNameHelper.SecondStepBaseName`）；
             * 剩下的那一段 = 族后缀 + 规范卷标记（`x` + `7z.001` / `444` + `part1.rar`）。
             * `baseName + "." + canonicalSegment` 与旧实现拼出来的**目标名逐字相同** ——
             * 这一步只是把"哪一段算基名"换成用户口径，改名结果一个字符都没动。
             */
            baseName = FileNameHelper.SecondStepBaseName(canonicalName);

            if (baseName.Length == 0 || canonicalName.Length <= baseName.Length + 1)
            {
                return false;
            }

            canonicalSegment = canonicalName[(baseName.Length + 1)..];

            return canonicalSegment.Length > 0;
        }

        /// <summary>
        /// 拼出"规范卷名"（<c>giu910.7z.001</c> / <c>444.p1art2.part1.rar</c> / <c>y.z03</c>）：
        /// 这是**改名目标的重建**，不是基名算法 —— 基名由 <see cref="FileNameHelper.SecondStepBaseName"/>
        /// 一处给出（旧实现把这两件事混在一个"档"里）。
        ///
        /// <para>原在 <c>FileNameHelper.TryResolveDisguisedVolume</c>（唯一基名出口的 <c>DisguisedVolume</c> 档），
        /// 2026-10-23 按用户口径拆开：基名部分删掉、只留"规范卷名重建"在这里；
        /// 三道闸门（⓪ 只接尾巴真脏 / ① 末段自己带垃圾 / ② 标记后面还挂着点段）一个字没改。</para>
        /// </summary>
        private static bool TryResolveCanonicalVolumeName(string fileName, out string canonicalName)
        {
            canonicalName = string.Empty;

            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            /*
             * ⓪ <基名>.partN.rar（**卷标记与 rar 尾巴都可以粘垃圾**）：目标名必须**连 `.rar` 一起**保留。
             *
             * ⛔ 老写法把它当"标记后面挂着别的点段"处理，只把卷标记接回去 ⇒ 目标名算成 `X.part1`，
             * 等于把 RAR 的族后缀吃掉：改完 7-Zip / UnRAR 更打不开这一组（改名是**不可逆**动作，
             * 比"一个都不改"更糟）。判据与归组 / 包基名转调**同一份**
             * <see cref="ExtensionHelper.TrySplitPartNumberedVolume"/>。
             */
            if (ExtensionHelper.TrySplitPartNumberedVolume(
                    fileName,
                    out string partBase,
                    out string partMark,
                    out _,
                    out string partTail,
                    out _)
                && (!partTail.Equals("rar", StringComparison.OrdinalIgnoreCase)
                    || ExtensionHelper.IsPartNumberedMarkBySkeleton(fileName, out _)))
            {
                canonicalName = partBase + "." + partMark + ".rar";

                return canonicalName.Length > 0;
            }

            // ① 最后一段自己就是"带垃圾的卷标记"（001删除 / 删除001 / z0删除3）
            if (!ExtensionHelper.IsVolumePartExtension("." + parts[^1]) &&
                ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^1], out string mark, out _))
            {
                canonicalName = ExtensionHelper.NormalizeFamilySegment(string.Join('.', parts, 0, parts.Length - 1))
                                + "." + mark;

                return canonicalName.Length > 1;
            }

            // ② 卷标记后面还挂着别的点段（x.7z.001.txt）
            //    ⚠ 别去查卷标记自己是不是"已知压缩后缀" —— `.001` 本身就在那份名单里，
            //      拿它当闸门会把 `.001.txt` 全挡掉（实测踩到过）。
            for (int i = parts.Length - 2; i >= 1; i--)
            {
                if (!ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[i], out string innerMark, out _))
                {
                    continue;
                }

                bool onlyPlainTails = true;

                for (int j = i + 1; j < parts.Length; j++)
                {
                    if (ExtensionHelper.IsKnownArchiveExtension("." + parts[j]))
                    {
                        onlyPlainTails = false;
                        break;
                    }
                }

                if (!onlyPlainTails)
                {
                    continue;
                }

                canonicalName = ExtensionHelper.NormalizeFamilySegment(string.Join('.', parts, 0, i))
                                + "." + innerMark;

                return canonicalName.Length > 1;
            }

            /*
             * ③ **末段逐字就是合法卷标记，脏的是它前面那一段"归档后缀段"**
             *    （`set.7aaaaz.002` / `333.78a8fuaz.003` / `set.7_______z.002`）。
             *    判据只转调既有那把尺子 `TryRecoverDisguisedArchiveBody`，只把**那一段**换成它。
             */
            if (parts.Length >= 3 &&
                parts[0].Length > 0 &&
                ExtensionHelper.IsVolumePartExtension("." + parts[^1]) &&
                !ExtensionHelper.IsKnownArchiveExtension("." + parts[^2]) &&
                ExtensionHelper.TryRecoverDisguisedArchiveBody(parts[^2], out string recoveredExtension, out _))
            {
                canonicalName = string.Join('.', parts, 0, parts.Length - 2)
                                + "." + recoveredExtension
                                + "." + parts[^1];

                return true;
            }

            return false;
        }


        // ══════════════ 「还原」工序的**递归层挂点**（方案 §2.1 挂点②，用户 2026-10-03）══════════════
        //
        // 顺序不可颠倒：**① 按魔数认出底层 → ② 还原名字 → ③ 才回到第 1 层用该族专属证据重判一次**。
        // 用户原话：「我们首先第一步就是识别底层文件找出伪装文件，然后还原，再接着匹配」。
        //
        // 为什么要有这一步：包**里面**解出来的那一层，过去没有任何一步先擦伪装尾巴
        // （`RecursiveExtractor` 对 `VolumeNameRepair` / `RenameService` 零引用）——
        // 引擎按标准名去找兄弟卷，找不到就只报「分卷缺失」，那 12 GiB 的内容永远出不来
        // （AGENTS.md §11.4 §51 的现场就是它）。
        //
        // ⛔ 铁律（一条都不许放宽）：
        //   ① 只对**我们自己产出的内层包副本**做（调用方传进来的必须是这一层的产物目录里的文件）；
        //      用户源目录那一档归批首的「修正后缀」管（`RenameService.BuildFixByDetectedFormatFileName`）；
        //   ② 只改名、**全成或全不成**、**绝不覆盖**（任何一份的目标名被占 ⇒ 整组一个名字都不改）；
        //   ③ 执行体只有 <see cref="TryApply"/> 一套（只 `File.Move`，改完核对"新名在、旧名没了、字节数不差"，
        //      中途失败倒序改回原名）—— ⛔ 这里不再写第二套改名；
        //   ④ 判据只转调既有那两把尺子：**骨架化**（经唯一基名出口的 `DisguisedVolume` 档）与
        //      **归档体还原**（`ExtensionHelper.TryRecoverDisguisedArchiveBody`），⛔ 不新造第三把；
        //   ⑤ 认出底层**只认魔数**（`VolumeContentInference.SniffFormat`）。⚠ 2026-10-08 放宽一处（用户拍板）：
        //      **格式未知时仍允许㈠档**（卷标记段被伪装 —— 规范标记 `zNN`/`partN`/`NNN` 自己说得清族）；
        //      ㈡档（本体后缀被伪装）照旧必须有魔数。两档都判不出 ⇒ 什么都不做。

        /// <summary>
        /// 递归层内的「还原」：把这一层里内层包副本的**伪装后缀换成规范形态**（替换，⛔ 不是追加；
        /// 基名一个字不动），然后由调用方**回到第 1 层重判一次**（那一步就是方案说的"回环"）。
        ///
        /// <para>⚠ 7z / 跨盘 zip 的**续卷没有魔数**（只有第 1 卷带魔数），所以"认出底层"这一步
        /// 按组做：自己认不出时退到**同组第 1 卷**那一份的魔数 —— 那仍然是魔数证据，只是取自锚点那一卷。
        /// 两处都认不出 ⇒ 整组什么都不做。</para>
        /// </summary>
        /// <param name="candidatePaths">这一层产物目录里已被认成归档的候选（我们自己解出来的副本）。</param>
        /// <param name="log">日志回调 (级别, 文案)；为空则不写日志。</param>
        /// <returns>还原之后的路径清单（顺序与数量与入参完全一致；没还原的项原样返回）。</returns>
        /// <summary>
        /// **第二大步入口的纯后缀清洗**：只把第一个点之后那一段里的非 ASCII 杂质删掉，
        /// 基名一个字不动；判不出/清洗后不是规范形态 ⇒ 不改。
        ///
        /// <para>公开只给交界处调用；执行仍走 <see cref="TryApply"/>，不另写一套改名。</para>
        /// </summary>
        public static bool TryStripSuffixJunk(string? fileName, out string cleaned)
        {
            cleaned = string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            return TryStripNonAsciiJunkFromTailSegment(fileName, out cleaned);
        }
    }
}