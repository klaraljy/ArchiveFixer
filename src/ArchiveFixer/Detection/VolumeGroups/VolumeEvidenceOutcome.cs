using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Detection;
/// <summary>一条证据在这次判定里的结论。<see cref="Unknown"/> ≠ <see cref="NotSatisfied"/>（没取到 ≠ 不成立）。</summary>
public enum VolumeEvidenceOutcome
{
    /// <summary>没取到 / 没试（例如没有可用引擎、跨卷做不了硬链接）。</summary>
    Unknown = 0,

    /// <summary>成立。</summary>
    Satisfied = 1,

    /// <summary>不成立（**是事实，不是猜测**）。</summary>
    NotSatisfied = 2,
}
