using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines.WinRar
{
    /// <summary>
    /// UnRAR 输出的解释器：**"这次到底成没成、为什么没成"**。
    ///
    /// 只允许待在 <c>Engines/WinRar/</c> 里（AGENTS.md §3.1 禁止项②）：
    /// 出了这个目录，谁都不许再判断 UnRAR 的错误字符串。
    ///
    /// <para>
    /// 本文件的判据全部来自本机 <c>UNRAR 7.23 x64 freeware</c> 的**真样本实测**
    /// （样本见 `_tmp\ArchiveFixer\unrar-samples\`，逐条结论写在对应分支的注释里）：
    /// <list type="bullet">
    /// <item><description>加密文件名（<c>-hp</c>）+ 无密码 / 错密码，<c>l</c>：退出码 <b>11</b>，
    /// 输出含 <c>Details: RAR 5, encrypted headers</c> + <c>Incorrect password for &lt;包&gt;</c>；</description></item>
    /// <item><description>同一个包在 <c>t</c> / <c>x</c> 上：退出码 <b>11</b>，但输出**没有** "encrypted headers"
    /// 那行，只有 <c>Incorrect password for …</c> —— 所以"加密文件名"的判定**必须**限定在列目录上；</description></item>
    /// <item><description>缺分卷：<c>Cannot find volume &lt;路径&gt;</c> + <c>Total errors: N</c> + <c>&lt;名&gt; - checksum error</c>，
    /// 退出码 <b>6</b>（不是 3！），且**明确点名缺的是哪一个卷**；</description></item>
    /// <item><description>归档截断：<c>&lt;名&gt; - checksum error</c> + <c>Unexpected end of archive</c>，退出码 <b>3</b>；</description></item>
    /// <item><description>需要密码但没给（标准输入被关）：<c>Enter password (will not be echoed) for …</c> +
    /// <c>Read error in the file stdin</c>，退出码 <b>12</b> —— 我们一律传 <c>-p&lt;密码&gt;</c> 或 <c>-p-</c>，
    /// 所以这条路径正常情况下不该出现，但**必须**认得它，否则"问了密码"会被报成"读取错误"；
    /// </description></item>
    /// <item><description>归档不存在：<c>Cannot open &lt;路径&gt;</c>，退出码 <b>10</b>。</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static class UnRarOutputParser
    {
        /// <summary>加密文件名（RAR <c>-hp</c>）。值必须与 <see cref="EngineErrorTypes.EncryptedHeaders"/> 一致。</summary>
        public const string EncryptedHeadersErrorType = EngineErrorTypes.EncryptedHeaders;

        /// <summary>退出码 1：发生非致命错误（部分完成，绝不算成功）。</summary>
        public const string NonFatalErrorType = EngineErrorTypes.NonFatalError;

        /// <summary>退出码 2：致命错误（关键字分不出更具体原因时的兜底）。</summary>
        public const string FatalErrorType = EngineErrorTypes.FatalError;

        /// <summary>退出码 8：内存不足。</summary>
        public const string OutOfMemoryErrorType = EngineErrorTypes.OutOfMemory;

        /// <summary>"文件名像分卷、但首卷不在"。</summary>
        public const string MissingFirstVolumeErrorType = EngineErrorTypes.MissingFirstVolume;

        /// <summary>结论里那几行原话之间用什么连（与 7-Zip 侧同一个形状）。</summary>
        public const string ImportantMessageSeparator = " ｜ ";

        /// <summary>结论里最多带几行原话（理由见 7-Zip 侧同名常量）。</summary>
        public const int ImportantMessageMaxLines = 3;

        /// <summary>
        /// 输出里那句"这个包加密了文件名"的显式证据（UnRAR 会直接写出来，比 7-Zip 的纯文本猜测稳）。
        /// </summary>
        public const string EncryptedHeadersMarker = "encrypted headers";

        /// <summary>
        /// 这次算不算成功。
        ///
        /// ⚠ **只有退出码 0**：UnRAR 的 1 是"发生非致命错误"（部分文件没解出来），
        /// 那是"部分成功"，按不变量 6 绝不允许显示成成功。
        /// </summary>
        public static bool LooksLikeSuccess(int exitCode) => exitCode == UnRarExitCodes.Success;

        /// <summary>
        /// 判定错误类型。
        ///
        /// <paramref name="operation"/> 说明这次跑的是哪个命令（<c>l</c> / <c>t</c> / <c>x</c>）：
        /// **只有列目录**才允许下"加密了文件名"的结论 —— 解压 / 测试路径上的 11 必须老老实实报
        /// <c>WrongPassword</c>，因为它是**密码候选循环的驱动信号**（循环见到非 WrongPassword 就 break）。
        /// 把加密头的包在解压时判成 EncryptedHeaders，等于第一个候选（常是空密码）就把循环打断，
        /// 我们自己产出的加密包会因此解不开。
        /// </summary>
        public static string DetectErrorType(
            int exitCode,
            string? output,
            string? error,
            EngineOperation? operation)
        {
            if (exitCode == UnRarExitCodes.Success)
            {
                return "None";
            }

            if (exitCode == -2)
            {
                return EngineErrorTypes.Cancelled;
            }

            if (exitCode == -3)
            {
                return EngineErrorTypes.TimedOut;
            }

            string text = CombineOutput(output, error);

            /*
             * 超时**只按退出码认**（上面那条 -3）：-3 是我们自己的运行器在超时强杀后写的
             * （见 UnRarProcessRunner 里 isTimeout 那一段）。
             *
             * ⚠ 这里曾经还匹配过一个中文字面量"执行超时" —— 那是**我们自己**的消息文案，
             * 永远不可能出现在 UnRAR 的 stdout/stderr 里（它不会说中文）。留着它有两个害处：
             * ① 读代码的人以为"超时是靠文本认的"，于是去改文案时不敢动；
             * ② 文案一改（比如加个空格）判定就静默失效，而测试还是绿的。
             * 引擎自己的英文措辞仍然认（不同版本可能换词），但不再认我们自己的中文。
             */
            if (ContainsAny(text, "timed out", "timeout"))
            {
                return EngineErrorTypes.TimedOut;
            }

            if (ContainsAny(text, "User break", "Break signaled", "Operation canceled", "Operation cancelled"))
            {
                return EngineErrorTypes.Cancelled;
            }

            /*
             * 加密文件名必须排在"密码错误"之前判，而且**只在列目录时**成立。
             * 实测：UnRAR 在 l 上会写出 "Details: RAR 5, encrypted headers"，
             * 而 t / x 上同样缺密码却只写 "Incorrect password for …" —— 两者字面不同，
             * 但为了不依赖这个细节，判定仍然按"操作类型 + 加密头字样"两道一起来。
             */
            if (LooksLikeEncryptedHeaders(exitCode, output, error, operation))
            {
                return EncryptedHeadersErrorType;
            }

            // 密码：实测 "Incorrect password for <路径或条目名>"。
            if (ContainsAny(text, "Incorrect password", "Wrong password", "Enter password"))
            {
                return EngineErrorTypes.WrongPassword;
            }

            /*
             * 缺分卷：实测 UnRAR 会**点名**缺的是哪一个卷（比 7-Zip 的
             * "Cannot open the file as archive" 强得多，正好补上不变量 7 要的"缺哪几个"）。
             * 退出码是 6（文件打开错误），因此关键字必须排在退出码兜底之前。
             */
            if (ContainsAny(text, "Cannot find volume", "Can not find volume", "Cannot open volume",
                    "Insert disk", "Please insert"))
            {
                return EngineErrorTypes.VolumeMissing;
            }

            if (ContainsAny(text,
                    "checksum error",
                    "Checksum error",
                    "CRC error",
                    "CRC failed",
                    "Unexpected end of archive",
                    "Unexpected end of file",
                    "is corrupted",
                    "Corrupt"))
            {
                return EngineErrorTypes.CorruptedArchive;
            }

            if (ContainsAny(text,
                    "Access is denied",
                    "Permission denied",
                    "Cannot create",
                    "Can not create",
                    "Cannot open output file"))
            {
                return EngineErrorTypes.AccessDenied;
            }

            if (ContainsAny(text,
                    "The filename or extension is too long",
                    "Path too long",
                    "Filename too long",
                    "The path is too long"))
            {
                return EngineErrorTypes.PathTooLong;
            }

            /*
             * "打不开这个归档"这一类。
             *
             * ⚠ 实测的一个坑：归档**根本不存在**时 UnRAR 也报 "Cannot open <路径>" 配退出码 10。
             * 正常运行器会先 File.Exists 拦一道，所以这里只在没有更具体关键字时兜底成
             * UnsupportedFormat（"这不是一个能读的 RAR"）——**不**编造"文件不存在"这种结论。
             */
            if (ContainsAny(text,
                    "is not RAR archive",
                    "Is not RAR archive",
                    "not a RAR archive",
                    "Cannot open",
                    "Can not open"))
            {
                return EngineErrorTypes.UnsupportedFormat;
            }

            if (ContainsAny(text, "Unknown option", "Command line error", "Incorrect command line"))
            {
                return EngineErrorTypes.CommandLineError;
            }

            // 关键字都分不出来时才用退出码兜底（映射表在 UnRarExitCodes，本目录内唯一一份）。
            string? byExitCode = UnRarExitCodes.ToErrorType(exitCode);

            if (!string.IsNullOrWhiteSpace(byExitCode))
            {
                return byExitCode;
            }

            // 11 与 3 这两条即使关键字没命中也要给出结论：它们各自只有一个合理解释。
            if (exitCode == UnRarExitCodes.WrongPassword)
            {
                return EngineErrorTypes.WrongPassword;
            }

            if (exitCode == UnRarExitCodes.DataError)
            {
                return EngineErrorTypes.CorruptedArchive;
            }

            if (exitCode == UnRarExitCodes.OpenError)
            {
                // 只有"打开"失败、又没有上面任何关键字：最可能是分卷整组不可读，
                // 但不编造"缺哪一卷"（消息里给原始输出，让用户自己看）。
                return EngineErrorTypes.CorruptedArchive;
            }

            if (exitCode == UnRarExitCodes.CreateError || exitCode == UnRarExitCodes.WriteError)
            {
                return EngineErrorTypes.AccessDenied;
            }

            if (exitCode == UnRarExitCodes.ReadError)
            {
                // 实测：需要密码却问不到（stdin 被关）走 12。正常路径不该出现，
                // 但真出现了就是"要密码"，不是"读坏了" —— 报密码问题，别把用户引到"文件损坏"上。
                return EngineErrorTypes.WrongPassword;
            }

            return EngineErrorTypes.UnknownError;
        }

        /// <summary>
        /// 判定"文件名（头部）被加密"：RAR <c>-hp</c>。
        ///
        /// <b>为什么只看列目录</b>：解压 / 测试路径靠 <c>WrongPassword</c> 驱动密码候选循环
        /// （见 <c>ExtractionCoordinator</c>），在那里改判会打断循环。
        /// 判据（实测）：<c>l</c> 失败 + 输出里有 <c>encrypted headers</c> 且带密码字样
        /// （"Incorrect password for …"）。
        /// </summary>
        public static bool LooksLikeEncryptedHeaders(
            int exitCode,
            string? output,
            string? error,
            EngineOperation? operation)
        {
            if (operation != EngineOperation.List)
            {
                return false;
            }

            if (exitCode == UnRarExitCodes.Success)
            {
                return false;
            }

            string text = CombineOutput(output, error);

            return ContainsAny(text, EncryptedHeadersMarker)
                && ContainsAny(text, "password", "Password");
        }

        /// <summary>错误类型 → 任务状态（文案一律取 <see cref="StatusText"/> 常量，不手写中文）。</summary>
        public static string ErrorTypeToTaskStatus(string? errorType)
        {
            return errorType switch
            {
                "None" => StatusText.ExtractSuccess,
                EngineErrorTypes.WrongPassword => StatusText.WrongPassword,
                EngineErrorTypes.NeedPassword => StatusText.WrongPassword,
                EngineErrorTypes.CorruptedArchive => StatusText.Corrupted,
                EngineErrorTypes.UnsupportedFormat => StatusText.ExtractFailed,

                // 退出码 1 = 发生非致命错误：解出来一部分，但**不是成功**（不变量 6）。
                NonFatalErrorType => StatusText.PartiallyCompleted,

                // 加密文件名：内容无法判定，要用户补正确密码。
                EncryptedHeadersErrorType => StatusText.EncryptedHeaders,

                FatalErrorType => StatusText.ExtractFailed,
                OutOfMemoryErrorType => StatusText.ExtractFailed,
                EngineErrorTypes.AccessDenied => StatusText.AccessDenied,
                EngineErrorTypes.OutputConflict => StatusText.OutputConflict,
                EngineErrorTypes.VolumeMissing => StatusText.VolumeMissing,
                MissingFirstVolumeErrorType => StatusText.VolumeMissing,
                EngineErrorTypes.PathTooLong => StatusText.PathTooLong,
                EngineErrorTypes.CommandLineError => StatusText.ExtractFailed,
                EngineErrorTypes.Cancelled => StatusText.Cancelled,
                EngineErrorTypes.TimedOut => StatusText.UnknownError,
                _ => StatusText.UnknownError
            };
        }

        /// <summary>错误类型 → 给用户看的一句话（带原始输出的关键行，便于对照）。</summary>
        public static string ErrorTypeToMessage(string? errorType, string? combinedOutput)
        {
            string detail = ExtractImportantMessage(combinedOutput);

            return errorType switch
            {
                "None" => "操作成功",
                EngineErrorTypes.WrongPassword => string.IsNullOrWhiteSpace(detail)
                    ? "密码错误或缺少正确密码"
                    : "密码错误或缺少正确密码。" + detail,
                EngineErrorTypes.NeedPassword => "压缩包需要密码，但当前没有提供正确密码",
                EngineErrorTypes.CorruptedArchive => string.IsNullOrWhiteSpace(detail)
                    ? "压缩包可能损坏或下载不完整"
                    : "压缩包可能损坏或下载不完整。" + detail,

                NonFatalErrorType => string.IsNullOrWhiteSpace(detail)
                    ? "UnRAR 报告发生非致命错误：解出来的是部分内容，请核对产物"
                    : "UnRAR 报告发生非致命错误：解出来的是部分内容，请核对产物。" + detail,

                EncryptedHeadersErrorType => "该包加密了文件名（RAR -hp），需要正确密码才能列出内容",

                FatalErrorType => string.IsNullOrWhiteSpace(detail)
                    ? "UnRAR 报告发生致命错误"
                    : "UnRAR 报告发生致命错误：" + detail,

                OutOfMemoryErrorType => string.IsNullOrWhiteSpace(detail)
                    ? "UnRAR 内存不足，请关闭其它占用内存的程序后重试"
                    : "UnRAR 内存不足，请关闭其它占用内存的程序后重试。" + detail,

                EngineErrorTypes.AccessDenied => string.IsNullOrWhiteSpace(detail)
                    ? "权限不足，无法读取文件或写入输出目录"
                    : "权限不足，无法读取文件或写入输出目录。" + detail,
                EngineErrorTypes.OutputConflict => "输出路径存在冲突",
                EngineErrorTypes.VolumeMissing => string.IsNullOrWhiteSpace(detail)
                    ? "分卷压缩包缺少必要分卷"
                    : "分卷压缩包缺少必要分卷。" + detail + "请把同一组分卷放在同一目录后重试。",
                MissingFirstVolumeErrorType => "这是分卷压缩包的后续卷，缺少首卷（.rar / 第 1 卷）——请把同一组分卷放在同一目录后再解压",
                EngineErrorTypes.PathTooLong => "路径过长，请缩短文件名或输出目录",
                EngineErrorTypes.CommandLineError => string.IsNullOrWhiteSpace(detail)
                    ? "UnRAR 命令行参数错误"
                    : "UnRAR 命令行参数错误：" + detail,
                EngineErrorTypes.Cancelled => "操作已取消",
                EngineErrorTypes.TimedOut => "UnRAR 执行超时，可能正在等待输入密码或文件过大/异常",
                _ => string.IsNullOrWhiteSpace(detail)
                    ? "未知错误，请查看日志"
                    : detail
            };
        }

        /// <summary>
        /// 从 UnRAR 输出里挑出**最能说明问题的几行**（给错误消息用），最多三行、按重要度排序。
        ///
        /// <para>实测要认的行：<c>Incorrect password for …</c>、<c>Cannot find volume …</c>、
        /// <c>&lt;名&gt; - checksum error</c>、<c>Unexpected end of archive</c>、
        /// <c>Total errors: N</c>、<c>Cannot open …</c>。
        /// 与 7-Zip 侧同一份排序规则（<see cref="EngineOutputKeywords"/>）：
        /// <c>ERROR</c> 行 &gt; 原因行 &gt; 其它；一行都挑不出来时退回**最后一行**
        /// （UnRAR 的结论常在最末尾 —— 老口径，别动）。</para>
        /// </summary>
        public static string ExtractImportantMessage(string? text)
        {
            text = PasswordMasker.Sanitize(text);

            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string ranked = EngineOutputKeywords.RankedMessage(
                text,
                ImportantMessageSeparator,
                ImportantMessageMaxLines,
                UnRarKeywords.Buckets);

            return ranked.Length > 0
                ? ranked
                : EngineOutputKeywords.FallbackLine(text, preferFirst: false);
        }

        /// <summary>
        /// 从输出里抽出"缺的是哪个卷"（<c>Cannot find volume &lt;路径&gt;</c>）。
        /// 数不出来就返回空列表 —— 不编造名字（不变量 7 的口径）。
        /// </summary>
        public static IReadOnlyList<string> ExtractMissingVolumeNames(string? text)
        {
            var names = new List<string>();

            if (string.IsNullOrWhiteSpace(text))
            {
                return names;
            }

            foreach (string rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int index = line.IndexOf("Cannot find volume", StringComparison.OrdinalIgnoreCase);

                if (index < 0)
                {
                    index = line.IndexOf("Can not find volume", StringComparison.OrdinalIgnoreCase);
                }

                if (index < 0)
                {
                    continue;
                }

                int colon = line.IndexOf(':', index);

                string value = colon >= 0 ? line[(colon + 1)..].Trim() : string.Empty;

                if (value.Length == 0)
                {
                    continue;
                }

                string name;

                try
                {
                    name = System.IO.Path.GetFileName(value);
                }
                catch
                {
                    name = value;
                }

                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        /// <summary>把 stdout / stderr 合成一段（分类只看这一段）。</summary>
        public static string CombineOutput(string? output, string? error)
        {
            output ??= string.Empty;
            error ??= string.Empty;

            if (string.IsNullOrWhiteSpace(output))
            {
                return error;
            }

            if (string.IsNullOrWhiteSpace(error))
            {
                return output;
            }

            return output + Environment.NewLine + error;
        }

        /// <summary>不区分大小写的关键字命中（null / 空白安全）。</summary>
        public static bool ContainsAny(string? text, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (string keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    continue;
                }

                if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
