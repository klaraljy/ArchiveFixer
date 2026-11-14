namespace ArchiveFixer.Services;

/// <summary>
/// 任务汇总的**结果分桶**：每个任务恰好落进一个桶。
///
/// 为什么要有这个东西：旧的 <see cref="TaskSummaryService.BuildSummary"/> 用一堆互不排斥的
/// <c>Count(...)</c> 拼出来，同一个任务会同时进"解压失败"和"其他失败"（`UnknownError`）、
/// 同时进"解压失败"和"密码错误"（`WrongPassword`）—— 用户看到的就是"汇总数字前后对不上：
/// 成功 + 失败 + 跳过 比总数还大"。分桶是唯一真相来源，界面字段只能从桶里取数，
/// 不允许再出现第二套判断。
/// </summary>
public enum SummaryBucket
{
    /// <summary>还没有结果：等待扫描 / 已识别 / 进行中。它也是分桶的一部分，只是界面上没有单独一项。</summary>
    Pending = 0,

    RenameSuccess,
    RenameFailed,
    TestPassed,
    TestFailed,
    ExtractSuccess,

    /// <summary>密码错误（<see cref="StatusText.WrongPassword"/>）。**只有它算这一类**。</summary>
    PasswordError,

    Corrupted,

    /// <summary>解压侧其它失败：解压失败 / 权限不足 / 输出路径冲突 / 分卷缺失 / 路径过长 / 7z不存在 / 未知错误 / 达到密码尝试上限。</summary>
    ExtractFailed,

    Skipped,
    Cancelled,

    /// <summary>部分完成、格式未知、文件名已加密，以及将来新增但还没归类的失败状态。</summary>
    OtherFailed
}
