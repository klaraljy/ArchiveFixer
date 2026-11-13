using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{    /// <summary>一次试开的结论（由 <c>Extraction/VolumeProbeVerifier</c> 落地：硬链接 + 引擎列目录）。</summary>
    public sealed class VolumeTrialOutcome
    {
        /// <summary>顺序成立（引擎真的读到了里面的条目，或认出这一组是"文件名也加密"的归档）。</summary>
        public bool Confirmed { get; init; }

        /// <summary>真的试过（false = 没有可用引擎 / 做不了硬链接 —— 这一档是"没试"，不是"不成立"）。</summary>
        public bool Attempted { get; init; }

        /// <summary>没试的结构化原因（跨盘那一档会改变结论，见 <see cref="VolumeTrialSkipReason"/>）。</summary>
        public VolumeTrialSkipReason SkipReason { get; init; } = VolumeTrialSkipReason.None;

        /// <summary>成立但读不出清单（<c>-mhe</c> / <c>-hp</c>，要正确密码才列得出条目）。</summary>
        public bool NeedsPassword { get; init; }

        /// <summary>成立时的完整卷序（第 1 卷在第一位）。</summary>
        public IReadOnlyList<string> OrderedVolumePaths { get; init; } = Array.Empty<string>();

        /// <summary>试了几种排列。</summary>
        public int Attempts { get; init; }

        /// <summary>给人看的一句话。</summary>
        public string Reason { get; init; } = string.Empty;
    }

}
