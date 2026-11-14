using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage
{    /// <summary>回收站删除的尝试结果。</summary>
    public enum RecycleAttemptResult
    {
        /// <summary>已移入回收站（目标已经不在原位置）。</summary>
        Recycled = 0,

        /// <summary>回收站不可用（网络盘 / 策略禁用 / 卷没有回收站 / 非 64 位进程）：<b>没有执行任何删除</b>。</summary>
        Unavailable = 1,

        /// <summary>回收站可用但这次没成功（占用 / 权限 / 被取消 / 系统错误）。</summary>
        Failed = 2
    }

}
