using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Helpers
{

    /// <summary>
    /// 文件名处理工具。
    /// </summary>
    public static class FileNameHelper
    {
        /// <summary>
        /// 获取文件名。
        /// </summary>
        public static string GetFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// 获取不带最后一个后缀的文件名。
        /// </summary>
        public static string GetFileNameWithoutExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return GetFileName(path);
            }
        }

        /// <summary>
        /// 获取目录路径。
        /// </summary>
        public static string GetDirectoryName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 清理非法文件名字符。
        /// </summary>
        public static string SanitizeFileName(string? fileName, string replacement = "_")
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "未命名";
            }

            string safeReplacement = replacement ?? "_";
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(fileName.Length);

            foreach (char ch in fileName)
            {
                if (Array.IndexOf(invalidChars, ch) >= 0)
                {
                    builder.Append(safeReplacement);
                }
                else
                {
                    builder.Append(ch);
                }
            }

            string result = builder.ToString().Trim();

            if (string.IsNullOrWhiteSpace(result))
            {
                result = "未命名";
            }

            result = TrimEndingDotsAndSpaces(result);

            if (IsReservedDeviceName(result))
            {
                result = "_" + result;
            }

            return result;
        }

        /// <summary>
        /// 清理非法路径片段。
        /// </summary>
        public static string SanitizePathPart(string? name)
        {
            return SanitizeFileName(name);
        }

        /// <summary>
        /// 去掉 Windows 不允许的结尾点和空格。
        /// </summary>
        public static string TrimEndingDotsAndSpaces(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "未命名";
            }

            string result = name.TrimEnd(' ', '.');

            if (string.IsNullOrWhiteSpace(result))
            {
                return "未命名";
            }

            return result;
        }

        /// <summary>
        /// 判断是否是 Windows 保留设备名。
        /// </summary>
        public static bool IsReservedDeviceName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string fileName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();

            return fileName is
                "CON" or
                "PRN" or
                "AUX" or
                "NUL" or
                "COM1" or
                "COM2" or
                "COM3" or
                "COM4" or
                "COM5" or
                "COM6" or
                "COM7" or
                "COM8" or
                "COM9" or
                "LPT1" or
                "LPT2" or
                "LPT3" or
                "LPT4" or
                "LPT5" or
                "LPT6" or
                "LPT7" or
                "LPT8" or
                "LPT9";
        }

        // ══════════════════ 基名只有这两份（用户 2026-10-23 拍板）══════════════════
        //
        // 规则（逐字口径）：取文件名里**从左数第一个 `.`**，删掉这个点及后面全部内容，剩下的就是基名；
        // 没有 `.` ⇒ 整个文件名就是基名。
        //   · 第一大步（单一压缩包）：111.ra删除r → 111、222-c.7AAAAAz → 222-c、333---v.zi中文中文中文p → 333---v
        //   · 第二大步（分卷）：      111.7z.001 → 111、222.zi删除p.002 → 222、333.part3.rar → 333、444.AAAAA.7删除z → 444
        //
        // 为什么必须两处分开：两步问的**不是同一个问题** —— 第一大步的格式与"是不是单卷"已由内容回答，
        // 基名只用来命名；第二大步没有这个前提：7z 非首卷、ZIP 裸切中间片在内容上判不出卷号与族，
        // 只能靠"第一个点之前的基名 + 大小分布"归组。⛔ 不许再回到"按调用方各挑一个档"的旧做法
        // （旧的 6 档体系已按用户要求整体删除）。
        //
        // ⛔ 两份的文本规则现在是同一条，但语义与前提不同：改其中一份不许影响另一份
        // —— 共用下面这一个私有切点，只共用"切在哪"，不共用"什么时候用"。

        /// <summary>
        /// **第一大步**（单一压缩包）的基名：第一个 <c>.</c> 之前。
        /// 前置事实：这一份已由内容证成"单一完整包"，格式也从内容拿到了 ⇒ 名字怎么脏都不影响判定，
        /// 基名只用于落点 / 命名 / 匹配键。
        /// </summary>
        /// <param name="path">文件名或路径（只取文件名）。</param>
        /// <returns>基名；判不出（空名字 / 以点开头 / 没有正文）⇒ 空串。</returns>
        public static string FirstStepBaseName(string? path) => CutAtFirstDot(GetFileName(path));

        /// <summary>
        /// **第二大步**（分卷）的基名：同一条文本规则（第一个 <c>.</c> 之前）。
        /// 前置事实**不要求**内容能给出卷号 —— 判不出族的片（7z 非首卷、ZIP 裸切中间片）
        /// 靠"基名相同 + 大小分布"归组，所以这里只用名字，不读内容、不问族。
        /// </summary>
        /// <param name="path">文件名或路径（只取文件名）。</param>
        /// <returns>基名；判不出 ⇒ 空串（调用方按"判不出 ⇒ 什么都不做"处理）。</returns>
        public static string SecondStepBaseName(string? path) => CutAtFirstDot(GetFileName(path));

        /// <summary>
        /// 唯一的切点：第一个 <c>.</c> 之前；没有点 ⇒ 全名；点在最开头（<c>.zip</c>）⇒ 空串（判不出）。
        /// </summary>
        private static string CutAtFirstDot(string name) => name.IndexOf('.') switch
        {
            < 0 => name,
            0 => string.Empty,
            int dot => name[..dot]
        };

        /// <summary>
        /// 生成压缩包基础名（**第一大步那份**：<see cref="FirstStepBaseName"/>，第一个 <c>.</c> 之前）。
        /// <c>222.7z.001</c> / <c>222.part1.rar</c> / <c>222.ra删除r</c> / <c>222.zi删除p.002</c> 一律得到 <c>222</c>。
        ///
        /// <para>
        /// ⚠ 2026-10-23（用户口径）：基名规则收成**两份** —— 第一大步 <see cref="FirstStepBaseName"/> 与
        /// 第二大步 <see cref="SecondStepBaseName"/>，都是"第一个点之前、没有点就是全名"。
        /// 旧的 6 档体系与它那几套"剥卷标记 / 剥已知后缀 / 从右往左剥"的实现
        /// 已按用户要求删除：同一件事的真值只允许有一个出口。
        /// </para>
        ///
        /// <para>
        /// 分卷那一步在这里是**免费的**：<c>222.7z.001</c> 的第一个点之后本来就全是卷标记与族后缀。
        /// ⛔ 别再加"什么算分卷标记"的第二把尺子 —— 那是 <see cref="ExtensionHelper.IsVolumePartExtension"/>
        /// 与 <see cref="IsVolumeContinuationPart"/> 的事，与基名无关。
        /// </para>
        /// </summary>
        public static string GetArchiveBaseName(string filePath)
        {
            string fileName = GetFileName(filePath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "未命名";
            }

            string baseName = FirstStepBaseName(fileName);

            return baseName.Length > 0 ? baseName : fileName;
        }

        /// <summary>
        /// 判断是否为多重伪装文件名。
        /// </summary>
        public static bool IsMultiExtensionSuspicious(string fileName, string suggestedExtension)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            // 分卷不是伪装。
            // 例：volume.7z.001 的最后一段是分卷标记，不是"伪装上去的图片后缀"，
            // 不能报成"多重后缀疑似伪装"（设计.md §七 要求分卷单独归类，M2 补"分卷后缀"状态）。
            if (IsVolumePartFileName(fileName))
            {
                return false;
            }

            suggestedExtension = ExtensionHelper.NormalizeExtension(suggestedExtension);

            if (string.IsNullOrWhiteSpace(suggestedExtension))
            {
                return false;
            }

            if (!ExtensionHelper.HasMultipleExtensions(fileName))
            {
                return false;
            }

            string lastExt = ExtensionHelper.GetLastExtension(fileName);

            if (string.Equals(lastExt, suggestedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ExtensionHelper.ContainsExtension(fileName, suggestedExtension);
        }

        /// <summary>
        /// 判断文件名是否是分卷的一部分。
        ///
        /// 例：
        ///   volume.7z.001 / data.zip.002   —— .001 分卷
        ///   movie.part1.rar                —— rar 新式分卷
        ///   old.rar / old.r00              —— rar 老式分卷
        ///   x.z01                          —— zip 分卷
        ///
        /// 用途：**拦住会破坏分卷链的改名**。7z 只认 ".001" 这一套命名，
        /// 一旦把 volume.7z.001 改成 volume.7z，整个分卷组就解不开了。
        /// </summary>
        public static bool IsVolumePartFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            // xxx.001 / xxx.z01 / xxx.part1
            // ⚠ 两处都放宽（用户 2026-09-28 第二轮）：
            //   ① 卷标记里/前后夹垃圾的（`xxx.7z.001删除`、`xxx.7z.删除001`、`xxx.z0删除3`）也算；
            //   ② **卷标记后面还挂着别的点段**的（`xxx.7z.001.txt`、`xxx.rar.001.bak`）也算 ——
            //      只要那些尾段**不是已知压缩后缀**。
            //   不算的话，这类名字会被当成"普通包"，改名时把卷号吃掉、整条分卷链断掉。
            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^1], out _, out _))
            {
                return true;
            }

            if (IsVolumeMarkFollowedByNonArchiveTail(parts))
            {
                return true;
            }

            // xxx.part1.rar
            // ⚠ 2026-10-03（真机 §51）：尾巴粘垃圾的（`xxx.part1.rar删除`）与标记里夹垃圾的
            // （`111.parts1.rar`）同属这一族 —— 判据同样转调唯一出口，⛔ 不再自己写一遍
            // "最后一段是不是 rar"（三处一起失效的那一格）。
            if (ExtensionHelper.TrySplitPartNumberedVolume(fileName, out _, out _, out _, out _, out _))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// "卷标记后面还挂着别的点段"（<c>x.7z.001.txt</c> / <c>x.rar.001.bak</c>）算不算分卷：
        /// **从右往左**找第一个能当卷标记的段，它右边的所有段都不许是已知压缩后缀。
        ///
        /// 为什么这么定：<c>x.7z.001.rar</c> 是"分卷段后挂 .rar"的既有写法（另一条分支管它），
        /// <c>x.7z.001.zip</c> 更像"一个真 zip 被改了名" —— 这两种都不算跨段伪装。
        /// </summary>
        private static bool IsVolumeMarkFollowedByNonArchiveTail(string[] parts)
        {
            if (parts.Length < 3)
            {
                return false;
            }

            for (int i = parts.Length - 2; i >= 1; i--)
            {
                if (!ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[i], out _, out _))
                {
                    continue;
                }

                /*
                 * ⚠ 收紧的一道闸门（2026-09-28 全量回归逮到的真回归）：
                 * 只有"卷标记**紧跟在已知压缩后缀后面**"才算跨段伪装（`x.7z.001.txt`、`x.rar.001.bak`）。
                 * 不加这条，`rar-android-722.132.apk` 会被当成"卷 132 + .apk 尾巴"，
                 * 基名被剥成 `rar-android-722` —— 落点目录跟着变（`OutputPlacementTests` 当场红）。
                 * 宁可漏认（用户还能靠改名解决），也不能把正常文件名剥错。
                 */
                if (i < 1 || !ExtensionHelper.IsKnownArchiveExtension("." + parts[i - 1]))
                {
                    continue;
                }

                // 卷标记左边得有基名（`001.txt` 这种连基名都没有的不算分卷）
                if (string.Join('.', parts, 0, i - 1).Length == 0)
                {
                    return false;
                }

                for (int j = i + 1; j < parts.Length; j++)
                {
                    if (ExtensionHelper.IsKnownArchiveExtension("." + parts[j]))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取安全的压缩包基础名。
        /// </summary>
        public static string GetSafeArchiveBaseName(string filePath)
        {
            return SanitizeFileName(GetArchiveBaseName(filePath));
        }

        /// <summary>
        /// 这个文件是不是**分卷的后续卷**（组的起点不是它）：<c>.002</c> 及更大的三位数字卷、
        /// <c>.z01</c>/<c>.r00</c>、<c>.part2</c> 及更大的分卷段都算；<c>.001</c> / <c>.zip</c> 本体 /
        /// <c>.part1</c> / <c>.rar</c>（起点）不算。
        ///
        /// <para><b>为什么判据只能有一份</b>（2026-09-28 审计 + 2026-10-02 真机）：全项目曾经三处各写一遍
        /// "分卷名判断"，两处不同步就会出"同一份包，扫描说它是后续卷、续解说它是内层包"这种自相矛盾。
        /// 现在的真实判据 = <see cref="ExtensionHelper.TrySplitVolumeSegmentTolerant"/> + "001 才是起点"，
        /// 由本方法一处给出：续解扫描（<c>OneClickCoordinator</c>）与递归的**单链判定**
        /// （<c>RecursiveExtractor.HasOnlyInformationalSiblings</c>）都读它。</para>
        ///
        /// <para><b>为什么递归的"单链判定"也需要它</b>（用户 2026-10-02 真机 `1-6 电磁感应定律（1）`）：
        /// 那一层里是 <c>51658213.7z.001</c>（**唯一**的内层归档）+ <c>51658213.7z.002</c>（**同一组**的后续卷），
        /// 老判据只跳过"内层归档那一个文件"，于是 <c>.002</c> 被算成"别的文件"⇒ 单链不成立 ⇒
        /// 报「这一层里有 1 个内层归档（多分支）」并**保守停在那一层**：一键档白白多跑一轮，
        /// 手动档还会拿"要不要展开多分支"去问用户 —— 而他看到的只有一个包。</para>
        /// </summary>
        public static bool IsVolumeContinuationPart(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string fileName = GetFileName(filePath);
            string extension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            /*
             * ⚠ 判据用**容差档**（2026-09-28 审计：这是全项目最后一处还在用"前缀档"的分卷名判断）。
             * 老写法 `IsVolumePartExtension(extension)` 只认干净的 `.001`/`.z01`，于是名字被伪装过的后续卷
             * （`amb909.7删z.00除2`、`amb909.7z.002sc`）在这里判成"不是后续卷" —— 与探测器、改名闸门
             * 的口径不一致（同一判据第三次翻车就是这种"两处不同步"）。判据只留 `ExtensionHelper` 一份。
             */
            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(extension.TrimStart('.'), out string volumeMark, out _))
            {
                // 三位数字分卷里只有 001 是起点；.z01 / .r00 这类也不是组的开头（老口径不变）。
                return !string.Equals(volumeMark, "001", StringComparison.OrdinalIgnoreCase);
            }

            /*
             * xxx.part2.rar 的最后后缀是 .rar，编号在倒数第二个后缀上。
             *
             * ⚠ 2026-10-13 收口（困难批 `run-2222` 实测）：这一档**转调唯一出口**
             * <see cref="ExtensionHelper.TrySplitPartNumberedVolume"/> —— 原来这里自己用"**逐字** `part` 前缀"
             * 数数字，于是 EEE 式塞了垃圾的名字（`bbb-L2.par@@@@t3.rar`，卷号 3）判成"**不是后续卷**" ⇒
             * 它进不了"后续卷"名单 ⇒ 在链里被当成**独立内层包**去开 ⇒ 逐字
             * `已完成 2 层，停在第 3 层，原因：压缩包损坏` ⇒ 整单失败、连那一片都被丢掉 ⇒ ①红。
             * 那把尺子（含骨架档）本来就能认出它来 —— 与 `IsVolumePartFileName` / 探测器 / 改名闸门
             * 用的是**同一份**判据（§9.5：同一件事的真值只允许有一个出口）。
             */
            return ExtensionHelper.TrySplitPartNumberedVolume(
                       fileName,
                       out _,
                       out _,
                       out int partNumberedIndex,
                       out _,
                       out _)
                   && partNumberedIndex > 1;
        }

    }
}
