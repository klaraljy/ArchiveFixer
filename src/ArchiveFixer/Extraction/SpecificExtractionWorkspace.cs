using ArchiveFixer.Storage;
using System;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 特定解压的**唯一工作区创建出口**：父工作区根（<c>&lt;输出根&gt;\.ArchiveFixer.work</c>）、
/// 本任务目录、每层目录、标准分卷入口目录，全部由这里创建并立刻标隐藏。
///
/// <para>为什么单独一个类：特定解压原来在
/// <see cref="SpecificExtractionRunner"/> 里直接 <see cref="Directory.CreateDirectory(string)"/>，
/// 绕开了普通解压的 <see cref="WorkspaceTree"/> 隐藏出口 —— 用户真机看到
/// <c>.ArchiveFixer.work</c> 和任务目录是可见的（2026-11-15）。隐藏属性不继承，
/// 只标父目录或只标子目录都会漏；把创建与标记收进一个类，runner、调用方和测试
/// 都只认这一条路径，⛔ 不再各自拼 <c>.ArchiveFixer.work</c> 或自行标记。</para>
///
/// <para>隐藏是尽力而为：<see cref="WorkspaceTree.MarkHidden"/> 设不上不影响解压本身；
/// 但目录建不出来仍按原来的 <see cref="Directory.CreateDirectory(string)"/> 语义抛异常。</para>
/// </summary>
internal sealed class SpecificExtractionWorkspace
{
    private const string TaskDirectoryPrefix = "specific-";
    private const string LayerDirectoryPrefix = "layer-";
    private const string StandardVolumeEntryDirectoryName = "standard-volume-entry";

    private SpecificExtractionWorkspace(string rootDirectory, string taskDirectory)
    {
        RootDirectory = rootDirectory;
        TaskDirectory = taskDirectory;
    }

    /// <summary>工作区父目录（<c>&lt;输出根&gt;\.ArchiveFixer.work</c>），永远隐藏。</summary>
    internal string RootDirectory { get; }

    /// <summary>本次特定解压的任务目录（<c>specific-&lt;guid&gt;</c>），永远隐藏。</summary>
    internal string TaskDirectory { get; }

    /// <summary>
    /// 在输出根下创建这一趟的隐藏工作区。父目录已存在时也补标 Hidden；
    /// 任务目录建不出来时收掉刚建的空父目录，保持“失败不留空壳”的既有口径。
    /// </summary>
    internal static SpecificExtractionWorkspace Create(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("特定解压输出根不能为空", nameof(outputRoot));
        }

        string rootDirectory = Path.Combine(
            outputRoot,
            WorkspaceRootResolver.DefaultWorkspaceDirectoryName);

        Directory.CreateDirectory(rootDirectory);
        WorkspaceTree.MarkHidden(rootDirectory);

        string taskDirectory = Path.Combine(
            rootDirectory,
            TaskDirectoryPrefix + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(taskDirectory);
            WorkspaceTree.MarkHidden(taskDirectory);
        }
        catch
        {
            TryDeleteEmptyRoot(rootDirectory);
            throw;
        }

        return new SpecificExtractionWorkspace(rootDirectory, taskDirectory);
    }

    /// <summary>创建第 <paramref name="depth"/> 层目录并标隐藏（替代 runner 里的裸建目录）。</summary>
    internal string CreateLayer(int depth)
    {
        string layerDirectory = Path.Combine(
            TaskDirectory,
            LayerDirectoryPrefix + depth.ToString("D2"));

        Directory.CreateDirectory(layerDirectory);
        WorkspaceTree.MarkHidden(layerDirectory);

        return layerDirectory;
    }

    /// <summary>在指定层里创建标准卷入口目录并标隐藏。</summary>
    internal string CreateStandardVolumeEntry(string layerDirectory)
    {
        string entryDirectory = Path.Combine(layerDirectory, StandardVolumeEntryDirectoryName);

        Directory.CreateDirectory(entryDirectory);
        WorkspaceTree.MarkHidden(entryDirectory);

        return entryDirectory;
    }

    /// <summary>
    /// 收掉本任务目录；父工作区根只有在**空**时才删（与调用方原来的 finally 完全同口径）。
    /// 任何删除异常都吞掉：清理失败不能让已经成功的解压变成失败。
    /// </summary>
    internal void Cleanup()
    {
        try
        {
            if (Directory.Exists(TaskDirectory))
            {
                Directory.Delete(TaskDirectory, recursive: true);
            }
        }
        catch
        {
        }

        TryDeleteEmptyRoot(RootDirectory);
    }

    private static void TryDeleteEmptyRoot(string rootDirectory)
    {
        try
        {
            if (Directory.Exists(rootDirectory)
                && !Directory.EnumerateFileSystemEntries(rootDirectory).Any())
            {
                Directory.Delete(rootDirectory);
            }
        }
        catch
        {
        }
    }
}
