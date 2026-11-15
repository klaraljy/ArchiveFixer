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
/// 每层规则（原 <c>RecursiveExtractor.LayerRules.cs</c> 的全部实现）：内容物保留关键词 / 内容容器保护 /
/// 同组后续卷折叠 / 层上限与展开比 / 多分支决策请求。
///
/// <para>从 <see cref="RecursiveExtractor"/> 按职责搬出的协作类，只持有宿主
/// <see cref="RecursiveExtractor"/>。⛔ 判据、日志顺序、调用顺序与搬出前逐字一致；
/// 宿主那一分片只留同签名薄转发与最小 internal 访问器。</para>
/// </summary>
internal sealed class RecursiveLayerRulesHandler
{
    private readonly RecursiveExtractor _host;

    internal RecursiveLayerRulesHandler(RecursiveExtractor host)
    {
        _host = host;
    }

    /// <summary>
    /// 把这一层扫出来的候选里**命中「内容物保留关键词」**的那些摘掉（连同**同组的兄弟卷**）——
    /// 它们**不当内层归档**：不解开、也不改名，只当普通内容物留在这一层的结果里。
    ///
    /// <para>用户 2026-10-04 的新功能原话：「只要文件名里面包含着这个字符就不能动……
    /// 这些压缩文件碰都不要碰」「包含的也同样是」。判据唯一出口 <see cref="ContentKeepRules"/>
    /// （包含即命中、<see cref="StringComparison.OrdinalIgnoreCase"/>、空关键词忽略、⛔ 不做通配 / 正则）。</para>
    ///
    /// <para>⛔ **空关键词列表时本方法不摘任何东西**（原样返回）。
    /// 默认内容容器保护由紧随其后的 <c>ApplyContentContainerProtection</c> 单独执行，
    /// 两者不混在一起；这一条是关键词回归命根。</para>
    ///
    /// <para>⛔ 为什么不跟引擎、文件内容打交道：用户明确说"你甚至不用去检测他是否是压缩文件" ——
    /// 判据**只看文件名**（内容探测在这一步之前已经做完了，这里只决定"要不要当内层归档"）。</para>
    /// </summary>
    internal List<string> ApplyContentKeepRules(List<string> found, string layerLabel)
    {
        if (_host.KeepRules.IsEmpty || found.Count == 0)
        {
            return found;
        }

        var hitKeywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in found)
        {
            string? keyword = _host.KeepRules.FindMatch(path);

            if (keyword != null)
            {
                hitKeywords[path] = keyword;
            }
        }

        if (hitKeywords.Count == 0)
        {
            return found;
        }

        var kept = new List<string>(found.Count);

        foreach (string path in found)
        {
            if (hitKeywords.TryGetValue(path, out string? ownKeyword))
            {
                _host.Log(
                    "INFO",
                    $"{layerLabel}：按「内容物保留关键词」原样留着，不解开也不改名 —— "
                    + $"{Path.GetFileName(path)}（命中「{ownKeyword}」）");

                continue;
            }

            // 同组的兄弟卷：整组是一个归档，一卷命中就整组都不当内层归档。
            string? siblingKeyword = null;
            string? siblingHit = null;

            foreach (string hit in hitKeywords.Keys)
            {
                if (IsSameGroupContinuationVolume(path, hit))
                {
                    siblingKeyword = hitKeywords[hit];
                    siblingHit = hit;
                    break;
                }
            }

            if (siblingKeyword != null)
            {
                _host.Log(
                    "INFO",
                    $"{layerLabel}：按「内容物保留关键词」原样留着，不解开也不改名 —— "
                    + $"{Path.GetFileName(path)}（与 {Path.GetFileName(siblingHit!)} 是同一组，"
                    + $"那一组命中「{siblingKeyword}」）");

                continue;
            }

            kept.Add(path);
        }

        return kept;
    }

    /// <summary>
    /// 内容容器默认保护：`.docx` / `.xlsx` / `.epub` / `.jar` 等即使内容是 ZIP，也当内容物保留。
    /// 只在解压链内部消费；用户直接导入的源包不经过这里（手动「只解压」仍可拆）。
    /// </summary>
    internal List<string> ApplyContentContainerProtection(List<string> found, string layerLabel)
    {
        if (found.Count == 0)
        {
            return found;
        }

        var kept = new List<string>(found.Count);

        foreach (string path in found)
        {
            if (!ArchiveFixer.Security.ContentContainerProtection.ShouldProtect(path))
            {
                kept.Add(path);
                continue;
            }

            _host.Log(
                "INFO",
                $"{layerLabel}：{Path.GetFileName(path)} 是内容容器（{ArchiveFixer.Security.ContentContainerProtection.Describe(path)}）"
                + " —— 默认当内容物保留，不递归也不改名；要拆请单独选中它用「只解压」。");
        }

        return kept;
    }

    /// <summary>
    /// 把"这一层扫出来的内层归档候选"里的**同组后续卷**折掉，只留每一组的**第一卷**（2026-10-03 真机 §51）。
    ///
    /// <para>一组 N 卷在**内容**上各自都是合法归档（每卷都带自己的 RAR / 7z 签名），所以内容扫描
    /// 会把它们全收进来 —— 老写法于是把一组 4 卷当成 **4 个内层归档**：「展开所有分支」档下
    /// 建 4 个分支层、同一组被解 4 遍（峰值多占 3 倍），其中三个分支层还会被当成"产物在哪"的答案
    /// 写进日志（真机现场见 <c>docs/真机事故复盘.md</c> §51）。</para>
    ///
    /// <para>判据**复用同一条**（同目录 + 同包基名 + 是后续卷 =
    /// <see cref="IsSameGroupContinuationVolume"/>，与单链那档同一个出口）：
    /// 只有真的同组后续卷才会被折叠，别的归档一个都不动。</para>
    ///
    /// <para>顺序：先按卷序（认不出卷序的排最后）决定"谁是首卷"，**结果仍按传进来的顺序返回** ——
    /// ⛔ 不许让折叠顺手改掉这一层的分支顺序。纯函数、不碰盘，所以能被单测直接摆布。</para>
    /// </summary>
    internal static List<string> CollapseSameGroupVolumes(List<string> found)
    {
        if (found == null || found.Count < 2)
        {
            return found ?? new List<string>();
        }

        List<string> byVolumeOrder = found
            .OrderBy(path => Detection.LegacyVolumeNames.TryGetIndex(Path.GetFileName(path))
                             ?? int.MaxValue)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var firstVolumes = new List<string>();

        foreach (string candidate in byVolumeOrder)
        {
            if (firstVolumes.Any(first => IsSameGroupContinuationVolume(candidate, first)))
            {
                continue;
            }

            firstVolumes.Add(candidate);
        }

        return firstVolumes.Count == found.Count
            ? found
            : found.Where(path => firstVolumes.Contains(path)).ToList();
    }

    /// <summary>
    /// 候选文件是不是**内层归档那一组的后续卷**：同目录 + 同包基名 + 名字是后续卷，三条同时成立。
    ///
    /// <para>判据全部转调既有出口（⛔ 这里不许自带第二套名字规则）：
    /// 「是不是后续卷」= <see cref="FileNameHelper.IsVolumeContinuationPart"/>（与续解扫描同一份），
    /// 「包基名」= <see cref="FileNameHelper.GetArchiveBaseName"/>（它内部剥分卷标记用的也是唯一那份
    /// <c>ExtensionHelper.TrySplitVolumeSegmentTolerant</c>）。</para>
    ///
    /// <para>为什么要"同目录"这一条：引擎找兄弟卷**只看入口文件旁边那一层**（AGENTS.md §11.4），
    /// 别的目录里那个 <c>.002</c> 不可能是这一组的可用分片 —— 那种情况照旧按"别的文件"处理。</para>
    ///
    /// <para><b>为什么是 internal</b>（2026-10-05）：<c>VolumeGroupRulerParityTests</c> 要把这一把尺子与
    /// 搬运/删除那一把（<c>ExtractionWorkspace.IsSameGroupMemberInSameDirectory</c>）放在**同一份名字语料**上
    /// 逐格比对。⛔ 那个用例里**不许复制一份判据**（复制出来的守卫拦不住真身漂移）—— 所以只放宽可见性，
    /// 判据本体一个字没动。</para>
    /// </summary>
    internal static bool IsSameGroupContinuationVolume(string candidate, string innerArchivePath)
    {
        if (!FileNameHelper.IsVolumeContinuationPart(candidate))
        {
            return false;
        }

        string candidateFull = SafePathHelper.GetFullPathSafe(candidate);
        string archiveFull = SafePathHelper.GetFullPathSafe(innerArchivePath);

        if (candidateFull.Length == 0 || archiveFull.Length == 0)
        {
            return false;
        }

        string candidateDirectory = Path.GetDirectoryName(candidateFull) ?? string.Empty;
        string archiveDirectory = Path.GetDirectoryName(archiveFull) ?? string.Empty;

        if (!string.Equals(candidateDirectory, archiveDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(
            FileNameHelper.GetArchiveBaseName(candidateFull),
            FileNameHelper.GetArchiveBaseName(archiveFull),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsInformationalFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);

        return RecursiveExtractor.InformationalExtensionsForLayerRules.Any(
            known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 每次要继续之前检查的硬上限（规则 5）。这里的每一项命中都必须**停止**，没有例外。
    ///
    /// ⚠ 这里**不再**包含展开比：展开比要知道"这个包解压后多大"，那是一次引擎调用或一次磁盘遍历，
    /// 是异步的（见 <see cref="IsExpansionRatioExceededAsync"/>）。把一个 async 操作塞进同步判定，
    /// 只能靠 <c>GetAwaiter().GetResult()</c>，而那正是历史卡死的原因。调用方按顺序查完这里，再 await 展开比。
    /// </summary>
    internal RecursionStopReason CheckLimitsBeforeLayer(
        WorkItem item,
        List<RecursionLayerReport> layers,
        int totalFiles,
        long totalSize)
    {
        /*
         * 层数：只解层号 < MaxDepth 的层（0 起算，所以 MaxDepth = 1 时第 0 层照解、
         * 第 1 层被拦下）—— 这与 AGENTS.md §10 M4 的"达到上限安全停下并报告"一致。
         *
         * 判据是**层号**（item.Depth），不是"已经解了几层"（layers.Count）：分支模式下同一层
         * 会排好几项，拿已解层数当层号会在合法的宽树上假报超限（AllBranches 展开 3 个以上分支时，
         * 第 3 项就会被当成"第 3 层"拦掉）。
         *
         * 这一关也是"入队判据写错"的兜底：每一项都要先过这里才会被解，所以入队处万一漏判，
         * 多出来的层只会停在队列里、以 MaxDepthReached 收场，不会被真的解开。
         */
        if (item.Depth >= _host.Limits.MaxDepth)
        {
            return RecursionStopReason.MaxDepthReached;
        }

        if (totalFiles >= _host.Limits.MaxTotalFiles)
        {
            return RecursionStopReason.MaxTotalFilesReached;
        }

        if (totalSize >= _host.Limits.MaxTotalSize)
        {
            return RecursionStopReason.MaxTotalSizeReached;
        }

        return RecursionStopReason.None;
    }

    /// <summary>
    /// 单层展开比检查：本层归档解压后的总大小 / 该归档文件大小，超限即"疑似压缩炸弹"。
    /// 取不到可信数值（文件不在、list 失败、被压缩成 0 字节）时**不拦**：
    /// 拿不准的时候拦下来会把正常包也误伤，而真正的兜底是 MaxTotalSize / MaxTotalFiles 这两条硬线。
    ///
    /// ⚠ **全程 async，绝不同步等待**：这里要问引擎"解压后多大"（第 0 层）或遍历磁盘（内层），
    /// 在 UI 线程上同步等它就是死锁（7z 的续体要回到同一个被阻塞的线程）。
    /// </summary>
    internal async Task<bool> IsExpansionRatioExceededAsync(
        WorkItem item,
        List<RecursionLayerReport> layers,
        Queue<WorkItem> pending)
    {
        long archiveSize = RecursiveExtractor.SafeFileLengthForLayerRules(item.ArchivePath);

        if (archiveSize <= 0)
        {
            return false;
        }

        long uncompressedSize;

        if (item.IsRoot)
        {
            /*
             * 只有第 0 层需要问引擎"解压后多大"：那时的归档还是用户给的原始文件，
             * 磁盘上还没有任何产物可量。
             */
            uncompressedSize = await GetRootUncompressedSizeAsync(item.ArchivePath).ConfigureAwait(false);
        }
        else
        {
            /*
             * 内层归档已经在磁盘上了：它自己所在那一层的产物实测大小就是它的解压后大小，
             * 比 list 更可信。
             *
             * 这里必须**定位到它自己那一层**。旧写法取 layers[^1]（最近完成的那一层），
             * 而主循环是宽度优先的：同一层排了多个分支时，处理第二个分支时 layers[^1] 是
             * **第一个分支**的产物。两个不相干的数相除，结果就是"随便一个正常小包被误报成
             * 压缩炸弹并拒绝发布"（例：第 0 层解出 2 GB 文件 + 一个 1 MB 的内层 zip → 2000 倍）。
             */
            uncompressedSize = ResolveLayerOutputSize(item.ArchivePath, layers, pending);
        }

        if (uncompressedSize <= 0)
        {
            return false;
        }

        return uncompressedSize / (double)archiveSize > _host.Limits.MaxExpansionRatio;
    }

    /// <summary>
    /// 问引擎"第 0 层的包解压后多大"。失败按"拿不准"处理（返回 0 = 不拦）。
    /// </summary>
    private async Task<long> GetRootUncompressedSizeAsync(string archivePath)
    {
        try
        {
            ArchiveListResult list = await _host.Engine
                .ListAsync(ArchiveRequest.For(archivePath), CancellationToken.None)
                .ConfigureAwait(false);

            return list.Success ? list.TotalUncompressedSize : 0;
        }
        catch
        {
            // 引擎抛异常时按"拿不准"处理，不拦。
            return 0;
        }
    }

    /// <summary>
    /// 找出"这个内层归档所属那一层"的产物总大小。
    ///
    /// 反查链：<see cref="WorkspaceLayer.Layers"/> 的每一层都记着 InputPath（它解的是哪个归档），
    /// 于是能唯一确定这个内层归档属于哪一层；该层的 OutputSize 就是它解压后的大小。
    /// 只有那一层已经解完（在 <paramref name="layers"/> 里有报告）时才拿得到。
    /// </summary>
    private long ResolveLayerOutputSize(
        string innerArchivePath,
        List<RecursionLayerReport> layers,
        Queue<WorkItem> pending)
    {
        string target = SafePathHelper.GetFullPathSafe(innerArchivePath);

        if (target.Length == 0)
        {
            return 0;
        }

        foreach (WorkItem queued in pending)
        {
            if (queued.Layer.InputPath.Length == 0)
            {
                continue;
            }

            if (!string.Equals(
                    SafePathHelper.GetFullPathSafe(queued.Layer.InputPath),
                    target,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string outputPath = SafePathHelper.GetFullPathSafe(queued.Layer.OutputPath);

            RecursionLayerReport? owner = layers.FirstOrDefault(layer =>
                string.Equals(
                    SafePathHelper.GetFullPathSafe(layer.OutputPath),
                    outputPath,
                    StringComparison.OrdinalIgnoreCase));

            if (owner != null)
            {
                return owner.OutputSize;
            }

            // 那一层还没解（理论上到不了这里）：拿不准就不拦。
            return 0;
        }

        return 0;
    }

    /// <summary>
    /// 把上次的多分支选择变成"只处理这几个路径"的执行项。
    /// 单独一个方法，是为了让"用户选了什么"这件事在代码里只有一处解释。
    /// </summary>
    private static RecursionDecisionRequest BuildDecision(WorkItem item, IReadOnlyList<string> innerArchives)
    {
        return new RecursionDecisionRequest
        {
            CurrentArchivePath = item.ArchivePath,
            Depth = item.Depth,
            CandidateArchives = innerArchives,
            Prompt =
                $"检测到 {innerArchives.Count} 个内层归档，预计最多产生 {innerArchives.Count} 个子任务，" +
                "可能产生大量输出，是否继续？"
        };
    }
}
