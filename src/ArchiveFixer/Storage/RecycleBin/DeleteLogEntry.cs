using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage;
/// <summary>
/// 一条删除日志（路径 + 理由 + 条目数 + 总大小 + 结果）。
///
/// 这是本服务对外"写日志"的唯一形态：<see cref="IDeleteLogSink"/> 把它交给调用方，
/// <see cref="DeleteResult.LogEntries"/> 也原样带回。**不允许**在这里拼 UI，
/// 更不允许记密码类内容（AGENTS.md §6 第 5 条）。
/// </summary>
public sealed class DeleteLogEntry
{
    public DateTime Time { get; init; } = ArchiveFixer.Helpers.Clock.Now;

    public string Path { get; init; } = string.Empty;

    /// <summary>为什么要删（调用方给的理由）。</summary>
    public string Reason { get; init; } = string.Empty;

    public DeleteMode Mode { get; init; }

    public bool Success { get; init; }

    public DeleteBlockReason BlockReason { get; init; }

    /// <summary>结果说明（失败时是失败原因）。</summary>
    public string Message { get; init; } = string.Empty;

    public int EntryCount { get; init; }

    public long TotalBytes { get; init; }

    public string ToDisplayText()
    {
        string modeText = Mode == DeleteMode.Permanent ? "彻底删除" : "移入回收站";

        return $"[{Time:yyyy-MM-dd HH:mm:ss}] [{modeText}] {(Success ? "成功" : "未执行")} " +
               $"路径={Path}；理由={(string.IsNullOrWhiteSpace(Reason) ? "未说明" : Reason)}；" +
               $"条目数={EntryCount}；总大小={TotalBytes} 字节；说明={Message}";
    }
}
