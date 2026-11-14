using ArchiveFixer.Password;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// 一次 UnRAR 运行的**输出读取**这一块：两路缓冲区、两路输出泵、有上限的收尾等待。
///
/// <para>为什么用 <see cref="ProcessOutputPump"/> 自己切流、而不是
/// <c>BeginOutputReadLine</c>：UnRAR 的百分比是**同一个文件那一行内用退格回写**的，
/// 按行读意味着"一个 5GB 的单文件在解完之前一条进度都看不到"（见 <see cref="ProcessOutputPump"/>
/// 的类注释）。本类只负责把泵的产物收进缓冲区，不判退出码、不判错误类型。</para>
///
/// <para>生命周期：<see cref="Complete"/> 之后进度与卡住提示的出口一律关闭，
/// 所以进程退出 / 取消 / 超时之后**不可能**再有迟到回调 —— 运行器在返回之前必定调它一次。</para>
/// </summary>
internal sealed class UnRarOutputCapture
{
    /// <summary>等两路输出泵收完的上限（进程已经退出，正常情况下是毫秒级）。</summary>
    private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(3);

    private readonly StringBuilder _outputBuilder = new();
    private readonly StringBuilder _errorBuilder = new();
    private readonly object _outputLock = new();
    private readonly object _errorLock = new();
    private readonly ArchiveProgressReporter _reporter;
    private readonly EngineOutputCollector _outputCollector;
    private readonly EngineOutputCollector _errorCollector;

    internal UnRarOutputCapture(EngineOperation? operation, EngineProgressContext? progressContext)
    {
        /*
         * UnRAR **默认就带百分比进度**（实测），所以没有"开关要不要打开"这一说；
         * 但列目录（lt）的输出是要被逐行解析的，那边绝不能把进度解析面引进来 ——
         * 只有"会长时间跑"的两件事才解析：解压 / 测试。
         */
        bool progressExpected = operation == EngineOperation.Extract || operation == EngineOperation.Test;
        EngineProgressParser? progressParser = progressExpected ? UnRarProgressParser.TryParse : null;

        _reporter = new ArchiveProgressReporter(progressContext?.Progress);

        /*
         * 即使调用方没给 Stalled 回调，也要建 monitor：
         * 它在这里的另一个职责是给"无输出超时"提供最后活动时间。
         */
        Monitor = new EngineOutputActivityMonitor(
            progressContext?.StallThreshold ?? EngineOutputActivityMonitor.DefaultStallThreshold,
            progressContext?.Stalled);

        _outputCollector = new EngineOutputCollector(
            _outputBuilder,
            _outputLock,
            progressParser,
            _reporter,
            Monitor);

        _errorCollector = new EngineOutputCollector(
            _errorBuilder,
            _errorLock,
            progressParser,
            _reporter,
            Monitor);
    }

    /// <summary>"这次运行有多久没输出"的时钟（超时看门狗也读它）。</summary>
    internal EngineOutputActivityMonitor Monitor { get; }

    internal Task OutputPump { get; private set; } = Task.CompletedTask;

    internal Task ErrorPump { get; private set; } = Task.CompletedTask;

    /// <summary>把两条管道接上泵（进程已经启动之后调用）。</summary>
    internal void StartPumps(Process process, CancellationToken cancellationToken)
    {
        OutputPump = ProcessOutputPump.PumpAsync(
            process.StandardOutput,
            _outputCollector.Handle,
            cancellationToken);

        ErrorPump = ProcessOutputPump.PumpAsync(
            process.StandardError,
            _errorCollector.Handle,
            cancellationToken);
    }

    /// <summary>
    /// 等两路输出泵收完，**有上限**。
    ///
    /// 为什么必须有上限：泵读的是子进程的管道；万一有别的进程继承了写端，EOF 可能永远不来。
    /// 那时宁可丢一点尾巴输出，也不能把整个任务挂在这里（AGENTS.md §9：禁止无界等待）。
    /// </summary>
    internal async Task DrainPumpsAsync()
    {
        try
        {
            Task all = Task.WhenAll(OutputPump, ErrorPump);

            await Task.WhenAny(all, Task.Delay(PumpDrainTimeout)).ConfigureAwait(false);
        }
        catch
        {
            // 泵内部已经把取消 / 管道关闭都吞掉了，这里只是兜底。
        }
    }

    /// <summary>取两路缓冲区（已脱敏）。进程退出 / 取消 / 异常三条路径都从这里取。</summary>
    internal (string Output, string Error) ReadBuffers()
    {
        string output;
        string error;

        lock (_outputLock)
        {
            output = _outputBuilder.ToString();
        }

        lock (_errorLock)
        {
            error = _errorBuilder.ToString();
        }

        return (PasswordMasker.Sanitize(output), PasswordMasker.Sanitize(error));
    }

    /// <summary>收口：此后进度 / 卡住提示的上报一律丢弃（红线的落点）。</summary>
    internal void Complete()
    {
        _reporter.Complete();
        Monitor.Complete();
    }
}
