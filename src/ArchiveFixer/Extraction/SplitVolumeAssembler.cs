using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ArchiveFixer.Detection;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{
    /// <summary>分卷的"族" —— 只用来回答"容器里那一段和外面那一组是不是同一族"。</summary>
    public enum SplitVolumeFamily
    {
        /// <summary>认不出来（既不是 7z / RAR / ZIP 的头，名字也推不出族）。</summary>
        Unknown,

        /// <summary>7z 分卷（<c>set.7z.001</c> / <c>set.7z.002</c>…）。</summary>
        SevenZip,

        /// <summary>RAR 分卷（新式 <c>set.part1.rar</c>，老式 <c>set.rar</c> + <c>set.r00</c>）。</summary>
        Rar,

        /// <summary>ZIP 分卷（<c>set.zip</c> + <c>set.z01</c>，或 <c>set.zip.001</c> 这一套）。</summary>
        Zip
    }

    /// <summary>拼装计划里的一条：把哪个文件以什么名字接进工作区。</summary>
    public sealed class SplitVolumeAssemblyEntry
    {
        /// <summary>源文件（**只读**：硬链接/复制的来源，绝不改动、绝不删除）。</summary>
        public string SourcePath { get; init; } = string.Empty;

        /// <summary>接进工作区之后用的文件名（后续卷 = 它原来的名字；首卷 = 标准首卷名）。</summary>
        public string TargetName { get; init; } = string.Empty;

        /// <summary>源文件字节数（拼完要逐个核对，见 <see cref="SplitVolumeAssembler.TryAssemble"/>）。</summary>
        public long Length { get; init; }

        /// <summary>这一条是不是"容器里装着的第 1 卷"。</summary>
        public bool IsFirstVolume { get; init; }
    }

    /// <summary>
    /// 「容器里装的是分卷的第 1 卷、后续卷在容器外面」时的**拼装计划**（用户 2026-09-25 第 42 条，
    /// 他选的方案 A：做，但**默认关**，②页那个开关打开才走这条路）。
    ///
    /// <para>为什么需要它：引擎（7z / UnRAR）是按**文件名**在**同一个目录**里找同组的其他卷的。
    /// 容器里那一段首卷被抠出来之后躺在工作区、名字也不带卷号（<c>封面.zip</c>）——
    /// 引擎两个条件都不满足，于是报"分卷缺失"。要让两边凑上，就得在**我们自己的工作区**里
    /// 摆出一套"名字成套、同在一个目录"的卷。</para>
    ///
    /// <para>⛔ 拼装**不动用户的源文件**：首卷来自容器（派生数据），后续卷只是被"多起一个名字"
    /// （同盘硬链接，零字节复制）或复制一份（跨盘时没办法），源文件的内容/名字/位置一个字节都不变。</para>
    /// </summary>
    public sealed class SplitVolumeAssemblyPlan
    {
        /// <summary>能不能拼（false 时看 <see cref="Reason"/>，一个字都不许动）。</summary>
        public bool CanAssemble { get; init; }

        /// <summary>不能拼的原因 / 能拼时的一句说明（给人看的中文）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>拼到哪个目录（调用方给：任务工作区下的一个子目录）。</summary>
        public string AssemblyDirectory { get; init; } = string.Empty;

        /// <summary>标准首卷名（例如 <c>set.7z.001</c>）—— 交给引擎的就是这个文件。</summary>
        public string FirstVolumeName { get; init; } = string.Empty;

        /// <summary>要接进来的每一条（第 1 条是首卷，其余按卷序）。</summary>
        public IReadOnlyList<SplitVolumeAssemblyEntry> Entries { get; init; } = Array.Empty<SplitVolumeAssemblyEntry>();

        /// <summary>这一组后续卷的字节数合计（跨盘要复制的就是它们）。</summary>
        public long ContinuationBytes => Entries.Where(entry => !entry.IsFirstVolume).Sum(entry => entry.Length);
    }

    /// <summary>拼装的结果（失败也要能说清为什么 —— 失败**不改变**任务的结论）。</summary>
    public sealed class SplitVolumeAssemblyResult
    {
        public bool Success { get; init; }

        public string Reason { get; init; } = string.Empty;

        /// <summary>拼好之后交给引擎的那个文件（首卷）。</summary>
        public string FirstVolumePath { get; init; } = string.Empty;

        /// <summary>用硬链接接了几个（零字节复制）。</summary>
        public int HardLinkCount { get; init; }

        /// <summary>复制了几个。</summary>
        public int CopyCount { get; init; }

        /// <summary>复制了多少字节（硬链接的不算）。</summary>
        public long CopiedBytes { get; init; }

        /// <summary>日志里那几行（调用方原样写出去，口径只有这一处）。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 把"容器里的第 1 卷"与"容器外面的后续卷"接成一个**名字成套、同在一个目录**的分卷组
    /// （纯逻辑 + 文件系统操作，不引用 WPF；判据全部来自文件系统事实）。
    ///
    /// <para>四条纪律（写死在实现里，测试各有一条钉着）：</para>
    /// <list type="number">
    /// <item><description><b>只读源文件</b>：源文件只被 <c>CreateHardLinkW</c> / <c>File.Read</c> 使用，
    /// 没有任何一处 Write / Move / Delete 指向源路径；</description></item>
    /// <item><description><b>同盘硬链接优先</b>：同一块盘上"接名字"是零字节、几乎瞬时的；
    /// 跨盘才复制，而且**复制前先查空间**，不够就不做（照旧按今天这样报"分卷缺失"）；</description></item>
    /// <item><description><b>认不准就不动</b>：容器里那一段的魔数决定"族"，只接同一族的缺首卷组；
    /// 同族两组以上、或者族都认不出来且不止一组 → 一个字都不碰；</description></item>
    /// <item><description><b>拼完要核</b>：每个目标文件的大小必须与源文件一个字节不差，否则整次拼装判失败。</description></item>
    /// </list>
    /// </summary>
    public static class SplitVolumeAssembler
    {
        /// <summary>拼装目录名（任务工作区下的直属子目录；唯一常量，别处不许再写一遍）。</summary>
        public const string AssemblyDirectoryName = "volumes";

        /// <summary>
        /// 复制路线要留的余量（与"抠取内嵌归档"那一步同量级）。
        ///
        /// <para>为什么留：复制几个 5 GB 的卷时，盘正好在最后一卷写满是最难看的结果 ——
        /// 既没拼成、又在用户盘上留一堆半截文件。取不到可用空间时**不复制**（不敢说够）。</para>
        /// </summary>
        public const long CopyReserveBytes = 256L * 1024 * 1024;

        /// <summary>一次复制/接名字的缓冲区（64 KiB：够大又不占内存，与抠取那一步一个量级）。</summary>
        private const int CopyBufferSize = 64 * 1024;

        /// <summary>
        /// 按**内容魔数**认族（只读前 8 个字节）。
        ///
        /// <para>为什么用魔数而不是后缀：这一段的文件名是我们自己起的（<c>封面.zip</c>，见
        /// <c>ExtractionCoordinator.BuildEmbeddedArchivePath</c> 的说明），后缀说明不了任何事；
        /// 而它开头那几个字节是第一卷真正的头。</para>
        /// </summary>
        public static SplitVolumeFamily DetectFamilyByMagic(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return SplitVolumeFamily.Unknown;
            }

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                Span<byte> head = stackalloc byte[8];
                int read = stream.Read(head);

                if (read >= 6
                    && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC
                    && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
                {
                    return SplitVolumeFamily.SevenZip;
                }

                if (read >= 7
                    && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72
                    && head[3] == 0x21 && head[4] == 0x1A && head[5] == 0x07
                    && (head[6] == 0x00 || head[6] == 0x01))
                {
                    return SplitVolumeFamily.Rar;
                }

                if (read >= 4 && head[0] == 0x50 && head[1] == 0x4B
                    && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
                {
                    return SplitVolumeFamily.Zip;
                }

                return SplitVolumeFamily.Unknown;
            }
            catch
            {
                // 读不出来就是认不出（⛔ 不猜：认不出那一档的处置是"只在唯一一组时才接"）。
                return SplitVolumeFamily.Unknown;
            }
        }

        /// <summary>
        /// 由"标准首卷名"推出族（<c>set.7z.001</c> → 7z；<c>set.part1.rar</c> / <c>set.rar</c> → RAR；
        /// <c>set.zip</c> / <c>set.zip.001</c> → ZIP）。
        ///
        /// <para>⚠ 末段是**纯数字**（<c>archive.001</c>）时算 <see cref="SplitVolumeFamily.Unknown"/>：
        /// 那种命名本身不说明格式（通用分片也是这个名字，而通用分片拼起来**不是**解压的输入）。</para>
        /// </summary>
        public static SplitVolumeFamily FamilyOfFirstVolumeName(string? firstVolumeName)
        {
            if (string.IsNullOrWhiteSpace(firstVolumeName))
            {
                return SplitVolumeFamily.Unknown;
            }

            string name = Path.GetFileName(firstVolumeName).ToLowerInvariant();

            if (name.EndsWith(".rar", StringComparison.Ordinal))
            {
                return SplitVolumeFamily.Rar;
            }

            if (name.EndsWith(".zip", StringComparison.Ordinal))
            {
                return SplitVolumeFamily.Zip;
            }

            if (name.EndsWith(".7z.001", StringComparison.Ordinal))
            {
                return SplitVolumeFamily.SevenZip;
            }

            return SplitVolumeFamily.Unknown;
        }

        /// <summary>
        /// 算一份拼装计划（**不碰文件系统**，除了"文件在不在/多大"这类只读查询）。
        ///
        /// <para>调用方给的三样东西：容器里抠出来的那一段（<paramref name="firstVolumeSourcePath"/>）、
        /// 那个容器的**源目录**（后续卷就在那儿）、以及我们自己的拼装目录。</para>
        /// </summary>
        public static SplitVolumeAssemblyPlan Plan(
            string? firstVolumeSourcePath,
            SplitVolumeFamily firstVolumeFamily,
            string? sourceDirectory,
            string? assemblyDirectory,
            IEnumerable<string?>? fileNamesInSourceDirectory)
        {
            string refuse(string reason) => reason;

            if (string.IsNullOrWhiteSpace(firstVolumeSourcePath) || !File.Exists(firstVolumeSourcePath))
            {
                return Refuse(refuse("容器里那一段还没取出来（或已经不在工作区里了），没什么可拼的。"), assemblyDirectory);
            }

            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                return Refuse(refuse("容器所在的源目录读不到，无法确定后续卷在哪。"), assemblyDirectory);
            }

            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                return Refuse(refuse("没有给出拼装目录（程序内部的工作区目录）。"), assemblyDirectory);
            }

            /*
             * 拼装目录绝不许落在**用户的源目录**里（那是"往用户的盘上写东西"，与不变量 1 的精神冲突）。
             * 正常的调用方给的是任务工作区；这一条只是防止将来有人接错线。
             */
            if (IsUnderDirectory(assemblyDirectory, sourceDirectory))
            {
                return Refuse(refuse("拼装目录落在源目录里面（程序不该往那里写东西），不做。"), assemblyDirectory);
            }

            long firstVolumeLength;

            try
            {
                firstVolumeLength = new FileInfo(firstVolumeSourcePath).Length;
            }
            catch (Exception ex)
            {
                return Refuse(refuse($"读不到那一段的大小（{ex.Message}），不做。"), assemblyDirectory);
            }

            if (firstVolumeLength <= 0)
            {
                return Refuse(refuse("容器里那一段是 0 字节，拼起来也解不开。"), assemblyDirectory);
            }

            IReadOnlyList<OrphanVolumeSet> orphans = OrphanVolumeSetDetector.Find(fileNamesInSourceDirectory);

            if (orphans.Count == 0)
            {
                return Refuse(refuse("同目录里没有「缺首卷」的分卷组，不需要拼。"), assemblyDirectory);
            }

            /*
             * 族的过滤：容器里那一段的魔数说它是什么，就只接**同一族**的那一组。
             * 这一条正是"目录里分卷多也不要紧"的底气 —— 用户 2026-09-25 的原话：
             * "分卷多了不要紧，你只要对着 001 使用解压就行了"（引擎自己会找同组的其他卷）。
             * 认不出族（Unknown）时不靠族去筛，只在"全目录恰好一组"时才接。
             */
            List<OrphanVolumeSet> candidates = orphans
                .Where(orphan => firstVolumeFamily == SplitVolumeFamily.Unknown
                                 || FamilyOfFirstVolumeName(orphan.FirstVolumeName) == firstVolumeFamily)
                .ToList();

            if (candidates.Count == 0)
            {
                return Refuse(
                    refuse($"同目录里有 {orphans.Count} 组缺首卷的分卷，但**没有一族**与容器里那一段对得上"
                           + $"（容器里是 {DescribeFamily(firstVolumeFamily)}），不接。"),
                    assemblyDirectory);
            }

            if (candidates.Count > 1)
            {
                /*
                 * 同族还两组以上：我们**无法确定**容器里装的是哪一组（两组都缺首卷、格式也一样）。
                 * ⛔ 不猜 —— 接错组的后果是"用别人的后续卷去解自己的首卷"，那比不做坏得多。
                 */
                return Refuse(
                    refuse($"同目录里有 {candidates.Count} 组**同一族**且都缺首卷的分卷"
                           + $"（{string.Join("；", candidates.Select(candidate => candidate.Describe()))}），"
                           + "无法确定容器里装的是哪一组，不接。"),
                    assemblyDirectory);
            }

            OrphanVolumeSet target = candidates[0];
            var entries = new List<SplitVolumeAssemblyEntry>
            {
                new()
                {
                    SourcePath = firstVolumeSourcePath,
                    TargetName = target.FirstVolumeName,
                    Length = firstVolumeLength,
                    IsFirstVolume = true
                }
            };

            foreach (string continuation in target.ContinuationFileNames)
            {
                string path = Path.Combine(sourceDirectory, continuation);

                if (!File.Exists(path))
                {
                    return Refuse(refuse($"后续卷 {continuation} 已经不在了（刚才扫目录时还在），不做。"), assemblyDirectory);
                }

                long length;

                try
                {
                    length = new FileInfo(path).Length;
                }
                catch (Exception ex)
                {
                    return Refuse(refuse($"读不到 {continuation} 的大小（{ex.Message}），不做。"), assemblyDirectory);
                }

                if (length <= 0)
                {
                    return Refuse(refuse($"后续卷 {continuation} 是 0 字节，接上去也解不开。"), assemblyDirectory);
                }

                entries.Add(new SplitVolumeAssemblyEntry
                {
                    SourcePath = path,
                    TargetName = continuation,
                    Length = length,
                    IsFirstVolume = false
                });
            }

            return new SplitVolumeAssemblyPlan
            {
                CanAssemble = true,
                Reason = $"容器里那一段是 {DescribeFamily(firstVolumeFamily)} 的第 1 卷，"
                         + $"外面这一组（{target.Describe()}）与它同族且唯一，可以接。",
                AssemblyDirectory = assemblyDirectory,
                FirstVolumeName = target.FirstVolumeName,
                Entries = entries
            };
        }

        /// <summary>
        /// 按计划拼：**同盘硬链接优先**，跨盘才复制（复制前先查空间）。
        ///
        /// <para>⛔ 源文件只读：整段实现里没有任何一处对源路径做写/改名/删除。
        /// 目标目录必须**已经不存在或为空** —— 本方法不删任何东西（删目录的容器检查在调用方）。</para>
        /// </summary>
        /// <param name="availableSpace">查可用空间的实现（默认 <see cref="SpaceChecker"/>；测试注入用）。</param>
        /// <param name="progress">复制进度回调（已复制字节数；调用方据此写日志）。</param>
        public static SplitVolumeAssemblyResult TryAssemble(
            SplitVolumeAssemblyPlan? plan,
            Func<string, long?>? availableSpace = null,
            Action<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (plan == null || !plan.CanAssemble || plan.Entries.Count == 0)
            {
                return Failed(plan?.Reason ?? "没有可执行的拼装计划。");
            }

            string directory = plan.AssemblyDirectory;

            try
            {
                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    // ⛔ 不删不覆盖：目录里有东西说明不是一次干净的拼装（残留 / 接错线），直接不做。
                    return Failed($"拼装目录里已经有东西了（{directory}），不做（程序不删自己没造过的东西）。");
                }

                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                return Failed($"建不出拼装目录（{ex.Message}），不做。");
            }

            /*
             * 先算"必须复制多少"：同盘的条目走硬链接（零字节），跨盘的只能复制 ——
             * 空间不够时**一个字都不写**（连硬链接也不接，省得留下半套卷让下一次看着像"已经拼过"）。
             */
            var mustCopy = new List<SplitVolumeAssemblyEntry>();

            foreach (SplitVolumeAssemblyEntry entry in plan.Entries)
            {
                if (!CanHardLink(entry.SourcePath, directory))
                {
                    mustCopy.Add(entry);
                }
            }

            long copyBytes = mustCopy.Sum(entry => entry.Length);

            if (copyBytes > 0)
            {
                Func<string, long?> space = availableSpace ?? SpaceChecker.GetAvailableFreeSpace;
                long? free = space(directory);

                if (!free.HasValue)
                {
                    return Failed("跨盘拼装需要复制后续卷，但读不到目标盘的可用空间 —— 不敢说够，所以不复制。");
                }

                if (free.Value < copyBytes + CopyReserveBytes)
                {
                    return Failed(
                        $"跨盘拼装需要复制 {FormatSize(copyBytes)} 的后续卷，"
                        + $"目标盘只剩 {FormatSize(free.Value)}（还要留 {FormatSize(CopyReserveBytes)} 余量），空间不够，不做。");
                }
            }

            int hardLinks = 0;
            int copies = 0;
            long copied = 0;
            long doneBytes = 0;
            var logLines = new List<string>();

            foreach (SplitVolumeAssemblyEntry entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string target = Path.Combine(directory, entry.TargetName);

                try
                {
                    if (CanHardLink(entry.SourcePath, directory) && TryCreateHardLink(target, entry.SourcePath))
                    {
                        hardLinks++;
                    }
                    else
                    {
                        long alreadyDone = doneBytes;

                        CopyFile(
                            entry.SourcePath,
                            target,
                            cancellationToken,
                            copiedInThisFile => progress?.Invoke(alreadyDone + copiedInThisFile));

                        copies++;
                        copied += entry.Length;
                    }

                    doneBytes += entry.Length;

                    /*
                     * 拼完要核：目标文件的大小必须与源文件一个字节不差。
                     * 硬链接正常时天然相等；复制路线如果被中途掐断、或盘写满，这里就能拦下来 ——
                     * ⛔ 绝不把"看着像拼好了"的半套卷交给引擎（那会解出垃圾或报一个莫名其妙的错）。
                     */
                    long targetLength = new FileInfo(target).Length;

                    if (targetLength != entry.Length)
                    {
                        return Failed(
                            $"{entry.TargetName} 接完只有 {targetLength} 字节（原文件 {entry.Length} 字节），"
                            + "拼装没成功，不做。");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Failed($"接 {entry.TargetName} 失败（{ex.Message}），不做。");
                }
            }

            string firstVolumePath = Path.Combine(directory, plan.FirstVolumeName);

            if (!File.Exists(firstVolumePath))
            {
                return Failed("拼完之后标准首卷名那个文件不在（程序内部不一致），不做。");
            }

            logLines.Add(
                $"{plan.FirstVolumeName}（第 1 卷来自容器，标准名）＋ 后续卷 {plan.Entries.Count - 1} 个 —— "
                + $"硬链接 {hardLinks} 个 / 复制 {copies} 个（复制 {FormatSize(copied)}），全部落在工作区 {directory}。");

            return new SplitVolumeAssemblyResult
            {
                Success = true,
                Reason = "拼装完成。",
                FirstVolumePath = firstVolumePath,
                HardLinkCount = hardLinks,
                CopyCount = copies,
                CopiedBytes = copied,
                LogLines = logLines
            };
        }

        /// <summary>给用户/日志看的族名。</summary>
        public static string DescribeFamily(SplitVolumeFamily family) => family switch
        {
            SplitVolumeFamily.SevenZip => "7z 分卷",
            SplitVolumeFamily.Rar => "RAR 分卷",
            SplitVolumeFamily.Zip => "ZIP 分卷",
            _ => "认不出的格式"
        };

        /// <summary>
        /// **测试用**：true = 一律走复制（把"同盘硬链接"这条路关掉，模拟跨盘 / 硬链接不可用）。
        ///
        /// <para>为什么要留这个口子：硬链接能不能做取决于两块盘是不是同一个卷 —— 测试里没法
        /// 凭空造一块盘出来，而"跨盘才复制、复制前先查空间"正是这一档最需要钉住的代价。
        /// ⛔ 产品代码里**没有任何入口**会把它置 true（②页那个开关不会碰它）。</para>
        /// </summary>
        internal static bool ForceCopyForTests { get; set; }

        /// <summary>硬链接只能同盘：两个路径的盘根相同才试（取不到盘根就当作不能）。</summary>
        private static bool CanHardLink(string sourcePath, string targetDirectory)
        {
            if (ForceCopyForTests || !OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                string? sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
                string? targetRoot = Path.GetPathRoot(Path.GetFullPath(targetDirectory));

                return !string.IsNullOrEmpty(sourceRoot)
                       && !string.IsNullOrEmpty(targetRoot)
                       && string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCreateHardLink(string linkPath, string existingPath)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                // ⛔ 只"多起一个名字"：不复制字节、不改动 existingPath（删掉 linkPath 也不会删掉它）。
                return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        private static void CopyFile(
            string sourcePath,
            string targetPath,
            CancellationToken cancellationToken,
            Action<long>? reportBytes)
        {
            const int chunk = CopyBufferSize;

            byte[] buffer = new byte[chunk];

            using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunk, FileOptions.SequentialScan);

            using var target = new FileStream(
                targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, chunk, FileOptions.SequentialScan);

            long written = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = source.Read(buffer, 0, chunk);

                if (read <= 0)
                {
                    break;
                }

                target.Write(buffer, 0, read);

                written += read;
                reportBytes?.Invoke(written);
            }

            target.Flush();
        }

        /// <summary>路径 <paramref name="candidate"/> 是不是在 <paramref name="directory"/> **里面**。</summary>
        private static bool IsUnderDirectory(string candidate, string directory)
        {
            try
            {
                string parent = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
                string child = Path.GetFullPath(candidate);

                return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 路径不合法 → 当作"在里面"（保守：宁可不做）。
                return true;
            }
        }

        private static SplitVolumeAssemblyPlan Refuse(string reason, string? assemblyDirectory) => new()
        {
            CanAssemble = false,
            Reason = reason,
            AssemblyDirectory = assemblyDirectory ?? string.Empty
        };

        private static SplitVolumeAssemblyResult Failed(string reason) => new()
        {
            Success = false,
            Reason = reason
        };

        private static string FormatSize(long bytes) => TaskSpaceEstimate.FormatSize(bytes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}
