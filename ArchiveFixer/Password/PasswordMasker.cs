using ArchiveFixer.Helpers;
using System;

namespace ArchiveFixer.Password
{
    /// <summary>
    /// 密码脱敏。
    ///
    /// 为什么单独一个类：
    /// 脱敏是**跨模块的硬约束**（AGENTS.md §6 第 5 条：密码只存内存，日志/报告/剪贴板一律脱敏），
    /// 不是 7-Zip 的私事 —— 以前它挂在 ProcessOutputHelper 里，导致"日志脱敏"这种通用需求
    /// 要依赖一个归档引擎的工具类。现在谁都能用，且与具体引擎无关。
    ///
    /// 注意：这里只做**文本层面**的脱敏。真正的防线是"不把密码传进日志"，
    /// 脱敏只是兜底 —— 不要把兜底当成许可。
    /// </summary>
    public static class PasswordMasker
    {
        /// <summary>把密码变成给人看的占位符（绝不返回原文）。</summary>
        public static string Mask(string? password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "空密码";
            }

            return "******";
        }

        /// <summary>
        /// 擦掉文本里可能夹带的密码。
        ///
        /// 覆盖的形态都是实际踩过的：
        /// - 7-Zip 命令行里的 <c>-p&lt;密码&gt;</c>
        /// - <c>password=xxx</c> / <c>password: xxx</c>（脚本与配置文件风格）
        /// - 本项目自己写的中文日志口径"使用密码 / 尝试密码 / 密码："
        /// </summary>
        public static string Sanitize(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string result = text;

            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"(?i)(^|\s)-p(?:[^\s]*)",
                m =>
                {
                    string prefix = m.Value.StartsWith(" ", StringComparison.Ordinal) ? " " : string.Empty;
                    return prefix + "-p******";
                });

            result = System.Text.RegularExpressions.Regex.Replace(result, @"(?i)(password\s*=\s*)([^\s;]+)", "$1******");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"(?i)(password\s*:\s*)([^\r\n]+)", "$1******");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"使用密码\s*[^\r\n]+", "使用密码 ******");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"尝试密码\s*[^\r\n]+", "尝试密码 ******");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"密码\s*[:：]\s*[^\r\n]+", "密码：******");

            return result;
        }
    }
}
