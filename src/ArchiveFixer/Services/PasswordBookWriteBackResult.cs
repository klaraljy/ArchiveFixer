using ArchiveFixer.Password;

namespace ArchiveFixer.Services;

/// <summary>
/// 一次「写回密码本」的结论。
///
/// <para>
/// <b>这里绝不携带密码原文</b>（AGENTS.md §6 不变量 5）：调用方拿这个对象去写日志、写界面提示条、
/// 拼失败原因，都不该、也不需要知道写了哪些密码。数量、文件、备份路径足够说清发生了什么。
/// </para>
/// </summary>
public sealed class PasswordBookWriteBackResult
{
    /// <summary>这次是不是成功了。失败时 <see cref="FailureReason"/> 一定有内容。</summary>
    public bool Success { get; init; }

    /// <summary>实际追加进文件的行数。</summary>
    public int AppendedCount { get; init; }

    /// <summary>没被写进去的条数（空密码、文件里已经有了、批内重复）。</summary>
    public int SkippedCount { get; init; }

    /// <summary>其中"因为文件里已经有同样的值"而没写的条数。</summary>
    public int AlreadyPresentCount { get; init; }

    /// <summary>其中"空密码"（长度为 0）被跳过的条数。</summary>
    public int SkippedEmptyCount { get; init; }

    /// <summary>
    /// 其中"只由空白字符组成"被跳过的条数。
    ///
    /// <para>为什么它们也不能写：本类写完会用 <see cref="PasswordBookParser.ParseFile"/> 自检，
    /// 而解析器把**只有空白字符的行**当成排版空行跳过（AGENTS.md §9.1 里那条取舍）——
    /// 写进去 = 立刻自检失败，用户会看到一条莫名其妙的失败。
    /// 与其写一条"下次启动读不回来"的内容，不如现在就如实说清它被跳过了。</para>
    /// </summary>
    public int SkippedBlankCount { get; init; }

    /// <summary>其中"同一次里重复出现"被跳过的条数。</summary>
    public int SkippedDuplicateCount { get; init; }

    /// <summary>写入的目标密码本文件（失败时是原本打算写的那个）。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>写前备份文件路径；一条都没写时为空串（没有备份，也没有改动）。</summary>
    public string BackupPath { get; init; } = string.Empty;

    /// <summary>失败原因（给用户看的一句话，**不含密码原文**）；成功时为空串。</summary>
    public string FailureReason { get; init; } = string.Empty;

    /// <summary>是否需要告诉用户"原文件一个字节都没变"。</summary>
    public bool IsNoOp => AppendedCount == 0 && Success;

    public static PasswordBookWriteBackResult Failure(string targetPath, string reason)
    {
        return new PasswordBookWriteBackResult
        {
            Success = false,
            TargetPath = targetPath ?? string.Empty,
            FailureReason = reason ?? string.Empty
        };
    }
}
