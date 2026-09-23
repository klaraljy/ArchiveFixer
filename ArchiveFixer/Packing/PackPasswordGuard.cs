using ArchiveFixer.Password;
using System;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 打包这条路上的密码脱敏（**兜底**，不是许可）。
    ///
    /// <para>三层，从外到内：</para>
    /// <list type="number">
    /// <item><description>打包的日志行**根本不拼密码**（只写"已设置密码"，
    /// 见 <see cref="PackingPasswordPolicy.SetLogLine"/>）—— 这是真正的防线；</description></item>
    /// <item><description><see cref="PasswordMasker.Sanitize"/>：擦掉 <c>-p&lt;明文&gt;</c> /
    /// <c>-hp&lt;明文&gt;</c> / <c>password=xxx</c> 这类形态（外部工具的异常文本里可能夹带命令行）；</description></item>
    /// <item><description>本类再加一道：**按字面**把本次真正用过的那个密码串换掉 ——
    /// 前两层都靠"形态"，万一密码以别的形态漏出来（工具自己打印的一句话、路径里带的），
    /// 这一层还能挡住。</description></item>
    /// </list>
    ///
    /// <para>为什么必须做第三层：外部进程的输出与异常消息是**别人写的**，我们控制不了它打印什么；
    /// 而密码一旦进了日志文件就是不可逆的（AGENTS.md §6 不变量 5、§8 隐私红线）。</para>
    /// </summary>
    public static class PackPasswordGuard
    {
        /// <summary>被替换成的东西（与 <c>PasswordMasker</c> 用同一个占位符）。</summary>
        public const string Placeholder = "******";

        /// <summary>脱敏一段文本；<paramref name="password"/> 为空时只做形态擦除。</summary>
        public static string Sanitize(string? text, string? password)
        {
            string result = PasswordMasker.Sanitize(text);

            if (string.IsNullOrEmpty(password) || result.Length == 0)
            {
                return result;
            }

            if (result.Contains(password, StringComparison.Ordinal))
            {
                result = result.Replace(password, Placeholder, StringComparison.Ordinal);
            }

            return result;
        }
    }
}
