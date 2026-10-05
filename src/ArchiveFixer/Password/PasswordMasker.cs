using ArchiveFixer.Helpers;
using System;

namespace ArchiveFixer.Password
{
    /// <summary>
    /// 密码脱敏。
    ///
    /// 为什么单独一个类：
    /// 脱敏是**跨模块的硬约束**（AGENTS.md §6 第 5 条：密码默认只存内存、日志/报告/剪贴板一律脱敏；
    /// 密码列表本身可以按本机 DPAPI 加密落盘，但**明文进日志**这条永远不放松），
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
        /// - RAR 命令行里的 <c>-hp&lt;密码&gt;</c>（打包功能要用；它**不匹配** <c>-p</c> 那条规则，
        ///   因为 <c>-</c> 后面跟的是 <c>h</c> —— 少了这一条，rar 的命令行会原样进日志）
        /// - <c>password=xxx</c> / <c>password: xxx</c>（脚本与配置文件风格）
        /// - 本项目自己写的中文日志口径"使用密码 / 尝试密码 / 密码："
        ///
        /// <para><b>⚠ 2026-10-04（真机）："尝试密码"那条规则过去把**整行**吃掉</b>
        /// （<c>尝试密码\s*[^\r\n]+</c> ⇒ <c>尝试密码 ******</c>），于是
        /// 「密码候选 3/10，尝试密码列表第 3 项：******」在日志 / 屏幕 / 导出三处都只剩
        /// 「尝试密码 ******」—— 用户看不出**试的是第几项、什么来源**，"为什么试这么多次"就没法回答。
        /// 现在只擦**密码本体**：产出方（<c>PasswordService.BuildTryPasswordLogText</c>）写的一律是
        /// <c>描述：******</c> 这个形状，**描述原样保留**、冒号后面一律擦成 <c>******</c>；
        /// 形状对不上的（真有明文夹在里面）照旧整行擦掉 —— 兜底一寸都不放松。</para>
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

            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"(?i)(^|\s)-hp(?:[^\s]*)",
                m =>
                {
                    string prefix = m.Value.StartsWith(" ", StringComparison.Ordinal) ? " " : string.Empty;
                    return prefix + "-hp******";
                });

            result = System.Text.RegularExpressions.Regex.Replace(result, @"(?i)(password\s*=\s*)([^\s;]+)", "$1******");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"(?i)(password\s*:\s*)([^\r\n]+)", "$1******");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"使用密码\s*[^\r\n]+", "使用密码 ******");

            /*
             * 尝试密码：**只保留"描述 + ：******"那个已知安全的形状**，其余照旧整行擦掉。
             *
             * 负向断言里的 {0,40} 是给描述留的上限（"列表第 3 项"这种最长也就十几个字）：
             * 超过它、或者没有冒号收尾的，一律按"里面可能有明文"处理。
             * Multiline：导出全部日志时是整份文件一起过这里，`$` 必须按行匹配。
             *
             * ⚠ 2026-10-05（真机第七批）：`$` 前面必须允许 `\r`。日志文件在 Windows 上是 **CRLF** 行尾，
             * 而 .NET 的 `$`（Multiline）只匹配"字符串末尾或 `\n` 之前"——`[ \t]*$` 在 `******\r\n`
             * 这种行尾上**永远匹配不上**，于是负向断言恒成立、这条规则把**每一行**都擦成
             * 「尝试密码 ******」：用户 555 那一批 39 条候选日志里，「尝试密码列表第 N 项」一次都没留下，
             * 只剩「尝试密码 ******」，"试的是第几项、为什么试这么多次"又变成没法回答。
             * ⛔ 单测当时用的是 LF（`"第一行\n…"`），所以这个缺陷在测试里照不出来 —— 用例已补 CRLF 那一格。
             */
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"尝试密码(?![^\r\n：:]{0,40}[:：]\*{6}[ \t]*\r?$)[^\r\n]*",
                "尝试密码 ******",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            result = System.Text.RegularExpressions.Regex.Replace(result, @"密码\s*[:：]\s*[^\r\n]+", "密码：******");

            return result;
        }
    }
}
