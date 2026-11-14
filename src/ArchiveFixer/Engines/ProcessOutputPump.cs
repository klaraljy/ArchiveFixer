using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines;

/// <summary>
/// 把外部进程的输出流切成"行 / 行内片段"。
///
/// <para>
/// <b>为什么不能用 <c>Process.BeginOutputReadLine</c>（旧写法）</b>：
/// </para>
/// <list type="number">
/// <item><description>UnRAR 的百分比是**在同一个文件那一行里用退格回退重写**的
/// （实测：<c>Extracting  …payload.bin     \b\b\b\b  1%\b\b\b\b  3%…</c>）。
/// 按行读取时这一整行要等到文件解完才会到达 —— 一个 5GB 的单文件在解完之前**一个进度都拿不到**，
/// 而"长时间无输出"正是用户抱怨的那个现象。</description></item>
/// <item><description>7-Zip 的进度是 <c>\r</c> 分隔的（实测）。
/// 靠 <c>StreamReader.ReadLine()</c> 把 <c>\r</c> 也当行尾只是**碰巧**能工作，
/// 而且会把 <c>\r\n</c> 拆成"一个片段 + 一个空行"，污染 <c>-slt</c> 的空行分隔语义。</description></item>
/// </list>
///
/// <para>
/// 本类**不含任何引擎知识**：它只认 <c>\r</c> / <c>\n</c> / <c>\b</c> 三个控制字符。
/// "这条片段是不是进度行"由各引擎自己目录里的解析器回答（AGENTS.md §3.1 禁止项②）。
/// </para>
/// </summary>
public static class ProcessOutputPump
{
    private const int BufferSize = 4096;

    /// <summary>
    /// 读到流结束（或令牌取消），把切出来的片段逐条交给 <paramref name="onSegment"/>。
    ///
    /// <paramref name="onSegment"/> 在同一线程上按顺序调用，实现里**不允许**做阻塞动作。
    /// </summary>
    public static async Task PumpAsync(
        TextReader reader,
        Action<ProcessOutputSegment> onSegment,
        CancellationToken cancellationToken = default)
    {
        if (reader == null || onSegment == null)
        {
            return;
        }

        var buffer = new char[BufferSize];
        var pending = new StringBuilder();
        bool pendingCarriageReturn = false;

        void Flush(ProcessOutputSegmentKind kind)
        {
            onSegment(new ProcessOutputSegment(pending.ToString(), kind));
            pending.Clear();
        }

        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];

                    if (pendingCarriageReturn)
                    {
                        /*
                         * 上一个字符是 \r。它到底算"行尾"还是"就地把这一行重写"，
                         * 取决于紧接着的这个字符是不是 \n —— 所以必须等到现在才能下结论。
                         */
                        pendingCarriageReturn = false;
                        Flush(c == '\n' ? ProcessOutputSegmentKind.Line : ProcessOutputSegmentKind.Fragment);

                        if (c == '\n')
                        {
                            continue;
                        }
                    }

                    switch (c)
                    {
                        case '\r':
                            pendingCarriageReturn = true;
                            break;

                        case '\n':
                            Flush(ProcessOutputSegmentKind.Line);
                            break;

                        case '\b':
                            // 退格 = 引擎把光标退回去重写，所以它结束的是"一段片段"。
                            Flush(ProcessOutputSegmentKind.Fragment);
                            break;

                        default:
                            pending.Append(c);
                            break;
                    }
                }
            }

            // 流结束时还挂着一个孤立的 \r：它是片段分隔符（进度行就是这么结束的）。
            if (pendingCarriageReturn)
            {
                Flush(ProcessOutputSegmentKind.Fragment);
            }

            // 结尾没有换行的最后一行也要交出去，否则最后一条输出会被丢掉
            //（7-Zip 出错时最后一行常常没有换行符）。
            if (pending.Length > 0)
            {
                Flush(ProcessOutputSegmentKind.Line);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消 / 进程被杀 → 流被关掉。这不是错误，剩余内容已经没有必要再收。
        }
        catch (ObjectDisposedException)
        {
            // 进程对象先被释放了（取消路径）。
        }
        catch (IOException)
        {
            // 管道被对端关闭。
        }
    }
}
