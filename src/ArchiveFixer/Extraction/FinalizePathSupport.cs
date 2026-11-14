using ArchiveFixer.Helpers;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 定稿计划里的路径与重名支持：目标名清洗、本批占位去重、<c>From</c> 路径拼接、
/// "是不是同一个路径之下"。纯字符串运算，⛔ 不读盘、不建目录。
/// </summary>
internal static class FinalizePathSupport
{
    /// <summary>重名不覆盖：撞了就加 <c>(1)</c>、<c>(2)</c>…（加在扩展名之前）。</summary>
    public static string Unique(string target, HashSet<string> used)
    {
        if (used.Add(target))
        {
            return target;
        }

        string directory = FileNameHelper.GetDirectoryName(target);
        string name = FileNameHelper.GetFileName(target);
        string extension = Path.GetExtension(name);
        string stem = extension.Length > 0 && extension.Length < name.Length
            ? name[..^extension.Length]
            : name;

        for (int i = 1; i <= 100000; i++)
        {
            string candidate = SafeCombine(directory, $"{stem}({i}){extension}");

            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        return target;
    }

    public static string SafeName(string? name)
    {
        return FileNameHelper.SanitizeFileName(name);
    }

    public static string SafeCombine(string directory, string name)
    {
        return SafePathHelper.Combine(directory, name);
    }

    /// <summary><c>From</c> 路径：给了暂存根就拼成可执行的绝对路径，否则就是相对路径。</summary>
    public static string FromPath(string stagingRoot, StagedTreeNode node)
    {
        return stagingRoot.Length == 0
            ? node.RelativePath
            : SafeCombine(stagingRoot, node.RelativePath);
    }

    /// <summary>逐段清洗相对路径（Windows 保留名、结尾空格/点、非法字符），保留目录层级。</summary>
    public static string SanitizeRelativePath(string relativePath)
    {
        string[] segments = relativePath.Split('\\');

        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = SafeName(segments[i]);
        }

        return string.Join("\\", segments);
    }

    /// <summary>
    /// 把 <c>其余物\xxx</c> 的开头那段摘掉：已经在其余物目录里的东西不要再套一层。
    /// 旧名 <c>过程物</c> 同样认（决策 D-8：老版本留下的目录还在用户的盘上）。
    /// </summary>
    public static string StripLeadingArtifactSegment(string relativePath)
    {
        int separator = relativePath.IndexOf('\\');

        if (separator <= 0)
        {
            return string.Empty;
        }

        string first = relativePath[..separator];

        return ProcessArtifactLayout.IsArtifactDirectoryName(first)
            ? relativePath[(separator + 1)..]
            : string.Empty;
    }

    /// <summary>candidate 与 parent 相同、或位于 parent 之下（Windows 下忽略大小写）。</summary>
    public static bool IsSameOrChildPath(string candidate, string parent)
    {
        string a = candidate.TrimEnd('\\', '/');
        string b = parent.TrimEnd('\\', '/');

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)
               || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase);
    }
}
