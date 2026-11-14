namespace ArchiveFixer.Services;

/// <summary>
/// 各分桶的计数。**硬不变量：<see cref="Total"/> 恒等于任务总数**（互斥且可加），
/// 所以"各分项之和 == 任务数"是构造出来的性质，不是靠调用方自觉。
/// </summary>
public sealed class SummaryBucketCounts
{
    public int Pending { get; init; }

    public int RenameSuccess { get; init; }

    public int RenameFailed { get; init; }

    public int TestPassed { get; init; }

    public int TestFailed { get; init; }

    public int ExtractSuccess { get; init; }

    public int PasswordError { get; init; }

    public int Corrupted { get; init; }

    public int ExtractFailed { get; init; }

    public int Skipped { get; init; }

    public int Cancelled { get; init; }

    public int OtherFailed { get; init; }

    /// <summary>全部分桶之和 —— 按定义等于任务总数。</summary>
    public int Total =>
        Pending +
        RenameSuccess + RenameFailed +
        TestPassed + TestFailed +
        ExtractSuccess + ExtractFailed +
        PasswordError + Corrupted +
        Skipped + Cancelled + OtherFailed;

    /// <summary>失败侧合计（与界面"失败总数"同口径，不含"待处理"，也不含任何重复计数）。</summary>
    public int FailureTotal =>
        RenameFailed + TestFailed + ExtractFailed + PasswordError + Corrupted + OtherFailed;

    public int Get(SummaryBucket bucket)
    {
        return bucket switch
        {
            SummaryBucket.Pending => Pending,
            SummaryBucket.RenameSuccess => RenameSuccess,
            SummaryBucket.RenameFailed => RenameFailed,
            SummaryBucket.TestPassed => TestPassed,
            SummaryBucket.TestFailed => TestFailed,
            SummaryBucket.ExtractSuccess => ExtractSuccess,
            SummaryBucket.PasswordError => PasswordError,
            SummaryBucket.Corrupted => Corrupted,
            SummaryBucket.ExtractFailed => ExtractFailed,
            SummaryBucket.Skipped => Skipped,
            SummaryBucket.Cancelled => Cancelled,
            SummaryBucket.OtherFailed => OtherFailed,
            _ => Pending
        };
    }
}
