using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    public static partial class VolumeGatherPlanner
    {        /// <summary>**跨盘 ZIP 收卷**的结论（纯事实 + 已经建好的链接；用户的源文件一个字节都不动）。</summary>
        internal sealed class SpannedZipDiskGather
        {
            /// <summary>这一档适不适用：入口必须**自述是跨盘 zip 的末片**（EOCD 盘号 &gt; 0 且总片数 ≥ 2）。</summary>
            public bool Applicable { get; init; }

            /// <summary>入口那一层现在**逐字规范名**地摆着第 1..n-1 片（引擎解得开）。</summary>
            public bool Complete { get; init; }

            /// <summary>入口自述的总片数（含末片）。</summary>
            public int DiskCount { get; init; }

            /// <summary>这一趟真建了几条链接。</summary>
            public int LinkedCount { get; init; }

            /// <summary>
            /// 这一趟建出来的链接**路径**（调用方必须在用完这一层之后把它们删掉 —— 它们是我们的临时名字，
            /// ⛔ 不是内容物：留着会被发布侧当成品搬进用户目录、还会把"半套分卷"闸门自己绊倒，见 2026-10-05 真机）。
            /// </summary>
            public IReadOnlyList<string> LinkedPaths { get; init; } = Array.Empty<string>();

            /// <summary>这几条链接指向的**用户源片路径**（用于"这一组已由谁解出"的记账）。</summary>
            public IReadOnlyList<string> LinkedSources { get; init; } = Array.Empty<string>();

            /// <summary>点名还缺哪几片（规范卷名）。</summary>
            public IReadOnlyList<string> MissingNames { get; init; } = Array.Empty<string>();

            /// <summary>给人看的一句话。</summary>
            public string Detail { get; init; } = string.Empty;
        }
    }
}