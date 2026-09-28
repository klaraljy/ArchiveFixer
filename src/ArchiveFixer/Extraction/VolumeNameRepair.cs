using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「按建议改名并重试」的计划（用户 2026-09-25 第 41 条）。
    ///
    /// <para><b>为什么要有它</b>：第一卷第名字被改坏（<c>X.7z(删掉.001</c>）时，程序**只能**报「分卷缺失」
    /// 并给一个改名建议 —— 源文件一个字节都不能动（不变量 1）。可用户手上那一卷是真需要改名的，
    /// 让他自己去资源管理器里对着日志手打一遍名字，既慢又容易打错（那名字本来就不规则）。
    /// 于是给一个**显式**的按钮：「按建议改名并重试」——用户点了才改，只改**名字**、内容一个字节不动。</para>
    ///
    /// <para><b>判据全在事实里</b>（不看中文状态、不看调用方怎么想）：</para>
    /// <list type="number">
    /// <item><description>源文件还在；</description></item>
    /// <item><description>它的名字里**有卷号、而且是第 1 卷**（不是第一卷时改名毫无意义）；</description></item>
    /// <item><description>同目录里找得到"像后续卷"的文件（一个都没有就凑不齐一组）；</description></item>
    /// <item><description>从后续卷的名字**推得出**标准名（推不出来宁可不做 —— 改错名字比不改更糟）；</description></item>
    /// <item><description>它现在的名字**不是**标准名（已经是了就别乱动）；</description></item>
    /// <item><description>目标名**没被占用**（⛔ 绝不覆盖，不变量 3）。</description></item>
    /// </list>
    ///
    /// <para>任一不成立 → <see cref="VolumeNameRepairPlan.CanRepair"/> = false，并把**为什么不能改**
    /// 写在 <see cref="VolumeNameRepairPlan.Reason"/> 里（直接给用户看，不许含糊）。</para>
    /// </summary>
    public sealed class VolumeNameRepairPlan
    {
        /// <summary>能不能改（false 时看 <see cref="Reason"/>）。</summary>
        public bool CanRepair { get; init; }

        /// <summary>不能改的原因 / 能改时的补充说明（面向用户，可为空）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>现在的完整路径。</summary>
        public string CurrentPath { get; init; } = string.Empty;

        /// <summary>现在的文件名。</summary>
        public string CurrentFileName { get; init; } = string.Empty;

        /// <summary>建议的文件名（例如 <c>Code Complete-BZ.7z.001</c>）。</summary>
        public string SuggestedFileName { get; init; } = string.Empty;

        /// <summary>改名后的完整路径。</summary>
        public string TargetPath { get; init; } = string.Empty;

        /// <summary>同目录里"像这一组后续卷"的文件名（只用来把话说清）。</summary>
        public IReadOnlyList<string> Siblings { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 这次要改的**每一卷**（用户 2026-09-28 追加：网盘给整组的名字都缀了「删除」，
        /// 只改第一卷没用 —— 7-Zip 找 `.002` 时名字对不上，照样报缺卷）。
        ///
        /// <para>老调用方读上面那四个单文件属性（= 第一项），行为与以前逐字一样；
        /// 「修复分卷名并重试」按本列表一次把整组改干净。</para>
        /// </summary>
        public IReadOnlyList<VolumeRepairItem> Items { get; init; } = Array.Empty<VolumeRepairItem>();

        /// <summary>一行给人看：<c>旧名 → 新名</c>（整组时写出卷数）。</summary>
        public string Describe() =>
            Items.Count > 1
                ? $"{CurrentFileName} → {SuggestedFileName} 等 {Items.Count} 卷"
                : $"{CurrentFileName} → {SuggestedFileName}";
    }

    /// <summary>一组里的一卷：现在叫什么、该叫什么。</summary>
    public sealed class VolumeRepairItem
    {
        public string CurrentPath { get; init; } = string.Empty;

        public string CurrentFileName { get; init; } = string.Empty;

        public string SuggestedFileName { get; init; } = string.Empty;

        public string TargetPath { get; init; } = string.Empty;
    }

    /// <summary>改名结果。</summary>
    public sealed class VolumeNameRepairResult
    {
        /// <summary>是否真的改成功了。</summary>
        public bool Success { get; init; }

        /// <summary>改完之后的完整路径（失败时为空）。</summary>
        public string NewPath { get; init; } = string.Empty;

        /// <summary>给人看的一句话（成功 / 失败都说清）。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 「按建议改名并重试」的实现（纯逻辑 + 文件操作，不引用 WPF）。
    ///
    /// <para>建议名的推法与失败提示**共用同一份** <see cref="RawSplitStreamDetector.SuggestStandardFirstName"/>：
    /// ⛔ 界面上说的名字与真正改成的名字必须是同一个，否则用户点一下反而把文件改坏。</para>
    /// </summary>
    public static class VolumeNameRepair
    {
        /// <summary>
        /// 同目录里的文件名（只要名字、不要路径；读不了就返回空 —— 计划会因此判"不能改"，**绝不抛**）。
        ///
        /// <para>只有这一份实现：识别阶段（算"要不要点亮修复按钮"）、改名预览（算落点）、
        /// ①页那颗按钮（算真正会改成的名字）三处共用。⛔ 各写一份必然漂移 ——
        /// 那正是"按它说的改完还是解不开"的来源。</para>
        /// </summary>
        public static IReadOnlyList<string?> EnumerateFileNamesInDirectory(string? filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath ?? string.Empty) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return Array.Empty<string?>();
                }

                return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string?>();
            }
        }

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
                return Cannot(path, string.Format(StatusText.VolumeRepairPlanFailedFormat, ex.Message));
            }

            /*
             * 「名字里得有卷号、而且必须是第 1 卷」这两条是**这道门的核心**：
             * 名字里没有卷号的文件（普通包）改名只会把它弄坏；不是第一卷的（.002 之类）
             * 改名解决不了"缺第一卷"的问题 —— 那种情况该做的是把它放回原组，不是改它的名字。
             */
            int? index = VolumeGroupDetector.TryGetVolumeIndex(fileName);

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

            if (groupPlan != null)
            {
                return groupPlan;
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

            if (ExtensionHelper.TrySplitVolumeSegmentLoose(parts[^1], out _, out _))
            {
                return false;
            }

            return ExtensionHelper.TrySplitVolumeSegmentLoose(parts[^2], out _, out _);
        }

        /// <summary>
        /// 把"被伪装的卷名"拆开：<c>giu910.7z.001删除</c> → 基名 <c>giu910.7z</c>、标准卷段 <c>001</c>；
        /// <c>x.7z.001.txt</c> → 基名 <c>x.7z</c>、卷段 <c>001</c>；<c>y.z0删除3</c> → 基名 <c>y</c>、<c>z03</c>。
        /// 判据转调 <see cref="ExtensionHelper.TrySplitVolumeSegmentLoose"/>（只此一处）。
        /// </summary>
        private static bool TrySplitDisguised(string fileName, out string baseName, out string canonicalSegment)
        {
            baseName = string.Empty;
            canonicalSegment = string.Empty;

            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            // ① 最后一段自己就是"带垃圾的卷标记"（001删除 / 删除001 / z0删除3）
            if (!ExtensionHelper.IsVolumePartExtension("." + parts[^1]) &&
                ExtensionHelper.TrySplitVolumeSegmentLoose(parts[^1], out string mark, out _))
            {
                baseName = string.Join('.', parts, 0, parts.Length - 1);
                canonicalSegment = mark;
                return baseName.Length > 0;
            }

            // ② 卷标记后面还挂着别的点段（x.7z.001.txt）
            //    ⚠ 别去查卷标记自己是不是"已知压缩后缀" —— `.001` 本身就在那份名单里，
            //      拿它当闸门会把 `.001.txt` 全挡掉（实测踩到过）。
            for (int i = parts.Length - 2; i >= 1; i--)
            {
                if (!ExtensionHelper.TrySplitVolumeSegmentLoose(parts[i], out string innerMark, out _))
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

                baseName = string.Join('.', parts, 0, i);
                canonicalSegment = innerMark;
                return baseName.Length > 0;
            }

            return false;
        }

        /// <summary>
        /// 照着计划**只改名字**。⛔ 绝无覆盖、绝无删除、绝不改内容：
        /// 目标名已存在就原地拒绝（<see cref="File.Move(string, string)"/> 在没有 overwrite 参数时
        /// 碰到已存在的目标会抛 —— 这里不用"先判断再移动"的写法当唯一防线，两道都在）。
        /// </summary>
        public static VolumeNameRepairResult TryApply(VolumeNameRepairPlan? plan)
        {
            if (plan == null || !plan.CanRepair)
            {
                return Failure(plan?.Reason ?? StatusText.VolumeRepairSourceMissing);
            }

            long sizeBefore;

            try
            {
                if (!File.Exists(plan.CurrentPath))
                {
                    return Failure(StatusText.VolumeRepairSourceMissing);
                }

                if (string.Equals(plan.CurrentPath, plan.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(StatusText.VolumeRepairAlreadyStandard);
                }

                if (File.Exists(plan.TargetPath))
                {
                    return Failure(string.Format(StatusText.VolumeRepairTargetTakenFormat, plan.SuggestedFileName));
                }

                sizeBefore = new FileInfo(plan.CurrentPath).Length;

                File.Move(plan.CurrentPath, plan.TargetPath);
            }
            catch (Exception ex)
            {
                return Failure(string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, ex.Message));
            }

            /*
             * 改完立刻核一遍：新名字在、旧名字没了、**字节数一个不差**。
             * 这三条是"只改了名字"的机器证据（改名本来不该动内容；对不上就说明有别的程序在动它，
             * 那时候必须如实报出来，而不是让后面的解压拿着一个说不清的文件去跑）。
             */
            try
            {
                if (!File.Exists(plan.TargetPath))
                {
                    return Failure(string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, "改完之后新名字没找到"));
                }

                if (File.Exists(plan.CurrentPath))
                {
                    return Failure(string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, "旧名字还在"));
                }

                long sizeAfter = new FileInfo(plan.TargetPath).Length;

                if (sizeAfter != sizeBefore)
                {
                    return Failure(string.Format(
                        StatusText.VolumeRepairRenameFailedFormat,
                        plan.SuggestedFileName,
                        $"字节数变了（{sizeBefore} → {sizeAfter}）"));
                }
            }
            catch (Exception ex)
            {
                return Failure(string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, ex.Message));
            }

            /*
             * 还有别的卷要改（网盘给整组缀了「删除」那种）：接着一卷一卷来。
             * ⛔ 每一卷都走与上面同一套判据（源在、目标未被占、字节数不差）；
             * 中途失败就**停下并如实报"已改了 N 卷"**，绝不假装整组都改好了。
             */
            var rest = (plan.Items ?? Array.Empty<VolumeRepairItem>())
                .Where(i => !string.Equals(i.CurrentPath, plan.CurrentPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int done = 0;

            foreach (VolumeRepairItem item in rest)
            {
                try
                {
                    if (!File.Exists(item.CurrentPath))
                    {
                        return PartialFailure(plan, done, $"{item.CurrentFileName}：{StatusText.VolumeRepairSourceMissing}");
                    }

                    if (string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.Exists(item.TargetPath))
                    {
                        return PartialFailure(
                            plan,
                            done,
                            string.Format(StatusText.VolumeRepairTargetTakenFormat, item.SuggestedFileName));
                    }

                    long before = new FileInfo(item.CurrentPath).Length;
                    File.Move(item.CurrentPath, item.TargetPath);
                    long after = new FileInfo(item.TargetPath).Length;

                    if (after != before)
                    {
                        return PartialFailure(plan, done, $"字节数变了（{before} → {after}）");
                    }

                    done++;
                }
                catch (Exception ex)
                {
                    return PartialFailure(
                        plan,
                        done,
                        string.Format(StatusText.VolumeRepairRenameFailedFormat, item.SuggestedFileName, ex.Message));
                }
            }

            return new VolumeNameRepairResult
            {
                Success = true,
                NewPath = plan.TargetPath,
                Message = done == 0
                    ? string.Format(StatusText.VolumeRepairDoneFormat, plan.CurrentFileName, plan.SuggestedFileName)
                    : string.Format(
                        StatusText.VolumeRepairGroupDoneFormat,
                        plan.CurrentFileName,
                        plan.SuggestedFileName,
                        done + 1)
            };
        }

        /// <summary>整组改到一半失败：把"改了几卷、卡在哪一卷"写清楚（面向用户，不含糊）。</summary>
        private static VolumeNameRepairResult PartialFailure(VolumeNameRepairPlan plan, int done, string why) =>
            Failure(string.Format(StatusText.VolumeRepairGroupPartialFormat, done + 1, why));

        private static VolumeNameRepairPlan Cannot(string path, string reason) => new()
        {
            CanRepair = false,
            Reason = reason,
            CurrentPath = path,
            CurrentFileName = SafeFileName(path)
        };

        private static VolumeNameRepairResult Failure(string message) => new()
        {
            Success = false,
            Message = message
        };

        private static string SafeFileName(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
