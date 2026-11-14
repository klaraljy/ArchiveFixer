using System;
using System.Collections.Generic;

namespace ArchiveFixer.Packing;

/// <summary>
/// 一次打包的**日志与进度出口**（<see cref="PackingService"/> 的协作类，职责：日志）。
///
/// <para>为什么单独一个类型：每一句日志既进结果（<see cref="PackingResult.LogLines"/>）、又走调用方
/// 传进来的那个回调。两条路必须是**同一次、同一句** —— 拆成两处写，迟早会有一边漏掉，
/// 于是结果区与主日志对不上，用户按日志排查时看不到同一件事。</para>
///
/// <para>⛔ 这里只收**已经脱敏**的文本：我们自己写的日志行根本不拼密码
/// （<see cref="PackingPasswordPolicy.SetLogLine"/>）；外部工具的输出由调用方先过
/// <see cref="PackPasswordGuard.Sanitize"/> 再交进来。</para>
/// </summary>
internal sealed class PackingRunLog
{
    private readonly List<string> _lines = new();
    private readonly Action<string>? _log;
    private readonly IProgress<PackingProgress>? _progress;

    internal PackingRunLog(Action<string>? log, IProgress<PackingProgress>? progress)
    {
        _log = log;
        _progress = progress;
    }

    /// <summary>到目前为止写下的全部日志行（与 <see cref="PackingResult.LogLines"/> 是同一份内容）。</summary>
    internal IReadOnlyList<string> Lines => _lines;

    /// <summary>写一行日志：空白行直接丢弃（引擎输出里常见），其余原样进结果与回调。</summary>
    internal void Write(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _lines.Add(line);
        _log?.Invoke(line);
    }

    /// <summary>报一个「到哪一步了」；没有进度接收端时什么也不做。</summary>
    internal void Report(
        PackingStep step,
        string text,
        int percent = PackingProgress.UnknownPercent,
        string detail = "")
    {
        _progress?.Report(new PackingProgress
        {
            Step = step,
            StepText = text,
            Percent = percent,
            Detail = detail
        });
    }

    /// <summary>
    /// 把引擎的裸进度（<see cref="PackStepProgress"/>）包成「这一步到哪了」（<see cref="PackingProgress"/>）：
    /// 上层拿到的只是"这一步到哪了"，与工具输出格式无关。
    /// <para>没有进度接收端时返回 null —— 不建包装、不给引擎多一层空回调。</para>
    /// </summary>
    internal IProgress<PackStepProgress>? WrapProgress(PackingStep step, string stepText)
    {
        IProgress<PackingProgress>? progress = _progress;

        if (progress == null)
        {
            return null;
        }

        IProgress<PackingProgress> relay = progress;

        return new Progress<PackStepProgress>(p => relay.Report(new PackingProgress
        {
            Step = step,
            StepText = stepText,
            Percent = p.Percent,
            Detail = p.Detail
        }));
    }
}
