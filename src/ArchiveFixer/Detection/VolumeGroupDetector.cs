using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 输入：一个候选文件。
    ///
    /// 只带"路径 + 大小"两个事实：分卷组识别在本模块里是**纯命名**判定，
    /// 格式探测、卷内结构之类的证据由调用方（扫描/识别服务）在别处提供。
    /// </summary>
    public sealed class VolumeCandidate
    {
        /// <summary>完整路径（调用方保证非空）。也允许只给文件名，本模块不做全路径规范化的假设。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>
        /// 字节数，未知填 -1。
        ///
        /// 目前**不参与任何判定**，只是原样带进 <see cref="VolumeGroup.Volumes"/>。
        /// 留它的原因是 AGENTS.md §9.3 / 设计.md §十六 要求"不要只凭 .001/.002 就认定是一组"，
        /// 将来补"大小是否一致、是否首卷带主头"的复核时要用到，但不该在这个纯命名模块里实现。
        /// </summary>
        public long Size { get; init; } = -1;
    }

    /// <summary>
    /// 一组分卷（AGENTS.md §9.3：**一组分卷 = 一个任务**，只从 <see cref="FirstVolumePath"/> 启动）。
    /// </summary>
    public sealed class VolumeGroup
    {
        /// <summary>
        /// 归组键，形如 <c>目录|基名</c>（例：<c>C:\t|volume.7z</c>），语义上忽略大小写。
        ///
        /// 只有极少数情况下末尾会再加一段族标记（<c>|num</c> / <c>|z</c> / <c>|r</c> / <c>|part</c>）：
        /// 同一目录同一基名下**并存两套命名**时（例如 <c>x.rar</c>+<c>x.r00</c> 与 <c>x.part1.rar</c>+<c>x.part2.rar</c>，
        /// 这是两个真实存在、互不兼容的分卷集），否则两组会撞同一个键。下游拿它当字典键是安全的。
        /// </summary>
        public string GroupKey { get; init; } = string.Empty;

        /// <summary>所在目录（调用方给的原样路径片段，本模块不碰文件系统，不做全路径规范化）。</summary>
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>
        /// 基名。例：<c>volume.7z.001</c> → <c>volume.7z</c>、<c>archive.001</c> → <c>archive</c>、
        /// <c>movie.part1.rar</c> → <c>movie</c>、<c>x.z01</c>/<c>x.zip</c> → <c>x</c>。
        /// 保留首次出现时的大小写，比较时忽略大小写。
        /// </summary>
        public string BaseName { get; init; } = string.Empty;

        /// <summary>已按卷序升序排列（含 zip/rar 本体这一"第 1 卷"）。</summary>
        public IReadOnlyList<VolumeCandidate> Volumes { get; init; } = Array.Empty<VolumeCandidate>();

        /// <summary>
        /// 启动解压用的那一卷：**已找到**的分卷里卷序最小的那一卷。
        ///
        /// 注意两点：一是 <see cref="IsComplete"/> 为 false 时不得拿它去启动（不变量 7：缺卷不得开始不可完成的任务，
        /// 先把 <see cref="MissingVolumeNames"/> 报给用户）；二是只有 <c>.z01</c>/<c>.r00</c> 而没有本体时，
        /// 这里会是 <c>.z01</c>/<c>.r00</c>，同时缺卷清单里带着本体名。
        /// </summary>
        public string FirstVolumePath { get; init; } = string.Empty;

        /// <summary>已找到的卷数（含本体）。</summary>
        public int KnownVolumeCount { get; init; }

        /// <summary>
        /// 期望卷数；无法确定时为 0。
        ///
        /// 本模块认的五种命名（<c>.001</c> / <c>.z01</c> / <c>.r00</c> / <c>.partN.rar</c> / 本体）
        /// **没有一个带"总共几卷"这个信息**，所以这里恒为 0 —— 这是有意为之，
        /// 不是"没实现"：拿"找到的最大卷号"冒充总数，会让缺卷的组看起来是完整的。
        /// 字段保留是给将来出现真带总数的命名（例如某些打包器会在文件名里写 total）留位。
        /// </summary>
        public int ExpectedVolumeCount { get; init; }

        /// <summary>
        /// 卷序是否**从 1 开始连续、没有缺号**。
        ///
        /// 为什么不是"期望卷数 > 0 且无缺口"：期望卷数永远推不出来（见 <see cref="ExpectedVolumeCount"/>），
        /// 那条判据会恒为 false，等于把所有正常的分卷组都标成不完整。
        /// 所以这里取"能验证的那一半"：找到了第 1 卷、且 1..最大卷号之间没有洞。
        /// 反过来，只有 1 卷（例如只有 <c>volume.7z.001</c>）时这里是 true —— 后面有没有卷没人知道，
        /// 这种情况由 <see cref="Note"/> 提示"可能不完整"，不要用 false 冒充确定结论。
        /// </summary>
        public bool IsComplete { get; init; }

        /// <summary>
        /// 缺失的卷文件名（只有文件名，不含目录）。判据是卷号：<c>.001</c>+<c>.003</c> 缺的是
        /// <c>volume.7z.002</c>；<c>.z01</c>/<c>.r00</c> 缺本体时报 <c>x.zip</c>/<c>x.rar</c>。
        /// 超出"最大已找到卷号"的缺号无法得知，不会列在这里（总数未知，不猜）。
        /// </summary>
        public IReadOnlyList<string> MissingVolumeNames { get; init; } = Array.Empty<string>();

        /// <summary>给用户看的一句话，例如"无法确定分卷总数，按顺序找到 3 卷"。</summary>
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>
    /// 分卷组识别（AGENTS.md §9.3）。
    ///
    /// 认的命名（左列是第 1 卷，右列是后续卷）：
    ///
    /// <code>
    /// volume.7z.001   →  .002 / .003 …        基名 volume.7z
    /// archive.001     →  .002 / .003 …        基名 archive
    /// x.zip           →  x.z01 / x.z02 …      基名 x（本体就是第 1 卷）
    /// x.rar           →  x.r00 / x.r01 …      基名 x（本体就是第 1 卷）
    /// x.part1.rar     →  x.part2.rar …        基名 x
    /// </code>
    ///
    /// 三个最容易搞错的地方，先说清楚为什么：
    ///
    /// 1. <b>卷序是"绝对卷序"，不是"后缀里的数字"</b>。<c>x.r00</c> 返回 2 而不是 1：
    ///    rar 老式分卷里 <c>x.rar</c> 才是第 1 卷，<c>r00</c> 只是"第 1 个后续卷"，
    ///    所以 <c>rNN</c> 的卷序是 NN + 2，<c>zNN</c> 的卷序是 NN + 1（zip 分卷的本体是 <c>x.zip</c>）。
    ///    这个偏移写错，后果是永远报"缺第 1 卷"或把两卷当成一卷。
    ///
    /// 2. <b>卷总数推不出来，就不推</b>。这些命名都只有"第几卷"没有"共几卷"，
    ///    所以 <see cref="VolumeGroup.ExpectedVolumeCount"/> 恒为 0、<see cref="VolumeGroup.IsComplete"/>
    ///    只管"从 1 开始连续无缺号"。缺号（<c>.001</c>+<c>.003</c>）能确定，就列进
    ///    <see cref="VolumeGroup.MissingVolumeNames"/>；最大卷号之后的缺号无法得知，不猜。
    ///
    /// 3. <b>普通压缩包绝不能被吞成分卷组</b>。<c>x.zip</c>/<c>x.rar</c> 单看名字确实"是第 1 卷"，
    ///    但只有当同目录同基名下真的存在 <c>.z01</c>/<c>.r00</c> 时才算一组
    ///    —— 否则一个装满普通 zip 的目录会被整体识别成分卷组，那比漏识别糟得多。
    ///
    /// 明确的能力边界：本模块**只按命名归组**，而且**不碰文件系统**（不查存在性、不读内容）。
    /// "同名但其实是两个不同归档"这种情况，需要大小/首卷主头/引擎探测结果来复核，
    /// 那是调用方的事（设计.md §十六；AGENTS.md §9.3 要求不得只凭 .001/.002 就认定同一组）。
    /// </summary>
    public static class VolumeGroupDetector
    {
        /// <summary>Note 里最多列几个缺卷名，超出的用"等"收尾（完整清单看 <see cref="VolumeGroup.MissingVolumeNames"/>）。</summary>
        private const int MaxMissingNamesInNote = 5;

        /// <summary>把一个目录下的候选文件按分卷组归并；非分卷文件不返回。</summary>
        public static IReadOnlyList<VolumeGroup> Group(IEnumerable<VolumeCandidate> files)
        {
            if (files == null)
            {
                return Array.Empty<VolumeGroup>();
            }

            var buckets = new Dictionary<string, VolumeBucket>(StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (VolumeCandidate candidate in files)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
                {
                    continue;
                }

                // 同一个文件被喂两次（"扫描目录"和"用户手工添加"各来一遍）只算一次。
                if (!seenPaths.Add(candidate.Path))
                {
                    continue;
                }

                VolumeNameInfo? info = Analyze(candidate.Path);
                if (info == null)
                {
                    continue;
                }

                string directoryPath = FileNameHelper.GetDirectoryName(candidate.Path);
                string bucketKey = BuildBaseKey(directoryPath, info.BaseName) + "|" + FamilyToken(info.Family);

                if (!buckets.TryGetValue(bucketKey, out VolumeBucket? bucket))
                {
                    bucket = new VolumeBucket(directoryPath, info.BaseName, info.Family);
                    buckets.Add(bucketKey, bucket);
                }

                bucket.Add(candidate, info);
            }

            // 只有"本体"没有分卷标记的桶不是分卷组（普通 x.zip / x.rar 就落在这里）。
            List<VolumeBucket> survivors = buckets.Values.Where(b => b.HasVolumeMark).ToList();

            /*
             * 名字**被伪装过**的组（`001删除` / `001.txt`）必须再过一道"尺寸规律"：
             * 除最后一卷外大小完全相等、最后一卷 ≤ 整卷大小。理由（用户 2026-09-28 三层方案）：
             * 宽松的名字判据有可能把"碰巧带数字段"的一堆文件凑成一组，而尺寸规律是**与名字无关**的硬证据；
             * 一旦认错组，一键处理就会去改一批不相干文件的名字 —— 认错比不认糟得多。
             */
            survivors = survivors.Where(b => !b.HasDisguisedName || b.HasRegularVolumeSizes).ToList();

            if (survivors.Count == 0)
            {
                return Array.Empty<VolumeGroup>();
            }

            // 同一"目录+基名"下有几套命名；只有多于一套时 GroupKey 才需要加族标记来保证唯一。
            Dictionary<string, int> familyCounts = survivors
                .GroupBy(b => BuildBaseKey(b.DirectoryPath, b.BaseName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            return survivors
                .Select(bucket => BuildGroup(bucket, familyCounts))
                .OrderBy(g => g.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.BaseName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.GroupKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>取单个文件的卷序（1 表示第一卷）；不是分卷返回 null。</summary>
        public static int? TryGetVolumeIndex(string fileName)
        {
            return Analyze(fileName)?.Index;
        }

        /// <summary>
        /// 由任意一卷推出该组的"第一卷文件名"；不是分卷返回 null。
        ///
        /// 只给文件名、不查存在性：<c>x.z02</c> 推出的是 <c>x.zip</c>，即便目录里根本没有这个本体
        /// （这种情况由 <see cref="Group"/> 在缺卷清单和 Note 里点名）。
        /// </summary>
        public static string? TryGetFirstVolumeName(string fileName)
        {
            VolumeNameInfo? info = Analyze(fileName);
            if (info == null)
            {
                return null;
            }

            return info.TryFormat(1, out string firstVolumeName) ? firstVolumeName : null;
        }

        /// <summary>
        /// 判断两个文件是否属于同一分卷组的同一基名（同目录比较由调用方负责）。
        ///
        /// 只比"族 + 基名"：<c>x.zip</c> 与 <c>x.z01</c> 同组（同一族的本体与后续卷），
        /// 而 <c>x.rar</c> 与 <c>x.part1.rar</c> **不同组** —— 老式与新式是两套互不兼容的命名，
        /// 合并会让两个集的第一卷撞在同一个卷号上。
        /// </summary>
        public static bool BelongsToSameGroup(string fileNameA, string fileNameB)
        {
            VolumeNameInfo? a = Analyze(fileNameA);
            VolumeNameInfo? b = Analyze(fileNameB);

            return a != null
                   && b != null
                   && a.Family == b.Family
                   && string.Equals(a.BaseName, b.BaseName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 分卷命名族。族决定三件事：卷序怎么算、缺失名字怎么补、能不能归到一组。
        /// </summary>
        private enum VolumeFamily
        {
            /// <summary>末尾纯数字段（<c>volume.7z.001</c> / <c>archive.001</c>）：数字就是卷序，没有单独的本体。</summary>
            Numeric,

            /// <summary>zip 分卷（<c>x.zip</c> + <c>x.z01</c> …）：本体是第 1 卷，<c>zNN</c> 是第 NN+1 卷。</summary>
            ZipSpanned,

            /// <summary>rar 老式分卷（<c>x.rar</c> + <c>x.r00</c> …）：本体是第 1 卷，<c>rNN</c> 是第 NN+2 卷。</summary>
            RarOld,

            /// <summary>rar 新式分卷（<c>x.partN.rar</c>）：N 就是卷序，part1 是第一卷，没有单独的本体。</summary>
            PartNumbered
        }

        /// <summary>一个文件名的解析结果：属于哪一族、第几卷、基名是什么。</summary>
        private sealed class VolumeNameInfo
        {
            public string BaseName { get; init; } = string.Empty;

            public VolumeFamily Family { get; init; }

            /// <summary>绝对卷序，1 起。</summary>
            public int Index { get; init; }

            /// <summary>是不是"本体"（<c>x.zip</c> / <c>x.rar</c>）。本体只有在本族的后续卷存在时才算数。</summary>
            public bool IsBody { get; init; }

            /// <summary>分卷段后面固定挂的尾巴（目前只有 <c>.rar</c>，例如 <c>x.part1.rar</c> / <c>x.001.rar</c>）。</summary>
            public string Tail { get; init; } = string.Empty;

            /// <summary><c>partN</c> 的数字位宽（<c>part01</c> → 2），用来让补出来的缺失名保持同样的补零风格。</summary>
            public int DigitWidth { get; init; } = 1;

            /// <summary>是不是"名字被伪装过"的分卷标记（夹了垃圾 `001删除`、或标记后面挂点段 `001.txt`）。</summary>
            public bool IsDisguised { get; init; }

            /// <summary>
            /// 这个名字是不是"**本体后缀被塞了垃圾**"（<c>222.zi删除p</c> / <c>222.zscip</c>）。
            ///
            /// <para>它与"干净的本体"（<c>222.zip</c>）**一样算分卷成员**：真机里那一组的另一个成员
            /// 恰恰就是 <c>222.zip</c>，靠它才认得出"这是一组"（只有一对光杆本体时不许成组 ——
            /// 一份单独的 <c>.zip</c> 后面还有没有卷，名字给不出答案）。</para>
            /// </summary>
            public bool IsDisguisedBody { get; init; }

            /// <summary>
            /// 这个名字是不是**这一族的规范名、一字不差**（<c>x.zip</c> / <c>x.rar</c> / <c>x.z01</c> / <c>x.part1.rar</c>）。
            ///
            /// <para>⛔ 靠容错/骨架**还原**出来的（<c>222.zi删除p</c> → <c>zip</c>、<c>222.z0sc1</c> → <c>z01</c>）
            /// 一律为 false —— 它只用来决定"同一个卷号上有两个文件时先认谁"（规范名优先，见
            /// <see cref="VolumeBucket.Add"/>），⛔ 不许拿它当"可以删"的资格（那个唯一出口是
            /// <c>VolumeGroupResolver.CanEnterDeletableRestItems</c>）。</para>
            /// </summary>
            public bool HasCanonicalName { get; init; }

            /// <summary>按本族命名规则写出"第 index 卷"的文件名；该族表示不了这个卷号时返回 false。</summary>
            public bool TryFormat(int index, out string fileName)
            {
                fileName = string.Empty;

                if (index < 1)
                {
                    return false;
                }

                switch (Family)
                {
                    case VolumeFamily.Numeric:
                        if (index > 999)
                        {
                            return false;
                        }

                        fileName = BaseName + "." + index.ToString("D3", CultureInfo.InvariantCulture) + Tail;
                        return true;

                    case VolumeFamily.ZipSpanned:
                        if (index == 1)
                        {
                            fileName = BaseName + ".zip";
                            return true;
                        }

                        if (index - 1 > 99)
                        {
                            return false;
                        }

                        fileName = BaseName + ".z" + (index - 1).ToString("D2", CultureInfo.InvariantCulture);
                        return true;

                    case VolumeFamily.RarOld:
                        if (index == 1)
                        {
                            fileName = BaseName + ".rar";
                            return true;
                        }

                        if (index - 2 > 99)
                        {
                            return false;
                        }

                        fileName = BaseName + ".r" + (index - 2).ToString("D2", CultureInfo.InvariantCulture);
                        return true;

                    default:
                        fileName = BaseName + ".part" + index.ToString("D" + DigitWidth, CultureInfo.InvariantCulture) + Tail;
                        return true;
                }
            }
        }

        /// <summary>同一目录、同一基名、同一命名族的卷集合。</summary>
        private sealed class VolumeBucket
        {
            private readonly Dictionary<int, VolumeCandidate> _byIndex = new Dictionary<int, VolumeCandidate>();

            /// <summary>已经有"规范名"占住的卷号（后来者即使也是规范名也不抢，先到先得）。</summary>
            private readonly HashSet<int> _canonicalIndices = new HashSet<int>();

            /// <summary>进过这个桶的全部路径（判"桶里有没有真正的分卷标记"用）。</summary>
            private readonly HashSet<string> _allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>其中属于"本体"那一档的路径（<c>x.zip</c> / <c>x.rar</c>，含被塞了杂质的本体名）。</summary>
            private readonly HashSet<string> _bodyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>其中"被塞了杂质的本体名"（<c>222.zi删除p</c>）—— 它也算分卷成员，只是名字不标准。</summary>
            private readonly HashSet<string> _disguisedBodyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>名字不标准的卷（脏本体名 / 容错还原出来的卷标记）—— 尺寸规律不拿它们当基准。</summary>
            private readonly HashSet<string> _disguisedVolumePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public VolumeBucket(string directoryPath, string baseName, VolumeFamily family)
            {
                DirectoryPath = directoryPath;
                BaseName = baseName;
                Family = family;
            }

            public string DirectoryPath { get; }

            public string BaseName { get; }

            public VolumeFamily Family { get; }

            /// <summary>
            /// 这个桶里除了"干净的本体"以外，还有没有别的分卷成员
            /// （干净的后续卷 <c>x.z01</c>，或者**被塞了杂质的本体名** <c>222.zi删除p</c>）。
            ///
            /// <para>只有一对光杆本体（<c>x.zip</c> + <c>x.zi删除p</c>）时这一条才算成立 ——
            /// 老口径"只有本体不算分卷组"必须留着：一份单独的 <c>.zip</c> 后面还有没有卷，
            /// 名字给不出答案，认成组就会去改用户的名字（宁可判不出）。</para>
            /// </summary>
            public bool HasVolumeMark
            {
                get
                {
                    if (_disguisedBodyPaths.Count > 0)
                    {
                        return true;
                    }

                    foreach (string path in _allPaths)
                    {
                        if (!_bodyPaths.Contains(path))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            /// <summary>桶里出现过"名字被伪装过"的卷标记（<c>001删除</c> / <c>001.txt</c>）。</summary>
            public bool HasDisguisedName { get; private set; }

            /// <summary>
            /// **尺寸规律**（三层证据里的第 2 层，与名字无关）：除最后一卷外大小完全相等、
            /// 最后一卷更小或相等。名字被伪装过的组必须过这一关才认 —— 认错比不认更糟。
            /// 量不出大小（<c>Size &lt;= 0</c>）时一律当"没证据"，不放行。
            /// </summary>
            public bool HasRegularVolumeSizes
            {
                get
                {
                    /*
                     * 尺寸规律问的是"**干净的那些卷**对不对得上"：
                     *   · "卷标记 + 脏尾巴"那一档（`giu910.7z.001删除`）照旧参与
                     *     （用户 2026-09-28 那一组就是靠它成组的）；
                     *   · **脏本体名**（`222.zi删除p`）不参与比大小 —— 它在目录里的位置（最后一个）
                     *     与它声称的卷序（1）天然对不上，参与进去会把真机那一组判死。
                     *
                     * ⚠ 但它**算成员**（见 `HasVolumeMark`）：所以"唯一一个干净卷 + 一个脏本体名"
                     * 也要能成组（真机就是 `222.z0sc1` + `222.zi删除p`），
                     * 那一档只要求"这一个卷量得出大小"。
                     */
                    var ordered = SortedIndices
                        .Where(i => !_disguisedVolumePaths.Contains(this[i].Path))
                        .Select(i => this[i])
                        .ToList();

                    // 一个脏本体名 + 一个干净卷：本体名本身就是"这是一组"的证据，尺寸只需量得出来。
                    if (ordered.Count == 1 && _disguisedBodyPaths.Count == 1)
                    {
                        return ordered[0].Size > 0;
                    }

                    if (ordered.Count < 2)
                    {
                        return false;
                    }

                    long full = ordered[0].Size;

                    if (full <= 0)
                    {
                        return false;
                    }

                    for (int k = 0; k < ordered.Count - 1; k++)
                    {
                        if (ordered[k].Size != full)
                        {
                            return false;
                        }
                    }

                    return ordered[^1].Size > 0 && ordered[^1].Size <= full;
                }
            }

            /// <summary>补齐缺失文件名时用的尾巴（见 <see cref="VolumeNameInfo.Tail"/>）。</summary>
            public string Tail { get; private set; } = string.Empty;

            /// <summary>补齐缺失文件名时用的补零位宽。</summary>
            public int DigitWidth { get; private set; } = 1;

            public List<int> SortedIndices => _byIndex.Keys.OrderBy(i => i).ToList();

            public VolumeCandidate this[int index] => _byIndex[index];

            public bool ContainsIndex(int index)
            {
                return _byIndex.ContainsKey(index);
            }

            public void Add(VolumeCandidate candidate, VolumeNameInfo info)
            {
                _allPaths.Add(candidate.Path);

                if (info.IsBody)
                {
                    _bodyPaths.Add(candidate.Path);
                }

                if (info.Tail.Length > 0)
                {
                    Tail = info.Tail;
                }

                if (info.IsDisguised)
                {
                    HasDisguisedName = true;
                }

                /*
                 * ⛔ 尺寸规律（与名字无关的那条硬证据）**只把"脏本体名"排除在外**：
                 * `222.zi删除p` 这种名字里根本没有卷标记，它在目录里的位置（最后一个）与它声称的
                 * 卷序（1）天然对不上；真机上它与另一卷等大，拿它当"满卷"基准会把"最后一卷更小"
                 * 那一条判死 ⇒ 整桶被丢 ⇒ 归组为空 ⇒ 定稿判"判不出"。
                 *
                 * ⚠ 卷标记后面粘着尾巴的那一档（`giu910.7z.001删除`）**照旧参与** —— 三卷等大时
                 * 它必须过得了这一关（用户 2026-09-28 报的那一组就是靠它才成组的）。
                 */
                if (info.IsDisguisedBody)
                {
                    _disguisedBodyPaths.Add(candidate.Path);
                    _disguisedVolumePaths.Add(candidate.Path);
                }

                if (info.DigitWidth > DigitWidth)
                {
                    DigitWidth = info.DigitWidth;
                }

                /*
                 * 一个卷号只留第一个 —— **但规范名优先**（用户 2026-09-27 真机）。
                 *
                 * 现场：暂存目录里同时有 `222.zip`（规范名）与 `222.zi删除p`（同一个名字被塞了中文的
                 * **另一个文件**），两个都声称自己是第 1 卷。老写法"先到先得" ⇒ 谁先列到谁当第 1 卷：
                 *   · 认到 `222.zi删除p` ⇒ 组里那一卷名字不标准 ⇒ 整层不定稿（产物落地失败）；
                 *   · 认到 `222.zip` 也不对劲 —— 它会去按体积/位置**推定** `222.zi删除p` 是"缺的第 1 卷"，
                 *     于是同一句话里既说"缺 `222.zip`"、又说那个文件"就是第 1 卷"。
                 * 规范名优先把这一档定死：**名字一字不差的那个才是这一卷**，其余照旧当"没有卷号的候选"，
                 * 由判定器如实报"这一组有同号的两个文件"（⛔ 绝不当成可删过程物 —— 那 25 GB 就是这么没的）。
                 */
                if (!_byIndex.TryGetValue(info.Index, out VolumeCandidate? existing))
                {
                    _byIndex.Add(info.Index, candidate);
                }
                else if (info.HasCanonicalName && !_canonicalIndices.Contains(info.Index))
                {
                    _byIndex[info.Index] = candidate;
                }

                if (info.HasCanonicalName)
                {
                    _canonicalIndices.Add(info.Index);
                }
            }
        }

        /// <summary>解析一个文件名（可以传完整路径，内部只取文件名部分）。不是分卷/本体返回 null。</summary>
        private static VolumeNameInfo? Analyze(string? fileNameOrPath)
        {
            string fileName = FileNameHelper.GetFileName(fileNameOrPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            string[] parts = fileName.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            string last = parts[^1];
            if (last.Length == 0)
            {
                return null;
            }

            // ① <基名>.<分卷段>.rar：分卷段后面还挂着一个 .rar 尾巴（x.part1.rar）。
            //    这里只认 partN 与三位数字两种分卷段 —— x.z01.rar / x.r00.rar 不是真实存在的写法，
            //    与其猜，不如让它落到 ③ 的"普通 rar 本体"分支（那样不会成组）。
            if (parts.Length >= 3
                && last.Equals("rar", StringComparison.OrdinalIgnoreCase)
                && TryParseNumberedSegment(parts[^2], out VolumeFamily tailFamily, out int tailIndex, out int tailWidth))
            {
                string tailBase = JoinBaseName(parts, 2);
                if (tailBase.Length > 0)
                {
                    return new VolumeNameInfo
                    {
                        BaseName = tailBase,
                        Family = tailFamily,
                        Index = tailIndex,
                        DigitWidth = tailWidth,
                        Tail = ".rar"
                    };
                }
            }

            // ② 末尾一段自己就是分卷标记：volume.7z.001 / archive.001 / x.z01 / x.r00 / x.part1。
            //    注意 volume.7z.001.txt 走不到这里（它末段是 txt），这正是要的效果：
            //    改坏后缀的文件不再被当成同一组的分卷。
            //    ⚠ 但"分卷标记后面**粘着**垃圾"的（volume.7z.001删除，百度网盘给每卷缀「删除」）算分卷：
            //    不算的话整组会散成几个独立压缩包，一键处理改名时会把 .001 这一段吃掉（2026-09-28 事故）。
            //    垃圾尾巴记进 Tail，小组时用它补出来的"缺失卷名"才跟磁盘上的名字对得上。
            // 末尾一段是分卷标记：先按已有两档解析；解析不出来再试**容错**档
            // （用户 2026-09-28 第三次真机：`amb909.7sz.00c1` —— 干扰字符塞进卷号内部、而且是字母）。
            // ⛔ 容错档只多认"删少量非纯数字字符后是合法卷标记"，认不出就照旧 null（宁可不动）。
            bool isVolumeSegment = TryParseVolumeSegment(last, out VolumeFamily family, out int index, out int digitWidth, out string junkTail);

            /*
             * 这一段是**靠容错档**（删掉 1~2 个字符）才认出来的（`z0sc1` → `z01`）——
             * 它算分卷成员，但**名字不是规范名**：尺寸规律那一道不许拿它当"满卷"基准
             * （真机 222 那一组里 `222.z0sc1` 与本体等大，拿它当满卷会让整桶被尺寸规律判死）。
             */
            bool volumeSegmentByTolerance = false;

            if (!isVolumeSegment &&
                ExtensionHelper.TrySplitVolumeSegmentTolerant(last, out string tolerantMark, out string tolerantJunk) &&
                TryParseVolumeSegment(tolerantMark, out family, out index, out digitWidth, out _))
            {
                // 容错档的垃圾是**夹在卷号内部**的（`00c1` 的 `c`）—— 补卷名时要用**干净的标准名**
                // （`amb909.7z.001`），所以这里不把垃圾当尾巴传下去（尾巴是给"粘在末尾"那种用的）。
                _ = tolerantJunk;
                junkTail = string.Empty;
                isVolumeSegment = true;
                volumeSegmentByTolerance = true;
            }

            if (isVolumeSegment)
            {
                string baseName = JoinBaseName(parts, 1);
                if (baseName.Length > 0)
                {
                    return new VolumeNameInfo
                    {
                        BaseName = baseName,
                        Family = family,
                        Index = index,
                        DigitWidth = digitWidth,
                        Tail = junkTail,
                        IsDisguised = junkTail.Length > 0 || volumeSegmentByTolerance,
                        HasCanonicalName = junkTail.Length == 0 && !volumeSegmentByTolerance
                    };
                }
            }

            // ②b 卷标记后面还挂着别的点段：volume.7z.001.txt / archive.rar.001.bak
            //     （用户 2026-09-28 追加：不能只认"标记必须是最后一段"）。
            //     尾巴原样记进 Tail，补缺失卷名时才能拼回 `…001.txt` 这个名字。
            if (parts.Length >= 3 && !ExtensionHelper.IsKnownArchiveExtension("." + last))
            {
                for (int i = parts.Length - 2; i >= 1; i--)
                {
                    if (!TryParseVolumeSegment(parts[i], out VolumeFamily tailFamily2, out int tailIndex2, out int tailWidth2, out _))
                    {
                        continue;
                    }

                    /*
                     * ⚠ 与 FileNameHelper 里那条**同一道闸门**（2026-09-28）：只有"卷标记**紧跟在已知压缩后缀后面**"
                     * 才算跨段伪装（`x.7z.001.txt` / `x.rar.001.bak`）。不加这条，`rar-android-722.132.apk`
                     * 会被算成"卷 132 + .apk 尾巴"（`TryGetFirstVolumeName` 推出 `rar-android-722.001.apk`）——
                     * 落点与改名那两条路另有闸门不受影响，但**探测器口径必须一致**，否则迟早再冒出一条边角。
                     */
                    if (i < 1 || !ExtensionHelper.IsKnownArchiveExtension("." + parts[i - 1]))
                    {
                        continue;
                    }
                    /*
                     * ⚠ 只检查**卷标记右边**的那几段，别去查卷标记自己 —— `.001` 本身就在
                     * KnownArchiveExtensions 里（老代码把它当"压缩包后缀"收进去了），
                     * 拿它当判据会把 `.001.txt` 这种刚刚要支持的名字全挡掉（实测踩到过）。
                     */
                    bool tailsArePlain = true;

                    for (int j = i + 1; j < parts.Length; j++)
                    {
                        if (ExtensionHelper.IsKnownArchiveExtension("." + parts[j]))
                        {
                            tailsArePlain = false;
                            break;
                        }
                    }

                    if (!tailsArePlain)
                    {
                        continue;
                    }

                    string tailBase = JoinBaseName(parts, parts.Length - i);
                    if (tailBase.Length == 0)
                    {
                        continue;
                    }

                    return new VolumeNameInfo
                    {
                        BaseName = tailBase,
                        Family = tailFamily2,
                        Index = tailIndex2,
                        DigitWidth = tailWidth2,
                        Tail = "." + string.Join('.', parts, i + 1, parts.Length - i - 1),
                        IsDisguised = true
                    };
                }
            }

            // ③ x.zip / x.rar 本体：单看名字就是本族的第 1 卷（所以 TryGetVolumeIndex 返回 1），
            //    但"算不算分卷组"由 Group() 决定：要有同族的 .z01 / .r00 才算。
            if (last.Equals("zip", StringComparison.OrdinalIgnoreCase) || last.Equals("rar", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = JoinBaseName(parts, 1);
                if (baseName.Length > 0)
                {
                    return new VolumeNameInfo
                    {
                        BaseName = baseName,
                        Family = last.Equals("zip", StringComparison.OrdinalIgnoreCase)
                            ? VolumeFamily.ZipSpanned
                            : VolumeFamily.RarOld,
                        Index = 1,
                        IsBody = true,
                        HasCanonicalName = true
                    };
                }
            }

            /*
             * ③b **本体后缀被塞了垃圾**（`222.zi删除p` / `222.zscip`）—— 用户 2026-09-27 真机。
             *
             * 现场：外层跨盘 zip 解出来的两个条目叫 `222.zi删除p` 与 `222.z0sc1`（网盘把中文塞进后缀），
             * 这一支**以前根本不存在** ⇒ `222.zi删除p` 被判成"一个没有卷标记的普通文件"，
             * 归组时基名算成 `222.zscip`（去掉最后一段），整组改名产出 `222.zscip.zip`，
             * 与归档内部记的 `222.zip` 对不上 ⇒ 定稿闸门判"缺 `222.zip`"、整层作废。
             *
             * 判据只有一条（唯一出口 <see cref="ExtensionHelper.TryRecoverDisguisedArchiveBody"/>）：
             * 去掉最多 2 个非数字字符后**唯一地**变成本族的规范后缀。还原出来的名字照旧算
             * "第 1 卷本体"，但 <see cref="VolumeNameInfo.HasCanonicalName"/> = false ——
             * 它够资格**参与归组**，不够资格被当成"名字标准、可以删"（那一条只由
             * <c>VolumeGroupResolver</c> 按磁盘上的真名字回答）。
             */
            if (!ExtensionHelper.IsVolumeSegment(last) &&
                ExtensionHelper.TryRecoverDisguisedArchiveBody(last, out string bodySuffix, out _) &&
                (bodySuffix.Equals("zip", StringComparison.OrdinalIgnoreCase)
                 || bodySuffix.Equals("rar", StringComparison.OrdinalIgnoreCase)))
            {
                string bodyBaseName = JoinBaseName(parts, 1);

                if (bodyBaseName.Length > 0)
                {
                    return new VolumeNameInfo
                    {
                        BaseName = bodyBaseName,
                        Family = bodySuffix.Equals("zip", StringComparison.OrdinalIgnoreCase)
                            ? VolumeFamily.ZipSpanned
                            : VolumeFamily.RarOld,
                        Index = 1,
                        IsBody = true,
                        IsDisguised = true,
                        IsDisguisedBody = true,
                        HasCanonicalName = false
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// 解析"数字型"分卷段：三位数字（<c>001</c>）或 part + 数字（<c>part1</c> / <c>part01</c>）。
        ///
        /// z/r 记号的段不走这里：<c>x.z01.rar</c> 这种组合不存在真实样本。
        /// </summary>
        private static bool TryParseNumberedSegment(string segment, out VolumeFamily family, out int index, out int digitWidth)
        {
            family = VolumeFamily.Numeric;
            index = 0;
            digitWidth = 1;

            if (!ExtensionHelper.IsVolumePartExtension("." + segment))
            {
                return false;
            }

            if (segment.Length == 3 && IsAsciiDigits(segment.AsSpan()))
            {
                family = VolumeFamily.Numeric;
                digitWidth = 3;
                return TryParseVolumeNumber(segment, out index);
            }

            if (segment.Length >= 5
                && segment.StartsWith("part", StringComparison.OrdinalIgnoreCase)
                && IsAsciiDigits(segment.AsSpan(4)))
            {
                family = VolumeFamily.PartNumbered;
                digitWidth = segment.Length - 4;
                return TryParseVolumeNumber(segment[4..], out index);
            }

            return false;
        }

        /// <summary>
        /// 解析"末尾那一段"属于哪一族、是第几卷，以及**粘在卷号后面的垃圾尾巴**。
        ///
        /// 先用 <see cref="ExtensionHelper.TrySplitVolumeSegment"/> 把段拆成「分卷标记 + 垃圾」：
        /// "什么算分卷标记"只留一份定义，将来它扩了（比如补上新的记法），这里不会漏；
        /// 但它只回答"是不是、垃圾是什么"，"是哪一族、第几卷"还得自己拆 ——
        /// 这才是卷序偏移最容易写错的地方。
        /// </summary>
        private static bool TryParseVolumeSegment(
            string segment,
            out VolumeFamily family,
            out int index,
            out int digitWidth,
            out string junkTail)
        {
            family = VolumeFamily.Numeric;
            index = 0;
            digitWidth = 1;
            junkTail = string.Empty;

            if (!ExtensionHelper.TrySplitVolumeSegmentLoose(segment, out string mark, out string junk))
            {
                return false;
            }

            junkTail = junk;

            // .z01 ~ .z99：zip 分卷的**后续**卷。z01 前面还有本体 x.zip，所以卷序 = NN + 1。
            if (mark.Length == 3
                && (mark[0] == 'z' || mark[0] == 'Z')
                && TryParseTwoDigits(mark.AsSpan(1), out int zipOrdinal)
                && zipOrdinal >= 1)
            {
                family = VolumeFamily.ZipSpanned;
                index = zipOrdinal + 1;
                return true;
            }

            // .r00 ~ .r99：rar 老式分卷的后续卷。r00 是"第 1 个后续卷"、本体 x.rar 才是第 1 卷，
            // 所以卷序 = NN + 2（这里 0 是合法序号，不能像 zip 那样拒掉）。
            if (mark.Length == 3
                && (mark[0] == 'r' || mark[0] == 'R')
                && TryParseTwoDigits(mark.AsSpan(1), out int rarOrdinal))
            {
                family = VolumeFamily.RarOld;
                index = rarOrdinal + 2;
                return true;
            }

            // .001 ~ .999：数字本身就是卷序。
            if (mark.Length == 3 && IsAsciiDigits(mark.AsSpan()))
            {
                if (!TryParseVolumeNumber(mark, out index))
                {
                    return false;
                }

                family = VolumeFamily.Numeric;
                digitWidth = 3;
                return true;
            }

            // .partN / .partNN：卷序就是 N（part1 本身就是第一卷）。
            if (mark.Length >= 5
                && mark.StartsWith("part", StringComparison.OrdinalIgnoreCase)
                && IsAsciiDigits(mark.AsSpan(4)))
            {
                if (!TryParseVolumeNumber(mark[4..], out index))
                {
                    return false;
                }

                family = VolumeFamily.PartNumbered;
                digitWidth = mark.Length - 4;
                return true;
            }

            junkTail = string.Empty;
            return false;
        }

        /// <summary>
        /// 解析卷号。用 <see cref="NumberStyles.None"/> 且要求 ≥ 1：
        /// 卷号只能是纯数字（挡掉 "+1"、"1 "、"١" 这类写法），
        /// 而且 <c>x.000</c> / <c>x.part0</c> 这种没有意义的"第 0 卷"一律不算分卷
        /// —— 认了它们，缺卷检测就会开始报不存在的名字。
        /// </summary>
        private static bool TryParseVolumeNumber(string text, out int index)
        {
            index = 0;

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 1;
        }

        /// <summary>解析恰好两位的十进制序号（<c>z01</c>/<c>r00</c> 的数字部分），允许 0。</summary>
        private static bool TryParseTwoDigits(ReadOnlySpan<char> span, out int value)
        {
            value = 0;

            if (span.Length != 2 || !IsAsciiDigits(span))
            {
                return false;
            }

            return int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// 只认 ASCII 数字。<see cref="char.IsDigit(char)"/> 会放过阿拉伯-印度数字之类的字符，
        /// 那些字符经 int.Parse 又解析不出来，用它当条件会得到"看着像分卷但卷号是 0"的怪结果。
        /// </summary>
        private static bool IsAsciiDigits(ReadOnlySpan<char> span)
        {
            if (span.Length == 0)
            {
                return false;
            }

            foreach (char ch in span)
            {
                if (ch < '0' || ch > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>把末尾 <paramref name="trailingCount"/> 段之外的片段拼回基名。</summary>
        private static string JoinBaseName(string[] parts, int trailingCount)
        {
            int count = parts.Length - trailingCount;
            if (count <= 0)
            {
                return string.Empty;
            }

            string baseName = string.Join(".", parts, 0, count);

            /*
             * ⛔ 剥掉"粘在压缩后缀上的垃圾"（2026-09-28 真机）：
             * `amb909.7z删除.001` 的基名老算法算成 `amb909.7z删除`，而同组的 `amb909.7z.002sc`
             * 算成 `amb909.7z` —— **两个基名不同 → 同一组的两卷被分到两个组**，
             * 于是第二卷自己单干、报"缺第一卷"，一键处理里就出现"跳过但名字确实改了"那种看不懂的场面。
             *
             * 判据很窄：基名的**最后一段**必须以已知压缩后缀开头、后面只跟非字母数字的垃圾
             * （`7z删除` → `7z`）。正常名字（`rar-android-722.132`）不受影响 —— 那一段不是"后缀+垃圾"。
             */
            baseName = StripJunkAfterArchiveExtension(baseName);
            baseName = NormalizeArchiveExtensionSegment(baseName);

            // 基名不 Trim：文件名里的首尾空格是真的会改变归组的字符。
            // 但"全是空白"的基名（例如文件名叫 ".zip"）没有意义，直接不认。
            return string.IsNullOrWhiteSpace(baseName) ? string.Empty : baseName;
        }

        /// <summary>
        /// 把基名最后一段里"夹在压缩后缀**内部**的垃圾"归一：`x.7sz` → `x.7z`、`x.7删z` → `x.7z`。
        ///
        /// <para>为什么需要（用户 2026-09-28 第三次真机）：两卷分别被伪装成 `amb909.7sz.00c1` 与
        /// `amb909.7删z.00除2`，后缀段里各塞了一个字符 → 基名成了 `amb909.7sz` / `amb909.7删z`，
        /// **两个基名不同 → 同一组两卷散成两组**。这里只做"删 1 个字符后是不是已知压缩后缀"，
        /// 候选**唯一**才认（有歧义就不动）。</para>
        /// </summary>
        internal static string NormalizeArchiveExtensionSegment(string baseName)
        {
            int lastDot = baseName.LastIndexOf('.');

            if (lastDot <= 0 || lastDot == baseName.Length - 1)
            {
                return baseName;
            }

            string segment = baseName[(lastDot + 1)..];

            if (ExtensionHelper.IsKnownArchiveExtension("." + segment))
            {
                return baseName;
            }

            string? unique = null;

            for (int i = 0; i < segment.Length; i++)
            {
                string candidate = segment.Remove(i, 1);

                if (!ExtensionHelper.IsKnownArchiveExtension("." + candidate))
                {
                    continue;
                }

                if (unique != null)
                {
                    return baseName; // 有歧义：宁可不归一
                }

                unique = candidate;
            }

            return unique == null ? baseName : baseName[..(lastDot + 1)] + unique;
        }

        /// <summary>
        /// 把"压缩后缀后面粘着垃圾"的最后一段清干净：<c>x.7z删除</c> → <c>x.7z</c>；<c>y.rar副本</c> → <c>y.rar</c>。
        /// 段里没有已知压缩后缀、或后缀后面还跟着字母数字的，一律原样返回（宁可不动，也不乱剪）。
        /// </summary>
        private static string StripJunkAfterArchiveExtension(string baseName)
        {
            int lastDot = baseName.LastIndexOf('.');

            if (lastDot <= 0 || lastDot == baseName.Length - 1)
            {
                return baseName;
            }

            string segment = baseName[(lastDot + 1)..];
            int letterCount = 0;

            while (letterCount < segment.Length && char.IsAsciiLetterOrDigit(segment[letterCount]))
            {
                letterCount++;
            }

            if (letterCount == 0 || letterCount == segment.Length)
            {
                // 整段都是字母数字（`7z` / `132`）：要么本来就是干净后缀，要么根本不是"后缀+垃圾"
                return baseName;
            }

            string extension = "." + segment[..letterCount];

            if (!ExtensionHelper.IsKnownArchiveExtension(extension) &&
                !ExtensionHelper.IsVolumePartExtension(extension))
            {
                return baseName;
            }

            return baseName[..(lastDot + 1 + letterCount)];
        }

        private static VolumeGroup BuildGroup(VolumeBucket bucket, Dictionary<string, int> familyCounts)
        {
            List<int> indices = bucket.SortedIndices;
            List<VolumeCandidate> volumes = indices.Select(i => bucket[i]).ToList();

            string baseKey = BuildBaseKey(bucket.DirectoryPath, bucket.BaseName);
            string groupKey = familyCounts.TryGetValue(baseKey, out int sameBaseFamilyCount) && sameBaseFamilyCount > 1
                ? baseKey + "|" + FamilyToken(bucket.Family)
                : baseKey;

            var naming = new VolumeNameInfo
            {
                BaseName = bucket.BaseName,
                Family = bucket.Family,
                Tail = bucket.Tail,
                DigitWidth = bucket.DigitWidth
            };

            // 缺号只需要在 [1, 最大已找到卷号] 里找：再往后有没有卷，命名里没有答案。
            var missing = new List<string>();
            bool hasUnnamableHole = false;

            for (int index = 1; index < indices[^1]; index++)
            {
                if (bucket.ContainsIndex(index))
                {
                    continue;
                }

                if (naming.TryFormat(index, out string missingName))
                {
                    missing.Add(missingName);
                }
                else
                {
                    hasUnnamableHole = true;
                }
            }

            bool startsAtFirstVolume = indices[0] == 1;
            bool isComplete = startsAtFirstVolume && missing.Count == 0 && !hasUnnamableHole;

            return new VolumeGroup
            {
                GroupKey = groupKey,
                DirectoryPath = bucket.DirectoryPath,
                BaseName = bucket.BaseName,
                Volumes = volumes,
                FirstVolumePath = volumes[0].Path,
                KnownVolumeCount = volumes.Count,
                ExpectedVolumeCount = 0,
                IsComplete = isComplete,
                MissingVolumeNames = missing,
                Note = BuildNote(bucket, volumes, missing, startsAtFirstVolume, hasUnnamableHole)
            };
        }

        /// <summary>
        /// 给用户的一句话：先说"总数不知道"，再说找到几卷 / 缺哪几卷，
        /// 最后在"只能用 .z01 / .r00 启动"这种危险情况下额外点名 —— 别指望用户自己去读字段。
        /// </summary>
        private static string BuildNote(
            VolumeBucket bucket,
            IReadOnlyList<VolumeCandidate> volumes,
            IReadOnlyList<string> missing,
            bool startsAtFirstVolume,
            bool hasUnnamableHole)
        {
            var note = new StringBuilder();

            note.Append("无法确定分卷总数，");

            if (volumes.Count == 1)
            {
                note.Append("只找到 1 卷，可能不完整");
            }
            else
            {
                note.Append("按顺序找到 ").Append(volumes.Count).Append(" 卷");
            }

            if (missing.Count > 0)
            {
                note.Append("，缺 ").Append(missing.Count).Append(" 卷：");
                note.Append(string.Join("、", missing.Take(MaxMissingNamesInNote)));

                if (missing.Count > MaxMissingNamesInNote)
                {
                    note.Append(" 等");
                }
            }

            if (hasUnnamableHole)
            {
                note.Append("；另有缺号超出该命名的编号上限，无法给出文件名");
            }

            // 本体缺失：x.zip / x.rar 才是第 1 卷，没有它整组解不开，必须说清"现在会从哪一卷起手"。
            if (!startsAtFirstVolume
                && (bucket.Family == VolumeFamily.ZipSpanned || bucket.Family == VolumeFamily.RarOld))
            {
                string bodyName = bucket.Family == VolumeFamily.ZipSpanned
                    ? bucket.BaseName + ".zip"
                    : bucket.BaseName + ".rar";

                note.Append("；没找到第 1 卷本体 ").Append(bodyName)
                    .Append("，只能从 ").Append(FileNameHelper.GetFileName(volumes[0].Path))
                    .Append(" 起手");
            }

            return note.ToString();
        }

        /// <summary>GroupKey 的前半段：目录 + 基名（目录为空时只有基名）。Windows 文件名不含 <c>|</c>，不会歧义。</summary>
        private static string BuildBaseKey(string directoryPath, string baseName)
        {
            return string.IsNullOrEmpty(directoryPath) ? baseName : directoryPath + "|" + baseName;
        }

        private static string FamilyToken(VolumeFamily family)
        {
            switch (family)
            {
                case VolumeFamily.Numeric:
                    return "num";
                case VolumeFamily.ZipSpanned:
                    return "z";
                case VolumeFamily.RarOld:
                    return "r";
                default:
                    return "part";
            }
        }
    }
}
