using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 定稿搬运的收尾工具（第 20 轮前奏：从 <c>ExtractionCoordinator.Recursion.cs</c> 搬成独立静态类）。
    /// 回滚与"没搬成的明细"都不碰判据 —— 判据在 <see cref="ResultFinalizer"/> 与调用方。
    /// </summary>
    internal static class FinalizeMoveSupport
    {
        /// <summary>定稿失败明细最多列几条（前 5 个足够定性；多出来的折成"还有 K 项"）。</summary>
        internal const int MaxFinalizeContentNameLines = 5;

    /// <summary>
    /// 定稿**没能搬运**的那几条明细（<c>文件名（原因原文）</c>），最多前
    /// <see cref="MaxFinalizeContentNameLines"/> 条，多出来的折成"还有 K 项没列出来"。
    ///
    /// <para>为什么是 5 条：与「本次内容物 —— 名字、名字…」同一个理由（前 5 个足够定性，
    /// 几十个同名文件全列出来只会把日志与①页那一格撑爆）。</para>
    ///
    /// <para>⛔ 这里一个字都不改判据、也不重算原因：明细就是 <c>failures</c> 里已经收好的那一份
    /// （来源：`无法创建目标目录` / 两阶段覆盖的失败原因 / `File.Move` / `Directory.Move` 的异常原文）。</para>
    /// </summary>
    internal static string DescribeFinalizeMoveFailures(IReadOnlyList<string> failures)
    {
        List<string> shown = failures.Take(MaxFinalizeContentNameLines).ToList();
        string text = string.Join("、", shown);

        if (failures.Count <= shown.Count)
        {
            return text;
        }

        return text + string.Format(
            CultureInfo.CurrentCulture,
            StatusText.FinalizeMoveFailureHiddenFormat,
            failures.Count - shown.Count);
    }

    /// <summary>
    /// 定稿的回滚：把已经搬过去的条目按**相反顺序**搬回暂存区原路径。
    ///
    /// 为什么必须回滚（契约 §6 第 2 条"定稿前 destDir 不得出现半成品"）：
    /// 用户按下「取消当前」时，搬运可能已经做了一半 —— 只把最终目录里那一半留下，
    /// 用户看到的就是一个"解压到一半"的目录，正是这次改造要根治的观感问题。
    /// 回滚失败（文件被别的程序锁住等）只吞掉：那是更小的问题，而且日志里已经有取消记录。
    ///
    /// 条目可能是**整个目录**（判定表 2/3/4 会整棵搬），所以这里也要按类型分开搬回去。
    /// </summary>
internal static void RollBackFinalLayout(
        List<(string From, string To)> moved,
        string destinationDirectory,
        bool destinationExisted)
    {
        for (int i = moved.Count - 1; i >= 0; i--)
        {
            try
            {
                SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(moved[i].From));

                if (Directory.Exists(moved[i].To) && !File.Exists(moved[i].To))
                {
                    Directory.Move(moved[i].To, moved[i].From);
                }
                else
                {
                    File.Move(moved[i].To, moved[i].From);
                }
            }
            catch
            {
                // 回滚不掉的单个条目不影响"已经尽量退回"这个结论。
            }
        }

        if (destinationExisted)
        {
            // 目录本来就在（续解内层包时它装着上一轮的内容物），绝不能删。
            return;
        }

        TryDeleteEmptyDirectoryTree(destinationDirectory);
    }


    /// <summary>由深到浅删掉空目录（只删空的；非空一律不碰）。定稿回滚后收拾自己造出来的目录壳。</summary>
    internal static void TryDeleteEmptyDirectoryTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            string[] directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);

            foreach (string directory in directories.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.GetFileSystemEntries(directory).Length == 0)
                    {
                        Directory.Delete(directory, recursive: false);
                    }
                }
                catch
                {
                    // 删不掉就留着，不影响结论。
                }
            }

            if (Directory.GetFileSystemEntries(root).Length == 0)
            {
                Directory.Delete(root, recursive: false);
            }
        }
        catch
        {
            // 见方法注释：收拾目录壳是尽力而为。
        }
    }
    }
}
