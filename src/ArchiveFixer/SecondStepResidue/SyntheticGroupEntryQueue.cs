using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **合成组入口的队列**（2026-10-23 按 <c>修改日志.md</c> 2026-10-22 ㈡ 重建）：
    ///
    /// <para>同一组只建**一次**：去重键 = <see cref="VolumeStepExecutor.BuildSyntheticGroupEntryKey"/>
    /// （源层 + 基名）。⛔ 不用 <c>BaseName|Family</c>（族可能判不出，会把同一组散成多个入口）。</para>
    ///
    /// <para>⚠ 那个真机坑（原文逐字）：同一键第一次 `已在=False`、第二次 `已在=True`、
    /// **第三次又变 False** —— 原因是"续跑轮里算出别的 batchRoot"把队列表清掉了。
    /// ⇒ 清空只允许发生在**非续跑轮**（<see cref="ClearForNewRound"/> 的判据）。</para>
    /// </summary>
    public sealed class SyntheticGroupEntryQueue
    {
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已经入队的组入口（只读；顺序按入队先后）。</summary>
        public IReadOnlyList<Entry> Entries => _entries.Values.ToList();

        public int Count => _entries.Count;

        /// <summary>一组已经建好的组入口事实（单元 + 那把去重键 + 搬片结果）。</summary>
        public sealed class Entry
        {
            public string Key { get; init; } = string.Empty;

            public VolumeGroupUnit Unit { get; init; } = null!;

            public VolumeGroupEntryLayer.MoveResult Move { get; init; } = new();
        }

        /// <summary>
        /// 新的一轮开始：**只有非续跑轮**才允许清空（续跑轮清空会把"已在"变成"不在"，同一组会被建两次）。
        /// </summary>
        /// <param name="continuationRound">这一轮是不是续跑轮（判据由调用方回答，⛔ 这里不猜）。</param>
        public void ClearForNewRound(bool continuationRound)
        {
            if (!continuationRound)
            {
                _entries.Clear();
            }
        }

        /// <summary>
        /// 直接把一个已经建好的组入口入队（判重键与 <see cref="TryBuildAndQueueAsync"/> **同一个**）。
        /// </summary>
        /// <returns>true = 这次真的入队；false = 这一组已经在队列里（⛔ 不重复入队、不重复搬片）。</returns>
        public bool TryEnqueue(string? sourceLayer, string? baseName, Entry entry)
        {
            string key = VolumeStepExecutor.BuildSyntheticGroupEntryKey(sourceLayer, baseName);

            if (_entries.ContainsKey(key))
            {
                return false;
            }

            _entries[key] = entry;
            return true;
        }

        /// <summary>
        /// 建组 + 把片放进入口层 + 入队（**幂等**：同键只做一次）。
        /// </summary>
        /// <param name="pieces">这一组的片（任意顺序；事实由内容卡给）。</param>
        /// <param name="sourceLayer">这一组的源层（去重键的一半；⛔ 不许用工作区/其余物当源层）。</param>
        /// <param name="baseName">组基名（去重键的另一半）。</param>
        /// <param name="entryLayerDirectory">入口包所在那一层（片要搬到这里）。</param>
        /// <param name="workRoot">试开用的工作区根。</param>
        /// <param name="engine">与解压同一套引擎。</param>
        /// <param name="isUserImportedSource">"这一片是不是用户导入的原件"的唯一判据（由调用方给）。</param>
        /// <param name="log">日志回调 (级别, 文案)。</param>
        public async Task<Entry?> TryBuildAndQueueAsync(
            IEnumerable<string>? pieces,
            string? sourceLayer,
            string? baseName,
            string? entryLayerDirectory,
            string? workRoot,
            IArchiveEngine? engine,
            Func<string, bool>? isUserImportedSource,
            Action<string, string>? log = null,
            CancellationToken cancellationToken = default)
        {
            string key = VolumeStepExecutor.BuildSyntheticGroupEntryKey(sourceLayer, baseName);

            if (_entries.TryGetValue(key, out Entry? existing))
            {
                // 判重日志照原文写：这一组已经在队列里 ⇒ 不再建、不再搬（⛔ 不重复搬片）。
                log?.Invoke("INFO", $"组入口判重：{baseName} 已在=True（同一源层同一组只建一次）");
                return existing;
            }

            log?.Invoke("INFO", $"组入口判重：{baseName} 已在=False（第一次见到这一组）");

            VolumeGroupUnit? unit = await VolumeStepExecutor.TryBuildGroupUnitFromContentAsync(
                    pieces,
                    workRoot,
                    engine,
                    log,
                    cancellationToken)
                .ConfigureAwait(false);

            if (unit == null)
            {
                return null;
            }

            List<string> ordered = (unit.ConfirmedOrder ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (ordered.Count == 0)
            {
                ordered = (pieces ?? Array.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            }

            VolumeGroupEntryLayer.MoveResult move = VolumeGroupEntryLayer.MovePiecesIntoEntryLayer(
                ordered,
                entryLayerDirectory,
                isUserImportedSource,
                log);

            var entry = new Entry
            {
                Key = key,
                Unit = unit,
                Move = move
            };

            if (!TryEnqueue(sourceLayer, baseName, entry))
            {
                log?.Invoke("INFO", $"组入口判重：{baseName} 已在=True（同一源层同一组只建一次）");
                return _entries[key];
            }

            log?.Invoke("INFO", $"{baseName}：组入口已登记 —— {move.Describe()}");

            return entry;
        }
    }
}
