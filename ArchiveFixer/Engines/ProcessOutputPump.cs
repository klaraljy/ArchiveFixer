using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines
{
    /// <summary>输出片段的种类。</summary>
    public enum ProcessOutputSegmentKind
    {
        /// <summary>一整行：以 <c>\n</c>（含 <c>\r\n</c>）结束。空行也是行（<c>-slt</c> 靠空行分隔条目）。</summary>
        Line = 0,

        /// <summary>
        /// 行内片段：以单独一个 <c>\r</c> 或退格 <c>\b</c> 结束 —— 引擎"就地重写"的进度用的就是它。
        /// </summary>
        Fragment = 1
    }

    /// <summary>从外部进程输出流里切出来的一段文本。</summary>
    public readonly struct ProcessOutputSegment
    {
        public ProcessOutputSegment(string text, ProcessOutputSegmentKind kind)
        {
            Text = text ?? string.Empty;
            Kind = kind;
        }

        public string Text { get; }

        public ProcessOutputSegmentKind Kind { get; }

        public override string ToString()
        {
            return Kind == ProcessOutputSegmentKind.Line ? $"[行]{Text}" : $"[片段]{Text}";
        }
    }

    /// <summary>
    /// 纯函数式的进度解析签名：给一段输出，回答"这是不是进度，是的话到哪了"。
    /// **实现只允许出现在各自的引擎目录里**（<c>Engines/SevenZip/</c>、<c>Engines/WinRar/</c>）。
    /// </summary>
    public delegate ArchiveProgress? EngineProgressParser(string segment);

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

    /// <summary>
    /// 把一路输出（stdout 或 stderr）的片段收成三件事：**活动时间**、**进度**、**日志缓冲区**。
    ///
    /// <para>
    /// 两个引擎运行器共用它，避免"哪边漏了 MarkActivity"这种隐蔽的不一致；
    /// 它同样不含引擎知识（判断是不是进度靠注入的 <see cref="EngineProgressParser"/>）。
    /// </para>
    ///
    /// <para>
    /// 收集规则（两条，都很保守）：
    /// </para>
    /// <list type="number">
    /// <item><description><b>行内片段</b>（<c>\r</c> / <c>\b</c> 分隔的）里净是空白的，一律丢掉 ——
    /// 那是引擎用来"擦掉上一行"的填充空格，以前它们会被当成一条条独立的行塞进日志与错误分类。</description></item>
    /// <item><description>被解析器认定成进度的那一段，只喂给节流后的进度接收端，**不进缓冲区**：
    /// 进度行又密又长，进了缓冲区就会淹没日志、干扰错误关键字分类。</description></item>
    /// </list>
    ///
    /// <para>
    /// ⚠ <b>空行必须保留</b>：<c>7z l -slt</c> 用空行分隔条目，把它一起丢掉会直接让列目录解析失效。
    /// 所以这里只丢"行内片段"里的空白，不丢 <see cref="ProcessOutputSegmentKind.Line"/> 的空行。
    /// </para>
    /// </summary>
    public sealed class EngineOutputCollector
    {
        private readonly EngineProgressParser? _progressParser;
        private readonly StringBuilder _buffer;
        private readonly object _bufferLock;
        private readonly ArchiveProgressReporter? _reporter;
        private readonly EngineOutputActivityMonitor? _monitor;

        public EngineOutputCollector(
            StringBuilder buffer,
            object bufferLock,
            EngineProgressParser? progressParser = null,
            ArchiveProgressReporter? reporter = null,
            EngineOutputActivityMonitor? monitor = null)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _bufferLock = bufferLock ?? new object();
            _progressParser = progressParser;
            _reporter = reporter;
            _monitor = monitor;
        }

        /// <summary>这一路收到的进度片段条数（诊断用；被节流丢掉的不算）。</summary>
        public int ProgressSegmentCount { get; private set; }

        public void Handle(ProcessOutputSegment segment)
        {
            // 任何输出都算"引擎还活着"，包括进度行与空行 —— 卡住判定的口径就是"有没有任何输出"。
            _monitor?.MarkActivity();

            if (_progressParser != null)
            {
                ArchiveProgress? progress = _progressParser(segment.Text);

                if (progress != null)
                {
                    ProgressSegmentCount++;
                    _reporter?.Report(progress);
                    return;
                }
            }

            if (segment.Kind == ProcessOutputSegmentKind.Fragment && string.IsNullOrWhiteSpace(segment.Text))
            {
                return;
            }

            lock (_bufferLock)
            {
                _buffer.AppendLine(segment.Text);
            }
        }
    }
}
