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
{    /// <summary>目标的度量结果：条目数 + 总大小，用于删除日志（§3.2 要求日志报总大小）。</summary>
    public sealed class DeleteMeasurement
    {
        /// <summary>是否完整量到了（目录读不到时为 false，此时数字是"至少这么多"）。</summary>
        public bool Determined { get; init; }

        public bool IsDirectory { get; init; }

        /// <summary>条目数（文件 + 目录，不含目标自己）。单个文件为 1。</summary>
        public int EntryCount { get; init; }

        public int FileCount { get; init; }

        public long TotalBytes { get; init; }
    }

}
