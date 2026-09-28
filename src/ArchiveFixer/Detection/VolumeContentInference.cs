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
    /// 本类只做能做的三件事：① 用内容认出"这是 7z 的第一卷"；② 用同目录**尺寸**排出候选顺序；
    /// ③ 把顺序交给 <c>VolumeProbeVerifier</c> 用引擎**试开**验证（验证通过才算数）。</para>
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
                .OrderByDescending(c => c.Size)
                .ThenBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();
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
        /// 候选的**假设顺序**（第一个元素是第 2 卷，依此类推）。
        ///
        /// <para>只拿两类候选参与：**与第一卷等长的**（真正的满卷）与**最大的那一个更短的**
        /// （体积规律上它才可能是最后一卷；目录里别的小文件与这一组无关，不拉进来添乱）。
        /// 排序用的一条物理事实：最后一卷**必然排最后**，所以只需要排列满卷那一批 ——
        /// 这一条把 720 种砍到通常只有几种。满卷之间大小完全一样，名字是唯一的线索，
        /// 所以按名字升序打头、再逐个换位。</para>
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

            var full = candidates
                .Where(c => c.Size == firstSize)
                .OrderBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 只有"最大的那个更短的文件"才可能是最后一卷；其余更短的一律当无关文件。
            VolumeCandidate? partial = candidates
                .Where(c => c.Size < firstSize)
                .OrderByDescending(c => c.Size)
                .ThenBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (full.Count == 0)
            {
                return Array.Empty<IReadOnlyList<VolumeCandidate>>();
            }

            var orderings = new List<IReadOnlyList<VolumeCandidate>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (List<VolumeCandidate> arrangement in Permutations(full, MaxOrderings))
            {
                var ordered = new List<VolumeCandidate>(arrangement);

                if (partial != null)
                {
                    ordered.Add(partial);
                }

                string key = string.Join("|", ordered.Select(c => SafeFileName(c.Path)));

                if (seen.Add(key))
                {
                    orderings.Add(ordered);
                }

                if (orderings.Count >= MaxOrderings)
                {
                    break;
                }
            }

            return orderings;
        }

        /// <summary>
        /// 由第一卷推出"这一组改名后该叫什么"的基名。
        ///
        /// <para>两步，都是**保守**的：</para>
        /// <list type="number">
        /// <item><description>末段是 1~4 位纯数字（<c>amb909.7.01</c> 的 <c>01</c>）就当它是被改烂的卷号，去掉；</description></item>
        /// <item><description>剩下的基名末段如果能**只差一个字符**变成内容对应的后缀（<c>amb909.7</c> → <c>amb909.7z</c>：
        /// 差一个 <c>z</c>），就用它 —— 这正是用户现场那个名字的来历；改不动就把后缀**接在后面**
        /// （<c>mystery.bin</c> → <c>mystery.bin.7z</c>），绝不硬猜。</description></item>
        /// </list>
        ///
        /// <para>⛔ 基名怎么取**不影响正确性**：它只是试开时用的名字，改名之前一定会先试开验证；
        /// 取这个名字只是为了让改完的名字像人写的（<c>amb909.7z.001</c> 而不是 <c>volprobe.7z.001</c>）。</para>
        /// </summary>
        public static bool TryDeriveBaseName(string? firstVolumePath, VolumeContentFormat format, out string baseName)
        {
            baseName = string.Empty;

            string fileName = SafeFileName(firstVolumePath);

            if (fileName.Length == 0)
            {
                return false;
            }

            string extension = ExtensionFor(format);

            if (extension.Length == 0)
            {
                return false;
            }

            string[] parts = fileName.Split('.');
            int keep = parts.Length;

            if (keep >= 2 && IsOrdinalSegment(parts[^1]))
            {
                keep--;
            }

            string stem = keep > 0 ? string.Join('.', parts, 0, keep) : fileName;

            if (stem.Length == 0)
            {
                return false;
            }

            baseName = NormalizeExtensionSegment(stem, extension);

            return !string.IsNullOrWhiteSpace(baseName) && baseName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>一组卷改名后的完整文件名清单：<c>基名.001</c> / <c>.002</c>……（与 <c>VolumeGroupDetector</c> 的数字族口径一致）。</summary>
        public static IReadOnlyList<string> BuildStandardFileNames(string? baseName, int volumeCount)
        {
            if (string.IsNullOrWhiteSpace(baseName) || volumeCount < 1)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>(volumeCount);

            for (int index = 1; index <= volumeCount; index++)
            {
                names.Add(baseName + "." + index.ToString("D3", CultureInfo.InvariantCulture));
            }

            return names;
        }

        /// <summary>试开目录：第一卷**所在卷根**下的 <c>.ArchiveFixer.work\volprobe-&lt;guid&gt;</c>；拿不到卷根返回空串。</summary>
        public static string BuildProbeRoot(string? firstVolumePath)
        {
            try
            {
                string full = Path.GetFullPath(firstVolumePath ?? string.Empty);
                string root = Path.GetPathRoot(full) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(root))
                {
                    return string.Empty;
                }

                return Path.Combine(root, WorkDirectoryName, "volprobe-" + Guid.NewGuid().ToString("N"));
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
        /// 1~4 位纯数字段 = 像是被改烂的卷号（<c>01</c> / <c>3</c>）。
        /// 位数上限 4 是刻意的：真实卷号最多三位（<c>.999</c>），四位以上更像版本号之类的普通名字段。
        /// </summary>
        private static bool IsOrdinalSegment(string segment)
        {
            if (segment.Length == 0 || segment.Length > 4)
            {
                return false;
            }

            foreach (char ch in segment)
            {
                if (ch < '0' || ch > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 把基名末段往内容后缀上靠：已经是了 → 原样；只差一个字符（插入或删除 1 个字符）能变成后缀
        /// 而且**只有一种变法** → 改；否则 → 把后缀接在后面。⛔ 有歧义就不改（宁可名字长一点，也不乱剪）。
        /// </summary>
        private static string NormalizeExtensionSegment(string stem, string extension)
        {
            int lastDot = stem.LastIndexOf('.');

            if (lastDot < 0 || lastDot == stem.Length - 1)
            {
                return stem + "." + extension;
            }

            string segment = stem[(lastDot + 1)..];

            if (string.Equals(segment, extension, StringComparison.OrdinalIgnoreCase))
            {
                return stem;
            }

            string? unique = null;

            foreach (string known in KnownExtensionNames())
            {
                if (!IsOneEditAway(segment, known))
                {
                    continue;
                }

                if (unique != null)
                {
                    return stem + "." + extension; // 有歧义：宁可不归一
                }

                unique = known;
            }

            if (unique == null || !string.Equals(unique, extension, StringComparison.OrdinalIgnoreCase))
            {
                return stem + "." + extension;
            }

            return stem[..(lastDot + 1)] + unique;
        }

        /// <summary>
        /// 已知压缩后缀的名字（不含点，长度 ≥ 2）。单字符的 <c>.z</c> 不算：
        /// 它太短，"差一个字符"对它来说等于"随便什么名字都算"，会把 <c>x.a</c> 这类名字归一成 <c>x.z</c>。
        /// </summary>
        private static IEnumerable<string> KnownExtensionNames() =>
            Helpers.ExtensionHelper.KnownArchiveExtensions
                .Select(e => e.TrimStart('.'))
                .Where(name => name.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>两段之间差**恰好一个**字符（插入或删除 1 个字符）—— 用户现场那种"后缀被吃掉/多打一个字符"。</summary>
        private static bool IsOneEditAway(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) != 1)
            {
                return false;
            }

            string longer = a.Length > b.Length ? a : b;
            string shorter = a.Length > b.Length ? b : a;
            int i = 0;

            while (i < shorter.Length && char.ToLowerInvariant(longer[i]) == char.ToLowerInvariant(shorter[i]))
            {
                i++;
            }

            for (int j = i; j < shorter.Length; j++)
            {
                if (char.ToLowerInvariant(longer[j + 1]) != char.ToLowerInvariant(shorter[j]))
                {
                    return false;
                }
            }

            return true;
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
