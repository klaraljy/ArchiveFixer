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
{    /// <summary>证据权重。落结论时**只按权重与是否成立**，不按"名字像不像"。</summary>
    public enum VolumeEvidenceWeight
    {
        /// <summary>弱证据：只够"报出来"，不够"据此下破坏性结论"。</summary>
        Weak = 0,

        /// <summary>强证据：名字 / 体积 / 位置 / 物理同一性这一档。</summary>
        Strong = 1,

        /// <summary>决定性证据：引擎试开成立或不成立 —— 它一票定案。</summary>
        Decisive = 2,
    }

}
