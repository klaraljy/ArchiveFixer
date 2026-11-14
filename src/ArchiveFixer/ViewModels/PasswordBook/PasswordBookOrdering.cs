using System;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels.PasswordBook;

/// <summary>密码本路径清单的合并/去重规则。</summary>
internal static class PasswordBookOrdering
{
    internal static List<string> Order(
        IReadOnlyList<string> remembered,
        IReadOnlyList<string> fromSettings)
    {
        var result = new List<string>();

        if (fromSettings == null || fromSettings.Count == 0)
        {
            foreach (string path in remembered ?? (IReadOnlyList<string>)Array.Empty<string>())
            {
                AddIfNew(result, path);
            }

            return result;
        }

        foreach (string path in remembered ?? (IReadOnlyList<string>)Array.Empty<string>())
        {
            if (ContainsBook(fromSettings, path))
            {
                AddIfNew(result, path);
            }
        }

        foreach (string path in fromSettings)
        {
            AddIfNew(result, path);
        }

        return result;
    }

    private static void AddIfNew(List<string> target, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        foreach (string existing in target)
        {
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        target.Add(path);
    }

    private static bool ContainsBook(IReadOnlyList<string> paths, string? path)
    {
        if (path == null)
        {
            return false;
        }

        foreach (string existing in paths)
        {
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
