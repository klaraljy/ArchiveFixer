namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 7-Zip 退出码的语义档（<c>NExitCode</c> 那张表）。
///
/// 为什么要把这张表固化成枚举，而不是在各处写 <c>if (exitCode == 1)</c>：
/// 退出码是**外部进程的对外契约**，它的含义只有一份权威定义。散着写的结果是
/// "同一份输出在 list 路径判成成功、在 extract 路径判成失败"这类只在特定样本上才暴露的分歧。
/// </summary>
public enum SevenZipExitKind
{
    /// <summary>0：成功。</summary>
    Success = 0,

    /// <summary>1：**发生非致命错误**（部分文件没解出来、部分条目被跳过）。</summary>
    NonFatalWarning,

    /// <summary>2：发生致命错误。</summary>
    FatalError,

    /// <summary>3：无效校验和 / 数据损坏。</summary>
    DataError,

    /// <summary>4：试图修改被锁定的压缩文件（我们只读，正常不会遇到）。</summary>
    LockedArchive,

    /// <summary>5：写磁盘错误。</summary>
    WriteError,

    /// <summary>6：文件打开错误。</summary>
    OpenError,

    /// <summary>7：错误的命令行选项。</summary>
    CommandLineError,

    /// <summary>8：内存不足。</summary>
    OutOfMemory,

    /// <summary>9：文件创建错误。</summary>
    CreateError,

    /// <summary>10：没有找到与掩码和选项匹配的文件。</summary>
    NoMatchingItems,

    /// <summary>11：密码错误。</summary>
    WrongPassword,

    /// <summary>12：读取错误（6.00 起，读取错误提示里选任何一项都返回 12）。</summary>
    ReadError,

    /// <summary>255：用户中断。</summary>
    UserBreak,

    /// <summary>表里没有的码（含我们自己的 <c>-1/-2/-3</c> 哨兵值）。</summary>
    Unknown
}
