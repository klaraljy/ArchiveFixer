using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 定稿结算：把"已经落位的结果"换算成下游要的三件事实 ——
/// 内容物实际所在的那一层（续解产物按它落位）、归集之后的内容目录、下一轮的输入账本。
///
/// <para>这里只读盘面，不搬、不删；判据仍然只有 <see cref="PlacementMoveExecutor"/> 那一份。
/// <c>StageCommitResult</c> 的装配留在执行器（它才是唯一持有计数器与失败明细的地方），
/// 本类只回答"落位之后盘上是哪一层 / 哪些文件属于这一轮"。</para>
/// </summary>
internal sealed class PlacementCommitSettlement
{
    private readonly PathService _paths;

    internal PlacementCommitSettlement(PathService paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// 内容物**实际所在的那一层目录**（第 35 条）。
    ///
    /// <para>定稿计划的 <c>DestinationDirectory</c> 是"落点目录"，但内容物往往被套了一层
    /// （判定表：多文件套一层 / 包里自带一个同名文件夹）—— 真机现场就是这样：
    /// 共用输出根是 <c>BBB\111</c>，而定稿把那**一个**文件夹 <c>2222</c> 搬了进去，
    /// 于是内容物真正所在的那一层是 <c>BBB\111\2222</c>。</para>
    ///
    /// <para>判据：落点目录里**除了「其余物」和工作区自己那棵树之外**只剩一个条目、而且它是目录
    /// → 那一层就是内容物层；否则就是落点目录本身（内容物是散文件，或本来就有多项）。
    /// 续解产物按它落位，才能做到"一个源包 = 一个目录"（用户第 35 条原话：
    /// "你应该是要将 `BBB\111\222` 文件夹放在 `BBB\111\2222` 这个里面"）。</para>
    ///
    /// <para>⛔ <b>必须排除工作区那棵树</b>（用户 2026-09-30）：工作区默认就建在目标目录里面
    /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），不排除的话它会被当成"唯一的那一个子目录"，
    /// 于是"内容物层"被解析成工作区 —— 续解产物会直接落进工作区，然后被收尾清理一起删掉。</para>
    /// </summary>
    internal string ResolveContentLayerDirectory(string destinationDirectory, string processArtifactDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return string.Empty;
        }

        try
        {
            if (!Directory.Exists(destinationDirectory))
            {
                return destinationDirectory;
            }

            string workRoot = _paths.WorkDirectory;

            List<string> entries = Directory
                .GetFileSystemEntries(destinationDirectory)
                .Where(path => !ProcessArtifactLayout.IsArtifactDirectoryName(path))
                .Where(path => !WorkspaceTree.ShouldSkipEntry(path, workRoot))
                .ToList();

            if (entries.Count == 1 && Directory.Exists(entries[0]))
            {
                return entries[0];
            }
        }
        catch
        {
            // 读不了目录（权限 / 刚好被删）：按落点目录回落，绝不因此让定稿结论出问题。
        }

        return destinationDirectory;
    }

    /// <summary>
    /// 归集（把内容目录整棵搬走）之后内容物实际在哪儿：相对**输出目录**的那一段原样保留。
    /// 判不出就退回计划里的那个路径（归集失败/没归集时计划仍然成立）。
    /// </summary>
    internal static string ResolveContentDirectoryAfterCollect(
        StageCommitResult commit,
        CollectResult? collected)
    {
        string planned = commit.ContentDirectory;

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
            // 内容目录相对**输出目录**的那一段在归集之后原样保留（归集是整棵搬走）。
            string outputDirectory = commit.ContentDirectory;

            if (!string.IsNullOrWhiteSpace(commit.ProcessArtifactDirectory))
            {
                outputDirectory = Path.GetDirectoryName(commit.ProcessArtifactDirectory) ?? outputDirectory;
            }

            string moved = Path.Combine(
                collected.DestinationPath,
                Path.GetRelativePath(outputDirectory, planned));

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

    /// <summary>
    /// 轮次输入账本：只记**定稿计划里属于内容物**的落地文件。
    /// 下一轮不再递归扫输出目录；目录形式的移动在这里展开一次，仍然只碰本次真正搬过的目标。
    /// </summary>
    internal static List<string> CollectProducedFiles(FinalizePlan plan, IReadOnlyList<(string From, string To)> moved)
    {
        var producedFiles = new List<string>();

        foreach ((string From, string To) in moved)
        {
            if (plan.ProcessArtifactSources.Contains(From))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(To))
                {
                    producedFiles.AddRange(Directory.EnumerateFiles(
                        To,
                        "*",
                        SearchOption.AllDirectories));
                }
                else if (File.Exists(To))
                {
                    producedFiles.Add(To);
                }
            }
            catch
            {
                // 读不了就只留目录级事实；下一轮兼容扫描仍能兜底。
            }
        }

        return producedFiles;
    }
}
