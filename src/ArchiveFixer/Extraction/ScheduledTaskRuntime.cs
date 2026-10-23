using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>调度期每个任务的运行期状态（纯数据；从协调器嵌套类型提到 Extraction 顶层）。</summary>

    /// <summary>逐任务的运行期记账（并发下多个任务同时写，所以全部走锁）。</summary>
    internal sealed class ScheduledTaskRuntime
    {
        /// <summary>当前账上给这个任务预留的字节数（开工时是粗估，拿到清单后可能被调整）。</summary>
        public long ReservedBytes;

        /// <summary>它开工前的可用空间（-1 = 取不到）。</summary>
        public long AvailableBeforeStart = -1;

        /// <summary>它收尾（含其余物处理）之后的可用空间（-1 = 取不到）。</summary>
        public long AvailableAfterFinish = -1;

        /// <summary>其余物是不是被彻底删掉了。</summary>
        public bool RestPurged;

        /// <summary>其余物处理（回收站 / 彻底删除）释放或腾出的字节数。</summary>
        public long PurgedBytes;

        /// <summary>其余物处理没做成时的原因（空 = 没出问题）。</summary>
        public string PurgeNote = string.Empty;
    }
}
