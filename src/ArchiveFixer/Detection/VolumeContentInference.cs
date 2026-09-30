using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Detection
{
    /// <summary>文件头能直接回答的那几种格式（只读前几个字节，不解析归档）。</summary>
    public enum VolumeContentFormat
    {
        Unknown,

        /// <summary>7z：魔数 <c>37 7A BC AF 27 1C</c>（**只有第一卷**有；后续卷是裸字节流）。</summary>
        SevenZip,

        /// <summary>RAR：<c>Rar!\x1A\x07\x00</c>（RAR4）/ <c>Rar!\x1A\x07\x01\x00</c>（RAR5）。</summary>
        Rar,

        /// <summary>ZIP 家族本地头 <c>PK\x03\x04</c>（空归档是 <c>PK\x05\x06</c>）。</summary>
        Zip
    }

    /// <summary>
    /// **内容级**分卷推断：名字靠不住时，只拿"文件内容 + 同目录尺寸"这两条与名字无关的事实，
    /// 推出一组卷的候选与假设顺序。
    ///
    /// <para><b>为什么需要它</b>（用户 2026-09-28 真机）：他把三个 7z 卷的名字改成了
    /// <c>amb909.7.01</c> / <c>amb909.z.2</c> / <c>amb909..3</c> —— 卷号被改烂、后缀也不对，
    /// 任何**按名字**的判据都认不出它们是一组（第一卷既不叫 <c>.7z</c> 也不叫 <c>.001</c>）。
    /// 但内容没有骗人：第一卷开头就是 7z 魔数，且它自己单独打不开（说明后面还有卷）。</para>
    ///
    /// <para><b>物理事实（⛔ 别指望更多）</b>：7z 的 <c>-v</c> 分卷**内容里没有任何卷号标记** ——
    /// <c>.001</c> 是正常 7z 头，之后是裸字节流，所以"从内容直接读出第几卷"在 7z 上**不可能**。
    /// 本类只做能做的四件事：① 用内容认出"这是 7z 的第一卷"；② 用同目录**尺寸**排出候选顺序；
    /// ③ 名字里那个"短数字尾巴"（<c>.2</c>）只用来**排序 / 当放行的第二张门票**；
    /// ④ 把顺序交给 <c>VolumeProbeVerifier</c> 用引擎**试开**验证（验证通过才算数）。</para>
    ///
    /// <para><b>两卷形状（用户 2026-09-29 第二次真机）</b>：现场是 <c>amb909.7.01</c>（正好 2 GiB，
    /// 带 7z 魔数）+ <c>amb909.z.2</c>（1.89 GB，认不出格式 = 裸的续卷），程序却报
    /// 「同目录里也没有找到像后续卷的文件」—— 因为"尺寸规律"要的是"有与第一卷等长的文件"，
    /// 而两卷时第二卷就是余量、必然更短。这一档现在由 <see cref="HasTwoVolumeShapeEvidence"/>
    /// 放过闸门（名字的尾巴接得上 / 第一卷是整数 MiB），是不是真的一组仍旧只由试开回答。</para>
    ///
    /// <para>本类是**纯逻辑**：只读文件头（6 字节）、只做算术与排序，不建目录、不改名字、不调引擎。</para>
    /// </summary>
    public static class VolumeContentInference
    {
        /// <summary>
        /// 试开用的工作目录名（与工作区根同名）：⛔ 必须与第一卷**同卷**，
        /// 跨卷建目录就没法做硬链接（硬链接不能跨卷），那就只能复制 GB 级文件 —— 绝对不行。
        /// </summary>
        public const string WorkDirectoryName = ".ArchiveFixer.work";

        /// <summary>试开用的硬链接基名：7-Zip 只认"基名 + .001/.002"这套名，基名叫什么它不在乎（实测）。</summary>
        private const string ProbeBaseName = "volprobe.7z";

        /// <summary>排列数量上限：6 个候选的全排列是 720，每个排列都要真的起一次 7z，全试完要几分钟。</summary>
        public const int MaxOrderings = 24;

        /// <summary>
        /// 读文件头判断格式。读不出来（不存在 / 被占 / 权限）一律 <see cref="VolumeContentFormat.Unknown"/>，**绝不抛**。
        /// </summary>
        public static VolumeContentFormat SniffFormat(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return VolumeContentFormat.Unknown;
            }

            try
            {
                using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                Span<byte> head = stackalloc byte[8];
                int read = stream.Read(head);

                if (read >= 6
                    && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC
                    && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
                {
                    return VolumeContentFormat.SevenZip;
                }

                if (read >= 7
                    && head[0] == (byte)'R' && head[1] == (byte)'a' && head[2] == (byte)'r'
                    && head[3] == (byte)'!' && head[4] == 0x1A && head[5] == 0x07)
                {
                    return VolumeContentFormat.Rar;
                }

                if (read >= 4
                    && head[0] == (byte)'P' && head[1] == (byte)'K'
                    && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
                {
                    return VolumeContentFormat.Zip;
                }
            }
            catch
            {
                // 读不到头部就是"看不出来"：调用方会退回原来的按名字行为，不会有副作用。
            }

            return VolumeContentFormat.Unknown;
        }

        /// <summary>内容对应的标准后缀（不含点）；认不出来返回空串。</summary>
        public static string ExtensionFor(VolumeContentFormat format)
        {
            switch (format)
            {
                case VolumeContentFormat.SevenZip:
                    return "7z";
                case VolumeContentFormat.Rar:
                    return "rar";
                case VolumeContentFormat.Zip:
                    return "zip";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// 候选 = 同目录里除第一卷外"大小 ≤ 第一卷大小"的文件，按大小降序、同大小按名字升序。
        ///
        /// <para>为什么是"≤ 第一卷"：分卷除最后一卷外都是**等长满卷**，最后一卷更小或相等
        /// （<c>-v</c> 的切法就是这样）。比第一卷还大的文件**不可能是**同一组的后续卷，
        /// 直接排除能挡掉目录里绝大多数无关文件。⛔ 这只是"候选"，是不是真的成一组由试开决定。</para>
        /// </summary>
        public static IReadOnlyList<VolumeCandidate> BuildCandidates(
            string? firstVolumePath,
            IEnumerable<VolumeCandidate>? filesInDirectory)
        {
            if (string.IsNullOrWhiteSpace(firstVolumePath) || filesInDirectory == null)
            {
                return Array.Empty<VolumeCandidate>();
            }

            long firstSize = SizeOf(firstVolumePath);

            if (firstSize <= 0)
            {
                return Array.Empty<VolumeCandidate>();
            }

            string selfName = SafeFileName(firstVolumePath);

            return filesInDirectory
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
                .Where(c => !string.Equals(SafeFileName(c.Path), selfName, StringComparison.OrdinalIgnoreCase))
                .Where(c => c.Size > 0 && c.Size <= firstSize)

                /*
                 * 「不是**已识别的**归档」（用户 2026-09-29 原话："候选从同目录里、**不是已识别的归档**、
                 * 不是无用物（说明文件/样本图等）的文件里取"）。
                 *
                 * 这一条是**物理事实**，不是口味：7z 的 `-v` 分卷里，**只有第一卷有魔数**，
                 * 之后的卷都是裸字节流（见类注释）。所以一个"认得出格式"的文件（自己就是 7z / RAR / zip
                 * 头）绝不可能是另一组的续卷 —— 它是另一个独立的包。把它留在候选里只会拿它去试开、
                 * 白烧一次引擎调用；真正的续卷恰恰是**认不出格式**的那些（用户那句
                 * "不要把同目录里认不出格式的文件当无关物"说的就是它们）。
                 *
                 * ⚠ 顺序有讲究：先按下限（size ≤ 第一卷）筛，再读文件头 ——
                 * 比第一卷还大的文件连打开都不用打开。
                 */
                .Where(c => SniffFormat(c.Path) == VolumeContentFormat.Unknown)
                .OrderByDescending(c => c.Size)
                .ThenBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 名字里的**短数字尾巴**（最后一段是 1~3 位纯数字）：<c>amb909.7.01</c> → 1、
        /// <c>amb909.z.2</c> → 2、<c>amb909.7z.003</c> → 3；认不出来返回 null。
        ///
        /// <para><b>为什么加上它</b>（用户 2026-09-29 原话："我还说了，可以靠后缀数字 2 的情况猜一猜是第二卷"）：
        /// 7z 的分卷内容里没有卷号，而**两卷**时"尺寸规律"必然给不出证据（第一卷满片、第二卷是余量，
        /// 没有任何一卷与第一卷等长）—— 那一刻名字里的这个数字是唯一的线索。</para>
        ///
        /// <para>⛔ 它**只用来排序、并且只当"值得试开一次"的第二张门票**，绝不用来判"这是不是分卷"：
        /// 是不是仍然只由"内容 + 硬链接试开"回答。位宽照旧只认 1~3 位纯数字
        /// （<c>0012</c> 这种更像另一套位宽，不猜 —— 与 <c>ExtensionHelper</c> 的口径一致）。</para>
        /// </summary>
        public static int? TryReadShortNumberTail(string? path)
        {
            string name = SafeFileName(path);

            int dot = name.LastIndexOf('.');

            if (dot <= 0 || dot == name.Length - 1)
            {
                return null;
            }

            string tail = name[(dot + 1)..];

            if (tail.Length is < 1 or > 3)
            {
                return null;
            }

            foreach (char c in tail)
            {
                if (!char.IsAsciiDigit(c))
                {
                    return null;
                }
            }

            return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                   && value is >= 1 and <= 999
                ? value
                : null;
        }

        /// <summary>
        /// 尺寸规律（三层证据里的第 2 层，与名字无关）：同目录里**至少有一个与第一卷等长的文件**。
        ///
        /// <para>为什么是"至少一个等长"而不是"所有候选都等长"：真实目录里除了这一组卷，常常还躺着
        /// 别的杂七杂八的小文件（说明、封面、另一个包……），要求"全部都等长"会把正常情形也否掉。
        /// 等长本身就是很强的证据（<c>-v</c> 切出来的卷除最后一卷外**必然**一模一样大），
        /// 足够当"值得试开一次"的门槛 —— 真正的结论仍然由试开给。</para>
        /// </summary>
        public static bool HasVolumeSizePattern(string? firstVolumePath, IReadOnlyList<VolumeCandidate>? candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return false;
            }

            long firstSize = SizeOf(firstVolumePath);

            return firstSize > 0 && candidates.Any(c => c.Size == firstSize);
        }

        /// <summary>
        /// **「两卷形状」这张门票**（用户 2026-09-29 现场：<c>amb909.7.01</c> 正好 2 GiB、
        /// <c>amb909.z.2</c> 1.89 GB —— 一组两卷的包，能救却救不回来）。
        ///
        /// <para><b>为什么必须放宽到这一档</b>：<see cref="HasVolumeSizePattern"/> 要的是"有与第一卷等长的文件"，
        /// 而**两卷**这一组里第二卷就是余量、必然更短 —— 那条规律**永远**给不出证据。
        /// 结果就是最常见的两卷切法反而被挡在门外（老行为：报「同目录里也没有找到像后续卷的文件」，
        /// 而兄弟卷就躺在同一个目录里）。</para>
        ///
        /// <para><b>两张门票，满足其一即可</b>（⛔ 都只是"值得试开一次"的门槛，成不成立仍然只由
        /// <see cref="VolumeProbeVerifier"/> 的硬链接试开回答；试不出就什么都不做）：</para>
        /// <list type="number">
        /// <item><description><b>名字里的短数字尾巴接得上</b>：第一卷有尾巴时要求"候选 = 它 + 1"
        /// （<c>.01</c> → <c>.2</c>），第一卷没尾巴时要求候选的号 ≥ 2（用户原话："可以靠后缀数字
        /// 2 的情况猜一猜是第二卷"）；</description></item>
        /// <item><description><b>体积规律</b>：第一卷是**整数 MiB** —— 7z 的切分上限永远是整数 MiB
        /// （<c>-v1m</c> / <c>-v2g</c> / <c>-v100m</c>），所以"它是满片、这个更短的是末卷"在体积上说得通
        /// （用户原话："对不上再用体积规律（7z <c>-v</c> 的除末卷外都是满片：正好等于切分上限，例如 2 GiB）"）。
        /// 这只是**放行的第二张门票**，不是结论。</description></item>
        /// </list>
        /// </summary>
        public static bool HasTwoVolumeShapeEvidence(
            string? firstVolumePath,
            IReadOnlyList<VolumeCandidate>? candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return false;
            }

            long firstSize = SizeOf(firstVolumePath);

            if (firstSize <= 0)
            {
                return false;
            }

            // 有等长的 → 走的是"尺寸规律"那张门票，本张不必出手（调用方按 或 组合两张）。
            if (candidates.Any(c => c.Size == firstSize))
            {
                return false;
            }

            VolumeCandidate? partial = LargestShorter(firstVolumePath, candidates);

            if (partial == null)
            {
                return false;
            }

            int? firstTail = TryReadShortNumberTail(firstVolumePath);
            int? partialTail = TryReadShortNumberTail(partial.Path);

            if (partialTail is int number
                && number >= 2
                && (firstTail == null || firstTail.Value == number - 1))
            {
                return true;
            }

            // 体积规律：切分上限永远是整数 MiB，"第一卷正好整 MiB"= 它在体积上像个满片。
            const long MiB = 1024 * 1024;

            return firstSize >= MiB && firstSize % MiB == 0;
        }

        /// <summary>
        /// 候选的**假设顺序**（第一个元素是第 2 卷，依此类推）。
        ///
        /// <para>只拿两类候选参与：**与第一卷等长的**（真正的满卷）与**最大的那一个更短的**
        /// （体积规律上它才可能是最后一卷；目录里别的小文件与这一组无关，不拉进来添乱）。
        /// 排序用的一条物理事实：最后一卷**必然排最后**，所以只需要排列满卷那一批 ——
        /// 这一条把 720 种砍到通常只有几种。</para>
        ///
        /// <para><b>两处放宽（用户 2026-09-29）</b>：</para>
        /// <list type="number">
        /// <item><description><b>名字里的短数字尾巴优先</b>（他原话："可以靠后缀数字 2 的情况猜一猜是第二卷"）：
        /// 满卷那一批全都有尾巴且互不相同时，**第一个**尝试的顺序就按号排（<c>.2</c> → <c>.3</c>），
        /// 老的全排列照旧跟在后面兜底 —— 试开一次就成立时不必再烧后面那些。</description></item>
        /// <item><description><b>一个等长的都没有时也给出顺序</b>：那正是"第一卷满片 + 末卷是余量"的两卷形状
        /// （<c>-v2g</c> 切两个 2 GiB 级文件的现场）。这时唯一的假设顺序就是"第一卷 + 那个更短的"。
        /// ⛔ 闸门在调用方（<see cref="HasVolumeSizePattern"/> / <see cref="HasTwoVolumeShapeEvidence"/>
        /// 两张门票），这里只负责"假设"，成不成立由试开回答。</description></item>
        /// </list>
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<VolumeCandidate>> BuildOrderings(
            string? firstVolumePath,
            IReadOnlyList<VolumeCandidate>? candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return Array.Empty<IReadOnlyList<VolumeCandidate>>();
            }

            long firstSize = SizeOf(firstVolumePath);

            if (firstSize <= 0)
            {
                return Array.Empty<IReadOnlyList<VolumeCandidate>>();
            }

            List<VolumeCandidate> full = candidates.Where(c => c.Size == firstSize).ToList();

            VolumeCandidate? partial = LargestShorter(firstVolumePath, candidates);

            if (full.Count == 0)
            {
                return partial == null
                    ? Array.Empty<IReadOnlyList<VolumeCandidate>>()
                    : new IReadOnlyList<VolumeCandidate>[] { new[] { partial } };
            }

            var orderings = new List<IReadOnlyList<VolumeCandidate>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Emit(List<VolumeCandidate> ordered)
            {
                if (partial != null)
                {
                    ordered = new List<VolumeCandidate>(ordered) { partial };
                }

                string key = string.Join("|", ordered.Select(c => SafeFileName(c.Path)));

                if (seen.Add(key) && orderings.Count < MaxOrderings)
                {
                    orderings.Add(ordered);
                }
            }

            /*
             * ① 名字里的短数字尾巴接得上 → 先按号排（"数字对得上就直接按号排"）。
             * 要求"全都有尾巴且互不相同"：缺一个或撞号就没有"按号排"可言，交给老的全排列。
             */
            var tails = full.Select(c => TryReadShortNumberTail(c.Path)).ToList();

            if (tails.All(t => t != null) && tails.Distinct().Count() == tails.Count)
            {
                Emit(full
                    .OrderBy(c => TryReadShortNumberTail(c.Path) ?? int.MaxValue)
                    .ThenBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
                    .ToList());
            }

            // ② 兜底：按名字升序打头的老全排列（大小完全一样时名字是唯一的线索），上限不变。
            foreach (List<VolumeCandidate> arrangement in Permutations(full, MaxOrderings))
            {
                Emit(arrangement);

                if (orderings.Count >= MaxOrderings)
                {
                    break;
                }
            }

            return orderings;
        }

        /// <summary>
        /// 试开目录：第一卷**所在卷根**下的 <c>.ArchiveFixer.work\volprobe-&lt;guid&gt;</c>；拿不到卷根返回空串。
        /// </summary>
        public static string BuildProbeRoot(string? firstVolumePath) => BuildProbeRoot(firstVolumePath, null);

        /// <summary>
        /// 试开目录，**优先落在调用方给的工作区根**下（用户 2026-09-30 口径：临时物只准落
        /// <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）。
        ///
        /// <para>⛔ <b>硬链接不能跨卷</b>：给的工作区根与第一卷不在同一个卷上时，这里**如实退到**
        /// 第一卷所在卷根下的 <c>.ArchiveFixer.work</c>（{0} 的那条老路）—— 退档的原因由调用方
        /// 写进结论，⛔ 不许悄悄复制大文件去凑（一卷 2 GiB，复制一组要几十 GiB 和几分钟）。</para>
        /// </summary>
        /// <param name="firstVolumePath">第一卷（决定"必须同卷"的那个卷是哪一个）。</param>
        /// <param name="preferredWorkRoot">
        /// 期望的落点（通常是 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）。空 = 直接用卷根那一档。
        /// </param>
        public static string BuildProbeRoot(string? firstVolumePath, string? preferredWorkRoot)
        {
            try
            {
                string full = Path.GetFullPath(firstVolumePath ?? string.Empty);
                string root = Path.GetPathRoot(full) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(root))
                {
                    return string.Empty;
                }

                string suffix = "volprobe-" + Guid.NewGuid().ToString("N");

                if (!string.IsNullOrWhiteSpace(preferredWorkRoot))
                {
                    string preferredFull = Path.GetFullPath(preferredWorkRoot.Trim());

                    if (string.Equals(
                        Path.GetPathRoot(preferredFull),
                        root,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return Path.Combine(preferredFull, suffix);
                    }
                }

                return Path.Combine(root, WorkDirectoryName, suffix);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>试开时第 <paramref name="index"/>（1 起）卷的假设名。</summary>
        public static string ProbeFileName(int index) =>
            ProbeBaseName + "." + index.ToString("D3", CultureInfo.InvariantCulture);

        /// <summary>
        /// 候选里"最像末卷"的那一个：更短的里面**最大的**（并列时名字升序）。
        ///
        /// <para>为什么只认这一个：<c>-v</c> 切出来的末卷**必然比前面的卷短**，而目录里别的更短文件
        /// （说明、封面、另一个小包）与这一组无关。这一条判据只有这一处实现 ——
        /// "谁可能是末卷"在假设顺序与两张门票里必须是同一个答案。</para>
        /// </summary>
        private static VolumeCandidate? LargestShorter(string? firstVolumePath, IReadOnlyList<VolumeCandidate> candidates)
        {
            long firstSize = SizeOf(firstVolumePath);

            return candidates
                .Where(c => c.Size > 0 && c.Size < firstSize)
                .OrderByDescending(c => c.Size)
                .ThenBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        /// <summary>满卷那一批的全排列（最多 <paramref name="limit"/> 个），按名字升序打头。</summary>
        private static IEnumerable<List<VolumeCandidate>> Permutations(List<VolumeCandidate> items, int limit)
        {
            var sorted = items.OrderBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase).ToList();
            var current = new List<VolumeCandidate>();
            var used = new bool[sorted.Count];
            int emitted = 0;

            IEnumerable<List<VolumeCandidate>> Walk()
            {
                if (current.Count == sorted.Count)
                {
                    emitted++;
                    yield return new List<VolumeCandidate>(current);
                    yield break;
                }

                for (int i = 0; i < sorted.Count; i++)
                {
                    if (used[i] || emitted >= limit)
                    {
                        continue;
                    }

                    used[i] = true;
                    current.Add(sorted[i]);

                    foreach (List<VolumeCandidate> result in Walk())
                    {
                        yield return result;
                    }

                    current.RemoveAt(current.Count - 1);
                    used[i] = false;
                }
            }

            return Walk();
        }

        private static long SizeOf(string? path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? 0 : new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static string SafeFileName(string? path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
