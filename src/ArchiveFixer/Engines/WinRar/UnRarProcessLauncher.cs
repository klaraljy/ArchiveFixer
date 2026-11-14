using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// UnRAR 进程的**启动**这一块：<see cref="ProcessStartInfo"/> 的形状、"起得来/起不来"、
/// 以及启动之后立刻关掉标准输入。
///
/// <para>两路输出一律按 UTF-8 解码（配合参数的 <c>-scf</c>，见 <see cref="UnRarCommandLine"/>）；
/// 逐项 <see cref="ProcessStartInfo.ArgumentList"/>（不拼命令行字符串）、重定向、
/// 建窗口开关与工作目录都与 7-Zip 侧同一口径。</para>
///
/// <para>⛔ 本类只创建/启动进程，不杀进程：收尸只在 <see cref="UnRarProcessTerminator"/>，
/// 且只针对本类刚刚启动的那个 PID。</para>
/// </summary>
internal static class UnRarProcessLauncher
{
    /// <summary>按 UnRAR 的文本口径创建一个还没启动的进程对象。</summary>
    internal static Process CreateProcess(string unRarPath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = unRarPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };

        foreach (string arg in arguments)
        {
            if (arg != null)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }

    /// <summary>启动进程；返回 <c>Process.Start()</c> 的原始结果（false = 没起来）。</summary>
    internal static bool TryStart(Process process)
    {
        return process.Start();
    }

    /// <summary>
    /// 立刻关掉标准输入：UnRAR 一旦想"问密码"就拿到 EOF 立刻失败，
    /// 而不是把整个任务挂在那里等一个永远不会来的输入（我们一律用 <c>-p</c>/<c>-p-</c> 避免它问）。
    /// </summary>
    internal static void CloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
        }
    }
}
