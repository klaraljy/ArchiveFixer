using ArchiveFixer.Helpers;

namespace ArchiveFixer.Password;

/// <summary>
/// 从归档旁边的说明文件里抽出来的一个密码候选。
///
/// 隐私约定（AGENTS.md §8）：<see cref="Password"/> 是明文，只能在内存里流转；
/// 日志、报告、异常信息里一律不得输出它，也不要依赖默认的 ToString 打印本类型。
/// </summary>
public sealed class SidecarCandidate
{
    /// <summary>明文密码（仅内存）。</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>来源说明文件的完整路径。</summary>
    public string SourceFile { get; init; } = string.Empty;

    /// <summary>在说明文件里的行号，1 起。</summary>
    public int LineNumber { get; init; }

    /// <summary>给界面看的说明，例如"来自 archive.7z 同目录的 说明.txt 第 3 行"。不写文件内容。</summary>
    public string? Remark { get; init; }

    /// <summary>
    /// 脱敏显示，避免手滑把本对象丢进日志或绑定到界面时泄露明文。
    /// </summary>
    public override string ToString()
    {
        return $"******（来自 {FileNameHelper.GetFileName(SourceFile)} 第 {LineNumber} 行）";
    }
}
