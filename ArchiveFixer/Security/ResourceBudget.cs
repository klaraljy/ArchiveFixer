using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using ArchiveFixer.Engines;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Security
{
    /// <summary>
    /// 资源预算上限。全部是硬上限，不猜意图（AGENTS.md §2：不做压缩炸弹的"智能判定"）。
    /// 默认值偏保守，允许调用方从设置里覆盖。
    /// </summary>
    public sealed class ResourceBudgetOptions
    {
        /// <summary>解压后总大小上限，默认 20 GiB。</summary>
        public long MaxTotalSize { get; init; } = 20L * 1024 * 1024 * 1024;

        /// <summary>单个文件解压后大小上限，默认 4 GiB。</summary>
        public long MaxSingleFileSize { get; init; } = 4L * 1024 * 1024 * 1024;

        /// <summary>文件数上限，默认 20 万。</summary>
        public int MaxFileCount { get; init; } = 200_000;

        /// <summary>展开比上限（解压后 / 压缩包），默认 1000 倍，超过视为压缩炸弹。</summary>
        public double MaxExpansionRatio { get; init; } = 1000d;

        /// <summary>目标盘需要保留的空闲字节数，默认 512 MiB。</summary>
        public long MinFreeSpaceReserveBytes { get; init; } = 512L * 1024 * 1024;

        /// <summary>默认上限。init-only 属性 + 不可变对象，所以可以安全地在多处共享同一个实例。</summary>
        public static ResourceBudgetOptions Default { get; } = new();
    }

    /// <summary>一次解压前预算检查的结论。</summary>
    public sealed class BudgetCheckResult
    {
        public bool Allowed { get; init; }

        /// <summary>中文说明，直接给用户看（拒绝原因里一定带具体数字）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>本次判断用的解压后总大小估算（拿不到清单时为 0）。</summary>
        public long EstimatedTotalSize { get; init; }

        /// <summary>展开比；压缩包体积未知或 &lt;= 0 时为 0（表示"这项没判"）。</summary>
        public double ExpansionRatio { get; init; }

        /// <summary>目标盘可用字节数；没给目标目录或取不到时为 null。</summary>
        public long? FreeSpaceBytes { get; init; }
    }

    /// <summary>
    /// 资源预算 —— 解压前的硬上限判断（设计.md §二十二、AGENTS.md §6 第 8 条）。
    ///
    /// 两条路径，缺一不可：
    /// ① 解压前能拿到条目清单 → <see cref="CheckBeforeExtract"/> 一次算清单文件 / 文件数 / 总大小 /
    ///    展开比 / 目标盘空间；
    /// ② 拿不到清单（引擎拒绝列目录、加密包、流式读取）→ <b>不假装知道</b>：
    ///    放行但明确标注"改为运行时预算"，由 <see cref="CreateTracker"/> 在写入过程中累计拦截。
    ///
    /// ⚠ 边界：本类只做"上限"，不做"意图"。它拦不住所有压缩炸弹 ——
    /// 一个 1000 倍以内、总量也在上限内的包照样能吃掉不少磁盘，这是有意的取舍。
    /// </summary>
    public sealed class ResourceBudget
    {
        private readonly ResourceBudgetOptions _options;

        public ResourceBudget(ResourceBudgetOptions? options = null)
        {
            _options = options ?? ResourceBudgetOptions.Default;
        }

        /// <summary>
        /// 解压前估算。<paramref name="list"/> 可能为 null 或 Success=false（引擎给不出清单）：
        /// 这时只做"已知信息"的判断，不假装知道解压后会占多少。
        /// </summary>
        public BudgetCheckResult CheckBeforeExtract(ArchiveListResult? list, long archiveSizeBytes, string? targetDirectory)
        {
            long? freeSpaceBytes = null;

            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                /*
                 * 取盘可用空间只有一份实现（SpaceChecker），这里不重写一遍 DriveInfo 取法：
                 * 两处各写一遍，迟早在"路径还不存在""盘未就绪""相对路径"这些边界上给出不同答案。
                 * 取不到时是 null —— 不抛异常，也不当成"空间充足"。
                 */
                freeSpaceBytes = SpaceChecker.GetAvailableFreeSpace(targetDirectory);
            }

            /*
             * 规则 1：没有清单不等于"没问题"，也不等于"有问题"。
             * 大小 / 数量 / 展开比这三项依赖清单，清单未知就**不做判断**，交给运行时预算；
             * 但"目标盘已经放不下"是已知事实，与清单无关，该拦还是要拦
             * （设计.md §二十一：不能等写入失败后才提示）。
             */
            if (list == null || !list.Success)
            {
                string? unknownListSpaceShortage = CheckFreeSpace(freeSpaceBytes, 0);

                if (unknownListSpaceShortage != null)
                {
                    return new BudgetCheckResult
                    {
                        Allowed = false,
                        Reason = $"引擎未能提供条目清单（本应改为运行时预算），但{unknownListSpaceShortage}",
                        EstimatedTotalSize = 0,
                        ExpansionRatio = 0d,
                        FreeSpaceBytes = freeSpaceBytes
                    };
                }

                return new BudgetCheckResult
                {
                    Allowed = true,
                    Reason = "引擎未能提供条目清单，改为运行时预算：解压过程中按累计大小与文件数设限，超限即停",
                    EstimatedTotalSize = 0,
                    ExpansionRatio = 0d,
                    FreeSpaceBytes = freeSpaceBytes
                };
            }

            IReadOnlyList<ArchiveEntry>? entries = list.Entries;
            int entriesFileCount = 0;
            long entriesTotalSize = 0;
            long largestFileSize = 0;
            string largestFilePath = string.Empty;

            if (entries != null)
            {
                foreach (ArchiveEntry? entry in entries)
                {
                    // 目录不占输出字节数。entry 为 null 是调用方数据异常，
                    // 不在这里改判成超限（清单本身有问题应该由引擎层报错，不是预算层）。
                    if (entry == null || entry.IsDirectory)
                    {
                        continue;
                    }

                    // 负数是解析异常：按 0 计，但绝不用它冲减已累计的总量 ——
                    // 否则一个 -N 的条目就能把超限的包"洗白"。
                    long size = entry.Size > 0 ? entry.Size : 0L;

                    entriesFileCount++;
                    entriesTotalSize = SaturatingAdd(entriesTotalSize, size);

                    if (size > largestFileSize)
                    {
                        largestFileSize = size;
                        largestFilePath = entry.Path;
                    }
                }
            }

            /*
             * 引擎自报的数字与逐条累加出来的数字取更大的那个：
             * 两边不一致时按更保守的一侧判断，免得"解析器少报了几条"就把超限的包放过去。
             */
            int fileCount = Math.Max(list.FileCount, entriesFileCount);
            long totalSize = Math.Max(list.TotalUncompressedSize, entriesTotalSize);

            // 规则 3 先算出来：总大小超限时用它补一句"疑似压缩炸弹"，用户才知道该怎么处理。
            double expansionRatio = archiveSizeBytes > 0 ? (double)totalSize / archiveSizeBytes : 0d;

            // 规则 2a：单文件上限 —— 报出最大的那个文件，用户一眼知道是谁。
            if (largestFileSize > _options.MaxSingleFileSize)
            {
                return Reject(
                    $"单个文件解压后 {largestFileSize} 字节（{FormatSize(largestFileSize)}）超过单文件上限 " +
                    $"{_options.MaxSingleFileSize} 字节（{FormatSize(_options.MaxSingleFileSize)}）：{largestFilePath}",
                    totalSize,
                    expansionRatio,
                    freeSpaceBytes);
            }

            // 规则 2b：文件数上限。
            if (fileCount > _options.MaxFileCount)
            {
                return Reject(
                    $"归档内文件数 {fileCount} 超过上限 {_options.MaxFileCount}",
                    totalSize,
                    expansionRatio,
                    freeSpaceBytes);
            }

            // 规则 2c：总大小上限。
            if (totalSize > _options.MaxTotalSize)
            {
                string bombHint = archiveSizeBytes > 0 && expansionRatio > _options.MaxExpansionRatio
                    ? $"，展开比 {FormatRatio(expansionRatio)} 倍（上限 {FormatRatio(_options.MaxExpansionRatio)} 倍），疑似压缩炸弹"
                    : string.Empty;

                return Reject(
                    $"解压后总大小 {totalSize} 字节（{FormatSize(totalSize)}）超过上限 " +
                    $"{_options.MaxTotalSize} 字节（{FormatSize(_options.MaxTotalSize)}）{bombHint}",
                    totalSize,
                    expansionRatio,
                    freeSpaceBytes);
            }

            // 规则 3：展开比。压缩包体积未知（<=0）时比值记 0，**不做**这项判断（不知道就别猜）。
            if (archiveSizeBytes > 0 && expansionRatio > _options.MaxExpansionRatio)
            {
                return Reject(
                    $"展开比 {FormatRatio(expansionRatio)} 倍超过上限 {FormatRatio(_options.MaxExpansionRatio)} 倍，疑似压缩炸弹：" +
                    $"压缩包 {archiveSizeBytes} 字节（{FormatSize(archiveSizeBytes)}）→ 解压后约 {totalSize} 字节（{FormatSize(totalSize)}）",
                    totalSize,
                    expansionRatio,
                    freeSpaceBytes);
            }

            // 规则 4：目标盘空间。
            string? spaceShortage = CheckFreeSpace(freeSpaceBytes, totalSize);

            if (spaceShortage != null)
            {
                return Reject(spaceShortage, totalSize, expansionRatio, freeSpaceBytes);
            }

            string ratioText = archiveSizeBytes > 0
                ? $"，展开比 {FormatRatio(expansionRatio)} 倍"
                : "，压缩包体积未知，未判展开比";

            string spaceText = freeSpaceBytes.HasValue
                ? $"，目标盘可用 {freeSpaceBytes.Value} 字节（{FormatSize(freeSpaceBytes.Value)}）"
                : "，未取到目标盘可用空间（写入过程中仍需运行时预算兜底）";

            return new BudgetCheckResult
            {
                Allowed = true,
                Reason = $"预检通过：{fileCount} 个文件 / 约 {totalSize} 字节（{FormatSize(totalSize)}）{ratioText}{spaceText}",
                EstimatedTotalSize = totalSize,
                ExpansionRatio = expansionRatio,
                FreeSpaceBytes = freeSpaceBytes
            };
        }

        /// <summary>
        /// 运行时预算：解压前拿不到清单时靠它兜底 —— 引擎每写一个文件就 <see cref="BudgetTracker.Record"/> 一次，
        /// 返回非 null 就说明超限，调用方应当停下来并把这个原因展示给用户。
        ///
        /// ⚠ 它判不了展开比（那需要压缩包体积，且要在解压前算才有意义），也不管目标盘空间
        /// （那是 <see cref="CheckBeforeExtract"/> 的活）—— 这两项不在这里假装覆盖。
        /// </summary>
        public BudgetTracker CreateTracker()
        {
            return new BudgetTracker(_options);
        }

        private BudgetCheckResult Reject(string reason, long estimatedTotalSize, double expansionRatio, long? freeSpaceBytes)
        {
            return new BudgetCheckResult
            {
                Allowed = false,
                Reason = reason,
                EstimatedTotalSize = estimatedTotalSize,
                ExpansionRatio = expansionRatio,
                FreeSpaceBytes = freeSpaceBytes
            };
        }

        /// <summary>
        /// 空间判断：<c>可用 - 预计需要 &lt; 需要保留</c> 就拒绝。
        /// 取不到可用空间（null）时不判定 —— 不知道就不说"不够"，也不说"够"。
        /// </summary>
        private string? CheckFreeSpace(long? freeSpaceBytes, long estimatedTotalSize)
        {
            if (!freeSpaceBytes.HasValue)
            {
                return null;
            }

            long available = freeSpaceBytes.Value;
            long required = estimatedTotalSize > 0 ? estimatedTotalSize : 0L;

            // 两个数都是非负，减法不会溢出（最坏也只是到 -long.MaxValue）。
            if (available - required >= _options.MinFreeSpaceReserveBytes)
            {
                return null;
            }

            return $"目标盘可用空间不足：可用 {available} 字节（{FormatSize(available)}），" +
                   $"解压预计需要 {required} 字节（{FormatSize(required)}），" +
                   $"且需要保留 {_options.MinFreeSpaceReserveBytes} 字节（{FormatSize(_options.MinFreeSpaceReserveBytes)}）";
        }

        /// <summary>
        /// 饱和加法：回绕成负数等于把总大小上限整个废掉（溢出后 total 变小，检查全过），
        /// 所以这里宁可停在 long.MaxValue 上。
        /// </summary>
        private static long SaturatingAdd(long current, long delta)
        {
            if (delta <= 0)
            {
                return current;
            }

            return current > long.MaxValue - delta ? long.MaxValue : current + delta;
        }

        /// <summary>把字节数说成人话（"20 GiB"）。换算只用展示，判断一律用原始字节数。</summary>
        private static string FormatSize(long bytes)
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

        private static string FormatRatio(double ratio)
        {
            return ratio.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 解压过程中的运行时预算累计器。
    ///
    /// ⚠ <b>会被并发解压同时调用</b>（AGENTS.md §12 第 4 条允许并发解压），
    /// 所以两个计数器都用 <see cref="Interlocked"/> 更新：用 "读-改-写" 会让并发写入的计数偏小，
    /// 而计数偏小恰恰是预算最不能出的错（少算 = 放过超限的包）。
    /// </summary>
    public sealed class BudgetTracker
    {
        private readonly ResourceBudgetOptions _options;

        // long 的读写在 32 位进程里不是原子的，一律走 Interlocked / Volatile。
        private long _totalBytes;
        private int _fileCount;

        public BudgetTracker(ResourceBudgetOptions? options = null)
        {
            _options = options ?? ResourceBudgetOptions.Default;
        }

        public long TotalBytes => Interlocked.Read(ref _totalBytes);

        public int FileCount => Volatile.Read(ref _fileCount);

        /// <summary>
        /// 记一个文件。返回 null 表示仍在预算内；返回字符串表示超限原因（中文，可直接展示）。
        /// 调用方拿到原因后应当停止解压 —— 本方法只报告，不替调用方决定怎么停。
        /// </summary>
        public string? Record(long fileSize)
        {
            // 负数（引擎给不出大小、解析异常）按 0 计：不能让它把已累计的量冲减掉。
            long size = fileSize > 0 ? fileSize : 0L;

            long total = Interlocked.Add(ref _totalBytes, size);
            int count = Interlocked.Increment(ref _fileCount);

            /*
             * 判断顺序与解压前一致：单文件 → 文件数 → 总量，报出来的是最具体的那条。
             * 用"累加后的值"比较，所以一旦超限，后续每次调用都会继续返回原因，
             * 不会出现"超了一次、第二次又说没事"的抖动。
             */
            if (size > _options.MaxSingleFileSize)
            {
                return $"单个文件 {size} 字节（{FormatSize(size)}）超过单文件上限 " +
                       $"{_options.MaxSingleFileSize} 字节（{FormatSize(_options.MaxSingleFileSize)}）";
            }

            if (count > _options.MaxFileCount)
            {
                return $"文件数已达 {count}，超过上限 {_options.MaxFileCount}";
            }

            if (total > _options.MaxTotalSize)
            {
                return $"累计解压大小 {total} 字节（{FormatSize(total)}）超过总大小上限 " +
                       $"{_options.MaxTotalSize} 字节（{FormatSize(_options.MaxTotalSize)}）";
            }

            return null;
        }

        /// <summary>给人看的累计进度（进日志 / 报告）。</summary>
        public string Describe()
        {
            return $"累计 {FileCount} 个文件 / {TotalBytes} 字节";
        }

        /// <summary>与 <see cref="ResourceBudget"/> 用同一套换算，避免两处显示口径不一致。</summary>
        private static string FormatSize(long bytes)
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
}
