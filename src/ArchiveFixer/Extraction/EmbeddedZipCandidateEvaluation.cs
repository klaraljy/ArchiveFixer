using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 一个 EOCD 候选的评估结论（扫描阶段的**结果模型**，只在本模块内流转）。
///
/// <para><see cref="IsCandidate"/> 区分两件事：</para>
/// <list type="bullet">
/// <item><description><c>false</c>：那个字节序列根本不是这个归档的收尾 ⇒ 扫描继续往前找下一个候选；</description></item>
/// <item><description><c>true</c>：就是它 ⇒ <see cref="Result"/> 给出最终结论（可能"不支持"，也可能是硬失败
/// <see cref="PathRejected"/> / <see cref="PasswordRejected"/>，或直读可用）。</description></item>
/// </list>
///
/// <para>⛔ 这里只装配结论，不判断：判据都在 <see cref="EmbeddedZipCentralDirectoryScanner"/> 与
/// <see cref="EmbeddedZipCentralDirectoryParser"/> 里。</para>
/// </summary>
internal sealed class EmbeddedZipCandidateEvaluation
{
    /// <summary>这个字节序列是不是这个归档的收尾（不是 ⇒ 继续往前找）。</summary>
    internal bool IsCandidate { get; private init; }

    /// <summary>最终结论（<see cref="IsCandidate"/> 为 true 时非 null）。</summary>
    internal EmbeddedZipProbeResult? Result { get; private init; }

    /// <summary>不是候选（继续往前找）。</summary>
    internal static EmbeddedZipCandidateEvaluation NotACandidate => new() { IsCandidate = false };

    /// <summary>是候选，但不支持直读（回落信号）。</summary>
    internal static EmbeddedZipCandidateEvaluation Unsupported(string reason) => new()
    {
        IsCandidate = true,
        Result = EmbeddedZipOutcomes.Unsupported(reason)
    };

    /// <summary>是候选，但**只差一个密码**（AES 条目在场、这次没给候选密码）。</summary>
    internal static EmbeddedZipCandidateEvaluation NeedsPassword(string reason) => new()
    {
        IsCandidate = true,
        Result = new EmbeddedZipProbeResult
        {
            Supported = false,
            RequiresPassword = true,
            Reason = reason,
            Message = reason
        }
    };

    /// <summary>是候选，但条目名不安全（硬失败，不回落）。</summary>
    internal static EmbeddedZipCandidateEvaluation PathRejected(
        string message,
        List<EmbeddedZipEntry> entries,
        List<ArchiveEntry> listEntries,
        long totalBytes) => new()
        {
            IsCandidate = true,
            Result = new EmbeddedZipProbeResult
            {
                Supported = false,
                PathRejected = true,
                Message = message,
                Reason = message,
                Entries = entries,
                List = new ArchiveListResult
                {
                    Success = true,
                    Entries = listEntries,
                    TotalUncompressedSize = totalBytes,
                    FileCount = listEntries.Count(e => !e.IsDirectory),
                    DirectoryCount = listEntries.Count(e => e.IsDirectory)
                }
            }
        };

    /// <summary>
    /// 是候选，但加密条目的候选密码一个都没通过校验（硬失败，**不回落** —— 理由见
    /// <see cref="EmbeddedZipProbeResult.PasswordRejected"/>）。
    /// </summary>
    internal static EmbeddedZipCandidateEvaluation PasswordRejected(
        string message,
        List<EmbeddedZipEntry> entries,
        List<ArchiveEntry> listEntries,
        long totalBytes,
        bool isEncrypted) => new()
        {
            IsCandidate = true,
            Result = new EmbeddedZipProbeResult
            {
                Supported = false,
                PasswordRejected = true,
                Message = message,
                Reason = message,
                Entries = entries,
                List = new ArchiveListResult
                {
                    Success = true,
                    Entries = listEntries,
                    TotalUncompressedSize = totalBytes,
                    FileCount = listEntries.Count(e => !e.IsDirectory),
                    DirectoryCount = listEntries.Count(e => e.IsDirectory),
                    IsEncrypted = isEncrypted
                }
            }
        };

    /// <summary>是候选，直读可用。</summary>
    internal static EmbeddedZipCandidateEvaluation Ok(
        List<EmbeddedZipEntry> entries,
        ArchiveListResult list,
        long totalBytes,
        long offset,
        long end,
        int resolvedPasswordIndex,
        ZipAesPasswordEncoding resolvedPasswordEncoding,
        string namingSummary = "") => new()
        {
            IsCandidate = true,
            Result = new EmbeddedZipProbeResult
            {
                Supported = true,
                Entries = entries,
                List = list,
                ArchiveLength = end - offset,
                Offset = offset,
                End = end,
                TotalBytes = totalBytes,
                ResolvedPasswordIndex = resolvedPasswordIndex,
                ResolvedPasswordEncoding = resolvedPasswordEncoding,
                NamingSummary = namingSummary
            }
        };
}
