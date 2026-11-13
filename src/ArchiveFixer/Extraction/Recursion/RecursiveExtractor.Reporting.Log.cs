using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    public sealed partial class RecursiveExtractor
    {        /// <summary>
             /// 写一条日志。没有日志出口时（单元测试直接 new）什么也不做；
             /// 日志出口自己抛异常时也吞掉 —— 写日志失败绝不该影响任务结论。
             /// </summary>
        internal void Log(string level, string message)
        {
            try
            {
                _log?.Invoke(level, message);
            }
            catch
            {
                // 见方法注释。
            }
        }

        /// <summary>引擎调用观察口：⛔ 不参与判据；观察者自己抛异常也不影响解压。</summary>
        internal void ObserveEngineCall(ArchiveOperationResult result, string archivePath, string action)
        {
            if (EngineCallObserved == null || result == null)
            {
                return;
            }

            try
            {
                EngineCallObserved(result, archivePath, action);
            }
            catch
            {
                // 诊断观察失败不得影响解压。
            }
        }

    }
}