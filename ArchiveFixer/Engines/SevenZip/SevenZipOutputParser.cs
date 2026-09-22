using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Password;

namespace ArchiveFixer.Engines.SevenZip
{
    public static class SevenZipOutputParser
    {
        /// <summary>
        /// "文件名本身就是分卷、但首卷不在"时的错误类型。
        ///
        /// 与 <c>VolumeMissing</c> 分开的理由（不变量 7："必须报缺哪几个"）：
        /// <c>VolumeMissing</c> 表示调用方**已经确知缺了哪几卷**（同目录里数出了缺号），
        /// 而这一条是"看上去像缺卷，但目录里没有任何可见的缺号"——
        /// 只能告诉用户"这是分卷，先找齐第一卷"，不能编一个缺卷清单出来。
        /// </summary>
        public const string MissingFirstVolumeErrorType = "MissingFirstVolume";

        /// <summary>
        /// 7-Zip 退出码 1：**发生非致命错误**（部分文件解出来了、部分失败）。
        ///
        /// 单独一类而不是并进"未知错误"的理由：这一类的用户动作与"解压失败"不同 ——
        /// 产物是**部分可用**的，用户要去看清单里缺了什么，而不是重跑一遍。
        /// 状态固定映射到 <c>StatusText.PartiallyCompleted</c>，**绝不允许算成功**（不变量 6）。
        /// </summary>
        public const string NonFatalErrorType = "NonFatalError";

        /// <summary>7-Zip 退出码 2：发生致命错误（关键字分不出更具体的原因时用它兜底）。</summary>
        public const string FatalErrorType = "FatalError";

        /// <summary>7-Zip 退出码 8：内存不足。</summary>
        public const string OutOfMemoryErrorType = "OutOfMemory";

        /// <summary>
        /// 文件名（头部）被加密：RAR <c>-hp</c> / 7z <c>-mhe</c>。
        ///
        /// 这类包**连文件列表都读不出来**，所以它落到"密码错误 / 文件损坏"里给出的结论是错的。
        /// 判据见 <see cref="LooksLikeEncryptedHeaders"/>：**只有列目录失败**才算 ——
        /// 数据加密（未加密文件名）的包不给密码也能列目录（26.01 实测退出码 0），
        /// 所以"列不出来"本身就是"名字被加密了 / 密码不对，内容无法判定"的证据。
        /// </summary>
        public const string EncryptedHeadersErrorType = "EncryptedHeaders";

        /// <summary>
        /// 判定"文件名是分卷、且首卷看不到"。
        ///
        /// <paramref name="archivePath"/> 为 null 时不做这个判定（老调用方按纯文本分类，行为不变）；
        /// 分卷的头信息在**最后一卷**里，缺后续卷时连列目录都会失败 ——
        /// 宁可判成缺卷，也不要误报成"不支持该格式"（不变量 7 要求报缺哪几个）。
        ///
        /// 注意：这里只按**文件名**判定，判不出"首卷到底在不在"。真正的二次判定（数目录里的缺号）
        /// 在 <c>SevenZipProcessRunner.ResolveVolumeMissingErrorType</c>，那里才有文件系统可用。
        /// </summary>
        public static bool LooksLikeMissingVolumePart(string? archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return false;
            }

            return VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(archivePath)) != null;
        }

        public static string DetectSevenZipErrorType(
            int exitCode,
            string? output,
            string? error,
            string? archivePath = null,
            EngineOperation? operation = null)
        {
            string text = CombineOutput(output, error);

            if (exitCode == 0)
            {
                return "None";
            }

            if (exitCode == -2)
            {
                return "Cancelled";
            }

            if (exitCode == -3)
            {
                return "TimedOut";
            }

            if (ContainsAny(text, "7-Zip 执行超时", "执行超时", "timed out", "timeout"))
            {
                return "TimedOut";
            }

            if (exitCode == 7)
            {
                return "CommandLineError";
            }

            if (exitCode == 255)
            {
                if (ContainsAny(text, "Break signaled", "User break", "User stopped", "Operation canceled", "Operation cancelled"))
                {
                    return "Cancelled";
                }

                return SevenZipExitCodes.ToErrorType(exitCode) ?? "UnknownError";
            }

            if (ContainsAny(text, "Break signaled", "User break", "User stopped", "Operation canceled", "Operation cancelled"))
            {
                return "Cancelled";
            }

            /*
             * 加密文件名（-hp / -mhe）必须排在"密码错误"之前判：
             * 这两种情况的输出**字面完全一样**（"Cannot open encrypted archive. Wrong password?"），
             * 唯一能把它们分开的是**这次调用是不是列目录** —— 见 LooksLikeEncryptedHeaders 的说明。
             */
            if (LooksLikeEncryptedHeaders(exitCode, output, error, operation))
            {
                return EncryptedHeadersErrorType;
            }

            if (ContainsAny(text,
                    "Wrong password",
                    "ERROR: Wrong password",
                    "Can not open encrypted archive",
                    "Cannot open encrypted archive",
                    "Data Error in encrypted file",
                    "Encrypted file. Wrong password",
                    "Enter password",
                    "Enter password:",
                    "Can not read password",
                    "Cannot read password",
                    "Can not read from input",
                    "Cannot read from input",
                    "Can not get password",
                    "Cannot get password",
                    "Password is incorrect",
                    "E_NOTIMPL"))
            {
                return "WrongPassword";
            }

            if (ContainsAny(text, "encrypted archive", "Encrypted archive", "is encrypted", "encrypted file"))
            {
                return "NeedPassword";
            }

            if (ContainsAny(text,
                    "Missing volume",
                    "Cannot find archive part",
                    "Can not open file as archive part",
                    "Can not open the file as archive part",
                    "No more files"))
            {
                return "VolumeMissing";
            }

            if (ContainsAny(text, "Can not open file", "Cannot open file"))
            {
                if (ContainsAny(text, "archive part", "volume", ".001", ".002", ".003", "No more files"))
                {
                    return "VolumeMissing";
                }
            }

            /*
             * 只给非首卷时报的是 "Cannot open the file as archive"（26.01 实测，退出码 2），
             * 字面上与"这不是归档"完全一样。判据不能用关键字，只能用**文件名是不是分卷**：
             * 一个本身就叫 .001/.002/… 的文件打不开，最可能的原因是同组的卷不在同目录里。
             */
            if (LooksLikeMissingVolumePart(archivePath))
            {
                return MissingFirstVolumeErrorType;
            }

            if (ContainsAny(text,
                    "Access is denied",
                    "Permission denied",
                    "Cannot create",
                    "Can not create output directory",
                    "Can not create file",
                    "Cannot open output file",
                    "Can not open output file"))
            {
                return "AccessDenied";
            }

            if (ContainsAny(text,
                    "The filename or extension is too long",
                    "Path too long",
                    "Filename too long",
                    "The path is too long"))
            {
                return "PathTooLong";
            }

            if (ContainsAny(text, "already exists", "file exists", "Cannot overwrite", "Can not overwrite"))
            {
                return "OutputConflict";
            }

            if (ContainsAny(text,
                    "CRC Failed",
                    "CRC error",
                    "Unexpected end of data",
                    "Unexpected end of archive",
                    "Headers Error",
                    "Header Error",
                    "Can not open the file as archive part"))
            {
                return "CorruptedArchive";
            }

            if (ContainsAny(text, "Data Error", "Data error"))
            {
                return "CorruptedArchive";
            }

            if (ContainsAny(text,
                    "Can not open the file as archive",
                    "Cannot open the file as archive",
                    "Is not archive",
                    "is not archive",
                    "Unsupported Method",
                    "Unsupported method",
                    "Open ERROR: Can not open the file as archive"))
            {
                return "UnsupportedFormat";
            }

            if (ContainsAny(text,
                    "Command Line Error",
                    "Incorrect command line",
                    "Too short switch",
                    "Unsupported command",
                    "Unknown switch"))
            {
                return "CommandLineError";
            }

            /*
             * 关键字都分不出来时，才用退出码兜底（映射表在 SevenZipExitCodes，全仓唯一一份）。
             *
             * 顺序刻意如此：关键字能给出**更具体**的结论（密码错误 / 缺卷 / 路径过长 / 损坏），
             * 退出码只能给出"这一类错误"。反过来先按码硬映射，会把好结论盖成"未知错误"。
             * 这里唯一不能让步的是**退出码 1**：它必须落在"部分完成"上，绝不能算成功（不变量 6）。
             */
            string? byExitCode = SevenZipExitCodes.ToErrorType(exitCode);

            if (!string.IsNullOrWhiteSpace(byExitCode))
            {
                return byExitCode;
            }

            return "UnknownError";
        }

        /// <summary>
        /// 判定"文件名（头部）被加密"：RAR <c>-hp</c> / 7z <c>-mhe</c>。
        ///
        /// <b>为什么只看"列目录"</b>：加密头与"密码错误"的 7-Zip 输出字面完全一样
        /// （26.01 实测都是 <c>ERROR: x.7z : Cannot open encrypted archive. Wrong password?</c> +
        /// <c>ERRORS: Headers Error</c>，退出码 2），靠文本分不开。能分开的是**操作类型**：
        ///
        /// · 数据加密（未加密文件名）的包，**不给密码也能列出条目名**（26.01 实测 `l` 退出码 0）；
        /// · 所以"列目录失败 + 加密/头部错误字样" ⇒ 名字本身读不出来
        ///   ⇒ 当前密码候选不对，或密码根本没给 —— 两种情况的用户动作都是"补正确密码"。
        ///
        /// ⚠ <b>不许用在解压 / 测试上</b>：那两条路径靠 <c>WrongPassword</c> 驱动密码候选循环
        /// （<c>ExtractionCoordinator</c> 见到非 WrongPassword 就跳出循环）。把加密头的包在解压时
        /// 判成这一类，等于**第一个候选（常是空密码）就把循环打断**，我们自己的内层 <c>-mhe</c>
        /// 分卷会因此解不开 —— 那正是"别把正常加密包误判"要防的事。
        /// 给了正确密码时列目录会成功（26.01 实测退出码 0），所以正常可解的加密头包不会被判成这一类。
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

            if (exitCode == 0)
            {
                return false;
            }

            string text = CombineOutput(output, error);

            if (ContainsAny(
                    text,
                    "Cannot open encrypted archive",
                    "Can not open encrypted archive"))
            {
                return true;
            }

            // 头部错误 + 密码/加密字样：同一件事的另一种说法（RAR -hp 走的是这一支）。
            return ContainsAny(text, "Headers Error", "Header Error") &&
                   ContainsAny(text, "Wrong password", "encrypted");
        }

        public static string ErrorTypeToTaskStatus(string? errorType)
        {
            return errorType switch
            {
                "None" => StatusText.ExtractSuccess,
                "WrongPassword" => StatusText.WrongPassword,
                "NeedPassword" => StatusText.WrongPassword,
                "CorruptedArchive" => StatusText.Corrupted,
                "UnsupportedFormat" => StatusText.ExtractFailed,

                // 退出码 1 = 发生非致命错误：解出来一部分，但**不是成功**（不变量 6）。
                NonFatalErrorType => StatusText.PartiallyCompleted,

                // 加密文件名：内容无法判定，要用户补正确密码 —— 既不是"密码错误"，也不是"文件损坏"。
                EncryptedHeadersErrorType => StatusText.EncryptedHeaders,

                FatalErrorType => StatusText.ExtractFailed,
                OutOfMemoryErrorType => StatusText.ExtractFailed,
                "AccessDenied" => StatusText.AccessDenied,
                "OutputConflict" => StatusText.OutputConflict,
                "VolumeMissing" => StatusText.VolumeMissing,
                MissingFirstVolumeErrorType => StatusText.VolumeMissing,
                "PathTooLong" => StatusText.PathTooLong,
                "SevenZipMissing" => StatusText.SevenZipMissing,
                "CommandLineError" => StatusText.ExtractFailed,
                "Cancelled" => StatusText.Cancelled,
                "TimedOut" => StatusText.UnknownError,
                _ => StatusText.UnknownError
            };
        }

        public static string ErrorTypeToMessage(string? errorType)
        {
            return ErrorTypeToMessage(errorType, string.Empty);
        }

        public static string ErrorTypeToMessage(string? errorType, string? combinedOutput)
        {
            string detail = ExtractImportantMessage(combinedOutput);

            return errorType switch
            {
                "None" => "操作成功",
                "WrongPassword" => "密码错误或缺少正确密码",
                "NeedPassword" => "压缩包需要密码，但当前没有提供正确密码",
                "CorruptedArchive" => "压缩包可能损坏或下载不完整",
                "UnsupportedFormat" => "7-Zip 无法识别或不支持该格式",

                NonFatalErrorType => string.IsNullOrWhiteSpace(detail)
                    ? $"7-Zip 报告{SevenZipExitCodes.Describe(SevenZipExitCodes.NonFatalWarning)}（退出码 {SevenZipExitCodes.NonFatalWarning}）：解出来的是部分内容，请核对产物"
                    : $"7-Zip 报告{SevenZipExitCodes.Describe(SevenZipExitCodes.NonFatalWarning)}（退出码 {SevenZipExitCodes.NonFatalWarning}）：解出来的是部分内容，请核对产物。{detail}",

                EncryptedHeadersErrorType => string.IsNullOrWhiteSpace(detail)
                    ? "该包可能加密了文件名（RAR -hp / 7z -mhe），需要正确密码才能列出内容"
                    : $"该包可能加密了文件名（RAR -hp / 7z -mhe），需要正确密码才能列出内容。（7-Zip：{detail}）",

                FatalErrorType => string.IsNullOrWhiteSpace(detail)
                    ? $"7-Zip 报告{SevenZipExitCodes.Describe(SevenZipExitCodes.FatalError)}（退出码 {SevenZipExitCodes.FatalError}）"
                    : $"7-Zip 报告{SevenZipExitCodes.Describe(SevenZipExitCodes.FatalError)}（退出码 {SevenZipExitCodes.FatalError}）：{detail}",

                OutOfMemoryErrorType => string.IsNullOrWhiteSpace(detail)
                    ? $"7-Zip {SevenZipExitCodes.Describe(SevenZipExitCodes.OutOfMemory)}（退出码 {SevenZipExitCodes.OutOfMemory}），请关闭其它占用内存的程序后重试"
                    : $"7-Zip {SevenZipExitCodes.Describe(SevenZipExitCodes.OutOfMemory)}（退出码 {SevenZipExitCodes.OutOfMemory}），请关闭其它占用内存的程序后重试。{detail}",

                "AccessDenied" => "权限不足，无法读取文件或写入输出目录",
                "OutputConflict" => "输出路径存在冲突",
                "VolumeMissing" => "分卷压缩包缺少必要分卷",
                MissingFirstVolumeErrorType => "这是分卷压缩包的后续卷，缺少首卷（.001 / 第 1 卷）——请把同一组分卷放在同一目录后再解压",
                "PathTooLong" => "路径过长，请缩短文件名或输出目录",
                "SevenZipMissing" => "未找到 tools\\7zip\\7z.exe",
                "CommandLineError" => string.IsNullOrWhiteSpace(detail)
                    ? "7-Zip 命令行参数错误"
                    : "7-Zip 命令行参数错误：" + detail,
                "Cancelled" => "操作已取消",
                "TimedOut" => "7-Zip 执行超时，可能正在等待输入密码或文件过大/异常",
                _ => string.IsNullOrWhiteSpace(detail)
                    ? "未知错误，请查看日志"
                    : detail
            };
        }

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

        // 注意：原先这里的 SanitizePasswordText / MaskPassword 已经搬到 ArchiveFixer.Password.PasswordMasker。
        // 理由：脱敏是跨模块的硬约束，不是 7-Zip 的私事 —— 放在这里会让"日志脱敏"依赖一个归档引擎的工具类。

        public static bool LooksLikePasswordRequired(string? output, string? error)
        {
            string text = CombineOutput(output, error);

            return ContainsAny(text,
                "encrypted",
                "Enter password",
                "Enter password:",
                "Can not open encrypted archive",
                "Cannot open encrypted archive",
                "Wrong password",
                "Data Error in encrypted file",
                "Can not read password",
                "Cannot read password",
                "Can not read from input",
                "Cannot read from input",
                "Can not get password",
                "Cannot get password");
        }

        /// <summary>
        /// "这次算不算成功"。
        ///
        /// ⚠ **退出码 1 一律不算成功**（AGENTS.md §6 第 6 条：部分成功不得显示为成功）。
        /// 旧实现在这里放了一个"退出码 1 + 输出里有 Everything is Ok 就算成功"的口子 ——
        /// 那正是"部分文件解出来、部分失败"被判成成功的路径，已删除。理由：
        ///
        /// · 退出码 1 的官方含义就是"发生非致命错误"，它出现的场合本身就意味着**有东西没做成功**；
        /// · "Everything is Ok" 只描述 7-Zip 自己写完的那部分，它**不保证条目齐全**；
        /// · 本机 26.01 实测：真正的警告（如 "There are data after the end of archive"）返回的是 0，
        ///   并不需要靠 1 这个口子来兜 —— 也就是说这个口子只会在"确实出错"时生效，纯属误判来源。
        /// </summary>
        public static bool LooksLikeSuccess(int exitCode, string? output, string? error)
        {
            return exitCode == 0;
        }

        public static string ExtractImportantMessage(string? text)
        {
            text = PasswordMasker.Sanitize(text);

            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string[] lines = text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (lines.Length == 0)
            {
                return string.Empty;
            }

            string[] importantKeywords =
            {
                "ERROR",
                "Error",
                "WARNING",
                "Warning",
                "Wrong password",
                "Password is incorrect",
                "Enter password",
                "Can not open",
                "Cannot open",
                "Can not create",
                "Cannot create",
                "Data Error",
                "CRC Failed",
                "Headers Error",
                "Unexpected end",
                "Access is denied",
                "Permission denied",
                "Unsupported Method",
                "Unsupported method",
                "Missing volume",
                "Command Line Error",
                "Incorrect command line",
                "Can not read",
                "Cannot read",
                "Can not get password",
                "Cannot get password",
                "Break signaled",
                "User break",
                "User stopped",
                "Operation canceled",
                "Operation cancelled",
                "timed out",
                "timeout",
                "执行超时"
            };

            foreach (string line in lines)
            {
                if (importantKeywords.Any(k =>
                        line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return line;
                }
            }

            return lines.LastOrDefault() ?? string.Empty;
        }

        public static int? TryParseProgressPercent(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            Match match = Regex.Match(line, @"(?<!\d)(\d{1,3})%");

            if (!match.Success)
            {
                return null;
            }

            if (!int.TryParse(match.Groups[1].Value, out int value))
            {
                return null;
            }

            if (value < 0 || value > 100)
            {
                return null;
            }

            return value;
        }
    }
}
