using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage;
/// <summary>
/// 删除日志的落点。由调用方注入（界面日志、文件日志、测试收集器都行）——
/// 本层**不依赖** UI，也不需要知道日志最终写到哪。
/// </summary>
public interface IDeleteLogSink
{
    void Write(DeleteLogEntry entry);
}
