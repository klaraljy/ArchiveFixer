using ArchiveFixer.Password;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Services.Password;

/// <summary>
/// **侧车**：从归档同目录的说明文件里抽密码候选（<see cref="SidecarPasswordReader"/> 的候选链入口）。
///
/// <para>它只负责"文件 → 值 + 一句给日志看的来源说明"；排序与去重由
/// <see cref="PasswordCandidateBuilder"/> 统一做（侧车候选总是排在最后，因为它是猜的）。
/// 来源说明里只有说明文件名与行号，**不含文件内容、不含密码明文**。</para>
/// </summary>
internal sealed class PasswordSidecarCandidateSource
{
    internal IEnumerable<(string Value, string Remark)> Read(string archivePath)
    {
        foreach (SidecarCandidate candidate in SidecarPasswordReader.ReadCandidates(archivePath))
        {
            string fileName = Path.GetFileName(candidate.SourceFile);

            yield return (candidate.Password, $"同目录说明文件 {fileName} 第 {candidate.LineNumber} 行");
        }
    }
}
