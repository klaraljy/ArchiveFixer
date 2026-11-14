using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models;

/// <summary>
/// **这一行"你的文件"经历过的变换**（改名 / 续解每一层），一步一步攒起来，**全是程序自己的记录**：
/// 改名那一步 = 「最初导入的名字 → 最新已知的名字」（两个名字都是任务账上的事实字段），
/// 续解那几步 = 递归每层开头报上来的那一帧原文（<c>第 N 层：&lt;那一层的包&gt;</c>）。
///
/// <para>来由（用户 2026-10-07）：「我们最后想看到 <c>111.rar</c> 通过解压缩两层变换到 <c>111.zip</c>，
/// 用户如果看到你什么都不动就会以为你什么都没改变」；同一轮他还点名 ⛔ 不许复述或猜测这条链
/// （「谁和你说第一层得到 <c>111.part1.rar</c>，你不要给我瞎猜，看程序怎么弄」）。
/// ⇒ 本类只做拼接，⛔ 一个字都不编。</para>
///
/// <para><b>失败模型</b>：空 / 纯空白的一步直接忽略；重复的连续一步只留一条；没有记录时整条链是空串
/// （⛔ 不写"无"）—— 没有异常路径。</para>
///
/// <para>生命周期：随任务创建；唯一写入出口是 <see cref="Append"/>（调用方必须持有"程序自己那条记录"），
/// 没有清空出口（收尾之后这条链就是这一单的历史）。</para>
/// </summary>
internal sealed class TransformationChain
{
    private readonly List<string> _steps = new();

    /// <summary>
    /// 记下一步变换；空 / 与上一步重复 ⇒ 不记。
    /// </summary>
    /// <returns>真的记下了 ⇒ <c>true</c>（调用方据此发状态格那条通知）。</returns>
    internal bool Append(string? step)
    {
        if (string.IsNullOrWhiteSpace(step))
        {
            return false;
        }

        string trimmed = step.Trim();

        if (_steps.Count > 0
            && string.Equals(_steps[^1], trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        _steps.Add(trimmed);
        return true;
    }

    /// <summary>
    /// 变换链给用户看的那一行（空 = 这一步什么都没发生，不写"无"）。
    /// 例：<c>111.z0删除3 → 111.z03；第 1 层：111.rar → 第 2 层：111.zip；由「111.z03」那一单解开整组</c>。
    /// </summary>
    internal string Describe(string settledWithGroupUnitName)
    {
        var parts = new List<string>();

        // ① 改名 / 续解每一步：**程序自己记下来的**（唯一写入出口见 Append）。
        if (_steps.Count > 0)
        {
            parts.Add(string.Join(" → ", _steps));
        }

        // ② 这一片被整组接手（收场那一刻记下的事实）。
        if (settledWithGroupUnitName.Length > 0)
        {
            parts.Add(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SettledWithGroupChainFormat,
                settledWithGroupUnitName));
        }

        return parts.Count == 0 ? string.Empty : string.Join("；", parts);
    }
}
