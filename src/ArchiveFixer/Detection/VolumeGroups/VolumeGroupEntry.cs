using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Detection
{    /// <summary>目录里的一个文件（判定器的输入事实：路径 + 体积 + 时间 + 枚举次序）。</summary>
    public sealed class VolumeGroupEntry
    {
        /// <summary>完整路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>字节数（量不出时 &lt;= 0）。</summary>
        public long Size { get; init; }

        /// <summary>最后写入时间（用于"与邻居的相对位置"这一条弱证据）。</summary>
        public DateTime LastWriteTimeUtc { get; init; }

        /// <summary>目录枚举次序（0 起；目录自己的顺序不保证稳定，所以只当弱证据）。</summary>
        public int OrderIndex { get; init; }
    }

}
