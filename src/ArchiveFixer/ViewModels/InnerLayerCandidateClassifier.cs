using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 内层包候选的**纯判据**：是不是后续卷、内容自证首片、候选种子、候选目录、明细截断。
///
/// <para>为什么单独成类：这些判据只读文件名/文件头与任务落点字段，不碰"这一轮怎么跑"；
/// 一键处理主体与收集器都只转调这里，⛔ 判据只有一处。</para>
/// </summary>
internal static class InnerLayerCandidateClassifier
{
    /// <summary>明细行的条数上限（每个方向）：够看清现场，又不至于让日志爆掉。</summary>
    internal const int MaxScanDetailLines = 20;

    /// <summary>把明细行截成"最多 N 条 + 一行说明还剩多少"（绝不静默截断）。</summary>
    internal static IEnumerable<string> AppendCapped(List<string> lines, string label)
    {
        foreach (string line in lines.Take(MaxScanDetailLines))
        {
            yield return $"一键处理：    {label}：{line}";
        }

        if (lines.Count > MaxScanDetailLines)
        {
            yield return $"一键处理：    {label}：…还有 {lines.Count - MaxScanDetailLines} 个（略）";
        }
    }

    /// <summary>
    /// 取候选文件的字节数；读不到（文件刚好被删 / 权限）返回 false = 不算候选。
    ///
    /// <para>0 字节的 `*.7z.001` **不是包**（用户 2026-09-24 铁证）：7z 用错密码"半成功"时
    /// 就会写出这种桩文件，把它当内层包继续解等于连解两层空气。</para>
    /// </summary>
    internal static bool TryGetCandidateLength(string filePath, out long length)
    {
        length = 0;

        try
        {
            length = new FileInfo(filePath).Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一个任务的产物可能落在哪几个目录。
    ///
    /// 除了输出目录，还要看归集目录：开了"结果归集"时产物会被**移动**到归集目标目录，
    /// 内层包也跟着跑了 —— 只看 OutputPath 的话这种配置下永远找不到内层包（看起来就像功能没生效）。
    /// </summary>
    internal static IEnumerable<string> CandidateDirectories(ArchiveTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.OutputPath))
        {
            yield return task.OutputPath;
        }

        if (!string.IsNullOrWhiteSpace(task.CollectedPath))
        {
            yield return task.CollectedPath;
        }
    }

    /// <summary>
    /// 判断一个文件是不是<b>分卷的后续卷</b>（<c>.002+</c>／<c>.z02+</c>／<c>.r01+</c>／<c>.part2+</c>）。
    ///
    /// <para>这是续解扫描里**唯一**还按名字排除的一类，理由：一组分卷里"哪一卷是开头"
    /// 只能从名字看出来 —— 那是<b>分组</b>信息，不是格式判断。格式一律只看内容
    /// （见 <see cref="ClassifyArchiveByContentAsync"/>）。</para>
    ///
    /// <para>⛔ 别在这里加"后缀必须是已知归档后缀"那种条件：2026-09-25 真机上
    /// "名字被塞了两个字（<c>222.ra删除r</c>）就只解一层"的根因正是那样一道门。</para>
    /// </summary>
    internal static bool IsVolumeContinuationPart(string filePath)
    {
        /*
         * ⚠ 2026-10-02：实现搬到 `Helpers/FileNameHelper.IsVolumeContinuationPart`，这里只留一个转调。
         *
         * 理由不是"换个地方放"，而是**同一条规则现在有两个消费者**：续解扫描（这里）与递归的
         * 单链判定（`RecursiveExtractor.HasOnlyInformationalSiblings` —— 分卷组过去会被那一边
         * 当成"多分支"）。而 `Extraction` 不许反向引用 `ViewModels`（分层铁律），
         * 所以规则必须落在两边都能读的 `Helpers` 里 —— ⛔ 不许在那边再写第二份。
         */
        return FileNameHelper.IsVolumeContinuationPart(filePath);
    }

    /// <summary>
    /// 内容自证“我是某一组的第一片”（7z `VolumeFirstPiece`：起始头自述整包字节/片数）。
    /// 用来压过**名字**的“像后续卷”判断（真机 19191919：`.0!601` 是首片，却被名字尾巴 601 跳过）。
    /// 判不出/读不到 ⇒ false（⛔ 不猜）。
    /// </summary>
    internal static bool ContentProvesFirstVolumePiece(string filePath)
    {
        try
        {
            return ArchiveFixer.Detection.FileNature.Inspect(filePath).BodyKind
                == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 名字/内容像“某一组的片”的候选种子（只用于第二大步候选池，⛔ 不据此搬/删/改名）：
    /// 卷标记容忍档命中、卷号 ≥ 2、或内容自证首片。
    /// </summary>
    internal static bool IsVolumeCandidateSeed(string filePath)
    {
        try
        {
            if (FileNameHelper.IsVolumeContinuationPart(filePath))
            {
                return true;
            }

            int? index = LegacyVolumeNames.TryGetIndex(
                Path.GetFileName(filePath));

            if (index is >= 2)
            {
                return true;
            }

            return ContentProvesFirstVolumePiece(filePath);
        }
        catch
        {
            return false;
        }
    }
}

