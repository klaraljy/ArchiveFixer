using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **第二大步「收片」的两个执行体**（2026-10-23 按 <c>修改日志.md</c> 2026-10-22 的口径重建：
    /// 这两段原先在协调器里、随未提交改动一起丢失，全仓 0 处）。
    ///
    /// <list type="number">
    /// <item><description><b>搬进入口层</b>：程序产出的片**同卷搬**、跨卷复制（源不动）；
    /// **用户导入的原件只建零字节硬链接**（源目录只读红线）；⛔ 目标名被占 ⇒ 不覆盖、跳过并写清原因。</description></item>
    /// <item><description><b>成功后删片</b>：这一组解开成功 ⇒ 入口层里这一组的片按过程物删掉；
    /// 失败 / 密码错 / 缺卷 ⇒ **调用方不调这个方法**（一个字节都不动）。
    /// ⛔ 用户导入的原件不删（判据由调用方给的唯一出口回答，⛔ 这里不自己判"谁是用户源"）。</description></item>
    /// </list>
    /// </summary>
    public static class VolumeGroupEntryLayer
    {
        /// <summary>搬片结果（逐片细节 + 计数；计数与细节都不许静默）。</summary>
        public sealed class MoveResult
        {
            public int Moved { get; set; }

            public int Copied { get; set; }

            public int Linked { get; set; }

            public int Skipped { get; set; }

            public int Failed { get; set; }

            public List<string> Details { get; } = new();

            public string Describe() =>
                $"搬进入口层：搬 {Moved} / 复制 {Copied} / 链接 {Linked} / 跳过 {Skipped} / 失败 {Failed}";
        }

        /// <summary>
        /// 把这一组的片放进**入口包那一层**。
        /// </summary>
        /// <param name="pieces">这一组的片（物理路径；可含已经在入口层的那一份）。</param>
        /// <param name="entryLayerDirectory">入口包所在那一层（目标目录）。</param>
        /// <param name="isUserImportedSource">
        /// 调用方给的**唯一判据**：这一片是不是用户导入的原件。判据不在这里（⛔ 不按路径/名字猜）。
        /// </param>
        /// <param name="log">日志回调 (级别, 文案)；为空则不写。</param>
        /// <param name="forceLinks">
        /// true = **一律只建硬链接**（交界处重建 C 段用：宁可多一个链接名字，也绝不搬走/复制任何源片；
        /// 目标名被占、硬链接建不出来 ⇒ 跳过并写清原因）。
        /// </param>
        public static MoveResult MovePiecesIntoEntryLayer(
            IEnumerable<string>? pieces,
            string? entryLayerDirectory,
            Func<string, bool>? isUserImportedSource,
            Action<string, string>? log = null,
            bool forceLinks = false)
        {
            var result = new MoveResult();

            if (string.IsNullOrWhiteSpace(entryLayerDirectory))
            {
                result.Failed++;
                result.Details.Add("入口层目录为空 —— 什么都不做");
                return result;
            }

            foreach (string piece in pieces ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(piece) || !File.Exists(piece))
                {
                    result.Skipped++;
                    continue;
                }

                try
                {
                    string target = Path.Combine(entryLayerDirectory, Path.GetFileName(piece));

                    if (string.Equals(Path.GetFullPath(piece), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    {
                        // 已经在入口层：什么都不用做。
                        result.Skipped++;
                        continue;
                    }

                    if (File.Exists(target))
                    {
                        // ⛔ 绝不覆盖。
                        result.Skipped++;
                        result.Details.Add($"{Path.GetFileName(piece)}：目标名已被占用，跳过（不覆盖）");
                        continue;
                    }

                    bool userSource = forceLinks || isUserImportedSource?.Invoke(piece) == true;

                    if (userSource)
                    {
                        /*
                         * 用户导入的原件：**只建零字节硬链接**，一个字节都不搬走
                         * （源目录只读红线；搬走会打断"源包原地不动"）。硬链接建不出来 ⇒ 不动，写清原因。
                         */
                        if (HardLinkHelper.TryCreateHardLink(target, piece))
                        {
                            result.Linked++;
                            result.Details.Add(forceLinks
                                ? $"{Path.GetFileName(piece)}：C 段规则，只在入口层建硬链接（原件未动）"
                                : $"{Path.GetFileName(piece)}：用户原件，已在入口层建硬链接（原件未动）");
                        }
                        else
                        {
                            result.Skipped++;
                            result.Details.Add($"{Path.GetFileName(piece)}：硬链接建不出来（跨卷？），原件未动");
                        }

                        continue;
                    }

                    if (HardLinkHelper.CanHardLink(piece, entryLayerDirectory))
                    {
                        File.Move(piece, target);
                        result.Moved++;
                        result.Details.Add($"{Path.GetFileName(piece)}：程序产出的片，同卷搬进入口层");
                    }
                    else
                    {
                        // 跨卷：搬不动也不能把唯一的产物弄丢 ⇒ 复制，源留在原处（程序退出前由清理那一档收）。
                        File.Copy(piece, target, overwrite: false);
                        result.Copied++;
                        result.Details.Add($"{Path.GetFileName(piece)}：程序产出的片，跨卷已复制进入口层（源仍在原处）");
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Details.Add($"{Path.GetFileName(piece)}：搬进入口层失败 —— {ex.GetType().Name}：{ex.Message}");
                }
            }

            foreach (string detail in result.Details)
            {
                log?.Invoke(result.Failed > 0 ? "WARN" : "INFO", detail);
            }

            return result;
        }

        /// <summary>按规范名计划的链接结果（逐片细节 + 真建出来的链接名）。</summary>
        public sealed class PlanLinkResult
        {
            public int Linked { get; set; }

            public int Skipped { get; set; }

            public int Failed { get; set; }

            public List<string> LinkedNames { get; } = new();

            public List<string> Details { get; } = new();

            public string Describe() => $"按规范名建链接：链接 {Linked} / 跳过 {Skipped} / 失败 {Failed}";
        }

        /// <summary>
        /// **按族算法给出的规范名计划建链接**（第二大步 P1）：只建硬链接，
        /// ⛔ 不搬、不复制、不覆盖；目标名被占/源片不在/就是它自己 ⇒ 跳过并写清原因。
        /// </summary>
        public static PlanLinkResult LinkByPlan(
            CanonicalNamePlan? plan,
            string? sourceLayer,
            Action<string, string>? log = null)
        {
            var result = new PlanLinkResult();

            if (plan == null || plan.IsEmpty || string.IsNullOrWhiteSpace(sourceLayer))
            {
                result.Failed++;
                result.Details.Add("规范名计划或入口层为空 —— 什么都不做");
                return result;
            }

            foreach (CanonicalNamePlanItem item in plan.Items)
            {
                if (string.IsNullOrWhiteSpace(item.PiecePath) || !File.Exists(item.PiecePath))
                {
                    result.Skipped++;
                    result.Details.Add($"{item.TargetName}：源片不在盘上，跳过");
                    continue;
                }

                string target = Path.Combine(sourceLayer, item.TargetName);

                /*
                 * 目标名已经存在、且与源片是**同一份物理文件**（上一轮已建过这条链接）⇒
                 * 不算冲突、不再建一次，但必须记进 LinkedNames —— 收片账要按它把原名也一起收掉。
                 */
                if (File.Exists(target)
                    && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(item.PiecePath), StringComparison.OrdinalIgnoreCase)
                    && FileIdentity.IsSamePhysicalFile(item.PiecePath, target))
                {
                    result.Linked++;
                    result.LinkedNames.Add(target);
                    result.Details.Add($"{item.TargetName}：同一份文件的规范名已经在了（计入本组收片账）");
                    continue;
                }

                if (item.Conflict.Length > 0)
                {
                    result.Skipped++;
                    result.Details.Add($"{item.TargetName}：{item.Conflict}，跳过（不覆盖）");
                    continue;
                }

                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(item.PiecePath), StringComparison.OrdinalIgnoreCase))
                {
                    result.Skipped++;
                    continue;
                }

                if (File.Exists(target))
                {
                    result.Skipped++;
                    result.Details.Add($"{item.TargetName}：目标名已被占用，跳过（不覆盖）");
                    continue;
                }

                if (HardLinkHelper.TryCreateHardLink(target, item.PiecePath))
                {
                    result.Linked++;
                    result.LinkedNames.Add(target);
                    result.Details.Add($"{Path.GetFileName(item.PiecePath)} → {item.TargetName}：按规范名建硬链接（原件未动）");
                }
                else
                {
                    result.Failed++;
                    result.Details.Add($"{item.TargetName}：硬链接建不出来（跨卷？）");
                }
            }

            foreach (string detail in result.Details)
            {
                log?.Invoke(result.Failed > 0 ? "WARN" : "INFO", detail);
            }

            return result;
        }

        /// <summary>
        /// 成功 ⇒ 只删**本组自己建出来的链接名**（用户原件不删；失败那一档不调用）。
        /// ⛔ 不按原片名找，避免把用户原件或别的组的链接删掉。
        /// </summary>
        public static int PurgeLinkedNames(
            IEnumerable<string>? linkedNames,
            Func<string, bool>? isUserImportedSource,
            Action<string, string>? log = null)
        {
            int deleted = 0;

            foreach (string name in linkedNames ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(name) || !File.Exists(name))
                {
                    continue;
                }

                if (isUserImportedSource?.Invoke(name) == true)
                {
                    log?.Invoke("INFO", $"{Path.GetFileName(name)}：用户原件，不删");
                    continue;
                }

                try
                {
                    File.Delete(name);
                    deleted++;
                    log?.Invoke("INFO", $"{Path.GetFileName(name)}：这一组已经解开 ⇒ 链接按过程物删除");
                }
                catch (Exception ex)
                {
                    log?.Invoke("WARN", $"{Path.GetFileName(name)}：链接没删掉（不影响内容物）—— {ex.GetType().Name}");
                }
            }

            return deleted;
        }

        /// <summary>
        /// 这一组**解开成功**之后：把入口层里这一组的片按过程物删掉（⛔ 用户导入的原件不删）。
        /// 失败 / 密码错 / 缺卷那一档**不调用本方法** —— 片要留在入口包的位置。
        /// </summary>
        /// <returns>真删掉的片数。</returns>
        public static int PurgeSolvedGroupPiecesInEntryLayer(
            IEnumerable<string>? pieces,
            string? entryLayerDirectory,
            Func<string, bool>? isUserImportedSource,
            Action<string, string>? log = null)
        {
            int deleted = 0;

            if (string.IsNullOrWhiteSpace(entryLayerDirectory) || !Directory.Exists(entryLayerDirectory))
            {
                return 0;
            }

            foreach (string piece in pieces ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(piece))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(entryLayerDirectory, Path.GetFileName(piece));

                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    // ⛔ 用户导入的原件不删（判据由调用方给，唯一出口）。
                    if (isUserImportedSource?.Invoke(candidate) == true)
                    {
                        log?.Invoke("INFO", $"{Path.GetFileName(candidate)}：用户原件，不删");
                        continue;
                    }

                    File.Delete(candidate);
                    deleted++;
                    log?.Invoke("INFO", $"{Path.GetFileName(candidate)}：这一组已经解开 ⇒ 入口层里的片按过程物删除");
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        "WARN",
                        $"{Path.GetFileName(piece)}：入口层里的片没删掉（不影响内容物）—— {ex.GetType().Name}：{ex.Message}");
                }
            }

            return deleted;
        }
    }
}
