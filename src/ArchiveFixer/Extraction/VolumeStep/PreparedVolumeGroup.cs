using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>**一组已经由族专属 resolver 解析完的结论**（共用骨架）。</summary>
    public enum PreparedGroupState
    {
        /// <summary>可证完整（标准名已建好）。</summary>
        Complete = 0,

        /// <summary>缺片（只有内容/名字能点名时才允许）。</summary>
        Missing = 1,

        /// <summary>判不出（证据不足/冲突）。</summary>
        Unknown = 2,

        /// <summary>结构矛盾（数量/槽位/大小/混族）。</summary>
        SizeAnomaly = 3
    }

    /// <summary>
    /// **标准名 -> 原始片**的一条映射。
    ///
    /// <para>标准名只在临时组入口目录里存在；原始片是用户源文件或本链产出的片。
    /// 源包删除/落点/接片一律读 <see cref="OriginalPath"/>，⛔ 不碰临时链接。</para>
    /// </summary>
    public sealed class PreparedVolumePiece
    {
        public string CanonicalName { get; init; } = string.Empty;

        public string OriginalPath { get; init; } = string.Empty;

        public long Size { get; init; }

        public VolumeHandoffSource Source { get; init; }
    }

    /// <summary>
    /// **边界 resolver 的统一产物**（共用骨架）：族、顺序、标准名、原始映射、结论。
    ///
    /// <para>它是在 `GroupAnalysis` **之前**产出的；`GroupAnalysis` 只吃这个对象，
    /// 不再自己从脏名/全树重排。</para>
    /// </summary>
    public sealed class PreparedVolumeGroup
    {
        public string BaseName { get; init; } = string.Empty;

        public VolumeNamingFamily? Family { get; init; }

        public string OriginalSourceLayer { get; init; } = string.Empty;

        /// <summary>临时组入口目录（与源片同卷；只放标准名硬链接）。</summary>
        public string EntryLayer { get; init; } = string.Empty;

        /// <summary>入口片的原始路径（任务表/落点/source 处理读它）。</summary>
        public string OriginalEntryPath { get; init; } = string.Empty;

        /// <summary>入口片的标准名（引擎读它）。</summary>
        public string EntryCanonicalName { get; init; } = string.Empty;

        public IReadOnlyList<PreparedVolumePiece> Pieces { get; init; } = Array.Empty<PreparedVolumePiece>();

        public IReadOnlyList<int> MissingNumbers { get; init; } = Array.Empty<int>();

        public PreparedGroupState State { get; init; } = PreparedGroupState.Unknown;

        public string Evidence { get; init; } = string.Empty;

        /// <summary>`SizeAnomaly` 的具体判据；成功时为空。</summary>
        public string SizeAnomalyDetail { get; init; } = string.Empty;

        public bool IsComplete => State == PreparedGroupState.Complete;

        /// <summary>入口的标准名全路径；尚未建临时目录时为空。</summary>
        public string CanonicalEntryPath =>
            string.IsNullOrWhiteSpace(EntryLayer) || string.IsNullOrWhiteSpace(EntryCanonicalName)
                ? string.Empty
                : Path.Combine(EntryLayer, EntryCanonicalName);

        /// <summary>标准名 -> 原始路径（源包处理/落点/接片用）。</summary>
        public IReadOnlyDictionary<string, string> BuildOriginalMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (PreparedVolumePiece piece in Pieces)
            {
                if (!string.IsNullOrWhiteSpace(piece.CanonicalName)
                    && !string.IsNullOrWhiteSpace(piece.OriginalPath))
                {
                    map[piece.CanonicalName] = piece.OriginalPath;
                }
            }

            return map;
        }

        /// <summary>
        /// 建好临时入口层之后，复制一份带 <see cref="EntryLayer"/> 的结论；
        /// 其余事实（顺序、映射、结论）一个字不改。
        /// </summary>
        public PreparedVolumeGroup WithEntryLayer(string entryLayer)
        {
            return new PreparedVolumeGroup
            {
                BaseName = BaseName,
                Family = Family,
                OriginalSourceLayer = OriginalSourceLayer,
                EntryLayer = entryLayer,
                OriginalEntryPath = OriginalEntryPath,
                EntryCanonicalName = EntryCanonicalName,
                Pieces = Pieces,
                MissingNumbers = MissingNumbers,
                State = State,
                Evidence = Evidence,
                SizeAnomalyDetail = SizeAnomalyDetail
            };
        }

        /// <summary>同一组事实改成另一个结论（例如标准名硬链接建不出来 ⇒ Unknown）。</summary>
        public PreparedVolumeGroup WithState(PreparedGroupState state, string evidence)
        {
            return new PreparedVolumeGroup
            {
                BaseName = BaseName,
                Family = Family,
                OriginalSourceLayer = OriginalSourceLayer,
                EntryLayer = EntryLayer,
                OriginalEntryPath = OriginalEntryPath,
                EntryCanonicalName = EntryCanonicalName,
                Pieces = Pieces,
                MissingNumbers = MissingNumbers,
                State = state,
                Evidence = evidence,
                SizeAnomalyDetail = SizeAnomalyDetail
            };
        }

        /// <summary>给人看的一行（日志/判据输入共用；⛔ 不猜）。</summary>
        public string Describe()
        {
            string entry = string.IsNullOrWhiteSpace(OriginalEntryPath)
                ? "判不出"
                : Path.GetFileName(OriginalEntryPath);

            return $"{BaseName} ｜ 族={Family} ｜ 结论={State} ｜ 片={Pieces.Count}"
                + $" ｜ 入口={entry}"
                + (MissingNumbers.Count == 0 ? string.Empty : $" ｜ 缺={string.Join(",", MissingNumbers)}")
                + (string.IsNullOrWhiteSpace(SizeAnomalyDetail) ? string.Empty : $" ｜ {SizeAnomalyDetail}")
                + (string.IsNullOrWhiteSpace(Evidence) ? string.Empty : $" ｜ {Evidence}");
        }
    }
}
