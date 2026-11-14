using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 7-Zip 的"**只读某条目解密后的开头 N 字节**"操作（用户 2026-09-25 第 38 条）。
///
/// <para>为什么需要它：7z 的 AES **没有密码校验位**（不像 WinZip AES 有 2 字节校验值），
/// 所以"只验密码不读数据"在协议上做不到；但可以**只读开头几十字节**：密码对，解出来就是文件真正的开头
/// （z7/zip/rar/mp4… 的魔数），密码错，解出来是随机字节 —— 一眼分得开。
/// 代价是 7-Zip 的密钥派生（约 0.1–0.3 秒）+ 读几十字节，**与包多大无关**
/// （这正是"包里没有小文件"时唯一便宜的验密码办法）。</para>
///
/// <para>做法：<c>7z x -so 归档 条目 -p密码</c> 把内容流到标准输出，我们读够
/// <paramref name="maxBytes"/> 就**杀掉自己启动的这个进程树**（不变量 10：只杀自己启动的 PID
/// 及其子进程），于是一个 5 GB 的条目也只花零点几秒、一个字节都不落盘。</para>
///
/// <para>⛔ 判据不在这里：本方法只负责"把开头那些字节拿回来"，怎么判由
/// <c>Extraction/PasswordProbe</c> 决定（它必须保守 —— 看不出文件开头**不等于**密码错）。</para>
/// </summary>
internal static class SevenZipDecryptedPrefixReader
{
    /// <summary>读开头这一小段的硬上限；超时就按"说不准"处理（退回整包试解）。</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <returns>读到的字节（可能少于 <paramref name="maxBytes"/>）；拿不到返回 null。</returns>
    internal static async Task<byte[]?> ReadAsync(
        ToolLocator tools,
        string archivePath,
        string entryPath,
        string password,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) ||
            string.IsNullOrWhiteSpace(entryPath) ||
            maxBytes <= 0 ||
            !File.Exists(tools.SevenZipExePath))
        {
            return null;
        }

        Process? process = null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ReadTimeout);

        try
        {
            /*
             * ⚠ 刻意**不设** StandardOutputEncoding：`-so` 吐的是**原始字节**，
             * 用文本读取器会把非 UTF-8 的内容改坏（我们要拿它比对文件魔数）。
             */
            process = SevenZipProcessLauncher.CreateBinaryProcess(
                tools.SevenZipExePath,
                SevenZipCommandLine.BuildDecryptedPrefixArguments(archivePath, entryPath, password));

            if (!SevenZipProcessLauncher.TryStart(process))
            {
                return null;
            }

            var buffer = new byte[maxBytes];
            int total = 0;

            while (total < maxBytes)
            {
                int read = await process.StandardOutput.BaseStream
                    .ReadAsync(buffer.AsMemory(total, maxBytes - total), timeoutCts.Token)
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total > 0 ? buffer[..total] : null;
        }
        catch
        {
            // 读不到（超时 / 进程起不来 / 输出被截断）：调用方按"说不准"处理（退回整包试解）。
            return null;
        }
        finally
        {
            /*
             * 不管读没读够都要把进程收干净：`-so` 会把整个条目往下倒，
             * 不杀它就会一直读那个 5 GB 的文件（磁盘白转，还可能占住归档）。
             */
            await SevenZipProcessTerminator.KillTreeAsync(process).ConfigureAwait(false);
            process?.Dispose();
        }
    }
}
