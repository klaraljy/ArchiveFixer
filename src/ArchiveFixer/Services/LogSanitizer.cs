using System.Text.RegularExpressions;
using ArchiveFixer.Password;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 日志脱敏的唯一入口：人读 `.log`、机器 `.jsonl`、诊断 `.trace.jsonl`、导出日志
    /// 以及导出失败清单都必须走这里，不允许各自再抄一套规则。
    /// </summary>
    public static class LogSanitizer
    {
        public static string Sanitize(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            string result = PasswordMasker.Sanitize(message);

            // -p明文密码
            result = Regex.Replace(
                result,
                @"(?i)(^|\s)-p[^\s]+",
                m =>
                {
                    string prefix = m.Value.StartsWith(" ") ? " " : string.Empty;
                    return prefix + "-p******";
                });

            // --password=明文
            result = Regex.Replace(
                result,
                @"(?i)(--password\s*=\s*)([^\s;]+)",
                "$1******");

            // password=明文
            result = Regex.Replace(
                result,
                @"(?i)(password\s*=\s*)([^\s;]+)",
                "$1******");

            // Password: 明文
            result = Regex.Replace(
                result,
                @"(?i)(password\s*:\s*)([^\r\n]+)",
                "$1******");

            // 使用密码 明文
            result = Regex.Replace(
                result,
                @"使用密码\s*[^\r\n]+",
                "使用密码 ******");

            // 统一密码：明文
            result = Regex.Replace(
                result,
                @"统一密码\s*[:：]\s*[^\r\n]+",
                "统一密码：******");

            // 单任务密码：明文
            result = Regex.Replace(
                result,
                @"单任务密码\s*[:：]\s*[^\r\n]+",
                "单任务密码：******");

            // 密码：明文
            result = Regex.Replace(
                result,
                @"密码\s*[:：]\s*[^\r\n]+",
                "密码：******");

            return result;
        }
    }
}
