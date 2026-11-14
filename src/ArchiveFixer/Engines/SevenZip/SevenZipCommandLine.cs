using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 7-Zip 的**命令行参数**这一块：怎么拼、参数表里哪个是命令/归档、详细日志那份脱敏摘要。
///
/// <para>边界：7z 参数只允许出现在 <c>Engines/SevenZip/</c> 内（AGENTS.md §3.1 禁止项①）。
/// 本类只碰参数，不起进程、不读输出、不判退出码 —— 那些分别归
/// <see cref="SevenZipProcessLauncher"/>、<see cref="SevenZipOutputCapture"/>、
/// <see cref="SevenZipResultClassifier"/>。</para>
/// </summary>
internal static class SevenZipCommandLine
{
    /// <summary>
    /// 解压参数。
    ///
    /// <para>
    /// <b>进度开关（2026-09-22 改）</b>：原来是 <c>-bsp0 -bd</c>，等于**主动把 7-Zip 的进度关掉** ——
    /// 界面因此只剩"处理中/完成"两态，长时间零输出看起来就是卡死（用户反复抱怨的那件事）。
    /// 现在改成 <c>-bsp1</c>（进度打到 stdout），并**必须去掉 <c>-bd</c>**：
    /// 本机 26.03 实测 <c>-bsp1 -bd</c> 一条进度都不出，<c>-bd</c> 会压掉进度指示器；
    /// 去掉之后同一命令能稳定刷出 <c> 67% 8 - payload 08 数据.bin</c> 这样的行。
    /// 解析在 <see cref="SevenZipProgressParser"/>（只允许待在本命名空间内），
    /// 进度行本身会被 <see cref="EngineOutputCollector"/> 从日志缓冲区里剔掉。
    /// </para>
    ///
    /// <para>
    /// ⚠ <b>这里刻意**不**加 <c>-kb</c></b>（"保留受损的文件"）。这一点与
    /// <c>docs/WinRAR功能参考.md</c> §2 C 组的建议**相反**，依据是本机实测：
    /// <list type="bullet">
    /// <item><description><c>7z x -kb …</c> → <c>Command Line Error: Unknown switch: -kb</c>，退出码 <b>7</b>
    /// （26.01 实测，开关放前放后都一样）—— <c>-kb</c> 是 RAR / UnRAR 的开关，7-Zip **没有**它；</description></item>
    /// <item><description>7-Zip 本来**就保留**校验失败的半成品（实测：截断包里第二个文件被截到 29888 字节，
    /// 仍然留在输出目录里），也就是说"保留受损文件"这一档在 7-Zip 上**本来就是开着的**，没有对应开关可切。</description></item>
    /// </list>
    /// 所以那一档只作用于 RAR 引擎（<c>UnRarEngine</c> 的 <c>-kb</c>）；
    /// 真按建议给 7-Zip 加上，用户一开这个设置，**所有解压都会以命令行错误失败**。
    /// </para>
    /// </summary>
    internal static List<string> BuildExtractArguments(
        string archivePath,
        string outputPath,
        string password,
        ExtractOptions options)
    {
        options ??= new ExtractOptions();
        options.Normalize();

        var args = new List<string>
        {
            "x",
            "-bsp1",
            "-sccUTF-8",
            archivePath,
            "-o" + outputPath,
            "-y"
        };

        string overwriteArg = GetOverwriteArgument(options.OverwriteMode);

        if (!string.IsNullOrWhiteSpace(overwriteArg))
        {
            args.Add(overwriteArg);
        }

        args.Add("-p" + (password ?? string.Empty));

        /*
         * 只解指定的条目（密码预检：先解"最小的那一个"验密码，再决定要不要跑整包 —— 用户 2026-09-25 第 37 条）。
         * 7-Zip 把归档之后的位置当"要处理的条目名"，名字要原样给（`-o`/`-p` 这些开关都已经加完）。
         * ⚠ 调用方必须先把带通配符（* ? [）的名字挑掉：7-Zip 会把它们当模式匹配（见 Extraction/PasswordProbe）。
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
    /// 测试参数。进度开关与解压同一口径（见 <see cref="BuildExtractArguments"/>）：
    /// <c>t</c> 在大包上同样会跑几十秒，没有进度就还是"看起来死了"。
    /// </summary>
    internal static List<string> BuildTestArguments(string archivePath, string password)
    {
        var args = new List<string>
        {
            "t",
            "-bsp1",
            "-sccUTF-8",
            archivePath,
            "-y",
            "-p" + (password ?? string.Empty)
        };

        return args;
    }

    /// <summary>
    /// **只读某条目解密后的开头 N 字节**时用的参数（顺序与解压同一口径：
    /// <c>x -so 归档 -p密码 条目</c>）。
    ///
    /// <para>⚠ 它刻意**没有** <c>-y</c> / <c>-o</c>：<c>-so</c> 已经把内容流到标准输出，
    /// 不落盘、不需要覆盖确认。</para>
    /// </summary>
    internal static List<string> BuildDecryptedPrefixArguments(
        string archivePath,
        string entryPath,
        string password)
    {
        return new List<string>
        {
            "x",
            "-so",
            "-sccUTF-8",
            archivePath,
            "-p" + (password ?? string.Empty),
            entryPath
        };
    }

    internal static string GetOverwriteArgument(string overwriteMode)
    {
        return overwriteMode switch
        {
            "SkipExisting" => "-aos",
            "OverwriteAll" => "-aoa",
            "AutoRenameExtracted" => "-aou",
            "AutoRenameExisting" => "-aot",
            _ => "-aos"
        };
    }

    /// <summary>这次调用是不是打开了进度开关（<c>-bsp1</c> / <c>-bsp2</c>）。</summary>
    internal static bool HasProgressSwitch(IEnumerable<string>? arguments)
    {
        if (arguments == null)
        {
            return false;
        }

        foreach (string argument in arguments)
        {
            if (string.Equals(argument, "-bsp1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "-bsp2", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从参数表里认出这次跑的是哪个命令（<c>l</c> / <c>t</c> / <c>x</c> / <c>e</c>）。
    ///
    /// 为什么要它：**同一段输出在不同命令下的含义不同**。加密头（-mhe）的包用 `l` 列目录失败
    /// 说明"名字读不出来"，而在 `x` 上失败只说明"这个候选密码不对"（还要接着试下一个）。
    /// 认不出来时返回 null = "这层不下结论"，与旧行为一致。
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

            return command switch
            {
                "l" => EngineOperation.List,
                "t" => EngineOperation.Test,
                "x" => EngineOperation.Extract,
                "e" => EngineOperation.Extract,
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// 从参数表里找出归档路径（用于"缺卷 / 缺首卷"的二次判定）。
    ///
    /// ⚠ 第一个参数是**命令**（<c>x</c> / <c>t</c> / <c>l</c>），它不以 <c>-</c> 开头、
    /// 也不是路径 —— 必须显式跳过。曾经的写法是"跳过以 - 开头的，返回第一个别的"，
    /// 结果返回的是命令字 <c>x</c>，于是二次判定永远在判一个叫 "x" 的文件，
    /// 缺卷被静默判成"不支持该格式"（实测：诊断里 path=x）。
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
                // 命令本身不是路径。
                isFirst = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(argument) ||
                argument.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            return argument;
        }

        return null;
    }

    /// <summary>
    /// "这次到底拿什么参数调的 7-Zip"（**已脱敏**，只给详细日志用）。
    ///
    /// <para>形状：命令 + 开关 + 归档**文件名**。刻意不带完整路径 ——
    /// 详细日志也不该把用户的个人目录写进去（§8 隐私红线），而定位问题用文件名就够。</para>
    ///
    /// <para>⛔ 参数里绝不允许出现明文密码：这里再过一道 <see cref="PasswordMasker.Sanitize"/>，
    /// 由它把 <c>-p&lt;明文&gt;</c> 改成 <c>-p******</c>（不变量 5）。</para>
    /// </summary>
    internal static string BuildCommandSummary(IEnumerable<string>? arguments)
    {
        if (arguments == null)
        {
            return string.Empty;
        }

        var parts = new List<string> { "7z" };
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
                // 命令字（l / t / x / e）直接跟在 7z 后面。
                isFirst = false;
                parts.Add(argument.Trim());
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                // 输出目录不写进摘要（它可能很长，而且对排查"调了什么"没有信息量）。
                if (argument.StartsWith("-o", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add(argument.Trim());
                continue;
            }

            if (!sawArchive)
            {
                // 第一个非开关参数就是归档（参数拼装顺序固定，见 BuildExtractArguments）：
                // 只留**文件名**，完整路径不进日志（§8 隐私红线）。
                sawArchive = true;
                parts.Add(Path.GetFileName(argument));
                continue;
            }

            // 归档之后的位置是"要处理的条目名"（密码预检只解那一条）：保留，它能说明这次解的是什么。
            parts.Add(argument.Trim());
        }

        return PasswordMasker.Sanitize(string.Join(" ", parts));
    }
}
