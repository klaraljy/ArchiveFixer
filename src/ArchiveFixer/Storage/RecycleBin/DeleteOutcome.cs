using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;

namespace ArchiveFixer.Storage
{    /// <summary>单个目标的删除结果。</summary>
    public sealed class DeleteOutcome
    {
        public string Path { get; init; } = string.Empty;

        /// <summary>为什么要删（调用方给的理由）。</summary>
        public string Reason { get; init; } = string.Empty;

        public bool Success { get; init; }

        public DeleteMode Mode { get; init; }

        public DeleteBlockReason BlockReason { get; init; }

        /// <summary>中文说明（失败时即失败原因）。</summary>
        public string Message { get; init; } = string.Empty;

        public int EntryCount { get; init; }

        public long TotalBytes { get; init; }

        /// <summary>**彻底删除**真正释放的字节数。移入回收站时为 0（文件还在回收站里占着空间）。</summary>
        public long FreedBytes { get; init; }

        /// <summary>移入回收站的字节数（没有真正释放空间，单独记，免得把"搬走"说成"腾出"）。</summary>
        public long RecycledBytes { get; init; }
    }

}
