using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using System;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 工作区相关路径的**唯一判据出口**：规范化、包含关系、相对路径、容错读盘。
///
/// <para>发布、就地替换、清理三处都必须回答"这个路径是不是在那棵树 / 那一层之下"，
/// 而每一条都直接决定"删不删、往哪搬"。判据只留这一份 ——
/// ⛔ 别处不许再写第二套字符串比较（两套判据漂移的代价是不可逆的）。</para>
/// </summary>
internal static class WorkspacePathRules
{
    /// <summary>candidate 与 parent 相同、或位于 parent 之下时返回 true（Windows 下忽略大小写）。</summary>
    internal static bool IsSameOrChildPath(string? candidate, string? parent)
    {
        string fullCandidate = NormalizeForCompare(candidate);
        string fullParent = NormalizeForCompare(parent);

        if (fullCandidate.Length == 0 || fullParent.Length == 0)
        {
            return false;
        }

        if (string.Equals(fullCandidate, fullParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>规范化后判断 candidate 是否位于 root 之下（Windows 下忽略大小写）。</summary>
    internal static bool IsInsideRoot(string? rootDirectory, string? candidate)
    {
        string fullCandidate = NormalizeForCompare(candidate);
        string fullRoot = NormalizeForCompare(rootDirectory);

        if (fullCandidate.Length == 0 || fullRoot.Length == 0)
        {
            return false;
        }

        // 与 root 相同也算"不在其下"：那等于删掉整个根目录（别的任务的工作区可能就在旁边）。
        if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 这个路径是不是落在**递归工作区**里（<c>&lt;目标目录&gt;\.ArchiveFixer.work\…</c> 的任一层子目录）。
    ///
    /// <para>递归路「逐层回收」只允许删这一棵树里的东西：那一层的过程物是我们自己刚解出来的内层包
    /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work\recursive\&lt;id&gt;\layer-NNN\output\…</c>）；
    /// **用户拖进来的源包永远不在它下面**（它在用户的源目录里）⇒ 这条判据就是"最外层源包留到链尾"
    /// 那道保险的落点。判不出 / 拿不到路径 ⇒ 返回 false ⇒ 上层什么都不做（删除不可恢复）。</para>
    ///
    /// <para>标记的唯一出口是 <see cref="VolumeContentInference.WorkDirectoryName"/>
    /// （AGENTS.md §9.5：⛔ 不另写一个字面量）。⛔ 与
    /// <see cref="ProcessArtifactLayout.IsInsideDeletableProcessFolders"/> 的区别：那一档只问
    /// "路径里有没有这几个字"（给导出起始目录用，容忍半段），本档要求它是**整整一段目录名**。</para>
    /// </summary>
    internal static bool IsInsideRecursionWorkspace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string marker = Path.DirectorySeparatorChar
            + VolumeContentInference.WorkDirectoryName
            + Path.DirectorySeparatorChar;

        return SafePathHelper.GetFullPathSafe(path)
            .Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><paramref name="childPath"/> 在 <paramref name="parentDirectory"/> 之下的相对路径。</summary>
    internal static string RelativeUnder(string? parentDirectory, string? childPath)
    {
        string fullParent = NormalizeForCompare(parentDirectory);
        string fullChild = NormalizeForCompare(childPath);

        if (fullParent.Length == 0
            || fullChild.Length == 0
            || !fullChild.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return fullChild[(fullParent.Length + 1)..];
    }

    /// <summary>
    /// 规范化比较用的完整路径：去掉末尾的分隔符
    /// （否则 <c>X\</c> 与 <c>X</c> 会被当成两个目录）。
    /// </summary>
    internal static string NormalizeForCompare(string? path)
    {
        return SafePathHelper.GetFullPathSafe(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>目录是否真的存在；读盘异常一律按"不存在"回答，不把异常抛给发布流程。</summary>
    internal static bool SafeDirectoryExists(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读文件属性；读不到属性时返回 false（调用方按"不可处理"继续）。</summary>
    internal static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch
        {
            attributes = default;
            return false;
        }
    }
}
