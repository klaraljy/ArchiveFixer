using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **第一大步文件账本**（共用骨架的唯一收口）。
    ///
    /// <para>它回答"这一批里有哪些文件过了第一大步闸门、各自是什么"，供第二大步的
    /// 边界 resolver 按基名/族/大小收片。⛔ 它不做任何分组结论、不碰盘。</para>
    /// </summary>
    public sealed class FirstStepFileLedger
    {
        private readonly List<FirstStepFileLedgerEntry> _entries = new();
        private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<FirstStepFileLedgerEntry> Entries => _entries;

        public int Count => _entries.Count;

        /// <summary>批首清空（与其它批内账本同一处调用）。</summary>
        public void Clear()
        {
            _entries.Clear();
            _indexByPath.Clear();
        }

        /// <summary>
        /// 登记一项；同一路径重复登记时**保留后到的那一项**（内容卡可能因改名/补片而变化），
        /// 但绝不因为"路径相同"就把两份不同文件合成一份。
        /// </summary>
        public FirstStepFileLedgerEntry AddOrReplace(FirstStepFileLedgerEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                return entry;
            }

            string fullPath;

            try
            {
                fullPath = System.IO.Path.GetFullPath(entry.Path);
            }
            catch
            {
                fullPath = entry.Path;
            }

            if (_indexByPath.TryGetValue(fullPath, out int index))
            {
                /*
                 * 后到项可能是“补录兄弟片”产生的低信息卡（没有名字槽/租约/角色）。
                 * 直接替换会让账本事实倒退；合并缺口，保留已确认事实。
                 */
                FirstStepFileLedgerEntry merged = entry.PreserveFactsFrom(_entries[index]);
                _entries[index] = merged;
                return merged;
            }

            _indexByPath[fullPath] = _entries.Count;
            _entries.Add(entry);
            return entry;
        }

        /// <summary>
        /// <summary>
        /// 把这一份**物理文件**在账本里的所有名字都标成「已消费源包」（路径 + 同一物理身份的所有改名副本）。
        ///
        /// <para>为什么需要：组里记的是**解开时那一刻**的路径，而改名之后账本里那一条已经换了名字
        /// （真机 125 批 926000008：`aaa-W3-1.m删除p4` 改名成 `aaa-W3-1.rar`，按旧路径标不中）。
        /// 判据只认物理身份（卷序列号 + 文件索引），⛔ 不看后缀/基名/内容格式。</para>
        /// </summary>
        public int MarkConsumedByIdentity(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            int marked = MarkConsumed(path) ? 1 : 0;

            if (!FileIdentity.TryGetIdentity(path, out ulong volume, out ulong index))
            {
                return marked;
            }

            for (int position = 0; position < _entries.Count; position++)
            {
                FirstStepFileLedgerEntry entry = _entries[position];

                if (entry.IsConsumedSource
                    || !entry.HasIdentity
                    || entry.VolumeSerial != volume
                    || entry.FileIndex != index)
                {
                    continue;
                }

                _entries[position] = entry.WithConsumedSource();
                marked++;
            }

            return marked;
        }
        /// 把某个路径标成「**已解成功并定稿的源包**」：从此它不再进第二大步分卷候选。
        ///
        /// <para>判据只认**路径事实**（这个路径的任务已经解成功），⛔ 与后缀、基名、内容格式无关
        /// —— 真机 2026-11-10 那次就是外层包已定稿，却仍被同基名的 7z 分卷组当中间片收进去。</para>
        ///
        /// <para>路径不在账本里 ⇒ 什么都不做（判不出就不动）。</para>
        /// </summary>
        public bool MarkConsumed(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fullPath;

            try
            {
                fullPath = System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            if (!_indexByPath.TryGetValue(fullPath, out int index)
                || _entries[index].IsConsumedSource)
            {
                return false;
            }

            _entries[index] = _entries[index].WithConsumedSource();
            return true;
        }

        /// <summary>
        /// **第二大步候选池的唯一入口**：同基名里"过了闸门、不是已消费源包、内容也不是普通媒体"的那些项。
        ///
        /// <para>⛔ 这里只做**否定证据**的排除，不替族 resolver 判"这一份是不是分卷"：
        /// 7z 中间片/裸切续卷名字丢了也能靠族算法定序；如果在这里要求"必须有卷号/名字槽"，
        /// 合法但事实稀疏的片会被挡在组外（实测 2026-11-11 的 9 条骨架用例因此变红）。
        /// 普通媒体有 <see cref="FileContentKind.NormalMedia"/> 这条内容事实，足以把 336.png
        /// 这类内容物挡在候选池外。</para>
        ///
        /// <para>⛔ 族 resolver / 族仲裁只许从这里拿候选：老写法各自用
        /// <see cref="SameBase"/> 再叠自己的过滤条件，于是"外层包已解成功"的账本项
        /// 被 7z 那条 `Format == Unknown` 放进了组（真机 2026-11-10 的 SizeAnomaly 根因）。</para>
        /// </summary>
        public IReadOnlyList<FirstStepFileLedgerEntry> SecondStepCandidates(string? baseName)
        {
            return SameBase(baseName)
                .Where(IsSecondStepCandidate)
                .ToList();
        }

        /// <summary>
        /// 全账本的第二大步候选（扁平）：判据与 <see cref="SecondStepCandidates(string?)"/> 完全相同，
        /// 只服务"标准名快通道要对所有候选片一次定组"这种调用方 —— ⛔ 不许各自再写一遍过滤。
        /// </summary>
        public IReadOnlyList<FirstStepFileLedgerEntry> AllSecondStepCandidates()
        {
            return _entries
                .Where(IsSecondStepCandidate)
                .ToList();
        }

        private static bool IsSecondStepCandidate(FirstStepFileLedgerEntry entry)
        {
            /*
             * ⛔ 不能在这里再要求 `HasSecondStepEvidence`：族 resolver 需要“改名到没有卷标记”的
             * 中间片也能进候选池，再由试开/顺序证据定序（`VolumeBoundarySkeletonTests`
             * 的“中间片名字丢了_硬链接试开定序”就是这一档）。候选池只排除
             * 已消费、0 字节与普通媒体这三条否定事实，其余交给族算法判。
             */
            if (!entry.IsGatePassed
                || entry.IsConsumedSource
                || entry.Size <= 0
                || entry.ContentKind == FileContentKind.NormalMedia
                || entry.ContentClass == FileContentClass.KnownContent)
            {
                return false;
            }

            /*
             * ZIP 家族的内容物只有深探测才能认出来（docx/epub/apk/jar）。
             * 候选数很少，精确一次；⛔ 不在 HasSecondStepEvidence/FileNature 里给每个文件开 ZIP。
             */
            if (entry.Format == VolumeContentFormat.Zip
                && ContentAdmission.Probe(
                    entry.Path,
                    ContentAdmissionScope.VolumeCandidate,
                    deep: true).Class == FileContentClass.KnownContent)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 这个基名在**第二大步里的代表层**：只在候选片里取（<see cref="SecondStepCandidates"/>），
        /// 全部候选都没有层时才退回同基名的任意项。
        ///
        /// <para>⛔ 为什么不能直接用 <see cref="SameBase"/> 的第一条：外层包与它解出的分卷片
        /// 常常同基名（`X.tar` 与 `X.7z.001` 都会被切成 `X`），外层包那一条的 `SourceLayer`
        /// 是**源目录**，不是片所在的成品目录。用错层去拼"快通道键 / 原始源层"会让两把键永远对不上
        /// （真机 2026-11-10：快通道明明 Ready，仍然落回 resolver 整组试开 21 GB）。</para>
        /// </summary>
        public string CandidateSourceLayer(string? baseName)
        {
            foreach (FirstStepFileLedgerEntry entry in SecondStepCandidates(baseName))
            {
                if (!string.IsNullOrWhiteSpace(entry.SourceLayer))
                {
                    return entry.SourceLayer;
                }
            }

            return SameBase(baseName)
                .Select(entry => entry.SourceLayer)
                .FirstOrDefault(layer => !string.IsNullOrWhiteSpace(layer))
                ?? string.Empty;
        }

        /// <summary>
        /// 这个基名在第二大步里的**代表路径**：优先第 1 片（名字槽 1），再退任意候选片；
        /// 没有候选片才退回同基名任意项。
        ///
        /// <para>用于"按这一组找密码 / 找同目录说明文件"这类只读定位 —— 必须是片，不能是外层包。</para>
        /// </summary>
        public string CandidateRepresentativePath(string? baseName)
        {
            IReadOnlyList<FirstStepFileLedgerEntry> candidates = SecondStepCandidates(baseName);
            string? entryPath = candidates
                .Where(entry => entry.NameSlot == 1)
                .Select(entry => entry.Path)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

            if (!string.IsNullOrWhiteSpace(entryPath))
            {
                return entryPath!;
            }

            string? anyPath = candidates
                .Select(entry => entry.Path)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

            if (!string.IsNullOrWhiteSpace(anyPath))
            {
                return anyPath!;
            }

            return SameBase(baseName)
                .Select(entry => entry.Path)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? string.Empty;
        }
        /// <summary>
        /// 任务改名/修后缀后，把旧路径那一条改成新路径。
        /// 只换路径与派生字段，底层身份/卷证据原样保留；旧路径不存在时不新增。
        /// </summary>
        public FirstStepFileLedgerEntry? RetargetPath(string? oldPath, string? newPath)
        {
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            {
                return null;
            }

            string oldFull;
            string newFull;

            try
            {
                oldFull = System.IO.Path.GetFullPath(oldPath);
                newFull = System.IO.Path.GetFullPath(newPath);
            }
            catch
            {
                return null;
            }

            if (!_indexByPath.TryGetValue(oldFull, out int index))
            {
                return null;
            }

            FirstStepFileLedgerEntry retargeted = _entries[index].WithPath(newFull);
            _entries[index] = retargeted;
            _indexByPath.Remove(oldFull);
            _indexByPath[newFull] = index;
            return retargeted;
        }

        /// <summary>按路径取账本项（路径规范化后比较）；没有返回 false。</summary>
        public bool TryGetByPath(string? path, out FirstStepFileLedgerEntry entry)
        {
            entry = null!;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fullPath;

            try
            {
                fullPath = System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            if (!_indexByPath.TryGetValue(fullPath, out int index))
            {
                return false;
            }

            entry = _entries[index];
            return true;
        }

        /// <summary>同基名（第二大步基名）的项；⛔ 不做族判定。</summary>
        public IReadOnlyList<FirstStepFileLedgerEntry> SameBase(string? baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName))
            {
                return Array.Empty<FirstStepFileLedgerEntry>();
            }

            return _entries
                .Where(entry => string.Equals(entry.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>某一条源根链下的全部账本项；空根身份返回空表，⛔ 不退化成“全部”。</summary>
        public IReadOnlyList<FirstStepFileLedgerEntry> ByOriginRoot(string? originRootId)
        {
            if (string.IsNullOrWhiteSpace(originRootId))
            {
                return Array.Empty<FirstStepFileLedgerEntry>();
            }

            return _entries
                .Where(entry => string.Equals(entry.OriginRootId, originRootId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>当前持有租约、交界前不得回收的候选片。</summary>
        public IReadOnlyList<FirstStepFileLedgerEntry> LeasedEntries()
        {
            return _entries.Where(entry => entry.HasCandidateLease).ToList();
        }

        /// <summary>本批出现过的基名（去重、稳定顺序）。</summary>
        public IReadOnlyList<string> BaseNames()
        {
            return _entries
                .Select(entry => entry.BaseName)
                .Where(baseName => !string.IsNullOrWhiteSpace(baseName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(baseName => baseName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
