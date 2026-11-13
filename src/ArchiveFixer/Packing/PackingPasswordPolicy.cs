using System;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 打包的密码规则（**必填** —— 用户原话："密码必须有"）。
    ///
    /// <para>与密码本解析（<c>Password/PasswordBook</c>）刻意不同：那里**不 Trim**
    /// （密码本里的密码可能真的带首尾空格），这里判"能不能用"用的是
    /// <see cref="string.IsNullOrWhiteSpace(string)"/> —— 全是空格等于没设密码。</para>
    /// </summary>
    public static class PackingPasswordPolicy
    {
        /// <summary>没设密码时的那句话（用户原话的落点：密码必须有）。</summary>
        public const string RequiredMessage =
            "必须设置密码：两层加密（7z 分卷 + 外层容器）都要用它，空密码和只有空格的密码都不接受。";

        /// <summary>两次输入不一致。</summary>
        public const string MismatchMessage = "两次输入的密码不一致，请重新输入。";

        /// <summary>外层勾了「用另一个密码」但没填。</summary>
        public const string OuterRequiredMessage = "勾了「两层用不同的密码」，就要把「最外层」那个密码也填上。";

        /// <summary>
        /// 日志里代表"已设置密码"的**唯一一句话**。
        /// 它替代了"把密码（或掩码后的 -p 开关）写进日志"这种做法 —— 日志里连长度都不写。
        /// </summary>
        public const string SetLogLine = "已设置密码（内容不写入日志）";

        /// <summary>够不够格当密码：null / 空串 / 只有空格一律不行。</summary>
        public static bool IsUsable(string? password)
        {
            return !string.IsNullOrWhiteSpace(password);
        }

        /// <summary>
        /// 校验"密码 + 确认"这一对。返回空串 = 通过；否则是给用户看的那句话。
        /// </summary>
        public static string Validate(string? password, string? confirm)
        {
            if (!IsUsable(password))
            {
                return RequiredMessage;
            }

            // 密码本身**不 Trim**（与密码本同一口径），只做逐字符相等比较。
            return string.Equals(password, confirm, StringComparison.Ordinal)
                ? string.Empty
                : MismatchMessage;
        }

        /// <summary>
        /// 外层密码的校验：勾了"用另一个密码"就必须另填一个且能对上；没勾就跟着内层走。
        /// </summary>
        public static string ValidateOuter(bool useSeparateOuterPassword, string? outerPassword, string? outerConfirm)
        {
            if (!useSeparateOuterPassword)
            {
                return string.Empty;
            }

            if (!IsUsable(outerPassword))
            {
                return OuterRequiredMessage;
            }

            return string.Equals(outerPassword, outerConfirm, StringComparison.Ordinal)
                ? string.Empty
                : MismatchMessage;
        }
    }
}
