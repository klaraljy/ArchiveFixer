using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 一次 7-Zip 运行的**输出读取**这一块：两路缓冲区、两路输出泵、有上限的收尾等待。
///
/// <para>为什么用 <see cref="ProcessOutputPump"/> 自己切流、而不是
/// <c>BeginOutputReadLine</c>：7-Zip 的进度是 <c>\r</c> 分隔的，UnRAR 的百分比是同一行内
/// 用退格回写的 —— 按行读会把"实时进度"退化成"解完才有"（见 <see cref="ProcessOutputPump"/>
/// 的类注释）。本类只负责把泵的产物收进缓冲区，不判退出码、不判错误类型。</para>
///
/// <para>生命周期：<see cref="Complete"/> 之后进度与卡住提示的出口一律关闭，
/// 所以进程退出 / 取消 / 超时之后**不可能**再有迟到回调 —— 运行器在返回之前必定调它一次。</para>
/// </summary>
internal sealed class SevenZipOutputCapture
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

    internal SevenZipOutputCapture(
        IEnumerable<string>? arguments,
        EngineProgressContext? progressContext)
    {
        /*
         * 进度解析只在"这次真的打开了进度开关"时才启用。
         *
         * 为什么要这个条件：RunSevenZipAsync 是通用的（列目录 / 测试 / 解压都走它），
         * 而 l -slt 的输出是要被逐行解析的。只有带 -bsp1 的调用才可能产出进度行，
         * 这样列目录那条路径上"7-Zip 文本"的解析面一点都没变宽。
         */
        EngineProgressParser? progressParser = SevenZipCommandLine.HasProgressSwitch(arguments)
            ? SevenZipProgressParser.TryParse
            : null;

        _reporter = new ArchiveProgressReporter(progressContext?.Progress);

        /*
         * 卡住监视器：始终建（即使没有 WARN 回调）—— 它同时是下面 30 分钟超时的“最后活动”时钟。
         * ⛔ 它**没有杀进程的能力**，只记录活动；真正的杀进程只在超时/取消分支里做。
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
