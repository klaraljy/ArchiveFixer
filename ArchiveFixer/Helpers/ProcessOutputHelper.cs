using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ArchiveFixer.Helpers
{
    public static class ProcessOutputHelper
    {
        public static string DetectSevenZipErrorType(int exitCode, string? output, string? error)
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
                "None" => "解压成功",
                "WrongPassword" => "密码错误",
                "NeedPassword" => "密码错误",
                "CorruptedArchive" => "文件损坏",
                "UnsupportedFormat" => "解压失败",
                "AccessDenied" => "权限不足",
                "OutputConflict" => "输出路径冲突",
                "VolumeMissing" => "分卷缺失",
                "PathTooLong" => "路径过长",
                "SevenZipMissing" => "7z不存在",
                "CommandLineError" => "解压失败",
                "Cancelled" => "已取消",
                "TimedOut" => "未知错误",
                _ => "未知错误"
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

        public static string SanitizePasswordText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string result = text;

            result = Regex.Replace(
                result,
                @"(?i)(^|\s)-p(?:[^\s]*)",
                m =>
                {
                    string prefix = m.Value.StartsWith(" ") ? " " : string.Empty;
                    return prefix + "-p******";
                });

            result = Regex.Replace(result, @"(?i)(password\s*=\s*)([^\s;]+)", "$1******");
            result = Regex.Replace(result, @"(?i)(password\s*:\s*)([^\r\n]+)", "$1******");
            result = Regex.Replace(result, @"使用密码\s*[^\r\n]+", "使用密码 ******");
            result = Regex.Replace(result, @"尝试密码\s*[^\r\n]+", "尝试密码 ******");
            result = Regex.Replace(result, @"密码\s*[:：]\s*[^\r\n]+", "密码：******");

            return result;
        }

        public static string MaskPassword(string? password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "空密码";
            }

            return "******";
        }

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
            text = SanitizePasswordText(text);

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
