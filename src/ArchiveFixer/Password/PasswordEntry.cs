namespace ArchiveFixer.Password;

/// <summary>
/// 密码本里的一条候选密码。
///
/// 隐私约定（AGENTS.md §8）：<see cref="Password"/> 是明文，只能在内存里流转；
/// 日志、报告、异常信息里一律不得输出它，也不要依赖默认的 ToString 打印本类型。
/// </summary>
public sealed class PasswordEntry
{
    /// <summary>明文密码（仅内存）。列表式是整行原文，映射式是冒号右侧原文。</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>映射式的"名称"；列表式为 null。名称已 Trim（名称前后空格是排版噪音）。</summary>
    public string? Name { get; init; }

    /// <summary>条目类型。</summary>
    public PasswordEntryKind Kind { get; init; }

    /// <summary>在密码本文件里的行号，1 起。Parse 无行号概念时按拆分顺序编号。</summary>
    public int LineNumber { get; init; }

    /// <summary>给界面看的说明，例如"来自第 12 行"。不含密码内容。</summary>
    public string? Remark { get; init; }

    /// <summary>
    /// 映射式条目的**整行原文**（例：<c>abc:123</c>）；列表式与空密码标记为 null。
    ///
    /// <para><b>为什么必须留着它</b>（2026-09-25 第 30 条）：「映射式还是列表式」只能靠"这一行里有没有冒号"
    /// 来猜，而**列表式的密码本身完全可能带冒号** —— <c>abc:123</c>、<c>www.xxx.com:8888</c>
    /// 这类资源站密码很常见。那种情况下冒号右侧根本不是密码，**整行才是**。
    /// 于是候选链要"两种理解都试"（见 <c>PasswordService.GetPasswordCandidates</c> 的 BookRawLine 一档），
    /// 而密码列表界面上一条都不多（整行只在候选链里出现）。</para>
    /// </summary>
    public string? RawLine { get; init; }

    /// <summary>
    /// 脱敏显示，避免手滑把本对象丢进日志或绑定到界面时泄露明文。
    /// </summary>
    public override string ToString()
    {
        string body = Password.Length == 0 ? "空密码" : "******";

        return Name == null
            ? $"{Kind} 第 {LineNumber} 行：{body}"
            : $"{Kind} 第 {LineNumber} 行（{Name}）：{body}";
    }
}
