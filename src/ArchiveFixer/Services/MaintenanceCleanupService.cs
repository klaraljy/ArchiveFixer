using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Services
{
    /// <summary>清理的作用域（两档互相独立，绝不合并成一个开关）。</summary>
    public enum CleanupScope
    {
        /// <summary>其余物（旧的 <c>过程物</c> 目录，规格 §3.2）。</summary>
        Artifacts = 0,

        /// <summary>当前任务输出根下"任意层级都没有文件"的第一层子目录（规格 §5）。</summary>
        EmptyFolders = 1
    }

    /// <summary>
    /// 删除档位之外的**作用域档位**：只删当前勾选任务那一份，还是删整个共享目录下的全部。
    ///
    /// <para>
    /// 拆成独立枚举而不是往 <see cref="CleanupScope"/> 里再塞一个值：作用域类型（其余物 / 空文件夹）
    /// 与作用域大小（本任务 / 整个目录）是两个正交的问题，混在一起会让枚举变成乘积。
    /// </para>
    /// </summary>
    public enum ArtifactDeleteScope
    {
        /// <summary>只删当前勾选任务自己那一份（<b>默认，也是唯一安全的档</b>）。</summary>
        SelectedTask = 0,

        /// <summary>删掉当前共享目录下的全部其余物（会影响同目录里的其它包，默认不选）。</summary>
        EverythingInDirectory = 1
    }

    /// <summary>
    /// 其余物清理的作用域解析结果。
    ///
    /// <para>
    /// 这个类型存在的唯一理由是**把"删哪一份"这件事变成一次可检查、可断言、可复用的计算**：
    /// 预览与执行必须拿到同一个答案，绝不允许出现"预览按窄的算、执行按宽的删"。
    /// </para>
    /// </summary>
    public sealed class ArtifactCleanupScope
    {
        /// <summary>作用域种类：只有本任务那一份 / 整个共享目录。</summary>
        public CleanupScope Scope { get; init; } = CleanupScope.Artifacts;

        /// <summary>
        /// 允许删除的根（交给 <see cref="DeleteSafetyGuard"/> 做容器内校验的那个 <c>AllowedRoot</c>）。
        /// 为空 = 没有可信的边界 → 调用方必须拒绝删除。
        /// </summary>
        public string AllowedRoot { get; init; } = string.Empty;

        /// <summary>
        /// 真正会被删除的第一个目录（其余物目录）。为空 = 没有可删的落点。
        ///
        /// ⚠ 完整清单看 <see cref="ArtifactDirectories"/>：老版本留下的 <c>过程物</c> 与新版
        /// <c>其余物</c> 可能**同时存在**，两个都是本任务那一份，要一起删掉。
        /// </summary>
        public string ArtifactDirectory => ArtifactDirectories.Count > 0 ? ArtifactDirectories[0] : string.Empty;

        /// <summary>本次要删掉的**全部**其余物目录（同一个任务的历史名 + 新版名）。</summary>
        public IReadOnlyList<string> ArtifactDirectories { get; init; } = Array.Empty<string>();

        /// <summary>这次删除的作用域是不是"整个共享目录下的全部其余物"。</summary>
        public bool DeletesEverythingInDirectory { get; init; }

        /// <summary>是不是共享输出目录（模式 B / 指定位置直接放：多个包共用一个输出目录）。</summary>
        public bool OutputDirectoryIsShared { get; init; }

        /// <summary>这个作用域为什么是这个样子（进日志与确认框，说清判据）。</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>没算出来的原因（为空 = 算出来了）。</summary>
        public string BlockReason { get; init; } = string.Empty;

        /// <summary>
        /// 没算出来是因为**确实没有**（正常状态，告诉用户"没有可删的"就行），
        /// 还是因为**拿不准所以不敢删**（必须把拒绝理由说清楚）。
        /// 这两件事对用户的意义完全不同：前者无感，后者要让用户知道程序为什么不动手。
        /// </summary>
        public bool RefusedUnsafe { get; init; }

        /// <summary>能不能删：算出来了、有边界、有落点。</summary>
        public bool IsResolved => ArtifactDirectories.Count > 0 && !string.IsNullOrWhiteSpace(AllowedRoot);
    }

    /// <summary>
    /// 清理**预览**：动手之前给用户看的清单（规格 §3.2 要求"列出路径数量与总大小"）。
    ///
    /// <para>
    /// 预览**不删除任何东西**：<see cref="HasTarget"/> 为 false 时连目标都没有，
    /// 调用方应当直接告诉用户"没有可清理的"，而不是弹一个"确定要删 0 项吗"。
    /// </para>
    /// </summary>
    public sealed class CleanupPreview
    {
        public CleanupScope Scope { get; init; }

        /// <summary>被清理的作用域路径（其余物目录 / 输出根）。</summary>
        public string ScopePath { get; init; } = string.Empty;

        /// <summary>允许删除的根（预览与执行必须一致；空 = 没算出来，拒绝执行）。</summary>
        public string AllowedRoot { get; init; } = string.Empty;

        /// <summary>
        /// 这次预览算出来的**完整作用域判定**。执行阶段直接复用它，
        /// 不再自己算一遍 —— 预览与执行分叉是这类删除功能最容易出事的地方。
        /// </summary>
        public ArtifactCleanupScope? ResolvedScope { get; init; }

        /// <summary>有没有可清理的东西（false 时不要进入确认流程）。</summary>
        public bool HasTarget { get; init; }

        /// <summary>将删除的**顶层条目数**（其余物目录算 1 项；空文件夹清理是候选目录数）。</summary>
        public int ItemCount { get; init; }

        /// <summary>条目总数（含子项；空文件夹清理里没有文件所以恒为 0）。</summary>
        public int EntryCount { get; init; }

        /// <summary>总字节数。</summary>
        public long TotalBytes { get; init; }

        /// <summary>能不能量准（读不到时为 false —— 数字宁可没有，也不能是编的）。</summary>
        public bool Determined { get; init; }

        /// <summary>将删除的顶层条目（最多列 <see cref="MaxListedItems"/> 个，其余用"等 N 项"收尾）。</summary>
        public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 被删条目里的**源包文件**（压缩包本身）。大于 0 时确认框必须显著提示 ——
        /// 删掉源包意味着要重新下载，这是用户最容易忽略的后果。
        /// </summary>
        public IReadOnlyList<string> SourcePackageNames { get; init; } = Array.Empty<string>();

        /// <summary>源包文件个数（<see cref="SourcePackageNames"/> 可能被截断，计数以它为准）。</summary>
        public int SourcePackageCount { get; init; }

        /// <summary>
        /// 「删除本目录全部其余物」会影响到**几个包**（其余物根下按包基名分的子目录数）。
        ///
        /// <para>
        /// 为什么要有它：这个入口的作用域是**整个共享输出目录**，用户点之前必须知道
        /// "会有几个包一起被清掉"。以前确认框只说"会影响同目录里的所有包" —— 是一句形容词，
        /// 用户没法判断这次到底动了 2 个还是 20 个（2026-09-22 验收的口径：作用域越大越要点名）。
        /// 非整目录档恒为 0（此时作用域只有一个包，用不着点名单）。
        /// </para>
        /// </summary>
        public int AffectedPackageCount { get; init; }

        /// <summary>受影响的包名清单（最多列 <see cref="MaxListedItems"/> 个；计数以 <see cref="AffectedPackageCount"/> 为准）。</summary>
        public IReadOnlyList<string> AffectedPackageNames { get; init; } = Array.Empty<string>();

        /// <summary>一句话结论，可直接进日志 / 确认框。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>确认框里最多列几项 —— 列太多会把对话框撑满，用户反而看不清主要信息。</summary>
        public const int MaxListedItems = 10;
    }

    /// <summary>清理**执行**的结论。</summary>
    public sealed class CleanupOutcome
    {
        public CleanupScope Scope { get; init; }

        public DeleteMode Mode { get; init; }

        /// <summary>是否真的执行过删除（未确认 / 没有候选时为 false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        public int SuccessCount { get; init; }

        public int FailureCount { get; init; }

        /// <summary>彻底删除真正释放的字节数。</summary>
        public long FreedBytes { get; init; }

        /// <summary>移入回收站的字节数（未真正释放空间）。</summary>
        public long RecycledBytes { get; init; }

        /// <summary>失败原因清单（"路径：说明"）。</summary>
        public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

        /// <summary>每一步的删除日志（路径 + 理由 + 条目数 + 总大小），调用方原样写进界面/文件日志。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();

        public string Message { get; init; } = string.Empty;

        /// <summary>是不是"一项都没删掉"（含"根本没执行"）—— 界面据此决定用提示还是警告。</summary>
        public bool SucceededNothing => SuccessCount <= 0;
    }

    /// <summary>
    /// 「删除其余物…」「删除本目录全部其余物…」「清理空文件夹…」三个界面入口背后的本体
    /// （规格 §3.2 / §5，用户 2026-09-22 拍板的新作用域模型）。
    ///
    /// <para>
    /// ⚠️ **本次修复的核心**（上一版的真实缺陷）：旧实现的其余物作用域是
    /// <c>&lt;任务输出目录&gt;其余物</c>。共用落点（"添加文件夹 + 指定位置"）下**所有任务的输出目录都是同一个**
    /// （源目录本身），于是"只勾选 222 去清理"会连带清掉同一个源目录里 333、444 的其余物。
    /// 新模型按布局再分一层包基名（<c>&lt;共享根&gt;\其余物\222\</c>），清理也必须按同一层收窄。
    /// </para>
    /// <para>
    /// 四条硬约束（都来自规格，不在这里重新发明）：
    /// ① 作用域**不得越出**：包有自己的目录时只删 <c>&lt;任务输出目录&gt;\其余物\</c>；
    ///    共享输出目录时只删 <c>&lt;共享根&gt;\其余物\&lt;本任务包基名&gt;\</c>，绝不碰别的包的目录；
    /// ② 默认档 = <b>移入回收站</b>；回收站不可用时**拒绝执行**，绝不降级成永久删除
    ///    （<see cref="RecycleBinService"/> 内部就这么做，本类不绕过它）；
    /// ③ 每一件事都写日志（路径 + 理由 + 条目数 + 总大小）；
    /// ④ **本类全是磁盘活**：调用方必须放后台线程（界面只更新状态）。
    /// </para>
    /// <para>
    /// ⚠ 注入约定：构造函数收的 <see cref="IDeleteExecutor"/> / <see cref="IDeleteFileSystemProbe"/>
    /// 会同时给 <see cref="RecycleBinService"/> 与 <see cref="EmptyFolderCleaner"/> 用同一份 ——
    /// "扫描看到的"和"删除时校验的"必须是同一个世界（单测里尤其明显，见 EmptyFolderCleaner 的注释）。
    /// </para>
    /// </summary>
    public sealed class MaintenanceCleanupService
    {
        private readonly IDeleteExecutor _executor;
        private readonly IDeleteFileSystemProbe _probe;

        public MaintenanceCleanupService(
            IDeleteExecutor? executor = null,
            IDeleteFileSystemProbe? probe = null)
        {
            _executor = executor ?? new ShellDeleteExecutor();
            _probe = probe ?? WindowsDeleteFileSystemProbe.Instance;
        }

        /// <summary>清理理由（写进删除日志；两档共用同一句话）。</summary>
        public const string ProcessArtifactReason = "删除其余物（内层归档 / 分卷 / 过程物 / 源包）";

        /// <summary>「删除本目录全部其余物」的理由：说清这次不是只删本任务那一份。</summary>
        public const string AllArtifactsReason = "删除本目录全部其余物（影响该目录下所有包）";

        public const string EmptyFolderReason = "清理空文件夹（任意层级都没有文件）";

        /// <summary>
        /// 其余物目录名与"老名字也认"的规则**唯一来源**是 <see cref="ProcessArtifactLayout"/>：
        /// 目录名（<see cref="ProcessArtifactLayout.ArtifactDirectoryName"/>）、历史名
        /// （<see cref="ProcessArtifactLayout.LegacyArtifactDirectoryName"/>）、
        /// "已存在哪些"（<see cref="ProcessArtifactLayout.FindExistingArtifactDirectories"/>）
        /// 全部引用它，本类一个字面量都不写 —— 布局改名时这里必须跟着一起变，
        /// 而"跟着变"的唯一可靠做法就是不复制它的字面量。
        /// </summary>
        public static string ArtifactDirectoryName => ProcessArtifactLayout.ArtifactDirectoryName;

        // ================================================================ ① 作用域解析

        /// <summary>
        /// **唯一**的作用域解析处：当前勾选任务的其余物是哪一份。
        ///
        /// <para>判据只用公开信息，不自己拼路径前缀比较：</para>
        /// <list type="number">
        /// <item><description>
        /// 任务输出目录：<see cref="ArchiveTask.OutputPath"/>（解压管线回写的**实际落点**）。
        /// </description></item>
        /// <item><description>
        /// 共享目录判据：<see cref="OutputPlacement.LandsInSourceDirectory"/>（输出目录就是源包自己的目录）。
        /// 这是本项目"多个任务共用一个输出目录"的唯一判据，本类不另写一套。
        /// </description></item>
        /// <item><description>
        /// 落点目录：<see cref="ProcessArtifactLayout.ResolveArtifactDirectory(string, string, bool)"/>
        /// （决策 D-10 的唯一实现处，含"共享根下按包基名分一层"），目录名与历史名兼容也走它。
        /// </description></item>
        /// <item><description>
        /// 包基名：<see cref="OutputPlacement.ResolveArchiveBaseName"/>（分卷组取整组基名，
        /// <c>222.7z.001</c> → <c>222</c>），与定稿时给其余物分层的名字同源。
        /// </description></item>
        /// </list>
        ///
        /// <para>
        /// 共享目录下**只认** <c>&lt;共享根&gt;\其余物\&lt;本任务包基名&gt;\</c>：
        /// 那一层不存在就**拒绝**（宁可少删，不可错删）—— 退到上一层去删
        /// <c>&lt;共享根&gt;\其余物\</c> 正好就是本次要根治的误删缺陷。
        /// 用户真想清整个目录时，走「删除本目录全部其余物…」那个显式入口。
        /// </para>
        /// </summary>
        public static ArtifactCleanupScope ResolveArtifactScope(
            ArchiveTask? task,
            ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
        {
            if (task == null)
            {
                return Unresolved("没有选中的任务，无法确定要删哪一份其余物");
            }

            return ResolveArtifactScope(
                task.OutputPath,
                task.CurrentPath,
                task.VolumeGroupKey,
                deleteScope);
        }

        /// <summary>
        /// 作用域解析的字符串重载。存在的理由是**可测**：不必为了验证一条路径判定
        /// 去构造整个 <see cref="ArchiveTask"/>。
        /// </summary>
        /// <param name="outputDirectory">任务的实际输出目录（<see cref="ArchiveTask.OutputPath"/>）。</param>
        /// <param name="sourceArchivePath">源包路径（判"输出目录是不是就是源包目录"用）。</param>
        /// <param name="volumeGroupBaseName">
        /// 分卷组基名（<see cref="ArchiveTask.VolumeGroupKey"/>）；给了它优先用，省掉再算一遍剥分卷那一步。
        /// </param>
        public static ArtifactCleanupScope ResolveArtifactScope(
            string? outputDirectory,
            string? sourceArchivePath = null,
            string? volumeGroupBaseName = null,
            ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Unresolved("当前任务还没有输出目录（先解压一次再删除）");
            }

            string outputFull = SafePathHelper.GetFullPathSafe(outputDirectory);

            if (string.IsNullOrWhiteSpace(outputFull))
            {
                return Unresolved($"输出目录路径无法规范化，无法确认删除范围：{outputDirectory}");
            }

            bool shared = OutputPlacement.LandsInSourceDirectory(sourceArchivePath, outputFull);

            if (!shared)
            {
                /*
                 * 包有自己的输出目录（默认模式 / 自定义位置单独建文件夹）：其余物就在它自己的目录里，
                 * 边界就是它。老名字（过程物）留下的目录同样返回 —— 老目录不能永远清不掉。
                 */
                IReadOnlyList<string> own = ResolveExistingArtifactDirectories(
                    outputFull,
                    archiveBaseName: null,
                    sharedRoot: false);

                if (own.Count == 0)
                {
                    // 确实没有（不是"不敢删"）：给一句带路径的话，用户能自己去看一眼。
                    return new ArtifactCleanupScope
                    {
                        Scope = CleanupScope.Artifacts,
                        AllowedRoot = outputFull,
                        BlockReason = $"没有可删除的其余物：当前任务的输出目录（{outputFull}）里没有 {ArtifactDirectoryName} 目录"
                    };
                }

                return new ArtifactCleanupScope
                {
                    Scope = CleanupScope.Artifacts,
                    AllowedRoot = outputFull,
                    ArtifactDirectories = own,
                    Description = $"当前任务自己的其余物目录：{string.Join("、", own)}"
                };
            }

            if (deleteScope == ArtifactDeleteScope.EverythingInDirectory)
            {
                // 显式入口：用户要的就是整个目录（文案里已经写明"会影响 N 个包"）。
                IReadOnlyList<string> everything = ResolveExistingArtifactDirectories(
                    outputFull,
                    archiveBaseName: null,
                    sharedRoot: false);

                return new ArtifactCleanupScope
                {
                    Scope = CleanupScope.Artifacts,
                    AllowedRoot = outputFull,
                    ArtifactDirectories = everything,
                    DeletesEverythingInDirectory = true,
                    OutputDirectoryIsShared = true,
                    Description = everything.Count == 0
                        ? $"共享输出目录（{outputFull}）里没有其余物目录"
                        : $"共享输出目录下的全部其余物：{string.Join("、", everything)}",
                    BlockReason = everything.Count == 0
                        ? $"没有可删除的其余物：共享输出目录（{outputFull}）里没有 {ArtifactDirectoryName} 目录"
                        : string.Empty
                };
            }

            string packageBaseName = OutputPlacement.ResolveArchiveBaseName(
                sourceArchivePath,
                volumeGroupBaseName);

            if (string.IsNullOrWhiteSpace(packageBaseName))
            {
                return Unresolved(
                    $"认不出当前任务的包基名（{sourceArchivePath}），无法确定 {outputFull} 下哪一份其余物属于它，"
                    + "已拒绝删除（如需清整个目录，请用「删除本目录全部其余物…」并确认影响范围）",
                    outputFull,
                    shared: true,
                    refused: true);
            }

            // 决策 D-10 的唯一实现处：共享根下其余物按包基名再分一层。
            IReadOnlyList<string> existing = ResolveExistingArtifactDirectories(
                outputFull,
                packageBaseName,
                sharedRoot: true);

            if (existing.Count == 0)
            {
                return new ArtifactCleanupScope
                {
                    Scope = CleanupScope.Artifacts,

                    // 允许根是**共享根**而不是共享根\其余物：目标与允许根之间隔着
                    // 其余物\包名 两级，DeleteSafetyGuard 会逐级确认它们都不是符号链接。
                    AllowedRoot = outputFull,
                    ArtifactDirectories = new[]
                    {
                        ProcessArtifactLayout.ResolveArtifactDirectory(outputFull, packageBaseName, sharedRoot: true)
                    },
                    OutputDirectoryIsShared = true,
                    RefusedUnsafe = true,
                    BlockReason =
                        $"{outputFull} 是多个包共用的输出目录，只允许删本任务那一份"
                        + $"（{Path.Combine(ArtifactDirectoryName, packageBaseName)}），但它不存在，所以本次不删任何东西；"
                        + "不会退到上一层去删整个其余物目录（那会误删同目录里其它包的过程物）"
                };
            }

            return new ArtifactCleanupScope
            {
                Scope = CleanupScope.Artifacts,
                AllowedRoot = outputFull,
                ArtifactDirectories = existing,
                OutputDirectoryIsShared = true,
                Description = $"{outputFull} 是多个包共用的输出目录，本次只删「{packageBaseName}」这一份：{string.Join("、", existing)}"
            };
        }

        /// <summary>
        /// 某个内容物根下的其余物目录：**新旧两个名字都算**，且共享根下按包基名再分一层
        /// （决策 D-10 的唯一实现处是 <see cref="ProcessArtifactLayout.ResolveArtifactDirectoryWithName"/>）。
        ///
        /// <para>
        /// ⚠ 必须"两个名字各算一次"，不能拿 <c>D\其余物\222</c> 去喂
        /// <see cref="ProcessArtifactLayout.FindExistingArtifactDirectories"/>：
        /// 那个方法是"再往下一层找其余物目录"（它给参数追加 <c>\其余物</c> 或 <c>\过程物</c>），
        /// 喂一个带包名的路径进去会去找 <c>D\其余物\222\其余物</c>，永远找不到。
        /// </para>
        /// </summary>
        private static IReadOnlyList<string> ResolveExistingArtifactDirectories(
            string contentRoot,
            string? archiveBaseName,
            bool sharedRoot)
        {
            var found = new List<string>();

            foreach (string name in new[]
                     {
                         ProcessArtifactLayout.ArtifactDirectoryName,
                         ProcessArtifactLayout.LegacyArtifactDirectoryName
                     })
            {
                string candidate = ProcessArtifactLayout.ResolveArtifactDirectoryWithName(
                    contentRoot,
                    name,
                    archiveBaseName,
                    sharedRoot);

                if (!string.IsNullOrWhiteSpace(candidate) && SafePathHelper.DirectoryExists(candidate))
                {
                    found.Add(candidate);
                }
            }

            return found;
        }

        /// <summary>
        /// 其余物清理的作用域路径（兼容入口，等价于 <see cref="ResolveArtifactScope(ArchiveTask?, ArtifactDeleteScope)"/>
        /// 解析出来的落点）。拿不到时返回空串。
        /// </summary>
        public static string ResolveProcessArtifactScope(ArchiveTask? task)
        {
            return ResolveArtifactScope(task).ArtifactDirectory;
        }

        // ================================================================ ② 预览

        /// <summary>
        /// 预览其余物删除：条目数 + 总大小 + 顶层条目清单 + 源包识别。**不删除任何东西**。
        /// </summary>
        public CleanupPreview PreviewProcessArtifacts(
            ArchiveTask? task,
            ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
        {
            ArtifactCleanupScope scope = ResolveArtifactScope(task, deleteScope);

            if (!scope.IsResolved || scope.RefusedUnsafe)
            {
                /*
                 * "确实没有"与"不敢删"必须分开说：
                 * 前者是正常状态（用户点了一下，程序说这儿没东西），后者要让用户知道程序为什么不动手。
                 *
                 * ⚠ 拒绝的那一档必须走这里，**不能**掉到下面的"列清单"分支去：
                 * 拒绝时 ArtifactDirectories 里放的是"本该属于本任务、但不存在"的候选路径
                 * （为了让日志能说清它去哪儿找过），拿它去列目录只会得到一句
                 * "其余物目录是空的"，把"我拒绝了"说成"这里没东西" —— 用户会以为作用域算错了。
                 */
                return new CleanupPreview
                {
                    Scope = CleanupScope.Artifacts,
                    ScopePath = scope.ArtifactDirectory,
                    AllowedRoot = scope.AllowedRoot,
                    ResolvedScope = scope,
                    HasTarget = false,
                    Message = scope.RefusedUnsafe
                        ? "已拒绝删除其余物：" + scope.BlockReason
                        : scope.BlockReason
                };
            }

            var items = new List<string>();
            var sourcePackages = new List<string>();
            RecycleBinService deleteService = CreateDeleteService();
            var entryCount = 0;
            long totalBytes = 0;
            bool determined = true;

            foreach (string directory in scope.ArtifactDirectories)
            {
                // 容器内校验：目标必须落在允许根之内（不变量 4 的同一口径，不另写前缀比较）。
                if (!ArchivePathGuard.IsInsideRoot(scope.AllowedRoot, directory, out string reason))
                {
                    return new CleanupPreview
                    {
                        Scope = CleanupScope.Artifacts,
                        ScopePath = directory,
                        AllowedRoot = scope.AllowedRoot,
                        ResolvedScope = scope,
                        HasTarget = false,
                        Message = $"其余物目录不在允许的范围内，已拒绝删除 —— {reason}"
                    };
                }

                if (!Directory.Exists(directory))
                {
                    // 解析时判过存在性，但那是两次系统调用之间的时间差（TOCTOU 的温和版本）；
                    // 这里再确认一次，让"预览说有、执行时没了"变成一句明确的话。
                    continue;
                }

                List<string> directoryItems;
                List<string> directoryPackages;

                try
                {
                    directoryItems = ListTopLevelEntries(directory);
                    directoryPackages = FindSourcePackageNames(directory, directoryItems, task);
                }
                catch (Exception ex)
                {
                    return new CleanupPreview
                    {
                        Scope = CleanupScope.Artifacts,
                        ScopePath = directory,
                        AllowedRoot = scope.AllowedRoot,
                        ResolvedScope = scope,
                        HasTarget = false,
                        Message = $"读不了其余物目录（{ex.Message}），已放弃删除：{directory}"
                    };
                }

                // 度量走 RecycleBinService.MeasureTarget：与真正删除时用的是同一段代码，
                // 预览里报的数字和删除日志里的数字不会互相打架。
                DeleteMeasurement measurement = deleteService.MeasureTarget(directory);

                items.AddRange(directoryItems);
                sourcePackages.AddRange(directoryPackages);
                entryCount += measurement.EntryCount;
                totalBytes += measurement.TotalBytes;
                determined &= measurement.Determined;
            }

            if (items.Count == 0)
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.Artifacts,
                    ScopePath = scope.ArtifactDirectory,
                    AllowedRoot = scope.AllowedRoot,
                    ResolvedScope = scope,
                    HasTarget = false,
                    Message = $"其余物目录是空的，没有可删除的内容：{scope.ArtifactDirectory}"
                };
            }

            string message =
                $"将删除其余物：{string.Join("、", scope.ArtifactDirectories)}"
                + $"（顶层 {items.Count} 项，共 {entryCount} 个条目 / {totalBytes} 字节）"
                + (determined ? string.Empty : "，其中部分内容读不到，数字可能不全");

            if (sourcePackages.Count > 0)
            {
                message += $"；⚠ 其中含 {sourcePackages.Count} 个源包文件（删掉后需要重新下载）";
            }

            /*
             * 「删除本目录全部其余物」要点名"影响几个包"：其余物根下的**子目录**就是各个包的那一份
             * （共享根布局是 <共享根>\其余物\<包基名>\）。数不出名字时保持 0 —— 调用方会改用
             * "整个目录"的说法，绝不编一个数字出来（预览里的数字宁可没有，也不能是猜的）。
             */
            (int affectedPackageCount, IReadOnlyList<string> affectedPackageNames) =
                scope.DeletesEverythingInDirectory
                    ? CountAffectedPackages(scope.ArtifactDirectories)
                    : (0, (IReadOnlyList<string>)Array.Empty<string>());

            if (affectedPackageCount > 0)
            {
                message += $"；⚠ 本次是「删除本目录全部其余物」，会影响该目录下 {affectedPackageCount} 个包";
            }

            return new CleanupPreview
            {
                Scope = CleanupScope.Artifacts,
                ScopePath = scope.ArtifactDirectory,
                AllowedRoot = scope.AllowedRoot,
                ResolvedScope = scope,

                // 量不准也能删（删除时服务自己会再判一次并拒绝），但预览要如实说"数字可能不全"。
                HasTarget = true,
                ItemCount = items.Count,
                EntryCount = entryCount,
                TotalBytes = totalBytes,
                Determined = determined,
                Items = items.Take(CleanupPreview.MaxListedItems).ToList(),
                SourcePackageNames = sourcePackages.Take(CleanupPreview.MaxListedItems).ToList(),
                SourcePackageCount = sourcePackages.Count,
                AffectedPackageCount = affectedPackageCount,
                AffectedPackageNames = affectedPackageNames,
                Message = message
            };
        }

        /// <summary>
        /// 数一数这次整目录删除会波及几个包：其余物根下的每一个子目录算一个包。
        ///
        /// <para>
        /// 只在「删除本目录全部其余物」这一档调用。读不了目录时返回 0（= 数不出来），
        /// **不抛异常、不中断预览** —— 预览已经拿到了"有多少项、多少字节"这些硬数字，
        /// 包名清单只是补充说明，不该因为它读不到就让整次删除失败。
        /// </para>
        /// </summary>
        private static (int Count, IReadOnlyList<string> Names) CountAffectedPackages(
            IReadOnlyList<string> artifactDirectories)
        {
            var names = new List<string>();

            foreach (string directory in artifactDirectories)
            {
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (string sub in Directory.GetDirectories(directory))
                    {
                        string name = Path.GetFileName(sub);

                        if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        {
                            names.Add(name);
                        }
                    }
                }
                catch
                {
                    return (0, Array.Empty<string>());
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);

            return (names.Count, names.Take(CleanupPreview.MaxListedItems).ToList());
        }

        /// <summary>
        /// 预览空文件夹清理：候选目录清单（"任意层级都没有文件"的第一层子目录）。**不删除任何东西**。
        /// </summary>
        public CleanupPreview PreviewEmptyFolders(string? rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.EmptyFolders,
                    ScopePath = rootDirectory ?? string.Empty,
                    HasTarget = false,
                    Message = "没有可清理的空文件夹：当前任务还没有输出目录"
                };
            }

            // UserConfirmed = false：这一步只让它"报告本来会删哪些"，一个都不会删。
            EmptyFolderCleanResult scan = CreateEmptyFolderCleaner().Clean(new EmptyFolderCleanOptions
            {
                RootDirectory = rootDirectory,
                UserConfirmed = false,
                Mode = DeleteMode.RecycleBin,
                Reason = EmptyFolderReason
            });

            if (!scan.Scanned || scan.EmptyTrees.Count == 0)
            {
                return new CleanupPreview
                {
                    Scope = CleanupScope.EmptyFolders,
                    ScopePath = scan.RootDirectory,
                    HasTarget = false,
                    Message = scan.Message
                };
            }

            // 这些目录树按定义"任意层级都没有文件"，所以总大小恒为 0；条目数仍然量一遍，
            // 让用户在确认框里看到"要删掉多少个空目录层"。
            long totalBytes = 0;
            int entryCount = 0;
            bool determined = true;
            RecycleBinService deleteService = CreateDeleteService();

            foreach (string tree in scan.EmptyTrees)
            {
                DeleteMeasurement measurement = deleteService.MeasureTarget(tree);

                entryCount += Math.Max(measurement.EntryCount, 1);
                totalBytes += measurement.TotalBytes;
                determined &= measurement.Determined;
            }

            return new CleanupPreview
            {
                Scope = CleanupScope.EmptyFolders,
                ScopePath = scan.RootDirectory,
                AllowedRoot = scan.RootDirectory,
                HasTarget = true,
                ItemCount = scan.EmptyTrees.Count,
                EntryCount = entryCount,
                TotalBytes = totalBytes,
                Determined = determined,
                Items = scan.EmptyTrees.Take(CleanupPreview.MaxListedItems).ToList(),
                Message = $"将清理空文件夹：{scan.RootDirectory} 下有 {scan.EmptyTrees.Count} 个\"任意层级都没有文件\"的子目录" +
                          $"（共 {entryCount} 个目录 / {totalBytes} 字节）"
            };
        }

        // ================================================================ ③ 执行

        /// <summary>
        /// 执行其余物删除。<paramref name="mode"/> 默认档 = 回收站；
        /// <see cref="DeleteMode.Permanent"/> 只能由界面在"红色 + 二次确认"之后传进来。
        ///
        /// <para>
        /// ⚠ <paramref name="preview"/> 是**必填的语义**：作用域必须在预览阶段算好、在这里原样复用。
        /// 传 null 时本方法会自己算一遍（仍然只有一处实现），但调用方不该走那条路 ——
        /// "预览给用户看的是 A、执行删的是 B"正是上一个版本的缺陷形态。
        /// </para>
        /// </summary>
        public CleanupOutcome CleanProcessArtifacts(
            ArchiveTask? task,
            DeleteMode mode,
            CleanupPreview? preview = null,
            ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
        {
            ArtifactCleanupScope scope = preview?.ResolvedScope
                                         ?? ResolveArtifactScope(task, deleteScope);

            if (!scope.IsResolved || scope.RefusedUnsafe)
            {
                return NotAttempted(CleanupScope.Artifacts, mode, "已拒绝删除其余物：" + scope.BlockReason);
            }

            // 作用域必须与预览时那一刻一致（拿不出证据一致就不动手）：
            // 预览给用户看的是 A、执行删的是 B，正是上一版的缺陷形态。
            if (preview != null
                && (!SafePathHelper.PathEquals(preview.AllowedRoot, scope.AllowedRoot)
                    || !SameDirectorySet(preview.ResolvedScope?.ArtifactDirectories, scope.ArtifactDirectories)))
            {
                return NotAttempted(
                    CleanupScope.Artifacts,
                    mode,
                    "作用域与预览不一致（预览之后目录有变动），已拒绝删除；请重新预览一次");
            }

            string reason = scope.DeletesEverythingInDirectory ? AllArtifactsReason : ProcessArtifactReason;
            var requests = new List<DeleteRequest>();

            foreach (string directory in scope.ArtifactDirectories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                if (!ArchivePathGuard.IsInsideRoot(scope.AllowedRoot, directory, out string outsideReason))
                {
                    return NotAttempted(
                        CleanupScope.Artifacts,
                        mode,
                        $"其余物目录不在允许的范围内，已拒绝删除 —— {outsideReason}");
                }

                requests.Add(new DeleteRequest(directory, reason));
            }

            if (requests.Count == 0)
            {
                return NotAttempted(
                    CleanupScope.Artifacts,
                    mode,
                    $"没有可删除的其余物：{scope.ArtifactDirectory} 不存在");
            }

            DeleteResult result;

            try
            {
                result = CreateDeleteService().Delete(
                    requests,
                    new DeleteOptions
                    {
                        // 允许根：包的输出目录，或共享输出目录。其余物目录必须落在它之内，且不得等于它。
                        AllowedRoot = SafePathHelper.GetFullPathSafe(scope.AllowedRoot),
                        UserConfirmed = true,
                        Mode = mode,
                        Reason = reason
                    });
            }
            catch (Exception ex)
            {
                return NotAttempted(CleanupScope.Artifacts, mode, "删除其余物时出现意外错误：" + ex.Message);
            }

            return BuildOutcome(CleanupScope.Artifacts, mode, result);
        }

        /// <summary>执行空文件夹清理（作用域 = 任务输出根，不得越出）。</summary>
        public CleanupOutcome CleanEmptyFolders(string? rootDirectory, DeleteMode mode)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return NotAttempted(CleanupScope.EmptyFolders, mode, "没有可清理的空文件夹：当前任务还没有输出目录");
            }

            EmptyFolderCleanResult result;

            try
            {
                result = CreateEmptyFolderCleaner().Clean(new EmptyFolderCleanOptions
                {
                    RootDirectory = rootDirectory,
                    UserConfirmed = true,
                    Mode = mode,
                    Reason = EmptyFolderReason
                });
            }
            catch (Exception ex)
            {
                return NotAttempted(CleanupScope.EmptyFolders, mode, "清理空文件夹时出现意外错误：" + ex.Message);
            }

            if (!result.Scanned)
            {
                return NotAttempted(CleanupScope.EmptyFolders, mode, result.Message);
            }

            var logLines = new List<string>();

            if (result.DeleteResult != null)
            {
                logLines.AddRange(result.DeleteResult.LogEntries.Select(entry => entry.ToDisplayText()));
            }

            return new CleanupOutcome
            {
                Scope = CleanupScope.EmptyFolders,
                Mode = mode,
                Attempted = result.Attempted,
                SuccessCount = result.DeletedDirectories.Count,
                FailureCount = result.Skipped.Count(skip => skip.Reason == EmptyFolderSkipReason.DeleteFailed),
                FreedBytes = result.FreedBytes,
                RecycledBytes = result.RecycledBytes,
                FailureReasons = result.Skipped
                    .Where(skip => skip.Reason == EmptyFolderSkipReason.DeleteFailed)
                    .Select(skip => $"{skip.Path}：{skip.Message}")
                    .ToList(),
                LogLines = logLines,
                Message = result.Message
            };
        }

        // ================================================================ 内部

        /// <summary>其余物目录里的顶层条目名（目录在前、文件在后，各自按名字排序）。</summary>
        private static List<string> ListTopLevelEntries(string directory)
        {
            var items = new List<string>();

            items.AddRange(Directory.GetDirectories(directory)
                .Select(FileNameHelper.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

            items.AddRange(Directory.GetFiles(directory)
                .Select(FileNameHelper.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

            return items;
        }

        /// <summary>
        /// 顶层条目里的**源包文件**（删掉之后要重新下载的那些）。
        ///
        /// <para>
        /// 判据优先级（先精确、后兜底）：
        /// ① 任务自己登记的源文件（分卷组取整组各卷，单文件任务取它自己）—— 这是**精确**判据；
        /// ② 任务一个源文件都没登记（手工构造的任务等）时才退到"扩展名像归档"这个兜底判据。
        /// </para>
        /// <para>
        /// ⚠ 为什么不直接按扩展名全量判：其余物里同时躺着**内层归档**与**分卷**（<c>.7z.001</c> 这类），
        /// 它们由源包生成，删了还能再解出来；把它们也算成"源包"会让警告天天误报，
        /// 真出现源包时用户反而不当回事。分卷组的第一卷**是**源包（任务登记着它），所以仍然会报。
        /// </para>
        /// <para>只认文件、不认目录：目录名匹配归档后缀的（例如 <c>其余物\222\</c>）不构成"源包文件"。</para>
        /// </summary>
        private static List<string> FindSourcePackageNames(
            string directory,
            IReadOnlyList<string> topLevelItems,
            ArchiveTask? task)
        {
            List<string> registered = CollectRegisteredSourceFiles(task);
            bool useFallback = registered.Count == 0;
            var packages = new List<string>();

            foreach (string name in topLevelItems)
            {
                if (!SafePathHelper.FileExists(SafePathHelper.Combine(directory, name)))
                {
                    continue;
                }

                bool isSourcePackage = useFallback
                    ? LooksLikeArchiveFile(name)
                    : registered.Any(path => string.Equals(
                        FileNameHelper.GetFileName(path),
                        name,
                        StringComparison.OrdinalIgnoreCase));

                if (isSourcePackage)
                {
                    packages.Add(name);
                }
            }

            return packages;
        }

        /// <summary>
        /// 其余物里**源包文件**的精确判据：任务自己登记的源文件清单。
        ///
        /// 清单只来自任务自身，**不扫目录** —— 与"搬源包进其余物"和"清理源包"两处同一口径
        /// （那两处一个搬、一个删，这边是提示，三处必须认同一批源包）。
        /// 分卷组只认 <see cref="ArchiveTask.VolumePaths"/>：为空说明分组信息不完整，
        /// 与其猜一卷，不如什么都不报（少一个提示好过多一个假警报）。
        /// </summary>
        private static List<string> CollectRegisteredSourceFiles(ArchiveTask? task)
        {
            var paths = new List<string>();

            if (task == null)
            {
                return paths;
            }

            IEnumerable<string> candidates = task.IsVolumeGroup
                ? task.VolumePaths
                : new[] { task.CurrentPath };

            foreach (string candidate in candidates)
            {
                string full = SafePathHelper.GetFullPathSafe(candidate);

                if (!string.IsNullOrWhiteSpace(full)
                    && !paths.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(full);
                }
            }

            return paths;
        }

        /// <summary>
        /// 兜底判据：名字的扩展名是**归档本体**（<c>.7z</c>/<c>.rar</c>/<c>.zip</c>/<c>.tar.gz</c>…）。
        ///
        /// <para>
        /// 为什么兜底**不认**"伪装成常见文件"的后缀（<c>.jpg</c>/<c>.txt</c>）：那是识别阶段的事，
        /// 到了这个位置文件已经在其余物目录里躺着了，把一个 <c>readme.txt</c> 说成"源包、删了要重新下载"
        /// 是纯粹的误报（用户还会以为程序认错了格式）。宁可漏报一个改坏后缀的源包，
        /// 也不要每次弹窗都喊"含源包文件"。
        /// </para>
        /// </summary>
        private static bool LooksLikeArchiveFile(string fileName)
        {
            string extension = Path.GetExtension(fileName);

            return !string.IsNullOrWhiteSpace(extension)
                   && ExtensionHelper.IsKnownArchiveExtension(extension);
        }

        private static ArtifactCleanupScope Unresolved(
            string reason,
            string allowedRoot = "",
            bool shared = false,
            bool everything = false,
            bool refused = false)
        {
            return new ArtifactCleanupScope
            {
                Scope = CleanupScope.Artifacts,
                AllowedRoot = allowedRoot,
                DeletesEverythingInDirectory = everything,
                OutputDirectoryIsShared = shared,
                RefusedUnsafe = refused,
                BlockReason = reason
            };
        }

        /// <summary>
        /// 两组目录是不是同一个集合（顺序无关、大小写无关）。
        ///
        /// 预览与执行之间"目录少了一个 / 多了一个"都必须被发现：多一个意味着解析结果变了，
        /// 少一个意味着有东西在两次调用之间动过 —— 两种都不该继续删。
        /// </summary>
        private static bool SameDirectorySet(
            IReadOnlyList<string>? left,
            IReadOnlyList<string>? right)
        {
            var leftSet = new HashSet<string>(
                (left ?? Array.Empty<string>()).Select(SafePathHelper.GetFullPathSafe),
                StringComparer.OrdinalIgnoreCase);

            var rightSet = new HashSet<string>(
                (right ?? Array.Empty<string>()).Select(SafePathHelper.GetFullPathSafe),
                StringComparer.OrdinalIgnoreCase);

            return leftSet.SetEquals(rightSet);
        }

        /// <summary>把一次 <see cref="DeleteResult"/> 翻成界面上要的那几样东西。</summary>
        private static CleanupOutcome BuildOutcome(CleanupScope scope, DeleteMode mode, DeleteResult result)
        {
            return new CleanupOutcome
            {
                Scope = scope,
                Mode = mode,
                Attempted = true,
                SuccessCount = result.SuccessCount,
                FailureCount = result.FailureCount,
                FreedBytes = result.FreedBytes,
                RecycledBytes = result.RecycledBytes,
                FailureReasons = result.FailureReasons,
                LogLines = result.LogEntries.Select(entry => entry.ToDisplayText()).ToList(),
                Message = result.Message
            };
        }

        private static CleanupOutcome NotAttempted(CleanupScope scope, DeleteMode mode, string message)
        {
            return new CleanupOutcome
            {
                Scope = scope,
                Mode = mode,
                Attempted = false,
                Message = message
            };
        }

        /// <summary>删除服务：注入项的**唯一**装配处（保证探针与执行器两条路用的是同一份）。</summary>
        private RecycleBinService CreateDeleteService()
        {
            return new RecycleBinService(_executor, logSink: null, probe: _probe);
        }

        private EmptyFolderCleaner CreateEmptyFolderCleaner()
        {
            return new EmptyFolderCleaner(CreateDeleteService(), _probe);
        }
    }
}
