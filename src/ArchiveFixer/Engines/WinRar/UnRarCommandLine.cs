using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// UnRAR 的**命令行参数**这一块：怎么拼、参数表里哪个是命令/归档、详细日志那份脱敏摘要。
///
/// <para>
/// 边界（AGENTS.md §3.1 四条禁止项）：参数拼接、输出解析、退出码到错误的映射
/// **全部只能存在于 <c>Engines/WinRar/</c> 内**。核心模块一律通过 <see cref="IArchiveEngine"/> 调用。
/// 本类只碰参数：不起进程（<see cref="UnRarProcessLauncher"/>）、不读输出
/// （<see cref="UnRarOutputCapture"/>）、不判退出码（<see cref="UnRarResultClassifier"/>）。
/// </para>
///
/// <para>
/// 与 7-Zip 运行器的**实测差异**（写下来免得后来人以为是抄漏了）：
/// <list type="number">
/// <item><description><b>必须加 <c>-scf</c></b>：UnRAR 默认按系统 OEM 代码页输出（中文系统 = GBK），
/// 而我们按 UTF-8 解码 → 不加这个开关中文条目名全是乱码（7.23 实测）。</description></item>
/// <item><description><b>密码一律显式给</b>：<c>-p&lt;密码&gt;</c>，空密码/不传密码时给 <c>-p-</c>
/// （= 不询问密码）。⛔ 绝不能传裸的 <c>-p</c>：那会让 UnRAR 去**问密码**，
/// 而我们的标准输入是关着的 —— 实测结果是 "Enter password …" + "Read error in the file stdin" + 退出码 12，
/// 于是"需要密码"会被误报成"读取错误"。</description></item>
/// <item><description><b>不用 <c>-y</c></b>：<c>-y</c> 是"所有询问都回答是"，
/// 它同时会把覆盖策略也变成"覆盖全部" —— 与我们"默认不覆盖"的落位约定（不变量 3）冲突。
/// 覆盖怎么处理由显式的 <c>-o+ / -o- / -or</c> 决定，不用"全答是"这种粗开关。</description></item>
/// <item><description><b>输出目录走 <c>-op&lt;路径&gt;</c></b>：它接受不带尾随分隔符的路径，
/// 不像位置参数那样必须写成 <c>dest\</c>（RAR 6.10 起的行为，7.23 实测可用）。</description></item>
/// <item><description><b>⛔ 不加 <c>-idq</c> / <c>-idp</c></b>：<c>-idq</c>（安静模式）实测**连进度一起关掉**
/// （成功时输出 0 字节），<c>-idp</c> 关的正是百分比指示器。
/// 这两个开关一加，界面就退回"处理中/完成"两态 —— 正是用户抱怨"卡死"的成因。
/// 实测 UnRAR **默认**就带百分比进度，所以这里什么都不用加（见
/// <see cref="UnRarProgressParser"/>）。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class UnRarCommandLine
{
    /// <summary>
    /// 列表：<c>unrar lt -scf -c- -p&lt;密码&gt; &lt;包&gt;</c>。
    ///
    /// <c>lt</c> = 技术列表（稳定键值，见 <see cref="UnRarListParser"/>）；
    /// <c>-c-</c> = 不显示归档注释（注释是自由文本，混进列表只会污染解析）。
    /// </summary>
    internal static List<string> BuildListArguments(string archivePath, string? password)
    {
        return new List<string>
        {
            "lt",
            "-scf",
            "-c-",
            BuildPasswordArgument(password),
            archivePath
        };
    }

    /// <summary>测试：<c>unrar t -scf -c- -p&lt;密码&gt; &lt;包&gt;</c>。</summary>
    internal static List<string> BuildTestArguments(string archivePath, string? password)
    {
        return new List<string>
        {
            "t",
            "-scf",
            "-c-",
            BuildPasswordArgument(password),
            archivePath
        };
    }

    /// <summary>
    /// 解压：<c>unrar x -scf -c- [-kb] &lt;覆盖档&gt; -op&lt;输出目录&gt; -p&lt;密码&gt; &lt;包&gt;</c>。
    ///
    /// <c>x</c> = 保留包内路径（**永不**用 <c>e</c>：摊平必然撞名，
    /// 见 `docs/WinRAR功能参考.md` §1.13）。
    /// </summary>
    internal static List<string> BuildExtractArguments(
        string archivePath,
        string outputPath,
        string? password,
        ExtractOptions options,
        bool keepBrokenFiles)
    {
        options ??= new ExtractOptions();
        options.Normalize();

        var args = new List<string>
        {
            "x",
            "-scf",
            "-c-"
        };

        if (keepBrokenFiles)
        {
            /*
             * 保留受损文件（WinRAR 的"保留受损的文件"，默认关）。
             *
             * ⚠ 它**只**决定"校验失败的半成品留不留在磁盘上"，
             * **不改变**退出码与错误分类 —— 所以任务状态仍然是失败 / 部分完成，
             * 绝不会因为这个开关显示成功（不变量 6）。
             */
            args.Add("-kb");
        }

        args.Add(GetOverwriteArgument(options.OverwriteMode));
        args.Add("-op" + outputPath);
        args.Add(BuildPasswordArgument(password));
        args.Add(archivePath);

        /*
         * 只解指定的条目（密码预检：先解"最小的那一个"验密码，再决定要不要跑整卷 —— 用户 2026-09-25 第 37 条）。
         * UnRAR 的用法是 `unrar x 归档 条目…`，条目名要给在归档**之后**。
         */
        foreach (string entry in options.IncludeEntries ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                args.Add(entry);
            }
        }

        return args;
    }

    /// <summary>
    /// 覆盖模式 → UnRAR 开关。
    ///
    /// ⚠ <c>AutoRenameExisting</c>（"把已存在的那个改名让位"）UnRAR **没有**对应开关，
    /// 这里落到 <c>-o-</c>（不覆盖、保留已存在的文件）—— 方向与不变量 3 一致：
    /// 宁可少落一个产物，也绝不默认覆盖用户已有文件。差异在
    /// <see cref="UnRarEngine"/> 的"已知问题"里如实登记。
    /// </summary>
    internal static string GetOverwriteArgument(string? overwriteMode)
    {
        return overwriteMode switch
        {
            "SkipExisting" => "-o-",
            "OverwriteAll" => "-o+",
            "AutoRenameExtracted" => "-or",
            "AutoRenameExisting" => "-o-",
            _ => "-o-"
        };
    }

    /// <summary>
    /// 密码参数：<c>-p&lt;密码&gt;</c>；空 / 未给时 <c>-p-</c>。
    ///
    /// ⛔ 绝不返回裸的 <c>-p</c>：那会让 UnRAR 交互式问密码（见类注释的实测记录）。
    /// </summary>
    internal static string BuildPasswordArgument(string? password)
    {
        return string.IsNullOrEmpty(password) ? "-p-" : "-p" + password;
    }

    /// <summary>
    /// "这次到底拿什么参数调的 UnRAR"（**已脱敏**，只给详细日志用）。
    /// 形状与 7-Zip 侧同一个口径：命令 + 开关 + 归档**文件名** + 条目名；
    /// ⛔ 完整路径不进日志（§8），<c>-p</c> / <c>-hp</c> 由脱敏兜底（不变量 5）。
    /// </summary>
    internal static string BuildCommandSummary(IEnumerable<string>? arguments)
    {
        if (arguments == null)
        {
            return string.Empty;
        }

        var parts = new List<string> { "unrar" };
        bool isFirst = true;
        bool sawArchive = false;

        foreach (string argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (isFirst)
            {
                isFirst = false;
                parts.Add(argument.Trim());
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                parts.Add(argument.Trim());
                continue;
            }

            if (!sawArchive)
            {
                sawArchive = true;
                parts.Add(Path.GetFileName(argument));
                continue;
            }

            /*
             * 归档之后的位置通常是两个东西：**输出目录**（`unrar x <包> <目录>\`）与**条目名**。
             * 只留条目名 —— 目录进去既没有信息量、又会把用户的个人路径写进日志（§8 隐私红线）。
             */
            if (argument.Contains('\\') || argument.Contains('/'))
            {
                continue;
            }

            parts.Add(argument.Trim());
        }

        return PasswordMasker.Sanitize(string.Join(" ", parts));
    }

    /// <summary>
    /// 从参数表里认出这次跑的是哪个命令（<c>lt</c> / <c>t</c> / <c>x</c>）。
    ///
    /// 为什么要它：**同一段输出在不同命令下的含义不同** —— 加密头的包在 <c>lt</c> 上失败
    /// 说明"名字读不出来"，在 <c>x</c> 上失败只说明"这个候选密码不对"（还要接着试下一个）。
    /// 第一条非 <c>-</c> 参数就是命令（UnRAR 的命令永远在最前）。
    /// </summary>
    internal static EngineOperation? ResolveOperation(IEnumerable<string>? arguments)
    {
        if (arguments == null)
        {
            return null;
        }

        foreach (string argument in arguments)
        {
            if (argument == null)
            {
                continue;
            }

            string command = argument.Trim().ToLowerInvariant();

            if (command.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            return command switch
            {
                "l" or "lt" or "lta" or "lb" or "v" or "vt" or "vb" => EngineOperation.List,
                "t" => EngineOperation.Test,
                "x" or "e" => EngineOperation.Extract,
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// 从参数表里找出归档路径（"缺卷"的二次判定与消息要用）。
    /// 命令字（<c>lt</c>）与开关都不算路径 —— 与 7-Zip 侧同一个坑：
    /// 曾经的写法返回的是命令字本身，于是二次判定永远在判一个叫 "x" 的文件。
    /// 这里用**跳过命令 + 跳过开关 + 跳过 <c>-op</c> 的值**的写法。
    /// </summary>
    internal static string? FindArchiveArgument(IEnumerable<string>? arguments)
    {
        if (arguments == null)
        {
            return null;
        }

        bool isFirst = true;

        foreach (string argument in arguments)
        {
            if (isFirst)
            {
                isFirst = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            return argument;
        }

        return null;
    }
}
