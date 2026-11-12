using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static ArchiveFixer.Extraction.VolumeNameRepair;
using static ArchiveFixer.Extraction.VolumeNameRestorer;

namespace ArchiveFixer.Extraction
{
    internal static partial class VolumeNamePlanner
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

                return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
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
        /// <summary>
        /// **本批"所有剩余的"里像卷的那些**（用户 2026-10-12 口径，原话：
        /// 「这个第一大步完成之后就是扫描所有剩余的，我之前说过的什么"父目录+自己这一层+所有子目录"
        /// 在这个新框架下可能是不成立的」）。
        ///
        /// <para>做法 = 既有窗口那一份（<see cref="EnumerateVolumeCandidatesNearby(string?)"/>：
        /// 自己这一层 + 自己的子目录 + 父目录这一家）**并上**调用方给出的那些路径各自所在目录里像卷的
        /// —— 去重之后一起交给下面那三道闸门（同族同基名 / 卷号一份对一份 / 整组自洽）。
        /// ⛔ 范围只到"本批导入/剩下的那些路径所在的目录"：⛔ 不是整盘扫、⛔ 不递归无关目录。</para>
        ///
        /// <para>⚠ 这道放宽只改"**去哪儿找**"，⛔ 一个字都不改"凭什么叫同一组"那三道闸门，
        /// 也不改"必须证明整组完整才允许搬/删"（<c>RestVolumeCompletenessGate</c>）。</para>
        /// </summary>
        public static IReadOnlyList<VolumeCandidate> EnumerateVolumeCandidatesNearby(
            string? filePath,
            IEnumerable<string>? extraPaths)
        {
            var candidates = new List<VolumeCandidate>(EnumerateVolumeCandidatesNearby(filePath));
            var seen = new HashSet<string>(
                candidates
                    .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Path))
                    .Select(candidate => candidate.Path!),
                StringComparer.OrdinalIgnoreCase);

            if (extraPaths == null)
            {
                return candidates;
            }

            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? path in extraPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string? directory = null;

                try
                {
                    directory = Path.GetDirectoryName(path);
                }
                catch
                {
                    // 路径形态怪 ⇒ 跳过这一份（⛔ 不猜、不报错）。
                }

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    directories.Add(directory);
                }
            }

            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    foreach (VolumeCandidate candidate in Detection.VolumeContentInference.EnumerateCandidatesIn(directory))
                    {
                        if (candidates.Count >= MaxNearbyCandidates)
                        {
                            return candidates;
                        }

                        if (candidate != null
                            && !string.IsNullOrWhiteSpace(candidate.Path)
                            && seen.Add(candidate.Path!))
                        {
                            candidates.Add(candidate);
                        }
                    }
                }
                catch
                {
                    // 某个目录读不动（权限 / 半路被删）不影响已经收到的那几份候选。
                }
            }

            return candidates;
        }

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
        internal const long NearbyMinimumBytes = 16 * 1024;

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
        internal static bool TryConfirmSelfConsistentVolumeGroup(
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

            /*
             * ===== 模板（安全版）：卡**只剔除"可证不是分卷成员"**的那些 =====
             * 用户 2026-10-17 小目标第②项。这里"组键标签"是调用方给的 `expectedBaseName`、
             * 完整性由名字尺子回答 —— 卡不能替代这两件事（上一轮已用两条红证明），
             * 但卡能**排除噪声**：内容可证"我不是这一族任何分卷"的那几份不进桶。
             * 剔除后不足 2 份 ⇒ 一个都不剔（老口径）。
             */
            var cardFiltered = new List<VolumeCandidate>();

            foreach (VolumeCandidate candidate in list)
            {
                bool provablyNotMember;

                try
                {
                    provablyNotMember = ArchiveFixer.Detection.FileNature.Inspect(candidate.Path).Membership
                        == ArchiveFixer.Detection.VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;
                }
                catch
                {
                    provablyNotMember = false;
                }

                if (!provablyNotMember)
                {
                    cardFiltered.Add(candidate);
                }
            }

            if (cardFiltered.Count >= 2 && cardFiltered.Count < list.Count)
            {
                list = cardFiltered;
            }

            if (list.Count == 0)
            {
                return false;
            }

            foreach (VolumeGroup group in VolumeGroupDetector.Group(list))
            {
                /*
                 * ⚠ `group.BaseName` 是**名字前缀**（`set.7z` —— 写出来的规范卷名要往它后面接卷标记），
                 * 而调用方给的可能是**基名**（`set` —— 第二大步那份，第一个点之前）。
                 * 这一问问的是"**是不是同一组**"，所以两边都按基名比；⛔ 别拿前缀当身份。
                 */
                if (!group.IsComplete ||
                    !string.Equals(
                        FileNameHelper.SecondStepBaseName(group.BaseName),
                        FileNameHelper.SecondStepBaseName(expectedBaseName),
                        StringComparison.OrdinalIgnoreCase))
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
        internal static bool TryConfirmSelfConsistentVolumeGroup(
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

            bool looksClean = ExtensionHelper.IsVolumePartExtension("." + last)
                              || last.Equals("zip", StringComparison.OrdinalIgnoreCase)
                              || last.Equals("rar", StringComparison.OrdinalIgnoreCase);

            if (!looksClean)
            {
                return false;
            }

            /*
             * ⛔ **还必须是"它自己那一族的规范写法"**（2026-10-10 真机 FFFF 实测，一次不可逆的改名）：
             * 上面那条只看**末段**，于是 `111_.part1` 也过关（末段 `part1` 算卷标记）——
             * 「还原」就把一个**本来就规范**的 `111_.part1.rar` 改成了 `111_.part1`（`.rar` 丢了）：
             * 族名一坏，引擎随后就找不到 `111_.part2.rar` 那几卷，整组解不开。
             * 守门 `ChainInnerVolumeRestoreTests.真机布局_四个外层包各在一个子目录里_四片分处四地_整组照样凑齐并解开`
             * 的日志里逐字是：「分卷名不标准，已按标准名改好（1 卷…）：111_.part1.rar → 111_.part1」，
             * 随后引擎报 `Missing volume`。
             *
             * 判据转调唯一出口 `LegacyVolumeNames.TryGetCanonicalName`（族 + 卷号 ⇒ 规范名），
             * ⛔ 这里不新造第二套族规则（末段那条留着当第一道粗筛）。判不出来 ⇒ 不算规范
             * ⇒ 整份计划不成立（兜底照旧落在"什么名字都不改"那一档）。
             */
            return LegacyVolumeNames.TryGetCanonicalName(fileName, out string canonical)
                   && string.Equals(canonical, fileName, StringComparison.OrdinalIgnoreCase);
        }
    }
}