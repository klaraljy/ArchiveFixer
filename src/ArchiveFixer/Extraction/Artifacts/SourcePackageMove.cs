using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{    /// <summary>一条源包搬运计划（纯数据：不代表磁盘上已经发生任何事）。</summary>
    public sealed class SourcePackageMove
    {
        public string SourcePath { get; init; } = string.Empty;

        public string TargetPath { get; init; } = string.Empty;

        /// <summary>源文件字节数（写日志 / 报总大小用）。</summary>
        public long Size { get; init; }

        /// <summary>目标同名，已加序号（<b>绝不覆盖</b>）。</summary>
        public bool Renamed { get; init; }
    }

}
