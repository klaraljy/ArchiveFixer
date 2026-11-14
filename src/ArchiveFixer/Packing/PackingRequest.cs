using System;

namespace ArchiveFixer.Packing;

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
