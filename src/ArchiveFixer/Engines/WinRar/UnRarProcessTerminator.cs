using System.Diagnostics;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// UnRAR 进程的**收尸**这一块：取消 / 超时 / 异常之后把自己启动的进程收干净。
///
/// <para>不变量 10：只杀自己启动的 PID 及其子进程。本类收的是运行器刚创建的那个
/// <see cref="Process"/> 对象，⛔ 不按名字杀、不扫全局进程表。</para>
///
/// <para>为什么还要 <c>taskkill /PID /T /F</c> 兜一层：<c>Process.Kill(entireProcessTree: true)</c>
/// 在管道仍被继承、或进程卡在驱动里时可能抛异常/杀不掉；那时按**已取到的 PID** 强制收树，
/// 仍然只波及这一棵自己启动的树。取不到 PID 时什么都不做（判不出 ⇒ 不动）。</para>
/// </summary>
internal static class UnRarProcessTerminator
{
    /// <summary>强制结束进程树（取消 / 超时 / 异常路径），并等它真的退出。</summary>
    internal static async Task KillTreeAsync(Process? process)
    {
        int? pid = null;

        try
        {
            if (process != null)
            {
                pid = process.Id;
            }
        }
        catch
        {
            pid = null;
        }

        try
        {
            if (process != null)
            {
                try
                {
                    process.CancelOutputRead();
                }
                catch
                {
                }

                try
                {
                    process.CancelErrorRead();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        try
        {
            if (process != null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        bool exited = false;

        try
        {
            if (process != null)
            {
                exited = process.WaitForExit(10000);
            }
        }
        catch
        {
            exited = false;
        }

        // ⚠ 只按**自己起的那个 PID** 收尸（不变量 10：禁止按进程名批量杀 UnRAR.exe）。
        if (!exited && pid.HasValue)
        {
            RunTaskKillByPid(pid.Value);
        }

        await Task.Delay(800).ConfigureAwait(false);
    }

    /// <summary>
    /// 正常收尾（进程已经退出）：断开两路读取并释放 <see cref="Process"/>。
    ///
    /// 与 <see cref="KillTreeAsync"/> 分开：这条路**不杀任何东西**，只放句柄，
    /// 所以它可以在 <c>finally</c> 里无条件调用。
    /// </summary>
    internal static void Release(Process? process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            process.CancelOutputRead();
        }
        catch
        {
        }

        try
        {
            process.CancelErrorRead();
        }
        catch
        {
        }

        try
        {
            process.Dispose();
        }
        catch
        {
        }
    }

    private static void RunTaskKillByPid(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("/PID");
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add("/T");
            psi.ArgumentList.Add("/F");

            using Process taskkill = Process.Start(psi)!;
            taskkill.WaitForExit(5000);
        }
        catch
        {
        }
    }
}
