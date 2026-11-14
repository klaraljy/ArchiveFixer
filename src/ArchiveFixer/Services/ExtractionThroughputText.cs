using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 「这段活干了多快」的措辞（写出字节 ÷ 用时）。
///
/// <para>它原本长在 <see cref="RestHandlingStep"/> 里，但和其余物处理**没有任何生命周期关系** ——
/// 它是直读那一条日志的措辞。拆出来是为了不让「其余物」这一步混进一个无关职责，而不是按行数切。</para>
/// </summary>
internal static class ExtractionThroughputText
{
    /// <summary>
    /// 把"这段活干了多快"说成人话（"21.0 MB/s"）——直读那一条日志用它（用户 2026-09-25 第 38 条）。
    /// 速度按**写出字节 ÷ 用时**算：直读的真实工作量就是把这些字节从源读到成品。
    /// </summary>
    internal static string Describe(long writtenBytes, TimeSpan elapsed)
    {
        if (writtenBytes <= 0 || elapsed.TotalSeconds <= 0.05)
        {
            return "速度算不出来（太小或太快）";
        }

        double mibPerSecond = writtenBytes / 1024d / 1024d / elapsed.TotalSeconds;

        return $"{mibPerSecond:0.0} MB/s";
    }
}
