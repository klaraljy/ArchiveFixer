using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 「按建议改名并重试」的计划（用户 2026-09-25 第 41 条）。
    ///
    /// <para><b>为什么要有它</b>：第一卷第名字被改坏（<c>X.7z(删掉.001</c>）时，程序**只能**报「分卷缺失」
    /// 并给一个改名建议 —— 源文件一个字节都不能动（不变量 1）。可用户手上那一卷是真需要改名的，
    /// 让他自己去资源管理器里对着日志手打一遍名字，既慢又容易打错（那名字本来就不规则）。
    /// 于是给一个**显式**的按钮：「按建议改名并重试」——用户点了才改，只改**名字**、内容一个字节不动。</para>
    ///
    /// <para><b>判据全在事实里</b>（不看中文状态、不看调用方怎么想）：</para>
    /// <list type="number">
    /// <item><description>源文件还在；</description></item>
    /// <item><description>它的名字里**有卷号、而且是第 1 卷**（不是第一卷时改名毫无意义）；</description></item>
    /// <item><description>同目录里找得到"像后续卷"的文件（一个都没有就凑不齐一组）；</description></item>
    /// <item><description>从后续卷的名字**推得出**标准名（推不出来宁可不做 —— 改错名字比不改更糟）；</description></item>
    /// <item><description>它现在的名字**不是**标准名（已经是了就别乱动）；</description></item>
    /// <item><description>目标名**没被占用**（⛔ 绝不覆盖，不变量 3）。</description></item>
    /// </list>
    ///
    /// <para>任一不成立 → <see cref="VolumeNameRepairPlan.CanRepair"/> = false，并把**为什么不能改**
    /// 写在 <see cref="VolumeNameRepairPlan.Reason"/> 里（直接给用户看，不许含糊）。</para>
    /// </summary>
    public sealed class VolumeNameRepairPlan
    {
        /// <summary>
        /// <see cref="Describe"/> 里最多列几条 `旧名 → 新名`（用户 2026-10-02：整组改名必须点名）。
        /// 超过它才折成「等 N 卷」—— 一行日志读得完，又不至于把名字全藏起来。
        /// </summary>
        private const int MaxNamesInDescribe = 3;

        /// <summary>能不能改（false 时看 <see cref="Reason"/>）。</summary>
        public bool CanRepair { get; init; }

        /// <summary>不能改的原因 / 能改时的补充说明（面向用户，可为空）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>现在的完整路径。</summary>
        public string CurrentPath { get; init; } = string.Empty;

        /// <summary>现在的文件名。</summary>
        public string CurrentFileName { get; init; } = string.Empty;

        /// <summary>建议的文件名（例如 <c>Code Complete-BZ.7z.001</c>）。</summary>
        public string SuggestedFileName { get; init; } = string.Empty;

        /// <summary>改名后的完整路径。</summary>
        public string TargetPath { get; init; } = string.Empty;

        /// <summary>同目录里"像这一组后续卷"的文件名（只用来把话说清）。</summary>
        public IReadOnlyList<string> Siblings { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 这一份计划**真的跑过试开**（硬链接 + 引擎列一次目录）。
        ///
        /// <para>为什么要标出来：内容级这条路以前"不成立就静默返回"，用户在日志里看不到任何痕迹 ——
        /// 2026-09-29 真机就是因此查不出断在哪（<c>ExtractionCoordinator.NormalizeDisguisedVolumeNamesAsync</c>）。
        /// 标了它，调用方才能只对"真跑过试开"的那些写一行结论，
        /// 而不会给每个普通压缩包都添一行（第 45 条的日志纪律：成功的任务只留一行）。</para>
        /// </summary>
        public bool TrialAttempted { get; init; }

        /// <summary>试开成立、但引擎读不出清单：这一组是"文件名也加密"的归档（7z <c>-mhe</c> / RAR <c>-hp</c>）。</summary>
        public bool ProbeNeedsPassword { get; init; }

        /// <summary>这一份计划里**有几卷是从别的目录收过来的**（0 = 全在同一层，与老行为一样）。</summary>
        public int GatheredVolumes { get; init; }

        /// <summary>
        /// 这一份"不能改"是**有结论的**：同目录里那些片全是满片、其中一片开头就是跨盘标记
        /// ⇒ 这是一组跨盘 zip，缺的是**末片**（用户要的"说清缺的是第几片"）。
        ///
        /// <para>为什么单独立一位：调用方只对"值得说一句"的结论写日志（第 45 条：成功的任务只留一行），
        /// 而这个结论与"试开跑过"是两件事 —— 它一次引擎都没调，但用户必须看到
        /// "缺的是末片"而不是含糊的「分卷缺失」。</para>
        /// </summary>
        public bool SpannedTailMissing { get; init; }

        /// <summary>
        /// 这一份"不能改"是**字节数那条硬证据**给的结论：7z 起始头自述的整包字节数与手上这几卷对不上
        /// （缺多少 / 多出多少都如实写在 <see cref="Reason"/> 里），所以**一次引擎都没调**。
        ///
        /// <para>为什么单独立一位（2026-10-03 阶段 B）：调用方按"值不值得说一句"决定写不写日志
        /// （第 45 条），而这一档的结论是用户最需要看见的那句"还差多少字节" —— 它既不是
        /// <see cref="TrialAttempted"/>（⛔ 一次引擎都没调，不许说成"试过"），也不是
        /// <see cref="SpannedTailMissing"/>（那是跨盘 zip 的事）。⛔ 它只影响**怎么写这句话**，
        /// 不参与任何删除 / 搬运的判据（方案 §4 阶段 B 的 (d)）。</para>
        /// </summary>
        public bool ByteBudgetMismatch { get; init; }

        /// <summary>
        /// 这次要改的**每一卷**（用户 2026-09-28 追加：网盘给整组的名字都缀了「删除」，
        /// 只改第一卷没用 —— 7-Zip 找 `.002` 时名字对不上，照样报缺卷）。
        ///
        /// <para>老调用方读上面那四个单文件属性（= 第一项），行为与以前逐字一样；
        /// 「修复分卷名并重试」按本列表一次把整组改干净。</para>
        /// </summary>
        public IReadOnlyList<VolumeRepairItem> Items { get; init; } = Array.Empty<VolumeRepairItem>();

        /// <summary>
        /// 一行给人看：**每一卷都点名**（`旧名 → 新名`）—— 列全 / 折起来都写出这一组共几卷。
        ///
        /// <para><b>为什么必须逐条点名</b>（用户 2026-10-02 真机）：老写法只报第一项、其余折成
        /// 「等 N 卷」，于是真机日志里那一行是
        /// 「风景01.7z：分卷名不标准，已按标准名改好（2 卷……）：风景01.7z → 风景01.7z.001 等 2 卷」
        /// —— 另一卷原本叫 **`风景02.mp4`**（被改成 `风景01.7z.002`），日志里**一个字都查不到**，
        /// 而它随后被彻底删除了。用户对不上账："我的风景02.mp4 去哪了"。</para>
        ///
        /// <para>规则：≤ <see cref="MaxNamesInDescribe"/> 项**全列**（末尾写「（共 N 卷）」）；
        /// 更多时只列前 <see cref="MaxNamesInDescribe"/> 条 + 「等 N 卷」（N 仍是**总卷数**，
        /// 与老口径一致 —— 绝不静默截断、也不假称只有列出来的那几卷）。</para>
        /// </summary>
        public string Describe()
        {
            if (Items.Count <= 1)
            {
                return $"{CurrentFileName} → {SuggestedFileName}";
            }

            IEnumerable<VolumeRepairItem> listed = Items.Count > MaxNamesInDescribe
                ? Items.Take(MaxNamesInDescribe)
                : Items;

            string body = string.Join(
                "；",
                listed.Select(item => $"{item.CurrentFileName} → {item.SuggestedFileName}"));

            return Items.Count > MaxNamesInDescribe
                ? $"{body}；等 {Items.Count} 卷"
                : $"{body}（共 {Items.Count} 卷）";
        }
    }

    /// <summary>一组里的一卷：现在叫什么、该叫什么。</summary>
    public sealed class VolumeRepairItem
    {
        public string CurrentPath { get; init; } = string.Empty;

        public string CurrentFileName { get; init; } = string.Empty;

        public string SuggestedFileName { get; init; } = string.Empty;

        public string TargetPath { get; init; } = string.Empty;
    }

    /// <summary>改名结果。</summary>
    public sealed class VolumeNameRepairResult
    {
        /// <summary>是否真的改成功了。</summary>
        public bool Success { get; init; }

        /// <summary>改完之后的完整路径（失败时为空）。</summary>
        public string NewPath { get; init; } = string.Empty;

        /// <summary>给人看的一句话（成功 / 失败都说清）。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 「按建议改名并重试」的实现（纯逻辑 + 文件操作，不引用 WPF）。
    ///
    /// <para>建议名的推法与失败提示**共用同一份** <see cref="RawSplitStreamDetector.SuggestStandardFirstName"/>：
    /// ⛔ 界面上说的名字与真正改成的名字必须是同一个，否则用户点一下反而把文件改坏。</para>
    /// </summary>
    public static class VolumeNameRepair
    {
        /// <summary>
        /// 同目录里的文件名（只要名字、不要路径；读不了就返回空 —— 计划会因此判"不能改"，**绝不抛**）。
        ///
        /// <para>只有这一份实现：识别阶段（算"要不要点亮修复按钮"）、改名预览（算落点）、
        /// ①页那颗按钮（算真正会改成的名字）三处共用。⛔ 各写一份必然漂移 ——
        /// 那正是"按它说的改完还是解不开"的来源。</para>
        /// </summary>
        public static IReadOnlyList<string?> EnumerateFileNamesInDirectory(string? filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath ?? string.Empty) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return Array.Empty<string?>();
                }

                return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string?>();
            }
        }

        /// <summary>
        /// 同目录里的"路径 + 大小"（内容级推断要按尺寸排候选；读不了就返回空 —— 推断会因此判"不能改"，**绝不抛**）。
        ///
        /// <para>与 <see cref="EnumerateFileNamesInDirectory"/> 一样只有一份实现：
        /// 必须与它看到**同一个目录、同一批文件**，否则"按名字能改、按内容不能改"这类矛盾迟早冒出来。</para>
        /// </summary>
        public static IReadOnlyList<VolumeCandidate> EnumerateVolumeCandidatesInDirectory(string? filePath) =>
            Detection.VolumeContentInference.EnumerateCandidatesIn(Path.GetDirectoryName(filePath ?? string.Empty));

        /// <summary>
        /// **候选池（含邻近目录）**：找卷的**基准**是**手上最浅的那一卷**（用户导入进来的那个位置），
        /// 窗口 = **它的第一层父文件夹这一家**（父目录本身 + 父目录的各直接子目录 = 兄弟目录）
        /// + **它自己那一层** + **它的子目录（递归，到 <see cref="NearbyMaxSubdirectoryLevels"/> 为止）**。
        ///
        /// <para><b>为什么基准必须是"最浅的那一卷"、⛔ 绝不是 001</b>（用户 2026-10-05 原话：
        /// 「你要以一开始的分卷文件为准，不要以 001 为准，否则这时的第一层父文件夹就探测不到了」）：
        /// 真正要看的父文件夹是**最外层那个分卷**的父文件夹。若拿一个**解出来的** <c>001</c>
        /// 当基准（它躺在产物树深处），窗口的"第一层父文件夹"就成了那个深层目录，
        /// 而散在旁边的 <c>.002</c> / <c>.003</c> 一个都探测不到 —— 他给的恶心形状是
        /// <c>111\1111.7z.002删除</c> + <c>111\1111.7z.003删除</c> + <c>111\222.zip</c>，
        /// 而真正的 <c>001</c> 压在 <c>222.zip</c> 里面（解开后是 <c>111\222\333\1111.7z.001删除</c>）。</para>
        ///
        /// <para>为什么要有它（用户 2026-10-01 点名两次）：「跨目录找同组的卷」；他明确了两条口径 ——
        /// ① 「**归档自己所在目录的子目录**，这个也不能少」；② 「绝大多数只会在一个**父文件夹和父文件夹的
        /// 同级子文件夹**当中」。⚠ 后一句同时也是**代价说明**：这种形状本来就是小概率，
        /// 所以这里的做法是"宁可多看一眼、但边界划死、粗筛挡住噪声"，⛔ 不是满盘搜。</para>
        ///
        /// <para><b>边界是刻意划的</b>（§8 隐私红线：不替用户在他盘上到处找文件）：</para>
        /// <list type="number">
        /// <item><description><b>看</b>：自己这一层（不做尺寸粗筛，与以前"同目录"的行为逐字一致）
        /// + 自己的子目录（**递归**到 <see cref="NearbyMaxSubdirectoryLevels"/> 层，自己这一层算第 1 层）
        /// + 父目录这一家（这一块过一次 ≥ 16 KiB 的粗筛 + 硬上限）。</description></item>
        /// <item><description><b>不看</b>：祖父及以上、比 <see cref="NearbyMaxSubdirectoryLevels"/> 更深的子目录、
        /// 以及"父目录这一家"之外的目录。归档目录**本身就是卷根**（例如就放在 <c>H:\</c> 下）时
        /// 没有父目录这一家，退化成"自己这一层 + 自己的子目录"。</description></item>
        /// <item><description><b>候选只是候选</b>：跨目录来的文件同样要过"内容 / 尺寸 / 试开"那几道判据，
        /// ⛔ 不会因为"它躺在附近"就被认成这一组的一员。</description></item>
        /// </list>
        /// </summary>
        public static IReadOnlyList<VolumeCandidate> EnumerateVolumeCandidatesNearby(string? filePath)
        {
            string directory = Path.GetDirectoryName(filePath ?? string.Empty) ?? string.Empty;

            var candidates = new List<VolumeCandidate>(
                Detection.VolumeContentInference.EnumerateCandidatesIn(directory));

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return candidates;
            }

            /*
             * ① 自己的子目录**递归**（2026-10-05 从"只看直接子目录"放宽到"看到第 3 层"）。
             *
             * 用户点名的那条理由：真正的第 1 卷常常压在**里面那个包**里，解开之后落在
             * `111\222\333\` 这种**子目录的子目录**里 —— 只看直接子目录就永远看不见它。
             * 深度常量见 NearbyMaxSubdirectoryLevels（到顶就是"判不出 ⇒ 什么都不做"）。
             */
            try
            {
                var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { directory };
                var frontier = new List<string> { directory };
                int directoriesSeen = 0;

                for (int level = 1; level < NearbyMaxSubdirectoryLevels && frontier.Count > 0; level++)
                {
                    var next = new List<string>();

                    foreach (string current in frontier)
                    {
                        foreach (string sub in Directory.GetDirectories(current))
                        {
                            if (++directoriesSeen > MaxNearbyDirectories)
                            {
                                break;
                            }

                            if (scanned.Add(sub))
                            {
                                next.Add(sub);
                            }
                        }
                    }

                    foreach (string sub in next)
                    {
                        foreach (VolumeCandidate candidate in Detection.VolumeContentInference.EnumerateCandidatesIn(sub))
                        {
                            // 与"父目录这一家"同一道上限（沿用既有 500 份，⛔ 不新造第二套）。
                            if (candidates.Count >= MaxNearbyCandidates)
                            {
                                return candidates;
                            }

                            candidates.Add(candidate);
                        }
                    }

                    frontier = next;
                }
            }
            catch
            {
                // 某个子目录读不动（权限 / 半路被删）不影响已经收到的那几份候选。
            }

            // ② 父目录这一家（父目录 + 它的各直接子目录 = 兄弟目录）。
            try
            {
                string full = Path.GetFullPath(directory);
                string? parent = Directory.GetParent(full)?.FullName;

                // 自己这一层就是卷根 ⇒ 没有"父目录这一家"可看（⛔ 绝不退化成整盘扫）。
                if (!string.IsNullOrWhiteSpace(parent)
                    && !string.Equals(parent, full, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.AddRange(EnumerateFamilyCandidates(parent, full, candidates));
                }
            }
            catch
            {
                // 取父目录失败（路径形态怪 / 权限）⇒ 就按"自己这一层 + 自己的子目录"办，⛔ 不猜、不报错。
            }

            return candidates;
        }

        /// <summary>父目录这一家（父目录 + 它的各直接子目录）里"值得读一眼"的候选。</summary>
        private static List<VolumeCandidate> EnumerateFamilyCandidates(
            string parent,
            string ownDirectory,
            List<VolumeCandidate> alreadyCollected)
        {
            var result = new List<VolumeCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new List<string> { parent };

            try
            {
                foreach (string sub in Directory.GetDirectories(parent))
                {
                    if (directories.Count >= MaxNearbyDirectories)
                    {
                        break;
                    }

                    directories.Add(sub);
                }
            }
            catch
            {
                // 父目录列不动就只留父目录这一层。
            }

            foreach (string scanned in directories)
            {
                if (string.Equals(scanned, ownDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // 自己这一层已经在上面收过了（不计入粗筛，行为与以前一致）
                }

                foreach (VolumeCandidate candidate in Detection.VolumeContentInference.EnumerateCandidatesIn(scanned))
                {
                    if (result.Count >= MaxNearbyCandidates)
                    {
                        return result;
                    }

                    if (candidate.Size < NearbyMinimumBytes || !seen.Add(candidate.Path))
                    {
                        continue;
                    }

                    if (alreadyCollected.Any(c => string.Equals(c.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    result.Add(candidate);
                }
            }

            return result;
        }

        /// <summary>父目录这一家里的粗筛下限：小于它的文件不可能是分卷片（几 KB 的说明文件 / 图片）。</summary>
        private const long NearbyMinimumBytes = 16 * 1024;

        /// <summary>
        /// 找卷窗口里**往下递归的层数**：自己这一层算**第 1 层**，所以这个值 = 能看到"子目录的子目录"
        /// （第 2、3 层）。
        ///
        /// <para><b>为什么定 3</b>（用户 2026-10-05 口径：「它的子文件夹（递归，⛔ 不是只看直接子目录）」）：
        /// 他给的那个形状里，<c>111\222.zip</c> 解开之后第 1 卷落在 <c>111\222\333\</c> ——
        /// 那正是"自己这一层（<c>111</c>）往下第 3 层"，只看直接子目录（旧行为）永远够不着。
        /// 再深就当**判不出**（⛔ 什么都不做）：越往下越接近"替用户满盘找文件"（§8），
        /// 而且每多一层，邻近目录里**别的包**的卷被拖进候选池的概率就高一截
        /// （7z 那条路"尺寸排序 + 试开"的排列数会跟着涨）。</para>
        /// </summary>
        private const int NearbyMaxSubdirectoryLevels = 3;

        /// <summary>
        /// 这一卷的名字里，**卷标记段或归档后缀段**是不是"要**重建**才读得出来"的
        /// （<c>444.pa8rt1.rar</c> / <c>333.7z.0a0b1</c> / <c>222.z0删1</c> / <c>set.7aaaaz.002</c>）。
        ///
        /// <para>要重建的名字**必须**再过「整组自洽」（<see cref="TryConfirmSelfConsistentVolumeGroup"/>）
        /// 才允许改名 —— 用户 2026-10-04 第六轮把这条闸门从 partN 那一档**扩到全部"要重建"的名字**：
        /// 形状判据只回答"它像什么"，**够不够改**要另有一条与名字无关的证据
        /// （组自洽 / 硬链接试开），判不出 ⇒ 什么都不做。</para>
        ///
        /// <para>⛔ 判据只有两个既有出口（<see cref="ExtensionHelper.IsVolumeMarkByDisguise"/> 与
        /// <see cref="ExtensionHelper.IsArchiveSegmentByDisguise"/>），⛔ 这里不新造尺子；
        /// ⛔ 逐字干净的名字（<c>x.7z.001</c> / <c>x.7z.002.txt</c>）与**「逐字标记 + 粘着的垃圾」**
        /// （<c>giu910.7z.001删除</c>，2026-09-28 起"只删尾巴不动卷号"的老口径）一律 false，照旧走老口径。</para>
        /// </summary>
        private static bool IsGuessedVolumeName(string fileName) =>
            ExtensionHelper.IsVolumeMarkByDisguise(fileName, out _) ||
            ExtensionHelper.IsArchiveSegmentByDisguise(fileName, out _);

        /// <summary>
        /// 照计划里那把"整组自洽"再判一次（**候选直接给对象**，尺寸由调用方填）。
        ///
        /// <para>为什么要这个重载：跨层收卷那一档的候选**躺在不同的层产物目录里**，而
        /// <see cref="VolumeGroupDetector.Group"/> 的分桶键是「目录 + 基名 + 命名族」——
        /// 直接喂一堆真路径，三片会各自成一个"完整"的单卷桶（一片自己也是"从 1 起、无缺号"），
        /// 这条闸门就退化成恒真。收卷判的本来就是「**移动之后的样子**」：
        /// 调用方因此喂"入口那一层 + 规范卷名"的虚拟候选，尺寸用各片**实际**字节数。</para>
        /// </summary>
        private static bool TryConfirmSelfConsistentVolumeGroup(
            IEnumerable<VolumeCandidate>? candidates,
            string selfPath,
            string expectedBaseName)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(selfPath) || string.IsNullOrWhiteSpace(expectedBaseName))
            {
                return false;
            }

            string selfName = FileNameHelper.GetFileName(selfPath);
            var list = candidates.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path)).ToList();

            if (list.Count == 0)
            {
                return false;
            }

            foreach (VolumeGroup group in VolumeGroupDetector.Group(list))
            {
                if (!group.IsComplete ||
                    !string.Equals(group.BaseName, expectedBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (group.Volumes.Any(v => string.Equals(
                        SafeFileName(v.Path),
                        selfName,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// **整组自洽**（用户 2026-10-04 拍板）：<paramref name="candidatePaths"/> 里能不能配出**一组自洽的兄弟卷**
        /// —— **基名逐字相同 + 卷标记连续（除末片外等大）**，而且 <paramref name="selfPath"/> 必须在这一组里。
        ///
        /// <para>判据整体转调**既有出口** <see cref="VolumeGroupDetector.Group"/>：桶键 = 目录 + 基名 + 命名族
        /// （那一条就是"基名逐字相同"）、<c>IsComplete</c> = 从第 1 卷起**连续无缺号**（"卷标记连续"）、
        /// 尺寸规律 = **除最后一卷外彼此等大**；⛔ 这里不另写一套，⛔ 也不放宽它（认错比不认更糟）。</para>
        ///
        /// <para>候选池**只喂调用方手上那一层**（批首 / 手动那档 = 入口那一卷所在目录；还原工序那档 =
        /// 已经认出来的这一组成员）—— ⛔ 不替用户满盘找文件。量不出大小 ⇒ 尺寸规律不过 ⇒ 判不出
        /// ⇒ **调用方什么都不做**（兜底落在"不改名"那一档）。</para>
        /// </summary>
        private static bool TryConfirmSelfConsistentVolumeGroup(
            IEnumerable<string>? candidatePaths,
            string selfPath,
            string expectedBaseName)
        {
            var candidates = new List<VolumeCandidate>();

            foreach (string? candidate in candidatePaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                candidates.Add(new VolumeCandidate { Path = candidate, Size = LengthOf(candidate) });
            }

            return TryConfirmSelfConsistentVolumeGroup(candidates, selfPath, expectedBaseName);
        }

        /// <summary>父目录这一家里最多收几份候选（防止"父目录是个大杂烩"时白读一大堆文件头）。</summary>
        private const int MaxNearbyCandidates = 500;

        /// <summary>父目录这一家最多看几个子目录。</summary>
        private const int MaxNearbyDirectories = 200;

        /// <summary>
        /// 这个名字是不是**规范名**（一点杂质都没有）：`x.7z.001` / `x.zip` / `x.rar` / `X.part1.rar` ✓；
        /// `x.7z.001.txt`（另起一段的尾巴）/ `x.7z.001删除`（粘着垃圾）/ `x.z0删1`（靠容错才认出来）✗。
        ///
        /// <para><b>为什么必须把它当第二道闸门</b>（用户 2026-10-04）：改名是**不可逆**动作，
        /// 而"建议名"是从名字推出来的 —— 只要它自己还带着脏尾巴，就说明这份计划在拿一个
        /// **没被证实干净**的名字去覆盖一个**可能本来就对**的名字（探针实测：`x.7z.001 → x.7z.001.txt`）。
        /// 判不出 ⇒ 整份计划不成立。</para>
        ///
        /// <para>判据**只转调既有出口**（⛔ 不新造第三把尺子）：末段逐字就是卷标记
        /// （<see cref="ExtensionHelper.IsVolumePartExtension"/>），或者末段逐字是本体后缀（`zip` / `rar`）。
        /// ⚠ 刻意**不用**容错档：`x.7z.001.txt` 在 `IsVolumePartFileName` 那儿是"算分卷"的
        /// （另起一段的尾巴归**还原工序**管），拿它当闸门等于没闸门。</para>
        /// </summary>
        private static bool IsCanonicalVolumeName(string fileName)
        {
            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            string last = parts[^1];

            if (ExtensionHelper.IsVolumePartExtension("." + last))
            {
                return true;
            }

            return last.Equals("zip", StringComparison.OrdinalIgnoreCase)
                   || last.Equals("rar", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 算出改名计划。任何 IO 意外都落成"不能改 + 原因"，绝不抛。
        /// </summary>
        /// <param name="currentPath">那一卷现在的完整路径。</param>
        /// <param name="fileNamesInDirectory">同目录里的文件名（只要名字，不要路径）。</param>
        public static VolumeNameRepairPlan Plan(string? currentPath, IEnumerable<string?>? fileNamesInDirectory)
        {
            string path = currentPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                return Cannot(path, StatusText.VolumeRepairSourceMissing);
            }

            string fileName;

            try
            {
                if (!File.Exists(path))
                {
                    return Cannot(path, StatusText.VolumeRepairSourceMissing);
                }

                fileName = Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                return Cannot(path, string.Format(StatusText.VolumeRepairPlanFailedFormat, ex.Message));
            }

            /*
             * 「本体是标准名、只有几个续卷的名字被改坏」这一档（用户 2026-10-01 真机 DDD：跨盘 ZIP 分了 4 片，
             * 本体 `222.zip` 干净、三个续卷叫 `222.z0删1` / `222.z除02` / `222.z文03`；7-Zip 报
             * `ERROR = Missing volume : 222.z01` ⇒ 整包解不开、源包四个都不进其余物）。
             *
             * ⛔ 必须排在下面两道门**之前**：那两道门（"名字里得有卷号"、"必须是第 1 卷 / 得找得到兄弟"）
             * 都是为"**本体自己**被改坏"设计的，而这一档里本体本来就是对的 —— 走到下面只会被
             * `FindSiblingVolumes`（**按标准名**找兄弟）判成"没有兄弟"，于是整组一个名字都不改。
             */
            VolumeNameRepairPlan? disguisedVolumes =
                PlanDisguisedVolumesBesideStandardSelf(path, fileName, fileNamesInDirectory);

            if (disguisedVolumes != null)
            {
                return disguisedVolumes;
            }

            /*
             * 「名字里得有卷号、而且必须是第 1 卷」这两条是**这道门的核心**：
             * 名字里没有卷号的文件（普通包）改名只会把它弄坏；不是第一卷的（.002 之类）
             * 改名解决不了"缺第一卷"的问题 —— 那种情况该做的是把它放回原组，不是改它的名字。
             */
            int? index = VolumeGroupDetector.TryGetVolumeIndex(fileName);

            if (index == null)
            {
                return Cannot(path, StatusText.VolumeRepairNotAVolumeName);
            }

            /*
             * 先看"整组名字都被缀了垃圾"这一档（2026-09-28 真机：百度网盘给每个分卷名缀「删除」）。
             * ⚠ 必须排在"必须是第 1 卷"与"得有兄弟卷"这两道门**之前** —— 带垃圾的组里，
             * 第 2/3 卷也有名字要改，而且 `RawSplitStreamDetector.FindSiblingVolumes` 认不出它们
             * （它按标准名找兄弟），排后面就永远走不到。
             * 判据只有一条：这一段的卷标记后面粘着垃圾，去掉垃圾就是标准名 —— 只删尾巴、不动卷号。
             */
            VolumeNameRepairPlan? groupPlan = PlanJunkTailGroup(path, fileName, fileNamesInDirectory);

            if (groupPlan != null)
            {
                return groupPlan;
            }

            if (index.Value != 1)
            {
                return Cannot(path, StatusText.VolumeRepairNotFirstVolume);
            }

            IReadOnlyList<string> siblings = RawSplitStreamDetector.FindSiblingVolumes(fileNamesInDirectory, path);

            if (siblings.Count == 0)
            {
                return Cannot(path, StatusText.VolumeRepairNoSiblings);
            }

            string suggested = RawSplitStreamDetector.SuggestStandardFirstName(siblings);

            if (string.IsNullOrWhiteSpace(suggested))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            if (string.Equals(suggested, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return Cannot(path, StatusText.VolumeRepairAlreadyStandard);
            }

            /*
             * ⛔ **第二道闸门（用户 2026-10-04 点名）：建议名自己必须是规范名。**
             *
             * 现场（2026-10-04 只读探针实测，同一组 `x.7z.001` + `x.7z.002.txt`）：
             * 建议名是从**兄弟卷的名字**推出来的（`TryGetFirstVolumeName('x.7z.002.txt')` ⇒ `x.7z.001.txt`）
             * —— **脏尾巴被原样继承**；而下面那道"兄弟基名逐字相等"的闸门用的又是**剥标记档**
             * （跨段形状两边都退化成 `x`）⇒ 放行 ⇒ 计划把**干净的名字改成脏名字**（方向反了）。
             * 入口换成那个脏兄弟时反而给出正确方向 —— 同一个判据在两种入口下给出相反结论。
             *
             * 判据只转调既有出口（见 <see cref="IsCanonicalVolumeName"/>）：`x.7z.001` / `x.zip` / `X.part1.rar`
             * 算规范；`x.7z.001.txt`（另起一段的尾巴）、`x.7z.001删除`（粘着垃圾）、`x.z0删1` 一律不算
             * ⇒ **整份计划不成立**（判不出就什么都不做，兜底落在"不改名"那一档）。
             */
            if (!IsCanonicalVolumeName(suggested))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            /*
             * ⛔ 第二道闸门：**这份计划只改第一卷**（Items 只有一项）。
             *
             * 如果同目录里的兄弟卷自己也带着垃圾（它们的基名与建议名的基名不一致），那改完第一卷之后
             * 7-Zip 会按**新的**基名去找 `.002` / `.003` —— 找不到就整组打不开：
             * 「改一个」比「一个都不改」更糟（2026-09-29 复查逮到：`全坏.7z(删掉.001` 那一组两卷都带垃圾，
             * 老代码会把第一卷改成 `全坏.7z.001`，剩下两卷还叫 `(删掉` 那套名字）。
             *
             * 判据只读事实：兄弟卷的基名与建议名的基名**必须相等**（剥法只有一处：
             * <see cref="FileNameHelper.StripVolumeMarkers"/>，它连"卷标记里夹的垃圾"一起剥）。
             * 不等 = 它们也得跟着改，而这条路改不到它们 → 退到「什么都不做」。
             * 「能一次把整组改干净」的那一档是 <see cref="PlanJunkTailGroup"/>，它排在这条路**前面** ——
             * 走到这里说明它认不出这个形状（垃圾塞在压缩后缀里，而不是粘在卷标记上）。
             */
            string suggestedStem = FileNameHelper.StripVolumeMarkers(suggested);

            if (siblings.Any(sibling => !string.Equals(
                FileNameHelper.StripVolumeMarkers(sibling),
                suggestedStem,
                StringComparison.OrdinalIgnoreCase)))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string targetPath = Path.Combine(directory, suggested);

            if (File.Exists(targetPath))
            {
                return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, suggested));
            }

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = path,
                CurrentFileName = fileName,
                SuggestedFileName = suggested,
                TargetPath = targetPath,
                Siblings = siblings,
                Items = new[]
                {
                    new VolumeRepairItem
                    {
                        CurrentPath = path,
                        CurrentFileName = fileName,
                        SuggestedFileName = suggested,
                        TargetPath = targetPath
                    }
                }
            };
        }

        /// <summary>
        /// 「**名字完全靠不住**」那一档：内容能回答多少就按内容办 —— 判据与执行体都只有这一份计划
        /// （用户 2026-09-28 三层方案的第 1 步 + 2026-09-29 的 RAR / ZIP 内容级卷号）。
        ///
        /// <para><b>三种格式走三条</b>（按内容挑，⛔ 不按后缀）：</para>
        /// <list type="number">
        /// <item><description><b>7z</b>：内容里**没有卷号**（只有第一卷有魔数），所以只能
        /// "同目录尺寸排候选顺序 + 硬链接试开验证"（<see cref="VolumeProbeVerifier"/>）。</description></item>
        /// <item><description><b>RAR</b>：卷号**写在内容里**（RAR5 在主归档头、RAR 1.5–4.x/RAR4 在卷尾归档结尾块）→
        /// 直接按卷号归组，不需要试开（<see cref="Detection.VolumeNumberFromContent"/>）。</description></item>
        /// <item><description><b>跨盘 ZIP</b>：末片的 EOCD 里有盘号（= 总片数），非末片只有结尾的跨盘标记 →
        /// 只有 2 片时能用消去法定序。</description></item>
        /// </list>
        ///
        /// <para><b>改名永远先过三道</b>：整组卷号必须连成 1..N（内容自洽）、目标名没被占用（⛔ 绝不覆盖）、
        /// 至少有一卷的名字真的要改。任何一道不过 → <c>CanRepair = false</c> + 写明为什么，**一个字节都不动**。</para>
        ///
        /// <para><b>⛔ 工作区根必须由调用方传进来</b>（不变量 12：需要临时物的地方一律由调用方把工作区根传进去，
        /// 传不进来就不做那件事）。7z 那条路要"硬链接 + 引擎列目录"才算得出顺序，而硬链接**不能跨卷** ——
        /// 所以：<b>同盘</b>（工作区根与第一卷在同一个卷）⇒ 探针落在目标工作区里做，这条路照旧可用；
        /// <b>跨盘 / 没传</b> ⇒ **不试开**，如实报"无法确认"，**不出改名计划**（⛔ 不退源卷根、⛔ 不复制、
        /// ⛔ 不往程序目录或 <c>%TEMP%</c> 写）。参数**没有默认值**就是这个意思：每个调用点都必须显式表态，
        /// 不许留下"不传 ⇒ 悄悄退到卷根"的老路。</para>
        /// </summary>
        /// <param name="currentPath">要修的那一卷（组里任意一卷）。</param>
        /// <param name="filesInDirectory">同目录候选（拿不到就传 null）。</param>
        /// <param name="engine">试开用的引擎。</param>
        /// <param name="workRootDirectory">
        /// 这一单的目标工作区根（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）。空 = 调用方拿不到工作区根
        /// ⇒ 不做试开（如实报"无法确认"）。
        /// </param>
        /// <param name="passwordCandidates">
        /// 密码候选（**只给值、只活在内存里**）。跨盘 zip 那一档要用它：加密的包只有用对的密码
        /// 才验得出"这几片的先后对不对"（顺序错与密码错在引擎那儿是两句话，但没密码就两句话都听不到）。
        /// 空 = 手上没有密码 ⇒ 加密的包只能如实报"顺序没法验证"。
        /// </param>
        /// <param name="cancellationToken">取消。</param>
        public static async Task<VolumeNameRepairPlan> PlanByContentAsync(
            string? currentPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates = null,
            CancellationToken cancellationToken = default)
        {
            return await PlanByContentCoreAsync(
                    currentPath,
                    filesInDirectory,
                    engine,
                    workRootDirectory,
                    passwordCandidates,
                    allowNearbyDirectories: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 与 <see cref="PlanByContentAsync"/> 同一份实现，只是**允许候选来自邻近目录**
        /// （调用方用 <see cref="EnumerateVolumeCandidatesNearby"/> 收的池）。
        ///
        /// <para>⛔ 只有**内容能自证身份**的两条路（跨盘 ZIP 的索引、RAR 的内容卷号）才吃放宽的池：
        /// 那两条路上"这一份是不是这一组的"由文件自己的字节回答，邻近目录里别的包的卷**冒充不了**。
        /// <b>7z 不吃</b> —— 它的中间片内容里没有任何身份信息，"尺寸排序 + 试开"在放宽的池子里会把
        /// 别的包的卷一起排进候选排列，既白烧引擎调用、又可能把真正那一组的顺序挤出排列上限
        /// （真机夹具上实测：放宽后两组直接解不出来）。所以 7z 这一档**只用同目录候选**，
        /// 判不出来就如实说判不出来。</para>
        /// </summary>
        public static async Task<VolumeNameRepairPlan> PlanByContentWithNearbyCandidatesAsync(
            string? currentPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates = null,
            CancellationToken cancellationToken = default)
        {
            return await PlanByContentCoreAsync(
                    currentPath,
                    filesInDirectory,
                    engine,
                    workRootDirectory,
                    passwordCandidates,
                    allowNearbyDirectories: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task<VolumeNameRepairPlan> PlanByContentCoreAsync(
            string? currentPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates,
            bool allowNearbyDirectories,
            CancellationToken cancellationToken)
        {
            string path = currentPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Cannot(path, StatusText.VolumeRepairSourceMissing);
            }

            string fileName = Path.GetFileName(path);

            /*
             * ① **专属算法**：跨盘 zip 的"按归档自己的索引定盘"（用户 2026-10-01 真机 `FFF` 那一组 7 片）。
             *
             * 为什么它必须排在下面那三条统一算法的**前面**：跨盘 zip 的末片里有中央目录，
             * 而中央目录的每一条都写着"这个文件的本地头在**第几盘**、离那一盘开头多少字节" ——
             * 那是归档自己说的话，比"名字里的卷号""尺寸规律""试开"都硬，而且**不需要名字、不需要密码**。
             * 名字路/内容路都判不出来的那一档（名字里一个卷号都没有），只有它能判。
             *
             * ⚠ 它**不抢**统一算法的活：这一档不适用（不是跨盘 zip / 片数 < 3 / 剩的候选对不上）时返回 null，
             * 一律原样往下走 —— 老路子（RAR 内容卷号、跨盘 zip 两片消去法、7z 试开）一个字都没动。
             */
            VolumeNameRepairPlan? byIndex = await PlanSpannedZipByIndexAsync(
                path,
                filesInDirectory,
                engine,
                workRootDirectory,
                passwordCandidates,
                cancellationToken).ConfigureAwait(false);

            if (byIndex != null)
            {
                return byIndex;
            }

            /*
             * 按**内容**分派，⛔ 不按后缀、也不按"开头那几个字节"：跨盘 zip 的末片是从数据中间切出来的，
             * 开头根本没有本地文件头（7z 的 -v 切出来就是这样），只有 EOCD 说得清它是什么。
             */
            Detection.VolumeNumberReading self = Detection.VolumeNumberFromContent.Read(path);

            /*
             * ⛔ 7z **不吃放宽的候选池**（理由写在 PlanByContentWithNearbyCandidatesAsync 的注释里）：
             * 它的中间片内容里没有任何身份信息，邻近目录里别的包的卷和它无法区分 ——
             * 放宽只会把别的包拖进"尺寸排序 + 试开"的排列里。这一档**只用同目录候选**。
             */
            IEnumerable<VolumeCandidate>? pool = allowNearbyDirectories
                ? OnlySameDirectory(filesInDirectory, path)
                : filesInDirectory;

            switch (self.Format)
            {
                case Detection.VolumeContentFormat.SevenZip:
                    {
                        /*
                         * **入口判据换对象**（2026-10-03 阶段 B 的 (a)，方案 §0 结论 3）。
                         *
                         * 老口径问的是"**手上这一卷**的名字里有没有卷号"：

                         *     if (VolumeGroupDetector.TryGetVolumeIndex(fileName) != null) return Cannot(AlreadyStandard);
                         *
                         * 于是真机那一档当场被拒 —— `111.7z.001`（标准名第 1 卷）+ `111`（名字整个丢了，
                         * 本该叫 `111.7z.002`）+ `111.7z.003`：手上这一卷的名字**本来就是标准的**，
                         * 而"要改的那一卷"是它旁边那个丢了名字的兄弟，这条路连内容都没看就返回了。
                         *
                         * 新口径问的是"**这一组的名字自不自洽**"（判据只有一处：
                         * VolumeContentInference.ReadSiblingShape 的三条 —— 基名逐字相同 / 卷标记连续 /
                         * 除末片外等大），而且**这一组里得真有一片名字丢了**（NamelessFillers > 0）
                         * ⇒ 这时"缺的那一卷叫什么"是**已知的**，才值得进内容路去定序 + 试开。
                         *
                         * ⛔ 两者都不成立时照旧返回老结论（名字本来就标准、没有要补的名字）——
                         * 这样"一组名字齐全的标准分卷"不会被拖去白试开一次（第 45 条：成功的任务只留一行）。
                         */
                        Detection.SiblingVolumeShape shape = Detection.VolumeContentInference.ReadSiblingShape(
                            path,
                            pool,
                            Detection.VolumeContentInference.ExtensionFor(Detection.VolumeContentFormat.SevenZip));

                        if (VolumeGroupDetector.TryGetVolumeIndex(fileName) != null)
                        {
                            if (!shape.SelfConsistent || shape.NamelessFillers.Count == 0)
                            {
                                return shape.NamelessFillers.Count > 0 && !shape.SelfConsistent
                                    ? Cannot(
                                        path,
                                        string.Format(StatusText.VolumeRepairGroupShapeUnclearFormat, shape.Blocker))
                                    : Cannot(path, StatusText.VolumeRepairAlreadyStandard);
                            }
                        }

                        return await PlanSevenZipByContentAsync(
                                path,
                                path,
                                pool,
                                engine,
                                workRootDirectory,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                case Detection.VolumeContentFormat.Rar:
                    return PlanByNumberedContent(
                        path, filesInDirectory, VolumeNamingFamily.RarPart, Detection.VolumeContentFormat.Rar);

                case Detection.VolumeContentFormat.Zip:
                    return PlanByNumberedContent(
                        path, filesInDirectory, VolumeNamingFamily.ZipSpanned, Detection.VolumeContentFormat.Zip);

                default:
                    /*
                     * 内容不是 RAR / 跨盘 zip 的片，也**没有 7z 起始头** —— 但 7z 的**中间片与末片本来就是裸字节流**
                     * （内容里没有任何身份信息），所以"手上这一卷没有魔数"绝不等于"它不属于任何一组"。
                     *
                     * 真机那一档（`111.7z.001` / `111` / `111.7z.003`）里，用户手上完全可能就是那个名字丢了的
                     * `111`：这时**锚点去同目录找**（有 7z 起始头、基名与它逐字相同、这一组名字自洽、
                     * 而且它就是这组里名字丢了的那一片）⇒ 才按 7z 内容路走，基名从**锚点**推。
                     *
                     * ⛔ 找不到 / 找到两个说不清 ⇒ 照旧如实说"它的内容没说自己是分卷组的一员"，一个字节都不动。
                     */
                    string? sevenZipAnchor = TryFindSevenZipAnchor(path, pool);

                    if (sevenZipAnchor == null)
                    {
                        return Cannot(path, StatusText.VolumeRepairContentNotAVolumeMember);
                    }

                    return await PlanSevenZipByContentAsync(
                            sevenZipAnchor,
                            path,
                            pool,
                            engine,
                            workRootDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 手上这一卷**自己没有 7z 起始头**时，去同目录找这一组的**锚点**（= 带起始头的那一卷 = 第 1 卷）。
        ///
        /// <para>判据四条同时成立，缺一条就不算（⛔ 判不出 ⇒ 返回 null，"什么都不做"）：</para>
        /// <list type="number">
        /// <item><description>它**有 7z 起始头**（魔数，方案 §1.3 第 0 层：只认魔数，⛔ 不按后缀猜）；</description></item>
        /// <item><description>它的**包基名**与手上这一卷逐字相同（唯一出口 <see cref="FileNameHelper.TryResolveVolumeBaseName"/>）；
        /// —— 这一条把"另一个包的同目录文件"挡在门外，而且只用名字，不读写别的文件；</description></item>
        /// <item><description>这一组的**名字自洽**且**真有一片名字丢了**（<see cref="Detection.VolumeContentInference.ReadSiblingShape"/>）；</description></item>
        /// <item><description>**手上这一卷正是那一片名字丢了的成员**（否则它不属于这一组）。</description></item>
        /// </list>
        ///
        /// <para>找到两个锚点 ⇒ 说不清 ⇒ 返回 null（判不出就不做）。</para>
        /// </summary>
        private static string? TryFindSevenZipAnchor(string path, IEnumerable<VolumeCandidate>? filesInDirectory)
        {
            const Detection.VolumeContentFormat format = Detection.VolumeContentFormat.SevenZip;
            string extension = Detection.VolumeContentInference.ExtensionFor(format);
            string directory = Path.GetDirectoryName(path) ?? string.Empty;

            var pool = (filesInDirectory ?? Array.Empty<VolumeCandidate>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
                .Where(c => c.Size > 0)
                .Where(c => string.Equals(
                    Path.GetDirectoryName(c.Path), directory, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string? selfBase = TryResolvePackageBaseName(path, extension);

            if (selfBase == null)
            {
                return null;
            }

            if (!pool.Any(c => SamePath(c.Path, path)))
            {
                pool.Add(new VolumeCandidate { Path = path, Size = LengthOf(path) });
            }

            string? found = null;

            foreach (VolumeCandidate candidate in pool)
            {
                if (SamePath(candidate.Path, path)
                    || Detection.VolumeContentInference.SniffFormat(candidate.Path) != format
                    || !string.Equals(
                        TryResolvePackageBaseName(candidate.Path, extension),
                        selfBase,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Detection.SiblingVolumeShape shape = Detection.VolumeContentInference.ReadSiblingShape(
                    candidate.Path, pool, extension);

                if (!shape.SelfConsistent
                    || shape.NamelessFillers.Count == 0
                    || !shape.NamelessFillers.Any(f => SamePath(f.Path, path)))
                {
                    continue;
                }

                if (found != null)
                {
                    // 同目录里有两个都说得通的锚点 ⇒ 说不清，判不出（⛔ 不挑一个）。
                    return null;
                }

                found = candidate.Path;
            }

            return found;
        }

        /// <summary>包基名（唯一基名出口的 <see cref="VolumeBaseNameLevel.PackageName"/> 档）；判不出返回 null。</summary>
        private static string? TryResolvePackageBaseName(string? path, string archiveExtension) =>
            !string.IsNullOrWhiteSpace(path)
            && FileNameHelper.TryResolveVolumeBaseName(
                path,
                VolumeBaseNameLevel.PackageName,
                out string baseName,
                out _,
                archiveExtension)
            && baseName.Length > 0
                ? baseName
                : null;

        private static long LengthOf(string? path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? 0 : new FileInfo(path!).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>只留"与被修那一卷同目录"的候选（7z 那一档专用：它的内容里没有身份信息，不吃放宽的池）。</summary>
        private static IEnumerable<VolumeCandidate> OnlySameDirectory(
            IEnumerable<VolumeCandidate>? candidates,
            string currentPath)
        {
            string directory = Path.GetDirectoryName(currentPath) ?? string.Empty;

            return (candidates ?? Array.Empty<VolumeCandidate>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
                .Where(c => string.Equals(
                    Path.GetDirectoryName(c.Path),
                    directory,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// 试拼时最多允许"定不下来"的片数：3 片 = 6 种排列，再多就不是"试一下"而是暴力搜索了
        /// （每一种排列都要让引擎真解码一遍整组；一组 675 MB 的包，一次就是几十秒）。
        /// </summary>
        private const int SpannedZipTrialMaxUndecidedDisks = 3;

        /// <summary>
        /// **专属算法：跨盘 zip 按归档自己的索引定盘**（用户 2026-10-01 真机 <c>FFF</c> 那一组）。
        ///
        /// <para>三步，一步比一步软：</para>
        /// <list type="number">
        /// <item><description><b>索引定盘（硬）</b>：末片的中央目录写着"每个文件的本地头在**第几盘**、
        /// 离那一盘开头多少字节" ⇒ 把每个候选文件当第 k 盘，去那个偏移处看是不是本地头、名字对不对得上。
        /// 对得上就是它 —— <b>不看名字、不用引擎、不用密码</b>。全钉住时直接出计划。</description></item>
        /// <item><description><b>缺卷（也硬）</b>：某一盘有锚点、可整个目录里没有一份对得上 ⇒ **那一片不在手上**，
        /// 如实点名缺第几片（⛔ 不是"判不出"）。</description></item>
        /// <item><description><b>剩下的几片（软）</b>：某一片里"一个文件都没开始"（整段夹在别的数据中间）⇒
        /// 它没有身份证，和另一片同样空白的片**在字节上完全对称**。这时才请引擎**试拼**：
        /// 几片就是几种排列（上限 3 片），硬链接进工作区、按标准卷名排好，让引擎**测试**一遍
        /// （顺序错了必然 CRC 错；⛔ 列目录没用 —— 中央目录在末片里、不看中间几片的数据）。</description></item>
        /// </list>
        ///
        /// <para>⛔ 这一档**不抢**统一算法的活：不适用（不是跨盘 zip / 片数 &lt; 3 / 候选对不上号）时返回
        /// <c>null</c>，调用方原样往下走；判不出来时也**只改名一个字节都不动**。</para>
        /// </summary>
        private static async Task<VolumeNameRepairPlan?> PlanSpannedZipByIndexAsync(
            string path,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates,
            CancellationToken cancellationToken)
        {
            List<VolumeCandidate> candidates = (filesInDirectory ?? Array.Empty<VolumeCandidate>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path))
                .ToList();

            if (candidates.Count < 3)
            {
                // 两片那一档统一算法本来就会（消去法），这一档不抢。
                return null;
            }

            /*
             * ① 同目录里找出"自述是跨盘 zip 末片"的那一份 —— 判据**只有一处**：
             * VolumeNumberFromContent.Read（EOCD 盘号 > 0 ⇒ Number = 盘号 + 1）。
             * 目录里出现两个"末片"就说不清哪一片才是末片 ⇒ 不抢，交给统一算法（它也会如实拒绝）。
             */
            Detection.VolumeNumberReading? tail = null;

            foreach (VolumeCandidate candidate in candidates)
            {
                Detection.VolumeNumberReading reading = Detection.VolumeNumberFromContent.Read(candidate.Path);

                if (reading.Format != Detection.VolumeContentFormat.Zip
                    || !reading.IsVolumeMember
                    || reading.Number == null)
                {
                    continue;
                }

                if (tail != null)
                {
                    return null;
                }

                tail = reading;
            }

            if (tail == null)
            {
                /*
                 * 末片不在了吗？（用户要的"说清缺的是第几片"）
                 *
                 * 手上的证据只有两条，但两条都硬：① 这些片**彼此等大**（跨盘 zip 的非末片都是切分上限那么大的满片）；
                 * ② 其中一片的**开头就是跨盘标记** `PK\x07\x08`（真 PKZIP / WinRAR 造的跨盘 zip 第 1 片长这样）。
                 * 合起来只有一个解释：这是一组跨盘 zip，缺的是**末片**（`.zip` 那一片，它有中央目录、是解压入口）。
                 * ⛔ 这一档不试开、不改名 —— 只是把"缺什么"说清楚，免得用户去满盘找一个根本不缺的中间片。
                 */
                VolumeNameRepairPlan? missingTail = DescribeMissingSpannedTail(path, candidates);

                if (missingTail != null)
                {
                    return missingTail;
                }

                return null;
            }

            Detection.SpannedZipIndex? index = Detection.SpannedZipIndex.TryRead(tail.Path);

            if (index == null || index.DiskCount < 3)
            {
                return null;
            }

            Detection.SpannedZipPin pin = index.Pin(candidates);

            /*
             * ⛔ 不自洽（候选池里多了/少了文件、末片比满片还大）⇒ 判不出这一档，原样交给统一算法。
             * 那**不是**"缺卷"的结论，⛔ 不能拿它去吓用户。
             */
            if (!pin.Consistent || !IsInSpannedGroup(pin, path))
            {
                return null;
            }

            // ② 有锚点却没人对得上 ⇒ 那几片**不在手上**：如实点名（这是结论，不是"判不出"）。
            if (pin.MissingDisks.Count > 0)
            {
                return Cannot(
                    path,
                    string.Format(
                        StatusText.VolumeRepairSpannedZipMissingFormat,
                        pin.DiskCount,
                        pin.MissingDisks.Count,
                        string.Join("、", pin.MissingDisks.Select(d => (d + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))));
            }

            IReadOnlyList<string>? ordered = pin.AllPinned
                ? pin.Slots.Select(s => s!).ToList()
                : null;
            bool attempted = false;

            // ③ 还有几片定不下来 ⇒ 请引擎试拼（排列数有上限）。
            if (ordered == null)
            {
                if (pin.UndecidedDisks.Count > SpannedZipTrialMaxUndecidedDisks)
                {
                    return Cannot(
                        path,
                        string.Format(
                            StatusText.VolumeRepairSpannedZipUndecidedFormat,
                            pin.DiskCount,
                            pin.UndecidedDisks.Count));
                }

                var verifier = new VolumeProbeVerifier(engine);
                VolumeProbeOutcome probe = await verifier
                    .VerifySpannedZipOrderAsync(
                        BuildSpannedZipOrders(pin),
                        passwordCandidates,
                        workRootDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);

                attempted = probe.Attempted;

                if (!probe.Confirmed)
                {
                    /*
                     * ⛔ "没试"与"试过不成立"必须分开说（与 7z 那条路同一口径）：
                     * 没有工作区根 / 跨盘 ⇒ 一次都没试，只能如实报"无法确认"。
                     */
                    return probe.Attempted
                        ? Cannot(path, string.Format(StatusText.VolumeRepairContentProbeFailedFormat, probe.Reason), trialAttempted: true)
                        : Cannot(path, string.Format(StatusText.VolumeRepairNoProbeFormat, probe.Reason));
                }

                ordered = probe.OrderedVolumes.Select(v => v.Path).ToList();
            }

            // ④ 基名从**末片**推（它的标准名就是 `X.zip`），卷名拼法只有 BuildStandardNames 一处。
            if (!Detection.VolumeNumberFromContent.TryDeriveStem(
                    tail.Path, Detection.VolumeContentFormat.Zip, out string stem))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion, trialAttempted: attempted);
            }

            IReadOnlyList<string> targetNames = Detection.VolumeNumberFromContent.BuildStandardNames(
                stem, VolumeNamingFamily.ZipSpanned, ordered.Count);

            if (targetNames.Count != ordered.Count)
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion, trialAttempted: attempted);
            }

            var order = new Detection.VolumeGroupOrder
            {
                Confirmed = true,
                Slots = ordered
                    .Select((volume, i) => new Detection.VolumeGroupSlot { Path = volume, Number = i + 1 })
                    .ToList(),
                Fail = Detection.VolumeNumberFail.None,
                Detail = ordered.Count
            };

            // 入口那一卷 = 末片（引擎拿 `X.zip` 打开这一组，兄弟卷要在它旁边）。
            return BuildPlanFromOrder(
                path,
                order,
                targetNames,
                entryVolumePath: ordered[^1],
                trialAttempted: attempted);
        }

        /// <summary>
        /// 「末片不在了」这一档的诊断（**只出结论、不动任何文件**）。
        ///
        /// <para>判据两条，都要：① 同目录里至少 2 份文件**彼此等大**（满片规律 —— 跨盘 zip 除末片外都是满片）；
        /// ② 其中**恰好一份**的开头是跨盘标记 + 本地头（<c>PK\x07\x08PK\x03\x04</c>，实测真 PKZIP / WinRAR 的第 1 片）。
        /// 两条同时成立而目录里又没有一个自述是末片的文件 ⇒ 缺的就是末片。</para>
        ///
        /// <para>⚠ 这里用的是"**满片规律**"而不是"末卷更小"：后者**不是普遍成立**
        /// （实测 WinRAR 在条目比切分大小还大时，末片可以比满片大一倍）—— 拿它当闸门会误判。</para>
        /// </summary>
        private static VolumeNameRepairPlan? DescribeMissingSpannedTail(string path, List<VolumeCandidate> candidates)
        {
            List<VolumeCandidate> files = candidates
                .Where(c => c.Size > 0)
                .ToList();

            if (files.Count < 2)
            {
                return null;
            }

            long full = files
                .GroupBy(c => c.Size)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .First()
                .Key;

            List<VolumeCandidate> fullParts = files.Where(c => c.Size == full).ToList();

            if (fullParts.Count < 2 || fullParts.Count != files.Count)
            {
                // 不是"清一色满片"（有别的尺寸混着）⇒ 说不清，交给统一算法。
                return null;
            }

            if (!fullParts.Any(c => SamePath(c.Path, path)))
            {
                return null;
            }

            List<string> marked = fullParts.Where(StartsWithSpannedMarker).Select(c => c.Path).ToList();

            if (marked.Count != 1)
            {
                return null;
            }

            return new VolumeNameRepairPlan
            {
                CanRepair = false,
                CurrentPath = path,
                CurrentFileName = SafeFileName(path),
                Reason = string.Format(
                    StatusText.VolumeRepairSpannedZipTailMissingFormat,
                    fullParts.Count,
                    full,
                    SafeFileName(marked[0])),
                Siblings = fullParts.Select(c => SafeFileName(c.Path)).ToList(),
                SpannedTailMissing = true
            };
        }

        /// <summary>这一份的开头是不是"跨盘标记 + 本地头"（<c>PK\x07\x08PK\x03\x04</c>）。</summary>
        private static bool StartsWithSpannedMarker(VolumeCandidate candidate)
        {
            try
            {
                using var stream = new FileStream(
                    candidate.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var head = new byte[8];

                if (stream.Read(head, 0, 8) < 8)
                {
                    return false;
                }

                return head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x07 && head[3] == 0x08
                    && head[4] == 0x50 && head[5] == 0x4B && head[6] == 0x03 && head[7] == 0x04;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把"已经钉住的盘"与"剩下那几片的排列"拼成一份份**完整顺序**（每份都是 第 1 片 … 末片）。
        /// 只有定不下来的盘数 ≤ 3 时才会被调用（最多 6 份）。
        /// </summary>
        private static IReadOnlyList<IReadOnlyList<string>> BuildSpannedZipOrders(Detection.SpannedZipPin pin)
        {
            var results = new List<IReadOnlyList<string>>();
            string?[] slots = pin.Slots.ToArray();
            List<int> free = pin.UndecidedDisks.ToList();
            List<string> pool = pin.UndecidedCandidates.ToList();

            void Fill(int position, bool[] taken, string[] current)
            {
                if (position == free.Count)
                {
                    var snapshot = new string[slots.Length];

                    for (int i = 0; i < slots.Length; i++)
                    {
                        int freeIndex = free.IndexOf(i);
                        snapshot[i] = freeIndex >= 0 ? current[freeIndex] : slots[i]!;
                    }

                    results.Add(snapshot);
                    return;
                }

                for (int i = 0; i < pool.Count; i++)
                {
                    if (taken[i])
                    {
                        continue;
                    }

                    taken[i] = true;
                    current[position] = pool[i];
                    Fill(position + 1, taken, current);
                    taken[i] = false;
                }
            }

            if (free.Count == 0 || pool.Count != free.Count)
            {
                return Array.Empty<IReadOnlyList<string>>();
            }

            Fill(0, new bool[pool.Count], new string[free.Count]);

            return results;
        }

        /// <summary>手上的这一份在不在这一组里（钉住的片、或者"定不下来"的那几片之一）。</summary>
        private static bool IsInSpannedGroup(Detection.SpannedZipPin pin, string path) =>
            pin.Slots.Any(s => SamePath(s, path)) || pin.UndecidedCandidates.Any(p => SamePath(p, path));

        private static bool SamePath(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a)
            && !string.IsNullOrWhiteSpace(b)
            && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// **内容里带卷号**那一档（RAR 与跨盘 ZIP，用户 2026-09-29 任务）。
        ///
        /// <para>与 7z 那条最大的不同：这里**不靠猜、不用试开** —— 卷号是内容自己说的，
        /// 所以只要"整组连成 1..N"这一条自洽就够。认不出来（头截断 / CRC 对不上 / 绝对卷号连不成 1..N /
        /// RAR 1.5–4.x（RAR4）的基数两种解释都成立 / 跨盘 zip 片数 ≥ 3）一律拒绝，原样不动。</para>
        /// </summary>
        private static VolumeNameRepairPlan PlanByNumberedContent(
            string path,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            VolumeNamingFamily family,
            Detection.VolumeContentFormat format)
        {
            var readings = new List<Detection.VolumeNumberReading>();

            foreach (VolumeCandidate candidate in filesInDirectory ?? Array.Empty<VolumeCandidate>())
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
                {
                    continue;
                }

                readings.Add(Detection.VolumeNumberFromContent.Read(candidate.Path));
            }

            Detection.VolumeGroupOrder order = Detection.VolumeNumberFromContent.ResolveGroup(
                path,
                readings,
                requireCurrentFirstVolume: false);

            if (!order.Confirmed || order.Count < 2)
            {
                return Cannot(path, DescribeNumberedContentFail(order.Fail, order.Detail));
            }

            /*
             * 基名从"这一组里最像标准名的那一卷"推：跨盘 zip 取末片（它的标准名就是 `X.zip`），
             * RAR 取第 1 卷（`X.part1.rar`）。⛔ 名字只影响"改完像不像人写的"，正确性由卷号与"绝不覆盖"钉着。
             */
            string stemSource = family == VolumeNamingFamily.ZipSpanned
                ? order.Slots[^1].Path
                : order.Slots[0].Path;

            if (!Detection.VolumeNumberFromContent.TryDeriveStem(stemSource, format, out string stem))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            IReadOnlyList<string> targetNames = Detection.VolumeNumberFromContent.BuildStandardNames(stem, family, order.Count);

            if (targetNames.Count != order.Count)
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion);
            }

            /*
             * 入口那一卷（引擎要打开的那一份，同时也是"计划里当前这一项"）：
             *   · 跨盘 zip = 末片（`X.zip`）—— 中央目录在它身上，7-Zip 从它开始拼这一组；
             *   · RAR = **调用方手上那一卷**（方案 §4 阶段 C）：族标记 `partN` 已经把"第 1 卷是谁"
             *     写死了，计划的目标名仍从第 1 卷推（上面那句），⛔ 不因为入口不是第 1 卷就少改一卷。
             *     ⚠ 老写法在这里传的是第 1 卷 ⇒ 手上是 `.part2.rar` 时计划里的"当前项"指向别人的名字，
             *     调用方（`TryApply` / 任务路径同步）拿它当入口就会指到一个"名字本来就对"的卷上。
             */
            string entryVolumePath = family == VolumeNamingFamily.ZipSpanned ? stemSource : path;

            return BuildPlanFromOrder(path, order, targetNames, entryVolumePath: entryVolumePath);
        }

        /// <summary>
        /// 7z：内容认第一卷 + 同目录尺寸规律（或**两卷形状**，用户 2026-09-29 放宽）
        /// + **起始头自述的整包字节数**（2026-10-03 阶段 B）+ 硬链接试开验证（决定性）。
        /// </summary>
        /// <param name="anchorPath">
        /// **锚点** = 带 7z 起始头的那一卷 = 第 1 卷。⛔ 基名只从它推（方案 §4 阶段 B 的 (b)）：
        /// 名字丢了的那一片（<c>111</c>）身上没有任何"这一组叫什么"的信息，拿它推基名等于把标准名建立在
        /// 一个名字已经丢了的东西上。⛔ 引擎也只从这里开（找兄弟卷只看入口旁边那一层）。
        /// </param>
        /// <param name="entryPath">
        /// 调用方**手上这一卷**（可能就等于锚点，也可能就是那一片名字丢了的中间卷 —— 真机形状）。
        /// 它只用于"这句话该怎么说"（计划的 <c>CurrentPath</c> 与日志里的名字），⛔ 不参与定名。
        /// </param>
        private static async Task<VolumeNameRepairPlan> PlanSevenZipByContentAsync(
            string anchorPath,
            string entryPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            CancellationToken cancellationToken)
        {
            Detection.VolumeContentFormat format = Detection.VolumeContentFormat.SevenZip;

            IReadOnlyList<VolumeCandidate> candidates =
                Detection.VolumeContentInference.BuildCandidates(anchorPath, filesInDirectory);

            /*
             * 「值不值得试开一次」的闸门 = **两张门票取或**（用户 2026-09-29 放宽，理由写在
             * `VolumeContentInference.HasTwoVolumeShapeEvidence` 的注释里）：
             *   ① 尺寸规律：有与第一卷等长的满片；
             *   ② 两卷形状：一个等长的都没有 —— 那正是"第一卷满片 + 末卷是余量"这一组
             *      （现场 `amb909.7.01` 2 GiB + `amb909.z.2` 1.89 GB）。老口径只认 ①，
             *      于是这一组报「同目录里也没有找到像后续卷的文件」，而兄弟卷就躺在同一个目录里。
             *
             * ⛔ 放宽的只是**"敢不敢试一次"**这一道：成不成立仍然只由下面的硬链接试开回答，
             * 试不出来就一个字节都不动（判据仍然只有一处，就是这两张门票 + 试开）。
             */
            if (!Detection.VolumeContentInference.HasVolumeSizePattern(anchorPath, candidates)
                && !Detection.VolumeContentInference.HasTwoVolumeShapeEvidence(anchorPath, candidates))
            {
                return Cannot(entryPath, StatusText.VolumeRepairContentNoSizePattern);
            }

            /*
             * **起始头那条硬证据**（2026-10-03 阶段 B 的 (d)，方案 §1 表格 ①）：
             * `32 + NextHeaderOffset + NextHeaderSize` = 整包应当有的字节数，**与顺序无关**。
             * 拿它与"手上这几卷加起来"比，三档各有各的用处（⛔ 全都只读，⛔ 不拿它猜"哪一卷是第几卷"）：
             *   · **小于** ⇒ 缺卷，如实报"还差多少字节"，一次引擎都不调；
             *   · **正好** ⇒ 字节数这一条证据成立（"整组齐了"），顺序仍由名字 / 试开回答；
             *   · **大于** ⇒ 候选池里混进了不属于这一组的文件 ⇒ **缩池重来**（只接受唯一说得通的那种缩法）。
             */
            Detection.SevenZipByteBudget budget = Detection.SevenZipStartHeader.Measure(
                anchorPath,
                new[] { anchorPath }.Concat(candidates.Select(c => c.Path)));

            IReadOnlyList<VolumeCandidate> pool = candidates;

            if (budget.Known && budget.Verdict == Detection.SevenZipByteBudgetVerdict.Excess)
            {
                IReadOnlyList<VolumeCandidate>? shrunk = TryShrinkToExactByteBudget(
                    anchorPath, candidates, budget.ExpectedBytes);

                if (shrunk == null)
                {
                    return Cannot(
                        entryPath,
                        string.Format(
                            StatusText.VolumeRepairContentBytesExcessFormat,
                            budget.ExpectedBytes,
                            budget.ActualBytes,
                            budget.DifferenceBytes),
                        byteBudgetMismatch: true);
                }

                pool = shrunk;

                budget = Detection.SevenZipStartHeader.Measure(
                    anchorPath,
                    new[] { anchorPath }.Concat(pool.Select(c => c.Path)));
            }

            if (budget.Known && budget.Verdict == Detection.SevenZipByteBudgetVerdict.Short)
            {
                return Cannot(
                    entryPath,
                    string.Format(
                        StatusText.VolumeRepairContentBytesMissingFormat,
                        budget.ExpectedBytes,
                        budget.ActualBytes,
                        budget.DifferenceBytes),
                    byteBudgetMismatch: true);
            }

            IReadOnlyList<IReadOnlyList<VolumeCandidate>> orderings =
                Detection.VolumeContentInference.BuildOrderings(anchorPath, pool);

            var verifier = new VolumeProbeVerifier(engine);
            VolumeProbeOutcome probe = await verifier
                .VerifyAsync(anchorPath, orderings, cancellationToken, workRootDirectory)
                .ConfigureAwait(false);

            if (!probe.Confirmed || probe.OrderedVolumes.Count < 2)
            {
                /*
                 * ⛔ "没试"与"试过不成立"必须分开说（用户 2026-09-30 红线：工作区只准设在解压的地方）：
                 * 这条路**没有目标目录** ⇒ 没有工作区根 ⇒ 一次都不试开（见 VolumeProbeVerifier）。
                 * 那一档只能如实报"无法确认"，⛔ 不许写成"试开没通过"；结论照旧是**不改名**。
                 * `TrialAttempted` 也跟着如实走 —— 它的唯一含义就是"试开真跑过"。
                 */
                return probe.Attempted
                    ? Cannot(
                        entryPath,
                        string.Format(StatusText.VolumeRepairContentProbeFailedFormat, probe.Reason),
                        trialAttempted: true)
                    : Cannot(
                        entryPath,
                        string.Format(StatusText.VolumeRepairNoProbeFormat, probe.Reason),
                        trialAttempted: false);
            }

            /*
             * 基名从**锚点**推（⛔ 不从"手上这一卷"推）：试开把顺序钉住了，第 1 卷就是锚点自己
             * （`VerifyAsync` 永远把锚点放在第一位），它的名字才是这一组标准名的来源。
             */
            string stemSource = probe.OrderedVolumes[0].Path;

            if (!Detection.VolumeNumberFromContent.TryDeriveStem(stemSource, format, out string stem))
            {
                return Cannot(entryPath, StatusText.VolumeRepairNoSuggestion, trialAttempted: true);
            }

            IReadOnlyList<string> targetNames = Detection.VolumeNumberFromContent.BuildStandardNames(
                stem, VolumeNamingFamily.SevenZipNumbered, probe.OrderedVolumes.Count);

            string directory = Path.GetDirectoryName(stemSource) ?? string.Empty;
            var items = new List<VolumeRepairItem>();
            int primary = -1;

            for (int i = 0; i < probe.OrderedVolumes.Count && i < targetNames.Count; i++)
            {
                string source = probe.OrderedVolumes[i].Path;
                string target = Path.Combine(directory, targetNames[i]);

                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * 这一卷的名字**本来就是对的**（真机那一档的第 1 卷 `111.7z.001` 与末卷 `111.7z.003`）：
                     * 留在组里（调用方要按整份计划同步任务路径），但⛔ 不改它 —— 老写法在这里整份拒掉，
                     * 于是"只差中间那一卷名字"的组一个名字都改不成。
                     */
                    items.Add(new VolumeRepairItem
                    {
                        CurrentPath = source,
                        CurrentFileName = Path.GetFileName(source),
                        SuggestedFileName = targetNames[i],
                        TargetPath = target
                    });

                    continue;
                }

                // ⛔ 绝不覆盖：任何一个目标名被占，整组不改。
                if (File.Exists(target))
                {
                    return Cannot(
                        entryPath,
                        string.Format(StatusText.VolumeRepairTargetTakenFormat, targetNames[i]),
                        trialAttempted: true);
                }

                items.Add(new VolumeRepairItem
                {
                    CurrentPath = source,
                    CurrentFileName = Path.GetFileName(source),
                    SuggestedFileName = targetNames[i],
                    TargetPath = target
                });

                if (primary < 0 || SamePath(source, entryPath))
                {
                    primary = i;
                }
            }

            if (primary < 0)
            {
                // 组里没有一卷真的要改（名字本来就都对）⇒ 什么都不做。
                return Cannot(entryPath, StatusText.VolumeRepairAlreadyStandard, trialAttempted: true);
            }

            VolumeRepairItem self = items[primary];

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = self.CurrentPath,
                CurrentFileName = self.CurrentFileName,
                SuggestedFileName = self.SuggestedFileName,
                TargetPath = self.TargetPath,
                Siblings = items.Select(i => i.CurrentFileName).ToList(),
                Items = items,
                TrialAttempted = true,
                ProbeNeedsPassword = probe.NeedsPassword
            };
        }

        /// <summary>
        /// 「手上的字节数**多于**整包自述的字节数」⇒ **缩池重来**（方案 §4 阶段 B 的 (d) 第三档）。
        ///
        /// <para>7z 的 <c>-v</c> 切法给了一个很硬的形状：**除末片外每一片彼此等大**，
        /// 所以"多出来的"无非两种：多算了那个更短的候选（它不是末片），或者多算了一个满片
        /// （它不是这一组的卷）。于是只试这两种**各去掉一份**的缩法，而且要求**只有一种**能对上整包字节数 ——
        /// 两种都对得上、或者一种都对不上 ⇒ 返回 null（判不出 ⇒ 上层如实报"多出多少字节"、一个字节不动）。</para>
        ///
        /// <para>⚠ 满片有 <b>两份以上</b>时"去掉哪一份"无从知道（它们一样大），所以那一档也返回 null ——
        /// ⛔ 不挑一个去试。</para>
        /// </summary>
        private static IReadOnlyList<VolumeCandidate>? TryShrinkToExactByteBudget(
            string anchorPath,
            IReadOnlyList<VolumeCandidate> candidates,
            long expectedBytes)
        {
            long anchorSize = LengthOf(anchorPath);

            if (anchorSize <= 0)
            {
                return null;
            }

            List<VolumeCandidate> full = candidates.Where(c => c.Size == anchorSize).ToList();
            List<VolumeCandidate> rest = candidates.Where(c => c.Size != anchorSize).ToList();

            var exact = new List<List<VolumeCandidate>>();

            // ① 去掉"最像末片的那一个"（更短的那些里最大的那一个）。
            VolumeCandidate? partial = rest
                .Where(c => c.Size < anchorSize)
                .OrderByDescending(c => c.Size)
                .ThenBy(c => SafeFileName(c.Path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (partial != null)
            {
                List<VolumeCandidate> withoutPartial = candidates
                    .Where(c => !SamePath(c.Path, partial.Path))
                    .ToList();

                if (SumBytes(anchorPath, withoutPartial) == expectedBytes)
                {
                    exact.Add(withoutPartial);
                }
            }

            // ② 去掉一个满片（只有"满片恰好一份"时才知道去掉的是哪一个）。
            if (full.Count == 1)
            {
                List<VolumeCandidate> withoutFull = candidates
                    .Where(c => !SamePath(c.Path, full[0].Path))
                    .ToList();

                if (withoutFull.Count > 0 && SumBytes(anchorPath, withoutFull) == expectedBytes)
                {
                    exact.Add(withoutFull);
                }
            }

            if (exact.Count != 1)
            {
                return null;
            }

            return exact[0];
        }

        /// <summary>锚点 + 这几份的字节数之和（读不到任何一份 ⇒ 0，调用方按"对不上"处理）。</summary>
        private static long SumBytes(string anchorPath, IReadOnlyList<VolumeCandidate> volumes)
        {
            long total = LengthOf(anchorPath);

            if (total <= 0)
            {
                return 0;
            }

            foreach (VolumeCandidate volume in volumes)
            {
                long size = LengthOf(volume.Path);

                if (size <= 0)
                {
                    return 0;
                }

                total += size;
            }

            return total;
        }

        /// <summary>
        /// 由"内容定好序的整组"造计划：卷号升序、逐卷算目标名，⛔ 目标名被占 → 整组不改。
        ///
        /// <para><b>散在两层目录里的组会被"收"到一起</b>（用户 2026-10-01：「绝大多数只会在一个父文件夹和
        /// 父文件夹的同级子文件夹当中」）：目标目录一律取**引擎要打开的那一卷所在的目录**
        /// （跨盘 zip 是末片 <c>X.zip</c>，RAR / 7z 是第 1 卷）——
        /// 引擎找兄弟卷**只看入口文件旁边那一层**，散着放即使名字都对也解不开（实测 7-Zip 就是这样）。
        /// ⛔ 只搬**同一卷**上的（跨盘搬不动，也绝不做跨盘复制）：任何一卷与目标目录不同卷 ⇒ 整组不改。</para>
        ///
        /// <para>要改的第一卷（<c>CurrentPath</c>）优先取**调用方手上那一卷**（它在组里时），
        /// 否则取第一个真要改名的 —— <see cref="TryApply"/> 拿这一卷当入口，指到一个"名字本来就对"的卷上
        /// 会直接判"问题不在名字上"，整组就白算了。</para>
        /// </summary>
        private static VolumeNameRepairPlan BuildPlanFromOrder(
            string path,
            Detection.VolumeGroupOrder order,
            IReadOnlyList<string> targetNames,
            string entryVolumePath,
            bool trialAttempted = false,
            bool probeNeedsPassword = false)
        {
            // 目标目录 = 入口那一卷所在的那一层（引擎就在那儿找兄弟卷）。
            string targetDirectory = Path.GetDirectoryName(entryVolumePath)
                ?? Path.GetDirectoryName(path)
                ?? string.Empty;
            var items = new List<VolumeRepairItem>();
            int primary = -1;
            int gathered = 0;

            for (int i = 0; i < order.Count; i++)
            {
                string source = order.Slots[i].Path;

                if (!Detection.VolumeContentInference.IsSameVolumeRoot(source, targetDirectory))
                {
                    // 跨盘：搬不过去，也⛔ 绝不复制大文件 ⇒ 整组不改（判不出就不做）。
                    return Cannot(path, string.Format(
                        StatusText.VolumeRepairGatherAcrossVolumeFormat,
                        SafeFileName(source),
                        SafeFileName(entryVolumePath)));
                }

                string directory = Path.GetDirectoryName(source) ?? targetDirectory;

                if (!string.Equals(directory, targetDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    gathered++;
                }

                string target = Path.Combine(targetDirectory, targetNames[i]);

                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    // 这一卷的名字本来就是对的：留在组里（调用方要按它同步任务路径），但不改。
                    items.Add(new VolumeRepairItem
                    {
                        CurrentPath = source,
                        CurrentFileName = Path.GetFileName(source),
                        SuggestedFileName = targetNames[i],
                        TargetPath = target
                    });

                    continue;
                }

                if (File.Exists(target))
                {
                    return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, targetNames[i]));
                }

                items.Add(new VolumeRepairItem
                {
                    CurrentPath = source,
                    CurrentFileName = Path.GetFileName(source),
                    SuggestedFileName = targetNames[i],
                    TargetPath = target
                });

                if (primary < 0 || string.Equals(source, path, StringComparison.OrdinalIgnoreCase))
                {
                    primary = i;
                }
            }

            if (primary < 0)
            {
                return Cannot(path, StatusText.VolumeRepairAlreadyStandard);
            }

            VolumeRepairItem self = items[primary];

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = self.CurrentPath,
                CurrentFileName = self.CurrentFileName,
                SuggestedFileName = self.SuggestedFileName,
                TargetPath = self.TargetPath,
                Siblings = items.Select(i => i.CurrentFileName).ToList(),
                Items = items,
                TrialAttempted = trialAttempted,
                ProbeNeedsPassword = probeNeedsPassword,
                GatheredVolumes = gathered
            };
        }

        /// <summary>内容级认不出来时给用户的那句话（判据在 <see cref="Detection.VolumeNumberFromContent"/>，文案只有这一处）。</summary>
        private static string DescribeNumberedContentFail(Detection.VolumeNumberFail fail, int detail) => fail switch
        {
            Detection.VolumeNumberFail.RarOldNumbering => StatusText.VolumeRepairContentRarOldNumbering,
            Detection.VolumeNumberFail.RarFirstVolumeMismatch => StatusText.VolumeRepairContentRarFirstVolumeMismatch,
            Detection.VolumeNumberFail.GroupBaseAmbiguous => StatusText.VolumeRepairContentRarBaseAmbiguous,
            Detection.VolumeNumberFail.ZipSingleDisk => StatusText.VolumeRepairContentZipSingleDisk,
            Detection.VolumeNumberFail.ZipTooManyDisks =>
                string.Format(StatusText.VolumeRepairContentZipTooManyDisksFormat, detail),
            Detection.VolumeNumberFail.ZipPartsMissing =>
                string.Format(StatusText.VolumeRepairContentZipPartsMissingFormat, detail),
            Detection.VolumeNumberFail.CurrentNotFirstVolume => StatusText.VolumeRepairNotFirstVolume,
            Detection.VolumeNumberFail.CurrentNotInGroup => StatusText.VolumeRepairContentCurrentNotInGroup,
            Detection.VolumeNumberFail.GroupIncomplete or Detection.VolumeNumberFail.GroupNotContiguous =>
                string.Format(StatusText.VolumeRepairContentGroupNotContiguousFormat, detail),
            _ => StatusText.VolumeRepairContentNotAVolumeMember
        };

        /// <summary>
        /// 「**本体是标准名，只有几个续卷的名字被改坏**」这一档的计划（用户 2026-10-01 真机 DDD）。
        ///
        /// <para>现场：跨盘 ZIP 分了 4 片 —— 本体 `222.zip`（标准名，7-Zip 打开这一组的入口），
        /// 三片续卷叫 `222.z0删1` / `222.z除02` / `222.z文03`（字被塞进卷标记里）。7-Zip 找 `222.z01` 找不到 ⇒
        /// 「分卷压缩包缺少必要分卷」⇒ 整包解不开。用户原话：「我这次将 zip 多分了几个卷你就弄不了了」。</para>
        ///
        /// <para>判据（只用名字，不读内容、不试开）：</para>
        /// <list type="number">
        /// <item><description>自己**不是**"被伪装的卷名"（那种形状归 <see cref="PlanJunkTailGroup"/> 管，它在另一条路上）；</description></item>
        /// <item><description>自己的基名（剥卷标记 + 剥已知归档后缀，唯一出口 <see cref="OutputPlacement.ResolveArchiveBaseName"/>）
        /// 与某个同目录兄弟"去杂质之后的基名"**逐字相等**；</description></item>
        /// <item><description>这些兄弟去杂质之后得到的**规范卷标记互不相同**（两个文件还原成同一个卷名 ⇒ 有歧义 ⇒ 整组不动）；</description></item>
        /// <item><description>每一卷的目标名**都没被占用**（⛔ 绝不覆盖；占了一个 ⇒ 整组不动）。</description></item>
        /// </list>
        ///
        /// <para>⛔ 与其余改名路同一条纪律：只改名字、内容一个字节不动；全成或全不成（执行体仍是
        /// <see cref="TryApply"/>，中途失败倒序改回原名）。</para>
        /// </summary>
        private static VolumeNameRepairPlan? PlanDisguisedVolumesBesideStandardSelf(
            string path,
            string fileName,
            IEnumerable<string?>? fileNamesInDirectory)
        {
            // ① 自己带垃圾 ⇒ 不是这一档（那是 PlanJunkTailGroup 的形状）。
            if (TrySplitDisguised(fileName, out _, out _))
            {
                return null;
            }

            /*
             * ⛔ 2026-10-04：**同档比较**（"哪一档比就跟同一档比"）。
             *
             * 老写法是 `PackageName(本体)` 比 `DisguisedVolume(兄弟)` —— **跨档**：这两档对
             * "保不保留归档后缀段"这一格答得不一样（包名档：不保留 `x`；伪装卷档：保留 `x.7z`），
             * 于是 7z / rar 形状**永远配不上**（`111.7z.001` + `111.7z.002.txt` 判 `CanRepair=False`），
             * 只有 zip 形状恰好撞对（两边都掉后缀）。同一份判据"同形状换后缀就换结论"。
             *
             * 现在两边都取**锚点那一档**：本体名（`x.zip` / `x.rar`，后缀段属于它自己）⇒ 剥掉后缀段；
             * 末段逐字就是卷标记（`x.7z.001`）⇒ **保留**后缀段（`x.7z`，与兄弟卷的伪装卷档同解）。
             * ⛔ 两个用途（归组键 / 落点名）的语义一个字没动 —— 这里只用同一个出口算、不合并档位。
             */
            string selfBase = ResolveAnchorBaseName(fileName);

            if (selfBase.Length == 0)
            {
                return null;
            }

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            var items = new List<VolumeRepairItem>();
            var marks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? sibling in fileNamesInDirectory ?? Array.Empty<string?>())
            {
                if (string.IsNullOrWhiteSpace(sibling))
                {
                    continue;
                }

                if (!TrySplitDisguised(sibling, out string siblingBase, out string siblingMark) ||
                    !string.Equals(siblingBase, selfBase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(sibling, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 两个文件去杂质之后是同一个卷名 ⇒ 推不出"谁是谁"，整组不动（判不出就不改）。
                if (!marks.Add(siblingMark))
                {
                    return Cannot(path, StatusText.VolumeRepairNoSuggestion);
                }

                string targetName = selfBase + "." + siblingMark;

                if (File.Exists(Path.Combine(directory, targetName)))
                {
                    return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, targetName));
                }

                items.Add(new VolumeRepairItem
                {
                    CurrentPath = Path.Combine(directory, sibling),
                    CurrentFileName = sibling,
                    SuggestedFileName = targetName,
                    TargetPath = Path.Combine(directory, targetName)
                });
            }

            if (items.Count == 0)
            {
                return null;
            }

            /*
             * 第一卷先改（`TryApply` 按 CurrentPath/TargetPath 起手，再走 Items 里剩下的）。
             * ⚠ CurrentPath 必须指向**真要改的那一卷**，不能指向调用方手上那个已经标准名的本体：
             * TryApply 见到"源 == 目标"会直接判 AlreadyStandard 并把整份计划拒掉。
             */
            List<VolumeRepairItem> ordered = items
                .OrderBy(i => i.CurrentFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            /*
             * ⛔ **兄弟卷的名字是靠"猜"出来的 ⇒ 必须再过「整组自洽」**（用户 2026-10-04 第六轮：
             * 「兜底仍要有"敢不敢改"的证据 …… 判不出 ⇒ 什么都不做」）。
             *
             * 这一档（本体/入口是标准名、脏的是兄弟卷）原先只有"基名对得上"一条证据；而
             * `set.7aaaaz.002` 这种**后缀段靠容错/骨架档猜出来**的名字，光凭基名吻合不足以证明
             * "这一堆文件真是一组卷"—— 判据与 `PlanJunkTailGroup` 那道**完全同一份**
             * （<see cref="IsGuessedVolumeName"/> + <see cref="TryConfirmSelfConsistentVolumeGroup"/>），
             * ⛔ 不新造第二套；⛔ 逐字干净的兄弟卷（`x.7z.002` / `x.7z.002.txt`）照旧不受影响。
             */
            if (ordered.Any(i => IsGuessedVolumeName(i.CurrentFileName)) &&
                !TryConfirmSelfConsistentVolumeGroup(
                    (fileNamesInDirectory ?? Array.Empty<string?>())
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => Path.Combine(directory, n!)),
                    path,
                    selfBase))
            {
                return Cannot(path, StatusText.VolumeRepairNoSiblings);
            }

            VolumeRepairItem first = ordered[0];

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = first.CurrentPath,
                CurrentFileName = first.CurrentFileName,
                SuggestedFileName = first.SuggestedFileName,
                TargetPath = first.TargetPath,
                Siblings = ordered.Select(i => i.CurrentFileName).ToList(),
                Items = ordered
            };
        }

        /// <summary>
        /// **锚点那一卷的组基名**（<see cref="PlanDisguisedVolumesBesideStandardSelf"/> 用来跟兄弟卷
        /// **同一档**比的那个量）：
        /// <list type="bullet">
        /// <item><description>本体名（<c>x.zip</c> / <c>x.rar</c>，末尾一段是后缀不是卷标记）⇒
        /// 后缀段属于本体自己 ⇒ 基名 = <c>OutputPlacement.ResolveArchiveBaseName</c>（包名档，<c>x</c>）；</description></item>
        /// <item><description>末段**逐字就是卷标记**（<c>x.7z.001</c>）⇒ 后缀段属于整组 ⇒ 基名 =
        /// <see cref="FileNameHelper.StripVolumeMarkers"/>（剥标记档，<c>x.7z</c>，与兄弟卷的伪装卷档同解）。</description></item>
        /// </list>
        ///
        /// <para>⛔ 判据只转调既有出口，不新造尺子；⛔ 这里**不合并**组键与包名两个用途
        /// （见 <c>VolumeBaseNameLevel</c> 上的说明）。</para>
        /// </summary>
        private static string ResolveAnchorBaseName(string fileName)
        {
            string[] parts = fileName.Split('.');
            string last = parts.Length > 0 ? parts[^1] : string.Empty;

            return last.Length > 0 && ExtensionHelper.IsVolumePartExtension("." + last)
                ? FileNameHelper.StripVolumeMarkers(fileName)
                : OutputPlacement.ResolveArchiveBaseName(fileName);
        }

        /// <summary>
        /// 「整组名字的卷号后面都粘着垃圾」这一档的计划：<c>giu910.7z.001删除</c> →
        /// <c>giu910.7z.001</c>、<c>.002删除</c> → <c>.002</c>……一次把整组改回标准名。
        ///
        /// <para>⛔ 只删尾巴、**绝不动卷号**；任何一卷的目标名已被占用 → 整组不改（宁可不做，也不覆盖）；
        /// 只有一卷需要改时也算这一档（用户点一下就好）。返回 null 表示"不是这一档"，交给老的
        /// 「第一卷名字被改坏」那条路。</para>
        /// </summary>
        private static VolumeNameRepairPlan? PlanJunkTailGroup(
            string path,
            string fileName,
            IEnumerable<string?>? fileNamesInDirectory)
        {
            /*
             * 跨盘 zip 例外（用户 2026-09-29 真样本）：这一族的**整组名字只能靠内容定** ——
             * 末片叫 `.zip`（7-Zip 打开这一组的入口），之前的片叫 `.z01`/`.z02`……，
             * 而"哪一片才是末片"名字里根本没有（真样本第一片 `222.z0删除1`、末片 `222.z1111ip`）。
             * 按名字把每片还原成自己的卷标记只会得到 `222.z01` + `222.z11` ——
             * 那是一个 7-Zip 永远打不开的组（改错名字比不改更糟）。
             *
             * 所以：内容是**跨盘 zip 的成员**时，这条名字路让位给 PlanByContentAsync
             * （调用方在"推不出标准名"时会接着走内容那条路，判据与执行体仍只有一份）。
             *
             * ⚠ 但"让位"必须看**内容路真能不能拼出组**（2026-09-29 复核补的那半条）：
             * 7-Zip 自己造的 `-tzip -v` 分卷 zip 长这样 —— 第 1 片开头是本地文件头、后面几片是从数据中间切开的，
             * 而**末片的 EOCD 写的是"盘号 0 / 总盘数 0"**（7-Zip 就是这么写的，它不是 PKZIP 那套跨盘 EOCD）。
             * 内容路因此认不出它（末片自述"单盘"），可名字路才是它的正路：`enc.zip.001删除` 里
             * 卷标记 `001` 明明白白，去掉垃圾就是 7-Zip 认的标准名。
             * 老写法只看"手上这一片像不像 zip 成员"就让位 → 内容路拼不出组 → **两边都不动**，
             * 接着 7-Zip 打不开这一组、任务报「密码错误」（实测踩到）。
             * 判据：**目录里存在一片自述"带盘号的跨盘 zip 末片"**（`Number != null`）才让位。
             */
            if (IsDisguisedZipMember(path) && HasSpannedZipTailInDirectory(path, fileNamesInDirectory))
            {
                return null;
            }

            if (!TrySplitDisguised(fileName, out string baseName, out string _))
            {
                return null;
            }
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            var items = new List<VolumeRepairItem>();

            foreach (string? sibling in fileNamesInDirectory ?? Array.Empty<string?>())
            {
                if (string.IsNullOrWhiteSpace(sibling))
                {
                    continue;
                }

                // 同一组：同一个基名 + 同一种伪装形状（都带点段尾巴，或都不带）
                if (!TrySplitDisguised(sibling, out string siblingBase, out string siblingMark) ||
                    !string.Equals(siblingBase, baseName, StringComparison.OrdinalIgnoreCase) ||
                    !LooksLikeSameFamilyShape(fileName, sibling))
                {
                    continue;
                }

                string targetName = siblingBase + "." + siblingMark;

                if (string.Equals(targetName, sibling, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string current = Path.Combine(directory, sibling);
                string target = Path.Combine(directory, targetName);

                if (File.Exists(target))
                {
                    return Cannot(path, string.Format(StatusText.VolumeRepairTargetTakenFormat, targetName));
                }

                items.Add(new VolumeRepairItem
                {
                    CurrentPath = current,
                    CurrentFileName = sibling,
                    SuggestedFileName = targetName,
                    TargetPath = target
                });
            }

            if (items.Count == 0)
            {
                return null;
            }

            // 自己必须在里面（调用方给的这一卷就是要修的那一卷）
            VolumeRepairItem self = items.FirstOrDefault(
                i => string.Equals(i.CurrentFileName, fileName, StringComparison.OrdinalIgnoreCase))!;

            if (self == null)
            {
                return null;
            }

            var ordered = items
                .OrderBy(i => i.CurrentFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            /*
             * ⛔ **名字要"重建"才读得出来的那一档必须先过"整组自洽"**（用户 2026-10-04 第四轮起、第六轮扩面）：
             *
             * ① partN 骨架那一档（`444.pa8rt1.rar`）：同一段也可能是**基名自己的一段**
             *    （`444.p1art2.part2.rar` 里的 `p1art2`）—— 形状分不开；
             * ② **卷标记段要重建的数字族**（`333.7z.0a0b1` ⇒ `001`、`222.z0删1` ⇒ `z01`）：
             *    孤立一个也会被形状判据说成"第 1 卷"；
             * ③ **归档后缀段要重建的**（`set.7aaaaz.002` ⇒ `set.7z.002`）：后缀段的真实身份同样是猜的。
             *
             * ⇒ 三者只听**整组**的话：同一目录里配得出一组自洽的兄弟卷（基名逐字相同 + 卷标记连续 +
             * 除末片外等大）才允许改；**配不出 ⇒ 一个字都不改**（判不出 ⇒ 什么都不做）。
             * ⛔ 判据只有 <see cref="IsGuessedVolumeName"/> 一处（转调两个既有出口），
             * ⛔ 逐字干净的名字（`x.7z.001` / `x.7z.002.txt`）与**「逐字标记 + 粘着的垃圾」**
             * （`giu910.7z.001删除`，老口径）都不进这道闸门，一个字节的老口径都不动。
             */
            if (ordered.Any(i => IsGuessedVolumeName(i.CurrentFileName)) &&
                !TryConfirmSelfConsistentVolumeGroup(
                    ordered
                        .Select(i => i.CurrentPath)
                        .Concat((fileNamesInDirectory ?? Array.Empty<string?>())
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => Path.Combine(directory, n!))),
                    path,
                    baseName))
            {
                return Cannot(path, StatusText.VolumeRepairNoSiblings);
            }

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = self.CurrentPath,
                CurrentFileName = self.CurrentFileName,
                SuggestedFileName = self.SuggestedFileName,
                TargetPath = self.TargetPath,
                Siblings = ordered.Select(i => i.CurrentFileName).ToList(),
                Items = ordered
            };
        }

        /// <summary>
        /// 内容是**跨盘 zip 的成员**（判据只在 <see cref="Detection.VolumeNumberFromContent"/> 那一处，
        /// 这里只问结论）。⛔ 单盘 zip（EOCD 盘号 0）不算 —— 那种包的名字本来就该由别的路管。
        /// </summary>
        private static bool IsDisguisedZipMember(string path)
        {
            Detection.VolumeNumberReading reading = Detection.VolumeNumberFromContent.Read(path);

            return reading.Format == Detection.VolumeContentFormat.Zip && reading.IsVolumeMember;
        }

        /// <summary>
        /// 同目录里有没有**自述带盘号的跨盘 zip 末片** —— 有它，内容级那条路才真的拼得出整组
        /// （名字路这时必须让位，否则会把 `222.z0删除1` + `222.z1111ip` 还原成 7-Zip 永远打不开的
        /// `222.z01` + `222.z11`）。
        ///
        /// <para>⛔ 反过来不成立：7-Zip 自己造的 `-tzip -v` 分卷 zip，末片 EOCD 写的是"盘号 0"，
        /// 内容级认不出来 —— 那种组必须留给名字路（卷标记就在名字里）。判据只问
        /// <see cref="Detection.VolumeNumberFromContent"/> 的结论，这里不另写一套。</para>
        /// </summary>
        public static bool HasSpannedZipTailNearby(string? anyFileInDirectory) =>
            HasSpannedZipTailInDirectory(
                anyFileInDirectory ?? string.Empty,
                EnumerateFileNamesInDirectory(anyFileInDirectory));

        private static bool HasSpannedZipTailInDirectory(string path, IEnumerable<string?>? fileNamesInDirectory)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;

            foreach (string? name in fileNamesInDirectory ?? Array.Empty<string?>())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                Detection.VolumeNumberReading reading = Detection.VolumeNumberFromContent.Read(
                    Path.Combine(directory, name));

                if (reading.Format == Detection.VolumeContentFormat.Zip
                    && reading.IsVolumeMember
                    && reading.Number != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 把"卷号段 + 粘着的垃圾"拆出来：<c>giu910.7z.001删除</c> → 基名 <c>giu910.7z</c>、垃圾 <c>删除</c>。
        /// 判据转调 <see cref="ExtensionHelper.TrySplitVolumeSegment"/>（只此一处）。
        /// </summary>
        /// <summary>
        /// 两个名字是不是"同一种伪装形状"：都带点段尾巴（<c>001.txt</c>）或都不带（<c>001删除</c>）。
        /// 不这么分，<c>x.7z.001</c> 与 <c>x.7z.002.txt</c> 会被当成同一组的两卷，改出来一半带尾巴一半不带。
        /// </summary>
        private static bool LooksLikeSameFamilyShape(string a, string b) =>
            HasDotTailSegment(a) == HasDotTailSegment(b);

        private static bool HasDotTailSegment(string fileName)
        {
            string[] parts = fileName.Split('.');

            if (parts.Length < 3)
            {
                return false;
            }

            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^1], out _, out _))
            {
                return false;
            }

            return ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^2], out _, out _);
        }

        /// <summary>
        /// 把"被伪装的卷名"拆开：<c>giu910.7z.001删除</c> → 基名 <c>giu910.7z</c>、标准卷段 <c>001</c>；
        /// <c>x.7z.001.txt</c> → 基名 <c>x.7z</c>、卷段 <c>001</c>；<c>y.z0删除3</c> → 基名 <c>y</c>、<c>z03</c>。
        ///
        /// <para>⚠ 2026-10-03 阶段 A 收口：判据整体搬进**唯一基名出口**
        /// <see cref="FileNameHelper.TryResolveVolumeBaseName"/> 的
        /// <see cref="VolumeBaseNameLevel.DisguisedVolume"/> 档，本方法只转调（⛔ 这里不再算一遍基名）。</para>
        /// </summary>
        private static bool TrySplitDisguised(string fileName, out string baseName, out string canonicalSegment) =>
            FileNameHelper.TryResolveVolumeBaseName(
                fileName,
                VolumeBaseNameLevel.DisguisedVolume,
                out baseName,
                out canonicalSegment);

        // ══════════════ 「还原」工序的**递归层挂点**（方案 §2.1 挂点②，用户 2026-10-03）══════════════
        //
        // 顺序不可颠倒：**① 按魔数认出底层 → ② 还原名字 → ③ 才回到第 1 层用该族专属证据重判一次**。
        // 用户原话：「我们首先第一步就是识别底层文件找出伪装文件，然后还原，再接着匹配」。
        //
        // 为什么要有这一步：包**里面**解出来的那一层，过去没有任何一步先擦伪装尾巴
        // （`RecursiveExtractor` 对 `VolumeNameRepair` / `RenameService` 零引用）——
        // 引擎按标准名去找兄弟卷，找不到就只报「分卷缺失」，那 12 GiB 的内容永远出不来
        // （AGENTS.md §11.4 §51 的现场就是它）。
        //
        // ⛔ 铁律（一条都不许放宽）：
        //   ① 只对**我们自己产出的内层包副本**做（调用方传进来的必须是这一层的产物目录里的文件）；
        //      用户源目录那一档归批首的「修正后缀」管（`RenameService.BuildFixByDetectedFormatFileName`）；
        //   ② 只改名、**全成或全不成**、**绝不覆盖**（任何一份的目标名被占 ⇒ 整组一个名字都不改）；
        //   ③ 执行体只有 <see cref="TryApply"/> 一套（只 `File.Move`，改完核对"新名在、旧名没了、字节数不差"，
        //      中途失败倒序改回原名）—— ⛔ 这里不再写第二套改名；
        //   ④ 判据只转调既有那两把尺子：**骨架化**（经唯一基名出口的 `DisguisedVolume` 档）与
        //      **归档体还原**（`ExtensionHelper.TryRecoverDisguisedArchiveBody`），⛔ 不新造第三把；
        //   ⑤ 认出底层**只认魔数**（`VolumeContentInference.SniffFormat`），认不出 ⇒ 什么都不做。

        /// <summary>
        /// 递归层内的「还原」：把这一层里内层包副本的**伪装后缀换成规范形态**（替换，⛔ 不是追加；
        /// 基名一个字不动），然后由调用方**回到第 1 层重判一次**（那一步就是方案说的"回环"）。
        ///
        /// <para>⚠ 7z / 跨盘 zip 的**续卷没有魔数**（只有第 1 卷带魔数），所以"认出底层"这一步
        /// 按组做：自己认不出时退到**同组第 1 卷**那一份的魔数 —— 那仍然是魔数证据，只是取自锚点那一卷。
        /// 两处都认不出 ⇒ 整组什么都不做。</para>
        /// </summary>
        /// <param name="candidatePaths">这一层产物目录里已被认成归档的候选（我们自己解出来的副本）。</param>
        /// <param name="log">日志回调 (级别, 文案)；为空则不写日志。</param>
        /// <returns>还原之后的路径清单（顺序与数量与入参完全一致；没还原的项原样返回）。</returns>
        public static IReadOnlyList<string> RestoreDisguisedInnerPackageNames(
            IReadOnlyList<string>? candidatePaths,
            Action<string, string>? log = null)
        {
            if (candidatePaths == null || candidatePaths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var handledGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? candidate in candidatePaths)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                IReadOnlyList<string> members = EnumerateRestoreGroupMembers(candidate);

                if (members.Count == 0)
                {
                    continue;
                }

                if (!handledGroups.Add(BuildRestoreGroupKey(members)))
                {
                    continue;
                }

                // ① 认出底层（魔数；续卷退到同组第 1 卷那一份）
                VolumeContentFormat format = ResolveRestoreFormat(members);

                if (format == VolumeContentFormat.Unknown)
                {
                    continue;
                }

                /*
                 * ①b ⛔ **「末尾那段本身脏」那一档必须先过"整组自洽"**（用户 2026-10-04 拍板）：
                 * `444.pa8rt1.rar` 这种卷标记是靠**骨架档**才读出来的名字，而同一段也可能是**基名自己的一段**
                 * （`444.p1art2.part2.rar` 里的 `p1art2`）—— 形状分不开，所以只听**整组**的话：
                 * 同一层里配得出一组自洽的兄弟卷才允许还原，**配不出 ⇒ 整组一个字节都不动**。
                 */
                string selfBase = TryResolvePackageBaseName(Path.GetFileName(candidate), out string restoreBase)
                    ? restoreBase
                    : string.Empty;

                if (members.Any(m => ExtensionHelper.IsPartNumberedMarkBySkeleton(Path.GetFileName(m), out _)) &&
                    !TryConfirmSelfConsistentVolumeGroup(members, candidate, selfBase))
                {
                    log?.Invoke(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.InnerRestoreBlockedFormat,
                            string.Join("；", members.Select(m => Path.GetFileName(m))),
                            StatusText.VolumeRepairNoSiblings));

                    continue;
                }

                // ② 算规范名（只换后缀那一段，基名一个字不动）
                string directory = Path.GetDirectoryName(candidate) ?? string.Empty;
                var items = new List<RestoreRenameItem>();

                foreach (string member in members)
                {
                    string memberName = Path.GetFileName(member);

                    if (!TryBuildRestoredName(memberName, format, out string canonical) ||
                        string.Equals(memberName, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    items.Add(new RestoreRenameItem(member, Path.Combine(directory, canonical), memberName, canonical));
                }

                if (items.Count == 0)
                {
                    continue;
                }

                // ③ 全成或全不成 / 绝不覆盖
                string? blocker = DescribeRestoreBlocker(items);

                if (blocker != null)
                {
                    log?.Invoke(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.InnerRestoreBlockedFormat,
                            string.Join("；", items.Select(i => i.FromName)),
                            blocker));
                    continue;
                }

                // ④ 执行体只有既有那一套（TryApply：只 File.Move、字节数核对、失败倒序改回）
                VolumeNameRepairPlan plan = BuildRestorePlan(items);
                VolumeNameRepairResult applied = TryApply(plan);

                if (!applied.Success)
                {
                    log?.Invoke(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.InnerRestoreBlockedFormat,
                            string.Join("；", items.Select(i => i.FromName)),
                            applied.Message));
                    continue;
                }

                foreach (RestoreRenameItem item in items)
                {
                    renamed[item.From] = item.To;
                }

                log?.Invoke(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.InnerRestoreDoneFormat,
                        plan.Describe()));
            }

            var result = new List<string>(candidatePaths.Count);

            foreach (string candidate in candidatePaths)
            {
                result.Add(renamed.TryGetValue(candidate, out string? updated) ? updated : candidate);
            }

            return result;
        }

        /// <summary>一次还原里的一卷：现在叫什么、该叫什么。</summary>
        private sealed record RestoreRenameItem(string From, string To, string FromName, string ToName);

        /// <summary>
        /// 这一份候选所属的**一组**（含自己）：同目录 + 卷序认得出 + **包基名逐字相等**
        /// （判据全在既有出口里：卷序 <see cref="VolumeGroupDetector.TryGetVolumeIndex"/>、
        /// 包基名 <see cref="FileNameHelper.TryResolveVolumeBaseName"/> 的 `PackageName` 档）。
        ///
        /// <para>为什么必须把兄弟一起收进来：7z / 跨盘 zip 的续卷没有魔数、探测器根本不会把它们当候选，
        /// 而引擎找兄弟卷**只看入口文件旁边那一层** —— 只改第一卷的名字，整组照样打不开。</para>
        ///
        /// <para>没有卷号的那种（本体后缀被伪装，<c>222.zscip</c>）自己就是一组。</para>
        /// </summary>
        private static IReadOnlyList<string> EnumerateRestoreGroupMembers(string candidate)
        {
            var members = new List<string> { candidate };

            string candidateName = Path.GetFileName(candidate);
            string directory = Path.GetDirectoryName(candidate) ?? string.Empty;

            if (directory.Length == 0 ||
                VolumeGroupDetector.TryGetVolumeIndex(candidateName) == null ||
                !TryResolvePackageBaseName(candidateName, out string candidateBase))
            {
                return members;
            }

            string[] entries;

            try
            {
                entries = Directory.GetFiles(directory);
            }
            catch
            {
                // 读不了目录 ⇒ 只处理自己（判不出就少做，绝不多做）。
                return members;
            }

            foreach (string entry in entries)
            {
                if (string.Equals(entry, candidate, StringComparison.OrdinalIgnoreCase) ||
                    VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(entry)) == null ||
                    !TryResolvePackageBaseName(Path.GetFileName(entry), out string siblingBase))
                {
                    continue;
                }

                if (string.Equals(siblingBase, candidateBase, StringComparison.OrdinalIgnoreCase))
                {
                    members.Add(entry);
                }
            }

            return members;
        }

        private static bool TryResolvePackageBaseName(string fileName, out string baseName) =>
            FileNameHelper.TryResolveVolumeBaseName(
                fileName,
                VolumeBaseNameLevel.PackageName,
                out baseName,
                out _);

        /// <summary>同一组只还原一次：键 = 目录 + 第 1 卷的包基名。</summary>
        private static string BuildRestoreGroupKey(IReadOnlyList<string> members)
        {
            string directory = Path.GetDirectoryName(members[0]) ?? string.Empty;
            string anchor = members
                .OrderBy(member => VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(member)) ?? int.MaxValue)
                .First();

            TryResolvePackageBaseName(Path.GetFileName(anchor), out string baseName);

            return directory + "|" + baseName;
        }

        /// <summary>
        /// 认出这一组的底层：**只认魔数**。第 1 卷那一份的魔数优先（7z / 跨盘 zip 只有它带魔数）；
        /// 一个魔数都认不出 ⇒ <see cref="VolumeContentFormat.Unknown"/> ⇒ 调用方什么都不做。
        /// </summary>
        private static VolumeContentFormat ResolveRestoreFormat(IReadOnlyList<string> members)
        {
            VolumeContentFormat anyRecognized = VolumeContentFormat.Unknown;

            foreach (string member in members)
            {
                VolumeContentFormat format = VolumeContentInference.SniffFormat(member);

                if (format == VolumeContentFormat.Unknown)
                {
                    continue;
                }

                if (VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(member)) == 1)
                {
                    return format;
                }

                if (anyRecognized == VolumeContentFormat.Unknown)
                {
                    anyRecognized = format;
                }
            }

            return anyRecognized;
        }

        /// <summary>
        /// 算出"规范形态"的文件名：**只换后缀那一段**（替换，⛔ 不是追加），基名一个字不动。
        ///
        /// <para>两条路各自转调一把既有尺子：
        /// ㈠ 卷标记段被伪装（<c>X.part1.rar删除</c> / <c>x.7z.001.txt</c> / <c>111.parst1.racr</c>）
        ///    —— 骨架化，经唯一基名出口的 `DisguisedVolume` 档；
        /// ㈡ 本体后缀被伪装（<c>222.zscip</c>）—— 归档体还原 <c>TryRecoverDisguisedArchiveBody</c>。</para>
        ///
        /// <para>⚠ 还原出来的名字必须与**认出来的底层**同族（RAR 的 <c>partN.rar</c> 不能扣到 7z 内容上），
        /// 族对不上就判"不还原" —— 改错名字比不改更糟（改名不可逆）。</para>
        /// </summary>
        private static bool TryBuildRestoredName(string fileName, VolumeContentFormat format, out string canonical)
        {
            canonical = string.Empty;

            if (FileNameHelper.TryResolveVolumeBaseName(
                    fileName,
                    VolumeBaseNameLevel.DisguisedVolume,
                    out string baseName,
                    out string segment) &&
                baseName.Length > 0 &&
                segment.Length > 0 &&
                IsSegmentFamilyOfFormat(segment, format))
            {
                canonical = baseName + "." + segment;
                return true;
            }

            string extension = VolumeContentInference.ExtensionFor(format);
            string[] parts = fileName.Split('.');

            if (extension.Length == 0 || parts.Length < 2)
            {
                return false;
            }

            if (ExtensionHelper.TryRecoverDisguisedArchiveBody(parts[^1], out string recovered, out _) &&
                string.Equals(recovered, extension, StringComparison.OrdinalIgnoreCase))
            {
                canonical = string.Join('.', parts, 0, parts.Length - 1) + "." + recovered;
                return true;
            }

            return false;
        }

        /// <summary>规范卷段是不是**该族**的写法（`partN.rar` / `NNN` / `zNN`）—— 族对不上就不还原。</summary>
        private static bool IsSegmentFamilyOfFormat(string segment, VolumeContentFormat format)
        {
            if (segment.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                return format == VolumeContentFormat.Rar;
            }

            if (segment.Length == 3 && segment[0] is 'z' or 'Z' &&
                char.IsAsciiDigit(segment[1]) && char.IsAsciiDigit(segment[2]))
            {
                return format == VolumeContentFormat.Zip;
            }

            if (segment.Length == 3 && char.IsAsciiDigit(segment[0]) &&
                char.IsAsciiDigit(segment[1]) && char.IsAsciiDigit(segment[2]))
            {
                return format == VolumeContentFormat.SevenZip;
            }

            return false;
        }

        /// <summary>
        /// 全成或全不成的两道闸门：① 目标名一个都不许被占（⛔ 绝不覆盖）；
        /// ② 两份候选还原之后不许撞成同一个名字。任一不过 ⇒ 返回原因（整组一个都不改）。
        /// </summary>
        private static string? DescribeRestoreBlocker(IReadOnlyList<RestoreRenameItem> items)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RestoreRenameItem item in items)
            {
                if (!targets.Add(item.To))
                {
                    return string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.InnerRestoreCollisionFormat,
                        item.ToName);
                }

                if (File.Exists(item.To))
                {
                    return string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeRepairTargetTakenFormat,
                        item.ToName);
                }
            }

            return null;
        }

        private static VolumeNameRepairPlan BuildRestorePlan(IReadOnlyList<RestoreRenameItem> items)
        {
            List<RestoreRenameItem> ordered = items
                .OrderBy(i => i.FromName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = ordered[0].From,
                CurrentFileName = ordered[0].FromName,
                SuggestedFileName = ordered[0].ToName,
                TargetPath = ordered[0].To,
                Siblings = ordered.Select(i => i.FromName).ToList(),
                Items = ordered
                    .Select(i => new VolumeRepairItem
                    {
                        CurrentPath = i.From,
                        CurrentFileName = i.FromName,
                        SuggestedFileName = i.ToName,
                        TargetPath = i.To
                    })
                    .ToList()
            };
        }

        // ================================================================ 跨层收卷（2026-10-05 真机）

        /// <summary>
        /// 池里的一份候选（路径 + 名字自报的卷号 + 实际字节数）。
        /// 单独一个类型只为了让下面那几段读起来不必到处写三元组。
        /// </summary>
        private readonly record struct GatherPiece(string Path, int Index, long Size);

        /// <summary>
        /// **跨层收卷**的结论（纯事实 + 一张计划；<see cref="ResolveCrossLayerVolumeGather"/> 一个字节都不动）。
        /// </summary>
        internal sealed class CrossLayerVolumeGather
        {
            /// <summary>
            /// 这一档适不适用：手上这一份必须是某组的**第 1 卷**（卷号 == 1）。
            /// 不适用 ⇒ 调用方照旧走今天那条路（⛔ 一个字节都不动、也不免试）。
            /// </summary>
            public bool Applicable { get; init; }

            /// <summary>要把这几卷**搬进入口那一层**（<c>CanRepair = true</c> 才有值）；执行体仍是
            /// <see cref="TryApply"/>（全成或全不成、绝不覆盖、失败倒序回滚）。</summary>
            public VolumeNameRepairPlan? Plan { get; init; }

            /// <summary>这一趟真要动的卷数（搬过来的 + 原地改名的；入口那一卷不算）。</summary>
            public int MovedCount { get; init; }

            /// <summary>入口那一层**已经自证完整** ⇒ 引擎直接解得开（既不用搬、也不该免试）。</summary>
            public bool CompleteBesideEntry { get; init; }

            /// <summary>
            /// **不许再拿单独一片去试**（用户 2026-10-05 拍板）：有硬证据说这一组凑不齐、
            /// 或者凑得齐却收不到入口这一层。判据只有 7z 起始头那条字节数证据（见方法注释的"免试"一节）。
            /// </summary>
            public bool ShouldSkipTrial { get; init; }

            /// <summary>能点名的缺卷（规范卷名；空 = "缺的是末卷之后的卷"，名字里看不出来）。</summary>
            public IReadOnlyList<string> MissingNames { get; init; } = Array.Empty<string>();

            /// <summary>
            /// 这一趟**找卷用的基准**（用户 2026-10-05 口径：手上最浅的那一卷，⛔ 绝不是 001）。
            /// 空 = 这一档没跑到判基准那一步（不适用 / 判不出）。
            /// </summary>
            public string? AnchorPath { get; init; }

            /// <summary>给人看的一句话（收了哪几卷 / 为什么没收 / 缺哪几片）。</summary>
            public string Detail { get; init; } = string.Empty;
        }

        /// <summary>
        /// **跨层收卷**（用户 2026-10-05 真机原话：「各分卷在不同的目录，你就将其全部移动到头文件 .001
        /// 同级目录里面去」）。
        ///
        /// <para><b>现场</b>：三卷 7z 的三片分别躺在**三个不同的层产物目录**里
        /// （<c>layer-001\output\HK.7z.002</c>、<c>layer-002\output\HK.7z.003</c>、
        /// <c>layer-003\output\HK.7z.001</c>）。引擎找兄弟卷**只看入口文件旁边那一层** ——
        /// 所以第 2 层把 <c>HK.7z.001</c> 单独交给 7-Zip 必然打不开，而它回的那句
        /// <c>Open ERROR: Cannot open the file as [7z] archive</c> 还被说成「缺少首卷」（说反了，见
        /// <see cref="Engines.SevenZip.SevenZipOutputParser.LooksLikeMissingVolumePart"/>）——
        /// 整条链判「部分完成」，工作区 7 个文件 / 13.48 GiB 整份删掉，什么都没发布。</para>
        ///
        /// <para><b>三条边界</b>（不可逆动作，兜底一律落"什么都不做"）：</para>
        /// <list type="number">
        /// <item><description>候选池 = **入口自己那一层**（只读，引擎唯一会去找兄弟卷的地方）
        /// + 调用方给的**这条链的各层产物目录**（只读枚举，搬的来源）
        /// + 调用方给的**找卷窗口**（<paramref name="nearbyCandidates"/>；批首那条链给的是
        /// <see cref="EnumerateVolumeCandidatesNearby"/> 收的池，于是"两半分在两个兄弟文件夹里"
        /// 这种真机形状也能收）；
        /// ⛔ 不递归着往下满盘找、⛔ 不含 <c>carved</c> 这种我们自造的兄弟目录、⛔ 不跨盘。</description></item>
        /// <item><description>闸门（全过才动）：**整组自洽**（把"移动之后的样子"虚拟出来交给既有出口
        /// <see cref="VolumeGroupDetector.Group"/>：基名逐字相同 + 卷标记连续 + 完整且含自己）
        /// **并且**（7z）起始头自述的整包字节数 **==** 这几片字节数之和；目标名被占 / 入口自己的名字不标准
        /// / 不同盘 ⇒ **整组不动**。</description></item>
        /// <item><description>只移动**已经判定成这一组成员的那几片**（入口那一卷所在的目录里的文件，
        /// 以及收进来的那几片）。⛔ 绝不覆盖任何已存在的文件（<see cref="TryApply"/> 全成或全不成）。</description></item>
        /// </list>
        ///
        /// <para><b>找卷的基准</b>（用户 2026-10-05 口径：「你要以一开始的分卷文件为准，不要以 001 为准，
        /// 否则这时的第一层父文件夹就探测不到」）：先把手上这几份过一遍，取**最浅的那一卷**当基准
        /// （<see cref="ResolveShallowestAnchor"/>）；基准与入口不在同一层时，**再按基准收一次窗口**
        /// （<see cref="EnumerateVolumeCandidatesNearby"/>）—— 那是"最外层那个分卷的第一层父文件夹"
        /// 唯一能被带出来的地方。⚠ 「修复后缀之后」也不许改用 001 当基准：基准只由**手上这几卷的路径**回答。</para>
        ///
        /// <para><b>免试</b>（同一批用户口径：「要不然你在分开了你还会继续解压单独的001」）：
        /// 入口这一层配不出完整一组、又收不到一起时，**一次引擎调用都不做**，按不变量 7 如实报"缺哪几片"。
        /// ⛔ 只对**有硬证据**的那一档免试：名字自报第 1 卷 + <see cref="SevenZipStartHeader"/> 能读出
        /// 起始头（<c>32 + NextHeaderOffset + NextHeaderSize</c> = 整包应当有的字节数，<c>-mhe</c> 也不加密）。
        /// 其他族（RAR / 跨盘 zip）**本轮不做免试**（它们的内容里没有"整包多长"这个自述数），
        /// 照旧让引擎去试、按引擎原话给结论 —— 那几条既有用例断言的就是那条口径，⛔ 别顺手改。
        /// 起始头读不出来 / 字节数**多于**自述且缩不出唯一解释 ⇒ 判不出 ⇒ 也不免试（照旧解）。</para>
        /// </summary>
        /// <param name="entryVolumePath">引擎这一趟要打开的那一份（这一层的输入；名字自报卷号 1）。</param>
        /// <param name="chainPoolCandidates">
        /// 这条递归链**各层产物目录**里的直接子文件（调用方枚举，通常带真实尺寸）。
        /// 传 null / 空 ⇒ 只看入口自己那一层（不会搬任何东西）。
        /// </param>
        /// <param name="nearbyCandidates">
        /// **找卷窗口**里那一批候选（调用方用 <see cref="EnumerateVolumeCandidatesNearby"/> 收的池，
        /// 基准 = 手上最浅的那一卷）。传 null / 空 ⇒ 与以前逐字一致（只看入口自己那一层 + 链池）。
        /// </param>
        internal static CrossLayerVolumeGather ResolveCrossLayerVolumeGather(
            string? entryVolumePath,
            IEnumerable<VolumeCandidate>? chainPoolCandidates,
            IEnumerable<VolumeCandidate>? nearbyCandidates = null)
        {
            var notApplicable = new CrossLayerVolumeGather { Applicable = false };

            if (string.IsNullOrWhiteSpace(entryVolumePath) || !File.Exists(entryVolumePath))
            {
                return notApplicable;
            }

            string entryPath = SafePathHelper.GetFullPathSafe(entryVolumePath);
            string entryName = SafeFileName(entryPath);
            string entryDirectory = Path.GetDirectoryName(entryPath) ?? string.Empty;

            // 只接"手上这一份就是某组的第 1 卷"：入口是后续卷时该做的事完全不同（缺的是首卷）。
            if (entryDirectory.Length == 0 || VolumeGroupDetector.TryGetVolumeIndex(entryName) != 1)
            {
                return notApplicable;
            }

            VolumeContentFormat format = VolumeContentInference.SniffFormat(entryPath);

            /*
             * 候选池：同族同基名（唯一出口 BelongsToSameGroup）+ 卷号读得出来。
             * ⛔ 两处来源都只"看"：入口那一层用既有的同目录枚举、链池由调用方给。
             *
             * ⚠ **≥ 16 KiB 粗筛只加在"别的层产物目录"那一份上**（用户给的口径：跨目录找卷要挡噪声）——
             * 入口自己那一层**一个都不许滤掉**：那一层正是引擎要读的地方，漏掉一份小的末卷
             * 会让下面的字节数证据判成"缺卷"（免试）而其实它就在旁边。几 KB 的说明文件靠
             * `BelongsToSameGroup`（同族同基名、名字里得有卷标记）挡住，不需要尺寸这一刀。
             */
            var pool = new List<GatherPiece>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Collect(IEnumerable<VolumeCandidate>? candidates, bool applySizeFilter)
            {
                foreach (VolumeCandidate? candidate in candidates ?? Array.Empty<VolumeCandidate>())
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
                    {
                        continue;
                    }

                    string full = SafePathHelper.GetFullPathSafe(candidate.Path);

                    if (full.Length == 0 || !seenPaths.Add(full) || !File.Exists(full))
                    {
                        continue;
                    }

                    string name = SafeFileName(full);
                    int? index = VolumeGroupDetector.TryGetVolumeIndex(name);
                    long size = LengthOf(full);

                    if (index == null
                        || size <= 0
                        || (applySizeFilter && size < NearbyMinimumBytes)
                        || !VolumeGroupDetector.BelongsToSameGroup(entryName, name))
                    {
                        continue;
                    }

                    pool.Add(new GatherPiece(full, index.Value, size));
                }
            }

            Collect(EnumerateVolumeCandidatesInDirectory(entryPath), applySizeFilter: false);
            Collect(chainPoolCandidates, applySizeFilter: true);
            Collect(nearbyCandidates, applySizeFilter: true);

            /*
             * ===== 找卷的基准 = 手上**最浅的那一卷**（用户 2026-10-05 口径）=====
             *
             * ⛔ 绝不是 001：真机上真正该被看见的父文件夹是**最外层那个分卷**的父文件夹。拿一个解出来的
             * `001`（它躺在产物树深处）当基准时，窗口的"第一层父文件夹"成了那个深层目录 ⇒
             * `111\1111.7z.002删除` / `.003删除` 一个都探测不到。
             *
             * 做法：从**手上这几份**（入口 + 链池 + 窗口池）里挑路径最浅的那一卷当基准；
             * 它与入口不在同一层时，**再按基准收一次窗口**（`EnumerateVolumeCandidatesNearby`）——
             * "最外层那个分卷的第一层父文件夹"只有这一条路能带出来。
             *
             * ⚠ **只补一轮**（不迭代）：这一档是加法，多收一轮窗口的代价是目录枚举；
             * 收进来的每一份仍然要过下面那三道闸门（同组 / 卷号一份对一份 / 整组自洽 + 字节数），
             * 判不出就整档不做 —— 兜底永远落在"什么都不做"。
             */
            string anchorPath = ResolveShallowestAnchor(entryPath, pool);

            if (!SameDirectory(anchorPath, entryPath))
            {
                Collect(EnumerateVolumeCandidatesNearby(anchorPath), applySizeFilter: true);
            }

            bool hasEntry = pool.Any(piece => SamePath(piece.Path, entryPath));

            /*
             * 卷号只能一份对一个：同一个卷号上冒出两份候选（同目录里的 `HK.7z.002` 与 `HK.7z.00删除2`、
             * 或者别的分支里另一组同名卷）⇒ 判不出哪一份属于这一组 ⇒ 整档不做（照旧让引擎去判）。
             */
            var byIndex = new Dictionary<int, GatherPiece>();
            bool ambiguousIndex = false;

            foreach (GatherPiece piece in pool
                         .OrderBy(p => p.Index)
                         .ThenBy(p => SafeFileName(p.Path), StringComparer.OrdinalIgnoreCase))
            {
                if (!byIndex.TryAdd(piece.Index, piece))
                {
                    ambiguousIndex = true;
                }
            }

            if (!hasEntry || ambiguousIndex || byIndex.Count == 0 || !byIndex.ContainsKey(1))
            {
                return notApplicable;
            }

            /*
             * 期望基名 = 与 <see cref="VolumeGroupDetector.Group"/> **同一条代码路径**算出来的那个基名
             * （组键那一档本身**不剥卷标记**：`TryResolveVolumeBaseName("HK.7z.001", VolumeGroupKey)`
             * 原样返回 `HK.7z.001`，而组里算出来的是 `HK.7z` —— 必须交给同一个出口判）。
             * 拿不到（这一份的名字/尺寸进不了任何一个桶）⇒ 判不出 ⇒ 这一档整个不做。
             */
            string? expectedBaseName = ResolveGatherBaseName(pool, entryName);

            if (string.IsNullOrWhiteSpace(expectedBaseName))
            {
                return notApplicable;
            }

            /*
             * 规范卷名 = "移动之后该叫什么"：认得出伪装的按既有还原尺子还原（`HK.7z.00删除2` → `HK.7z.002`），
             * 认不出的保持原名。⛔ 不在这里拼名字（`TryBuildRestoredName` 是唯一出口）。
             */
            var targetNames = new Dictionary<int, string>();
            var targetNamesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (GatherPiece piece in byIndex.Values)
            {
                string canonical = SafeFileName(piece.Path);

                if (TryBuildRestoredName(canonical, format, out string restored))
                {
                    canonical = restored;
                }

                if (!targetNamesSeen.Add(canonical))
                {
                    // 两份还原成同一个名字 ⇒ 判不出（⛔ 绝不覆盖）。
                    return notApplicable;
                }

                targetNames[piece.Index] = canonical;
            }

            /*
             * ⛔ **入口那一卷自己的名字**必须是规范名：这一档只搬兄弟卷，**不改入口的名字** ——
             * 调用方手上那个 `item.ArchivePath` 是改名之前抓下来的，改了它就等于带着一个失效的路径
             * 往下走（改名是「还原」工序的活，不是这一档的）。
             */
            if (!SamePath(byIndex[1].Path, entryPath)
                || !SamePath(entryPath, Path.Combine(entryDirectory, targetNames[1])))
            {
                return new CrossLayerVolumeGather
                {
                    Applicable = true,
                    AnchorPath = anchorPath,
                    Detail = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.CrossLayerGatherEntryNameNotCanonicalFormat,
                        entryName)
                };
            }

            List<GatherPiece> beside = byIndex.Values
                .Where(piece => SameDirectory(piece.Path, entryPath))
                .ToList();

            // ① 入口这一层**已经自证完整** ⇒ 什么都不用做（正常分卷 / 完整单卷包走的都是这一支）。
            GroupEvidence besideEvidence = EvaluateGatherGroup(
                beside, entryPath, entryDirectory, expectedBaseName, targetNames, format);

            if (besideEvidence.NeedsNoWork && besideEvidence.Complete)
            {
                return new CrossLayerVolumeGather
                {
                    Applicable = true,
                    AnchorPath = anchorPath,
                    CompleteBesideEntry = true,
                    Detail = besideEvidence.Describe()
                };
            }

            // ② 不齐 / 名字不标准 ⇒ 试着把链池里那几份收过来（纯计划）。
            GroupEvidence allEvidence = EvaluateGatherGroup(
                byIndex.Values.ToList(), entryPath, entryDirectory, expectedBaseName, targetNames, format);

            VolumeNameRepairPlan? plan = null;
            string gatherBlocker;

            if (allEvidence.Complete)
            {
                plan = TryBuildGatherPlan(entryPath, byIndex.Values.ToList(), targetNames, out gatherBlocker);
            }
            else
            {
                gatherBlocker = allEvidence.Describe();
            }

            if (plan is { CanRepair: true })
            {
                return new CrossLayerVolumeGather
                {
                    Applicable = true,
                    AnchorPath = anchorPath,
                    Plan = plan,
                    MovedCount = plan.Items.Count(item => !string.Equals(
                        item.CurrentPath,
                        item.TargetPath,
                        StringComparison.OrdinalIgnoreCase)),
                    Detail = DescribeGatherPlan(plan, entryDirectory)
                };
            }

            /*
             * ③ 收不成 ⇒ 报"缺哪几片"。免试**只认 7z 那条硬证据**（见方法注释）：
             * 起始头读得出来、而且池子自证不是整包（少了字节）或已是整包却收不到一起。
             * 判不出（读不出起始头 / 多出来的那份缩不出唯一解释）⇒ 不免试，照旧让引擎去判。
             */
            bool hardEvidence = format == VolumeContentFormat.SevenZip
                && allEvidence.BytesBudgetKnown
                && !allEvidence.Ambiguous;

            return new CrossLayerVolumeGather
            {
                Applicable = true,
                AnchorPath = anchorPath,
                ShouldSkipTrial = hardEvidence,
                MissingNames = allEvidence.MissingNames,
                Detail = gatherBlocker
            };
        }

        // ================================================================
        // 跨盘 ZIP：末片在手、其余几片散在源目录里（2026-10-05 真机第八批）
        // ================================================================

        /// <summary>**跨盘 ZIP 收卷**的结论（纯事实 + 已经建好的链接；用户的源文件一个字节都不动）。</summary>
        internal sealed class SpannedZipDiskGather
        {
            /// <summary>这一档适不适用：入口必须**自述是跨盘 zip 的末片**（EOCD 盘号 &gt; 0 且总片数 ≥ 2）。</summary>
            public bool Applicable { get; init; }

            /// <summary>入口那一层现在**逐字规范名**地摆着第 1..n-1 片（引擎解得开）。</summary>
            public bool Complete { get; init; }

            /// <summary>入口自述的总片数（含末片）。</summary>
            public int DiskCount { get; init; }

            /// <summary>这一趟真建了几条链接。</summary>
            public int LinkedCount { get; init; }

            /// <summary>
            /// 这一趟建出来的链接**路径**（调用方必须在用完这一层之后把它们删掉 —— 它们是我们的临时名字，
            /// ⛔ 不是内容物：留着会被发布侧当成品搬进用户目录、还会把"半套分卷"闸门自己绊倒，见 2026-10-05 真机）。
            /// </summary>
            public IReadOnlyList<string> LinkedPaths { get; init; } = Array.Empty<string>();

            /// <summary>这几条链接指向的**用户源片路径**（用于"这一组已由谁解出"的记账）。</summary>
            public IReadOnlyList<string> LinkedSources { get; init; } = Array.Empty<string>();

            /// <summary>点名还缺哪几片（规范卷名）。</summary>
            public IReadOnlyList<string> MissingNames { get; init; } = Array.Empty<string>();

            /// <summary>给人看的一句话。</summary>
            public string Detail { get; init; } = string.Empty;
        }

        /// <summary>
        /// **入口是跨盘 ZIP 的末片时，把同一组的其余几片"接"到它旁边**（用户 2026-10-05 真机第八批：
        /// `111.z0删除1/2/3` 三片散在三个源目录里，末片 `111.zip` 压在两层层层加密的 RAR 里）。
        ///
        /// <para><b>为什么只有这一步做得成</b>：批首那一刻末片还在包里（没解出来）⇒ 源包那条路看不见它；
        /// 递归链把末片解出来之后，其余几片又全在用户源目录里（⛔ 那条路的候选池刻意不碰源目录）。
        /// 而末片的 EOCD 是**明文**（`-p` 加密的是数据，不影响它）⇒ 它自己写着"我是第 k 片、一共 n 片" ——
        /// 这是**硬证据**，不是猜（用户原话：「这不是让你看着答案写过程」）。</para>
        ///
        /// <para><b>四条同时成立才动</b>：① 入口自述 `k == n`（它是末片）且 <c>n ≥ 2</c>；
        /// ② 池里能一片对一片地凑出第 1..n-1 片（同一卷号两份候选 ⇒ 判不出 ⇒ 整档不做）；
        /// ③ 除末片外那几片**彼此等大**（既有实测口径：只有这一半真成立）；
        /// ④ **同一卷**（跨盘 ⇒ 判不出 ⇒ 整档不做，⛔ 绝不复制大文件）。</para>
        ///
        /// <para><b>动作 = 硬链接，不是搬、不是复制、不改名</b>：给用户源目录里那几片在**入口那一层**
        /// 多起一个规范卷名（`111.z0删除1` ⇒ 旁边多一个 `111.z01`），零字节、瞬时，
        /// 源文件一个字节不动、名字一个字符不改；链接随工作区收尾一起消失。
        /// ⛔ 绝不把用户的源片搬进工作区（工作区是整份删的 —— 搬进去等于删数据）。</para>
        ///
        /// <para>判不出 / 凑不齐 ⇒ 什么都不做（<see cref="SpannedZipDiskGather.Complete"/> = false，
        /// 如实报缺哪几片）。</para>
        /// </summary>
        internal static SpannedZipDiskGather ResolveSpannedZipDiskGather(
            string? entryPath,
            IEnumerable<VolumeCandidate>? crossCandidates)
        {
            var notApplicable = new SpannedZipDiskGather { Applicable = false };

            if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath))
            {
                return notApplicable;
            }

            Detection.SpannedZipIndex? index = Detection.SpannedZipIndex.TryRead(entryPath);

            // 只有"自述是跨盘 zip 的末片、而且不止一片"才进这一档；单盘 zip / 不是 zip ⇒ 不适用。
            if (index == null || index.DiskCount < 2)
            {
                return notApplicable;
            }

            string entryFull = SafePathHelper.GetFullPathSafe(entryPath);
            string entryName = SafeFileName(entryFull);
            string entryDirectory = Path.GetDirectoryName(entryFull) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(entryName);
            int diskCount = index.DiskCount;

            if (entryDirectory.Length == 0 || baseName.Length == 0)
            {
                return notApplicable;
            }

            /*
             * 入口那一层现在有哪些盘：**只认逐字规范名** —— 脏名（`111.z0删除1`）对引擎不算数，
             * 拿它当"这一片已经在旁边"会让引擎照样报 `Missing volume`。
             */
            var presentSizes = new Dictionary<int, long>();

            foreach (string? name in EnumerateFileNamesInDirectory(entryFull))
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                for (int disk = 1; disk < diskCount; disk++)
                {
                    if (string.Equals(name, CanonicalDiskName(baseName, disk), StringComparison.OrdinalIgnoreCase))
                    {
                        presentSizes[disk] = LengthOf(Path.Combine(entryDirectory, name!));
                    }
                }
            }

            var missing = new List<int>();

            for (int disk = 1; disk < diskCount; disk++)
            {
                if (!presentSizes.ContainsKey(disk))
                {
                    missing.Add(disk);
                }
            }

            if (missing.Count == 0)
            {
                return new SpannedZipDiskGather
                {
                    Applicable = true,
                    Complete = true,
                    DiskCount = diskCount,
                    Detail = StatusText.SpannedZipDisksAlreadyBeside
                };
            }

            // 池里找缺的那几片：一片对一片（同一卷号两份候选 ⇒ 判不出 ⇒ 整档不做）。
            var picked = new Dictionary<int, VolumeCandidate>();
            bool ambiguous = false;
            bool acrossVolume = false;

            foreach (VolumeCandidate? candidate in crossCandidates ?? Array.Empty<VolumeCandidate>())
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path) || !File.Exists(candidate.Path))
                {
                    continue;
                }

                string candidateName = SafeFileName(candidate.Path);

                if (!VolumeGroupDetector.BelongsToSameGroup(entryName, candidateName))
                {
                    continue;
                }

                int? familyIndex = VolumeGroupDetector.TryGetVolumeIndex(candidateName);

                if (familyIndex == null)
                {
                    continue;
                }

                // 这一族的编号：`.zip` = 1（本体 / 末片）、`.zNN` = NN + 1 ⇒ 真实盘序 = 编号 - 1。
                int disk = familyIndex.Value - 1;

                if (disk < 1 || disk >= diskCount || !missing.Contains(disk) || LengthOf(candidate.Path) <= 0)
                {
                    continue;
                }

                if (!HardLinkHelper.CanHardLink(candidate.Path, entryDirectory))
                {
                    // 跨盘：⛔ 不复制大文件 ⇒ 整档不做（如实说）。
                    acrossVolume = true;
                    continue;
                }

                if (picked.TryGetValue(disk, out VolumeCandidate? existing))
                {
                    if (!SamePath(existing.Path, candidate.Path))
                    {
                        ambiguous = true;
                    }

                    continue;
                }

                picked[disk] = candidate;
            }

            if (ambiguous || acrossVolume)
            {
                return new SpannedZipDiskGather
                {
                    Applicable = true,
                    DiskCount = diskCount,
                    MissingNames = missing.Select(disk => CanonicalDiskName(baseName, disk)).ToList(),
                    Detail = ambiguous
                        ? StatusText.SpannedZipDisksAmbiguous
                        : StatusText.SpannedZipDisksAcrossVolume
                };
            }

            List<string> unresolved = missing
                .Where(disk => !picked.ContainsKey(disk))
                .Select(disk => CanonicalDiskName(baseName, disk))
                .ToList();

            if (unresolved.Count > 0)
            {
                return new SpannedZipDiskGather
                {
                    Applicable = true,
                    DiskCount = diskCount,
                    MissingNames = unresolved,
                    Detail = DescribeStillMissing(diskCount, unresolved)
                };
            }

            // ③ 除末片外那几片彼此等大（已有的 + 要接的，一起比）。
            var sizes = new List<long>(presentSizes.Values);

            foreach (VolumeCandidate candidate in picked.Values)
            {
                sizes.Add(LengthOf(candidate.Path));
            }

            if (sizes.Count > 1 && sizes.Distinct().Count() > 1)
            {
                return new SpannedZipDiskGather
                {
                    Applicable = true,
                    DiskCount = diskCount,
                    MissingNames = missing.Select(disk => CanonicalDiskName(baseName, disk)).ToList(),
                    Detail = StatusText.SpannedZipDisksSizeMismatch
                };
            }

            // 动作：给这几片在入口那一层多起一个规范卷名（硬链接；全成或全不成，失败把建好的删掉）。
            var created = new List<string>();

            foreach ((int disk, VolumeCandidate candidate) in picked.OrderBy(pair => pair.Key))
            {
                string canonical = CanonicalDiskName(baseName, disk);
                string target = Path.Combine(entryDirectory, canonical);

                if (File.Exists(target) || !HardLinkHelper.TryCreateHardLink(target, candidate.Path))
                {
                    RollBack();
                    return new SpannedZipDiskGather
                    {
                        Applicable = true,
                        DiskCount = diskCount,
                        MissingNames = new[] { canonical },
                        Detail = File.Exists(target)
                            ? StatusText.SpannedZipDisksTargetOccupied
                            : StatusText.SpannedZipDisksLinkFailed
                    };
                }

                created.Add(target);
            }

            /*
             * 建完**重新读一次盘上事实**：这一组到底齐不齐，只能由那个目录里现在有什么回答 ——
             * ⛔ 不拿"刚刚链接成功了"当结论。
             */
            var nowPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? name in EnumerateFileNamesInDirectory(entryFull))
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    nowPresent.Add(name!);
                }
            }

            var stillMissing = new List<string>();

            for (int disk = 1; disk < diskCount; disk++)
            {
                string canonical = CanonicalDiskName(baseName, disk);

                if (!nowPresent.Contains(canonical))
                {
                    stillMissing.Add(canonical);
                }
            }

            return new SpannedZipDiskGather
            {
                Applicable = true,
                Complete = stillMissing.Count == 0,
                DiskCount = diskCount,
                LinkedCount = created.Count,
                LinkedPaths = created.ToList(),
                LinkedSources = picked.OrderBy(pair => pair.Key).Select(pair => pair.Value.Path).ToList(),
                MissingNames = stillMissing,
                Detail = stillMissing.Count == 0
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.SpannedZipDisksLinkedFormat,
                        diskCount,
                        created.Count)
                    : DescribeStillMissing(diskCount, stillMissing)
            };

            void RollBack()
            {
                foreach (string path in created)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    {
                        // 删不掉就留着 —— 它只是多出来的一个名字，不碰数据。
                    }
                }

                created.Clear();
            }
        }

        /// <summary>这一族第 <paramref name="disk"/> 片的规范卷名（<c>111</c> + 1 ⇒ <c>111.z01</c>）。</summary>
        internal static string CanonicalDiskName(string baseName, int disk) => $"{baseName}.z{disk:00}";

        private static string DescribeStillMissing(int diskCount, IReadOnlyList<string> missing) =>
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SpannedZipDisksStillMissingFormat,
                diskCount,
                string.Join("、", missing));

        /// <summary>
        /// 找卷的**基准** = 手上**最浅的那一卷**（用户 2026-10-05 口径：「你要以一开始的分卷文件为准，
        /// 不要以 001 为准，否则这时的第一层父文件夹就探测不到」）。
        ///
        /// <para>判据只读**路径事实**：目录层数最少的那一份；层数相同优先取**入口自己**
        /// （入口是调用方手上那一份，语义最稳），再按卷号、名字定序 —— 于是同一批候选每次得到**同一个**基准
        /// （⛔ 不随机、⛔ 不依赖枚举顺序）。判不出（池子空）⇒ 退回入口。</para>
        /// </summary>
        private static string ResolveShallowestAnchor(string entryPath, IReadOnlyList<GatherPiece> pool)
        {
            GatherPiece? shallowest = pool
                .OrderBy(p => Depth(p.Path))
                .ThenBy(p => SamePath(p.Path, entryPath) ? 0 : 1)
                .ThenBy(p => p.Index)
                .ThenBy(p => SafeFileName(p.Path), StringComparer.OrdinalIgnoreCase)
                .Cast<GatherPiece?>()
                .FirstOrDefault();

            return shallowest?.Path ?? entryPath;

            static int Depth(string path)
            {
                int count = 0;
                string current = SafePathHelper.GetFullPathSafe(path);

                while (!string.IsNullOrEmpty(current))
                {
                    count++;

                    string? parent = Path.GetDirectoryName(current);

                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    current = parent;
                }

                return count;
            }
        }

        /// <summary>一次"这一组齐不齐"的读数（纯事实）。</summary>
        private readonly record struct GroupEvidence(
            bool Complete,
            bool NeedsNoWork,
            bool BytesBudgetKnown,
            bool Ambiguous,
            IReadOnlyList<string> MissingNames,
            string Detail)
        {
            public string Describe() => Detail;
        }

        /// <summary>
        /// 期望基名：把池里那几片**只按文件名**喂给 <see cref="VolumeGroupDetector.Group"/>
        /// （目录无所谓 —— 这一问只想知道"这一组叫什么"），取含入口那一份的那个组的基名。
        ///
        /// <para>⛔ 与下面那道闸门**同一条代码路径**：那样才不会被"组键档不剥卷标记"这类档位差异坑到
        /// （实测 `TryResolveVolumeBaseName("HK.7z.001", VolumeGroupKey)` 原样返回 `HK.7z.001`，
        /// 而组里算出来的是 `HK.7z`）。判不出来 ⇒ 返回 null（调用方整档不做）。</para>
        /// </summary>
        private static string? ResolveGatherBaseName(IReadOnlyList<GatherPiece> pool, string entryName)
        {
            try
            {
                var named = pool
                    .Select(piece => new VolumeCandidate { Path = SafeFileName(piece.Path), Size = piece.Size })
                    .ToList();

                foreach (VolumeGroup group in VolumeGroupDetector.Group(named))
                {
                    if (group.Volumes.Any(v => string.Equals(
                            SafeFileName(v.Path),
                            entryName,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        return group.BaseName;
                    }
                }
            }
            catch
            {
                // 判不出来 ⇒ 什么都不做（调用方退回"照旧让引擎去判"）。
            }

            return null;
        }

        /// <summary>
        /// 判"这几片是不是完整的一组"：⛔ 判据只用既有出口，⛔ 不另写尺子。
        ///
        /// <list type="number">
        /// <item><description><b>结构自洽</b>：把这几片按"**移动之后的样子**"虚拟出来
        /// （路径 = 入口那一层 + 规范卷名，尺寸 = 各片实际字节数）喂给
        /// <see cref="TryConfirmSelfConsistentVolumeGroup(IEnumerable{VolumeCandidate}, string, string)"/>。
        /// 为什么必须虚拟：真实的几片躺在不同目录里，而 <see cref="VolumeGroupDetector.Group"/>
        /// 按"目录 + 基名 + 族"分桶 —— 直接喂真路径的话每一片都会自成一个"从 1 起、无缺号"的完整桶，
        /// 这条闸门就成了恒真。</description></item>
        /// <item><description><b>字节数硬证据</b>（7z）：起始头自述的整包长度 vs 这几片之和。
        /// 少了 ⇒ 缺卷（报"还差 N 字节"）；多了 ⇒ 只接受既有那把"唯一说得通的缩池"
        /// （<see cref="TryShrinkToExactByteBudget"/>），缩不出来 ⇒ <c>Ambiguous</c>（判不出，不免试）。</description></item>
        /// <item><description><b>名字上的洞</b>：同一命名空间里配一次组，<c>MissingVolumeNames</c> 就是
        /// "能点名的缺卷"。⚠ 名字里没有"共几卷"这个信息，末卷之后的缺号**一个字都不编**。</description></item>
        /// </list>
        /// </summary>
        private static GroupEvidence EvaluateGatherGroup(
            IReadOnlyList<GatherPiece> pieces,
            string entryPath,
            string entryDirectory,
            string expectedBaseName,
            IReadOnlyDictionary<int, string> targetNames,
            VolumeContentFormat format)
        {
            var missing = new List<string>();

            if (pieces.Count == 0)
            {
                return new GroupEvidence(false, false, false, true, missing, StatusText.CrossLayerGatherNoCandidates);
            }

            List<VolumeCandidate> Project() => pieces
                .Where(piece => targetNames.ContainsKey(piece.Index))
                .Select(piece => new VolumeCandidate
                {
                    Path = Path.Combine(entryDirectory, targetNames[piece.Index]),
                    Size = piece.Size
                })
                .ToList();

            List<VolumeCandidate> virtualCandidates = Project();

            bool selfConsistent = TryConfirmSelfConsistentVolumeGroup(
                virtualCandidates, entryPath, expectedBaseName);

            // 名字上的洞（同一命名空间里配组）：能从名字上看出来的缺卷逐个点名。
            try
            {
                foreach (VolumeGroup group in VolumeGroupDetector.Group(Project()))
                {
                    if (group.Volumes.Any(v => string.Equals(
                            SafeFileName(v.Path),
                            SafeFileName(entryPath),
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        missing.AddRange(group.MissingVolumeNames);
                    }
                }
            }
            catch
            {
                // 配不出来就是"点不了名"，不影响任何结论。
            }

            bool needsNoWork = pieces.All(piece =>
                targetNames.ContainsKey(piece.Index) &&
                SamePath(piece.Path, Path.Combine(entryDirectory, targetNames[piece.Index])));

            if (format != VolumeContentFormat.SevenZip)
            {
                /*
                 * 其他族没有"整包多长"这个自述数 ⇒ 只有名字这一条证据（与今天的行为一致：
                 * 完整一组就解、不完整就让引擎去报它自己那句"分卷缺失"）。
                 */
                return new GroupEvidence(
                    selfConsistent,
                    needsNoWork,
                    BytesBudgetKnown: false,
                    Ambiguous: false,
                    missing,
                    selfConsistent
                        ? StatusText.CrossLayerGatherSelfConsistent
                        : StatusText.CrossLayerGatherNotSelfConsistent);
            }

            SevenZipByteBudget budget = SevenZipStartHeader.Measure(
                entryPath,
                pieces.Select(piece => piece.Path).ToList());

            if (!budget.Known)
            {
                // 读不出起始头（入口这一份没有 7z 魔数）⇒ 这条证据缺席 ⇒ 照旧走名字那条。
                return new GroupEvidence(
                    selfConsistent,
                    needsNoWork,
                    BytesBudgetKnown: false,
                    Ambiguous: false,
                    missing,
                    StatusText.CrossLayerGatherNoStartHeader);
            }

            if (budget.Verdict == SevenZipByteBudgetVerdict.Excess)
            {
                List<VolumeCandidate> others = pieces
                    .Where(piece => !SamePath(piece.Path, entryPath))
                    .Select(piece => new VolumeCandidate { Path = piece.Path, Size = piece.Size })
                    .ToList();

                IReadOnlyList<VolumeCandidate>? shrunk = TryShrinkToExactByteBudget(
                    entryPath, others, budget.ExpectedBytes);

                if (shrunk == null)
                {
                    // 判不出多出来的是哪一份 ⇒ 不下任何结论（照旧让引擎去试）。
                    return new GroupEvidence(
                        selfConsistent,
                        needsNoWork,
                        BytesBudgetKnown: true,
                        Ambiguous: true,
                        missing,
                        budget.Describe());
                }

                budget = SevenZipStartHeader.Measure(
                    entryPath,
                    new[] { entryPath }.Concat(shrunk.Select(c => c.Path)));
            }

            return new GroupEvidence(
                selfConsistent && budget.Verdict == SevenZipByteBudgetVerdict.Exact,
                needsNoWork,
                BytesBudgetKnown: true,
                Ambiguous: false,
                missing,
                budget.Describe());
        }

        /// <summary>
        /// 造"把这几卷收进入口那一层"的计划。执行体仍是 <see cref="TryApply"/>；
        /// <paramref name="blocker"/> = 为什么收不成（跨盘 / 目标名被占 / 没什么可做）。
        /// </summary>
        private static VolumeNameRepairPlan? TryBuildGatherPlan(
            string entryPath,
            IReadOnlyList<GatherPiece> pieces,
            IReadOnlyDictionary<int, string> targetNames,
            out string blocker)
        {
            blocker = string.Empty;

            string entryDirectory = Path.GetDirectoryName(entryPath) ?? string.Empty;
            var slots = new List<VolumeGroupSlot>();
            var names = new List<string>();
            string? primary = null;

            foreach (GatherPiece piece in pieces.OrderBy(p => p.Index))
            {
                if (!targetNames.TryGetValue(piece.Index, out string? canonical))
                {
                    blocker = StatusText.CrossLayerGatherNoCandidates;
                    return null;
                }

                slots.Add(new VolumeGroupSlot { Path = piece.Path, Number = piece.Index });
                names.Add(canonical);

                if (primary == null && !SamePath(piece.Path, Path.Combine(entryDirectory, canonical)))
                {
                    // 计划里的"当前项"必须指向**真要动的那一卷**（不然 TryApply 一进来就判"名字本来就对"）。
                    primary = piece.Path;
                }
            }

            if (primary == null)
            {
                blocker = StatusText.VolumeRepairAlreadyStandard;
                return null;
            }

            var order = new VolumeGroupOrder { Confirmed = true, Slots = slots };
            VolumeNameRepairPlan plan = BuildPlanFromOrder(primary, order, names, entryPath);

            if (!plan.CanRepair)
            {
                blocker = plan.Reason;
                return null;
            }

            return plan;
        }

        /// <summary>
        /// 收卷那一行日志要说的内容（"收了几卷、**从哪**收到哪"）：逐卷写
        /// <c>旧名（从 &lt;那一层&gt; 收来）→ 新名</c>；原地只改名的写 <c>（原地改名）</c>。
        ///
        /// <para>⛔ 只写**目录名**（<c>layer-001</c> 这种），不写完整路径 —— §8 隐私红线与既有日志口径
        /// （"内层包已移入其余物 …（&lt;那一层&gt; 那一层）"）同一条。</para>
        /// </summary>
        private static string DescribeGatherPlan(VolumeNameRepairPlan plan, string entryDirectory)
        {
            var parts = new List<string>();

            foreach (VolumeRepairItem item in plan.Items ?? Array.Empty<VolumeRepairItem>())
            {
                if (string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string folder = DescribeSourceLayer(item.CurrentPath);
                bool renamed = !string.Equals(
                    item.CurrentFileName,
                    item.SuggestedFileName,
                    StringComparison.OrdinalIgnoreCase);

                bool sameDirectory = string.Equals(
                    Path.GetDirectoryName(item.CurrentPath),
                    entryDirectory,
                    StringComparison.OrdinalIgnoreCase);

                string where = sameDirectory ? "原地改名" : $"从 {folder} 收来";

                parts.Add(renamed
                    ? $"{item.CurrentFileName} → {item.SuggestedFileName}（{where}）"
                    : $"{item.CurrentFileName}（{where}）");
            }

            return parts.Count == 0 ? StatusText.VolumeRepairAlreadyStandard : string.Join("；", parts);
        }

        /// <summary>
        /// 这一卷**原来在哪一层**（写进收卷那一行日志的"从 &lt;哪&gt; 收来"）。
        ///
        /// <para>⛔ 为什么不直接写"最后一段目录名"（用户 2026-10-05 真机）：层产物目录固定叫
        /// <c>output</c>（<c>…\recursive\&lt;任务id&gt;\layer-001\output</c>），于是两卷分别来自
        /// <c>layer-001</c> 与 <c>layer-002</c> 时，日志两边都印成"从 output 收来" ——
        /// 用户根本看不出是哪一层（他点名要求："要印出是哪一层"）。</para>
        ///
        /// <para>判据只读路径事实：最后一段是层目录（<see cref="ExtractionWorkspace.LayerDirectoryPrefix"/>）
        /// 就用它；否则往上看一层，那一层是层目录就用它；都不是 ⇒ 退回最后一段目录名（⛔ 不编）。</para>
        /// </summary>
        private static string DescribeSourceLayer(string filePath)
        {
            string directory = SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(filePath) ?? string.Empty);
            string leaf = SafeFileName(directory);

            if (leaf.StartsWith(ExtractionWorkspace.LayerDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return leaf;
            }

            string parent = SafeFileName(Path.GetDirectoryName(directory) ?? string.Empty);

            return parent.StartsWith(ExtractionWorkspace.LayerDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
                ? parent
                : leaf;
        }

        private static bool SameDirectory(string a, string b) =>
            string.Equals(
                Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(a)),
                Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(b)),
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 照着计划**只改名字**。⛔ 绝无覆盖、绝无删除、绝不改内容：
        /// 目标名已存在就原地拒绝（<see cref="File.Move(string, string)"/> 在没有 overwrite 参数时
        /// 碰到已存在的目标会抛 —— 这里不用"先判断再移动"的写法当唯一防线，两道都在）。
        /// </summary>
        public static VolumeNameRepairResult TryApply(VolumeNameRepairPlan? plan)
        {
            if (plan == null || !plan.CanRepair)
            {
                return Failure(plan?.Reason ?? StatusText.VolumeRepairSourceMissing);
            }

            long sizeBefore;

            /*
             * 已经改成功的卷（旧名 → 新名）。整组改名必须"全成或全不成"：
             * 中途任何一卷失败，都要按这张表**倒序把名字改回去**，让盘上的状态与
             * "一组没改"这句话一致。
             * 真机现场（2026-09-30）：日志写「改名没成功」，盘上却已经出现 `111.part1.002` ——
             * 半改状态比不改更糟：7-Zip 按新基名去找后续卷，名字七零八落时整组都打不开。
             */
            var applied = new List<(string From, string To)>();

            try
            {
                if (!File.Exists(plan.CurrentPath))
                {
                    return Failure(StatusText.VolumeRepairSourceMissing);
                }

                if (string.Equals(plan.CurrentPath, plan.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(StatusText.VolumeRepairAlreadyStandard);
                }

                if (File.Exists(plan.TargetPath))
                {
                    return Failure(string.Format(StatusText.VolumeRepairTargetTakenFormat, plan.SuggestedFileName));
                }

                sizeBefore = new FileInfo(plan.CurrentPath).Length;

                File.Move(plan.CurrentPath, plan.TargetPath);

                applied.Add((plan.CurrentPath, plan.TargetPath));
            }
            catch (Exception ex)
            {
                return Failure(string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, ex.Message));
            }

            /*
             * 改完立刻核一遍：新名字在、旧名字没了、**字节数一个不差**。
             * 这三条是"只改了名字"的机器证据（改名本来不该动内容；对不上就说明有别的程序在动它，
             * 那时候必须如实报出来，而不是让后面的解压拿着一个说不清的文件去跑）。
             */
            try
            {
                if (!File.Exists(plan.TargetPath))
                {
                    return Undo(
                        applied,
                        string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, "改完之后新名字没找到"));
                }

                if (File.Exists(plan.CurrentPath))
                {
                    return Undo(
                        applied,
                        string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, "旧名字还在"));
                }

                long sizeAfter = new FileInfo(plan.TargetPath).Length;

                if (sizeAfter != sizeBefore)
                {
                    return Undo(
                        applied,
                        string.Format(
                            StatusText.VolumeRepairRenameFailedFormat,
                            plan.SuggestedFileName,
                            $"字节数变了（{sizeBefore} → {sizeAfter}）"));
                }
            }
            catch (Exception ex)
            {
                return Failure(string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, ex.Message));
            }

            /*
             * 还有别的卷要改（网盘给整组缀了「删除」那种）：接着一卷一卷来。
             * ⛔ 每一卷都走与上面同一套判据（源在、目标未被占、字节数不差）；
             * 中途任何一卷失败 ⇒ 把**已经改过的全部改回去**（<see cref="Undo"/>），再如实报"一组没改、卡在哪一卷"。
             * ⛔ 绝不留半改状态 —— 旧口径是"已改的那几卷不会再动"，真机上正是它把用户的名字改成七零八落。
             */
            var rest = (plan.Items ?? Array.Empty<VolumeRepairItem>())
                .Where(i => !string.Equals(i.CurrentPath, plan.CurrentPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int done = 0;

            foreach (VolumeRepairItem item in rest)
            {
                try
                {
                    if (!File.Exists(item.CurrentPath))
                    {
                        return Undo(applied, $"{item.CurrentFileName}：{StatusText.VolumeRepairSourceMissing}");
                    }

                    if (string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.Exists(item.TargetPath))
                    {
                        return Undo(
                            applied,
                            string.Format(StatusText.VolumeRepairTargetTakenFormat, item.SuggestedFileName));
                    }

                    long before = new FileInfo(item.CurrentPath).Length;
                    File.Move(item.CurrentPath, item.TargetPath);

                    applied.Add((item.CurrentPath, item.TargetPath));

                    long after = new FileInfo(item.TargetPath).Length;

                    if (after != before)
                    {
                        return Undo(applied, $"字节数变了（{before} → {after}）");
                    }

                    done++;
                }
                catch (Exception ex)
                {
                    return Undo(
                        applied,
                        string.Format(StatusText.VolumeRepairRenameFailedFormat, item.SuggestedFileName, ex.Message));
                }
            }

            return new VolumeNameRepairResult
            {
                Success = true,
                NewPath = plan.TargetPath,
                Message = done == 0
                    ? string.Format(StatusText.VolumeRepairDoneFormat, plan.CurrentFileName, plan.SuggestedFileName)
                    : string.Format(StatusText.VolumeRepairGroupDoneFormat, plan.Describe(), done + 1)
            };
        }

        /// <summary>
        /// 整组没改成 ⇒ 把**已经改过的名字倒序改回原名**，让盘上的状态与"一组没改"这句话一致。
        /// <para>回滚本身失败（原名被别的程序占了 / 文件被锁 / 新名字不见了）时**如实点名**，
        /// ⛔ 绝不让用户以为"没动过"。<c>why</c> = 卡在哪一卷、为什么。</para>
        /// </summary>
        private static VolumeNameRepairResult Undo(IReadOnlyList<(string From, string To)> applied, string why)
        {
            var stuck = new List<string>();

            for (int index = applied.Count - 1; index >= 0; index--)
            {
                (string from, string to) = applied[index];

                try
                {
                    if (!File.Exists(to))
                    {
                        stuck.Add(SafeFileName(to));
                        continue;
                    }

                    if (File.Exists(from))
                    {
                        // 原名又被占上了（别的程序插进来的）⇒ 不敢覆盖，只能如实点名。
                        stuck.Add(SafeFileName(to));
                        continue;
                    }

                    File.Move(to, from);
                }
                catch
                {
                    stuck.Add(SafeFileName(to));
                }
            }

            if (stuck.Count == 0)
            {
                return Failure(string.Format(StatusText.VolumeRepairRolledBackFormat, why));
            }

            return Failure(string.Format(
                StatusText.VolumeRepairRollbackIncompleteFormat,
                stuck.Count,
                string.Join("、", stuck)));
        }

        /// <param name="trialAttempted">这一份"不能改"的结论是不是**试开跑过之后**下的
        /// （只有真跑过试开才值得写一行日志说清结论，见 <see cref="VolumeNameRepairPlan.TrialAttempted"/>）。</param>
        /// <param name="byteBudgetMismatch">结论是不是 7z 起始头那条**字节数**证据给的（见
        /// <see cref="VolumeNameRepairPlan.ByteBudgetMismatch"/>：一次引擎都没调，但必须如实报出差的字节数）。</param>
        private static VolumeNameRepairPlan Cannot(
            string path,
            string reason,
            bool trialAttempted = false,
            bool byteBudgetMismatch = false) => new()
            {
                CanRepair = false,
                Reason = reason,
                CurrentPath = path,
                CurrentFileName = SafeFileName(path),
                TrialAttempted = trialAttempted,
                ByteBudgetMismatch = byteBudgetMismatch
            };

        private static VolumeNameRepairResult Failure(string message) => new()
        {
            Success = false,
            Message = message
        };

        private static string SafeFileName(string path)
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
