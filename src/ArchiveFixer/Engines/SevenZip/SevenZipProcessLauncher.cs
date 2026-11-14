using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 7-Zip 进程的**启动**这一块：<see cref="ProcessStartInfo"/> 的形状与"起得来/起不来"。
///
/// <para>两档参数表在这里只差一件事 —— 文本模式按 UTF-8 解码两路输出，二进制模式**不设解码器**
/// （<c>-so</c> 吐的是原始字节，用文本读取器会把内容改坏）。其余开关、重定向、工作目录、
/// 逐项 <c>ArgumentList</c>（不拼命令行字符串）全部一致。</para>
///
/// <para>⛔ 本类只创建/启动进程，不杀进程：收尸只在 <see cref="SevenZipProcessTerminator"/>，
/// 且只针对本类刚刚启动的那个 PID。</para>
/// </summary>
internal static class SevenZipProcessLauncher
{
    /// <summary>文本模式（test / extract / list）：两路输出按 UTF-8 解码。</summary>
    internal static Process CreateTextProcess(string sevenZipPath, IEnumerable<string> arguments)
    {
        ProcessStartInfo startInfo = CreateStartInfo(sevenZipPath, arguments);

        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        return CreateProcess(startInfo);
    }

    /// <summary>
    /// 二进制模式（<c>x -so</c> 读条目开头那几十字节）：**不设** <c>StandardOutputEncoding</c>，
    /// 让 <see cref="Process.StandardOutput"/> 的底层流原样给出字节。
    /// </summary>
    internal static Process CreateBinaryProcess(string sevenZipPath, IEnumerable<string> arguments)
    {
        return CreateProcess(CreateStartInfo(sevenZipPath, arguments));
    }

    /// <summary>启动进程；返回 <c>Process.Start()</c> 的原始结果（false = 没起来）。</summary>
    internal static bool TryStart(Process process)
    {
        return process.Start();
    }

    private static ProcessStartInfo CreateStartInfo(string sevenZipPath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        foreach (string arg in arguments)
        {
            if (arg != null)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        return startInfo;
    }

    private static Process CreateProcess(ProcessStartInfo startInfo)
    {
        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }
}
