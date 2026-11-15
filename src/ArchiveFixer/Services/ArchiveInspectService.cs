using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 「看内容 / 试密码」的实现（用户 2026-09-26 批准加的两个高频功能）。
///
/// <para><b>看内容</b>：只 `list` —— 列出包里有什么（条目数、总大小、前几条），
/// 用来判断"这个包值不值得解"，**不解压、不写盘**。</para>
///
/// <para><b>试密码</b>：拿候选逐个 `list` —— 问引擎"这个密码能不能开"。
/// 为什么用 `list` 而不是 `test`：`l` 只读头部（大包也是秒级），`t` 要把数据整份读一遍；
/// 这里回答的是"要不要密码、哪个密码能开"，不是"数据有没有坏"（那是解压校验的事）。</para>
///
/// <para>⛔ 两条纪律：①**一个字节都不写盘**（只调 `list`，没有任何输出目录参数）；
/// ②结论里**不许出现密码明文**（只写候选的说明文字与序号）。</para>
/// </summary>
public sealed class ArchiveInspectService
{
    /// <summary>每个任务最多列几条名字（够看清"这是什么包"就行）。</summary>
    public const int MaxTopEntries = 12;

    private readonly IArchiveEngine _engine;

    public ArchiveInspectService(IArchiveEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>
    /// 看一眼一个任务（<paramref name="candidates"/> 为空时只试"不带密码"那一遍）。
    /// </summary>
    public async Task<ArchiveInspectResult> InspectAsync(
        ArchiveTask? task,
        IReadOnlyList<PasswordItem>? candidates,
        CancellationToken cancellationToken = default)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
        {
            return new ArchiveInspectResult
            {
                Task = task,
                Message = "这一行没有可用的文件路径（先右键「重新扫描此文件」或重新添加一次）。"
            };
        }

        var stopwatch = Stopwatch.StartNew();
        string path = task.CurrentPath;

        // 第一遍：不带密码。大多数包到这一步就有结论了（也顺带回答"要不要密码"）。
        ArchiveListResult plain = await _engine
            .ListAsync(ArchiveRequest.For(path), cancellationToken)
            .ConfigureAwait(false);

        if (plain.Success && !plain.IsEncrypted)
        {
            return Build(task, plain, needsPassword: false, passwordFound: false, label: string.Empty,
                tried: 0, total: candidates?.Count ?? 0, message: string.Empty, stopwatch);
        }

        /*
         * 第二遍：需要密码（引擎说加密，或者第一遍就失败了）→ 逐个候选问一遍。
         *
         * 候选**由调用方按既有规则算好**（PasswordService.GetPasswordCandidates，与解压那条路同一份顺序）——
         * ⛔ 这里不许自己拼一套"先试空密码"的规则，否则界面上说的顺序与真正解压时的顺序会不一致。
         */
        int tried = 0;

        if (candidates != null)
        {
            foreach (PasswordItem candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                tried++;

                ArchiveListResult attempt = await _engine
                    .ListAsync(ArchiveRequest.For(path, candidate.Value), cancellationToken)
                    .ConfigureAwait(false);

                if (attempt.Success)
                {
                    return Build(task, attempt, needsPassword: true, passwordFound: true,
                        label: DescribeCandidate(candidate, tried), tried: tried, total: candidates.Count,
                        message: string.Empty, stopwatch);
                }
            }
        }

        // 一个候选都没成：如实说清"试了几个"，并把引擎那句原话带上（失败多说）。
        string reason = string.IsNullOrWhiteSpace(plain.Message)
            ? "引擎没能列出内容"
            : plain.Message;

        /*
         * ⚠ 只有**引擎说这是密码问题**时才能写"需要密码"（2026-09-26 真机代跑当场逮到）：
         * 一个**损坏**的包（`损坏的.7z`）第一遍 list 就失败，于是我也去试了候选，
         * 然后张嘴就说"需要密码：试了 10 个候选都没能打开" —— 那个包压根不加密，
         * 真相是引擎那句「7-Zip 无法识别或不支持该格式」。
         * 判据用引擎给的错误类型（EngineErrorTypes.WrongPassword / EncryptedHeaders），
         * ⛔ 不许用"试过候选"这种自己的动作当判据。
         */
        bool looksLikePasswordProblem =
            plain.IsEncrypted ||
            string.Equals(plain.ErrorType, EngineErrorTypes.WrongPassword, StringComparison.Ordinal) ||
            string.Equals(plain.ErrorType, EngineErrorTypes.EncryptedHeaders, StringComparison.Ordinal);

        string message = looksLikePasswordProblem
            ? $"需要密码：试了 {tried} 个候选都没能打开（{reason}）"
            : $"打不开：{reason}";

        return Build(task, plain, needsPassword: plain.IsEncrypted, passwordFound: false,
            label: string.Empty, tried: tried, total: candidates?.Count ?? 0, message: message, stopwatch);
    }

    /// <summary>候选的说明文字（脱敏）：优先用候选自带的 Remark，没有就按时序编号。</summary>
    private static string DescribeCandidate(PasswordItem candidate, int index)
    {
        string remark = candidate?.Remark ?? string.Empty;

        return string.IsNullOrWhiteSpace(remark)
            ? $"第 {index} 个候选"
            : remark;
    }

    private static ArchiveInspectResult Build(
        ArchiveTask task,
        ArchiveListResult list,
        bool needsPassword,
        bool passwordFound,
        string label,
        int tried,
        int total,
        string message,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();

        return new ArchiveInspectResult
        {
            Task = task,
            Success = list.Success,
            Format = string.IsNullOrWhiteSpace(task.DetectedFormat) ? "Unknown" : task.DetectedFormat,
            NeedsPassword = needsPassword,
            PasswordFound = passwordFound,
            PasswordLabel = label,
            CandidatesTried = tried,
            CandidatesTotal = total,
            FileCount = list.FileCount,
            DirectoryCount = list.DirectoryCount,
            TotalBytes = list.TotalUncompressedSize,
            IsMultiVolume = list.IsMultiVolume,
            TopEntries = (list.Entries ?? Array.Empty<ArchiveEntry>())
                .Where(entry => entry != null)
                .Take(MaxTopEntries)
                .Select(entry => new ArchiveInspectEntry(entry.Path, entry.Size, entry.IsDirectory))
                .ToList(),
            Message = message,
            ElapsedText = stopwatch.Elapsed.TotalSeconds < 1
                ? $"{stopwatch.ElapsedMilliseconds} ms"
                : $"{stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s"
        };
    }
}
