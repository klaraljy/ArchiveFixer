using System.IO;
namespace ArchiveFixer.Extraction;

/// <summary>
/// **第二大步的组分析（全树版）** —— 用户 2026-10-23 口径：
///
/// <para>「有些分卷是看不出来的」⇒ 判据不能只靠内容：<b>基名（第一个点之前）+ 大小分布</b> 是基石；
/// 内容只回答"能回答的那部分"（卷号 / 首卷标记 / 整包字节数）。</para>
///
/// <para>输入 = 第一大步登记的交接事实 / 任意几份候选文件（含中间卷、判不出的片）；
/// 扫描根 = 本批产物树 + 用户源目录（可多个）。只回答"这一组是谁、找齐了没有、入口是哪一片"，
/// ⛔ 不改名、⛔ 不搬、⛔ 不删（执行归分卷专属算法）。</para>
///
/// <para>判据顺序：① 基名相同（全树收片）⇒ ② 定序：内容卷号优先，名字卷标记次之，
/// 都没有，就**按大小**（除末片外等大、末片更小）⇒ ③ 连成 1..N 才算可证完整；
/// 连不成 ⇒ 缺卷（缺口能点名）；连定序都做不到 ⇒ 判不出（什么都不做）。</para>
///
/// <para>⚠ 用户口径（2026-10-23）：「基名相同、体积也相同但其实是两个包」的概率非常小，**不做处理** ——
/// 所以这一层<b>不按族/格式分桶</b>，族只作为证据字段如实写出来。</para>
///
/// <para><b>职责拆分（2026-11-14，公开 API 与判据一个字没改）</b>：本类只留公开入口
/// （三个入口都是一行转发）与公开 DTO；候选收集 = <see cref="VolumeCandidateCollector"/>
/// （种子准入 / 基名分桶 / 全树收片 / 字节补齐配对 / 自述分桶 / 内容卡补位 /
/// prepared 组适配）；
/// 同组判定 = <see cref="VolumeGroupMembership"/>（这一份能不能算某组的一片）；
/// 定序 = <see cref="VolumeGroupPieceOrdering"/>（入口/名字卷号/内容卷号/跨盘 ZIP）；
/// 证据建模 = <see cref="VolumeCandidateEvidence"/>（交接单优先的一片事实 / 公开事实 / 内容卡）；
/// 失败模型与定案 = <see cref="VolumeCandidateVerdict"/>（完整 / 缺片 / 判不出与证据措辞的唯一出口）。
/// 它们都是无状态静态协作类，没有 partial、没有第二份判据。</para>
/// </summary>
public static class VolumeCandidateDiscovery
{

    public sealed class VolumeCandidate
    {
        public string BaseName { get; init; } = string.Empty;

        public ArchiveFixer.Detection.VolumeContentFormat Format { get; init; }

        public IReadOnlyList<string> Pieces { get; init; } = Array.Empty<string>();

        public string EntryPath { get; init; } = string.Empty;

        /// <summary>
        /// 候选建议，不是最终结论；⛔ 第二大步的完整性/缺片/规范名只认
        /// <see cref="VolumeGroupService"/> + 族算法。
        /// </summary>
        public PreparedGroupState SuggestedVerdict { get; init; }

        private string _evidence = string.Empty;

        public string Evidence
        {
            get => _evidence;
            init => _evidence = value;
        }

        /// <summary>给已有候选补一句"忽略了哪些候选"的说明（⛔ 不改判据，只补证据文本）。</summary>
        internal void AppendEvidence(string note)
        {
            if (!string.IsNullOrWhiteSpace(note))
            {
                _evidence += note;
            }
        }

        /// <summary>判不出时"缺口"是空的；判出缺卷时列出缺的卷号（1 起）。</summary>
        public IReadOnlyList<int> MissingNumbers { get; init; } = Array.Empty<int>();

        /// <summary>自述片数（7z 首片起始头算出来的；随交接单过来）；判不出 = 0。</summary>
        public int DeclaredPieceCount { get; init; }

        /// <summary>已收片数（按物理身份去重后）。</summary>
        public int ReceivedPieceCount { get; init; }

        /// <summary>自述整包字节数（7z 硬证据）；判不出 = 0。</summary>
        public long DeclaredTotalBytes { get; init; }

        /// <summary>已收字节数合计（与自述整包对账用）。</summary>
        public long ReceivedBytes { get; init; }

        /// <summary>每片的原始事实（第二大步建组用；卷号可为空，⛔ 不在这里补）。</summary>
        public IReadOnlyList<VolumeFacts> PieceFacts { get; set; } = Array.Empty<VolumeFacts>();

        /// <summary>
        /// 这一组是**字节补齐配对**出来的（首片自述整包 = 首片 + 同目录唯一无魔数尾片）。
        /// 下游据此优先于 resolver 对同名/同基名的 Unknown 结论。
        /// </summary>
        public bool ByteFitPaired { get; set; }

        /// <summary>
        /// 边界 resolver 已经解析好的组（骨架阶段可能为 null）。
        /// 有它时，入口/顺序/标准名以它为准，⛔ 不再从脏名重排。
        /// </summary>
        public PreparedVolumeGroup? PreparedGroup { get; init; }

        /// <summary>入口片的原始路径（任务表/落点/source 处理用；临时标准名不算）。</summary>
        public string OriginalEntryPath { get; init; } = string.Empty;

        /// <summary>每片的底层内容卡（族判定唯一出口要用它；已登记的片由交接单还原，⛔ 不重读盘）。</summary>
        public IReadOnlyList<ArchiveFixer.Detection.FileNatureCard> Cards { get; set; } =
            Array.Empty<ArchiveFixer.Detection.FileNatureCard>();

        public string Describe() =>
            $"{BaseName} ｜ 族={Format} ｜ 找到 {Pieces.Count} 片 ｜ 候选"
            + $" ｜ 入口={(EntryPath.Length == 0 ? "判不出" : Path.GetFileName(EntryPath))}"
            + (MissingNumbers.Count == 0 ? string.Empty : $" ｜ 缺={string.Join(",", MissingNumbers)}")
            + (DeclaredPieceCount > 0
                ? $" ｜ 片数来自自述 {DeclaredPieceCount} 片（已收 {ReceivedPieceCount}）"
                : string.Empty)
            + (DeclaredTotalBytes > 0
                ? $" ｜ 自述整包 {DeclaredTotalBytes} 字节（已收 {ReceivedBytes}）"
                : string.Empty)
            + $" ｜ {Evidence}";
    }

    /// <summary>
    /// 全树组分析（只读）。<paramref name="seeds"/> 是任意几份文件（含中间卷、判不出的片），
    /// <paramref name="scanRoots"/> 是要扫的根目录（本批产物树 / 用户源目录）。
    /// </summary>
    public static List<VolumeCandidate> Analyze(
        IEnumerable<string>? seeds,
        IEnumerable<string>? scanRoots)
        => VolumeCandidateCollector.Collect(seeds, scanRoots, handoffByPath: null);

    /// <summary>
    /// **按交接单做全树组分析**（用户 2026-10-24：第二大步要吃交接单里的底层事实）：
    /// 种子 = 交接单里有路径的那些；每片的族 / 卷号 / 首卷 / 自述片数 / 自述整包字节数**直接取交接单**，
    /// ⛔ 不再对已经登记过的片重新 `FileNature.Inspect`；盘上只补“同基名但没登记过的兄弟片”，
    /// 补到的那几片才回盘上读一次证据。
    /// </summary>
    public static List<VolumeCandidate> Analyze(
        IReadOnlyList<ArchiveFixer.Detection.VolumeHandoffEntry>? handoffs,
        IEnumerable<string>? scanRoots,
        IEnumerable<string>? extraSeeds = null,
        IReadOnlyCollection<string>? excludedSources = null)
    {
        var handoffByPath = new Dictionary<string, ArchiveFixer.Detection.VolumeHandoffEntry>(
            StringComparer.OrdinalIgnoreCase);
        var seeds = new List<string>();

        foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry
                 in handoffs ?? Array.Empty<ArchiveFixer.Detection.VolumeHandoffEntry>())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            handoffByPath[entry.Path] = entry;
            seeds.Add(entry.Path);
        }

        /*
         * 额外种子 = 这一轮认出来的内层归档（它们没被闸门拦下、所以不在交接单里）——
         * 只加种子，不加事实：它们仍按盘上证据判。
         */
        foreach (string seed in extraSeeds ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(seed))
            {
                seeds.Add(seed);
            }
        }

        return VolumeCandidateCollector.Collect(seeds, scanRoots, handoffByPath, excludedSources);
    }

    /// <summary>
    /// **吃边界 resolver 的 prepared 组**（共用骨架的新入口）：
    /// 只把已经标准化的片收成候选，⛔ 不再扫用户源目录、⛔ 不再从脏名重排。
    /// 旧 <see cref="Analyze(IEnumerable{string}?, IEnumerable{string}?)"/> 保留给未迁移族。
    /// 实现（prepared 组 → 候选的适配）= <see cref="VolumeCandidateCollector.CollectPrepared"/>。
    /// </summary>
    public static List<VolumeCandidate> AnalyzePrepared(IReadOnlyList<PreparedVolumeGroup>? groups)
        => VolumeCandidateCollector.CollectPrepared(groups);
}
