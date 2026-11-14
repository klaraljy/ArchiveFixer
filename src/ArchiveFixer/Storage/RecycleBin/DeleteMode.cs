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
{    /// <summary>
     /// 删除档位（AGENTS.md §9.5、docs/输出与整理模型.md §3.2 / §5）。
     /// </summary>
    public enum DeleteMode
    {
        /// <summary>移入回收站（**默认档**）：可恢复，回收站不可用时一律不删。</summary>
        RecycleBin = 0,

        /// <summary>彻底删除（激进档）：不可恢复。界面必须红色标识 + 二次确认（§3.2 清理表）。</summary>
        Permanent = 1
    }

}
