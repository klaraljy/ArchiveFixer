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
            string? archivePath = null)
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

                return "UnknownError";
            }

            if (ContainsAny(text, "Break signaled", "User break", "User stopped", "Operation canceled", "Operation cancelled"))
            {
                return "Cancelled";
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

            return "UnknownError";
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

        public static bool LooksLikeSuccess(int exitCode, string? output, string? error)
        {
            if (exitCode == 0)
            {
                return true;
            }

            string text = CombineOutput(output, error);

            if (exitCode == 1 && ContainsAny(text, "Everything is Ok", "Everything is OK"))
            {
                return true;
            }

            return false;
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
