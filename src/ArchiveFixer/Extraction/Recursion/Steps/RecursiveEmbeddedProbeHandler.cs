using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 递归核心的**内嵌归档探针 / 尾部归档直读 / 双面文件补救**，以及原本和它住在同一个分片里的
/// 探针辅助判据（落点越界、停因映射、工作区候选池）。原 <c>RecursiveExtractor.Probe.Embedded.cs</c>
/// 的全部实现。
///
/// <para>从 <see cref="RecursiveExtractor"/> 按职责搬出的协作类，只持有宿主
/// <see cref="RecursiveExtractor"/>。⛔ 判据、日志顺序、调用顺序与搬出前逐字一致；
/// 宿主那一分片只留同签名薄转发与最小 internal 访问器。</para>
/// </summary>
internal sealed class RecursiveEmbeddedProbeHandler
{
    private readonly RecursiveExtractor _host;

    internal RecursiveEmbeddedProbeHandler(RecursiveExtractor host)
    {
        _host = host;
    }

    /// <summary>
    /// **尾部归档里只有一个原样存（stored）的条目 ⇒ 直接按偏移把那一段取出来。**
    ///
    /// <para><b>真机现场</b>（`ArchiveFixer-本次操作_20261005_134118.txt`，一键处理 21 分 34 秒）：
    /// 第 1 层三个双面视频，每个都先白试一次引擎（`Cannot open the file as archive`，0 秒），
    /// 再把尾部归档**整份抠成副本**（78 / 48 / 83 秒），然后让 7-Zip 从那个副本里**再解一遍**
    /// 那唯一一个条目（88 / 48 / 92 秒）—— 同一份字节写了两遍、读了两遍，约 3.8 分钟纯浪费。
    /// 而那段归档里其实只有一个**原样存**的条目：它的字节就是源文件里一段连续区间，
    /// 直接取出来就是产物（一次写、一次读、**一次引擎都不调**）。</para>
    ///
    /// <para><b>⛔ 只在能证明的前提下走这条路</b>（任何一条不成立都返回 <c>null</c> ⇒
    /// 原样退回今天那条"抠副本 + 引擎"的路，行为与加这一段之前逐字相同）：</para>
    /// <list type="number">
    /// <item><description>识别阶段那条只读判据成立：尾部有**自洽的** ZIP（<see cref="EmbeddedArchiveDetector"/>，
    /// 与识别阶段同一个检测器）、<c>Offset &gt; 0</c>、不是跨盘 ZIP 的末片；</description></item>
    /// <item><description><see cref="EmbeddedZipStreamExtractor.Probe"/> 说这份内嵌 ZIP 可直读
    /// （自洽 + 每个本地头是 <c>PK\x03\x04</c> + 方法 ∈ {stored, deflate} + 无加密位 + ZIP64 占位符取得到真值
    /// + 数据区不越过中央目录 + 条目名过既有的落点校验）；</description></item>
    /// <item><description>清单里**恰好一个**条目、不是目录、**没有加密**、压缩方法是 **stored（0）**、
    /// 且 <c>Size == CompressedSize</c>（原样存）—— 与"抠出来再让引擎解"得到的东西逐字节相同；</description></item>
    /// <item><description>条目大小不越过本层的单文件上限（越过的交给既有那道闸门照旧报，⛔ 不在这里抢着判）。</description></item>
    /// </list>
    ///
    /// <para><b>⚠ 与老路唯一的行为差别</b>（如实记在这里）：<see cref="EmbeddedZipStreamExtractor"/>
    /// 不校验 stored 条目的 CRC32，所以"结构自洽但字节坏了"的包在老路上会被 7-Zip 报 CRC 错误、
    /// 在这里会照旧成功。判据收得这么窄（单条目 + 原样存 + 无加密）就是为了把这一档压到最小。</para>
    ///
    /// <para>⛔ 成功时**不写 ERROR**：用户看到的必须是"为什么引擎打不开 + 我们换了什么办法"，
    /// 而不是一句会被读成"包坏了"的失败。</para>
    /// </summary>
    internal async Task<LayerOutcome?> TryTakeSingleStoredEntryAsync(
        WorkItem item,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        string layerLabel = RecursiveExtractor.DescribeProbeLabelForEmbeddedProbe(item);

        try
        {
            EmbeddedArchiveInfo info = DetectEmbeddedArchive(item.ArchivePath);

            if (!info.Found || info.IsVolumePart || info.Offset <= 0)
            {
                return null;
            }

            /*
             * ⛔ 传 passwords: null —— 探针阶段不猜密码：加密的（AES）内嵌包会返回"不支持"
             * 这个**回落信号**，于是原样走"抠副本 + 引擎"那条路（那时密码候选由既有的候选循环管）。
             */
            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                item.ArchivePath,
                info.Offset,
                info.ArchiveEnd,
                item.Layer.OutputPath,
                passwords: null,
                cancellationToken);

            if (!probe.Supported || probe.List == null || probe.Entries.Count != 1)
            {
                return null;
            }

            EmbeddedZipEntry entry = probe.Entries[0];

            if (entry.IsDirectory
                || entry.IsEncrypted
                || entry.Method != 0
                || entry.Size <= 0
                || entry.Size != entry.CompressedSize
                || string.IsNullOrWhiteSpace(entry.RelativePath))
            {
                return null;
            }

            // 单文件上限：越过的交给既有那道闸门（它的文案与判据只有一处，⛔ 不在这里抢着判）。
            if (entry.Size > _host.Limits.MaxSingleFileSize)
            {
                return null;
            }

            string entryDescription = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "{0}（{1}）",
                entry.Name,
                TaskSpaceEstimate.FormatSize(entry.Size));

            _host.Log(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.InnerEmbeddedSingleEntryTakeFormat,
                    layerLabel,
                    info.Offset,
                    entryDescription));

            /*
             * 界面也得有动静：这一段真机上是 48–83 秒的纯读写，引擎一个字都不报
             * （用户 2026-10-05：那几十秒日志里一行进度都没有）。
             */
            progress?.Report(new ArchiveProgress
            {
                Percent = 0,
                CurrentEntry = $"第 {item.Depth} 层：正在按偏移取出「{entry.Name}」"
            });

            EmbeddedZipExtractResult take = EmbeddedZipStreamExtractor.Extract(
                item.ArchivePath,
                info.Offset,
                info.ArchiveEnd,
                item.Layer.OutputPath,
                progress: null,
                cancellationToken,
                budgetOptions: new ResourceBudgetOptions
                {
                    MaxSingleFileSize = _host.Limits.MaxSingleFileSize,
                    MaxTotalSize = _host.Limits.MaxTotalSize,
                    MaxFileCount = _host.Limits.MaxTotalFiles
                },
                passwords: null);

            if (!take.Success || take.FileCount != 1 || take.WrittenBytes != entry.Size)
            {
                /*
                 * 没取成（或取出来的与清单对不上）⇒ 如实写一行 WARN 后**退回老路**：
                 * 半成品由直读器自己删干净（它保证"失败不留半成品"），接着走的
                 * "抠副本 + 引擎"那条路开头还会把这一层的产物目录整份重置一次。
                 */
                _host.Log(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.InnerEmbeddedSingleEntryTakeFailedFormat,
                        layerLabel,
                        string.IsNullOrWhiteSpace(take.Message) ? "取出来的字节数与清单对不上" : take.Message));

                return null;
            }

            /*
             * 可疑条目提示（与既有那一处**同一个出口**）：直读这条路跳过了引擎列目录那一段，
             * 但"这个包里有什么"的结论必须照旧回传一次 —— 否则同一件事在两条路上只有一条会提示。
             */
            _host.DangerousEntriesReported?.Invoke(
                ViewModels.ExtractionCoordinator.AnalyzeDangerousEntries(
                    probe.List.Entries,
                    _host.ReportDangerousEntries));

            return await _host.FinishSuccessfulLayerAsync(
                    item,
                    new ArchiveOperationResult
                    {
                        Success = true,
                        Status = StatusText.ExtractSuccess,
                        EngineDisplayName = EmbeddedZipStreamExtractor.ReaderDisplayName
                    },
                    LayerManifest.From(probe.List),
                    succeededPassword: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消照旧往上抛（不变量 6：取消不得显示成成功）。
            throw;
        }
        catch (Exception ex)
        {
            /*
             * 这一档是**加法**：判据里任何意外（读不到源文件、探测内部异常…）都只能得出
             * "这条路走不通"，⛔ 绝不能让一条新的近路把本来能解开的包挡下来。
             */
            _host.Log(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.InnerEmbeddedSingleEntryTakeFailedFormat,
                    layerLabel,
                    ex.Message));

            return null;
        }
    }

    /// <summary>
    /// **双面文件/媒体前缀的统一读法**（用户 2026-10-22 口径：7z、RAR 要和 ZIP 一样）。
    ///
    /// <para>先问既有那份唯一实现（ZIP 的 <see cref="EmbeddedArchiveDetector"/>，EOCD 偏移自洽）；
    /// 它判不出时再问 <see cref="EmbeddedArchiveLocator"/>（7z 两段 CRC / RAR4 主头 CRC + 媒体锚点）。
    /// 命中就**合成同一份 <see cref="EmbeddedArchiveInfo"/>**，于是"抠副本 + 重跑引擎"那条既有执行体
    /// 一行都不用改（⛔ 不另写第二套抠取/重试）。</para>
    /// </summary>
    internal static EmbeddedArchiveInfo DetectEmbeddedArchive(string archivePath)
    {
        EmbeddedArchiveInfo zip = EmbeddedArchiveDetector.Detect(archivePath);

        if (zip.Found)
        {
            return zip;
        }

        if (EmbeddedArchiveLocator.TryLocate(archivePath) is not { } hit)
        {
            return zip;
        }

        long fileLength;

        try
        {
            fileLength = new FileInfo(archivePath).Length;
        }
        catch
        {
            return zip;
        }

        long end = hit.Length > 0 ? Math.Min(hit.Offset + hit.Length, fileLength) : fileLength;

        if (hit.Offset <= 0 || end <= hit.Offset)
        {
            return zip;
        }

        string extension = hit.Format switch
        {
            VolumeContentFormat.SevenZip => ".7z",
            VolumeContentFormat.Rar => ".rar",
            _ => ".zip"
        };

        return new EmbeddedArchiveInfo
        {
            Found = true,
            Offset = hit.Offset,
            ArchiveEnd = end,
            ArchiveLength = end - hit.Offset,
            Format = extension == ".7z" ? "7Z" : extension == ".rar" ? "RAR" : "ZIP",
            SuggestedExtension = extension,
            Message = hit.Evidence
        };
    }
    /// <summary>
    /// 只读判据：这个文件是不是「双面文件」（尾部藏着**自洽的** ZIP，而且不是跨盘 ZIP 的末片）。
    ///
    /// <para>与补救那一处**同一个检测器**（<see cref="EmbeddedArchiveDetector"/>，⛔ 不另写一份判据）；
    /// 它只读文件尾部那段（默认 256 KB），不建目录、不改名字、不调引擎。</para>
    /// </summary>
    internal static bool IsDoubleFacedEmbeddedArchive(string archivePath)
    {
        EmbeddedArchiveInfo info = DetectEmbeddedArchive(archivePath);

        return info.Found && !info.IsVolumePart && info.Offset > 0;
    }

    /// <summary>
    /// 引擎回过"这根本不是归档"之后的一次补救：这个内层包是不是**双面文件**
    /// （真视频 / 图片 + 尾部一整个 ZIP）？是的话按偏移抠出副本、带副本重跑这一层。
    ///
    /// <para><b>为什么必须有这一步</b>（用户 2026-10-04 真机原话：「这么简单的操作，密码也是对的，
    /// 怎么解压不了」）：<c>HK.7z.001</c> 第 0 层正常解出 3 个 4K 视频（7.11 GiB），第 1 层探到
    /// <c>4K (11)_2.mp4</c> 是内层包，把它**原样**交给 7-Zip ⇒ <c>Cannot open the file as archive</c>
    /// ⇒ 整条链判"部分完成" ⇒ 已经解出来的 7.11 GiB 一个字节都不发布、工作区整份删掉（白跑 14 分钟）。
    /// 那个 mp4 是真视频 + 尾部一个完整 ZIP，而 <b>7-Zip 只在前面垫的数据 ≤ 8 MiB 时才容忍这种整体偏移</b>
    /// （实测边界见 <see cref="EmbeddedArchiveCarver"/> 的类注释）—— 单层路径早就接了这一档
    /// （<c>ExtractionCoordinator</c> 里那句"仍按偏移取出内嵌归档"），**递归这条路一直没接**；
    /// 而 2026-10-04 起出厂默认档正是"展开所有分支"，于是它成了默认路径。</para>
    ///
    /// <para>判据全部转调既有出口：是不是双面文件问 <see cref="EmbeddedArchiveDetector"/>
    /// （与识别阶段同一个检测器），抠取用 <see cref="EmbeddedArchiveCarver"/>（与单层路径同一个执行体）。
    /// ⛔ 判不出 / 抠不动 ⇒ 返回 <c>null</c>，调用方照旧按失败处置（口径一个字不改）；
    /// ⛔ 抠出来的只是**副本**（落在这一层的 <c>carved\</c> 兄弟目录里，不在产物目录里），原文件一个字节都不动。</para>
    /// </summary>
    internal async Task<LayerOutcome?> RetryWithCarvedEmbeddedArchiveAsync(
        WorkItem item,
        CancellationToken cancellationToken,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled)
    {
        string layerLabel = RecursiveExtractor.DescribeProbeLabelForEmbeddedProbe(item);

        EmbeddedArchiveInfo info = DetectEmbeddedArchive(item.ArchivePath);

        /*
         * 不是双面文件 ⇒ 什么都不做。
         * · `IsVolumePart`（跨盘 ZIP 的最后一片）也在这里排除：它没有"可抠出来的独立区间"，
         *   抠了只会得到一个解不开的半套。
         */
        if (!info.Found || info.IsVolumePart || info.Offset <= 0)
        {
            return null;
        }

        string layerDirectory = Path.GetDirectoryName(item.Layer.OutputPath) ?? item.Layer.OutputPath;
        string carveDirectory = Path.Combine(layerDirectory, RecursiveExtractor.CarveDirectoryName);

        try
        {
            Directory.CreateDirectory(carveDirectory);
        }
        catch
        {
            // 建不出目录（权限 / 盘）⇒ 补救做不了，按原样失败（绝不因此改结论）。
            return null;
        }

        /*
         * ===== 抠取这一段的进度（用户 2026-10-05 真机）=====
         *
         * 现场：第 1 层三个双面视频的抠取分别跑了 78 / 48 / 83 秒，日志里**一行都没有** ——
         * 上一行是"开始解压，密码候选 1/10"，下一行就是"已按偏移取出来……再解一次"，
         * 中间那几十秒读起来像卡死。⛔ 不报百分比（抠取没有"第几个条目"这种语义），
         * 只说清在做什么、大概多少量。
         */
        long carveBytes = info.ArchiveLength > 0
            ? info.ArchiveLength
            : Math.Max(0, new FileInfo(item.ArchivePath).Length - info.Offset);

        _host.Log(
            "INFO",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.InnerEmbeddedCarveProgressFormat,
                layerLabel,
                TaskSpaceEstimate.FormatSize(carveBytes)));

        CarveResult carve = EmbeddedArchiveCarver.Carve(
            item.ArchivePath,
            info.Offset,
            Path.Combine(carveDirectory, Path.GetFileName(item.ArchivePath)),
            info.ArchiveEnd,
            progress: null,
            cancellationToken);

        if (!carve.Success)
        {
            _host.Log(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.InnerEmbeddedCarveFailedFormat,
                    layerLabel,
                    carve.Message));

            return null;
        }

        _host.Log(
            "INFO",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.InnerEmbeddedCarveRetryFormat,
                layerLabel,
                info.Offset,
                carve.BytesWritten,
                info.EntryCount));

        LayerOutcome retried = await _host.ExtractLayerAsyncForEmbeddedProbe(
                item,
                cancellationToken,
                progress,
                stalled,
                carve.OutputPath)
            .ConfigureAwait(false);

        /*
         * 重解成功 ⇒ 抠出来的副本用完就删（它可能有几个 GB，成功路径上不该留）。
         * 失败 ⇒ 留着：③页「失败时保留中间产物」打开时，那份副本是排查"到底抠对没抠对"的直接证据。
         */
        if (retried.Report is { Success: true })
        {
            TryDeleteDirectoryQuietly(carveDirectory);
        }

        return retried;
    }

    /// <summary>
    /// 引擎调用之前的那一道：**跨层收卷**（把同组散在别的层产物目录里的卷收进入口那一层），
    /// 以及收不到一起时的**免试**判定。
    ///
    /// <para>返回 false = 这一组凑不齐、而且有硬证据 ⇒ **一次引擎都不许调**，
    /// <paramref name="missingVolumeMessage"/> 就是那句"缺哪几片"（写日志 + 当层结论）。</para>
    ///
    /// <para>⛔ 任何意外（列目录失败、判据内部异常）一律返回 true：这一档是**加法**，
    /// 判不出来时必须退回"照旧让引擎去判"，⛔ 绝不能让一个新的判据把本来能解的包挡下来。</para>
    /// </summary>
    /// <summary>
    /// **卡说这一份就是首卷 ⇒ 返回它**（用户 2026-10-17 总则：底层识别优先）。
    /// 判不出（手上是中间卷 / 卡读不出来）⇒ null，调用方照旧按名字找。
    /// ⛔ 只读：卡是 `FileNature.Inspect` 拍的，不碰任何文件。
    /// </summary>
    private static string? CardEntryOf(string archivePath)
    {
        try
        {
            ArchiveFixer.Detection.CardVolumeGroupReading reading =
                ArchiveFixer.Detection.VolumePiecesFromCards.Read(
                    new[] { ArchiveFixer.Detection.FileNature.Inspect(archivePath) });

            return reading.EntryPath.Length > 0 ? reading.EntryPath : null;
        }
        catch
        {
            return null;
        }
    }


    /// <summary>
    /// 这条链**各层产物目录的直接子文件**（跨层收卷的候选池）。
    ///
    /// <para>⛔ 只这一处枚举：<c>layer-NNN\output</c> 的**直接**子文件 —— 不递归、不含
    /// <c>carved</c>（它挂在层目录下、是产物目录的兄弟）、不碰用户源目录（源包不在工作区里）。</para>
    /// </summary>
    internal static List<VolumeCandidate> EnumerateWorkspacePool(ExtractionWorkspace? workspace)
    {
        var pool = new List<VolumeCandidate>();

        if (workspace == null)
        {
            // 拿不到工作区 ⇒ 空池（只按入口那一层判；⛔ 不替用户满盘找文件）。
            return pool;
        }

        foreach (WorkspaceLayer layer in workspace.Layers)
        {
            pool.AddRange(VolumeContentInference.EnumerateCandidatesIn(layer.OutputPath));
        }

        return pool;
    }

    /// <summary>
    /// 免试那一句"缺哪几片"：能点名的逐个点名（名字上的洞），点不了名的**如实说点不了**
    /// （命名里没有"共几卷"这个信息，⛔ 不编），再带上字节数那条证据与"为什么没收"。
    /// </summary>
    private static string BuildMissingVolumeMessage(
        string archivePath,
        string layerLabel,
        VolumeGatherPlanner.CrossLayerVolumeGather decision)
    {
        string names = decision.MissingNames.Count > 0
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.CrossLayerGatherMissingNamesFormat,
                string.Join("、", decision.MissingNames))
            : StatusText.CrossLayerGatherNoMissingNames;

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.CrossLayerGatherSkipTrialFormat,
            layerLabel,
            Path.GetFileName(archivePath),
            names,
            decision.Detail);
    }

    /// <summary>删一个目录，删不掉就算了（它只是工作区里的副本，不影响任何结论）。</summary>
    private static void TryDeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 删不掉只是工作区里多占一份副本，工作区收尾时会一起清掉。
        }
    }

    internal static long ResolveProbeEntrySize(ArchiveListResult? listed, string probeEntry)
    {
        if (listed?.Entries == null)
        {
            return 0;
        }

        foreach (ArchiveEntry? entry in listed.Entries)
        {
            if (entry != null && string.Equals(entry.Path, probeEntry, StringComparison.Ordinal))
            {
                return entry.Size;
            }
        }

        return 0;
    }

    /// 返回 null = 都老实待在该待的地方；否则返回第一条越界说明（给用户看的）。
    ///
    /// 判两件事：
    /// ① 路径规范化后必须落在产物目录之内（`..` / 绝对路径 / 盘符这类一眼可见的越界）；
    /// ② 产物里出现**目录联接点 / 符号链接**也算越界 —— 它的名字在目录里，写进去的内容却在别处。
    ///    （`OutputVerifier.Measure` 出于"不跟随链接"的考虑会跳过它们，所以这一条只能在这里拦。）
    ///
    /// ⚠ <b>能力边界</b>：这个方法只看得见产物目录**里面**的东西。
    /// 绕过我们直接写到别的目录去的引擎行为它发现不了 —— 那种情况靠
    /// <see cref="CheckEntriesBeforeExtractAsync"/> 的第一道预检拦。两道一起才是不变量 4。
    /// </summary>
    internal static string? FindLandingViolation(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return null;
        }

        var pending = new Stack<string>();
        pending.Push(outputRoot);

        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(current);
            }
            catch
            {
                // 读不了就跳过；漏看一个目录只是少查一层，不构成"越界"结论。
                continue;
            }

            foreach (string entry in entries)
            {
                if (!ArchivePathGuard.IsInsideRoot(outputRoot, entry, out string reason))
                {
                    return $"{entry}（{reason}）";
                }

                if (!RecursiveExtractor.TryGetAttributesForEmbeddedProbe(entry, out FileAttributes attributes))
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return $"{entry}（是目录联接点或符号链接：它在产物目录里的名字看不出真实落点，可能把内容写到目录之外）";
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 引擎给的错误分类 → 递归停因。
    ///
    /// 这里刻意**按引擎的分类枚举**（DetectedErrorType）而不是按中文状态串来判：
    /// 中文文案是给用户看的，随时可能改；用它做控制流会让"改一句提示"变成"改行为"。
    /// </summary>
    internal static RecursionStopReason MapEngineErrorToStopReason(ArchiveOperationResult result)
    {
        return result.DetectedErrorType switch
        {
            "CorruptedArchive" => RecursionStopReason.Corrupted,
            "WrongPassword" => RecursionStopReason.WrongPassword,
            "NeedPassword" => RecursionStopReason.WrongPassword,
            "Cancelled" => RecursionStopReason.UserCancelled,
            "UnsafePath" => RecursionStopReason.UnsafeEntry,

            /*
             * 写不下盘（引擎报磁盘空间不足）单独成一档：它与"引擎解不开"要用户做的事完全不同
             * （清空间 / 换盘 vs 查包 / 查引擎）。判据只读引擎的结构化错误码，⛔ 不比中文。
             */
            EngineErrorTypes.NoDiskSpace => RecursionStopReason.DiskSpaceInsufficient,

            /*
             * 分卷缺失（2026-10-05 真机第八批当场逮到）：引擎的结构化错误类型里**本来就有**
             * `MissingVolume`，可这份映射漏了它 ⇒ 掉进 `_ => EngineFailed`，于是同一件事
             * 在相邻两行里有两种说法：上一层那行写「分卷缺失，分卷压缩包缺少必要分卷」，
             * 下一行却写「已完成 2 层，停在第 3 层，原因：引擎操作失败」—— 用户会去查包、
             * 换引擎，而不是去把缺的那几片补上。⛔ 判据只读结构化错误码，不比中文。
             */
            EngineErrorTypes.VolumeMissing => RecursionStopReason.MissingVolume,

            _ => RecursionStopReason.EngineFailed
        };
    }

    /// <summary>
    /// 判定"全都没解开"时到底是密码错误还是达到尝试上限（规则 5 与规则 6 的分界）。
    /// </summary>
    internal static RecursionStopReason ResolvePasswordStopReason(
        int candidateCount,
        int attempts,
        ArchiveOperationResult? lastFailure,
        bool triedAny)
    {
        if (lastFailure != null && lastFailure.IsCorrupted)
        {
            return RecursionStopReason.Corrupted;
        }

        /*
         * 两义那一档：引擎自己说"密码可能不对、也可能数据坏了"。
         * ⛔ 不许折成上面那一档（"已损坏"= 断言单一原因），也不许折成下面那些
         * "密码错误 / 达到上限"（那是反过来把真损坏说成密码错）——
         * 它就是它自己：结论与日志同时保留两种可能（与 7-Zip 侧同一口径）。
         */
        if (lastFailure != null && lastFailure.IsPasswordOrCorrupted)
        {
            return RecursionStopReason.PasswordOrCorrupted;
        }

        if (!triedAny)
        {
            /*
             * 一个候选都没有：调用方的密码来源没给出任何东西。
             * 这里归到 WrongPassword（"没有可用密码"），而不是 EngineFailed ——
             * 对用户来说要做的动作是一样的（去补一个密码），而 EngineFailed 会把他引向查引擎，
             * 那是错的方向。
             */
            return RecursionStopReason.WrongPassword;
        }


        // 候选还有剩、却因为上限停手 —— 这才是"达到密码尝试上限"。
        if (candidateCount > attempts)
        {
            return RecursionStopReason.PasswordAttemptsExceeded;
        }

        // 候选全试完了都不对 —— 常规的"密码错误"。
        return RecursionStopReason.WrongPassword;
    }

    /// <summary>
    /// **内容魔数说它是归档、而名字里读不出卷标记**吗 —— 「还原」工序的第三类入参（2026-10-13）。
    ///
    /// <para>现场（`run-1111` 逐字）：`111-L2.7z.yvA001`（7z 的起始头 + 被切掉的字节）。
    /// 探针实测那张底层卡逐字：
    /// `内容：格式=SevenZip … 结论=NotAnArchive ｜ 名字：卷号=判不出 规范名=（空） 基名=111-L2.7z`。
    /// ⇒ `IsArchiveAsync` 为假（引擎按名字打不开分卷片）、`IsVolumePartFileName` 也为假
    /// ⇒ 它**两头都不进** ⇒ 「还原」那一档压根看不到它 ⇒ 名字永远是脏的 ⇒
    /// 那一组的入口包永远不叫规范名 ⇒ 整组凑不齐 ⇒ ①红。</para>
    ///
    /// <para>判据只有一条、只读内容：<see cref="FileNature.Inspect"/> 那张唯一底层的卡说
    /// `Format != Unknown`（魔数是已知归档格式）—— ⛔ 不自己读魔数、⛔ 不看后缀。
    /// ⛔ 它**只进「还原」改名那一档**（与既有那类"名字像卷片但认不出魔数"的入参同一个去向），
    /// ⛔ 不进续解名单。名字已经像卷片的那些，调用方原来那条已经收过。</para>
    /// </summary>
    internal static bool IsArchiveByMagicWithoutVolumeName(string path)
    {
        if (LegacyVolumeNames.IsVolumePart(FileNameHelper.GetFileName(path)))
        {
            return false;
        }

        return FileNature.Inspect(path).Format != VolumeContentFormat.Unknown;
    }

}
