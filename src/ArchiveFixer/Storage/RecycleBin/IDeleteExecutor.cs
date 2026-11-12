using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;

namespace ArchiveFixer.Storage
{    /// <summary>
     /// 真正动手删的那一层。抽出来是为了让 <see cref="RecycleBinService"/> 的
     /// 安全规则与日志**可以脱离真实回收站被测试**（测试绝不能往用户系统回收站里塞东西）。
     /// </summary>
    public interface IDeleteExecutor
    {
        /// <summary>
        /// 移入回收站。
        ///
        /// 返回 <see cref="RecycleAttemptResult.Unavailable"/> 表示回收站不可用 ——
        /// 实现方**不得**在此时改用永久删除，调用方也不得据此自行降级（§3.2 清理表）。
        /// </summary>
        RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message);

        /// <summary>永久删除（文件，或目录递归）。失败时抛异常，由调用方记录并不影响其余目标。</summary>
        void DeletePermanently(string path, bool isDirectory);
    }

}
