using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models;

/// <summary>
/// 任务的**源文件快照**（不变量 11：源文件变化后不得继续使用旧识别结果）—— 任务侧的持有者与入口。
///
/// <para>从 <see cref="ArchiveTask"/> 抽出的协作类（「结果」那一块的基准）：要回答的只有一个问题 ——
/// "识别那一刻记下来的大小 / 修改时间，在真正调引擎之前还对得上吗"。⛔ 它不碰识别结果、
/// 不拦任务（拦不拦由协调器按比对结论决定），只负责记住基准、按同一顺序重算路径、把比对交回。</para>
///
/// <para><b>失败模型</b>：<c>null</c> 快照 = 没有基准（老任务 / 从没扫描过）⇒ <see cref="Compare"/>
/// 返回 <c>null</c>，调用方补拍一次，⛔ 绝不当成"源文件变了"。stat 本身的异常由
/// <see cref="SourceFileSnapshot"/> 那一层吞（那一层在 Storage）。</para>
///
/// <para>生命周期：识别完成之后拍第一份；开工前发现没有基准时补拍；此后只读比对，不重拍（重拍=换基准）。
/// 与任务同寿命（任务对象是复用的，下一轮识别会重新拍）。</para>
/// </summary>
internal sealed class TaskSourceSnapshot
{
    /// <summary>识别那一刻的基准；<c>null</c> = 没有基准。</summary>
    internal SourceFileSnapshot? Snapshot { get; set; }

    /// <summary>上面那份快照是什么时候拍的（<b>本地时间</b>，给人看 / 写日志用；快照内部按 UTC 存）。</summary>
    internal DateTime? CapturedAtLocal { get; set; }

    /// <summary>有没有可用的基准（没有就该在开工前补拍，而不是判"变了"）。</summary>
    internal bool HasSnapshot => Snapshot != null;

    /// <summary>
    /// 本任务要记进快照的那一组路径：**主文件在前，分卷整组在后**（顺序即比对顺序）。
    ///
    /// <para>顺序稳定是刚需：比对是**按位**做的，这样"改名了 / 源包被搬进其余物了"
    /// 这种"路径变了、文件没变"的情形不会被误判成"源文件被换了" ——
    /// 路径不是不变量 11 要保护的东西，大小与修改时间才是。</para>
    ///
    /// <para>⛔ <b>同一份文件只许出现一次</b>（2026-10-10 真机 EEEE）："1 卷组"那一档里
    /// <c>VolumePaths</c> 装的就是这一单自己的文件（归组服务在识别**之后**才写进去），
    /// 于是"按位比对"会看到**第 2 条是快照里没有的新文件** ⇒ 报「源文件已变化」把这一单拦下、
    /// 一次引擎都不调。运行期读数（debug-mcp 停在 <c>ExtractionCoordinator.cs:498</c>）：
    /// <c>CurrentPath == VolumePaths[0]</c>、<c>VolumePaths.Count == 1</c>、
    /// <c>SourceSnapshot.Files.Count == 1</c>、<c>comparison.Changes[0].IsVolume == true</c>、
    /// <c>PreviousLength == 0</c>、<c>PreviousLastWriteTimeUtc.Ticks == 0</c>（<c>before == null</c> 那一支）。
    /// 守门用例 <c>SourceSnapshotDedupTests</c>（2 条，含"真的多出另一个文件"的哨兵）。</para>
    /// </summary>
    internal static List<string> GetPaths(string currentPath, IReadOnlyList<string> volumePaths)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(currentPath) && seen.Add(currentPath))
        {
            paths.Add(currentPath);
        }

        foreach (string volume in volumePaths)
        {
            if (!string.IsNullOrWhiteSpace(volume) && seen.Add(volume))
            {
                paths.Add(volume);
            }
        }

        return paths;
    }

    /// <summary>
    /// 现在就拍一份快照并挂在任务上（<b>只 stat，不读内容</b>），返回那一份并记下拍摄时刻。
    /// </summary>
    internal SourceFileSnapshot Capture(IReadOnlyList<string> paths)
    {
        SourceFileSnapshot snapshot = SourceFileSnapshot.Capture(paths);

        Snapshot = snapshot;
        CapturedAtLocal = snapshot.CapturedAt.ToLocalTime();

        return snapshot;
    }

    /// <summary>
    /// 与当前磁盘状况比一遍；<b>没有基准时返回 <c>null</c></b>（调用方补拍，不许当成"变了"）。
    /// </summary>
    internal SourceChangeResult? Compare(IReadOnlyList<string> paths) =>
        Snapshot?.Compare(paths);
}
