using System;

namespace ArchiveFixer.Engines.SevenZip
{
    /// <summary>
    /// 7-Zip 退出码映射 —— **全仓唯一**的一份（AGENTS.md §3.1 禁止项②的边界：
    /// 7-Zip 的文本解析与退出码解释只允许待在 <c>Engines/SevenZip/</c> 里）。
    ///
    /// 权威来源：RAR 手册的返回码表（`docs/WinRAR功能参考.md` §1.15）与 7-Zip 自己的
    /// <c>NExitCode</c> 表完全一致：0 成功 / **1 警告（非致命）** / 2 致命 / 3 校验和 /
    /// 4 锁定 / 5 写错 / 6 打开错 / 7 参数错 / 8 内存 / 9 创建错 / 10 无匹配 / 11 密码错 /
    /// 12 读错 / 255 用户中断。
    ///
    /// ⚠ 本机 26.01 实测补充（写下来免得后人以为这些码是拍脑袋定的）：
    /// · "There are data after the end of archive" 这类**警告在 26.01 里仍然返回 0**
    ///   （`Warnings: 1` 只出现在输出里，不影响退出码），所以"1 = 有警告"不能反过来解释成
    ///   "没有 1 就没有问题"；
    /// · 加密头（<c>-mhe</c>）的包用 `l` 列目录失败时报 **2**，数据加密（非加密头）的包
    ///   不给密码也能列目录、返回 **0** —— 这两条实测是"加密文件名"判定的证据基础，
    ///   见 <see cref="SevenZipOutputParser.LooksLikeEncryptedHeaders"/>。
    /// </summary>
    public static class SevenZipExitCodes
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

        /// <summary>把退出码翻成语义档。表里没有的码（含负数哨兵）一律 <see cref="SevenZipExitKind.Unknown"/>。</summary>
        public static SevenZipExitKind Classify(int exitCode)
        {
            return exitCode switch
            {
                Success => SevenZipExitKind.Success,
                NonFatalWarning => SevenZipExitKind.NonFatalWarning,
                FatalError => SevenZipExitKind.FatalError,
                DataError => SevenZipExitKind.DataError,
                LockedArchive => SevenZipExitKind.LockedArchive,
                WriteError => SevenZipExitKind.WriteError,
                OpenError => SevenZipExitKind.OpenError,
                CommandLineError => SevenZipExitKind.CommandLineError,
                OutOfMemory => SevenZipExitKind.OutOfMemory,
                CreateError => SevenZipExitKind.CreateError,
                NoMatchingItems => SevenZipExitKind.NoMatchingItems,
                WrongPassword => SevenZipExitKind.WrongPassword,
                ReadError => SevenZipExitKind.ReadError,
                UserBreak => SevenZipExitKind.UserBreak,
                _ => SevenZipExitKind.Unknown
            };
        }

        /// <summary>
        /// 非致命错误（退出码 1）：**部分成功，绝不算成功**（AGENTS.md §6 第 6 条）。
        ///
        /// 7-Zip 在"一部分文件解出来了、另一部分失败"时正是给 1 ——
        /// 这是"部分成功被显示成成功"最容易漏掉的一条路径。
        /// </summary>
        public static bool IsNonFatal(int exitCode) => exitCode == NonFatalWarning;

        /// <summary>
        /// 退出码 → 错误类型（<c>Success</c> / 未知码返回 null，表示"这层没有结论，交给输出关键字分类"）。
        ///
        /// ⚠ 刻意**只给 1 / 2 / 7 / 8 / 255 硬映射**，其余码按现状仍由输出关键字判定：
        /// 关键字能给出比"退出码 11"具体得多的结论（密码错误 / 缺卷 / 路径过长），
        /// 硬映射反而会把好结论盖成"未知错误"。表里其余的码照样在
        /// <see cref="Classify"/> 里有定义，只是不参与"退出码优先"的那一步。
        /// </summary>
        public static string? ToErrorType(int exitCode)
        {
            return exitCode switch
            {
                NonFatalWarning => SevenZipOutputParser.NonFatalErrorType,
                FatalError => SevenZipOutputParser.FatalErrorType,
                CommandLineError => "CommandLineError",
                OutOfMemory => SevenZipOutputParser.OutOfMemoryErrorType,
                UserBreak => "Cancelled",
                _ => null
            };
        }

        /// <summary>退出码的中文说明（进日志与错误消息用；未知码返回空字符串，不编造含义）。</summary>
        public static string Describe(int exitCode)
        {
            return Classify(exitCode) switch
            {
                SevenZipExitKind.Success => "成功",
                SevenZipExitKind.NonFatalWarning => "发生非致命错误（部分文件可能未解出）",
                SevenZipExitKind.FatalError => "发生致命错误",
                SevenZipExitKind.DataError => "无效校验和 / 数据损坏",
                SevenZipExitKind.LockedArchive => "压缩文件被锁定，拒绝修改",
                SevenZipExitKind.WriteError => "写磁盘错误",
                SevenZipExitKind.OpenError => "文件打开错误",
                SevenZipExitKind.CommandLineError => "错误的命令行选项",
                SevenZipExitKind.OutOfMemory => "内存不足",
                SevenZipExitKind.CreateError => "文件创建错误",
                SevenZipExitKind.NoMatchingItems => "没有找到匹配的文件",
                SevenZipExitKind.WrongPassword => "密码错误",
                SevenZipExitKind.ReadError => "读取错误",
                SevenZipExitKind.UserBreak => "用户中断",
                _ => string.Empty
            };
        }
    }
}
