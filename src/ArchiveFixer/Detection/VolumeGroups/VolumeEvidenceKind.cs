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
/// <summary>
/// 一条**可核查证据**的种类。
///
/// <para>分卷组装的全部判据只有这六条，判据实现只此一处（AGENTS.md §9.5「同一件事只有一个出口」）。</para>
/// </summary>
public enum VolumeEvidenceKind
{
    /// <summary>基名相等（去卷标记、去归档后缀之后名字相同）。</summary>
    BaseName = 0,

    /// <summary>卷号从 1 起连续、没有洞（<c>.00N</c> / <c>.z0N</c> / <c>.rNN</c> / <c>.partNN</c>）。</summary>
    Sequence = 1,

    /// <summary>体积关系：除最后一卷外体积相等（裸切），最后一卷 ≤ 其余。</summary>
    SizePattern = 2,

    /// <summary>物理同一性：同一目录 / 同一卷根；同 FileId ⇒ 同一份（硬链接或同一个文件的两个名字）。</summary>
    PhysicalIdentity = 3,

    /// <summary>没有卷号的候选按**体积 + 目录内位置/时间**插进序列（**推定**，置信度低一档）。</summary>
    PositionInference = 4,

    /// <summary>硬链接试开（最高权重、最终判据）。</summary>
    TrialOpen = 5,
}
