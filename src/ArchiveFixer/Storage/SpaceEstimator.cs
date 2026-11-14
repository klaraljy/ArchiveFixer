using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 任务空间需求的**唯一**估算处（<c>Storage\</c>；不引用 WPF）。
    ///
    /// <para>两级精度，刻意分开：</para>
    /// <list type="number">
    /// <item><description><b>粗估</b>（<see cref="FromSourceFiles"/>，只 stat 文件，不跑引擎）：
    /// 调度排序、并发建议、启动前的第一道空间门用它 —— 排 50–200 个包时绝不能为排序去逐个列目录。</description></item>
    /// <item><description><b>精估</b>（<see cref="RefineWithListing"/>，解压前那一遍 list 的产物）：
    /// 拿到清单后用真实的内容物大小替换估算值，这是**硬门**用的数字（同一份 list 也喂给
    /// <c>ResourceBudget</c>，绝不为了它多跑一次 7z —— 加密包每多列一次目录就多一次失败机会）。</description></item>
    /// </list>
    /// </summary>
    public static class SpaceEstimator
    {
        /// <summary>
        /// 清单读不到时，内容物按源包体积的几倍估。
        ///
        /// <para>取 1.0：资源包绝大多数是"已经压过一遍的东西再打包一次"（视频 / 图片 / 已压缩的内层包），
        /// 解压后总量与压缩包同量级。这是**下界估计**，不是承诺 —— 所以：</para>
        /// <list type="bullet">
        /// <item><description>它只用于**排序与并发建议**，以及"启动前"那道门；</description></item>
        /// <item><description>真正的硬门是解压前那一遍 list 算出来的精确值（<see cref="RefineWithListing"/>）；</description></item>
        /// <item><description>盘上还要求保留 <c>ResourceBudgetOptions.MinFreeSpaceReserveBytes</c>（默认 512 MiB）余量。</description></item>
        /// </list>
        /// </summary>
        public const double UnknownListingContentAllowance = 1.0d;

        /// <summary>
        /// 内层包**再展开**的增量按它自身体积的几倍估（0 = 不计）。
        ///
        /// <para><b>取 0 = 按「净增量」估（用户 2026-10-03 拍板，第 ③ 条）</b>：要的是
        /// <b>展开后 − 包本体</b>那一份，而不是"整份再扣一遍"。内层包**自身**已经算在
        /// <see cref="TaskSpaceEstimate.ContentBytes"/> 里，而它展开出来的内容与它同量级
        /// （资源包本来就是"已经压过一遍的东西再打包一次"）⇒ 净增量 ≈ 0。</para>
        ///
        /// <para><b>为什么老口径（1.0）是错的</b>：等于把内层包算了两遍（内容物里一遍 + 增量里一遍），
        /// 四层链的需求会被抬到"层数 × 单层"；朋友那台真机就是这么被抬高的
        /// （程序要 57.29 GiB、真实峰值 42.85 GiB —— 见 <c>_tmp\方案-普通档逐层回收其余物.md</c> §1）。</para>
        ///
        /// <para><b>为什么敢取 0</b>：普通档从 2026-10-03 起**逐层回收**（<c>ExtractionCoordinator</c>
        /// 在每一层定稿 + 校验通过之后当场删掉这一层的过程物），盘上同一时刻只有"当前层 + 下一层"
        /// ⇒ 展开内层包时，上一层的那些字节已经还回来了，不需要为"所有内层包同时展开"留余量。</para>
        ///
        /// <para>⚠ 下界仍然守得住：放行判据 <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>
        /// 是"内容物 + 过程物"的饱和加法，⛔ **永远 ≥ 内容物**；真正的硬门照旧是解压前那一遍
        /// list 算出来的精确值（<see cref="RefineWithListing"/>）与 <c>ResourceBudget</c>。</para>
        /// </summary>
        public const double NestedExpansionAllowance = 0.0d;

        /// <summary>
        /// 粗估：只读文件大小，不碰引擎。分卷组按 <see cref="ArchiveTask.VolumePaths"/> 求和（路径去重）。
        /// </summary>
        /// <param name="directReadAvailable">
        /// 这个任务的内嵌归档能不能走**直读**（<c>Extraction/EmbeddedZipStreamExtractor</c>）。
        /// <para>为 true 时**不记**那笔抠取副本 —— 直读路线一个字节的过程物都不产生
        /// （用户 2026-09-24 需求第 7 条：账面上必须与真实发生的动作一致，
        /// 不许一边说"省了副本"一边又把它预留出来）。判别由调用方给
        /// （<c>ArchiveTask.EmbeddedDirectReadSupported</c> + 这一批会不会走递归模式），
        /// 估算器本身不碰文件、不读设置。真回落时由抠取器自己那道空间预检兜底：
        /// 那一刻会如实报"取出内嵌归档需要约 X MB 临时空间"，而不是把盘写满。</para>
        /// </param>
        /// <param name="countsSourceAsNewOccupancy">
        /// 本批开着「部分完成发布」（③页那个开关）—— 那一档跑完源包**一定还在盘上**，
        /// 所以放行判据要按"多留一整个源包"算（见 <see cref="TaskSpaceEstimate.CountsSourceAsNewOccupancy"/>）。
        /// 精估（<see cref="RefineWithListing"/>）从这一份**继承**这个事实，⛔ 不需要也不许再传一次
        /// （同一件事的判据只允许有一个来源）。
        /// </param>
        public static TaskSpaceEstimate FromSourceFiles(
            ArchiveTask? task,
            bool directReadAvailable = false,
            bool countsSourceAsNewOccupancy = false)
        {
            if (task == null)
            {
                return new TaskSpaceEstimate { Basis = "没有任务，无法估算" };
            }

            IReadOnlyList<string> files = CollectSourceFiles(task);
            long sourceBytes = SumFileSizes(files, out int measuredCount, out int missingCount);

            /*
             * 内容物的下界估计 = 源包体积 × 1.0（见 UnknownListingContentAllowance）。
             * 用"下界"这三个字是认真的：它不保证够，只保证不会把明显放不下的任务排上去。
             */
            long contentEstimate = (long)Math.Min(
                long.MaxValue,
                sourceBytes * UnknownListingContentAllowance);

            long carvedBytes = EstimateCarvedBytes(task, sourceBytes, directReadAvailable);

            string basis =
                $"扫到 {measuredCount} 个源文件（{TaskSpaceEstimate.FormatSize(sourceBytes)}）"
                + (missingCount > 0 ? $"，其中 {missingCount} 个读不到大小" : string.Empty)
                + $"，内容物按源包 {UnknownListingContentAllowance:0.##} 倍估（清单还没读，属于下界）"
                + DescribeCarvedBytes(carvedBytes, task, directReadAvailable);

            return new TaskSpaceEstimate
            {
                TaskPath = task.CurrentPath,
                DisplayName = string.IsNullOrWhiteSpace(task.FileName)
                    ? Path.GetFileName(task.CurrentPath)
                    : task.FileName,
                SourceBytes = sourceBytes,
                ContentBytes = contentEstimate,
                ProcessArtifactBytes = carvedBytes,
                ContentEstimated = true,
                HasListing = false,
                CountsSourceAsNewOccupancy = countsSourceAsNewOccupancy,
                Basis = basis
            };
        }

        /// <summary>
        /// 精估：用解压前那一遍 list 替换内容物与过程物，源包大小用**真正交给引擎的那个文件**
        /// （内嵌归档抠出来之后它比源文件小得多，拿源文件当分母会把展开比算小）。
        /// </summary>
        /// <param name="cheap">粗估（提供源包字节数；为 null 时只按清单算）。</param>
        /// <param name="list">引擎给的条目清单；null / Success=false 表示读不到 —— **不假装知道**。</param>
        public static TaskSpaceEstimate RefineWithListing(
            TaskSpaceEstimate? cheap,
            ArchiveListResult? list,
            long carvedBytes = 0,
            bool carvedBytesNotNeeded = false)
        {
            TaskSpaceEstimate basis = cheap ?? new TaskSpaceEstimate();

            if (list == null || !list.Success)
            {
                /*
                 * 清单读不到（加密头 / 引擎拒绝列目录 / 流式）：**保留粗估**，一个字都不许假装知道。
                 * 只把"已经量出来的内嵌归档过程物"补进去 —— 那个字节数是真量出来的。
                 */
                return new TaskSpaceEstimate
                {
                    TaskPath = basis.TaskPath,
                    DisplayName = basis.DisplayName,
                    SourceBytes = basis.SourceBytes,
                    ContentBytes = basis.ContentBytes,
                    ProcessArtifactBytes = Math.Max(basis.ProcessArtifactBytes, carvedBytes > 0 ? carvedBytes : 0L),
                    ContentEstimated = true,
                    HasListing = false,
                    CountsSourceAsNewOccupancy = basis.CountsSourceAsNewOccupancy,
                    Basis = basis.Basis + "；本次仍没拿到条目清单，内容物维持估算值"
                };
            }

            (long contentBytes, long innerArchiveBytes) = SumListing(list);

            /*
             * 内层包再展开的**净增量**（用户 2026-10-03 第 ③ 条）：内层包**自身**已经在 contentBytes 里，
             * 这里只加"展开后 − 包本体"那一份 —— 见 NestedExpansionAllowance 的说明。
             * 没有内层包条目时是 0 —— 不凭空加。
             */
            long nestedExtra = (long)Math.Min(
                long.MaxValue,
                innerArchiveBytes * NestedExpansionAllowance);

            long processBytes = TaskSpaceEstimate.SaturatingSum(carvedBytes, nestedExtra);

            string basis2 =
                $"清单 {list.FileCount} 个文件 / 解压后 {TaskSpaceEstimate.FormatSize(contentBytes)}"
                + (innerArchiveBytes > 0
                    ? $"，其中内层包 {TaskSpaceEstimate.FormatSize(innerArchiveBytes)}"
                      + "（它本体已算在内容物里，再展开按「净增量」估、不再按整份扣一遍）"
                    /*
                     * ⛔ 这里**只能**说"按名字看不出"，不能说"没有内层包"（用户 2026-10-05 真机：
                     * 那句"没有内层包"是假话 —— 那个包里有 3 层内层包，只是第 0 层清单里那三个
                     * `.mp4` 的名字看不出是归档，它们其实是"尾部藏着归档"的双面文件）。
                     * ⛔ 只改措辞，预算口径一个字都不动（这一档本来就是 0 净增量）。
                     */
                    : "，按名字看不出内层包（真正的内层包要解出来才知道）")
                + DescribeCarvedBytes(carvedBytes, null, carvedBytesNotNeeded);

            return new TaskSpaceEstimate
            {
                TaskPath = basis.TaskPath,
                DisplayName = basis.DisplayName,
                SourceBytes = basis.SourceBytes,
                ContentBytes = contentBytes,
                ProcessArtifactBytes = processBytes,
                ContentEstimated = false,
                HasListing = true,
                CountsSourceAsNewOccupancy = basis.CountsSourceAsNewOccupancy,
                Basis = basis2
            };
        }

        /// <summary>
        /// 内嵌归档抠出来会多占多少字节：源文件里 <c>[偏移, 归档终点)</c> 那一段的副本。
        /// 偏移为 0（不是内嵌归档）时返回 0 —— 绝不凭空加一份。
        ///
        /// 终点缺失（0 或 ≤ 偏移）时按"抠到文件末尾"算，与抠取侧的回落完全同口径；
        /// 真实资源包在 EOCD 之后还有十几 KB 正常数据，那截不会被抠出来，所以这里也**不能**算进去。
        ///
        /// <para><paramref name="directReadAvailable"/> 为 true 时返回 0：
        /// 这个任务会走 ZIP 直读，**不会**产生那份等大的临时副本（用户 2026-09-24 需求第 7 条）。</para>
        /// </summary>
        public static long EstimateCarvedBytes(ArchiveTask? task, long sourceBytes, bool directReadAvailable = false)
        {
            if (task == null || task.EmbeddedArchiveOffset <= 0)
            {
                return 0;
            }

            if (directReadAvailable)
            {
                return 0;
            }

            long tail = 0;

            try
            {
                long length = new FileInfo(task.CurrentPath).Length;

                tail = task.EmbeddedArchiveEnd > task.EmbeddedArchiveOffset &&
                       task.EmbeddedArchiveEnd <= length
                    ? task.EmbeddedArchiveEnd - task.EmbeddedArchiveOffset
                    : length - task.EmbeddedArchiveOffset;
            }
            catch
            {
                // 量不到就退回源包总量作上界（宁可高估）：抠包是真实的字节拷贝。
                tail = sourceBytes;
            }

            return tail > 0 ? tail : 0;
        }

        /// <summary>
        /// 空间账面上"那份临时副本"的一句话。**口径必须与真实发生的动作一致**（用户 2026-09-24 需求第 7 条）：
        /// 直读时明说"不需要副本"，抠取时给出字节数 —— 不许一边说省了、一边又预留。
        /// </summary>
        private static string DescribeCarvedBytes(long carvedBytes, ArchiveTask? task, bool directReadAvailable)
        {
            bool isEmbedded = task == null
                ? carvedBytes > 0 || directReadAvailable
                : task.EmbeddedArchiveOffset > 0;

            if (!isEmbedded)
            {
                return string.Empty;
            }

            if (directReadAvailable)
            {
                return "，内嵌归档走 ZIP 直读（不需要那份等大的临时副本，不记这一笔）";
            }

            return carvedBytes > 0
                ? $"，内嵌归档过程物（抠取副本）{TaskSpaceEstimate.FormatSize(carvedBytes)}"
                : string.Empty;
        }

        /// <summary>逐条累加清单（与 <c>ResourceBudget.CheckBeforeExtract</c> 同一口径），顺便挑出内层包条目。</summary>
        private static (long ContentBytes, long InnerArchiveBytes) SumListing(ArchiveListResult list)
        {
            long total = 0;
            long inner = 0;

            foreach (ArchiveEntry? entry in list.Entries ?? Array.Empty<ArchiveEntry>())
            {
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                // 负数是解析异常：按 0 计，但绝不用它冲减已累计的量（同一个理由见 ResourceBudget）。
                long size = entry.Size > 0 ? entry.Size : 0L;

                total = TaskSpaceEstimate.SaturatingSum(total, size);

                if (LooksLikeNestedArchive(entry.Path))
                {
                    inner = TaskSpaceEstimate.SaturatingSum(inner, size);
                }
            }

            /*
             * 引擎自报的总量与逐条累加取更大的那个：两边不一致时按更保守的一侧算
             * （解析器少报几条就能把超限的包放过去，那是空间判断最不能出的错）。
             */
            return (Math.Max(total, list.TotalUncompressedSize > 0 ? list.TotalUncompressedSize : 0L), inner);
        }

        /// <summary>
        /// 清单里的一个条目是不是"解出来还要再解一层"的内层包。
        ///
        /// <para>后缀词表**不在这里重写**：<see cref="ExtensionHelper.IsKnownArchiveExtension"/> 与
        /// <see cref="ExtensionHelper.IsVolumePartExtension"/> 才是既有资产
        /// （`MaintenanceCleanupService.LooksLikeArchiveFile` 用的是同一套判断，只是它是私有的）。
        /// 这里只做"空间估算要不要给内层包留增量"这一个用途。</para>
        /// </summary>
        internal static bool LooksLikeNestedArchive(string? entryPath)
        {
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                return false;
            }

            string last = ExtensionHelper.GetLastExtension(entryPath);

            if (ExtensionHelper.IsKnownArchiveExtension(last))
            {
                return true;
            }

            // 分卷段（.001 / .z01 / .r00 / .part1）：它是"一组内层包"的一段，同样要再解一层。
            return ExtensionHelper.IsVolumePartExtension(last);
        }

        /// <summary>本任务的源文件集合（当前路径 + 最初路径 + 分卷各卷），按全路径去重。</summary>
        private static IReadOnlyList<string> CollectSourceFiles(ArchiveTask task)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();

            foreach (string? candidate in new[] { task.CurrentPath, task.OriginalPath }.Concat(task.VolumePaths))
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string full = SafePathHelper.GetFullPathSafe(candidate);

                if (string.IsNullOrWhiteSpace(full))
                {
                    full = candidate;
                }

                if (seen.Add(full))
                {
                    files.Add(full);
                }
            }

            return files;
        }

        /// <summary>把一组文件的大小求和（读不到的计入 <paramref name="missingCount"/>，按 0 计但如实报数）。</summary>
        private static long SumFileSizes(IReadOnlyList<string> files, out int measuredCount, out int missingCount)
        {
            long total = 0;
            int measured = 0;
            int missing = 0;

            foreach (string file in files)
            {
                try
                {
                    total = TaskSpaceEstimate.SaturatingSum(total, new FileInfo(file).Length);
                    measured++;
                }
                catch
                {
                    // 文件没了 / 被占用：**不抛** —— 空间估算跑在解压前的准备阶段，
                    // 一次抛异常会把"这个包我还没开始解"变成"整个任务崩了"。
                    missing++;
                }
            }

            measuredCount = measured;
            missingCount = missing;

            return total;
        }
    }
}
