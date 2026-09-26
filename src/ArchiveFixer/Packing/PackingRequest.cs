using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 外层容器三选一（用户 2026-09-23 决定）。
    ///
    /// <para>它取代了早先的 <c>bool SkipOuterRar</c>：那句布尔只有两种状态，而用户要的是三选一，
    /// 于是"外层用 7z 容器"这条路根本没有位置（勾了 = 不做外层，不勾 = 必须 rar）。
    /// <b>默认仍是 <see cref="Rar"/></b>（用户 2026-09-22 的原始要求：结果是一个带密码的 rar）。</para>
    ///
    /// <para>为什么"外层 7z"必须存在：<c>Rar.exe</c> / <c>WinRAR.exe</c> 是**共享软件**，
    /// RARLAB 的 EULA 明确禁止随其它软件包分发（AGENTS.md §3.1），所以"本机没装 WinRAR"是一种
    /// 常态而不是异常。此时除了"装 WinRAR"与"什么都不做"，还必须有第三条**不需要额外安装**的路
    /// —— 7-Zip 是 LGPL、本来就随程序分发。</para>
    /// </summary>
    public enum PackOuterContainer
    {
        /// <summary>外层做 <c>B.rar</c>（<b>默认</b>）—— 需要本机**已装**的 <c>Rar.exe</c>，程序绝不分发它。</summary>
        Rar = 0,

        /// <summary>外层做 <c>B.7z</c>（<c>-mhe=on</c>：连文件名一起加密）—— 用内置 7-Zip，**无需额外安装**。</summary>
        SevenZip = 1,

        /// <summary>不做外层：结果就是 B 里的那组 7z 加密分卷（本机没装 WinRAR 时最省的出路）。</summary>
        None = 2
    }

    /// <summary>外层容器的文案与派生规则（**唯一来源**；界面、日志、失败原因都引这里）。</summary>
    public static class PackOuterContainers
    {
        /// <summary>会不会产出一个外层容器文件（<see cref="PackOuterContainer.None"/> 不会）。</summary>
        public static bool HasOuterArtifact(this PackOuterContainer container)
        {
            return container != PackOuterContainer.None;
        }

        /// <summary>界面选项上的完整说法（含"要不要额外安装"这句关键信息）。</summary>
        public static string Describe(this PackOuterContainer container)
        {
            return container switch
            {
                PackOuterContainer.SevenZip => StatusText.PackOuterSevenZipText,
                PackOuterContainer.None => StatusText.PackOuterNoneText,
                _ => StatusText.PackOuterRarText
            };
        }

        /// <summary>日志 / 摘要里的短名（<c>rar</c> / <c>7z</c> / <c>不做外层</c>）。</summary>
        public static string ShortName(this PackOuterContainer container)
        {
            return container switch
            {
                PackOuterContainer.SevenZip => StatusText.PackOuterSevenZipShort,
                PackOuterContainer.None => StatusText.PackOuterNoneShort,
                _ => StatusText.PackOuterRarShort
            };
        }

        /// <summary>
        /// 句子里指代外层产物时的名词（"结果 rar 不能放在…" / "外层 7z 不能放在…"）。
        /// 不留空：调用方都是"确实有外层产物"的分支。
        /// </summary>
        public static string Noun(this PackOuterContainer container)
        {
            return container switch
            {
                PackOuterContainer.SevenZip => "外层 7z",
                PackOuterContainer.None => "外层容器",
                _ => "结果 rar"
            };
        }
    }

    /// <summary>
    /// 一次打包请求（用户 2026-09-22 需求第 10 条）。
    ///
    /// <para><b>密码的存放口径</b>（AGENTS.md §6 不变量 5）：密码只在**内存里**活到本次打包结束 ——
    /// 不写设置文件、不进日志、不进结果区（界面上的「复制密码」按钮从内存里取）。
    /// 命令行里必然会出现 <c>-p&lt;密码&gt;</c> / <c>-hp&lt;密码&gt;</c>（外部工具的固有限制），
    /// 这一点写在「已知限制」里，不假装解决了。</para>
    /// </summary>
    public sealed class PackingRequest
    {
        /// <summary>
        /// 用户**选中的那个路径**：文件夹（主用法）或**单个文件**（用户 2026-09-26 第 46 条：
        /// 选 <c>111.mp4</c> 时程序在它旁边生成 <c>111\</c> 把它装进去，再按文件夹打包）。
        /// 真正要打包的文件夹由 <see cref="PackingSourceResolver"/> 解析出来。
        /// </summary>
        public string SourceFolder { get; init; } = string.Empty;

        /// <summary>
        /// 装 7z 分卷的文件夹（= **其余物**）；留空时按落点 + 源名推（重名自动让位）。
        /// </summary>
        public string OutputFolder { get; init; } = string.Empty;

        /// <summary>结果 rar 的路径（留空时按"落点目录 + 源名.rar"推）。</summary>
        public string RarPath { get; init; } = string.Empty;

        /// <summary>
        /// 分卷大小（字节，整数 MiB）。**0 = 自动**（默认）：按 <see cref="PackingVolumeRule"/>
        /// 以"内容 &lt;1 GiB → 2 卷、≥1 GiB → 3 卷"算出每卷上限（用户 2026-09-26 拍板）。
        /// 填了正数就是显式指定（测试与旧路径用）。
        /// </summary>
        public long VolumeSizeBytes { get; init; } = AutoVolumeSizeBytes;

        /// <summary>分卷大小 = 自动（见 <see cref="VolumeSizeBytes"/>）。</summary>
        public const long AutoVolumeSizeBytes = 0;

        /// <summary>内层 7z 分卷的密码（**必填**）。</summary>
        public string Password { get; init; } = string.Empty;

        /// <summary>外层容器的密码；留空 = 与内层同一个（默认就是同一个）。</summary>
        public string OuterPassword { get; init; } = string.Empty;

        /// <summary>
        /// 外层容器（用户 2026-09-23 决定：三选一）。**默认 <see cref="PackOuterContainer.Rar"/>**。
        ///
        /// <para>⚠ 2026-09-26 第 46 条起：没装 WinRAR 时**自动改用 7z 外层**（用户原话：
        /// "如果用户没有装 WinRAR，那就弄 7z 吧"）—— 由界面/服务在起跑前把
        /// <see cref="PackOuterContainer.SevenZip"/> 填进来，不再拿"装 WinRAR"卡住整件事。</para>
        /// </summary>
        public PackOuterContainer OuterContainer { get; init; } = PackOuterContainer.Rar;

        /// <summary>
        /// 原包操作 / 其余物操作 / 落点（用户 2026-09-26 第 46 条；⛔ 与解压那套完全独立）。
        /// </summary>
        public PackingRunOptions RunOptions { get; init; } = new();

        /// <summary>外层实际用的密码：单独填了就用它，没填就与内层一致。</summary>
        public string EffectiveOuterPassword =>
            string.IsNullOrEmpty(OuterPassword) ? Password : OuterPassword;

        /// <summary>外层用的是不是与内层不同的密码（界面提示用）。</summary>
        public bool UsesSeparateOuterPassword =>
            !string.IsNullOrEmpty(OuterPassword) && !string.Equals(OuterPassword, Password, StringComparison.Ordinal);

        /// <summary>
        /// 进日志的一句话。**绝不包含密码本身**（连掩码过的 <c>-p</c> 开关都不拼）——
        /// 日志里只留"已设置密码"这个事实。
        /// </summary>
        public string DescribeForLog()
        {
            string volume = VolumeSizeBytes > 0
                ? $"分卷上限：{PackingPlan.FormatVolumeSize(VolumeSizeBytes)}"
                : "分卷：自动（按内容定卷数）";

            return $"源：{SourceFolder}；{RunOptions.DescribeForLog()}；"
                 + $"外层容器：{OuterContainer.Describe()}；{volume}；"
                 + PackingPasswordPolicy.SetLogLine;
        }
    }

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
