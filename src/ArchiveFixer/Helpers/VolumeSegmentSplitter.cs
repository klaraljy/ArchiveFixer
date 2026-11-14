using System;
using System.Collections.Generic;
using System.Linq;
using static ArchiveFixer.Helpers.VolumeMarkerRules;
using static ArchiveFixer.Helpers.VolumeMarkerSkeletonMatcher;

namespace ArchiveFixer.Helpers;

/// <summary>
/// **卷段拆分**：从一个名字段里拆出「合法卷标记 + 粘着的垃圾」，或判出"这段根本不认识"。
///
/// <para>本类的四个入口（顺序就是判据，⛔ 不许调换）：</para>
/// <list type="number">
/// <item><description><see cref="TrySplitVolumeSegment"/> —— 老口径：整段就是标记，或"标记 + 尾巴"
/// （<c>001(1)</c> / <c>001删除</c> 走这条）；</description></item>
/// <item><description><see cref="TrySplitVolumeSegmentLoose"/> —— 容错：杂质夹在中间也认
/// （骨架在 <see cref="ExtensionSkeletonMatcher"/>）；</description></item>
/// <item><description><see cref="TrySplitVolumeSegmentTolerant"/> —— 删最多 2 个字符 / 卷标记骨架 /
/// 任意长度垃圾 / 拉长卷号 / partN 卷号被挤到最后一位，五档按顺序兜底；</description></item>
/// <item><description><see cref="IsVolumeSegment"/> —— "去杂质后是合法卷标记"的谓词
/// （转调容错档）。</description></item>
/// </list>
///
/// <para>⛔ 只回答"这一段像不像卷标记、标记是哪一段"，⛔ **不负责敢不敢用**：
/// 真要据此改名字/拼组，调用方还必须拿到兄弟卷佐证、尺寸规律、整组自洽那几道闸门。</para>
/// </summary>
internal static class VolumeSegmentSplitter
{
    /// <summary>
    /// 把"可能是分卷标记"的**一整段**拆成「分卷标记 + 粘在后面的垃圾」。
    ///
    /// <code>
    /// 001        → (001, "")        z01 → (z01, "")      part1 → (part1, "")
    /// 001删除    → (001, "删除")    001(1) → (001, "(1)") part01副本 → (part01, "副本")
    /// </code>
    ///
    /// <para>
    /// 为什么要有这一条（2026-09-28 真机事故）：百度网盘会把每个分卷名缀上「删除」这类尾巴，
    /// 变成 <c>giu910.7z.001删除</c>。老判据要求"分卷标记必须是最后一段"，于是整组被当成
    /// **几个各自独立的 .7z**，一键处理再把它们改成 <c>giu910.7z</c> / <c>giu910(1).7z</c> ——
    /// 分卷链当场被切断（介质没坏，但名字再也对不上了）。
    /// </para>
    ///
    /// <para>
    /// 判据只此一处：<see cref="FileNameHelper"/> 与 <c>VolumeGroupDetector</c> 都转调本方法。
    /// 尾巴**必须粘在号码后面、里面不含点**（<c>.001.txt</c> 这种另起一段的**不认** —— 那是
    /// "后缀被改坏"的老口径，保持不变）；尾巴是纯数字的也不认（那更像另一套位宽，不许乱猜）。
    /// </para>
    /// </summary>
    public static bool TrySplitVolumeSegment(string? segment, out string volumeSegment, out string junkTail)
    {
        volumeSegment = string.Empty;
        junkTail = string.Empty;

        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        string s = segment.Trim();

        // ① 整段就是分卷标记（老口径，最常见）
        if (IsVolumePartExtension("." + s))
        {
            volumeSegment = s;
            return true;
        }

        // ② 标记 + 粘着的垃圾。先按 3 个字符试（001 / z01 / r00），再按 part+数字试。
        int markLength = 0;

        if (s.Length > 3 && IsVolumePartExtension("." + s[..3]))
        {
            markLength = 3;
        }
        else if (s.StartsWith("part", StringComparison.OrdinalIgnoreCase))
        {
            int i = 4;

            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            if (i > 4)
            {
                markLength = i;
            }
        }

        if (markLength <= 0 || markLength >= s.Length)
        {
            return false;
        }

        string mark = s[..markLength];
        string tail = s[markLength..];

        // 尾巴别是纯数字（0023 这种更像卷号本身），也别长到不像垃圾
        if (tail.Length > 32 || tail.All(char.IsAsciiDigit))
        {
            return false;
        }

        /*
         * ⛔ **标记的数字段后面不许紧跟数字**（2026-10-07 真机 `111.z11111111110删除3`）：
         * 老写法取 `s[..3]` = `z11` 就定了，尾巴 `111111110删除3` 只要"不是纯数字、≤32 字符"就放行
         * ⇒ 那 8 个数字被当垃圾丢掉，凭空得出"这是第 11 片"。数字与标记的数字**连在一起**
         * ⇒ 卷号本身有歧义 ⇒ 这一档判不出（交给骨架档与组内事实，证不出第几片就不改名）。
         * ⛔ `001(1)` 这一档不受影响：尾巴第一个字符是 `(`，不是数字（老口径照旧放行）。
         */
        if (char.IsAsciiDigit(tail[0]))
        {
            return false;
        }

        volumeSegment = mark;
        junkTail = tail;
        return true;
    }

    /// <summary>
    /// **宽松**版：段里任何位置夹了垃圾都能认出卷标记（用户 2026-09-28 第二轮追加）。
    ///
    /// <code>
    /// 001      → (001, "")        z01 → (z01, "")        part1 → (part1, "")
    /// 001删除  → (001, "删除")     删除001 → (001, "删除")   z0删除3 → (z03, "删除")
    /// 001(1)   → (001, "(1)")     part01副本 → (part01, "副本")
    /// 0012     → false（纯数字尾巴更像另一套位宽，不猜）
    /// </code>
    ///
    /// <para>
    /// 判据顺序（**顺序本身就是判据**，调换会让 <c>001(1)</c> 之类漏掉）：
    /// ① 整段就是标记 → ② 前缀标记 + 尾巴（老口径，<c>001(1)</c> 走这条）→ ③ **骨架**：
    /// 剔掉段内所有非字母数字字符，剩下的若整体是合法标记就算（<c>z0删除3</c> → <c>z03</c>、
    /// <c>删除001</c> → <c>001</c>）。
    /// </para>
    ///
    /// <para>
    /// 为什么敢宽松：认出卷标记只用来判"这几个文件是不是一组分卷"和"该改成什么名字"；
    /// 真要不要改，后面还有**尺寸规律**与**用户授权**两道。收益是实打实的 ——
    /// 网盘那个「删除」既能缀在后面（<c>.001删除</c>）也能缀在前面（<c>.删除001</c>），
    /// 还可能夹在中间（<c>.z0删除3</c>）。
    /// </para>
    /// </summary>
    public static bool TrySplitVolumeSegmentLoose(string? segment, out string canonicalSegment, out string junk)
    {
        canonicalSegment = string.Empty;
        junk = string.Empty;

        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        string s = segment.Trim();

        // ① 整段就是标记
        if (IsVolumePartExtension("." + s))
        {
            canonicalSegment = s;
            return true;
        }

        // ② 前缀标记 + 尾巴（老口径优先：001(1) / 001删除 走这条）
        if (TrySplitVolumeSegment(s, out string prefixMark, out string prefixTail))
        {
            canonicalSegment = prefixMark;
            junk = prefixTail;
            return true;
        }

        // ③ 骨架：剔掉所有非字母数字，剩下的整体是合法标记才算
        var skeleton = new System.Text.StringBuilder(s.Length);

        foreach (char c in s)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                skeleton.Append(c);
            }
        }

        string bones = skeleton.ToString();

        if (bones.Length == 0 || bones.Length == s.Length)
        {
            return false;
        }

        if (!IsVolumePartExtension("." + bones))
        {
            return false;
        }

        canonicalSegment = bones;

        /*
         * ⛔ 骨架档的垃圾**可能夹在号码中间**（`00除2` 的 `除`、`z0删除3` 的 `删除`），
         * 它**不是**"卷号后面的尾巴" —— 老写法 `s.Replace(bones, "")` 还会在"骨架不是原串子串"时
         * 原样吐回整段（`00除2` → 垃圾算成 `00除2`），补缺失卷名就拼出
         * `amb909.7z.00100除2` 这种磁盘上不存在的名字（用户 2026-09-28 真机）。
         * 尾巴只给"真·后缀"那一档用（`001删除` 走前缀规则，照旧带尾巴）。
         */
        junk = string.Empty;
        return true;
    }

    /// <summary>这一段是不是"去掉杂质后是合法分卷标记"（底层判据 = <see cref="TrySplitVolumeSegmentLoose"/>）。</summary>
    public static bool IsVolumeSegment(string? segment) =>
        TrySplitVolumeSegmentLoose(segment, out _, out _);

    /// <summary>
    /// **容错**版（用户 2026-09-28 第三次真机：`amb909.7sz.00c1` / `amb909.7删z.00除2`）：
    /// 干扰字符被**塞进卷号内部**、甚至是**字母**（`001`→`00c1`）时也要认出来。
    ///
    /// <para>判据：允许删掉**最多 2 个**"多余字符"后剩下的必须是合法卷标记；
    /// 被删掉的字符必须**不全都是数字**（否则 `0012` 会被误读成 `001`+`2`）；
    /// 候选唯一时才返回 —— 有歧义（能删出两种合法标记）一律**不认**（宁可不动）。</para>
    ///
    /// <code>
    /// 00c1 → (001, "c")     0删0除1 → (001, "删除")     7sz（后缀段）→ 不认（段本身不是卷标记）
    /// 0012 → false（删掉的是纯数字）      001 → (001, "")
    /// </code>
    ///
    /// <para>⛔ **只回答"像不像卷标记"，不负责敢不敢用** —— 真要据此改名字，调用方还必须拿到
    /// "兄弟卷佐证 + 尺寸规律"两条证据（子卷归组那三层判据里另外两层）。</para>
    /// </summary>
    /// <param name="segment">待判的那一段（不含点）。</param>
    /// <param name="canonicalSegment">还原出来的规范卷标记。</param>
    /// <param name="junk">被当成杂质剔掉的字符（转调 <see cref="TrySplitVolumeSegmentLoose"/> 那颗时按它的口径，可为空串）。</param>
    /// <param name="allowPartNumberedSkeleton">
    /// 允不允许吃 **<c>partN</c> 骨架**（<c>pa8rt1</c> → <c>part1</c>）。
    /// ⛔ 默认 false = 老口径；**只有"取最右"那一处**（<see cref="VolumeNameRebuilder.TrySplitPartNumberedVolume"/>：
    /// 尾巴逐字是 <c>rar</c> 的 <c>&lt;基名&gt;.&lt;卷标记&gt;.rar</c>）才传 true ——
    /// 理由见 <see cref="VolumeMarkerSkeletonMatcher.TryMatchVolumeMarkerSkeleton"/>（`p1art2` 与 `pa8rt1` 同形，
    /// 裸的末尾段吃了骨架档，`444.p1art2.part2.rar` 的基名就会变成 `444`）。
    /// </param>
    public static bool TrySplitVolumeSegmentTolerant(
        string? segment,
        out string canonicalSegment,
        out string junk,
        bool allowPartNumberedSkeleton = false)
    {
        canonicalSegment = string.Empty;
        junk = string.Empty;

        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        string s = segment.Trim();

        /*
         * ===== 第 1.5 档（2026-10-13）：**partN 里"卷号被垃圾挤到最后一位"** =====
         *
         * <para><b>现场</b>（大测试困难批 `run-2222`，逐字）：`bbb-L2.part1.rar` 被伪装成
         * `bbb-L2.part98598542删除1.rar`（EEE 式那一法：`part` + 一串随机数字 + 「删除」 + 卷号）。
         * 老尺子（**不是相似度匹配**，是"删掉最多 N 个字符 + 骨架命中"两档穷举）把它读成——
         * 删 ≤2 档删掉 `删/除` 得 `part985985421`（合法标记，但卷号 985985421）
         * ⇒ `TrySplitPartNumberedVolume` 后面那道 `> 9999` 闸门把它否掉 ⇒ 整条 partN 解析失败、
         * 退回"普通 rar 本体"分支 ⇒ 基名成了 `bbb-L2.part98598542删除1` ⇒ 与兄弟 `bbb-L2.part2.rar`
         * **认不成一组** ⇒ 缺卷 ⇒ ①「内容物解出来」红。</para>
         *
         * <para><b>为什么不能只靠"相似度"</b>（用户问过：这不是后缀相似匹配该管的吗）：单看一个名字，
         * `part98598542删除1` 里 `part9859` / `part98598` / `part1` 都是"完全包含"的合法子序列，
         * 光比相似度会挑出 `part9859` 这种错答案。**能定案的是结构事实**：`partN` 这一族的卷号
         * 永远在**末尾** ⇒ 取"末尾那一段数字"当卷号，中间那一段必须是"塞进去的垃圾"
         * （<see cref="VolumeMarkerRules.LooksLikeInsertedJunk"/>，与第四档同一把尺子：至少一个非字母数字、
         * 且没有 ≥3 个连续 ASCII 字母）。⛔ 干净的 `part12` 不受影响（中间没有垃圾，
         * "取最右那个能自洽的卷标记"这条既有口径一个字没动）。</para>
         *
         * <para>⚠ 已知歧义：`part1删2`（垃圾夹在两个数字之间）会被读成 `part2`。
         * 这一档只提供"单个名字的最佳读法"，**敢不敢用它改名字/拼组**照旧由后面那几道闸门
         * （兄弟卷佐证 / 卷号一份对一份 / 尺寸规律 / 整组自洽 / 试开）说了算。</para>
         *
         * <para>⛔ 位置必须在"前缀标记+尾巴"那一档**之前**：`part98598542删除1` 会被它抢答成
         * `part98598542`（卷号 98598542 越界）。</para>
         */
        if (s.StartsWith("part", StringComparison.OrdinalIgnoreCase) && s.Length > 6)
        {
            int digitStart = s.Length;

            while (digitStart > 4 && char.IsAsciiDigit(s[digitStart - 1]))
            {
                digitStart--;
            }

            string middle = s[4..digitStart];
            string ordinal = s[digitStart..];

            if (ordinal.Length > 0
                && middle.Length >= 2
                && LooksLikeInsertedJunk(middle)
                && int.TryParse(ordinal, out int ordinalValue)
                && ordinalValue <= 9999
                && IsVolumePartExtension(".part" + ordinal))
            {
                canonicalSegment = "part" + ordinal;
                junk = middle;
                return true;
            }
        }

        // 先走已有的两档（整段就是标记 / 前缀标记+尾巴 / 非字母数字骨架）
        if (TrySplitVolumeSegmentLoose(s, out canonicalSegment, out junk))
        {
            return true;
        }

        // 再试"删最多 2 个字符"：把删掉的字符当垃圾
        string? uniqueMark = null;
        string uniqueJunk = string.Empty;
        bool ambiguous = false;

        int length = s.Length;

        // 删 1 个
        for (int i = 0; i < length; i++)
        {
            string candidate = s.Remove(i, 1);

            if (!IsVolumePartExtension("." + candidate))
            {
                continue;
            }

            if (char.IsAsciiDigit(s[i]))
            {
                continue; // 删掉的是数字 → 更像另一套位宽，不猜
            }

            if (uniqueMark != null)
            {
                ambiguous = true;
                break;
            }

            uniqueMark = candidate;
            uniqueJunk = s[i].ToString();
        }

        // 删 2 个
        if (!ambiguous && uniqueMark == null && length >= 5)
        {
            for (int i = 0; i < length - 1; i++)
            {
                for (int j = i + 1; j < length; j++)
                {
                    string candidate = s.Remove(j, 1).Remove(i, 1);
                    string removed = string.Concat(s[i], s[j]);

                    if (!IsVolumePartExtension("." + candidate))
                    {
                        continue;
                    }

                    if (removed.All(char.IsAsciiDigit))
                    {
                        continue;
                    }

                    if (uniqueMark != null)
                    {
                        ambiguous = true;
                        break;
                    }

                    uniqueMark = candidate;
                    uniqueJunk = removed;
                }

                if (ambiguous || uniqueMark != null)
                {
                    break;
                }
            }
        }

        /*
         * 第三档（2026-10-04，用户拍板）：**通用骨架命中** —— `0a0b1` → `001`、`z0a1` → `z01`。
         *
         * ⛔ 顺序不变（用户口径）：前面那两档是**更快更保守的前置档**，它们判得出来就不用这一档。
         * ⛔ partN 骨架（`pa8rt1` → `part1`）**默认不吃**，只有 <paramref name="allowPartNumberedSkeleton"/>
         *    为 true 时才吃 —— 那一位只由"取最右"那一处（`TrySplitPartNumberedVolume`）传，
         *    理由写在 `TryMatchVolumeMarkerSkeleton` 上（`p1art2` 与 `pa8rt1` 同形）。
         */
        if (!ambiguous &&
            uniqueMark == null &&
            TryMatchVolumeMarkerSkeleton(s, allowPartNumberedSkeleton, out string skeletonMark, out string skeletonJunk))
        {
            uniqueMark = skeletonMark;
            uniqueJunk = skeletonJunk;
        }

        /*
         * ===== 第四档（2026-10-12，用户口径逐字：「这种情况就要去改代码了，范围太小了，
         * 以后凡是限制两到三个的都会出现问题，赶紧修改」）=====
         *
         * **任意长度的"垃圾"**：`--p002` → `002`、`@@003` → `003`、`#%z01` → `z01`。
         *
         * <para><b>为什么前三档不够</b>：档 1/2 是"删掉 ≤2 个字符"，而造包细则允许塞 **2–3 个**
         * （大测试困难批实测 `资源222-L2.zip.--p002`：杂质 3 个 ⇒ 认不出 ⇒ 那一片被当成
         * "双面文件"从偏移 162 抠出来 ⇒ 整组凑不齐）；档 3（骨架）要求"首位数字在段首、
         * 末位在段末"（首尾对齐）⇒ **杂质落在数字前面**时必然判死。</para>
         *
         * <para><b>换成结构判据，⛔ 不设个数上限</b>：</para>
         * <list type="number">
         * <item><description>标记必须**贴着段末**（卷标记本来就在末尾）—— 段末那一段连续数字
         * 必须是 2 位或 3 位（4 位以上判不出是 `0012` 还是 `012` ⇒ 不认）；</description></item>
         * <item><description>标记前面那一段算"垃圾"，它必须**至少含一个非字母非数字的字符**
         * （符号 / 中文 / 其它）—— 光靠 ASCII 字母凑出来的前缀更像一个词；</description></item>
         * <item><description>垃圾里**不许出现 ≥3 个连续 ASCII 字母** —— 防把 `photo-123`、
         * `backup-002` 这种真名字吃进来（这条是**结构**限制，不是个数限制）；</description></item>
         * <item><description>恢复出来的标记必须合法（<see cref="VolumeMarkerRules.IsVolumePartExtension"/>）；
         * 字母前缀（`z01`/`r00`）优先于纯数字（两条都合法时取更长的那个，**不是歧义**）。</description></item>
         * </list>
         *
         * <para>⛔ 顺序不变：前面三档判得出来就轮不到它 —— 既有行为一个字节不改。</para>
         */
        if (!ambiguous && uniqueMark == null)
        {
            int end = s.Length;
            int digitStart = end;

            while (digitStart > 0 && char.IsAsciiDigit(s[digitStart - 1]))
            {
                digitStart--;
            }

            int runLength = end - digitStart;

            if (runLength is 2 or 3)
            {
                string digitsCandidate = s[digitStart..end];
                string digitsJunk = s[..digitStart];

                // 字母前缀（z01 / r00）：紧挨着数字那一个字母也算标记的一部分。
                if (digitStart > 0
                    && (s[digitStart - 1] == 'z' || s[digitStart - 1] == 'r')
                    && IsVolumePartExtension("." + s[(digitStart - 1)..end]))
                {
                    uniqueMark = s[(digitStart - 1)..end];
                    uniqueJunk = s[..(digitStart - 1)];
                }
                else if (IsVolumePartExtension("." + digitsCandidate))
                {
                    uniqueMark = digitsCandidate;
                    uniqueJunk = digitsJunk;
                }

                if (uniqueMark != null && !LooksLikeInsertedJunk(uniqueJunk))
                {
                    /*
                     * ⚠ 例外：**纯数字的垃圾 + 带字母的标记**（2026-10-13 困难批 `run-26262626`）。
                     *
                     * <para>现场逐字：`111-L2.00000z01`（`z01` 前被塞了 5 个 `0` —— 造包细则的"重复字符"那一法
                     * 允许重复数字）⇒ 老判据（垃圾必须含非字母数字）当场否掉 ⇒ 这一片**没被认成分卷片**
                     * ⇒ 改名那一步按内容走（检测器说 `ZIP_SPANNED` ⇒ 建议后缀 `.zip`）⇒ 逐字
                     * `111-L2.00000z01 -> 111-L2.zip，状态：改名成功` ⇒ **把第 1 片改成了本体/末片的名字**
                     * ⇒ 真末片接不进来（`目标名已经被别的文件占着（⛔ 绝不覆盖）`）⇒ 那组永远凑不齐 ⇒ ①红。</para>
                     *
                     * <para>为什么这一档可以放：标记自身**带字母**（`z01` / `r00` / `partN`）时不存在
                     * "数字位宽"那种歧义 —— 而"删掉的字符不许全是数字"这条规矩本来就是为了
                     * **纯数字标记**（`0012` 到底是 `001` 还是 `012`）立的。⇒ 只对**带字母的标记**放行，
                     * ⛔ 纯数字标记（`0012`）照旧一个都不认。</para>
                     */
                    bool digitJunkWithLetterMark = uniqueJunk.Length > 0
                        && uniqueJunk.All(char.IsAsciiDigit)
                        && uniqueMark.Any(char.IsAsciiLetter);

                    if (!digitJunkWithLetterMark)
                    {
                        uniqueMark = null;
                        uniqueJunk = string.Empty;
                    }
                }
            }
        }

        /*
         * ===== 第五档（2026-10-12，同一条用户口径）：**数字型"拉长卷号"** =====
         *
         * `0000000001` → `001`。
         *
         * <para><b>现场</b>（大测试困难批，样本 `22222222` 逐字）：造包细则的"重复字符"那一法
         * 会把 `.001` 写成 `.0000000001`（把某个字符连写 3–8 次塞进标记里）。套娃包解开之后
         * 吐出这一片，而**前四档一条都命不中**（整段 10 位数字：不是 2–3 位、也没有 `z`/`r` 前缀、
         * 骨架档要求三位且首尾对齐）⇒ 它在链里被当成"普通内层包"去开 ⇒ 打不开 ⇒
         * 逐字 `已完成 1 层，停在第 2 层，原因：压缩包损坏` ⇒ 那一单失败、产物被清掉 ⇒
         * 整组永远缺 `.001` ⇒ ①「内容物解出来」红。</para>
         *
         * <para><b>判据</b>：整段都是数字、长度 &gt; 3 时，把**所有"同一字符连写 ≥3 次"**的片段
         * 各压成 1 个字符，压完必须正好是 3 位数字的合法卷标记。
         * `0000000001` → `001` ✓；`0012` 没有任何 ≥3 的连写 ⇒ 压完还是 4 位 ⇒ **不认**（老口径不变）。</para>
         */
        if (!ambiguous && uniqueMark == null && s.Length > 3 && s.All(char.IsAsciiDigit))
        {
            /*
             * ⚠ "压成 1 个"不够（实测踩到）：`0000000001` = **9 个 0 + 1**，压成 1 个只剩 `01`（两位，不合法），
             * 而它真身是 `.001`（连写的 7 个 0 与标记自己的两个 0 分不开）。
             * ⇒ 口径 = 每个"连写 ≥3 次"的片段**可以压成 1 个、也可以压成 2 个**，
             *   取**唯一**那个合法三位标记；两条都能凑出合法标记、或一条都凑不出 ⇒ 不认（宁可不猜）。
             */
            var forms = new List<string> { string.Empty };

            for (int i = 0; i < s.Length;)
            {
                int j = i;

                while (j < s.Length && s[j] == s[i])
                {
                    j++;
                }

                int runLength = j - i;
                var next = new List<string>(forms.Count * 2);

                foreach (string prefix in forms)
                {
                    if (runLength >= 3)
                    {
                        // 压成 1 个 / 压成 2 个，两种都留
                        next.Add(prefix + s[i]);
                        next.Add(prefix + new string(s[i], 2));
                    }
                    else
                    {
                        next.Add(prefix + s.Substring(i, runLength));
                    }
                }

                forms = next;

                if (forms.Count > 32)
                {
                    return false;   // 病态名字（太多"连写"）⇒ 不猜
                }

                i = j;
            }

            var legal = forms
                .Distinct(StringComparer.Ordinal)
                .Where(candidate => candidate.Length == 3 && IsVolumePartExtension("." + candidate))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (legal.Count == 1)
            {
                uniqueMark = legal[0];
                uniqueJunk = string.Empty;
            }
        }

        if (ambiguous || uniqueMark == null)
        {
            return false;
        }

        canonicalSegment = uniqueMark;
        junk = uniqueJunk;
        return true;
    }
}
