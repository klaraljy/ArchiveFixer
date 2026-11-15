using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 「其余物目录到底在哪」的解析（生命周期：链尾结算之前；失败模型：判不出 ⇒ 保留原值，⛔ 不猜路径）。
///
/// <para>两个方向：<see cref="ResolveChainRestDirectory"/> 修「根任务记的目录可能早于归集/内层产物落点」；
/// <see cref="ResolveRestDirectoryAfterCollect"/> 修「归集把落点整棵搬走之后，其余物跟着去了哪」。
/// 两处都只认盘上事实（存在性），不按任务状态/名字推断。</para>
/// </summary>
internal static class RestDirectoryResolver
{
    /// <summary>
    /// 链尾处理其余物时，根任务记下的目录可能**早于归集/内层产物落点**：
    /// 970 批 `25252525` 根任务记的是 `...\bbb-W0-1删除\其余物`，
    /// 真正装着过程物的是链内任务 `...\bbb-W0-1删除\bbb-W0-0\其余物`。
    /// 判据只认「盘上确实存在的其余物目录」：根任务自己的优先，其次链内任务里最深的一个；
    /// 判不出就保留原值，由后面的闸门保守拦下。
    /// </summary>
    internal static string ResolveChainRestDirectory(
        ArchiveTask task,
        IReadOnlyList<ArchiveTask>? chainTasks)
    {
        if (!string.IsNullOrWhiteSpace(task.RestDirectoryPath)
            && Directory.Exists(task.RestDirectoryPath)
            && Directory.EnumerateFileSystemEntries(task.RestDirectoryPath).Any())
        {
            return task.RestDirectoryPath;
        }

        string? candidate = chainTasks?
            .Where(item => item != null
                && !string.IsNullOrWhiteSpace(item.RestDirectoryPath)
                && Directory.Exists(item.RestDirectoryPath)
                && Directory.EnumerateFileSystemEntries(item.RestDirectoryPath).Any())
            .Select(item => item.RestDirectoryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return task.RestDirectoryPath;
        }

        if (!string.Equals(candidate, task.RestDirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            task.RestDirectoryPath = candidate;
            task.ErrorMessage = string.Empty;
        }

        return candidate;
    }

    /// <summary>
    /// 归集之后其余物目录到底在哪。
    ///
    /// 归集把 <c>destDir</c> 整棵搬到 <c>归集目标\包名\</c>（**保留目录结构**），
    /// 所以"计划路径相对 destDir 的那一段"就是它在新位置下的相对路径。三种情况：
    /// ① 归集之后的位置已经存在（其余物真的被搬过去了）→ 用它；
    /// ② 它还不存在、而计划路径还在（归集只搬走了一部分）→ 用计划路径；
    /// ③ 两边都不存在（本次根本没有过程物）→ **跟着内容物走**，在归集目录里新建。
    ///
    /// ③ 是最容易写错的一档：没有过程物时 <c>destDir\其余物</c> 压根不存在，
    /// 只看"存在与否"就会退回那个**已经不存在**的旧位置，源包被扔进一个空目录（用户找不到）。
    /// </summary>
    internal static string ResolveRestDirectoryAfterCollect(
        StageCommitResult commit,
        CollectResult? collected)
    {
        string planned = commit.ProcessArtifactDirectory;

        if (string.IsNullOrWhiteSpace(planned))
        {
            return string.Empty;
        }

        if (collected is not { Success: true } || string.IsNullOrWhiteSpace(collected.DestinationPath))
        {
            return planned;
        }

        try
        {
            // destDir 从计划里的路径反推：其余物目录永远在它下面（计划就是这么算的）。
            string destDir = Path.GetDirectoryName(planned) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(destDir))
            {
                return planned;
            }

            string moved = Path.Combine(
                collected.DestinationPath,
                Path.GetRelativePath(destDir, planned));

            if (SafePathHelper.DirectoryExists(moved))
            {
                return moved;
            }

            return SafePathHelper.DirectoryExists(planned) ? planned : moved;
        }
        catch
        {
            return planned;
        }
    }
}
