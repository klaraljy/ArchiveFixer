using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage
{    /// <summary>一次批量删除的结果。</summary>
    public sealed class DeleteResult
    {
        public DeleteMode Mode { get; init; }

        public string AllowedRoot { get; init; } = string.Empty;

        public IReadOnlyList<DeleteOutcome> Outcomes { get; init; } = Array.Empty<DeleteOutcome>();

        /// <summary>本次调用产生的删除日志（与注入的 <see cref="IDeleteLogSink"/> 收到的是同一批）。</summary>
        public IReadOnlyList<DeleteLogEntry> LogEntries { get; init; } = Array.Empty<DeleteLogEntry>();

        public int SuccessCount => Outcomes.Count(outcome => outcome.Success);

        public int FailureCount => Outcomes.Count(outcome => !outcome.Success);

        /// <summary>彻底删除真正释放的字节数合计。</summary>
        public long FreedBytes => Outcomes.Sum(outcome => outcome.FreedBytes);

        /// <summary>移入回收站的字节数合计（未真正释放）。</summary>
        public long RecycledBytes => Outcomes.Sum(outcome => outcome.RecycledBytes);

        /// <summary>失败原因清单（"路径：说明"），失败的每一个都在这里。</summary>
        public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

        /// <summary>一句话结论，可直接进日志 / 提示框。</summary>
        public string Message { get; init; } = string.Empty;
    }

}
