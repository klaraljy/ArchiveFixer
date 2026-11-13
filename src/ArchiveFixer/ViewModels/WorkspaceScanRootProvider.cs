using System;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels
{
    /// <summary>残留扫描要用的工作区根清单；当前根排最前、去重、不读历史。</summary>
    internal static class WorkspaceScanRootProvider
    {
        internal static IReadOnlyList<string> Resolve(
            string currentRoot,
            IEnumerable<string> sessionRoots)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string? root)
            {
                if (!string.IsNullOrWhiteSpace(root) && seen.Add(root.Trim()))
                {
                    roots.Add(root.Trim());
                }
            }

            TryAdd(currentRoot);

            foreach (string remembered in sessionRoots)
            {
                TryAdd(remembered);
            }

            return roots;
        }
    }
}
