using System;

namespace ArchiveFixer.Engines.WinRar
{
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

    /// <summary>
    /// UnRAR 退出码映射（<c>Engines/WinRar/</c> 内唯一一份）。
    ///
    /// 权威来源：RAR 控制台手册的返回码表（`docs/WinRAR功能参考.md` §1.15），
    /// 并在本机 <c>UNRAR 7.23 x64 freeware</c> 上用真样本逐条实测过（结论写在注释里，
    /// 免得后人以为这些码是照抄文档）。
    /// </summary>
    public static class UnRarExitCodes
    {
        public const int Success = 0;
        public const int NonFatalWarning = 1;
        public const int FatalError = 2;
        public const int DataError = 3;
        public const int LockedArchive = 4;
        public const int WriteError = 5;
        public const int OpenError = 6;
        public const int CommandLineError = 7;
        public const int OutOfMemory = 8;
        public const int CreateError = 9;
        public const int NoMatchingItems = 10;
        public const int WrongPassword = 11;
        public const int ReadError = 12;
        public const int UserBreak = 255;

        /// <summary>把退出码翻成语义档；表里没有的码（含负数哨兵）一律 <see cref="UnRarExitKind.Unknown"/>。</summary>
        public static UnRarExitKind Classify(int exitCode)
        {
            return exitCode switch
            {
                Success => UnRarExitKind.Success,
                NonFatalWarning => UnRarExitKind.NonFatalWarning,
                FatalError => UnRarExitKind.FatalError,
                DataError => UnRarExitKind.DataError,
                LockedArchive => UnRarExitKind.LockedArchive,
                WriteError => UnRarExitKind.WriteError,
                OpenError => UnRarExitKind.OpenError,
                CommandLineError => UnRarExitKind.CommandLineError,
                OutOfMemory => UnRarExitKind.OutOfMemory,
                CreateError => UnRarExitKind.CreateError,
                NoMatchingItems => UnRarExitKind.NoMatchingItems,
                WrongPassword => UnRarExitKind.WrongPassword,
                ReadError => UnRarExitKind.ReadError,
                UserBreak => UnRarExitKind.UserBreak,
                _ => UnRarExitKind.Unknown
            };
        }

        /// <summary>
        /// 非致命（退出码 1）：**部分成功，绝不算成功**（AGENTS.md §6 第 6 条）。
        /// </summary>
        public static bool IsNonFatal(int exitCode) => exitCode == NonFatalWarning;

        /// <summary>
        /// 退出码 → 错误类型；<c>Success</c> 与"这层没有结论"的码返回 null，
        /// 让输出关键字分类先说话（关键字能给出比"退出码 11"具体得多的结论）。
        ///
        /// ⚠ 与 7-Zip 那侧刻意保持一致：**只给 1 / 2 / 7 / 8 / 255 硬映射**。
        /// 尤其是 11（密码错误）**不**在这里硬映射 —— 它在"列目录"路径上要先经过
        /// "加密文件名"判定，硬映射会把那个更好的结论盖掉。
        /// </summary>
        public static string? ToErrorType(int exitCode)
        {
            return exitCode switch
            {
                NonFatalWarning => EngineErrorTypes.NonFatalError,
                FatalError => EngineErrorTypes.FatalError,
                CommandLineError => EngineErrorTypes.CommandLineError,
                OutOfMemory => EngineErrorTypes.OutOfMemory,
                UserBreak => EngineErrorTypes.Cancelled,
                _ => null
            };
        }

        /// <summary>退出码的中文说明（进日志与错误消息用；未知码返回空字符串，不编造含义）。</summary>
        public static string Describe(int exitCode)
        {
            return Classify(exitCode) switch
            {
                UnRarExitKind.Success => "成功",
                UnRarExitKind.NonFatalWarning => "发生非致命错误（部分文件可能未解出）",
                UnRarExitKind.FatalError => "发生致命错误",
                UnRarExitKind.DataError => "无效校验和 / 数据损坏",
                UnRarExitKind.LockedArchive => "压缩文件被锁定，拒绝修改",
                UnRarExitKind.WriteError => "写磁盘错误",
                UnRarExitKind.OpenError => "文件打开错误（分卷缺失 / 无法打开）",
                UnRarExitKind.CommandLineError => "错误的命令行选项",
                UnRarExitKind.OutOfMemory => "内存不足",
                UnRarExitKind.CreateError => "文件创建错误",
                UnRarExitKind.NoMatchingItems => "没有找到匹配的文件",
                UnRarExitKind.WrongPassword => "密码错误",
                UnRarExitKind.ReadError => "读取错误（需要密码但无法询问）",
                UnRarExitKind.UserBreak => "用户中断",
                _ => string.Empty
            };
        }
    }
}
