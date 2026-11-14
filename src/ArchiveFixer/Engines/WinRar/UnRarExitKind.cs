namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// RAR / UnRAR 退出码的语义档（`docs/WinRAR功能参考.md` §1.15 那张表）。
///
/// ⚠ 与 7-Zip 的那张表**数字相同但来源不同**：它们是两套独立的对外契约，
/// 只是历史上 RAR 与 7-Zip 用了同一组数字。所以本文件是 UnRAR 这一侧的**唯一**映射处，
/// 不许让别的文件去 `if (exitCode == 11)`（AGENTS.md §3.1 禁止项②的边界）。
/// </summary>
public enum UnRarExitKind
{
    /// <summary>0：成功。</summary>
    Success = 0,

    /// <summary>1：**警告，发生非致命错误**（部分文件没解出来）—— 绝不算成功（不变量 6）。</summary>
    NonFatalWarning,

    /// <summary>2：发生致命错误。</summary>
    FatalError,

    /// <summary>3：无效校验和 / 数据损坏。</summary>
    DataError,

    /// <summary>4：试图修改被 <c>k</c> 锁定的压缩文件（我们只读，正常不会遇到）。</summary>
    LockedArchive,

    /// <summary>5：写磁盘错误。</summary>
    WriteError,

    /// <summary>6：文件打开错误。**实测：缺分卷走的就是这个码**（配 "Cannot find volume"）。</summary>
    OpenError,

    /// <summary>7：错误的命令行选项。</summary>
    CommandLineError,

    /// <summary>8：内存不足。</summary>
    OutOfMemory,

    /// <summary>9：文件创建错误（输出目录不可写 / 磁盘满）。</summary>
    CreateError,

    /// <summary>10：没有找到与掩码和选项匹配的文件。**实测：归档文件本身不存在时也是这个码。**</summary>
    NoMatchingItems,

    /// <summary>11：密码错误。**实测：加密文件名的包不给密码 / 给错密码也是这个码。**</summary>
    WrongPassword,

    /// <summary>12：读取错误。**实测：需要密码却没给、标准输入被关掉时的"问密码失败"走这个码。**</summary>
    ReadError,

    /// <summary>255：用户中断。</summary>
    UserBreak,

    /// <summary>表里没有的码（含我们自己的 <c>-1/-2/-3</c> 哨兵值）。</summary>
    Unknown
}
