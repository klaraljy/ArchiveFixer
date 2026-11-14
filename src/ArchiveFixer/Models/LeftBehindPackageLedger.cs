using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Models;

/// <summary>
/// **成品目录里还留着、内容还没解出来的内层包**（只记文件名，隐私红线 §8）的账本。
///
/// <para>从 <see cref="ArchiveTask"/> 抽出的协作类（「结果」那一块的报告）：唯一写入点是
/// <c>RestHandlingStep.ReportLeftBehindInnerPackages</c>（链尾那一站**无条件**调）；
/// 唯一读取点是批末诊断与「一键处理完成」那一行 —— 用户 2026-11-10 真机口径：
/// 「汇总里没有」是缺口，这种"内容还在里面、一个字节都没解出来"的事实必须被汇总说出来。</para>
///
/// <para><b>失败模型</b>：空 / 纯空白的名字直接忽略；只留文件名（不落全路径，隐私红线）；
/// 同名去重（大小写不敏感）；⛔ 不抛、不判"是不是真内层包"（那是写入方的判据）。</para>
///
/// <para>生命周期：每轮开工由写入方（链尾那站）重新报一遍；<see cref="Names"/> 是**同一个列表实例**
/// （拆分前调用方拿着的就是它，批末诊断直接枚举），⛔ 这里不复制、不清空。</para>
/// </summary>
internal sealed class LeftBehindPackageLedger
{
    /// <summary>留着的内层包文件名（去重后，保持首次出现的顺序）。</summary>
    internal List<string> Names { get; } = new();

    /// <summary>记下这一轮"成品目录里还留着的内层包"（去重、只记文件名）。</summary>
    internal void Record(IEnumerable<string>? names)
    {
        foreach (string name in names ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string fileName = Path.GetFileName(name);

            if (!string.IsNullOrWhiteSpace(fileName)
                && !Names.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                Names.Add(fileName);
            }
        }
    }
}
