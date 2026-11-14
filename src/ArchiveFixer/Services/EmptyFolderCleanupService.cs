using ArchiveFixer.Extraction;
using ArchiveFixer.Storage;
using System;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 空文件夹清理（「清理空文件夹…」入口）的预览与执行。
///
/// <para>
/// 与其余物清理是**两条独立的作用域**：其余物要按包收窄（共享输出目录下只删自己那一份），
/// 空文件夹清理按定义就是"扫这个根"—— 只删"任意层级都没有文件"的第一层子目录树，
/// 所以它保持"根 = 任务输出目录"不变（规格 §5）。
/// </para>
/// <para>
/// 扫描与判定全部交给 <see cref="EmptyFolderCleaner"/>（它保持原 <c>删除空文件夹.bat</c> 的语义）；
/// 本类只做两件它不负责的事：把扫描结果翻成预览（**一个字节都不写盘**）与把清理结果翻成界面结论。
/// 真正动手的仍然是 <see cref="RecycleBinService"/>，根内校验 / 链接 / 确认标志 / 日志都在它那一套里。
/// </para>
/// </summary>
public sealed class EmptyFolderCleanupService
{
    /// <summary>清理理由（写进删除日志；预览与执行共用同一句话）。</summary>
    internal const string EmptyFolderReason = "清理空文件夹（任意层级都没有文件）";

    private readonly CleanupDeleteServices _deleteServices;

    /// <param name="deleteServices">注入项装配（扫描与删除必须用同一个探针）。</param>
    public EmptyFolderCleanupService(CleanupDeleteServices deleteServices)
    {
        _deleteServices = deleteServices;
    }

    /// <summary>
    /// 预览空文件夹清理：候选目录清单（"任意层级都没有文件"的第一层子目录）。**不删除任何东西**。
    /// </summary>
    public CleanupPreview Preview(string? rootDirectory)
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
        EmptyFolderCleanResult scan = _deleteServices.CreateEmptyFolderCleaner().Clean(new EmptyFolderCleanOptions
        {
            RootDirectory = rootDirectory,
            UserConfirmed = false,
            // ⚠ 2026-10-15：档位跟着真实动作走 —— 回收站下线后手工清理只剩"彻底删除"
            // （这一步一个字节都不写盘，只影响报告里的措辞，但两处口径必须一致）。
            Mode = DeleteMode.Permanent,
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
        RecycleBinService deleteService = _deleteServices.CreateDeleteService();

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

    /// <summary>执行空文件夹清理（作用域 = 任务输出根，不得越出）。</summary>
    public CleanupOutcome Clean(string? rootDirectory, DeleteMode mode)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return CleanupOutcomeMapper.NotAttempted(
                CleanupScope.EmptyFolders,
                mode,
                "没有可清理的空文件夹：当前任务还没有输出目录");
        }

        EmptyFolderCleanResult result;

        try
        {
            result = _deleteServices.CreateEmptyFolderCleaner().Clean(new EmptyFolderCleanOptions
            {
                RootDirectory = rootDirectory,
                UserConfirmed = true,
                Mode = mode,
                Reason = EmptyFolderReason
            });
        }
        catch (Exception ex)
        {
            return CleanupOutcomeMapper.NotAttempted(
                CleanupScope.EmptyFolders,
                mode,
                "清理空文件夹时出现意外错误：" + ex.Message);
        }

        if (!result.Scanned)
        {
            return CleanupOutcomeMapper.NotAttempted(CleanupScope.EmptyFolders, mode, result.Message);
        }

        return CleanupOutcomeMapper.FromEmptyFolderResult(result, mode);
    }
}
