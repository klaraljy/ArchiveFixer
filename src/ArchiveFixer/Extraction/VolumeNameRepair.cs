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
        /// **候选池（含邻近目录）**：归档自己所在的那一层 + **它自己的直接子目录** + **"父目录这一家"**
        /// （父目录本身 + 父目录的各直接子目录 = 自己的兄弟目录）。
        ///
        /// <para>为什么要有它（用户 2026-10-01 点名两次）：「跨目录找同组的卷」；他明确了两条口径 ——
        /// ① 「**归档自己所在目录的子目录**，这个也不能少」；② 「绝大多数只会在一个**父文件夹和父文件夹的
        /// 同级子文件夹**当中」。⚠ 后一句同时也是**代价说明**：这种形状本来就是小概率，
        /// 所以这里的做法是"宁可多看一眼、但边界划死、粗筛挡住噪声"，⛔ 不是满盘搜。</para>
        ///
        /// <para><b>边界是刻意划的</b>（§8 隐私红线：不替用户在他盘上到处找文件）：</para>
        /// <list type="number">
        /// <item><description><b>看</b>：自己这一层 + 自己的直接子目录（这两块**不做尺寸粗筛**，
        /// 与以前"同目录"的行为逐字一致）；再加父目录这一家（这一块过一次 ≥ 16 KiB 的粗筛 + 硬上限）。</description></item>
        /// <item><description><b>不看</b>：祖父及以上、孙目录（自己子目录的子目录）、以及"父目录这一家"之外的目录。
        /// 归档目录**本身就是卷根**（例如就放在 <c>H:\</c> 下）时没有父目录这一家，退化成"自己这一层 + 自己的子目录"。</description></item>
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

            // ① 自己的直接子目录（用户点名不能少）：与"自己这一层"同一待遇，不做尺寸粗筛。
            try
            {
                foreach (string sub in Directory.GetDirectories(directory))
                {
                    candidates.AddRange(Detection.VolumeContentInference.EnumerateCandidatesIn(sub));
                }
            }
            catch
            {
                // 某个子目录读不动（权限 / 半路被删）不影响已经收到的那几份候选。
            }

            // ② 父目录这一家（父目录 + 它的各直接子目录）。
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

        /// <summary>父目录这一家里最多收几份候选（防止"父目录是个大杂烩"时白读一大堆文件头）。</summary>
        private const int MaxNearbyCandidates = 500;

        /// <summary>父目录这一家最多看几个子目录。</summary>
        private const int MaxNearbyDirectories = 200;

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

            switch (self.Format)
            {
                case Detection.VolumeContentFormat.SevenZip:
                    {
                        // 名字里已经有卷号 → 那是 Plan 的活，这里不抢（判据出口只有一个）。
                        if (VolumeGroupDetector.TryGetVolumeIndex(fileName) != null)
                        {
                            return Cannot(path, StatusText.VolumeRepairAlreadyStandard);
                        }

                        /*
                         * ⛔ 7z **不吃放宽的候选池**（理由写在 PlanByContentWithNearbyCandidatesAsync 的注释里）：
                         * 它的中间片内容里没有任何身份信息，邻近目录里别的包的卷和它无法区分 ——
                         * 放宽只会把别的包拖进"尺寸排序 + 试开"的排列里。这一档**只用同目录候选**。
                         */
                        IEnumerable<VolumeCandidate>? pool = allowNearbyDirectories
                            ? OnlySameDirectory(filesInDirectory, path)
                            : filesInDirectory;

                        return await PlanSevenZipByContentAsync(
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
                    return Cannot(path, StatusText.VolumeRepairContentNotAVolumeMember);
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

            Detection.VolumeGroupOrder order = Detection.VolumeNumberFromContent.ResolveGroup(path, readings);

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

            // 入口那一卷：跨盘 zip 是末片（`X.zip`），RAR 是第 1 卷（`X.part1.rar`）—— 与上面推基名用的是同一卷。
            return BuildPlanFromOrder(path, order, targetNames, entryVolumePath: stemSource);
        }

        /// <summary>
        /// 7z：内容认第一卷 + 同目录尺寸规律（或**两卷形状**，用户 2026-09-29 放宽）+ 硬链接试开验证。
        /// </summary>
        private static async Task<VolumeNameRepairPlan> PlanSevenZipByContentAsync(
            string path,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            Engines.IArchiveEngine engine,
            string? workRootDirectory,
            CancellationToken cancellationToken)
        {
            Detection.VolumeContentFormat format = Detection.VolumeContentFormat.SevenZip;

            IReadOnlyList<VolumeCandidate> candidates =
                Detection.VolumeContentInference.BuildCandidates(path, filesInDirectory);

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
            if (!Detection.VolumeContentInference.HasVolumeSizePattern(path, candidates)
                && !Detection.VolumeContentInference.HasTwoVolumeShapeEvidence(path, candidates))
            {
                return Cannot(path, StatusText.VolumeRepairContentNoSizePattern);
            }

            IReadOnlyList<IReadOnlyList<VolumeCandidate>> orderings =
                Detection.VolumeContentInference.BuildOrderings(path, candidates);

            var verifier = new VolumeProbeVerifier(engine);
            VolumeProbeOutcome probe = await verifier
                .VerifyAsync(path, orderings, cancellationToken, workRootDirectory)
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
                        path,
                        string.Format(StatusText.VolumeRepairContentProbeFailedFormat, probe.Reason),
                        trialAttempted: true)
                    : Cannot(
                        path,
                        string.Format(StatusText.VolumeRepairNoProbeFormat, probe.Reason),
                        trialAttempted: false);
            }

            if (!Detection.VolumeNumberFromContent.TryDeriveStem(path, format, out string stem))
            {
                return Cannot(path, StatusText.VolumeRepairNoSuggestion, trialAttempted: true);
            }

            IReadOnlyList<string> targetNames = Detection.VolumeNumberFromContent.BuildStandardNames(
                stem, VolumeNamingFamily.SevenZipNumbered, probe.OrderedVolumes.Count);

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            var items = new List<VolumeRepairItem>();

            for (int i = 0; i < probe.OrderedVolumes.Count && i < targetNames.Count; i++)
            {
                string source = probe.OrderedVolumes[i].Path;
                string target = Path.Combine(directory, targetNames[i]);

                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    return Cannot(path, StatusText.VolumeRepairAlreadyStandard, trialAttempted: true);
                }

                // ⛔ 绝不覆盖：任何一个目标名被占，整组不改。
                if (File.Exists(target))
                {
                    return Cannot(
                        path,
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
            }

            VolumeRepairItem self = items[0];

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

            string selfBase = OutputPlacement.ResolveArchiveBaseName(fileName);

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
        /// 判据转调 <see cref="ExtensionHelper.TrySplitVolumeSegmentLoose"/>（只此一处）。
        /// </summary>
        private static bool TrySplitDisguised(string fileName, out string baseName, out string canonicalSegment)
        {
            baseName = string.Empty;
            canonicalSegment = string.Empty;

            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            /*
             * ⓪ <基名>.partN.rar（**卷标记与 rar 尾巴都可以粘垃圾**）：目标名必须**连 `.rar` 一起**保留。
             *
             * ⛔ 老写法把它当"标记后面挂着别的点段"处理，只把卷标记接回去 ⇒ 目标名算成 `X.part1`，
             * 等于把 RAR 的族后缀吃掉：改完 7-Zip / UnRAR 更打不开这一组（改名是**不可逆**动作，
             * 比"一个都不改"更糟）。判据与归组 / 包基名转调**同一份**
             * <see cref="ExtensionHelper.TrySplitPartNumberedVolume"/>。
             */
            if (ExtensionHelper.TrySplitPartNumberedVolume(
                    fileName,
                    out string partBase,
                    out string partMark,
                    out _,
                    out string partTail,
                    out _)
                && !partTail.Equals("rar", StringComparison.OrdinalIgnoreCase))
            {
                /*
                 * ⚠ 只接"**尾巴确实粘着垃圾**"那一档（`X.part1.rar删除`，网盘缀的「删除」）。
                 *
                 * 尾巴干净的（`X.part1.rar` / `444.pa删rt2.rar`）**必须留给下面既有那两条路**：
                 * 真机夹具（AAA）里 `444.p1art2.ra3r` 那一组，卷标记前面还有一段属于基名的
                 * `p1art2` —— 这里若抢着按"倒数第二段就是卷标记"去拆，基名会被算成 `444`、
                 * 改名产出 `444.part1.rar`（错），而正解是 `444.p1art2.part1.rar`。
                 * 实测：抢这一档 ⇒ `AaaReplayPipelineTests.夹具验收_一组分卷只解一次…` 当场变红
                 * （全量回归逮到，已收窄）。
                 */
                baseName = partBase;
                canonicalSegment = partMark + ".rar";
                return true;
            }

            // ① 最后一段自己就是"带垃圾的卷标记"（001删除 / 删除001 / z0删除3）
            if (!ExtensionHelper.IsVolumePartExtension("." + parts[^1]) &&
                ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[^1], out string mark, out _))
            {
                baseName = VolumeGroupDetector.NormalizeArchiveExtensionSegment(string.Join('.', parts, 0, parts.Length - 1));
                canonicalSegment = mark;
                return baseName.Length > 0;
            }

            // ② 卷标记后面还挂着别的点段（x.7z.001.txt）
            //    ⚠ 别去查卷标记自己是不是"已知压缩后缀" —— `.001` 本身就在那份名单里，
            //      拿它当闸门会把 `.001.txt` 全挡掉（实测踩到过）。
            for (int i = parts.Length - 2; i >= 1; i--)
            {
                if (!ExtensionHelper.TrySplitVolumeSegmentTolerant(parts[i], out string innerMark, out _))
                {
                    continue;
                }

                bool onlyPlainTails = true;

                for (int j = i + 1; j < parts.Length; j++)
                {
                    if (ExtensionHelper.IsKnownArchiveExtension("." + parts[j]))
                    {
                        onlyPlainTails = false;
                        break;
                    }
                }

                if (!onlyPlainTails)
                {
                    continue;
                }

                baseName = VolumeGroupDetector.NormalizeArchiveExtensionSegment(string.Join('.', parts, 0, i));
                canonicalSegment = innerMark;
                return baseName.Length > 0;
            }

            return false;
        }

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
        private static VolumeNameRepairPlan Cannot(string path, string reason, bool trialAttempted = false) => new()
        {
            CanRepair = false,
            Reason = reason,
            CurrentPath = path,
            CurrentFileName = SafeFileName(path),
            TrialAttempted = trialAttempted
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
