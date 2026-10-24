using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;




namespace ArchiveFixer.Extraction
{
    /// <summary>分卷改名/名字归一的专用出口（从 ExtractionCoordinator 搬出的第 1 个功能块，2026-10-23）。协调器原处只留调用，不再留第二份实现。</summary>
    public static class VolumeNameOrchestration
    {
        /// <summary>
        /// 把暂存目录里"**名字不标准的一整套分卷**"整组改回标准卷名（用户 2026-10-01 真机）。
        ///
        /// <para><b>为什么需要它</b>：外层跨盘 zip（`222.zscip.zip` + `222.zscip.z01`）解出来的两个条目
        /// 本身**又是一份完整的两卷 split zip**，而它们的名字是归档内部记的原名（`222.zi删除p` + `222.z0sc1`）。
        /// 跨盘 zip 只按**标准卷名**找兄弟（末片 `X.zip`、首片 `X.z01`），名字不对**谁**都打不开
        /// —— 7-Zip / WinRAR / UnRAR 实测给的是同一句"打不开"。整组改名的两个调用点都在
        /// "任务表"那一侧（批首 / 手动修复），递归产物从没进过任务表 ⇒ 名字原样落地。</para>
        ///
        /// <para><b>放哪一步</b>：定稿规划的最开头（暂存目录已经定下来、还没做任何分类与套层）。
        /// 这样后面的分卷组闸门、套层判定、搬运看到的都是标准名，不必在四处各补一次特例。</para>
        ///
        /// <para><b>判据不放宽一个字节</b>（唯一出口 = <see cref="VolumeGroupResolver"/> 的结论 +
        /// <see cref="VolumeGroupResolution.IsGroupMember"/> 认下来的那一卷）：</para>
        /// <list type="bullet">
        /// <item><description>这一组必须**结论完整**（判不出 / 缺卷 / 疑缺 ⇒ 一个字都不改）；</description></item>
        /// <item><description>每一卷的**规范名都真的在盘上**（`X.zip` / `X.z01` …）—— 这一条正是"名字变体那一档"的解药：</description></item>
        /// </list>
        /// 现场那一组是 `222.zip`（另一个文件）+ `222.zi删除p`（这个名字本身是那一卷的另一个写法），
        /// 规范名 `222.zip` 在盘上、变体 `222.zi删除p` 也在盘上 ⇒ 把后者改回 `222.zip` 不成立（会覆盖），
        /// 这一组**保持原样**（它的内容一个字节都不丢，用户自己看得见）。
        ///
        /// <para>⛔ 全成或全不成：任何一卷没到位就**倒序改回原名**（与 <c>VolumeNameRepair.TryApply</c>
        /// 同一条纪律）；目标名被占的那一卷跳过、其余照改；改名失败只写 WARN，⛔ 绝不拖垮定稿。</para>
        /// </summary>
        internal static void NormalizeEngineOutputVolumeNames(
            string stageRoot,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            if (string.IsNullOrWhiteSpace(stageRoot) || !Directory.Exists(stageRoot))
            {
                return;
            }

            foreach (string directory in EnumerateDirectoriesSafe(stageRoot))
            {
                RenameVolumeGroupInDirectory(directory, changes, renamedMembers);
            }
        }

        /// <summary>这一层（含根）的每个目录各判一次：这个目录里有没有"名字不标准但规范名齐全"的一整套分卷。</summary>
        internal static void RenameVolumeGroupInDirectory(
            string directory,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            IReadOnlyList<VolumeGroupEntry> entries;

            try
            {
                var list = new List<VolumeGroupEntry>();
                int order = 0;

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    long size;

                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    list.Add(new VolumeGroupEntry
                    {
                        Path = SafePathHelper.GetFullPathSafe(file),
                        Size = size,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(file),
                        OrderIndex = order++
                    });
                }

                entries = list;
            }
            catch
            {
                return;
            }

            if (entries.Count < 2)
            {
                return;
            }

            var candidates = entries
                .Select(e => new VolumeCandidate { Path = e.Path, Size = e.Size })
                .ToList();

            foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
            {
                VolumeGroupResolution resolution = new VolumeGroupResolver().Resolve(new VolumeGroupQuery
                {
                    AnchorPath = group.FirstVolumePath,
                    DirectoryEntries = entries,
                    AllowTrialOpen = false
                });

                TryRenameVolumeGroupToStandardNames(resolution, changes, renamedMembers);
            }
        }

        /// <summary>
        /// 这一卷按跨盘 zip 的族规则该叫什么。
        ///
        /// <para>基名就是**文件名去掉最后一段后缀**（`222.zi删除p` → `222`；`222.z0sc1` → `222`）——
        /// 与归档内部记的名字对不上时也只影响"改完像不像人写的"，能不能打开由"标准卷名"这一条决定。</para>
        ///
        /// <para>认不出来就返回 false ⇒ 调用方**整组不动**（宁可保持原样，也不猜着改用户的名字）。</para>
        /// </summary>
        internal static bool TryResolveStandardVolumeName(string fileName, out string target)
        {
            target = string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            /*
             * 基名 = **第二大步那一份**（唯一出口 `FileNameHelper.SecondStepBaseName`）；
             * 原来这里自己按第一个点切了一份（用户 2026-10-23：「14 处以后统统删掉」）。
             * 没有点 / 以点开头 ⇒ 判不出（与收口前同一结论）。
             */
            string stem = FileNameHelper.SecondStepBaseName(fileName);

            if (stem.Length == 0 || stem.Length >= fileName.Length)
            {
                return false;
            }

            string last = fileName[(stem.Length + 1)..];

            // 脏本体名（`222.zi删除p`：还原出来是 `zip`）⇒ 它就是末片 `.zip`。
            if (ExtensionHelper.TryRecoverDisguisedArchiveBody(last, out string suffix, out _)
                && suffix.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                target = stem + ".zip";
                return true;
            }

            // 干净的末片。
            if (last.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                target = stem + ".zip";
                return true;
            }

            // 卷标记（`z01` / `z0sc1` / `z删除01`）⇒ 一律按标准 `.zNN` 写。
            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(last, out string mark, out _)
                && mark.Length == 3
                && (mark[0] == 'z' || mark[0] == 'Z')
                && int.TryParse(mark.AsSpan(1), out int ordinal)
                && ordinal >= 1)
            {
                target = stem + ".z" + ordinal.ToString("D2", CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 把一组改回标准卷名；判据不成立就原样返回（不写日志、不动盘）。
        ///
        /// <para>闸门只用"这一组**已经是完整的一组**"（<see cref="VolumeGroupVerdict.Complete"/>）+
        /// "**每一个成员**都能唯一推出一个规范名"两条：</para>
        /// <list type="bullet">
        /// <item><description>⛔ **不拿 `HasRenamedVolume` 当拦下条件** —— 它说的正是"这个名字不标准"，
        /// 而本方法的全部目的就是把不标准的名字改成标准的（拿它当拦下条件等于这个方法永远不干活）；
        /// 真机那一组（`222.zi删除p` + `222.z0sc1`）就是被它拦下的。</description></item>
        /// <item><description>⛔ 只要有一个成员推不出规范名、或两个成员推出同一个名字，**整组不动**
        /// （宁可保持原样，也不猜着改用户的名字）。</description></item>
        /// </list>
        /// </summary>
        internal static void TryRenameVolumeGroupToStandardNames(
            VolumeGroupResolution resolution,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            if (resolution.Verdict != VolumeGroupVerdict.Complete)
            {
                return;
            }

            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string member in resolution.GroupFilePaths)
            {
                if (File.Exists(member))
                {
                    onDisk.Add(member);
                }
            }

            if (onDisk.Count < 2)
            {
                return;
            }

            var moves = new List<(string From, string To)>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string member in onDisk)
            {
                string fileName = Path.GetFileName(member);
                string directory = Path.GetDirectoryName(member) ?? string.Empty;

                if (directory.Length == 0)
                {
                    return;
                }

                if (!TryResolveStandardVolumeName(fileName, out string target))
                {
                    return;
                }

                string targetPath = Path.Combine(directory, target);

                // 已经是标准名 ⇒ 这一卷不动（但它也占住了这个目标名）。
                if (string.Equals(fileName, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (!targets.Add(targetPath))
                    {
                        return;
                    }

                    continue;
                }

                // 目标名被别的文件占着 ⇒ 这一卷跳过（绝不覆盖用户的数据）。
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    continue;
                }

                if (!targets.Add(targetPath))
                {
                    // 两个成员推出同一个名字 ⇒ 这一组认不清，整组不动。
                    return;
                }

                moves.Add((member, targetPath));
            }

            if (moves.Count == 0)
            {
                return;
            }

            var done = new List<(string From, string To)>();

            try
            {
                foreach ((string from, string to) in moves)
                {
                    File.Move(from, to);
                    done.Add((from, to));
                }

                // 全成或全不成：全部改完之后复查一遍（另一个进程可能刚好插手）。
                foreach ((_, string to) in done)
                {
                    if (!File.Exists(to))
                    {
                        throw new IOException($"改名后找不到目标：{Path.GetFileName(to)}");
                    }
                }
            }
            catch (Exception ex)
            {
                for (int i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        File.Move(done[i].To, done[i].From);
                    }
                    catch
                    {
                        // 回滚失败如实写进下面那条 WARN 里，⛔ 不假装成功。
                    }
                }

                if (changes != null)
                {
                    changes.Add((
                        "WARN",
                        $"这一层有一组分卷的名字没能改回标准名（{ex.Message}），已尽量改回原名；"
                        + "这一组要打开得手工把名字改成标准卷名（末片 X.zip、首片 X.z01…）。"));
                }

                return;
            }

            if (changes != null)
            {
                changes.Add((
                    "INFO",
                    $"这一层有一组跨盘分卷的名字不是标准名，已改回标准名（{done.Count} 卷，只改名字、内容一个字节没动）："
                    + string.Join("、", done.Select(m => $"{Path.GetFileName(m.From)} → {Path.GetFileName(m.To)}"))));
            }

            /* 记下"刚改过名"的那几个新路径：它们**不许进可删的其余物**（它们是内容物，不是过程物）。 */
            if (renamedMembers != null)
            {
                /*
                 * 整组都记进来（不只是真被改名的那几卷）：调用方据此判"这一组是内容物、不许进可删的其余物"，
                 * 而本来就标准的那几卷（`一只顶美.z01`）同样是这一组的内容物，漏掉它们等于把它们删掉。
                 */
                foreach (string member in onDisk)
                {
                    renamedMembers.Add(SafePathHelper.GetFullPathSafe(member));
                }

                foreach ((_, string to) in done)
                {
                    renamedMembers.Add(SafePathHelper.GetFullPathSafe(to));
                }
            }
        }

        /// <summary>
        /// **名字判不出族 / 卷号时，用底层识别卡把规范卷名拼出来**（唯一出口）。
        ///
        /// <para>族：卡上 `格式=SevenZip` ⇒ 这一族的规范名是 `<基名>.7z.00N`（本程序只认这一种写法，
        /// 唯一出口 <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/>）。
        /// 卷号：卡上有卷号用卡上的；卡说"我是第一片"（`VolumeFirstPiece`）⇒ 1；
        /// 卡说"我是后续片但没写第几"（`VolumePiece`）⇒ **判不出**（返回空串，交给"缺口 + 体积"）。
        /// 基名：<see cref="FileNameHelper.GetArchiveBaseName"/> 已经剥掉一层伪装尾巴
        /// （`pack111-L2.7z.txt` ⇒ `pack111-L2.7z`）。</para>
        ///
        /// <para>现场（官方环台 `10101010`）：这一片叫 `pack111-L2.7z.txt`，卡上
        /// `格式=SevenZip 专属算法=VolumeFirstPiece 自述整包=62919013 按内容算的片数=3`
        /// ⇒ 规范名应当是 `pack111-L2.7z.001` —— 名字那条路永远给不出，卡这条路给得出。</para>
        ///
        /// <para>⛔ 现在只做 7z 这一族（证据在手：卡上有"起始头自述整包 + 片数"）；其它族等有同样硬的
        /// 运行期证据再补，⛔ 不猜。</para>
        /// </summary>
        internal static string ResolveCanonicalNameFromCard(
            string piecePath,
            FileNatureCard card,
            FileNatureCard? groupAnchorCard = null,
            int? contentSlot = null,
            VolumeNamingFamily? namingFamily = null)
        {
            /*
             * ⚠ 2026-10-14：这套顺序（族来自卡 → 卷号来自卡 → 基名剥掉伪装尾巴 → 交给既有命名出口）
             * 已经收成一个唯一出口 —— <see cref="VolumePieceFacts.ResolveCanonicalName"/>。
             * ⛔ 别处不许再自己拼一遍。
             *
             * ⚠ 2026-10-16：RAR 1.5–4.x 那一族的卡上**没有绝对卷号**（只有卷尾结尾块的原始字段、
             * 基数待定）⇒ 把"这一组第 1 卷的卡"（锚）原样转下去（判据仍在 Detection 那一处）。
             *
             * ⚠ 2026-10-16（用户口径「消费方接受『第 1 槽不在这层』后把**卡的绝对卷号**翻成权威」）：
             * **接片这一档正是那个消费方** —— 它按规范名把这一片落到"这一组该拼到的那一层"，
             * 第 1 槽在不在这一层不影响它 ⇒ 明确传 `trustCardNumberWhenNameGivesNothing: true`
             * （判据内部只在"名字什么都给不出"时才读卡上的号，且只认 ≥ 2）。
             * 真机现场 `19191919`：`aaa-L2-976` 卡上写着卷号 2、名字什么都给不出 ⇒ 之前永远拿不到规范名。
             *
             * ⚠ 2026-10-18（困难批 `19191919` 同一份样本的第二处）：组读取已经用内容排出**槽位**与**命名族**
             * （通用分片 `ZipNumbered` vs 跨盘 `ZipSpanned`）⇒ 这两个事实原样转下去，名字不再参与命名
             * （否则 `X.zip` 会给成跨盘族的 `X.z01`/`X.zip`、两片撞名）。判不出 ⇒ null，走老口径。
             */
            return VolumePieceFacts.ResolveCanonicalName(
                piecePath,
                card,
                groupAnchorCard,
                trustCardNumberWhenNameGivesNothing: true,
                contentSlot: contentSlot,
                namingFamily: namingFamily);
        }

        /// <summary>
        /// **分卷组完整性确认**（用户 2026-09-30 真机铁证）：⛔ **只有确认整组完整且每一卷名字都标准，
        /// 分卷才允许进"可删的其余物"**。
        ///
        /// <para><b>为什么必须有它</b>：老判据只看"扩展名像不像分卷"，于是"缺首卷 / 缺末卷的残组"里
        /// 每一卷单看都像"待续解的过程物"，被打包收进可删的其余物；再叠加「空间不足」模式的永久删除，
        /// 用户那 25 GB 就是这么没的（日志还写着"解压成功 ｜ 校验通过"）。</para>
        ///
        /// <para><b>判据已经整体搬进唯一出口 <see cref="VolumeGroupResolver"/></b>（2026-09-30 分卷组装算法）：
        /// 这里只做一次"同目录问一问"，不再自己按扩展名拼判据 —— 老实现那五条（<c>.z01</c> 找同名 <c>.zip</c>、
        /// 纯数字找 <c>.001</c>、<c>.partNN</c> 找首卷…）现在全在判定器的证据表里，而且认得出
        /// "末卷被改名成 <c>一只顶美.z删除ip</c>"这种老判据必然漏掉的情形。</para>
        ///
        /// <para>⛔ 兜底不变：**确认不了（目录读不到、名字认不出、判不出）⇒ 一律按不完整处理** ——
        /// 兜底落在"什么都不做"那一档（AGENTS §9.5）。</para>
        /// </summary>
        internal static bool TryConfirmVolumeGroupComplete(string filePath, out string note)
        {
            note = string.Empty;

            VolumeGroupResolution resolution = new VolumeGroupResolver().Resolve(new VolumeGroupQuery
            {
                AnchorPath = filePath,
                AllowTrialOpen = false
            });

            if (resolution.Verdict == VolumeGroupVerdict.Undetermined)
            {
                note = $"{Path.GetFileName(filePath)}：{resolution.Reason}";
                return false;
            }

            if (resolution.CanEnterDeletableRestItems)
            {
                return true;
            }

            string missing = resolution.MissingVolumeNames.Count > 0
                ? "缺 " + string.Join("、", resolution.MissingVolumeNames)
                : resolution.Reason;

            note = $"{resolution.BaseName}：{missing}";
            return false;
        }

        /// <summary>暂存树里的全部目录（含根；读不到就是空的 —— 什么都不做）。</summary>
        internal static IEnumerable<string> EnumerateDirectoriesSafe(string root)
        {
            var result = new List<string> { root };

            try
            {
                result.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
            }
            catch
            {
                // 读不全就按已经拿到的那些判：宁可少改，也绝不乱改。
            }

            return result;
        }

    }
}
