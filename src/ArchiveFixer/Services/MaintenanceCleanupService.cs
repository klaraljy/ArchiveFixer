using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Services;

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
/// 装配只在 <see cref="CleanupDeleteServices"/> 一处发生。
/// </para>
/// <para>
/// 本类是**门面**：公开签名一个都没变，调用方（界面、任务信息导出）不知道里面分了几层。
/// 职责分工：作用域与路径判据在 <see cref="ArtifactCleanupScopeResolver"/>；
/// 其余物清单与源包识别在 <see cref="ArtifactDirectoryInspector"/>；
/// 预览在 <see cref="ArtifactCleanupPreviewer"/>；执行在 <see cref="ArtifactCleanupExecutor"/>；
/// 空文件夹清理在 <see cref="EmptyFolderCleanupService"/>；
/// 结果到界面结论的映射在 <see cref="CleanupOutcomeMapper"/>。
/// 门面自己只保留三件事：公开 API、清理理由常量、注入项的默认值与装配。
/// </para>
/// </summary>
public sealed class MaintenanceCleanupService
{
    private readonly ArtifactCleanupPreviewer _artifactPreviewer;
    private readonly ArtifactCleanupExecutor _artifactExecutor;
    private readonly EmptyFolderCleanupService _emptyFolderCleanup;

    /// <summary>
    /// 创建门面并按**唯一一份**注入项装配三个协作类。
    ///
    /// <para>
    /// 两个参数都可为空（为空时用真实的 Shell 删除 / Windows 探针）。
    /// 注入哪一个就用哪一个：预览的度量、其余物删除、空文件夹扫描与删除**全部**走同一份，
    /// 不允许某一条路径偷偷换成默认实现（那样"扫描看到的"和"删除时校验的"会是两个世界）。
    /// </para>
    /// </summary>
    public MaintenanceCleanupService(
        IDeleteExecutor? executor = null,
        IDeleteFileSystemProbe? probe = null)
    {
        var deleteServices = new CleanupDeleteServices(
            executor ?? new ShellDeleteExecutor(),
            probe ?? WindowsDeleteFileSystemProbe.Instance);

        _artifactPreviewer = new ArtifactCleanupPreviewer(deleteServices);
        _artifactExecutor = new ArtifactCleanupExecutor(deleteServices);
        _emptyFolderCleanup = new EmptyFolderCleanupService(deleteServices);
    }

    /// <summary>清理理由（写进删除日志；两档共用同一句话）。</summary>
    public const string ProcessArtifactReason = ArtifactCleanupExecutor.ProcessArtifactReason;

    /// <summary>「删除本目录全部其余物」的理由：说清这次不是只删本任务那一份。</summary>
    public const string AllArtifactsReason = ArtifactCleanupExecutor.AllArtifactsReason;

    /// <summary>空文件夹清理的理由（写进删除日志；预览与执行共用同一句话）。</summary>
    public const string EmptyFolderReason = EmptyFolderCleanupService.EmptyFolderReason;

    /// <summary>
    /// 其余物目录名与"老名字也认"的规则**唯一来源**是 <see cref="ProcessArtifactLayout"/>：
    /// 目录名（<see cref="ProcessArtifactLayout.ArtifactDirectoryName"/>）、
    /// "已存在哪些"（<see cref="ProcessArtifactLayout.FindExistingArtifactDirectories"/>）
    /// 全部引用它，本类一个字面量都不写 —— 布局改名时这里必须跟着一起变，
    /// 而"跟着变"的唯一可靠做法就是不复制它的字面量。
    /// </summary>
    public static string ArtifactDirectoryName => ProcessArtifactLayout.ArtifactDirectoryName;

    /// <summary>
    /// 作用域解析的**唯一入口**：当前勾选任务的其余物是哪一份。判据、允许根与拒绝条件
    /// 全部在 <see cref="ArtifactCleanupScopeResolver"/> 里（本方法只是转发）。
    /// </summary>
    public static ArtifactCleanupScope ResolveArtifactScope(
        ArchiveTask? task,
        ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
    {
        return ArtifactCleanupScopeResolver.Resolve(task, deleteScope);
    }

    /// <summary>
    /// 作用域解析的字符串重载。存在的理由是**可测**：不必为了验证一条路径判定
    /// 去构造整个 <see cref="ArchiveTask"/>。
    /// </summary>
    public static ArtifactCleanupScope ResolveArtifactScope(
        string? outputDirectory,
        string? sourceArchivePath = null,
        string? volumeGroupBaseName = null,
        ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
    {
        return ArtifactCleanupScopeResolver.Resolve(
            outputDirectory,
            sourceArchivePath,
            volumeGroupBaseName,
            deleteScope);
    }

    /// <summary>
    /// 其余物清理的作用域路径（相当于解析出来的落点）。拿不到时返回空串。
    /// </summary>
    public static string ResolveProcessArtifactScope(ArchiveTask? task)
    {
        return ArtifactCleanupScopeResolver.Resolve(task).ArtifactDirectory;
    }

    /// <summary>
    /// 预览其余物删除：条目数 + 总大小 + 顶层条目清单 + 源包识别。**不删除任何东西**。
    /// </summary>
    public CleanupPreview PreviewProcessArtifacts(
        ArchiveTask? task,
        ArtifactDeleteScope deleteScope = ArtifactDeleteScope.SelectedTask)
    {
        return _artifactPreviewer.Preview(task, deleteScope);
    }

    /// <summary>
    /// 预览空文件夹清理：候选目录清单（"任意层级都没有文件"的第一层子目录）。**不删除任何东西**。
    /// </summary>
    public CleanupPreview PreviewEmptyFolders(string? rootDirectory)
    {
        return _emptyFolderCleanup.Preview(rootDirectory);
    }

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
        return _artifactExecutor.Execute(task, mode, preview, deleteScope);
    }

    /// <summary>执行空文件夹清理（作用域 = 任务输出根，不得越出）。</summary>
    public CleanupOutcome CleanEmptyFolders(string? rootDirectory, DeleteMode mode)
    {
        return _emptyFolderCleanup.Clean(rootDirectory, mode);
    }
}
