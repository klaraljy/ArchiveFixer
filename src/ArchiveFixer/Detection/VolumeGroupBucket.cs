using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 证据桶 —— 同一目录、同一基名、同一命名族的卷集合，以及"它们够不够格算一组"的全部事实。
///
/// <para><b>为什么独立成层</b>：同一个卷号上出现两个文件时先认谁（规范名优先）、
/// 名字被伪装过的组要不要再过一道"尺寸规律"、只有本体时算不算分卷组 —— 这三件事都要求
/// "谁进了这个桶、谁是什么身份"是<b>同一份事实</b>。桶外各判一遍，就会出现
/// "同一句话里既说缺 <c>222.zip</c>、又说那个文件就是第 1 卷"这类自相矛盾。</para>
///
/// <para><b>失败模型</b>：量不出大小（<c>Size &lt;= 0</c>）一律当"没有证据"，⛔ 不放行；
/// 桶里只有"干净的本体"时 <see cref="HasVolumeMark"/> 为 false（单独一份 <c>.zip</c> 后面
/// 还有没有卷，名字给不出答案 —— 认成组就会去改用户的名字，宁可判不出）。</para>
///
/// <para>生命周期：一次 <see cref="VolumeGroupDetector.Group"/> 调用内可变（边收边记），
/// 收集结束后只读；只读调用方给的名字事实，不查盘、不读内容。</para>
/// </summary>
internal sealed class VolumeGroupBucket
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

    public VolumeGroupBucket(string directoryPath, string baseName, VolumeNameFamily family)
    {
        DirectoryPath = directoryPath;
        BaseName = baseName;
        Family = family;
    }

    public string DirectoryPath { get; }

    public string BaseName { get; }

    public VolumeNameFamily Family { get; }

    /// <summary>桶里当前保留的候选（只给“内容证明”这种组级判据读；不参与命名）。</summary>
    public IReadOnlyList<VolumeCandidate> Candidates => _byIndex.Values.ToList();

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
