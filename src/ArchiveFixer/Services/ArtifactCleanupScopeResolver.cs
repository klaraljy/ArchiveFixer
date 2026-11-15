using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 其余物清理的**作用域与路径安全**解析处：本次清理该删哪一份、允许的删除根在哪一级、
/// 预览与执行两次解析出来的结论是不是同一份。
///
/// <para>
/// 为什么单独成类：这一层判据全部是路径事实（输出目录、源包目录、包基名、允许根），
/// 与"要不要删、删成功了几个"（预览 / 执行）没有关系 —— 任务信息导出、界面提示也直接调它。
/// 留在清理流程里会让"只读的路径解析"跟着删除逻辑一起改，而它的错法恰恰是**不可逆**的那种。
/// </para>
/// <para>
/// ⚠ 作用域**不得越出**（规格 §3.2 的硬约束）：包有自己的目录时只认
/// <c>&lt;任务输出目录&gt;\其余物\</c>；共享输出目录时只认
/// <c>&lt;共享根&gt;\其余物\&lt;本任务包基名&gt;\</c>，绝不碰别的包的目录。
/// 拿不准时一律**拒绝**（宁可少删，不可错删）——"退到上一层去删"正是本模块要根治的误删缺陷。
/// </para>
/// </summary>
public static class ArtifactCleanupScopeResolver
{
    /// <summary>
    /// 其余物目录名与"老名字也认"的规则**唯一来源**是 <see cref="ProcessArtifactLayout"/>：
    /// 目录名（<see cref="ProcessArtifactLayout.ArtifactDirectoryName"/>）、
    /// "已存在哪些"（<see cref="ProcessArtifactLayout.FindExistingArtifactDirectories"/>）
    /// 全部引用它，本类一个字面量都不写 —— 布局改名时这里必须跟着一起变，
    /// 而"跟着变"的唯一可靠做法就是不复制它的字面量。
    /// </summary>
    private static string ArtifactDirectoryName => ProcessArtifactLayout.ArtifactDirectoryName;

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
    /// （决策 D-10 的唯一实现处，含"共享根下按包基名分一层"），目录名兼容也走它。
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
    public static ArtifactCleanupScope Resolve(
        ArchiveTask? task,
        ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
    {
        if (task == null)
        {
            return Unresolved("没有选中的任务，无法确定要删哪一份其余物");
        }

        return Resolve(
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
    /// <param name="deleteScope">
    /// 只删本任务那一份（默认档），还是删整个共享目录；**只在共享输出目录时才有区别**。
    /// </param>
    public static ArtifactCleanupScope Resolve(
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

        /*
         * 其余物现在和内容物文件夹同级，也就是 outputFull 的父目录；
         * 允许删除的根必须跟着上移一层，否则 DeleteSafetyGuard 会把合法目标判成越界。
         */
        string allowedRoot = CleanupPlanner.ResolveAllowedRoot(outputFull);

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
                    AllowedRoot = allowedRoot,
                    BlockReason = $"没有可删除的其余物：当前任务的输出目录（{outputFull}）里没有 {ArtifactDirectoryName} 目录"
                };
            }

            return new ArtifactCleanupScope
            {
                Scope = CleanupScope.Artifacts,
                AllowedRoot = allowedRoot,
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
                AllowedRoot = allowedRoot,
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
                AllowedRoot = allowedRoot,
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
            AllowedRoot = allowedRoot,
            ArtifactDirectories = existing,
            OutputDirectoryIsShared = true,
            Description = $"{outputFull} 是多个包共用的输出目录，本次只删「{packageBaseName}」这一份：{string.Join("、", existing)}"
        };
    }

    /// <summary>
    /// 两组目录是不是同一个集合（顺序无关、大小写无关）。
    ///
    /// <para>
    /// 预览与执行之间"目录少了一个 / 多了一个"都必须被发现：多一个意味着解析结果变了，
    /// 少一个意味着有东西在两次调用之间动过 —— 两种都不该继续删。
    /// </para>
    /// </summary>
    public static bool SameDirectorySet(
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

    /// <summary>
    /// 某个内容物根下的其余物目录（**新旧名字都算**），且共享根下按包基名再分一层
    /// （决策 D-10 的唯一实现处是 <see cref="ProcessArtifactLayout.ResolveArtifactDirectoryWithName"/>）。
    ///
    /// <para>
    /// ⚠ 必须"两个名字各算一次"，不能拿 <c>D\其余物\222</c> 去喂
    /// <see cref="ProcessArtifactLayout.FindExistingArtifactDirectories"/>：
    /// 那个方法是"再往下一层找其余物目录"（它给参数追加 <c>\其余物</c>），
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
                     ProcessArtifactLayout.ArtifactDirectoryName
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
}
