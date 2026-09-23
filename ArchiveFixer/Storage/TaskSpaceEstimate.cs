using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 一个解压任务的**空间需求**（用户 2026-09-22 需求："空间核算必须包含内容物 + 本次会产生的过程物
    /// （分卷、内层包）+ 去重后的峰值需求"）。
    ///
    /// <para><b>为什么需要它</b>：<c>Security/ResourceBudget</c> 回答的是"这个包该不该解"（硬上限），
    /// 而用户真正会撞上的是"解到一半盘满了"。要提前拦住，就必须有一个**任务级的空间账面**。</para>
    ///
    /// <para><b>三个组成（去重后）</b>：</para>
    /// <list type="number">
    /// <item><description><see cref="SourceBytes"/> —— 本任务源包**整组**（分卷求和）。
    /// 解压期间它一直占着盘：成功之后只是**搬进 `其余物`**（同盘移动不释放空间），
    /// 只有危险模式才会在定稿之后真的把它删掉。</description></item>
    /// <item><description><see cref="ContentBytes"/> —— 内容物。清单拿得到时是解压后总大小（与
    /// <c>ResourceBudget</c> 同一份 list、同一套累加口径）；拿不到时按源包体积估**下界**并标注
    /// <see cref="ContentEstimated"/>。</description></item>
    /// <item><description><see cref="ProcessArtifactBytes"/> —— 本次会**额外**产生的过程物：
    /// 内嵌归档抠出来的中间件（源文件尾部那一段的副本）+ 内容物里的内层包**再展开**的增量
    /// （内层包自身已经算在 <see cref="ContentBytes"/> 里，这里只算它展开后多出来的那一份）。</description></item>
    /// </list>
    ///
    /// <para><b>去重规则（三条，缺一条就会把需求算大一倍）</b>：</para>
    /// <list type="number">
    /// <item><description>同一个**全路径**只算一次：源包与它的各卷按路径去重（改名前后的同一个文件不重复计）。</description></item>
    /// <item><description>分卷各卷只进 <see cref="SourceBytes"/>，**绝不**再按"过程物"算第二遍
    /// （旧 `解压7z分卷文件.bat` 的场景里，分卷既被当成源包又被当成中间件，很容易重复计）。</description></item>
    /// <item><description>内层归档自身已在 <see cref="ContentBytes"/> 里，<see cref="ProcessArtifactBytes"/>
    /// 只计它的**展开增量**。</description></item>
    /// </list>
    ///
    /// <para><b>峰值</b>：<see cref="PeakBytes"/> = 源包 + 过程物 + 内容物，即"这一切同时存在"的那一刻。
    /// 定稿搬运是同盘移动（不额外占），所以峰值就取这个和，不再乘任何系数。</para>
    /// </summary>
    public sealed class TaskSpaceEstimate
    {
        /// <summary>任务路径（源包路径，或分卷组第一卷）。只用于日志与去重，不参与判断。</summary>
        public string TaskPath { get; init; } = string.Empty;

        /// <summary>给用户看的名字（文件名）。</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>源包整组字节数（分卷求和；路径去重后）。</summary>
        public long SourceBytes { get; init; }

        /// <summary>内容物字节数。清单拿不到时是按源包体积估的下界（见 <see cref="ContentEstimated"/>）。</summary>
        public long ContentBytes { get; init; }

        /// <summary>本次会额外产生的过程物字节数（内嵌归档中间件 + 内层包再展开的增量）。</summary>
        public long ProcessArtifactBytes { get; init; }

        /// <summary>是不是"清单读不到、内容物按源包体积估的"。</summary>
        public bool ContentEstimated { get; init; }

        /// <summary>估算用没用引擎给的条目清单。</summary>
        public bool HasListing { get; init; }

        /// <summary>估算依据的一句话（进日志与任务详情，让用户能自己复核这个数字怎么来的）。</summary>
        public string Basis { get; init; } = string.Empty;

        /// <summary>
        /// 任务完成前**一直占着盘**的那部分（源包 + 过程物）。
        ///
        /// <para>危险模式"边解边彻底删其余物"能立刻收回的就是它 —— 这也是那种模式能解决"空间不够"的
        /// 全部原因：普通档下这些字节只是从源目录搬进了 `其余物`，净占用一点没变。</para>
        /// </summary>
        public long RetainedBytes => SaturatingSum(SourceBytes, ProcessArtifactBytes);

        /// <summary>峰值需求：源包 + 过程物 + 内容物同时存在的那一刻。</summary>
        public long PeakBytes => SaturatingSum(RetainedBytes, ContentBytes);

        /// <summary>危险模式在定稿之后能收回的字节数。</summary>
        public long ReclaimableBytes => RetainedBytes;

        /// <summary>饱和加法：回绕成负数等于把空间判断整个废掉（"需要 -2G"永远放行）。</summary>
        public static long SaturatingSum(long left, long right)
        {
            long a = left > 0 ? left : 0L;
            long b = right > 0 ? right : 0L;

            return a > long.MaxValue - b ? long.MaxValue : a + b;
        }

        /// <summary>一句话说法（日志/确认框用；数字一律走与 <see cref="SpaceChecker"/> 一致的口径）。</summary>
        public string Describe()
        {
            string name = string.IsNullOrWhiteSpace(DisplayName) ? TaskPath : DisplayName;

            return $"{name}：源包 {FormatSize(SourceBytes)} + 内容物 {FormatSize(ContentBytes)}"
                   + (ContentEstimated ? "（估）" : string.Empty)
                   + $" + 过程物 {FormatSize(ProcessArtifactBytes)} = 峰值 {FormatSize(PeakBytes)}";
        }

        /// <summary>
        /// 把字节数说成人话。**口径必须与 <c>SpaceChecker</c> / <c>ResourceBudget</c> 一致** ——
        /// 三处各写一套换算时，同一块盘在界面上会出现两个不同的数字（历史上因此被投诉过）。
        /// </summary>
        internal static string FormatSize(long bytes)
        {
            long value = bytes > 0 ? bytes : 0L;
            double gib = value / 1024d / 1024d / 1024d;

            if (gib >= 1d)
            {
                return gib.ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
            }

            double mib = value / 1024d / 1024d;

            if (mib >= 1d)
            {
                return mib.ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
            }

            double kib = value / 1024d;

            if (kib >= 1d)
            {
                return kib.ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
            }

            return value.ToString(CultureInfo.InvariantCulture) + " 字节";
        }
    }

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
        /// <para>取 1.0 = "内层包解出来最多再多占它自己那么多"。理由：内层包自身已经算在内容物里，
        /// 它展开之后多出来的那一份没有清单可查（要再列一次目录才知道），
        /// 而空间判断宁可高估也不能低估（<c>SpaceChecker</c> 同一条价值观："不敢说够"）。</para>
        /// </summary>
        public const double NestedExpansionAllowance = 1.0d;

        /// <summary>
        /// 粗估：只读文件大小，不碰引擎。分卷组按 <see cref="ArchiveTask.VolumePaths"/> 求和（路径去重）。
        /// </summary>
        /// <param name="directReadAvailable">
        /// 这个任务的内嵌归档能不能走**直读**（<c>Extraction/EmbeddedZipStreamExtractor</c>）。
        ///
        /// <para>为 true 时**不记**那笔抠取副本 —— 直读路线一个字节的中间件都不产生
        /// （用户 2026-09-24 需求第 7 条：账面上必须与真实发生的动作一致，
        /// 不许一边说"省了副本"一边又把它预留出来）。判别由调用方给
        /// （<c>ArchiveTask.EmbeddedDirectReadSupported</c> + 这一批会不会走递归模式），
        /// 估算器本身不碰文件、不读设置。真回落时由抠取器自己那道空间预检兜底：
        /// 那一刻会如实报"取出内嵌归档需要约 X MB 临时空间"，而不是把盘写满。</para>
        /// </param>
        public static TaskSpaceEstimate FromSourceFiles(ArchiveTask? task, bool directReadAvailable = false)
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
                 * 只把"已经量出来的内嵌归档中间件"补进去 —— 那个字节数是真量出来的。
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
                    Basis = basis.Basis + "；本次仍没拿到条目清单，内容物维持估算值"
                };
            }

            (long contentBytes, long innerArchiveBytes) = SumListing(list);

            /*
             * 内层包再展开的增量：内层包**自身**已经在 contentBytes 里，这里只加它再解一次的增量
             * （见 NestedExpansionAllowance 的说明）。没有内层包条目时是 0 —— 不凭空加。
             */
            long nestedExtra = (long)Math.Min(
                long.MaxValue,
                innerArchiveBytes * NestedExpansionAllowance);

            long processBytes = TaskSpaceEstimate.SaturatingSum(carvedBytes, nestedExtra);

            string basis2 =
                $"清单 {list.FileCount} 个文件 / 解压后 {TaskSpaceEstimate.FormatSize(contentBytes)}"
                + (innerArchiveBytes > 0
                    ? $"，其中内层包 {TaskSpaceEstimate.FormatSize(innerArchiveBytes)}（再展开按 {NestedExpansionAllowance:0.##} 倍估增量）"
                    : "，没有内层包")
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
                return "，内嵌归档走 ZIP 直读（**不需要**那份等大的临时副本，不记这一笔）";
            }

            return carvedBytes > 0
                ? $"，内嵌归档中间件（抠取副本）{TaskSpaceEstimate.FormatSize(carvedBytes)}"
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
