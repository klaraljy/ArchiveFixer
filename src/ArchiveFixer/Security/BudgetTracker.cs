using ArchiveFixer.Storage;
using System.Threading;

namespace ArchiveFixer.Security;

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
                   $"{_options.MaxSingleFileSize} 字节（{FormatSize(_options.MaxSingleFileSize)}）" + ResourceBudget.CapHint;
        }

        if (count > _options.MaxFileCount)
        {
            return $"文件数已达 {count}，超过上限 {_options.MaxFileCount}" + ResourceBudget.CapHint;
        }

        if (total > _options.MaxTotalSize)
        {
            return $"累计解压大小 {total} 字节（{FormatSize(total)}）超过总大小上限 " +
                   $"{_options.MaxTotalSize} 字节（{FormatSize(_options.MaxTotalSize)}）" + ResourceBudget.CapHint;
        }

        return null;
    }

    /// <summary>给人看的累计进度（进日志 / 报告）。</summary>
    public string Describe()
    {
        return $"累计 {FileCount} 个文件 / {TotalBytes} 字节";
    }

    /// <summary>与 <see cref="ResourceBudget"/> 用同一套换算，避免两处显示口径不一致。</summary>
    private static string FormatSize(long bytes) => TaskSpaceEstimate.FormatSize(bytes);
}
