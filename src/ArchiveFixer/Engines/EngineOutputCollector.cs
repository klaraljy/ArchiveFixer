using System;
using System.Text;

namespace ArchiveFixer.Engines
{
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
