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
{    /// <summary>
     /// **对外唯一的那一个结论**。四档，没有第五档；⛔ 不许再往外吐"可能缺卷"这类没定义的中间态。
     /// </summary>
    public enum VolumeGroupVerdict
    {
        /// <summary>完整（有证据 —— 名字序号齐全，或试开成立）。</summary>
        Complete = 0,

        /// <summary>不完整·缺第 N 卷（**有证据**：卷号上有洞，且这一洞没有任何可采信的填充物）。</summary>
        IncompleteMissingVolume = 1,

        /// <summary>不完整·疑缺卷（**弱证据**：只有"按体积/位置推定"的候选，或体积规律对不上）。⛔ 只报不删。</summary>
        IncompleteSuspected = 2,

        /// <summary>判不出（名字对不上、目录读不到、关键事实取不到）。⛔ 不删源、不移动源。</summary>
        Undetermined = 3,
    }

}
