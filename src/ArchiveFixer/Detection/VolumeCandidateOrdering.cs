using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 卷号推断 / 假设顺序（第二层证据）：同目录里哪些文件**可能**是同一组卷、它们按什么顺序试。
///
/// <para><b>物理事实（⛔ 别指望更多）</b>：7z 的 <c>-v</c> 分卷**内容里没有任何卷号标记** ——
/// <c>.001</c> 是正常 7z 头，之后是裸字节流，所以"从内容直接读出第几卷"在 7z 上**不可能**。
/// 本类只做能做的三件事：① 用同目录**尺寸**排出候选；② 名字里那个"短数字尾巴"（<c>.2</c>）
/// 只用来**排序**（放行那一侧的"第二张门票"在 <see cref="VolumeShapeAdmission"/> 里用到它）；
/// ③ 排出一批"假设顺序"，交给 <c>VolumeProbeVerifier</c> 用引擎**试开**验证（验证通过才算数）。</para>
///
/// <para>⛔ 这里给出的顺序**只是假设**：本类不试开、不建目录、不改名字、不调引擎；
/// 是不是真的一组、顺序对不对，只由试开回答。</para>
///
/// <para><b>失败模型</b>：拿不到第一卷大小 / 候选为空 ⇒ 返回空集合，绝不抛；
/// 单个文件读不到大小（<c>0</c>）⇒ 它不进候选池。</para>
///
/// <para>生命周期：纯静态、无状态；输入是"第一卷路径 + 一份路径/大小清单"，输出与输入一一对应。</para>
/// </summary>
internal static class VolumeCandidateOrdering
{
    /// <summary>排列数量上限：6 个候选的全排列是 720，每个排列都要真的起一次 7z，全试完要几分钟。</summary>
    public const int MaxOrderings = 24;

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

        long firstSize = VolumeFileFacts.SizeOf(firstVolumePath);

        if (firstSize <= 0)
        {
            return Array.Empty<VolumeCandidate>();
        }

        string selfName = VolumeFileFacts.SafeFileName(firstVolumePath);

        return filesInDirectory
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
            .Where(c => !string.Equals(VolumeFileFacts.SafeFileName(c.Path), selfName, StringComparison.OrdinalIgnoreCase))
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
            .Where(c => VolumeContentSniffer.SniffFormat(c.Path) == VolumeContentFormat.Unknown)
            .OrderByDescending(c => c.Size)
            .ThenBy(c => VolumeFileFacts.SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
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
        string name = VolumeFileFacts.SafeFileName(path);

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
    /// ⛔ 闸门在调用方（<see cref="VolumeShapeAdmission.HasVolumeSizePattern"/> /
    /// <see cref="VolumeShapeAdmission.HasTwoVolumeShapeEvidence"/> 两张门票），这里只负责"假设"，
    /// 成不成立由试开回答。</description></item>
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

        long firstSize = VolumeFileFacts.SizeOf(firstVolumePath);

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

            string key = string.Join("|", ordered.Select(c => VolumeFileFacts.SafeFileName(c.Path)));

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
                .ThenBy(c => VolumeFileFacts.SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
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
    /// 枚举**一个目录**里的"路径 + 大小"（读不了就返回空，**绝不抛**）。
    ///
    /// <para>为什么单独留一个"收目录"的入口：分卷组的候选既要能按"某一卷所在目录"枚举，
    /// 也要能按"归档目录的**子目录**"枚举（用户 2026-10-01 点名要的跨目录找卷）——
    /// 两处必须走**同一份**实现，否则"同目录能找、子目录找不到"这种漂移迟早冒出来。</para>
    /// </summary>
    public static IReadOnlyList<VolumeCandidate> EnumerateCandidatesIn(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<VolumeCandidate>();
            }

            var candidates = new List<VolumeCandidate>();

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                long size;

                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    size = -1;
                }

                candidates.Add(new VolumeCandidate { Path = file, Size = size });
            }

            return candidates;
        }
        catch
        {
            return Array.Empty<VolumeCandidate>();
        }
    }

    /// <summary>
    /// 候选里"最像末卷"的那一个：更短的里面**最大的**（并列时名字升序）。
    ///
    /// <para>为什么只认这一个：<c>-v</c> 切出来的末卷**必然比前面的卷短**，而目录里别的更短文件
    /// （说明、封面、另一个小包）与这一组无关。这一条判据只有这一处实现 ——
    /// "谁可能是末卷"在假设顺序与两张门票里必须是同一个答案。</para>
    /// </summary>
    public static VolumeCandidate? LargestShorter(string? firstVolumePath, IReadOnlyList<VolumeCandidate> candidates)
    {
        long firstSize = VolumeFileFacts.SizeOf(firstVolumePath);

        return candidates
            .Where(c => c.Size > 0 && c.Size < firstSize)
            .OrderByDescending(c => c.Size)
            .ThenBy(c => VolumeFileFacts.SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>满卷那一批的全排列（最多 <paramref name="limit"/> 个），按名字升序打头。</summary>
    private static IEnumerable<List<VolumeCandidate>> Permutations(List<VolumeCandidate> items, int limit)
    {
        var sorted = items.OrderBy(c => VolumeFileFacts.SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase).ToList();
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
}
