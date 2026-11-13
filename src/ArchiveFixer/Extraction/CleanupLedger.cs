using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>每条链一份的清理账本；只记事实，不执行删除。</summary>
    public sealed class CleanupLedger
    {
        private readonly List<CleanupItem> _items = new();

        public IReadOnlyList<CleanupItem> Items => _items;

        public void Clear() => _items.Clear();

        public void Add(CleanupItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
            {
                return;
            }

            _items.Add(item);
        }

        public bool Has(CleanupItemKind kind)
        {
            foreach (CleanupItem item in _items)
            {
                if (item.Kind == kind)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

