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
{    /// <summary>组里已定位的一卷。</summary>
    public sealed class ResolvedVolume
    {
        /// <summary>完整路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>卷序（1 起）。</summary>
        public int Index { get; init; }

        /// <summary>字节数（量不出时 &lt;= 0）。</summary>
        public long Size { get; init; }

        /// <summary>
        /// 它的**名字里带不带卷号**。false = 靠体积/位置推定或靠试开才认进来的
        /// （例如无后缀的 <c>111</c>、被改成 <c>一只顶美.z删除ip</c> 的那一卷、伪装成 <c>.mp4</c> 的续卷）。
        /// </summary>
        public bool NameCarriesVolumeNumber { get; init; }

        /// <summary>这一卷是靠"按位置推定"插进来的（置信度低一档）。</summary>
        public bool InferredByPosition { get; init; }

        /// <summary>这一卷是靠试开才认进来的。</summary>
        public bool ConfirmedByTrialOpen { get; init; }
    }

}
